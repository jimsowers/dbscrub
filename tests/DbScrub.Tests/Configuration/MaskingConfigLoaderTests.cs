using System.Text.Json;
using DbScrub.Core.Configuration;
using Xunit;

namespace DbScrub.Tests.Configuration;

public class MaskingConfigLoaderTests
{
    // ---- the real sample config -------------------------------------------

    [Fact]
    public void SampleConfigLoads()
    {
        var config = MaskingConfigLoader.LoadFile(SampleConfigPath);

        Assert.Equal(3, config.Tables.Count);
        Assert.Equal(["dbo.Person", "dbo.LoginAudit", "dbo.EmailSendLog"],
            config.Tables.Select(t => t.QualifiedName));
    }

    [Fact]
    public void SampleConfigDefaultsMatchTheSpec()
    {
        var defaults = MaskingConfigLoader.LoadFile(SampleConfigPath).Defaults;

        Assert.Equal(["localhost", ".", "(local)", "127.0.0.1"], defaults.AllowedServers);
        Assert.Equal(UnclassifiedMode.Warn, defaults.UnclassifiedColumns);
        Assert.Equal(5000, defaults.BatchSize);
        Assert.Null(defaults.RenameTo);            // DECISIONS.md D10: in-place, no rename.
        Assert.Empty(defaults.RepairUsers);        // The team restore script owns login setup.
    }

    [Fact]
    public void SampleConfigParsesEveryStrategy()
    {
        var person = MaskingConfigLoader.LoadFile(SampleConfigPath).Tables
            .Single(t => t.QualifiedName == "dbo.Person");

        Assert.Equal(HistoryMode.Truncate, person.History);
        Assert.Null(person.Strategy);

        Assert.Equal(
            new ColumnConfig("FirstName", ColumnStrategy.Static, new ConfigValue(JsonValueKind.String, "Dev"), null),
            person.Columns.Single(c => c.Name == "FirstName"));

        Assert.Equal(ColumnStrategy.Scramble, person.Columns.Single(c => c.Name == "LastName").Strategy);
        Assert.Equal(ColumnStrategy.Null, person.Columns.Single(c => c.Name == "MiddleName").Strategy);

        var personId = person.Columns.Single(c => c.Name == "PersonId");
        Assert.Equal(ColumnStrategy.Keep, personId.Strategy);
        Assert.Equal("surrogate key", personId.Reason);
    }

    [Fact]
    public void SampleConfigTruncatedTablesHaveNoColumns()
    {
        var audit = MaskingConfigLoader.LoadFile(SampleConfigPath).Tables
            .Single(t => t.QualifiedName == "dbo.LoginAudit");

        Assert.Equal(TableStrategy.Truncate, audit.Strategy);
        Assert.Empty(audit.Columns);
    }

    // ---- defaults ----------------------------------------------------------

    [Fact]
    public void DefaultsBlockMayBeOmittedEntirely()
    {
        var config = MaskingConfigLoader.Parse("""{ "tables": [] }""", "test");

        Assert.Equal(ConfigDefaults.CreateDefault().AllowedServers, config.Defaults.AllowedServers);
        Assert.Equal(5000, config.Defaults.BatchSize);
        Assert.Equal(UnclassifiedMode.Warn, config.Defaults.UnclassifiedColumns);
    }

    [Fact]
    public void EmptyTablesArrayIsValid()
    {
        // The intended way to start an inventory: empty config, run report, paste
        // the UNCLASSIFIED list back in (DECISIONS.md D6).
        var config = MaskingConfigLoader.Parse("""{ "tables": [] }""", "test");

        Assert.Empty(config.Tables);
    }

