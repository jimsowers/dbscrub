using DbScrub.Core.Configuration;
using DbScrub.Core.Planning;
using DbScrub.Core.Reporting;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Verdicts;

/// <summary>
/// Table-level `"strategy": "keep"` — one line that declares a whole table
/// clean, instead of enumerating every column.
///
/// This exists for reference and lookup tables, which otherwise dominate the
/// UNCLASSIFIED list on a large database and train people to stop reading it.
/// It is the one feature that removes rows from that list without anyone
/// looking at a column, so the constraints around it are the point.
/// </summary>
public class TableLevelKeepTests
{
    [Fact]
    public void OneLineResolvesEveryColumn()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.StateCode", "Code", "Name", "SortOrder")
            .Build();

        var plan = Resolve(schema, """
            { "tables": [
                { "name": "dbo.StateCode", "strategy": "keep", "reason": "reference data" } ] }
            """);

        Assert.Empty(plan.Unclassified);
        Assert.True(plan.IsFullyClassified);
        Assert.Equal(TableAction.Keep, plan.Tables.Single().Action);
        Assert.All(plan.Tables.Single().Columns, c => Assert.Equal(VerdictKind.Kept, c.Kind));
    }

    [Fact]
    public void TheReasonIsCarriedOntoEveryColumn()
    {
        var schema = SchemaBuilder.Database().Table("dbo.StateCode", "Code", "Name").Build();

        var plan = Resolve(schema, """
            { "tables": [
                { "name": "dbo.StateCode", "strategy": "keep", "reason": "reference data" } ] }
            """);

        Assert.All(plan.Tables.Single().Columns, c => Assert.Equal("reference data", c.Reason));
    }

    [Fact]
    public void ItCoversColumnsAddedAfterTheConfigWasWritten()
    {
        // The trade this feature makes, stated as a test rather than left
        // implicit: a column nobody has seen is covered silently. That is why
        // a reason is mandatory and why the report lists these tables.
        var config = MaskingConfigLoader.Parse("""
            { "tables": [
                { "name": "dbo.StateCode", "strategy": "keep", "reason": "reference data" } ] }
            """, "test");

        var laterSchema = SchemaBuilder.Database()
            .Table("dbo.StateCode", "Code", "Name", "AddedNextQuarter")
            .Build();

        var plan = VerdictResolver.Resolve(laterSchema, config);

        Assert.Empty(plan.Unclassified);
        Assert.Equal(3, plan.Tables.Single().Columns.Count);
    }

    // ---- the constraints ---------------------------------------------------

    [Fact]
    public void AReasonIsRequired()
    {
        var error = SingleError("""
            { "tables": [ { "name": "dbo.StateCode", "strategy": "keep" } ] }
            """);

        Assert.Equal(ConfigErrorCodes.MissingProperty, error.Code);
        Assert.Contains("no \"reason\"", error.Message);
    }

    [Fact]
    public void TableLevelKeepPlusColumnsIsRefused()
    {
        // "Keep everything except these" would silently keep any column added
        // later — unprotected while looking decided. That is exactly the blind
        // spot the UNCLASSIFIED list exists to expose, so the config cannot
        // express it.
        var error = SingleError("""
            {
              "tables": [
                { "name": "dbo.Person", "strategy": "keep", "reason": "mostly clean", "columns": [
                  { "name": "Email", "strategy": "scramble" }
                ]}
              ]
            }
            """);

        Assert.Equal(ConfigErrorCodes.Contradictory, error.Code);
        Assert.Contains("list them all individually", error.Suggestion);
    }

    [Fact]
    public void TruncateStillWorksAndIsStillDistinctFromKeep()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.LoginAudit", "Id")
            .Table("dbo.StateCode", "Code")
            .Build();

        var plan = Resolve(schema, """
            { "tables": [
                { "name": "dbo.LoginAudit", "strategy": "truncate" },
                { "name": "dbo.StateCode",  "strategy": "keep", "reason": "reference data" } ] }
            """);

        Assert.Equal(TableAction.Truncate, Find(plan, "dbo.LoginAudit").Action);
        Assert.Equal(TableAction.Keep, Find(plan, "dbo.StateCode").Action);
        Assert.Single(plan.Truncated);
        Assert.Single(plan.KeptWholesale);
    }

    [Fact]
    public void AnUnknownTableStrategyNamesBothValidOnes()
    {
        var error = SingleError("""
            { "tables": [ { "name": "dbo.Person", "strategy": "ignore" } ] }
            """);

        Assert.Equal(ConfigErrorCodes.InvalidValue, error.Code);
        Assert.Contains("truncate", error.Suggestion);
        Assert.Contains("keep", error.Suggestion);
    }

    // ---- visibility --------------------------------------------------------

    [Fact]
    public void TheReportListsExcludedTablesWithTheirReasons()
    {
        // An exclusion nobody can see is a blind spot. These tables leave the
        // UNCLASSIFIED list, so they have to show up somewhere else.
        var schema = SchemaBuilder.Database()
            .Table("dbo.StateCode", "Code")
            .Table("dbo.CountyCode", "Code")
            .Build();

        var plan = Resolve(schema, """
            { "tables": [
                { "name": "dbo.StateCode",  "strategy": "keep", "reason": "reference data" },
                { "name": "dbo.CountyCode", "strategy": "keep", "reason": "public codes" } ] }
            """);

        var report = PlanReport.Render(CleanPlan.Build(plan), "localhost", "test.json");

        Assert.Contains("Excluded by a table-level \"keep\" (2)", report);
        Assert.Contains("dbo.StateCode", report);
        Assert.Contains("reference data", report);
        Assert.Contains("including any column added since", report);
    }

    [Fact]
    public void TheReportSaysNothingAboutExclusionsWhenThereAreNone()
    {
        var schema = SchemaBuilder.Database().Table("dbo.Person", "Email").Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.DoesNotContain("Excluded by a table-level", PlanReport.Render(CleanPlan.Build(plan), "localhost", "test.json"));
    }

    // ---- helpers -----------------------------------------------------------

    private static ScrubPlan Resolve(Core.Schema.DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));

    private static TablePlan Find(ScrubPlan plan, string qualifiedName) =>
        plan.Tables.Single(t => t.QualifiedName == qualifiedName);

    private static ConfigError SingleError(string json)
    {
        MaskingConfigLoader.TryParse(json, "test", out _, out var errors);

        Assert.True(errors.Count == 1,
            $"Expected exactly one error but got {errors.Count}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors.Select(e => e.Format("test"))));

        return errors[0];
    }
}
