using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Masking;

/// <summary>
/// Everything the mask engine will rewrite, decided before anything runs
/// (SPEC 5.3). Pure data: building it touches no database, so the whole of what
/// `clean` is about to do can be printed, reviewed and unit-tested first.
/// </summary>
/// <param name="Problems">
/// Reasons this plan cannot run. Non-empty means `clean` stops BEFORE its first
/// mutation — which is the entire value of resolving all this up front, because
/// the same problems discovered mid-run would leave a half-masked database.
/// </param>
public sealed record MaskPlan(
    IReadOnlyList<TableMaskPlan> Tables,
    IReadOnlyList<ConfigError> Problems)
{
    public bool CanRun => Problems.Count == 0;

    public int ColumnCount => Tables.Sum(t => t.Columns.Count);
}

/// <summary>One table's masking work.</summary>
public sealed record TableMaskPlan(
    SchemaTable Table,
    IReadOnlyList<MaskColumn> Columns,
    MaskMode Mode)
{
    public string QualifiedName => Table.QualifiedName;

    /// <summary>The primary key columns, in key order. Empty in <see cref="MaskMode.WholeTable"/>.</summary>
    public IReadOnlyList<SchemaColumn> KeyColumns => Table.KeyColumns;

    /// <summary>
    /// The columns whose new value has to be computed from the old one — today
    /// that means `scramble` and nothing else. Their presence is what forces
    /// <see cref="MaskMode.RowByRow"/>, because a replacement that depends on
    /// the current value cannot be expressed as one set-based UPDATE.
    /// </summary>
    public IReadOnlyList<MaskColumn> ComputedColumns =>
        Columns.Where(c => !c.IsConstant).ToList();

    /// <summary>The columns whose new value is the same for every row: `null` and `static`.</summary>
    public IReadOnlyList<MaskColumn> ConstantColumns =>
        Columns.Where(c => c.IsConstant).ToList();

    public override string ToString() => $"{QualifiedName} [{Mode}, {Columns.Count} column(s)]";
}

/// <summary>How one table gets rewritten. Chosen by <see cref="MaskPlanner"/>, never by the executor.</summary>
public enum MaskMode
{
    /// <summary>
    /// Read a batch of rows in key order, compute each new value in memory,
    /// write the batch back keyed on the primary key. The only mode that can
    /// handle `scramble`, and the only one that needs a key.
    /// </summary>
    RowByRow,

    /// <summary>
    /// One set-based UPDATE per key range. No rows are read, because every
    /// replacement is a constant — the key walk exists only to keep any single
    /// transaction (and therefore the log) bounded.
    /// </summary>
    BatchedConstant,

    /// <summary>
    /// One set-based UPDATE for the whole table, in one transaction. The
    /// fallback for a table with no primary key (SPEC 5.3). Correct, but
    /// unbounded — the run logs a warning naming the table.
    /// </summary>
    WholeTable,
}

/// <summary>
/// One column to rewrite, with its strategy already resolved against the live
/// column.
/// </summary>
/// <param name="Value">
/// The converted replacement for `static`, ready to bind as a parameter. Null
/// for every other strategy — including `null`, whose replacement is written as
/// the SQL keyword rather than as a parameter, so that a bug binding a null
/// parameter can never be mistaken for the `null` strategy working.
/// </param>
public sealed record MaskColumn(
    SchemaColumn Column,
    ColumnStrategy Strategy,
    object? Value)
{
    public string Name => Column.Name;

    /// <summary>True when the replacement does not depend on the row's current value.</summary>
    public bool IsConstant => Strategy is ColumnStrategy.Null or ColumnStrategy.Static;

    public override string ToString() => $"{Name} [{Strategy}]";
}
