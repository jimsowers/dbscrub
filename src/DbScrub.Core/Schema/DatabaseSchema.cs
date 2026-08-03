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

    /// <summary>
    /// The primary key's columns, in KEY ORDER — the order they were declared
    /// in, which is the order an index seek uses and therefore the order the
    /// mask engine walks the table in (SPEC 5.3, "ordered PK ranges").
    ///
    /// Key order is not column order and not alphabetical. Getting it wrong
    /// would not throw; it would produce a keyset predicate that quietly skips
    /// rows, which in this tool means rows that keep their real values. That is
    /// why this is an ordered list rather than a set, and why the reader sorts
    /// by key_ordinal.
    ///
    /// Empty for a heap, and — worth knowing — empty for a temporal history
    /// table too: SQL Server gives those a clustered index, never a primary key.
    ///
    /// An init property rather than a positional parameter, so the existing call
    /// sites and test builders keep compiling and `with { PrimaryKey = ... }`
    /// reads as the deliberate act it is. Like every other collection member on
    /// a record it compares by REFERENCE, not element by element — the same
    /// caveat already recorded on MaskingConfig, and the reason tests assert on
    /// the sequence rather than on whole table objects.
    /// </summary>
    public IReadOnlyList<string> PrimaryKey { get; init; } = [];

    /// <summary>
    /// Whether this table can be walked in key order. False means the mask
    /// engine has no way to address an individual row, which limits it to
    /// strategies whose replacement is the same for every row.
    /// </summary>
    public bool HasPrimaryKey => PrimaryKey.Count > 0;

    /// <summary>
    /// The key columns resolved against <see cref="Columns"/>, in key order.
    /// The mask engine needs the column's TYPE to bind a parameter, not just
    /// its name.
    /// </summary>
    public IReadOnlyList<SchemaColumn> KeyColumns =>
        PrimaryKey
            .Select(name => Columns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<SchemaColumn>()
            .ToList();

    /// <summary>Whether <paramref name="columnName"/> is part of the primary key.</summary>
    public bool IsKeyColumn(string columnName) =>
        PrimaryKey.Any(name => string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase));

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
    /// How many CHARACTERS this column holds, or null when the answer is
    /// "effectively unbounded" or the column is not textual.
    ///
    /// sys.columns.max_length is in BYTES, which is the trap: an nvarchar(100)
    /// reports 200, and a varchar(100) reports 100. The two Unicode types are
    /// therefore halved. -1 means one of the (max) types, which is 2GB and needs
    /// no checking. `text` and `ntext` report the size of a pointer rather than
    /// of the data, so they are treated as unbounded too — deprecated types the
    /// static-value length check has nothing useful to say about.
    /// </summary>
    public int? MaxLengthInCharacters => DataType switch
    {
        "text" or "ntext" => null,
        "nchar" or "nvarchar" => MaxLength < 0 ? null : MaxLength / 2,
        "char" or "varchar" => MaxLength < 0 ? null : MaxLength,
        _ => null,
    };

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
