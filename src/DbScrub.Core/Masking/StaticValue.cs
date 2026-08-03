using System.Globalization;
using System.Text.Json;
using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Masking;

/// <summary>
/// Turns a config's `"value"` into the CLR value bound to a parameter, and
/// refuses the ones the column cannot hold (SPEC section 4: "type-checked
/// against column type").
///
/// This check was deliberately deferred out of config loading, because it needs
/// the live column and config loading has never seen the database
/// (DECISIONS.md D12). It lands here rather than in the executor for a reason
/// worth stating: a type mismatch caught at PLAN time stops a run before
/// anything is modified, while the same mismatch caught at execution time stops
/// it halfway through, leaving a database that is neither raw nor clean.
///
/// The conversion is deliberately strict. A config that says
/// <c>"value": "42"</c> for an int column is not silently accepted as the
/// number 42 — the quotes were a decision the author made, and quietly
/// reinterpreting them is how a config comes to mean something other than what
/// it says.
/// </summary>
public static class StaticValue
{
    /// <summary>
    /// Converts, or explains why it cannot. <paramref name="converted"/> is the
    /// object to bind as a parameter; null is never a success here, because
    /// writing NULL is the `null` strategy's job, not `static`'s.
    /// </summary>
    public static bool TryConvert(
        ConfigValue value,
        SchemaColumn column,
        out object? converted,
        out string? problem,
        out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        return column.DataType switch
        {
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" =>
                TryText(value, column, out converted, out problem, out suggestion),

            "tinyint" or "smallint" or "int" or "bigint" =>
                TryInteger(value, column, out converted, out problem, out suggestion),

            "decimal" or "numeric" or "money" or "smallmoney" =>
                TryDecimal(value, column, out converted, out problem, out suggestion),

            "float" or "real" =>
                TryFloat(value, column, out converted, out problem, out suggestion),

            "bit" =>
                TryBit(value, column, out converted, out problem, out suggestion),

            "date" or "datetime" or "datetime2" or "smalldatetime" =>
                TryDateTime(value, column, out converted, out problem, out suggestion),

            "datetimeoffset" =>
                TryDateTimeOffset(value, column, out converted, out problem, out suggestion),

            "time" =>
                TryTime(value, column, out converted, out problem, out suggestion),

            "uniqueidentifier" =>
                TryGuid(value, column, out converted, out problem, out suggestion),

            // Everything else: binary, varbinary, image, xml, sql_variant,
            // geography, hierarchyid, rowversion, and any alias or CLR type.
            // Rather than guess at a conversion, say so. A column of one of
            // these types holding PII is a real possibility (xml especially),
            // and the honest answers for it are `null` or truncating the table.
            _ => Unsupported(column, out problem, out suggestion),
        };
    }

    // ---- per-type conversions ---------------------------------------------

    private static bool TryText(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (value.Kind != JsonValueKind.String)
        {
            problem = $"is {column.DataType}, but the value is {Describe(value.Kind)}.";
            suggestion = $"Quote it: \"value\": \"{value.Text}\".";
            return false;
        }

        var limit = column.MaxLengthInCharacters;

        if (limit is { } max && value.Text.Length > max)
        {
            // Left to SQL Server this is error 8152 (String or binary data would
            // be truncated) partway through the run, on whichever row happened
            // to be first. Catching it here costs one comparison.
            problem = $"is {column.DataType}({max}), but the value is {value.Text.Length} characters long.";
            suggestion = $"Shorten it to {max} characters or fewer.";
            return false;
        }

        converted = value.Text;
        return true;
    }

    private static bool TryInteger(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireNumber(value, column, ref problem, ref suggestion))
        {
            return false;
        }

        if (!long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            problem = $"is {column.DataType}, but {value.Text} is not a whole number.";
            suggestion = "Integer columns take integers — drop the decimal point.";
            return false;
        }

        var (min, max) = Range(column.DataType);

        if (parsed < min || parsed > max)
        {
            problem = $"is {column.DataType}, which holds {min} to {max}, but the value is {parsed}.";
            suggestion = $"Choose a value in range, or use a wider column type.";
            return false;
        }

        // Narrowed to the column's own CLR width so the parameter SqlClient
        // infers matches the column instead of forcing a conversion.
        //
        // The (object) casts are load-bearing, not noise. Without them the
        // switch arms have no common type, so C# picks the one they all convert
        // to — long — and every arm is widened back before boxing. The narrowing
        // silently does not happen, which is exactly what a test caught.
        converted = column.DataType switch
        {
            "tinyint" => (object)(byte)parsed,
            "smallint" => (object)(short)parsed,
            "int" => (object)(int)parsed,
            _ => parsed,
        };

