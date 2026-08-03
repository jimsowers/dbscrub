using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Verdicts;

/// <summary>
/// What a run WOULD do: the config projected onto the live schema. `report`
/// prints this; `clean` executes it. Building it never touches the database —
/// it is a pure function of (schema, config), which is what makes it testable.
/// </summary>
public sealed record ScrubPlan(
    DatabaseSchema Schema,
    IReadOnlyList<TablePlan> Tables,
    IReadOnlyList<ColumnVerdict> Unclassified,
    IReadOnlyList<ConfigError> Problems)
{
    public IEnumerable<TablePlan> Truncated => Tables.Where(t => t.Action == TableAction.Truncate);

    public IEnumerable<TablePlan> Masked => Tables.Where(t => t.Action == TableAction.Mask);

    /// <summary>Tables declared clean in one line by a table-level `keep`.</summary>
    public IEnumerable<TablePlan> KeptWholesale => Tables.Where(t => t.Action == TableAction.Keep);

    /// <summary>System-versioned tables need the SYSTEM_VERSIONING dance (SPEC 5.2).</summary>
    public IEnumerable<TablePlan> Temporal =>
        Tables.Where(t => t.Table.TemporalType == TemporalType.SystemVersioned);

    /// <summary>Tables carrying CDC capture instances; the database-level flag is on the schema.</summary>
    public IEnumerable<TablePlan> CdcTracked => Tables.Where(t => t.Table.IsTrackedByCdc);

    public int ColumnsToMask =>
        Tables.Sum(t => t.Columns.Count(c => c.Kind == VerdictKind.Masked));

    /// <summary>True when nothing blocks a run: no unclassified columns and no schema-vs-config problems.</summary>
    public bool IsFullyClassified => Unclassified.Count == 0;
}

/// <summary>What happens to one live table.</summary>
/// <param name="IsConfigured">
/// Whether the config has an entry for this table. Distinguishes "configured,
/// and everything in it is keep" from "nobody has looked at this table yet" —
/// both are <see cref="TableAction.Untouched"/>, but the UNCLASSIFIED output
/// has to emit different JSON for each (a whole table entry vs. columns to
/// paste into an existing one).
/// </param>
public sealed record TablePlan(
    SchemaTable Table,
    TableAction Action,
    IReadOnlyList<ColumnVerdict> Columns,
    bool IsConfigured)
{
    public string QualifiedName => Table.QualifiedName;

    /// <summary>
    /// What happens to this table's temporal history (SPEC 5.2). Meaningful only
    /// when <see cref="SchemaTable.TemporalType"/> is SystemVersioned; the
    /// default is Truncate for everything else, which is also the default for a
    /// temporal table nobody configured (DECISIONS.md D5).
    ///
    /// This is carried on the plan rather than looked up from the config later
    /// because the hygiene pass decides whether to empty the history table, and
    /// it only ever sees the plan. Before this existed the hygiene pass emptied
    /// history unconditionally, so `history: "mask"` truncated instead of
    /// masking — the config keyword read as intent that never happened.
    /// </summary>
    public HistoryMode History { get; init; } = HistoryMode.Truncate;

    /// <summary>The columns this run will rewrite. Empty unless <see cref="Action"/> is Mask.</summary>
    public IEnumerable<ColumnVerdict> MaskedColumns => Columns.Where(c => c.Kind == VerdictKind.Masked);

    public override string ToString() => $"{QualifiedName} [{Action}]";
}

public enum TableAction
{
    /// <summary>No config entry, or every column is `keep`. Nothing runs against it.</summary>
    Untouched,

    /// <summary>Table-level `strategy: "truncate"` (DECISIONS.md D5).</summary>
    Truncate,

    /// <summary>
    /// Table-level `strategy: "keep"` — declared clean in one line. Counted
    /// separately in the report so a blanket exclusion stays visible.
    /// </summary>
    Keep,

    /// <summary>At least one column has a masking strategy.</summary>
    Mask,

    /// <summary>
    /// A temporal history table. Not configured directly — its parent's
    /// `history` setting decides, so its columns are resolved, not unclassified.
    /// </summary>
    HistoryOfTemporalTable,

    /// <summary>The tool's own dbo.__SanitizationLog, exempt per SPEC section 4.</summary>
    ToolOwned,
}

/// <summary>
/// The verdict for one live column. SPEC section 4: every column resolves to a
/// strategy, truncate membership, or keep — or it is UNCLASSIFIED.
/// </summary>
public sealed record ColumnVerdict(
    string Schema,
    string Table,
    string Column,
    VerdictKind Kind,
    ColumnStrategy? Strategy,
    string? Reason)
{
    public string QualifiedTable => $"{Schema}.{Table}";

    public string QualifiedColumn => $"{Schema}.{Table}.{Column}";

    /// <summary>
    /// The config's `"value"` for a `static` column, carried through so the mask
    /// planner does not have to re-find this column in the config and repeat the
    /// name matching the resolver already did. Null for every other strategy.
    ///
    /// Deliberately the RAW config value, not a converted one: converting it
    /// needs the live column type, which is the mask planner's job.
    /// </summary>
    public ConfigValue? Value { get; init; }

    public override string ToString() => $"{QualifiedColumn} -> {Kind}";
}

public enum VerdictKind
{
    /// <summary>No verdict. Listed loudly; `fail` mode stops the run (SPEC section 4).</summary>
    Unclassified,

    /// <summary>A column strategy will rewrite it.</summary>
    Masked,

    /// <summary>Explicit `keep` — the author looked and decided there is no PII.</summary>
    Kept,

    /// <summary>Resolved because the whole table is truncated.</summary>
    Truncated,

    /// <summary>Resolved because the parent temporal table's `history` setting covers it.</summary>
    CoveredByParent,

    /// <summary>Exempt: system schema or the tool's own table.</summary>
    Exempt,
}
