using System.Globalization;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Masking;

/// <summary>
/// Turns a row's primary key into the short text that makes its masked values
/// different from every other row's (DECISIONS.md D23).
///
/// The key is the seed rather than a counter or a random number, and that choice
/// buys three things:
///
///   * Uniqueness for free. Keys are already unique, so there is no collision
///     retry loop and no chance of two rows landing on the same fake value.
///   * Stability. A developer who bookmarks person 4172 gets the same fake name
///     after every refresh. Random values change on every restore, which is a
///     cost paid daily to save a day of implementation.
///   * Testability. Same key in, same text out, no clock and no RNG state.
/// </summary>
public static class RowDiscriminator
{
    /// <summary>Joins the parts of a composite key. Never appears in a single-column key.</summary>
    public const char CompositeSeparator = '-';

    /// <summary>
    /// The text for one row's key. Invariant culture throughout, so the same
    /// database masks identically on any machine.
    /// </summary>
    public static string For(IReadOnlyList<object> keyValues)
    {
        if (keyValues.Count == 1)
        {
            return Text(keyValues[0]);
        }

        return string.Join(CompositeSeparator, keyValues.Select(Text));
    }

    private static string Text(object value) => value switch
    {
        // The common cases, spelled out so the formatting is obvious rather than
        // whatever ToString happens to do.
        byte b => b.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),

        // A GUID key is 36 characters, which is why the width check below
        // refuses it on anything but a wide column.
        Guid g => g.ToString("N", CultureInfo.InvariantCulture),

        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// The WIDEST text this key could ever produce, computed from the key's
    /// declared types rather than from any row.
    ///
    /// This is what makes the length check a plan-time refusal instead of a
    /// mid-run truncation error. It is derived from the schema, so it needs no
    /// query and it is right even for rows that do not exist yet.
    /// </summary>
    public static int MaxWidth(IReadOnlyList<SchemaColumn> keyColumns)
    {
        if (keyColumns.Count == 0)
        {
            return 0;
        }

        var separators = keyColumns.Count - 1;

        return separators + keyColumns.Sum(c => c.DataType switch
        {
            "tinyint" => 3,                       // 0 to 255
            "smallint" => 6,                      // -32768
            "int" => 11,                          // -2147483648
            "bigint" => 20,                       // -9223372036854775808
            "uniqueidentifier" => 32,             // 32 hex digits, "N" format
            // A textual or otherwise unusual key: its own declared width, or a
            // generous fallback when the type carries no length.
            _ => c.MaxLengthInCharacters ?? 32,
        });
    }

    /// <summary>
    /// The width a SPLICED key costs — the key itself plus the delimiter that
    /// separates it from the scrambled value (DECISIONS.md D28).
    ///
    /// A separate method from <see cref="MaxWidth"/> rather than a `+ 1` at the
    /// call site, because only ONE caller pays for the delimiter. The `email`
    /// strategy embeds the key between a fixed prefix and a fixed domain, so its
    /// boundaries are already unambiguous and it needs no delimiter and no extra
    /// character. Writing the two as one number is how the wrong one gets used.
    /// </summary>
    public static int MaxSplicedWidth(IReadOnlyList<SchemaColumn> keyColumns) =>
        MaxWidth(keyColumns) + 1;

    /// <summary>
    /// Key column types a row key can be SPLICED from (DECISIONS.md D28). The
    /// integral types and nothing else, because the list has to satisfy two
    /// requirements at once and they meet in a narrow place:
    ///
    ///   1. The rendering must never contain <see cref="Scrambler.KeyDelimiter"/>,
    ///      or the split point is in two places and the uniqueness argument
    ///      collapses. A `varchar` key can hold anything, so it fails here.
    ///   2. The rendering must be something the VERIFY gate recognises as a key,
    ///      which is digits and the composite separator. A `uniqueidentifier`
    ///      renders as hex, whose a–f are letters; a `decimal` brings a '.'; a
    ///      `datetime` brings ':' and 'T'. Each would satisfy (1) and then
    ///      produce masked values the gate reports as leaks — a config accepted
    ///      at plan time that cannot pass verify, which is the exact
    ///      accepted-then-explodes shape this pass exists to remove.
    ///
    /// Widening (2) instead was the alternative, and it was rejected: it means
    /// teaching the gate to excuse letters, and a rule that excuses more is the
    /// dangerous direction (DECISIONS.md D17).
    ///
    /// An ALLOWLIST, not a list of banned types. For a type nobody thought about
    /// the honest answer is no, and being wrongly left out costs a config edit —
    /// with `email` still available, which needs neither property because its key
    /// sits between a fixed prefix and a fixed domain.
    /// </summary>
    private static readonly string[] SpliceableKeyTypes =
        ["tinyint", "smallint", "int", "bigint"];

