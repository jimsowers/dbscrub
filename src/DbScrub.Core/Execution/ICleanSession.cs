using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;

namespace DbScrub.Core.Execution;

/// <summary>
/// The one place `clean` is allowed to change a database. Everything that
/// mutates goes through these three methods, so "what can this tool modify?" is
/// answered by reading one interface.
///
/// It exists as an interface for a second reason that matters more than usual
/// here: it lets <see cref="CleanRunner"/> — which owns the ORDER things happen
/// in, and whose order is what keeps masking from refilling temporal history —
/// be tested against a recording double instead of a live SQL Server. The
/// sequence is the correctness, so the sequence is what the tests assert.
/// </summary>
public interface ICleanSession : IAsyncDisposable
{
    /// <summary>Runs one hygiene statement (SPEC 5.2) exactly as it was printed.</summary>
    Task ExecuteAsync(HygieneStep step, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites one table (SPEC 5.3), batching per the plan's
    /// <see cref="MaskMode"/>. Returns what it did, including the row count it
    /// reconciled against.
    /// </summary>
    /// <param name="batchSize">Rows per transaction, from the config.</param>
    /// <param name="rowsDone">
    /// Reports cumulative rows rewritten as batches commit, so a long table can
    /// show progress instead of appearing hung.
    /// </param>
    Task<MaskResult> MaskTableAsync(
        TableMaskPlan plan,
        int batchSize,
        IProgress<long>? rowsDone = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What masking one table actually did.
/// </summary>
/// <param name="RowsInTable">
/// The row count taken immediately before the walk started. Present so the run
/// can PROVE it visited every row rather than assume it: the batching predicate
/// is the one piece of generated SQL whose bug would be silent, because a
/// skipped row is not an error, it is a row that keeps its real values.
/// </param>
public sealed record MaskResult(string QualifiedName, long RowsInTable, long RowsUpdated)
{
    /// <summary>
    /// True when every row the table had was rewritten. False is a serious
    /// finding, not a warning — see <see cref="CleanRunner"/>.
    /// </summary>
    public bool IsComplete => RowsUpdated >= RowsInTable;

    public override string ToString() => $"{QualifiedName}: {RowsUpdated:N0}/{RowsInTable:N0} rows";
}
