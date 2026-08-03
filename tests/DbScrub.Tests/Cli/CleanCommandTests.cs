using DbScrub.Cli;
using DbScrub.Core.Execution;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using DbScrub.Core.Schema;
using DbScrub.Core.Stamp;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Cli;

/// <summary>
/// The whole of `clean` except the SQL itself, exercised with fakes. No socket
/// is opened and nothing is modified.
///
/// The tests that matter most are the ones asserting that a refusal happened
/// BEFORE anything ran. A safety check that refuses after the first UPDATE is
/// not a safety check, so each of those asserts on the session having recorded
/// nothing rather than only on the exit code.
/// </summary>
public class CleanCommandTests : IDisposable
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

    // ---- the safety checks (SPEC section 3) --------------------------------

    [Fact]
    public async Task ANonAllowlistedServerIsRefusedWithoutConnecting()
    {
        // SPEC 3.1. There is no override flag, and the refusal happens before a
        // socket is opened — the fake session proves nothing was touched.
        var result = await RunAsync(Person(), PersonConfig, server: "prod-sql-01");

        Assert.Equal(ExitCode.SafetyCheckRefused, result.ExitCode);
        Assert.Contains("Refusing to connect to 'prod-sql-01'", result.Error);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task AnAlreadyStampedDatabaseIsSkippedWithExitZero()
    {
        // SPEC 3.3. Running clean twice is a mistake with no consequence, so a
        // wrapper script that re-runs it should carry on rather than fail.
        var result = await RunAsync(Person(), PersonConfig,
            stamp: new SanitizationStatus(true, DateTime.UtcNow, "abc123", "0.1.0"));

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("already sanitized", result.Output);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task TypingSomethingElseAbortsBeforeAnythingRuns()
    {
        // SPEC 3.2. "Press y" is muscle memory; reproducing a name from the
        // summary is not.
        var result = await RunAsync(Person(), PersonConfig, typed: "aavsb");

        Assert.Equal(ExitCode.SafetyCheckRefused, result.ExitCode);
        Assert.Contains("Nothing was modified", result.Error);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task TypingTheDatabaseNameProceeds()
    {
        var result = await RunAsync(Person(), PersonConfig, typed: "AAVSB");

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.NotEmpty(result.Session.Transcript);
    }

    [Fact]
    public async Task TheConfirmationSummarySaysWhatWillHappen()
    {
        // The operator is agreeing to something specific, not to the word
        // "continue".
        var result = await RunAsync(Person(), PersonConfig, typed: "AAVSB");

        Assert.Contains("About to MODIFY this database", result.Output);
        Assert.Contains("Type the database name (AAVSB)", result.Output);
    }

    [Fact]
    public async Task YesSkipsConfirmationOnALocalNamedInstance()
    {
        // DECISIONS.md D18 — localhost\MSSQLSERVER02 is exactly as local as
        // localhost, and without this the restore-then-scrub wrapper cannot run.
        var result = await RunAsync(Person(), PersonConfigFor("localhost\\MSSQLSERVER02"),
            server: "localhost\\MSSQLSERVER02", yes: true, typed: null);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.NotEmpty(result.Session.Transcript);
    }

    [Fact]
    public async Task YesIsRefusedForAnAllowlistedServerThatIsNotThisMachine()
    {
        // `--yes` is deliberately NARROWER than the allowlist: "(local)" is
        // allowlisted by default and still may not skip a human (SPEC 3.2).
        var result = await RunAsync(Person(), PersonConfig, server: "(local)", yes: true, typed: null);

        Assert.Equal(ExitCode.SafetyCheckRefused, result.ExitCode);
        Assert.Contains("--yes is not allowed", result.Error);
        Assert.Empty(result.Session.Transcript);
    }

    // ---- refusing to run a broken plan -------------------------------------

    [Fact]
    public async Task AnInvalidConfigExitsFiveWithoutConnecting()
    {
        var result = await RunAsync(Person(), """{ "tables": [ { "name": "Person" } ] }""");

        Assert.Equal(ExitCode.ConfigInvalid, result.ExitCode);
        Assert.Contains("DBS005", result.Error);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task APlanWithProblemsExitsFiveBeforeMutating()
    {
        // A scramble on a keyless table. The refusal has to land here, not
        // halfway through a run.
        var schema = SchemaBuilder.Database().Table("dbo.Import", "Email").Build();

        var result = await RunAsync(schema, """
            { "tables": [ { "name": "dbo.Import", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Equal(ExitCode.ConfigInvalid, result.ExitCode);
        Assert.Contains("block this run", result.Error);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task UnclassifiedColumnsInFailModeExitThree()
    {
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
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task TheFlagCanTightenTheConfigButThereIsNoFlagThatLoosensIt()
    {
        // The config says warn; --fail-on-unclassified overrides it to fail.
        var schema = SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email", "Nickname")
            .WithPrimaryKey("PersonId").Build();

        var config = """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """;

        Assert.Equal(ExitCode.Success, (await RunAsync(schema, config)).ExitCode);
        Assert.Equal(ExitCode.Unclassified,
            (await RunAsync(schema, config, failOnUnclassified: true)).ExitCode);
    }

    // ---- dry run -----------------------------------------------------------

    [Fact]
    public async Task DryRunPrintsThePlanAndChangesNothing()
    {
        var result = await RunAsync(Person(), PersonConfig, dryRun: true, typed: null);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("Nothing was modified", result.Output);
        Assert.Empty(result.Session.Transcript);
    }

    [Fact]
    public async Task DryRunNeverPromptsSoItCanRunUnattended()
    {
        // readLine throwing proves the prompt was not reached; a dry run that
        // blocked on stdin would be useless in a script.
        var result = await RunAsync(Person(), PersonConfig, dryRun: true,
            readLine: () => throw new InvalidOperationException("must not prompt"));

        Assert.Equal(ExitCode.Success, result.ExitCode);
    }

    // ---- what a finished run says ------------------------------------------

    [Fact]
    public async Task ASuccessfulRunSaysTheDatabaseIsNotStampedYet()
    {
        // The verify gate that earns a stamp is step 5. Nothing may claim a
        // database is clean without it, so the success message says so plainly
        // rather than exiting 0 in silence.
        var result = await RunAsync(Person(), PersonConfig, typed: "AAVSB");

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("NOT STAMPED", result.Output);
        Assert.Contains("status", result.Output);
    }

    [Fact]
    public async Task AFailedMaskExitsNonZeroAndSaysTheDatabaseIsUnusable()
    {
        var session = new RecordingSession { FailMasking = true };

        var result = await RunAsync(Person(), PersonConfig, typed: "AAVSB", session: session);

        Assert.Equal(ExitCode.UnexpectedError, result.ExitCode);
        Assert.Contains("partly masked and NOT safe to use", result.Error);
    }

    [Fact]
    public async Task RowsLeftUnmaskedFailTheRunRatherThanWarn()
    {
        // A table that rewrote fewer rows than it holds means rows kept their
        // real values. Reporting success there would be the worst bug this tool
        // could have.
        var session = new RecordingSession { RowsInTable = 100, RowsUpdated = 40 };

        var result = await RunAsync(Person(), PersonConfig, typed: "AAVSB", session: session);

        Assert.Equal(ExitCode.UnexpectedError, result.ExitCode);
        Assert.Contains("LEFT UNMASKED", result.Error);
    }

    [Fact]
    public async Task TheRunNeverPrintsAConfiguredStaticValue()
    {
        // CLAUDE.md hard guardrail. A static value is the fake, but the output
        // describes rather than echoes it, so this stays true in v1 when the
        // values get realistic.
        var result = await RunAsync(Person(), """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
            """, typed: "AAVSB");

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.DoesNotContain("dev@example.invalid", result.Output);
        Assert.DoesNotContain("dev@example.invalid", result.Error);
    }

    // ---- harness -----------------------------------------------------------

    private const string PersonConfig = """
        { "tables": [ { "name": "dbo.Person", "columns": [
            { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
            { "name": "Email", "strategy": "scramble" } ]} ] }
        """;

    private static string PersonConfigFor(string server) => $$"""
        {
          "defaults": { "allowedServers": ["{{server.Replace("\\", "\\\\")}}"] },
          "tables": [ { "name": "dbo.Person", "columns": [
            { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
            { "name": "Email", "strategy": "scramble" } ]} ]
        }
        """;

    private static DatabaseSchema Person() =>
        SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email")
            .WithPrimaryKey("PersonId").Build();

    private sealed record RunResult(int ExitCode, string Output, string Error, RecordingSession Session);

    private sealed class RecordingSession : ICleanSession
    {
        public List<string> Transcript { get; } = [];

        public bool FailMasking { get; init; }

        public long RowsInTable { get; init; } = 10;

        public long RowsUpdated { get; init; } = 10;

        public Task ExecuteAsync(HygieneStep step, CancellationToken cancellationToken = default)
        {
            Transcript.Add(step.Sql);
            return Task.CompletedTask;
        }

        public Task<MaskResult> MaskTableAsync(
            TableMaskPlan plan,
            int batchSize,
            IProgress<long>? rowsDone = null,
            CancellationToken cancellationToken = default)
        {
            Transcript.Add($"MASK {plan.QualifiedName}");

            return FailMasking
                ? Task.FromException<MaskResult>(new InvalidOperationException("masking blew up"))
                : Task.FromResult(new MaskResult(plan.QualifiedName, RowsInTable, RowsUpdated));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSchemaReader(DatabaseSchema schema) : ISchemaReader
    {
        public Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(schema);
    }

    private sealed class FakeStampReader(SanitizationStatus status) : IStampReader
    {
        public Task<SanitizationStatus> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);
    }

    private async Task<RunResult> RunAsync(
        DatabaseSchema schema,
        string configJson,
        string server = "localhost",
        bool yes = false,
        bool dryRun = false,
        bool failOnUnclassified = false,
        string? typed = "AAVSB",
        SanitizationStatus? stamp = null,
        RecordingSession? session = null,
        Func<string?>? readLine = null)
    {
        await File.WriteAllTextAsync(_configPath, configJson);

        var output = new StringWriter();
        var error = new StringWriter();
        var recorder = session ?? new RecordingSession();

        var exitCode = await CleanCommand.RunAsync(
            server: server,
            database: "AAVSB",
            configPath: _configPath,
            yes: yes,
            dryRun: dryRun,
            failOnUnclassified: failOnUnclassified,
            schemaReaderFactory: _ => new FakeSchemaReader(schema),
            stampReaderFactory: _ => new FakeStampReader(stamp ?? SanitizationStatus.NotSanitized),
            sessionFactory: _ => recorder,
            output: output,
            error: error,
            readLine: readLine ?? (() => typed));

        return new RunResult(exitCode, output.ToString(), error.ToString(), recorder);
    }
}
