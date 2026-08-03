namespace DbScrub.Core.Verify;

/// <summary>
/// What the verify sweep found (SPEC 5.4). The gate: nothing below it in the
/// pipeline runs unless <see cref="Passed"/> is true — no stamp, no rename.
///
/// Carries COUNTS and never values. That is not an oversight to be improved on
/// later: the whole point of this run is that the database might still contain
/// real personal data, so anything this type could print is exactly what must
/// not be printed (CLAUDE.md).
/// </summary>
public sealed record VerifyReport(
    IReadOnlyList<VerifyHit> Hits,
    int ColumnsScanned,
    long RowsInspected)
{
    /// <summary>True when nothing that looks like personal data survived masking.</summary>
    public bool Passed => Hits.Count == 0;

    /// <summary>Total hits across every column, for the one-line summary.</summary>
    public long TotalHits => Hits.Sum(h => h.Count);

    public static VerifyReport Clean(int columnsScanned, long rowsInspected) =>
        new([], columnsScanned, rowsInspected);
}

/// <summary>
/// One column that still holds values matching a pattern, and how many.
/// </summary>
/// <param name="Count">
/// How many values matched — NOT which ones. A caller wanting to see them has to
/// go and look at the database itself, deliberately.
/// </param>
public sealed record VerifyHit(
    string Schema,
    string Table,
    string Column,
    string Pattern,
    long Count)
{
    public string QualifiedColumn => $"{Schema}.{Table}.{Column}";

    public override string ToString() => $"{QualifiedColumn}  {Pattern}  {Count:N0} value(s)";
}
