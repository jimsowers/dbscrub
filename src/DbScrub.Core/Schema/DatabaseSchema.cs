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
public sealed record SchemaColumn(
    string Name,
    string DataType,
    bool IsNullable,
    int MaxLength,
    bool IsComputed,
    bool IsIdentity)
{
    /// <summary>
    /// True for the text types the verify sweep scans and the scramble strategy
    /// can operate on (SPEC 5.4 sweeps ALL string columns, not just masked ones).
    /// </summary>
    public bool IsTextual => DataType is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext";

    /// <summary>
    /// A computed column cannot be written to, and an identity column must not
    /// be. Either way the mask engine has to skip it, so the verdict pass needs
    /// to know before it promises to mask something it cannot.
    /// </summary>
    public bool IsWritable => !IsComputed && !IsIdentity;

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