        return true;
    }

    private static bool TryDecimal(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireNumber(value, column, ref problem, ref suggestion))
        {
            return false;
        }

        if (!decimal.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            problem = $"is {column.DataType}, but {value.Text} is not a number it can hold.";
            suggestion = "Use a plain decimal number, e.g. 0 or 12.34.";
            return false;
        }

        converted = parsed;
        return true;
    }

    private static bool TryFloat(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireNumber(value, column, ref problem, ref suggestion))
        {
            return false;
        }

        if (!double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            problem = $"is {column.DataType}, but {value.Text} is not a number it can hold.";
            suggestion = "Use a plain number, e.g. 0 or 12.34.";
            return false;
        }

        // (object) for the same reason as the integer switch above: a conditional
        // whose branches are float and double unifies to double, undoing the
        // narrowing before it is boxed.
        converted = column.DataType == "real" ? (object)(float)parsed : parsed;
        return true;
    }

    private static bool TryBit(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        // JSON true/false is the natural spelling, and 0/1 is accepted because
        // that is how a bit column reads in every query result the author has
        // ever seen. A quoted "true" is refused — see the class comment.
        switch (value)
        {
            case { Kind: JsonValueKind.True }:
                converted = true;
                return true;

            case { Kind: JsonValueKind.False }:
                converted = false;
                return true;

            case { Kind: JsonValueKind.Number, Text: "0" }:
                converted = false;
                return true;

            case { Kind: JsonValueKind.Number, Text: "1" }:
                converted = true;
                return true;

            default:
                problem = $"is bit, but the value is {Describe(value.Kind)}.";
                suggestion = "Use true, false, 0, or 1 — unquoted.";
                return false;
        }
    }

    private static bool TryDateTime(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireString(value, column, "a date", ref problem, ref suggestion))
        {
            return false;
        }

        // InvariantCulture on purpose: a config file is read on whatever machine
        // runs the tool, and "03/04/2020" must not mean March on one box and
        // April on another. RoundtripKind keeps an offset-bearing value from
        // being silently shifted into local time.
        if (!DateTime.TryParse(value.Text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            problem = $"is {column.DataType}, but \"{value.Text}\" is not a date this tool can read.";
            suggestion = "Use ISO 8601, e.g. \"1970-01-01\" or \"1970-01-01T00:00:00\".";
            return false;
        }

        converted = parsed;
        return true;
    }

    private static bool TryDateTimeOffset(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireString(value, column, "a date", ref problem, ref suggestion))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(value.Text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            problem = $"is datetimeoffset, but \"{value.Text}\" is not a date this tool can read.";
            suggestion = "Use ISO 8601 with an offset, e.g. \"1970-01-01T00:00:00+00:00\".";
            return false;
        }

        converted = parsed;
        return true;
    }

    private static bool TryTime(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireString(value, column, "a time", ref problem, ref suggestion))
        {
            return false;
        }

        if (!TimeSpan.TryParse(value.Text, CultureInfo.InvariantCulture, out var parsed))
        {
            problem = $"is time, but \"{value.Text}\" is not a time of day this tool can read.";
            suggestion = "Use \"HH:mm:ss\", e.g. \"00:00:00\".";
            return false;
        }

        converted = parsed;
        return true;
    }

    private static bool TryGuid(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion)
    {
        converted = null;
        problem = null;
        suggestion = null;

        if (!RequireString(value, column, "a GUID", ref problem, ref suggestion))
        {
            return false;
        }

        if (!Guid.TryParse(value.Text, out var parsed))
        {
            problem = $"is uniqueidentifier, but \"{value.Text}\" is not a GUID.";
            suggestion = "Use the 36-character form, e.g. \"00000000-0000-0000-0000-000000000000\".";
            return false;
        }

        converted = parsed;
        return true;
    }

    // ---- shared checks -----------------------------------------------------

    private static bool RequireNumber(
        ConfigValue value, SchemaColumn column, ref string? problem, ref string? suggestion)
    {
        if (value.Kind == JsonValueKind.Number)
        {
            return true;
        }

        problem = $"is {column.DataType}, but the value is {Describe(value.Kind)}.";
        suggestion = value.Kind == JsonValueKind.String
            ? $"Drop the quotes: \"value\": {value.Text}."
            : "Use a number.";

        return false;
    }

    private static bool RequireString(
        ConfigValue value, SchemaColumn column, string what, ref string? problem, ref string? suggestion)
    {
        if (value.Kind == JsonValueKind.String)
        {
            return true;
        }

        problem = $"is {column.DataType}, but the value is {Describe(value.Kind)}.";
        suggestion = $"Write {what} as a quoted string.";

        return false;
    }

    private static bool Unsupported(SchemaColumn column, out string? problem, out string? suggestion)
    {
        problem = $"is {column.DataType}, which \"static\" cannot write.";
        suggestion = column.IsNullable
            ? "Use \"null\" to clear it, or truncate the table if the whole row is disposable."
            : "The column is NOT NULL, so \"null\" is out too — truncate the table, or ask whether this "
                + "column really needs masking.";

        return false;
    }

    private static (long Min, long Max) Range(string dataType) => dataType switch
    {
        // tinyint is UNSIGNED in SQL Server — 0 to 255, not -128 to 127. That
        // difference from every C-family language is worth stating rather than
        // remembering.
        "tinyint" => (byte.MinValue, byte.MaxValue),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue),
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "not a value it can hold",
    };
}