    /// <summary>
    /// Whether a row key built from these columns can be spliced onto a masked
    /// value — true only when EVERY key column qualifies, since one that does not
    /// is enough to break the split.
    /// </summary>
    public static bool CanSplice(IReadOnlyList<SchemaColumn> keyColumns) =>
        keyColumns.Count > 0 && keyColumns.All(c =>
            SpliceableKeyTypes.Contains(c.DataType, StringComparer.OrdinalIgnoreCase));

    /// <summary>The key columns that are the reason <see cref="CanSplice"/> said no.</summary>
    public static IReadOnlyList<SchemaColumn> UnspliceableKeyColumns(IReadOnlyList<SchemaColumn> keyColumns) =>
        keyColumns
            .Where(c => !SpliceableKeyTypes.Contains(c.DataType, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Key column types that arrive from SQL Server as BYTES rather than as a
    /// scalar, and so render as the name of a CLR type instead of as the row's
    /// value (DECISIONS.md D28).
    ///
    /// <see cref="Text"/> formats the types it names and falls through to
    /// Convert.ToString for the rest. That fallback is right for almost
    /// everything — a decimal, a date and a varchar all render as themselves —
    /// but Convert.ToString returns `value.ToString()` for anything that is not
    /// IConvertible, and a byte array does not override ToString. The result is
    /// the literal text `System.Byte[]`, IDENTICAL for every row.
    ///
    /// That is worse than it sounds. A constant discriminator makes every
    /// generated address the same, which breaks the one promise `email` makes,
    /// and the verify gate does not catch it: `fakeemailSystem.Byte[]@notreal.
    /// invalid` fails the email pattern (the character before the '@' is ']',
    /// which no local part may contain), so it is never even a candidate. The
    /// run passes verify and earns a stamp with every row holding one broken
    /// address.
    ///
    /// A DENYLIST here, where <see cref="SpliceableKeyTypes"/> is an allowlist,
    /// because the questions differ. Splicing needs a proof about the text's
    /// alphabet, and few types can give one. This needs only to exclude the
    /// types that do not render a value at all, and they can be named — so
    /// `email` keeps working with the varchar and GUID keys that suit it.
    /// </summary>
    private static readonly string[] UnrenderableKeyTypes =
    [
        "binary", "varbinary", "image", "timestamp", "rowversion",

        // CLR user-defined types. dbscrub does not load their assemblies, so the
        // driver hands these back as bytes too.
        "hierarchyid", "geography", "geometry",
    ];

    /// <summary>
    /// Whether a key built from these columns renders as the row's VALUE — the
    /// minimum any per-row strategy needs, since a key that renders the same for
    /// every row identifies none of them.
    /// </summary>
    public static bool CanRender(IReadOnlyList<SchemaColumn> keyColumns) =>
        keyColumns.Count > 0 && keyColumns.All(c =>
            !UnrenderableKeyTypes.Contains(c.DataType, StringComparer.OrdinalIgnoreCase));

    /// <summary>The key columns that are the reason <see cref="CanRender"/> said no.</summary>
    public static IReadOnlyList<SchemaColumn> UnrenderableKeyColumns(IReadOnlyList<SchemaColumn> keyColumns) =>
        keyColumns
            .Where(c => UnrenderableKeyTypes.Contains(c.DataType, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
