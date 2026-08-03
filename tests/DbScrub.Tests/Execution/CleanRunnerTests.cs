using DbScrub.Core.Configuration;
using DbScrub.Core.Execution;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using DbScrub.Core.Planning;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Execution;

/// <summary>
/// The ORDER things happen in, asserted against a recording double.
///
/// This is the class that guards the bug HANDOFF.md warned about for step 4:
/// SPEC 5.2 describes the temporal dance as detach, empty, reattach — three
/// adjacent steps — which is correct while hygiene is all there is, and wrong the
/// moment masking exists. Masking has to sit between the detach and the reattach,
/// or every masking UPDATE copies its pre-update row into the history table that
/// was emptied seconds earlier, and the run reports success.
/// </summary>
public class CleanRunnerTests
{
    [Fact]
    public async Task MaskingHappensBetweenTheDetachAndTheReattach()
    {
        var session = new RecordingSession();

        await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        var detach = session.Transcript.FindIndex(e => e.Contains("SYSTEM_VERSIONING = OFF"));
        var mask = session.Transcript.FindIndex(e => e.StartsWith("MASK ", StringComparison.Ordinal));
        var reattach = session.Transcript.FindIndex(e => e.Contains("SYSTEM_VERSIONING = ON"));

        Assert.True(detach >= 0 && mask >= 0 && reattach >= 0,
            $"Expected all three phases in the transcript:{Environment.NewLine}{session}");

        Assert.True(detach < mask,
            $"Masking ran while versioning was still ON — history now holds the unmasked rows."
            + $"{Environment.NewLine}{session}");

        Assert.True(mask < reattach,
            $"Versioning was re-enabled before masking, so masking refilled history."
            + $"{Environment.NewLine}{session}");
    }

    [Fact]
    public async Task HistoryIsEmptiedBeforeMaskingRatherThanAfter()
    {
        // The other way round, the truncate would delete the history rows that
        // masking had just correctly produced — harmless, but it would mean the
        // detach was pointless and hide a real ordering error.
        var session = new RecordingSession();

        await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        var truncate = session.Transcript.FindIndex(e => e.Contains("TRUNCATE TABLE"));
        var mask = session.Transcript.FindIndex(e => e.StartsWith("MASK ", StringComparison.Ordinal));

        Assert.True(truncate < mask, session.ToString());
    }

    [Fact]
    public async Task RecoveryIsSetToSimpleBeforeAnythingElse()
    {
        // Under FULL recovery the log has to hold the entire run, so batching
        // buys nothing. It has to come first to be worth anything.
        var session = new RecordingSession();

        await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.Contains("RECOVERY SIMPLE", session.Transcript[0]);
    }

