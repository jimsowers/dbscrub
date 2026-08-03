namespace DbScrub.Core.Hygiene;

/// <summary>
/// Quotes SQL Server identifiers. CLAUDE.md requires this because table and
/// column names come from the config file, which is user input, and identifiers
/// cannot be parameterized — `DELETE FROM @table` is not a thing SQL Server
/// will run.
///
/// This is the ONE place identifier text becomes SQL. Everything that builds a
/// statement goes through it, so there is a single line to audit rather than a
/// habit to maintain.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Wraps a name in brackets, doubling any closing bracket inside it — the
    /// same rule QUOTENAME uses.
    ///
    /// The doubling is what makes this safe. A table named
    /// <c>Person] ; DROP DATABASE Foo --</c> becomes
    /// <c>[Person]] ; DROP DATABASE Foo --]</c>, which SQL Server reads as one
    /// absurd but harmless identifier rather than as a statement boundary.
    /// </summary>
    public static string Quote(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // SQL Server identifiers cap at 128 characters. A longer one cannot be
        // a real object, so it is a config error or an attack; either way it
        // must not reach the server.
        if (name.Length > 128)
        {
            throw new ArgumentException(
                $"Identifier is {name.Length} characters; SQL Server allows at most 128.", nameof(name));
        }

        if (name.Contains('\0'))
        {
            // A NUL can truncate the string in native layers below us.
            throw new ArgumentException("Identifier contains a NUL character.", nameof(name));
        }

        return $"[{name.Replace("]", "]]")}]";
    }

    /// <summary>Quotes a schema-qualified name: dbo.Person -> [dbo].[Person].</summary>
    public static string Quote(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
}
