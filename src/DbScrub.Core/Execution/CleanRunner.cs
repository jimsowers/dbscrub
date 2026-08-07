using DbScrub.Core.Hygiene;
using DbScrub.Core.Planning;
using DbScrub.Core.Stamp;
using DbScrub.Core.Verify;

namespace DbScrub.Core.Execution;

/// <summary>
/// Executes a <see cref="CleanPlan"/> in order (SPEC 5.1–5.5).
///
/// Two orderings live here, and both are the correctness of the whole tool.
///
/// **Masking sits between the hygiene phases.**
///
///     pre-mask hygiene  ->  mask every table  ->  post-mask hygiene
///
/// System versioning is turned OFF in the first phase and back ON only in the
/// third. Run as SPEC 5.2 literally describes it — detach, empty, reattach, all
/// adjacent — the reattach would happen before the first UPDATE, and masking
/// would refill history with exactly the rows the truncate had just removed. The
/// run would report success.
///
/// **The stamp comes last, and only after verify passes.**
///
///     ... -> verify -> stamp
///
/// Since DECISIONS.md D10 removed the naming distinction, the stamp is the only
/// thing separating a scrubbed copy from a raw one. `status` reads it, and the
/// read-only Guard eventually will too. So every path that could reach the stamp
/// without a clean verify has to be closed, which is why this method owns both
/// steps rather than leaving the sequence to a caller.
/// </summary>
public static class CleanRunner
{
    public static async Task<CleanOutcome> RunAsync(
        CleanPlan plan,
        CleanOptions options,
        ICleanSession session,
        IVerifier verifier,
        IStampWriter stampWriter,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanRun)
        {
            // Should be unreachable: the caller checks first. Refusing anyway,
            // because the failure mode is a half-masked database and the cost of
            // the check is one comparison.
            throw new InvalidOperationException(
                $"Refusing to run a plan with {plan.Problems.Count} unresolved problem(s).");
        }

        var clock = options.Clock;
        var startedUtc = clock.GetUtcNow().UtcDateTime;
        var startedAt = clock.GetTimestamp();

        var completed = new List<HygieneStep>();
        var masked = new List<MaskResult>();
        Exception? failure = null;

        try
        {
            foreach (var step in plan.PreMask)
            {
                progress?.Report(step.Description);
                await session.ExecuteAsync(step, cancellationToken);
                completed.Add(step);
            }

            foreach (var table in plan.Mask.Tables)
            {
                progress?.Report($"Masking {table.QualifiedName} ({table.Columns.Count} column(s))");

                var result = await session.MaskTableAsync(
                    table,
                    options.BatchSize,
                    new InlineProgress<long>(rows => progress?.Report($"  {table.QualifiedName}: {rows:N0} rows")),
                    cancellationToken);

                masked.Add(result);
            }
        }
        catch (Exception ex)
        {
            // Caught rather than propagated so the reattach below still runs and
            // the caller still learns what got as far as committing. Rethrowing
            // here would leave every temporal table detached.
            failure = ex;
        }

        // ALWAYS, including after a failure and after cancellation. A table left
        // with SYSTEM_VERSIONING off is the worst outcome available here: nothing
        // looks broken, and the database silently stops recording history for
        // everything that touches it from then on.
        var reattachFailure = await ReattachAsync(plan, session, completed, progress);

        var outcome = new CleanOutcome(completed, masked, failure, reattachFailure, Verify: null, Stamped: false);

        // ---- the gate --------------------------------------------------------

        // Anything wrong so far means the database is in an unknown state, so
        // there is nothing worth verifying and certainly nothing worth stamping.
        if (!outcome.MaskingSucceeded)
        {
            return outcome;
        }

        progress?.Report($"Verifying every string column in {plan.Scrub.Schema.DatabaseName}");

        VerifyReport report;
        try
        {
            // The gate has to be told what this run wrote, or it reports the
            // masked columns as leaks (see PlaceholderRules).
            report = await verifier.VerifyAsync(
                plan.Scrub.Schema, plan.Mask.ReplacementValues, cancellationToken);
        }
        catch (Exception ex)
        {
            // A verify that could not complete is NOT a verify that passed.
            return outcome with { Failure = ex };
        }

        outcome = outcome with { Verify = report };

