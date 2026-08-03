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

        Assert.Empty(PreMask(plan));
        Assert.Empty(HygienePlanner.BuildPostMask(plan));
    }

    [Fact]
    public void ChangeTrackingIsDisabledFirst()
    {
        // Its capture tables hold full copies of changed rows. Masking before
        // disabling would just add the masked versions alongside the originals.
        var schema = SchemaBuilder.Database().WithCdcEnabled().CdcTable("dbo.Person", "Email").Build();

        var steps = PreMask(Plan(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """));

        Assert.Equal(HygieneStepKind.DisableChangeTracking, steps[0].Kind);
        Assert.Equal("EXEC sys.sp_cdc_disable_db;", steps[0].Sql);
    }

    // ---- the versioning dance ---------------------------------------------

    [Fact]
    public void VersioningIsDetachedBeforeMaskingAndReattachedOnlyAfterIt()
    {
        // THE test for this class. While versioning is ON, every UPDATE writes
        // the pre-update row into history — so the reattach cannot sit next to
        // the detach, or masking would run with versioning back on and refill
        // history with exactly the rows the truncate just removed.
        //
        // The split between the two lists IS the interleaving: everything in the
        // first runs before the mask engine, everything in the second after it.
        var plan = TemporalPlan();

        Assert.Equal(
            [HygieneStepKind.DisableVersioning, HygieneStepKind.TruncateHistory],
            PreMask(plan).Select(s => s.Kind));

        Assert.Equal(
            [HygieneStepKind.ReEnableVersioning],
            HygienePlanner.BuildPostMask(plan).Select(s => s.Kind));
    }

    [Fact]
    public void EveryDetachedTableIsReattached()
    {
        // A table left detached would silently stop recording history for
        // everything that touches it afterwards, and nothing about the database
        // would look broken.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "Email")
            .TemporalTable("app.Claim", "app.ClaimHistory", "Notes")
            .Build();

        var plan = Plan(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "scramble" } ]},
                { "name": "app.Claim",  "columns": [ { "name": "Notes", "strategy": "scramble" } ]}
              ]
            }
            """);

        var detached = PreMask(plan)
            .Where(s => s.Kind == HygieneStepKind.DisableVersioning)
            .Select(s => s.Target);

        var reattached = HygienePlanner.BuildPostMask(plan)
            .Where(s => s.Kind == HygieneStepKind.ReEnableVersioning)
            .Select(s => s.Target);

        Assert.Equal(detached.Order(), reattached.Order());
    }

    [Fact]
    public void TheVersioningStatementsNameTheRightTables()
    {
        var plan = TemporalPlan();
        var pre = PreMask(plan);
        var post = HygienePlanner.BuildPostMask(plan);

        Assert.Equal("ALTER TABLE [dbo].[Person] SET (SYSTEM_VERSIONING = OFF);", pre[0].Sql);
        Assert.Equal("TRUNCATE TABLE [dbo].[PersonHistory];", pre[1].Sql);
        Assert.Contains("SYSTEM_VERSIONING = ON", post[0].Sql);
        Assert.Contains("HISTORY_TABLE = [dbo].[PersonHistory]", post[0].Sql);
    }

    [Fact]
    public void ReattachingSkipsTheConsistencyCheck()
    {
        // The check is an expensive scan of history, and nothing this run does
        // can break the invariant it verifies.
        Assert.Contains("DATA_CONSISTENCY_CHECK = OFF", HygienePlanner.BuildPostMask(TemporalPlan())[0].Sql);
    }

    [Fact]
    public void ATemporalTableInANonDboSchemaIsQuotedCorrectly()
    {
        var schema = SchemaBuilder.Database()
            .TemporalTable("app.Person", "app.PersonHistory", "Email")
            .Build();

        var steps = PreMask(Plan(schema, """
            { "tables": [ { "name": "app.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """));

        Assert.Equal("ALTER TABLE [app].[Person] SET (SYSTEM_VERSIONING = OFF);", steps[0].Sql);
        Assert.Equal("TRUNCATE TABLE [app].[PersonHistory];", steps[1].Sql);
    }

    [Fact]
    public void HistoryIsNotEmptiedWhenTheConfigAsksForItToBeMasked()
    {
        // Emptying it here would make `history: "mask"` a setting that reads as
        // intent and silently does the opposite. The rows have to survive the
        // hygiene pass in order to be masked by the pass after it.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "Email")
            .WithPrimaryKeyOn("dbo.Person", "PersonId")
            .Build();

        var steps = PreMask(Plan(schema, """
            { "tables": [ { "name": "dbo.Person", "history": "mask", "columns": [
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
            """));

        Assert.Equal([HygieneStepKind.DisableVersioning], steps.Select(s => s.Kind));

        // Still detached and still reattached — masking history requires
        // versioning off just as much as masking the parent does.
        Assert.Equal(
            [HygieneStepKind.ReEnableVersioning],
            HygienePlanner.BuildPostMask(Plan(schema, """
                { "tables": [ { "name": "dbo.Person", "history": "mask", "columns": [
                    { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
                """)).Select(s => s.Kind));
    }

    // ---- truncation --------------------------------------------------------

    [Fact]
    public void AConfiguredTruncateUsesDeleteNotTruncate()
    {
        // TRUNCATE is faster but SQL Server refuses it on any table referenced
        // by a foreign key, even one with no rows behind it. DELETE always
        // works, and audit tables are emptied for correctness, not speed.
        var schema = SchemaBuilder.Database().Table("dbo.LoginAudit", "Id").Build();

        var step = Assert.Single(PreMask(Plan(schema,
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

        var steps = PreMask(Plan(schema,
            """{ "tables": [ { "name": "dbo.LoginAudit", "strategy": "truncate" } ] }"""));

        Assert.Equal(HygieneStepKind.DisableChangeTracking, steps[0].Kind);
        Assert.Equal(HygieneStepKind.TruncateTable, steps[1].Kind);
    }

    [Fact]
    public void ATableKeptWholesaleIsNotTouched()
    {
        var schema = SchemaBuilder.Database().Table("dbo.StateCode", "Code").Build();

        Assert.Empty(PreMask(Plan(schema, """
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

        var plan = Plan(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble" } ]},
                { "name": "dbo.LoginAudit", "strategy": "truncate" }
              ]
            }
            """);

        var steps = PreMask(plan).Concat(HygienePlanner.BuildPostMask(plan));

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
        var plan = TemporalPlan();
        var steps = PreMask(plan).Concat(HygienePlanner.BuildPostMask(plan));

        Assert.All(steps, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(string.IsNullOrWhiteSpace(s.Target));
        });
    }

    // ---- preflight ---------------------------------------------------------

    [Fact]
    public void SimpleRecoveryIsSetBeforeAnythingElse()
    {
        // SPEC 5.1. Under FULL recovery the log has to hold the whole run,
        // because nobody backs up a disposable local copy — so batching buys
        // nothing unless this comes first.
        var steps = HygienePlanner.BuildPreMask(TemporalPlan());

        Assert.Equal(HygieneStepKind.SetSimpleRecovery, steps[0].Kind);
        Assert.Equal("ALTER DATABASE [AAVSB] SET RECOVERY SIMPLE;", steps[0].Sql);
    }

    [Fact]
    public void SimpleRecoveryIsSetEvenWhenThereIsNoOtherHygiene()
    {
        var plan = Plan(SchemaBuilder.Database().Table("dbo.Person", "Email").Build(), """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "static", "value": "x" } ]} ] }
            """);

        Assert.Equal([HygieneStepKind.SetSimpleRecovery],
            HygienePlanner.BuildPreMask(plan).Select(s => s.Kind));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The pre-mask steps WITHOUT the SIMPLE-recovery preflight, which is
    /// asserted on its own above. Dropping it here keeps every other test about
    /// the thing it is named after rather than about an index shift.
    /// </summary>
    private static IReadOnlyList<HygieneStep> PreMask(ScrubPlan plan) =>
        HygienePlanner.BuildPreMask(plan)
            .Where(s => s.Kind != HygieneStepKind.SetSimpleRecovery)
            .ToList();

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