    [Fact]
    public void EmptyAllowedServersIsRejected()
    {
        var error = SingleError("""{ "defaults": { "allowedServers": [] }, "tables": [] }""");

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Contains("refuse every server", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void BatchSizeOutsideTheAllowedRangeIsRejected(int batchSize)
    {
        var error = SingleError($$"""{ "defaults": { "batchSize": {{batchSize}} }, "tables": [] }""");

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
    }

    [Fact]
    public void UnknownUnclassifiedModeIsRejected()
    {
        var error = SingleError("""{ "defaults": { "unclassifiedColumns": "explode" }, "tables": [] }""");

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Contains("warn", error.Suggestion);
    }

    // ---- structural rules --------------------------------------------------

    [Fact]
    public void MissingTablesPropertyIsRejected()
    {
        var error = SingleError("""{ "defaults": {} }""");

        Assert.Equal(ConfigErrorCodes.MissingProperty, error.Code);
        Assert.Contains("\"tables\": []", error.Suggestion);
    }

    [Fact]
    public void UnknownPropertyIsRejectedWithATypoSuggestion()
    {
        var errors = ErrorsFrom("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "stragety": "scramble" }
                ]}
              ]
            }
            """);

        // Two errors, and both are worth printing: the typo itself, and the
        // consequence (the column now has no strategy at all). Reporting only
        // the first would leave the reader guessing what breaks downstream.
        var unknown = errors.Single(e => e.Code == ConfigErrorCodes.UnknownProperty);
        Assert.Equal("Did you mean \"strategy\"?", unknown.Suggestion);
        Assert.Equal(26, unknown.Column);   // points at "stragety", not at the line start

        Assert.Contains(errors, e => e.Code == ConfigErrorCodes.MissingProperty);
    }

    [Fact]
    public void MalformedJsonReportsAPosition()
    {
        var error = SingleError("""
            {
              "tables": [
            }
            """);

        Assert.Equal(ConfigErrorCodes.InvalidJson, error.Code);
        Assert.True(error.Line > 0, "A parse error should carry a line number.");
    }

    [Fact]
    public void UnqualifiedTableNameIsRejected()
    {
        var error = SingleError("""{ "tables": [ { "name": "Person", "strategy": "truncate" } ] }""");

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Equal("Write \"dbo.Person\" if it is in the dbo schema.", error.Suggestion);
    }

    [Fact]
    public void DuplicateTableIsRejected()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "strategy": "truncate" },
                { "name": "DBO.PERSON", "strategy": "truncate" }
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.Duplicate, error.Code);
    }

    [Fact]
    public void DuplicateColumnIsRejected()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble" },
                  { "name": "EMAIL",  "strategy": "scramble" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.Duplicate, error.Code);
    }

    [Fact]
    public void TruncatedTableWithColumnsIsContradictory()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.LoginAudit", "strategy": "truncate", "columns": [
                  { "name": "Email", "strategy": "scramble" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.Contradictory, error.Code);
        Assert.Contains("would never run", error.Message);
    }

    [Fact]
    public void TableThatAsksForNothingIsRejected()
    {
        var error = SingleError("""{ "tables": [ { "name": "dbo.Person" } ] }""");

        Assert.Equal(ConfigErrorCodes.Contradictory, error.Code);
        Assert.Contains("asks for nothing", error.Message);
    }

    // ---- strategy and value rules -----------------------------------------

    [Fact]
    public void StaticWithoutValueIsRejected()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "static" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.StrategyRequiresValue, error.Code);
        Assert.Contains("\"strategy\": \"static\", \"value\"", error.Suggestion);
    }

    [Fact]
    public void ValueOnANonStaticStrategyIsRejected()
    {
        // Dead config: it reads as intent that silently never happens.
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble", "value": "x@y.invalid" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.ValueNotAllowed, error.Code);
    }

    [Fact]
    public void StaticWithJsonNullIsRejectedAsAmbiguous()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "static", "value": null }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Contains("\"strategy\": \"null\"", error.Suggestion);
    }

    [Fact]
    public void UnknownColumnStrategyIsRejected()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "shred" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Contains("null, static, scramble, keep", error.Suggestion);
    }

    [Fact]
    public void NonStringStaticValuesArePreservedWithTheirKind()
    {
        // Slice 4 type-checks the value against the column type, and
        // "the JSON said 0" differs from "the JSON said \"0\"".
        var config = MaskingConfigLoader.Parse("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Score",    "strategy": "static", "value": 0 },
                  { "name": "IsActive", "strategy": "static", "value": true }
                ]}
              ]
            }
            """, "test");

        var columns = config.Tables.Single().Columns;

        Assert.Equal(new ConfigValue(JsonValueKind.Number, "0"), columns[0].Value);
        Assert.Equal(new ConfigValue(JsonValueKind.True, "true"), columns[1].Value);
    }

    // ---- error reporting behavior -----------------------------------------

    [Fact]
    public void EveryErrorIsCollected_NotJustTheFirst()
    {
        var errors = ErrorsFrom("""
            {
              "tables": [
                { "name": "Person", "columns": [
                  { "name": "Email", "strategy": "static" },
                  { "name": "Phone", "strategy": "shred" }
                ]}
              ]
            }
            """);

        // Unqualified table name aborts that table, so we expect exactly that
        // one error here — the point is that the loader does not throw on the
        // first problem it sees.
        Assert.NotEmpty(errors);

        var many = ErrorsFrom("""
            {
              "defaults": { "batchSize": 0, "unclassifiedColumns": "explode" },
              "tables": []
            }
            """);

        Assert.Equal(2, many.Count);
    }

    [Fact]
    public void SemanticErrorsCarryTheLineOfTheOffendingProperty()
    {
        // Line 1 is "{", line 2 "tables", line 3 the table object,
        // line 4 the column object -> the error points at the column.
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "static" }
                ]}
              ]
            }
            """);

        Assert.Equal(4, error.Line);
        Assert.Equal(7, error.Column);
    }

    [Fact]
    public void FormattedErrorsLookLikeCompilerDiagnostics()
    {
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "static" }
                ]}
              ]
            }
            """);

        var formatted = error.Format("config/masking.json");

        Assert.StartsWith("config/masking.json(4,7): error DBS006: ", formatted);
    }

    [Fact]
    public void ParseThrowsWithEveryErrorInTheMessage()
    {
        var exception = Assert.Throws<ConfigInvalidException>(() =>
            MaskingConfigLoader.Parse("""
                {
                  "defaults": { "batchSize": 0, "unclassifiedColumns": "explode" },
                  "tables": []
                }
                """, "masking.json"));

        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains("masking.json(", exception.Message);
        Assert.Contains("Config is invalid (2 errors).", exception.Message);
    }

    // ---- helpers -----------------------------------------------------------

    private static string SampleConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "masking.sample.json");

    private static IReadOnlyList<ConfigError> ErrorsFrom(string json)
    {
        MaskingConfigLoader.TryParse(json, "test", out _, out var errors);
        return errors;
    }

    private static ConfigError SingleError(string json)
    {
        var errors = ErrorsFrom(json);

        Assert.True(errors.Count == 1,
            $"Expected exactly one error but got {errors.Count}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors.Select(e => e.Format("test"))));

        return errors[0];
    }
}
