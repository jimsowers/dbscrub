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
}
