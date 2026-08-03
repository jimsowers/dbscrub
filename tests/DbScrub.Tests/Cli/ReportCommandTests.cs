using DbScrub.Cli;
using DbScrub.Core.Schema;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Cli;

/// <summary>
/// Exercises the whole `report` path — config load, schema read, verdicts,
/// rendering, exit code — with a fake schema reader. No socket is opened.
/// </summary>
public class ReportCommandTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"dbscrub-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        GC.SuppressFinalize(this);
    }

    // ---- exit codes (SPEC section 2) ---------------------------------------

    [Fact]
    public async Task AFullyClassifiedSchemaExitsZero()
    {
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email")
                .WithPrimaryKey("PersonId").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("UNCLASSIFIED columns: none.", result.Output);
    }

    [Fact]
    public async Task UnclassifiedColumnsInWarnModeStillExitZero()
    {
        // v0 default is warn (DECISIONS.md D6) — the run proceeds and the list
        // is printed loudly.
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email", "Nickname")
                .WithPrimaryKey("PersonId").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("UNCLASSIFIED columns (2)", result.Output);
    }

    [Fact]
    public async Task UnclassifiedColumnsInFailModeExitThree()
    {
        // This is what makes `report` usable as the CI gate D6 wants once the
        // inventory is complete. A report that always exits 0 gates nothing.
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email", "Nickname")
                .WithPrimaryKey("PersonId").Build(),
            """
            {
              "defaults": { "unclassifiedColumns": "fail" },
              "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ]
            }
            """);

        Assert.Equal(ExitCode.Unclassified, result.ExitCode);
        Assert.Contains("unclassified column(s)", result.Error);
    }

    [Fact]
    public async Task AnInvalidConfigExitsFive()
    {
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", "Email").Build(),
            """{ "tables": [ { "name": "Person" } ] }""");

        Assert.Equal(ExitCode.ConfigInvalid, result.ExitCode);
        Assert.Contains("DBS005", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task AMissingConfigFileExitsFive()
    {
        var (exitCode, _, error) = await RunWithConfigPathAsync(
            SchemaBuilder.Database().Build(),
            Path.Combine(Path.GetTempPath(), "definitely-not-here.json"));

        Assert.Equal(ExitCode.ConfigInvalid, exitCode);
        Assert.Contains("Could not read config file", error);
    }

    [Fact]
    public async Task SchemaProblemsExitFive()
    {
        // Valid JSON, but "null" on a NOT NULL column — only detectable once the
        // real schema is in hand.
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", SchemaBuilder.NotNull("Email")).Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "null" } ]} ] }
            """);

        Assert.Equal(ExitCode.ConfigInvalid, result.ExitCode);
        Assert.Contains("would block `clean`", result.Error);
        Assert.Contains("NOT NULL", result.Output);
    }

    [Fact]
    public async Task AFailingSchemaReadExitsOneWithoutAStackTrace()
    {
        var (exitCode, output, error) = await RunAsync(
            new ThrowingSchemaReader(new InvalidOperationException("boom")),
            """{ "tables": [] }""");

        Assert.Equal(ExitCode.UnexpectedError, exitCode);
        Assert.Contains("Could not read the schema of", error);
        Assert.Contains("InvalidOperationException: boom", error);
        Assert.Empty(output);
    }

    // ---- output content -----------------------------------------------------

    [Fact]
    public async Task TheReportNamesItselfAsReadOnly()
    {
        var result = await RunAsync(SchemaBuilder.Database().Build(), """{ "tables": [] }""");

        Assert.Contains("read-only — nothing is modified", result.Output);
    }

    [Fact]
    public async Task HygieneStepsAppearWhenCdcOrTemporalTablesExist()
    {
        var result = await RunAsync(
            SchemaBuilder.Database()
                .WithCdcEnabled()
                .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "Email")
                .WithPrimaryKeyOn("dbo.Person", "PersonId")
                .Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        // The report shows the statements that will actually run, not a
        // paraphrase — a report you cannot check against reality is not a
        // safeguard.
        Assert.Contains("EXEC sys.sp_cdc_disable_db;", result.Output);
        Assert.Contains("ALTER TABLE [dbo].[Person] SET (SYSTEM_VERSIONING = OFF);", result.Output);
        Assert.Contains("TRUNCATE TABLE [dbo].[PersonHistory];", result.Output);
        Assert.Contains("SYSTEM_VERSIONING = ON", result.Output);
    }

    [Fact]
    public async Task ThePhasesArePrintedInTheOrderTheyRun()
    {
        // The detach must appear before the mask section and the reattach after
        // it. A reader approves this text; if it showed the three temporal steps
        // together they would approve a sequence that is not the one that runs.
        var result = await RunAsync(
            SchemaBuilder.Database()
                .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "Email")
                .WithPrimaryKeyOn("dbo.Person", "PersonId")
                .Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        var detach = result.Output.IndexOf("SYSTEM_VERSIONING = OFF", StringComparison.Ordinal);
        var mask = result.Output.IndexOf("Mask", StringComparison.Ordinal);
        var reattach = result.Output.IndexOf("SYSTEM_VERSIONING = ON", StringComparison.Ordinal);

        Assert.True(detach < mask && mask < reattach,
            $"Expected detach ({detach}) before Mask ({mask}) before reattach ({reattach}).");
        Assert.Contains("Before masking", result.Output);
        Assert.Contains("After masking", result.Output);
    }

    [Fact]
    public async Task ThePlanListsTruncatesAndMasksWithStrategies()
    {
        var result = await RunAsync(
            SchemaBuilder.Database()
                .Table("dbo.Person", "PersonId", "Email", "Notes").WithPrimaryKey("PersonId")
                .Table("dbo.LoginAudit", "Id").WithPrimaryKey("Id")
                .Build(),
            """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                  { "name": "Email", "strategy": "scramble" },
                  { "name": "Notes", "strategy": "static", "value": "[redacted]" }
                ]},
                { "name": "dbo.LoginAudit", "strategy": "truncate" }
              ]
            }
            """);

        Assert.Contains("DELETE FROM [dbo].[LoginAudit];", result.Output);
        Assert.Contains("dbo.Person  (2 column(s), row by row, batched on the primary key)", result.Output);
        Assert.Contains("scramble  letters->x", result.Output);
    }

    [Fact]
    public async Task TheReportNeverPrintsAConfiguredStaticValueAsIfItWereData()
    {
        // CLAUDE.md hard guardrail: no PII values in output. A static value is
        // the fake, but the report still describes rather than echoes it, so
        // this stays true when values get more realistic in v1.
        var result = await RunAsync(
            SchemaBuilder.Database().Table("dbo.Person", "Email").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
            """);

        Assert.DoesNotContain("dev@example.invalid", result.Output);
        Assert.Contains("static    fixed replacement value", result.Output);
    }

    // ---- harness ------------------------------------------------------------

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private sealed class FakeSchemaReader(DatabaseSchema schema) : ISchemaReader
    {
        public Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(schema);
    }

    private sealed class ThrowingSchemaReader(Exception exception) : ISchemaReader
    {
        public Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private Task<RunResult> RunAsync(DatabaseSchema schema, string configJson) =>
        RunAsync(new FakeSchemaReader(schema), configJson);

    private async Task<RunResult> RunAsync(ISchemaReader reader, string configJson)
    {
        await File.WriteAllTextAsync(_configPath, configJson);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ReportCommand.RunAsync(
            server: "localhost",
            database: "AAVSB",
            configPath: _configPath,
            readerFactory: _ => reader,
            output: output,
            error: error);

        return new RunResult(exitCode, output.ToString(), error.ToString());
    }

    private static async Task<RunResult> RunWithConfigPathAsync(DatabaseSchema schema, string configPath)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ReportCommand.RunAsync(
            server: "localhost",
            database: "AAVSB",
            configPath: configPath,
            readerFactory: _ => new FakeSchemaReader(schema),
            output: output,
            error: error);

        return new RunResult(exitCode, output.ToString(), error.ToString());
    }
}
