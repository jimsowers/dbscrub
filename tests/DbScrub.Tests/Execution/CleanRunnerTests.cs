using DbScrub.Core.Configuration;
using DbScrub.Core.Execution;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using DbScrub.Core.Planning;
using DbScrub.Core.Stamp;
using DbScrub.Core.Verdicts;
using DbScrub.Core.Verify;
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

        await Run(TemporalPlan(), session);

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

        await Run(TemporalPlan(), session);

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

        await Run(TemporalPlan(), session);

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

        await Run(plan, session);

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

        var outcome = await Run(TemporalPlan(), session);

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

        var outcome = await Run(TemporalPlan(), session);

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

        var outcome = await Run(TemporalPlan(), session);

        Assert.False(outcome.Succeeded);
        var incomplete = Assert.Single(outcome.Incomplete);
        Assert.Equal("dbo.Person", incomplete.QualifiedName);
    }

    [Fact]
    public async Task AnEmptyTableCountsAsComplete()
    {
        var session = new RecordingSession { RowsInTable = 0, RowsUpdated = 0 };

        var outcome = await Run(TemporalPlan(), session);

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
            Run(plan, session));

        Assert.Empty(session.Transcript);
    }

    [Fact]
    public async Task TheOutcomeCountsOnlyWorkThatCommitted()
    {
        var session = new RecordingSession { RowsInTable = 42, RowsUpdated = 42 };

        var outcome = await Run(TemporalPlan(), session);

        Assert.Equal(42, outcome.RowsUpdated);
        Assert.True(outcome.Succeeded);
    }

    // ---- the gate: nothing gets stamped without a clean verify -------------

    [Fact]
    public async Task AStampIsWrittenOnlyAfterVerifyPasses()
    {
        var stamps = new RecordingStampWriter();

        var outcome = await Run(TemporalPlan(), new RecordingSession(), stampWriter: stamps);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Stamped);
        Assert.Single(stamps.Written);
    }

    [Fact]
    public async Task VerifyHitsBlockTheStamp()
    {
        // SPEC 5.4 — the gate. Since D10 removed the naming distinction, the
        // stamp is the ONLY thing separating a scrubbed copy from a raw one, so
        // a stamp written over surviving PII makes every later answer wrong.
        var stamps = new RecordingStampWriter();
        var verifier = new FakeVerifier(new VerifyReport(
            [new VerifyHit("dbo", "Person", "Notes", "email", 3)], ColumnsScanned: 4, RowsInspected: 3));

        var outcome = await Run(TemporalPlan(), new RecordingSession(), verifier, stamps);

        Assert.False(outcome.Stamped);
        Assert.False(outcome.Succeeded);
        Assert.Empty(stamps.Written);
        Assert.Equal(3, outcome.Verify!.TotalHits);
    }

    [Fact]
    public async Task FailedMaskingIsNotEvenVerified()
    {
        // The database is in an unknown state, so there is nothing worth
        // checking and certainly nothing worth stamping.
        var verifier = new FakeVerifier(VerifyReport.Clean(0, 0));
        var stamps = new RecordingStampWriter();

        var outcome = await Run(
            TemporalPlan(), new RecordingSession { FailMaskingOn = "dbo.Person" }, verifier, stamps);

        Assert.Null(outcome.Verify);
        Assert.False(outcome.Stamped);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(stamps.Written);
    }

    [Fact]
    public async Task RowsLeftUnmaskedBlockTheStampToo()
    {
        var stamps = new RecordingStampWriter();

        var outcome = await Run(
            TemporalPlan(), new RecordingSession { RowsInTable = 100, RowsUpdated = 40 }, stampWriter: stamps);

        Assert.False(outcome.Stamped);
        Assert.Empty(stamps.Written);
    }

    [Fact]
    public async Task TheRunTellsVerifyWhatItWrote()
    {
        // The end-to-end half of the static-replacement gap. Verify cannot
        // derive "dev@example.invalid" from its shape — the run has to declare
        // it, or the gate reports the column it just masked as a leak.
        var verifier = new FakeVerifier(VerifyReport.Clean(2, 0));

        var plan = Build(
            SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email")
                .WithPrimaryKey("PersonId").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
            """);

        await Run(plan, new RecordingSession(), verifier);

        Assert.Equal(["dev@example.invalid"], verifier.SawReplacements!.Order());
    }

    [Fact]
    public async Task AVerifyThatThrowsIsNotAVerifyThatPassed()
    {
        var stamps = new RecordingStampWriter();

        var outcome = await Run(
            TemporalPlan(), new RecordingSession(), new ThrowingVerifier(), stamps);

        Assert.False(outcome.Stamped);
        Assert.NotNull(outcome.Failure);
        Assert.Empty(stamps.Written);
    }

    [Fact]
    public async Task AFailedStampLeavesACleanDatabaseThatSaysItIsDirty()
    {
        // The safe direction to fail in: `status` reports it unsanitized and the
        // worst cost is a re-run. The reverse would be a database everybody
        // believes is safe.
        var outcome = await Run(
            TemporalPlan(), new RecordingSession(), stampWriter: new ThrowingStampWriter());

        Assert.False(outcome.Stamped);
        Assert.NotNull(outcome.Failure);
        Assert.True(outcome.Verify!.Passed);
    }

    [Fact]
    public async Task TheStampRecordsWhatTheRunActuallyDid()
    {
        var stamps = new RecordingStampWriter();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero));

        await CleanRunner.RunAsync(
            TemporalPlan(),
            new CleanOptions(100, "abc123", "9.9.9", clock),
            new RecordingSession { RowsInTable = 42, RowsUpdated = 42 },
            new FakeVerifier(VerifyReport.Clean(4, 0)),
            stamps);

        var record = Assert.Single(stamps.Written);

        Assert.Equal(new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc), record.RunUtc);
        Assert.Equal("abc123", record.ConfigHash);
        Assert.Equal("9.9.9", record.ToolVersion);
        Assert.Equal(42, record.RowsUpdated);
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

    /// <summary>
    /// Defaults everything the tests do not care about: a verify that passes and
    /// a stamp writer that records. A test that cares passes its own.
    /// </summary>
    private static Task<CleanOutcome> Run(
        CleanPlan plan,
        RecordingSession session,
        IVerifier? verifier = null,
        IStampWriter? stampWriter = null) =>
        CleanRunner.RunAsync(
            plan,
            new CleanOptions(BatchSize: 100, ConfigHash: "test-hash", ToolVersion: "test", TimeProvider.System),
            session,
            verifier ?? new FakeVerifier(VerifyReport.Clean(columnsScanned: 4, rowsInspected: 0)),
            stampWriter ?? new RecordingStampWriter());

    private sealed class FakeVerifier(VerifyReport report) : IVerifier
    {
        public int Calls { get; private set; }

        public IReadOnlySet<string>? SawReplacements { get; private set; }

        public Task<VerifyReport> VerifyAsync(
            Core.Schema.DatabaseSchema schema,
            IReadOnlySet<string> knownReplacements,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            SawReplacements = knownReplacements;
            return Task.FromResult(report);
        }
    }

    private sealed class ThrowingVerifier : IVerifier
    {
        public Task<VerifyReport> VerifyAsync(
            Core.Schema.DatabaseSchema schema,
            IReadOnlySet<string> knownReplacements,
            CancellationToken cancellationToken = default) =>
            Task.FromException<VerifyReport>(new InvalidOperationException("the sweep blew up"));
    }

    private sealed class RecordingStampWriter : IStampWriter
    {
        public List<StampRecord> Written { get; } = [];

        public Task WriteAsync(StampRecord record, CancellationToken cancellationToken = default)
        {
            Written.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStampWriter : IStampWriter
    {
        public Task WriteAsync(StampRecord record, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("could not write the stamp"));
    }

    /// <summary>A clock that does not move, so the stamp's timestamp is an exact assertion.</summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
