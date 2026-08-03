using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Execution;

/// <summary>
/// The real <see cref="ICleanSession"/> — the only type in this repository that
/// sends a modifying statement to SQL Server.
///
/// One connection for the whole run. That is not an optimization: the hygiene
/// pass turns system versioning off and the mask engine then writes to tables
/// that are relying on it staying off, and reconnecting between those would
/// leave a window where the state the run depends on is not visibly held by
/// anyone.
///
/// Every statement it sends comes from <see cref="HygienePlanner"/> or
/// <see cref="MaskSql"/> — this class composes no SQL of its own. It binds
/// parameters and manages transactions, which is exactly the part that cannot be
/// asserted in a string comparison.
/// </summary>
public sealed class SqlCleanSession(string connectionString) : ICleanSession
{
    /// <summary>
    /// No command timeout. A masking run legitimately takes minutes, and the
    /// alternative to waiting is a timeout partway through — which does not undo
    /// the batches that already committed, so it produces a half-masked database
    /// and a stack trace instead of an answer. The operator has Ctrl-C, and
    /// cancellation is honored between batches.
    /// </summary>
    private const int NoTimeout = 0;

    private SqlConnection? _connection;

    public async Task ExecuteAsync(HygieneStep step, CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(cancellationToken);

        // No explicit transaction. These are DDL and sp_cdc_disable_db, which
        // either do not belong in a user transaction or gain nothing from one —
        // each is already atomic, and each is idempotent enough to re-run.
        await using var command = new SqlCommand(step.Sql, connection) { CommandTimeout = NoTimeout };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MaskResult> MaskTableAsync(
        TableMaskPlan plan,
        int batchSize,
        IProgress<long>? rowsDone = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(cancellationToken);

        // Taken BEFORE any rewriting, so it is the number of rows the walk is
        // expected to visit. Compared against the total at the end; see
        // MaskResult.RowsInTable for why that comparison is worth a query.
        var rowsInTable = await CountRowsAsync(connection, plan, cancellationToken);

        var updated = plan.Mode switch
        {
            MaskMode.WholeTable =>
                await MaskWholeTableAsync(connection, plan, cancellationToken),

            MaskMode.BatchedConstant =>
                await MaskBatchedConstantAsync(connection, plan, batchSize, rowsDone, cancellationToken),

            _ => await MaskRowByRowAsync(connection, plan, batchSize, rowsDone, cancellationToken),
        };

        return new MaskResult(plan.QualifiedName, rowsInTable, updated);
    }

    // ---- the three modes ---------------------------------------------------

    /// <summary>
    /// One statement, one transaction, no key (SPEC 5.3's keyless fallback).
    /// Every replacement is a constant, which is the only reason this is
    /// possible without addressing rows individually.
    /// </summary>
    private static async Task<long> MaskWholeTableAsync(
        SqlConnection connection,
        TableMaskPlan plan,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(MaskSql.UpdateWholeTable(plan), connection, transaction)
        {
            CommandTimeout = NoTimeout,
        };

        BindConstants(command, plan);

        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return updated;
    }

    /// <summary>
    /// Walks the key, rewriting each range with one set-based UPDATE. Reads only
    /// key values, because nothing about the new values depends on the old ones.
    /// The walk exists purely to keep any one transaction bounded.
    /// </summary>
    private static async Task<long> MaskBatchedConstantAsync(
        SqlConnection connection,
        TableMaskPlan plan,
        int batchSize,
        IProgress<long>? rowsDone,
        CancellationToken cancellationToken)
    {
        object[]? lowerBound = null;
        long total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isFirstBatch = lowerBound is null;
            var keys = await ReadKeyBatchAsync(
                connection, plan, batchSize, lowerBound, isFirstBatch, cancellationToken);

            if (keys.Count == 0)
            {
                break;
            }

            // The last key this batch saw becomes the range's inclusive upper
            // bound, so the UPDATE touches exactly the rows the SELECT listed.
            var upperBound = keys[^1];

            await using (var transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                await using var command = new SqlCommand(
                    MaskSql.UpdateConstantRange(plan, isFirstBatch), connection, transaction)
                {
                    CommandTimeout = NoTimeout,
                };

                BindConstants(command, plan);
                BindBound(command, lowerBound, MaskSql.LowerBoundParameter);
                BindBound(command, upperBound, MaskSql.UpperBoundParameter);

                total += await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            rowsDone?.Report(total);
            lowerBound = upperBound;

            // A short batch means the key walk reached the end of the table.
            if (keys.Count < batchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Reads a batch in key order, computes each new value in memory, writes the
    /// batch back addressed row by row. The only mode that can run `scramble`,
    /// because a scrambled value is a function of the value being replaced and
    /// no T-SQL expression reproduces it (DECISIONS.md D19).
    /// </summary>
    private static async Task<long> MaskRowByRowAsync(
        SqlConnection connection,
        TableMaskPlan plan,
        int batchSize,
        IProgress<long>? rowsDone,
        CancellationToken cancellationToken)
    {
        // The config's batchSize is an upper bound here, not the answer: each row
        // spends parameters, and SQL Server refuses a command carrying more than
        // 2100 of them.
        var rowsPerCommand = MaskSql.RowsPerCommand(plan, batchSize);

        var keyCount = plan.KeyColumns.Count;

        // Two different lists, and conflating them is the bug waiting here.
        // ReadColumns is what the SELECT returns; ComputedColumns is what the
        // UPDATE writes. An `email` column is in the second and not the first,
        // because its new value comes from the key rather than the old value.
        var readColumns = plan.ReadColumns;
        var computed = plan.ComputedColumns;

        object[]? lowerBound = null;
        long total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isFirstBatch = lowerBound is null;

            var rows = new List<(object[] Key, object?[] Values)>();

            await using (var select = new SqlCommand(
                MaskSql.SelectBatch(plan, isFirstBatch), connection) { CommandTimeout = NoTimeout })
            {
                select.Parameters.AddWithValue(MaskSql.BatchSizeParameter, rowsPerCommand);
                BindBound(select, lowerBound, MaskSql.LowerBoundParameter);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var key = new object[keyCount];
                    for (var i = 0; i < keyCount; i++)
                    {
                        key[i] = reader.GetValue(i);
                    }

                    var discriminator = RowDiscriminator.For(key);

                    var values = new object?[computed.Count];
                    var readOrdinal = 0;

                    for (var i = 0; i < computed.Count; i++)
                    {
                        var column = computed[i];

                        // Read columns follow the key columns in the SELECT, in
                        // order; anything else is built from the key alone.
                        values[i] = column.NeedsCurrentValue
                            ? Transform(column, reader, keyCount + readOrdinal++, discriminator)
                            : Build(column, discriminator);
                    }

                    rows.Add((key, values));
                }
            }

            if (rows.Count == 0)
            {
                break;
            }

            await using (var transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                await using var update = new SqlCommand(
                    MaskSql.UpdateRows(plan, rows.Count), connection, transaction)
                {
                    CommandTimeout = NoTimeout,
                };

                BindConstants(update, plan);

                for (var row = 0; row < rows.Count; row++)
                {
                    for (var i = 0; i < keyCount; i++)
                    {
                        update.Parameters.AddWithValue(MaskSql.RowKeyParameter(row, i), rows[row].Key[i]);
                    }

                    for (var i = 0; i < computed.Count; i++)
                    {
                        update.Parameters.AddWithValue(
                            MaskSql.RowValueParameter(row, i), rows[row].Values[i] ?? DBNull.Value);
                    }
                }

                total += await update.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            rowsDone?.Report(total);
            lowerBound = rows[^1].Key;

            if (rows.Count < rowsPerCommand)
            {
                break;
            }
        }

        return total;
    }

    // ---- shared plumbing ---------------------------------------------------

    /// <summary>
    /// Applies one column's strategy to one row's current value. The only
    /// strategy that reaches here is `scramble` — the constant ones never need
    /// the old value and are bound once per command.
    /// </summary>
    private static object? Transform(
        MaskColumn column, SqlDataReader reader, int ordinal, string discriminator)
    {
        if (reader.IsDBNull(ordinal))
        {
            // NULL stays NULL. A null is already the absence of data, and
            // inventing a value for it would change what the row means.
            return null;
        }

        var current = reader.GetString(ordinal);

        return column.Strategy switch
        {
            ColumnStrategy.Scramble when column.Unique == UniqueMode.Key =>
                Scrambler.ScrambleUnique(current, discriminator),

            ColumnStrategy.Scramble => Scrambler.Scramble(current),

            // Unreachable: nothing else needs the current value.
            _ => throw new InvalidOperationException(
                $"{column.Name} has strategy {column.Strategy}, which is not computed from the row."),
        };
    }

    /// <summary>
    /// The new value for a column built from the row's KEY rather than from its
    /// current value — so nothing about the old value is read, or needs to be.
    /// </summary>
    private static object Build(MaskColumn column, string discriminator) => column.Strategy switch
    {
        ColumnStrategy.Email => FakeEmail.For(discriminator),

        _ => throw new InvalidOperationException(
            $"{column.Name} has strategy {column.Strategy}, which is not built from the row key."),
    };

    /// <summary>
    /// Binds the replacement values that are the same on every row. Numbered
    /// across the `static` columns only, in order — exactly how
    /// <see cref="MaskSql"/> numbered them, which is why both live off the same
    /// ConstantColumns sequence.
    /// </summary>
    private static void BindConstants(SqlCommand command, TableMaskPlan plan)
    {
        var ordinal = 0;

        foreach (var column in plan.ConstantColumns)
        {
            if (column.Strategy != ColumnStrategy.Static)
            {
                // `null` is written as the SQL keyword, so it has no parameter.
                continue;
            }

            command.Parameters.AddWithValue(
                MaskSql.ConstantParameter(ordinal), column.Value ?? (object)DBNull.Value);
            ordinal++;
        }
    }

    private static void BindBound(SqlCommand command, object[]? bound, Func<int, string> parameter)
    {
        if (bound is null)
        {
            return;
        }

        for (var i = 0; i < bound.Length; i++)
        {
            command.Parameters.AddWithValue(parameter(i), bound[i]);
        }
    }

    private static async Task<List<object[]>> ReadKeyBatchAsync(
        SqlConnection connection,
        TableMaskPlan plan,
        int batchSize,
        object[]? lowerBound,
        bool isFirstBatch,
        CancellationToken cancellationToken)
    {
        var keyCount = plan.KeyColumns.Count;
        var keys = new List<object[]>();

        await using var command = new SqlCommand(
            MaskSql.SelectKeyBatch(plan, isFirstBatch), connection) { CommandTimeout = NoTimeout };

        command.Parameters.AddWithValue(MaskSql.BatchSizeParameter, batchSize);
        BindBound(command, lowerBound, MaskSql.LowerBoundParameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new object[keyCount];
            for (var i = 0; i < keyCount; i++)
            {
                key[i] = reader.GetValue(i);
            }

            keys.Add(key);
        }

        return keys;
    }

    private static async Task<long> CountRowsAsync(
        SqlConnection connection,
        TableMaskPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(MaskSql.CountRows(plan.Table), connection)
        {
            CommandTimeout = NoTimeout,
        };

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long count ? count : 0;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        _connection = connection;

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
