using DbScrub.Core.Hygiene;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Verify;

/// <summary>Runs the verify sweep. An interface so `clean` can be tested without a server.</summary>
public interface IVerifier
{
    Task<VerifyReport> VerifyAsync(DatabaseSchema schema, CancellationToken cancellationToken = default);
}

/// <summary>
/// The verify gate (SPEC 5.4): sweep EVERY string column in the database for
/// values that still look like personal data.
///
/// Every string column, not only the masked ones. That is the point — a column
/// nobody configured is the most likely place for something to have been missed,
/// and a gate that only checks the work you remembered to do cannot catch the
/// work you forgot.
///
/// Two passes per column, because the fast case has to be fast:
///
///   1. A single `COUNT_BIG` with every pattern OR-ed together. One scan, no
///      data crosses the wire, and on a correctly scrubbed database it returns
///      zero for almost every column — which ends that column's work.
///   2. Only where the count is non-zero, stream the matching values and judge
///      them properly in C#: which pattern, and is it actually a leak or just
///      this tool's own output (DECISIONS.md D17).
///
/// Never prints, logs, or returns a value. See VerifyReport.
/// </summary>
public sealed class SqlVerifier(string connectionString) : IVerifier
{
    /// <summary>
    /// Verification reads the whole database. On a large one that is minutes,
    /// and a timeout would fail a run that had already masked successfully.
    /// </summary>
    private const int NoTimeout = 0;

    public async Task<VerifyReport> VerifyAsync(
        DatabaseSchema schema,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var hits = new List<VerifyHit>();
        var columnsScanned = 0;
        long rowsInspected = 0;

        foreach (var table in schema.Tables)
        {
            // The tool's own bookkeeping table is exempt (SPEC section 4). It
            // holds config hashes and timestamps, never row data.
            if (table.QualifiedName.Equals(
                    VerdictResolver.SanitizationLogTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var column in table.Columns.Where(c => c.IsTextual))
            {
                columnsScanned++;
                rowsInspected += await ScanColumnAsync(connection, table, column, hits, cancellationToken);
            }
        }

        return new VerifyReport(hits, columnsScanned, rowsInspected);
    }

    /// <summary>Returns how many candidate values were inspected in C#.</summary>
    private static async Task<long> ScanColumnAsync(
        SqlConnection connection,
        SchemaTable table,
        SchemaColumn column,
        List<VerifyHit> hits,
        CancellationToken cancellationToken)
    {
        var candidates = await CountCandidatesAsync(connection, table, column, cancellationToken);

        if (candidates == 0)
        {
            // The overwhelmingly common case on a scrubbed database, and the
            // reason the sweep is affordable at all.
            return 0;
        }

        // Attribute the candidates to patterns, and discount this tool's own
        // output. Counted per pattern so the report can say WHICH shape survived
        // — that is usually enough to identify the config gap without anyone
        // looking at a value.
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long inspected = 0;

        await using (var command = new SqlCommand(SelectCandidatesSql(table, column), connection)
        {
            CommandTimeout = NoTimeout,
        })
        {
            BindPatterns(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var value = reader.GetString(0);
                inspected++;

                // Masked output first: a scrambled SSN matches the SSN pattern
                // exactly, and treating it as a hit would mean no correctly
                // scrubbed database could ever be stamped.
                if (PlaceholderRules.IsMaskedOutput(value))
                {
                    continue;
                }

                foreach (var pattern in VerifyPattern.All)
                {
                    if (pattern.Matches(value))
                    {
                        counts[pattern.Name] = counts.GetValueOrDefault(pattern.Name) + 1;
                    }
                }
            }
        }

        foreach (var (pattern, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            hits.Add(new VerifyHit(table.Schema, table.Name, column.Name, pattern, count));
        }

        return inspected;
    }

    private static async Task<long> CountCandidatesAsync(
        SqlConnection connection,
        SchemaTable table,
        SchemaColumn column,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CountCandidatesSql(table, column), connection)
        {
            CommandTimeout = NoTimeout,
        };

        BindPatterns(command);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long count ? count : 0;
    }

    // ---- SQL ---------------------------------------------------------------

    /// <summary>
    /// The OR-ed pre-filter. Every pattern in one predicate so a column costs
    /// ONE scan rather than one per pattern — the difference between a sweep
    /// that runs and a sweep people turn off.
    /// </summary>
    internal static string WhereAnyPattern() =>
        string.Join(" OR ", VerifyPattern.All.Select((_, i) => $"c LIKE @p{i}"));

    internal static string CountCandidatesSql(SchemaTable table, SchemaColumn column) =>
        $"SELECT COUNT_BIG(*) FROM {Quote(table)} AS t "
        + $"CROSS APPLY (SELECT CAST({Column(column)} AS nvarchar(max)) AS c) AS v "
        + $"WHERE {WhereAnyPattern()};";

    internal static string SelectCandidatesSql(SchemaTable table, SchemaColumn column) =>
        $"SELECT v.c FROM {Quote(table)} AS t "
        + $"CROSS APPLY (SELECT CAST({Column(column)} AS nvarchar(max)) AS c) AS v "
        + $"WHERE {WhereAnyPattern()};";

    /// <summary>
    /// CAST to nvarchar(max) so `text` and `ntext` work too. Those deprecated
    /// types reject most operators, and they are exactly where a legacy schema
    /// hides a free-text notes column — the last place to skip.
    /// </summary>
    private static string Column(SchemaColumn column) => $"t.{SqlIdentifier.Quote(column.Name)}";

    private static string Quote(SchemaTable table) => SqlIdentifier.Quote(table.Schema, table.Name);

    private static void BindPatterns(SqlCommand command)
    {
        for (var i = 0; i < VerifyPattern.All.Count; i++)
        {
            command.Parameters.AddWithValue($"@p{i}", VerifyPattern.All[i].LikePattern);
        }
    }
}