    [Fact]
    public async Task ChangeTrackingIsDisabledBeforeMasking()
    {
        var session = new RecordingSession();
        var plan = Build(
            SchemaBuilder.Database().WithCdcEnabled()
                .CdcTable("dbo.Person", "PersonId", "Email").WithPrimaryKey("PersonId")
                .Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        await CleanRunner.RunAsync(plan, batchSize: 100, session);

        var cdc = session.Transcript.FindIndex(e => e.Contains("sp_cdc_disable_db"));
        var mask = session.Transcript.FindIndex(e => e.StartsWith("MASK ", StringComparison.Ordinal));

        Assert.True(cdc >= 0 && cdc < mask, session.ToString());
    }

    // ---- failure paths -----------------------------------------------------

    [Fact]
    public async Task VersioningIsReattachedEvenWhenMaskingFails()
    {
        // The worst outcome available here is a table left detached: nothing
        // about the database looks broken, and it silently records no history
        // from then on.
        var session = new RecordingSession { FailMaskingOn = "dbo.Person" };

        var outcome = await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.NotNull(outcome.Failure);
        Assert.Contains(session.Transcript, e => e.Contains("SYSTEM_VERSIONING = ON"));
        Assert.Null(outcome.ReattachFailure);
    }

    [Fact]
    public async Task AFailedReattachIsReportedSeparatelyFromAFailedMask()
    {
        // They need different sentences: one means "restore and try again", the
        // other means "this database needs an ALTER TABLE before you use it".
        var session = new RecordingSession { FailHygieneOn = HygieneStepKind.ReEnableVersioning };

        var outcome = await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.ReattachFailure);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task ATableThatRewroteFewerRowsThanItHoldsIsAFailure()
    {
        // Rows the walk never visited still hold their original values. This is
        // the check that turns a silent batching bug into a failed run.
        var session = new RecordingSession { RowsInTable = 100, RowsUpdated = 99 };

        var outcome = await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.False(outcome.Succeeded);
        var incomplete = Assert.Single(outcome.Incomplete);
        Assert.Equal("dbo.Person", incomplete.QualifiedName);
    }

    [Fact]
    public async Task AnEmptyTableCountsAsComplete()
    {
        var session = new RecordingSession { RowsInTable = 0, RowsUpdated = 0 };

        var outcome = await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.True(outcome.Succeeded);
        Assert.Empty(outcome.Incomplete);
    }

    [Fact]
    public async Task APlanWithProblemsIsRefusedBeforeAnythingRuns()
    {
        var session = new RecordingSession();

        // "null" on a NOT NULL column — a problem only the live schema reveals.
        var plan = Build(
            SchemaBuilder.Database().Table("dbo.Person", SchemaBuilder.NotNull("Email")).Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "null" } ]} ] }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CleanRunner.RunAsync(plan, batchSize: 100, session));

        Assert.Empty(session.Transcript);
    }

    [Fact]
    public async Task TheOutcomeCountsOnlyWorkThatCommitted()
    {
        var session = new RecordingSession { RowsInTable = 42, RowsUpdated = 42 };

        var outcome = await CleanRunner.RunAsync(TemporalPlan(), batchSize: 100, session);

        Assert.Equal(42, outcome.RowsUpdated);
        Assert.True(outcome.Succeeded);
    }

    // ---- harness -----------------------------------------------------------

    /// <summary>
    /// Records what it was asked to do and does none of it. The transcript is
    /// the assertion surface: this class is about sequence, and a sequence is
    /// exactly what a real database makes hard to observe.
    /// </summary>
    private sealed class RecordingSession : ICleanSession
    {
        public List<string> Transcript { get; } = [];

        public string? FailMaskingOn { get; init; }

        public HygieneStepKind? FailHygieneOn { get; init; }

        public long RowsInTable { get; init; } = 10;

        public long RowsUpdated { get; init; } = 10;

        public Task ExecuteAsync(HygieneStep step, CancellationToken cancellationToken = default)
        {
            Transcript.Add(step.Sql);

            return step.Kind == FailHygieneOn
                ? Task.FromException(new InvalidOperationException($"refusing {step.Kind}"))
                : Task.CompletedTask;
        }

        public Task<MaskResult> MaskTableAsync(
            TableMaskPlan plan,
            int batchSize,
            IProgress<long>? rowsDone = null,
            CancellationToken cancellationToken = default)
        {
            Transcript.Add($"MASK {plan.QualifiedName}");

            if (plan.QualifiedName == FailMaskingOn)
            {
                return Task.FromException<MaskResult>(new InvalidOperationException("masking blew up"));
            }

            return Task.FromResult(new MaskResult(plan.QualifiedName, RowsInTable, RowsUpdated));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() =>
            string.Join(Environment.NewLine, Transcript.Select((e, i) => $"  {i}: {e}"));
    }

    private static CleanPlan Build(Core.Schema.DatabaseSchema schema, string configJson) =>
        CleanPlan.Build(VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test")));

    private static CleanPlan TemporalPlan() => Build(
        SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "Email")
            .WithPrimaryKeyOn("dbo.Person", "PersonId")
            .Build(),
        """
        { "tables": [ { "name": "dbo.Person", "columns": [
            { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
            { "name": "Email", "strategy": "scramble" } ]} ] }
        """);
}
