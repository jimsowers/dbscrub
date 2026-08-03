using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Stamp;

/// <summary>Reads the sanitization stamp. Behind an interface so `status` tests need no server.</summary>
public interface IStampReader
{
    Task<SanitizationStatus> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the database-level extended properties written by `clean`
/// (SPEC section 5.5).
///
/// Database-level extended properties are `class = 0` — the classes above that
/// are for schemas, tables, columns and so on. Reading them needs no elevated
/// rights, which matters because SPEC section 8's Guard has to make this same
/// check using the consuming application's own low-privilege login.
///
/// Strictly read-only. This type contains no UPDATE, DELETE, or DDL and must
/// stay that way — it is the code the Guard will eventually share.
/// </summary>
public sealed class StampReader(string connectionString) : IStampReader
{
    private const string Sql = """
        SELECT ep.name,
               CAST(ep.value AS nvarchar(4000)) AS PropertyValue
        FROM sys.extended_properties AS ep
        WHERE ep.class = 0            -- 0 = the database itself
          AND ep.major_id = 0
          AND ep.minor_id = 0
          AND ep.name IN (@sanitized, @sanitizedUtc, @configHash, @toolVersion);
        """;

    public async Task<SanitizationStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(Sql, connection);

        // Parameterized even though these are our own constants: the habit is
        // the point, and CLAUDE.md allows no exceptions to it.
        command.Parameters.AddWithValue("@sanitized", SanitizationStatus.PropertyNames.Sanitized);
        command.Parameters.AddWithValue("@sanitizedUtc", SanitizationStatus.PropertyNames.SanitizedUtc);
        command.Parameters.AddWithValue("@configHash", SanitizationStatus.PropertyNames.ConfigHash);
        command.Parameters.AddWithValue("@toolVersion", SanitizationStatus.PropertyNames.ToolVersion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return Interpret(values);
    }

    /// <summary>
    /// Turns the raw property bag into a status. Separated out so the parsing
    /// rules — especially the fail-safe one below — are unit-testable.
    /// </summary>
    internal static SanitizationStatus Interpret(IReadOnlyDictionary<string, string?> values)
    {
        if (!values.TryGetValue(SanitizationStatus.PropertyNames.Sanitized, out var sanitized))
        {
            return SanitizationStatus.NotSanitized;
        }

        // Fail safe: only an explicit, recognizable "true" counts as sanitized.
        // Anything else — a stray property, a half-written value, someone
        // setting it by hand to the wrong thing — reads as NOT sanitized. The
        // dangerous mistake is calling a dirty database clean, never the
        // reverse.
        if (!IsTrue(sanitized))
        {
            return SanitizationStatus.NotSanitized;
        }

        return new SanitizationStatus(
            IsSanitized: true,
            SanitizedUtc: ParseUtc(Get(values, SanitizationStatus.PropertyNames.SanitizedUtc)),
            ConfigHash: Get(values, SanitizationStatus.PropertyNames.ConfigHash),
            ToolVersion: Get(values, SanitizationStatus.PropertyNames.ToolVersion));
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private static bool IsTrue(string? value) =>
        value is not null
        && (string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || value.Trim() == "1");

    private static DateTime? ParseUtc(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // The writer stores UTC, so AssumeUniversal covers a value that lost its
        // Z, and AdjustToUniversal normalizes one that kept it. (RoundtripKind
        // cannot be combined with either — it throws.)
        //
        // A stamp we cannot parse is reported as a stamp with an unknown date
        // rather than as no stamp at all: the Sanitized flag is the
        // load-bearing part, and downgrading the whole thing over a bad
        // timestamp would call a clean database dirty.
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
