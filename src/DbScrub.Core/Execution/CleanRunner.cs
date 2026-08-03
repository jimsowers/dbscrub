using DbScrub.Core.Hygiene;
using DbScrub.Core.Planning;

namespace DbScrub.Core.Execution;

/// <summary>
/// Executes a <see cref="CleanPlan"/> in order (SPEC 5.1–5.3).
///
/// The order is the whole job, and one part of it is not obvious:
///
///     pre-mask hygiene  ->  mask every table  ->  post-mask hygiene
///
/// System versioning is turned OFF in the first phase and back ON only in the
/// third. Masking runs in between, with versioning off, so no masking UPDATE can
/// copy its pre-update row into a history table. Run as SPEC 5.2 literally
/// describes it — detach, empty, reattach, all adjacent — the reattach would
/// happen before the first UPDATE, and masking would refill history with exactly
/// the rows the truncate had just removed. The run would report success.
///
/// What this class deliberately does NOT do: verify, stamp, or rename. Those are
/// step 5, and CLAUDE.md is explicit that a stamp follows a clean verify pass
/// and nothing else. A `clean` that finishes here leaves an unstamped database,
/// which `dbscrub status` correctly reports as not safe to use.
/// </summary>
public static class CleanRunner
{
    public static async Task<CleanOutcome> RunAsync(
        CleanPlan plan,
        int batchSize,
        ICleanSession session,
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
                    batchSize,
                    new Progress<long>(rows => progress?.Report($"  {table.QualifiedName}: {rows:N0} rows")),
                    cancellationToken);

                masked.Add(result);
            }
        }
        catch (Exception ex)
        {
            // Caught rather than propagated so the finally-equivalent below still
            // runs and the caller still learns what got as far as committing.
            // Rethrowing here would leave every temporal table detached.
            failure = ex;
        }

        // ALWAYS, including after a failure and after cancellation. A table left
        // with SYSTEM_VERSIONING off is the worst outcome available here: nothing
        // looks broken, and the database silently stops recording history for
        // everything that touches it from then on.
        var reattachFailure = await ReattachAsync(plan, session, completed, progress);

        return new CleanOutcome(completed, masked, failure, reattachFailure);
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
public sealed record CleanOutcome(
    IReadOnlyList<HygieneStep> HygieneCompleted,
    IReadOnlyList<MaskResult> Masked,
    Exception? Failure,
    Exception? ReattachFailure)
{
    public bool Succeeded => Failure is null && ReattachFailure is null && Incomplete.Count == 0;

    public long RowsUpdated => Masked.Sum(m => m.RowsUpdated);

    /// <summary>
    /// Tables whose rewritten row count did not match the count taken before the
    /// walk. Any entry here means rows were left holding their original values,
    /// so the run has NOT done what it claims — treated as a failure, not a
    /// warning.
    /// </summary>
    public IReadOnlyList<MaskResult> Incomplete => Masked.Where(m => !m.IsComplete).ToList();
}
