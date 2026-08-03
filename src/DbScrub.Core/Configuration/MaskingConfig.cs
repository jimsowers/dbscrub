using System.Text.Json;

namespace DbScrub.Core.Configuration;

/// <summary>
/// The parsed, validated masking config (SPEC section 4). Everything here is a
/// record, so two configs built from the same JSON compare equal member by
/// member — which keeps tests honest without an assertion library
/// (DECISIONS.md D13). Note the caveat: records give structural equality to
/// scalar and record members, NOT to list members, so tests assert on the
/// sequences (Assert.Equal walks IEnumerable element by element) rather than
/// on a whole config object.
/// </summary>
public sealed record MaskingConfig(
    ConfigDefaults Defaults,
    IReadOnlyList<TableConfig> Tables);

/// <summary>
/// The `defaults` block. Every property has a documented fallback, so a config
/// that omits the block entirely is still valid and still safe.
/// </summary>
public sealed record ConfigDefaults(
    IReadOnlyList<string> AllowedServers,
    UnclassifiedMode UnclassifiedColumns,
    int BatchSize,
    string? RenameTo,
    IReadOnlyList<string> RepairUsers)
{
    /// <summary>
    /// SPEC section 3.1. `(local)` is in here because it is a common legacy
    /// alias in web.config connection strings, not because it is elegant.
    /// A named instance (localhost\SQL2022) is NOT covered by these and must
    /// be listed explicitly — that is deliberate.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedServers =
        ["localhost", ".", "(local)", "127.0.0.1"];

    /// <summary>SPEC section 4: v0 default is warn, per DECISIONS.md D6.</summary>
    public const UnclassifiedMode DefaultUnclassifiedColumns = UnclassifiedMode.Warn;

    /// <summary>SPEC section 4.</summary>
    public const int DefaultBatchSize = 5000;

    /// <summary>
    /// Not from the spec: a sanity ceiling. batchSize is the number of rows
    /// held in memory and pushed through a table-valued parameter per
    /// transaction (SPEC 5.3), so a fat-fingered extra zero is a memory
    /// problem, not a typo we should honor.
    /// </summary>
    public const int MaxBatchSize = 1_000_000;

    /// <summary>What you get when the `defaults` block is absent entirely.</summary>
    public static ConfigDefaults CreateDefault() => new(
        AllowedServers: DefaultAllowedServers,
        UnclassifiedColumns: DefaultUnclassifiedColumns,
        BatchSize: DefaultBatchSize,
        RenameTo: null,
        RepairUsers: []);
}

/// <summary>
/// One entry in `tables`. Either the whole table is truncated
/// (<see cref="Strategy"/> = Truncate, no columns), or it carries per-column
/// strategies — never both; the validator rejects the contradiction rather
/// than silently picking a winner.
/// </summary>
public sealed record TableConfig(
    string Schema,
    string Name,
    TableStrategy? Strategy,
    HistoryMode History,
    IReadOnlyList<ColumnConfig> Columns,
    string? Reason = null)
{
    /// <summary>`dbo.Person` — how the config writes it and how errors print it.</summary>
    public string QualifiedName => $"{Schema}.{Name}";

    public override string ToString() => QualifiedName;
}

/// <summary>One entry in a table's `columns`.</summary>
/// <param name="Unique">
/// Whether every row should get a DIFFERENT masked value, seeded from its
/// primary key (DECISIONS.md D23). A modifier rather than a strategy of its
/// own: uniqueness is orthogonal to HOW a value is masked, and folding it into
/// the strategy name would multiply a deliberately closed set.
/// </param>
public sealed record ColumnConfig(
    string Name,
    ColumnStrategy Strategy,
    ConfigValue? Value,
    string? Reason,
    UniqueMode Unique = UniqueMode.None);

/// <summary>How a column achieves per-row distinctness (DECISIONS.md D23).</summary>
public enum UniqueMode
{
    /// <summary>The default: every row gets the same masked value.</summary>
    None,

    /// <summary>Seeded from the row's primary key — unique by construction, and stable across runs.</summary>
    Key,
}

/// <summary>
/// The literal behind `"value"` for a static strategy, kept as raw text plus
/// its JSON kind rather than as a CLR object.
///
/// Why not just `object?`: step 4 has to type-check this against the real
/// column type (SPEC section 4), and "the JSON said 5000" is a different fact
/// from "the JSON said \"5000\"". Boxing to object throws that distinction
/// away; a JsonElement can't be stored because its lifetime is tied to the
/// JsonDocument we dispose after parsing. Raw text plus kind survives both.
/// </summary>
public sealed record ConfigValue(JsonValueKind Kind, string Text)
{
    public override string ToString() =>
        Kind == JsonValueKind.String ? $"\"{Text}\"" : Text;
}

/// <summary>Column strategies — the v0 closed set from SPEC section 4.</summary>
public enum ColumnStrategy
{
    /// <summary>SET col = NULL. The column must be nullable; checked against the live schema.</summary>
    Null,

    /// <summary>Fixed value from `value`, type-checked against the column type.</summary>
    Static,

    /// <summary>Same-length replacement: letters to x/X preserving case, digits to 9, punctuation kept.</summary>
    Scramble,

    /// <summary>Explicit "no PII here". Recorded so the diff stays silent about it.</summary>
    Keep,

    /// <summary>
    /// A generated, non-deliverable address per row: fakeemail15@notreal.invalid.
    ///
    /// Exists as its own strategy rather than as a `static` value because the
    /// tool has to OWN the shape. A config-supplied address is opaque to the
    /// code and indistinguishable from a real one, so the verify gate can only
    /// recognise it from a literal list — which cannot work when the value
    /// differs per row. A generated one is recognisable by construction. See
    /// FakeEmail.
    /// </summary>
    Email,
}

/// <summary>
/// Table-level strategies. One value today; it is an enum rather than a bool
/// so that adding a second one later is an additive change everywhere it is
/// switched on, and so the config keyword and the code agree by name.
/// </summary>
public enum TableStrategy
{
    /// <summary>DELETE/TRUNCATE the whole table — audit, logs, queues (DECISIONS.md D5).</summary>
    Truncate,

    /// <summary>
    /// "I looked at this whole table and there is no PII in it." Every column
    /// resolves to Kept in one line, instead of enumerating them all.
    ///
    /// This is the escape hatch for reference and lookup tables, which
    /// otherwise dominate the UNCLASSIFIED list on a large database and train
    /// people to stop reading it — the exact failure the list exists to
    /// prevent. It requires a `reason`, so an exclusion stays a recorded
    /// decision rather than a way to make the report quiet.
    /// </summary>
    Keep,
}

/// <summary>What to do with a temporal table's history table (SPEC 5.2).</summary>
public enum HistoryMode
{
    /// <summary>The default, per DECISIONS.md D5.</summary>
    Truncate,

    /// <summary>Apply the same column strategies to the history table.</summary>
    Mask,
}

/// <summary>How a run reacts to columns with no verdict (SPEC section 4, DECISIONS.md D6).</summary>
public enum UnclassifiedMode
{
    /// <summary>List them loudly and carry on. The v0 default.</summary>
    Warn,

    /// <summary>Exit 3 before mutating anything.</summary>
    Fail,
}
