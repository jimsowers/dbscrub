namespace DbScrub.Core.Schema;

/// <summary>
/// What the live database looks like right now — the "is" side of the diff.
/// Records, so tests can hand-build one and compare without a server.
/// </summary>
public sealed record DatabaseSchema(
    string DatabaseName,
    bool IsCdcEnabled,
    IReadOnlyList<SchemaTable> Tables);

/// <summary>One user table. System schemas never appear here (see SchemaInventory).</summary>
public sealed record SchemaTable(
    string Schema,
    string Name,
    TemporalType TemporalType,
    bool IsTrackedByCdc,
    string? HistorySchema,
    string? HistoryName,
    IReadOnlyList<SchemaColumn> Columns)
{
    public string QualifiedName => $"{Schema}.{Name}";

    /// <summary>
    /// The history table behind a system-versioned table, e.g. dbo.PersonHistory.
    /// Null unless <see cref="TemporalType"/> is SystemVersioned.
    /// </summary>
    public string? QualifiedHistoryName =>
        HistorySchema is null || HistoryName is null ? null : $"{HistorySchema}.{HistoryName}";

    public override string ToString() => QualifiedName;
}

/// <summary>One column, with the facts the verdict pass and the mask engine need.</summary>
/// <param name="GeneratedAlwaysType">
/// sys.columns.generated_always_type. 0 for an ordinary column; non-zero for a
/// column the engine maintains — temporal period columns (AS_ROW_START /
/// AS_ROW_END) and, on SQL Server 2022+, ledger columns.
///
/// This is read INSTEAD OF is_hidden, which is the wrong signal: a period
/// column declared without the HIDDEN keyword is perfectly visible and still
/// cannot be written. It is also not covered by is_computed or is_identity,
/// which are both 0 for period columns — that gap let ValidFrom look like an
/// ordinary writable datetime2 until the DbScrubTest fixture proved otherwise.
/// </param>
/// <param name="GeneratedAlwaysDescription">
/// sys.columns.generated_always_type_desc, e.g. NOT_APPLICABLE or AS_ROW_START.
/// Carried so the report can say WHY a column was exempted, and so a value this
/// build has never heard of still prints its real name instead of a number.
/// </param>
public sealed record SchemaColumn(
    string Name,
    string DataType,
    bool IsNullable,
    int MaxLength,
    bool IsComputed,
    bool IsIdentity,
    byte GeneratedAlwaysType = 0,
    string GeneratedAlwaysDescription = "NOT_APPLICABLE")
{
    /// <summary>
    /// True when SQL Server maintains this column's value itself. Such a column
    /// cannot be written and cannot hold PII, so it is neither maskable nor
    /// something a human should be asked to classify.
    /// </summary>
    public bool IsSystemGenerated => GeneratedAlwaysType != 0;

    /// <summary>
    /// True for the text types the verify sweep scans and the scramble strategy
    /// can operate on (SPEC 5.4 sweeps ALL string columns, not just masked ones).
    /// </summary>
    public bool IsTextual => DataType is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext";

    /// <summary>
    /// A computed column cannot be written to, an identity column must not be,
    /// and a GENERATED ALWAYS column will have the UPDATE rejected outright.
    /// Either way the mask engine has to skip it, so the verdict pass needs to
    /// know before it promises to mask something it cannot.
    /// </summary>
    public bool IsWritable => !IsComputed && !IsIdentity && !IsSystemGenerated;

    public override string ToString() => Name;
}

/// <summary>
/// Mirrors sys.tables.temporal_type. The numeric values match SQL Server's, so
/// the reader can cast directly and a wrong value fails loudly rather than
/// mapping to something plausible.
/// </summary>
public enum TemporalType
{
    /// <summary>Ordinary table.</summary>
    None = 0,

    /// <summary>The history table BEHIND a system-versioned table, not a table a user configures directly.</summary>
    HistoryTable = 1,

    /// <summary>System-versioned temporal table — needs the SYSTEM_VERSIONING dance in SPEC 5.2.</summary>
    SystemVersioned = 2,
}
