using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Hygiene;

public class HygienePlannerTests
{
    [Fact]
    public void ACleanDatabaseNeedsNoHygiene()
    {
        var plan = Plan(SchemaBuilder.Database().Table("dbo.Person", "Email").Build(), """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Empty(HygienePlanner.Build(plan));
    }

    [Fact]
    public void ChangeTrackingIsDisabledFirst()
    {
        // Its capture tables hold full copies of changed rows. Masking before
        // disabling would just add the masked versions alongside the originals.
        var schema = SchemaBuilder.Database().WithCdcEnabled().CdcTable("dbo.Person", "Email").Build();

        var steps = HygienePlanner.Build(Plan(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """));

        Assert.Equal(HygieneStepKind.DisableChangeTracking, steps[0].Kind);
        Assert.Equal("EXEC sys.sp_cdc_disable_db;", steps[0].Sql);
    }

    // ---- the versioning dance ---------------------------------------------

    [Fact]
    public void ATemporalTableIsDetachedEmptiedAndReattachedInThatOrder()
    {
        // The order IS the correctness. While versioning is ON, every UPDATE
        // writes the pre-update row into history — so masking without detaching
        // first copies unmasked rows into history and reports success.
        var steps = HygienePlanner.Build(TemporalPlan());

        Assert.Equal(
            [
                HygieneStepKind.DisableVersioning,
                HygieneStepKind.TruncateHistory,
                HygieneStepKind.ReEnableVersioning,
            ],
            steps.Select(s => s.Kind));
    }

    [Fact]
    public void TheVersioningStatementsNameTheRightTables()
    {
        var steps = HygienePlanner.Build(TemporalPlan());

        Assert.Equal("ALTER TABLE [dbo].[Person] SET (SYSTEM_VERSIONING = OFF);", steps[0].Sql);
        Assert.Equal("TRUNCATE TABLE [dbo].[PersonHistory];", steps[1].Sql);
        Assert.Contains("SYSTEM_VERSIONING = ON", steps[2].Sql);
        Assert.Contains("HISTORY_TABLE = [dbo].[PersonHistory]", steps[2].Sql);
    }

    [Fact]
    public void ReattachingSkipsTheConsistencyCheck()
    {
        // The history table is empty at that point, so there is nothing to
        // check and the check is expensive.
        Assert.Contains("DATA_CONSISTENCY_CHECK = OFF", HygienePlanner.Build(TemporalPlan())[2].Sql);
    }

    [Fact]
    public void ATemporalTableInANonDboSchemaIsQuotedCorrectly()
    {
        var schema = SchemaBuilder.Database()
            .TemporalTable("app.Person", "app.PersonHistory", "Email")
            .Build();

        var steps = HygienePlanner.Build(Plan(schema, """
            { "tables": [ { "name": "app.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """));

        Assert.Equal("ALTER TABLE [app].[Person] SET (SYSTEM_VERSIONING = OFF);", steps[0].Sql);
        Assert.Equal("TRUNCATE TABLE [app].[PersonHistory];", steps[1].Sql);
    }

    // ---- truncation --------------------------------------------------------

    [Fact]
    public void AConfiguredTruncateUsesDeleteNotTruncate()
    {
        // TRUNCATE is faster but SQL Server refuses it on any table referenced
        // by a foreign key, even one with no rows behind it. DELETE always
        // works, and audit tables are emptied for correctness, not speed.
        var schema = SchemaBuilder.Database().Table("dbo.LoginAudit", "Id").Build();

        var step = Assert.Single(HygienePlanner.Build(Plan(schema,
            """{ "tables": [ { "name": "dbo.LoginAudit", "strategy": "truncate" } ] }""")));

        Assert.Equal(HygieneStepKind.TruncateTable, step.Kind);
        Assert.Equal("DELETE FROM [dbo].[LoginAudit];", step.Sql);
    }

    [Fact]
    public void TruncationsComeAfterChangeTrackingIsOff()
    {
        var schema = SchemaBuilder.Database()
            .WithCdcEnabled()
            .Table("dbo.LoginAudit", "Id")
            .Build();

        var steps = HygienePlanner.Build(Plan(schema,
            """{ "tables": [ { "name": "dbo.LoginAudit", "strategy": "truncate" } ] }"""));

        Assert.Equal(HygieneStepKind.DisableChangeTracking, steps[0].Kind);
        Assert.Equal(HygieneStepKind.TruncateTable, steps[1].Kind);
    }

    [Fact]
    public void ATableKeptWholesaleIsNotTouched()
    {
        var schema = SchemaBuilder.Database().Table("dbo.StateCode", "Code").Build();

        Assert.Empty(HygienePlanner.Build(Plan(schema, """
            { "tables": [ { "name": "dbo.StateCode", "strategy": "keep", "reason": "reference data" } ] }
            """)));
    }

    // ---- safety ------------------------------------------------------------

    [Fact]
    public void EveryStatementIsSingleAndTerminated()
    {
        // Each step must be one statement. A step carrying two would make the
        // description a lie about what runs.
        var schema = SchemaBuilder.Database()
            .WithCdcEnabled()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "Email")
            .Table("dbo.LoginAudit", "Id")
            .Build();

        var steps = HygienePlanner.Build(Plan(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble" } ]},
                { "name": "dbo.LoginAudit", "strategy": "truncate" }
              ]
            }
            """));

        Assert.All(steps, s =>
        {
            Assert.EndsWith(";", s.Sql);
            Assert.Equal(1, s.Sql.Count(c => c == ';'));
            Assert.DoesNotContain("--", s.Sql);
        });
    }

    [Fact]
    public void EveryStepExplainsItselfInEnglish()
    {
        // A human approves this list before it runs, so the description has to
        // stand on its own.
        Assert.All(HygienePlanner.Build(TemporalPlan()), s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(string.IsNullOrWhiteSpace(s.Target));
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static ScrubPlan Plan(Core.Schema.DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));

    private static ScrubPlan TemporalPlan()
    {
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "Email")
            .Build();

        return Plan(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);
    }
}