        if (!report.Passed)
        {
            // SPEC 5.4: any hit means no stamp and no rename. The caller exits 2.
            return outcome;
        }

        // ---- earned ----------------------------------------------------------

        progress?.Report("Verify passed — writing the sanitization stamp");

        try
        {
            await stampWriter.WriteAsync(
                new StampRecord(
                    RunUtc: startedUtc,
                    ToolVersion: options.ToolVersion,
                    ConfigHash: options.ConfigHash,
                    TablesTouched: completed.Count(s => s.Kind is HygieneStepKind.TruncateTable
                            or HygieneStepKind.TruncateHistory)
                        + masked.Count,
                    RowsUpdated: masked.Sum(m => m.RowsUpdated),
                    Duration: clock.GetElapsedTime(startedAt)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The database IS clean; it just does not say so. That is the safe
            // direction to fail in — `status` will report it unsanitized and the
            // worst cost is a re-run.
            return outcome with { Failure = ex };
        }

        return outcome with { Stamped = true };
    }

    private static async Task<Exception?> ReattachAsync(
        CleanPlan plan,
        ICleanSession session,
        List<HygieneStep> completed,
        IProgress<string>? progress)
    {
        foreach (var step in plan.PostMask)
        {
            try
            {
                progress?.Report(step.Description);

                // Deliberately NOT passing the cancellation token. This runs
                // BECAUSE something went wrong, cancellation included, and a
                // cancelled reattach is the one thing that must still happen.
                await session.ExecuteAsync(step, CancellationToken.None);
                completed.Add(step);
            }
            catch (Exception ex)
            {
                // Stop at the first failure and report it. Pressing on would
                // most likely produce the same error per table and bury the one
                // message that matters.
                return ex;
            }
        }

        return null;
    }
}

/// <summary>
/// The run's settings and the facts the stamp will record about it.
/// </summary>
/// <param name="Clock">
/// Injected so the stamp's timestamp and duration are testable. Defaults to the
/// real clock; tests pass a fake one and assert on an exact value rather than on
/// "something near now".
/// </param>
public sealed record CleanOptions(
    int BatchSize,
    string ConfigHash,
    string ToolVersion,
    TimeProvider Clock)
{
    public static CleanOptions For(int batchSize, string configHash) =>
        new(batchSize, configHash, StampRecord.CurrentToolVersion, TimeProvider.System);
}

/// <summary>
/// What a run did, whether or not it finished. Everything here is a fact about
/// work that COMMITTED, so a partially completed run reports honestly rather
/// than as all-or-nothing.
/// </summary>
/// <param name="ReattachFailure">
/// A failure to re-enable system versioning. Reported separately from
/// <paramref name="Failure"/> because it needs a different sentence from the
/// operator's point of view: the database is left with versioning OFF on at
/// least one table and needs a manual ALTER TABLE.
/// </param>
/// <param name="Verify">
/// Null when the sweep never ran, which is itself meaningful — it means masking
/// did not get far enough to be worth checking. Not the same as a sweep that ran
/// and found nothing.
/// </param>
public sealed record CleanOutcome(
    IReadOnlyList<HygieneStep> HygieneCompleted,
    IReadOnlyList<MaskResult> Masked,
    Exception? Failure,
    Exception? ReattachFailure,
    VerifyReport? Verify,
    bool Stamped)
{
    /// <summary>
    /// Everything up to and including masking went as planned. Verify may not
    /// have run yet — this is the precondition for running it, not a verdict on
    /// the database.
    /// </summary>
    public bool MaskingSucceeded =>
        Failure is null && ReattachFailure is null && Incomplete.Count == 0;

    /// <summary>The whole run worked and the database now says so.</summary>
    public bool Succeeded => MaskingSucceeded && Verify is { Passed: true } && Stamped;

    public long RowsUpdated => Masked.Sum(m => m.RowsUpdated);

    /// <summary>
    /// Tables whose rewritten row count did not match the count taken before the
    /// walk. Any entry here means rows were left holding their original values,
    /// so the run has NOT done what it claims — treated as a failure, not a
    /// warning.
    /// </summary>
    public IReadOnlyList<MaskResult> Incomplete => Masked.Where(m => !m.IsComplete).ToList();
}
