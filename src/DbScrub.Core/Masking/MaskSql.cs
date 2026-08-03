using System.Text;
using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Masking;

/// <summary>
/// Generates every statement the mask engine runs (SPEC 5.3). Pure string
/// functions with no database and no state, for the same reason
/// <see cref="HygienePlanner"/> is: the destructive SQL is then something a
/// person can read in a test file rather than something that only exists at
/// runtime.
///
/// Two conventions hold throughout:
///
///   * Every identifier goes through <see cref="SqlIdentifier"/>. Table and
///     column names come from the config, which is user input (CLAUDE.md).
///   * Every VALUE is a parameter, never inlined text. The one exception is the
///     `null` strategy, which emits the SQL keyword NULL — a keyword is not a
///     value and cannot carry one.
///
/// The parameter NAMES are part of the contract between this class and the
/// executor that binds them, so they are produced by the methods below rather
/// than spelled out in two places.
/// </summary>
public static class MaskSql
{
    /// <summary>How many rows the current batch should fetch.</summary>
    public const string BatchSizeParameter = "@batchSize";

    /// <summary>
    /// SQL Server refuses any single command carrying more than 2100
    /// parameters. Row-by-row batches spend (key columns + masked columns)
    /// parameters per row, so this is the real ceiling on how many rows fit in
    /// one round trip — the config's batchSize is capped by it, not the other
    /// way round.
    ///
    /// 2000 rather than 2100: the margin costs nothing and means a future
    /// statement that adds a parameter of its own cannot silently push a
    /// working config over the edge.
    /// </summary>
    public const int MaxParametersPerCommand = 2000;

    /// <summary>The lower-bound key parameter for key column <paramref name="ordinal"/>.</summary>
    public static string LowerBoundParameter(int ordinal) => $"@lo{ordinal}";

    /// <summary>The upper-bound key parameter for key column <paramref name="ordinal"/>.</summary>
    public static string UpperBoundParameter(int ordinal) => $"@hi{ordinal}";

    /// <summary>The replacement-value parameter for a constant column at <paramref name="ordinal"/>.</summary>
    public static string ConstantParameter(int ordinal) => $"@c{ordinal}";

    /// <summary>The new value for column <paramref name="ordinal"/> of row <paramref name="row"/>.</summary>
    public static string RowValueParameter(int row, int ordinal) => $"@v{row}_{ordinal}";

    /// <summary>The key value for key column <paramref name="ordinal"/> of row <paramref name="row"/>.</summary>
    public static string RowKeyParameter(int row, int ordinal) => $"@k{row}_{ordinal}";

    /// <summary>How many rows fit in one command for this table, given the parameter ceiling.</summary>
    public static int RowsPerCommand(TableMaskPlan plan, int batchSize)
    {
        // Each row spends one parameter per key column plus one per column whose
        // value is computed. Constant columns are bound once for the whole
        // command, so they do not scale with the row count.
        var perRow = plan.KeyColumns.Count + plan.ComputedColumns.Count;

        if (perRow == 0)
        {
            return batchSize;
        }

        return Math.Max(1, Math.Min(batchSize, MaxParametersPerCommand / perRow));
    }

    /// <summary>
    /// The read half of <see cref="MaskMode.RowByRow"/>: the next batch of rows
    /// in key order, carrying the key columns and the columns whose replacement
    /// has to be computed from the current value.
    ///
    /// TOP + ORDER BY + a keyset predicate, never OFFSET (SPEC 5.3). OFFSET
    /// re-reads and discards every row already processed, so a table costs
    /// O(rows²) to walk; a keyset seek starts where the last batch stopped.
    /// </summary>
    /// <param name="isFirstBatch">
    /// True for the opening batch, which has nothing to be greater than and so
    /// carries no WHERE clause at all.
    /// </param>
    public static string SelectBatch(TableMaskPlan plan, bool isFirstBatch)
    {
        RequireKey(plan, nameof(SelectBatch));

        var columns = plan.KeyColumns
            .Select(c => c.Name)
            .Concat(plan.ComputedColumns.Select(c => c.Name))
            .Select(SqlIdentifier.Quote);

        var builder = new StringBuilder();
        builder.Append($"SELECT TOP ({BatchSizeParameter}) {string.Join(", ", columns)}");
        builder.Append($" FROM {Quote(plan.Table)}");

        if (!isFirstBatch)
        {
            builder.Append($" WHERE {GreaterThanKey(plan, LowerBoundParameter)}");
        }

        builder.Append($" ORDER BY {OrderByKey(plan)};");

        return builder.ToString();
    }

    /// <summary>
    /// The read half of <see cref="MaskMode.BatchedConstant"/>: the same walk,
    /// but only the key columns, because nothing about the new values depends on
    /// the old ones. The batch's last key becomes the upper bound of the UPDATE
    /// that follows.
    /// </summary>
    public static string SelectKeyBatch(TableMaskPlan plan, bool isFirstBatch)
    {
        RequireKey(plan, nameof(SelectKeyBatch));

        var keys = string.Join(", ", plan.KeyColumns.Select(c => SqlIdentifier.Quote(c.Name)));

        var builder = new StringBuilder();
        builder.Append($"SELECT TOP ({BatchSizeParameter}) {keys}");
        builder.Append($" FROM {Quote(plan.Table)}");

        if (!isFirstBatch)
        {
            builder.Append($" WHERE {GreaterThanKey(plan, LowerBoundParameter)}");
        }

        builder.Append($" ORDER BY {OrderByKey(plan)};");

        return builder.ToString();
    }

    /// <summary>
    /// The write half of <see cref="MaskMode.BatchedConstant"/>: rewrite exactly
    /// the key range the preceding SELECT returned.
    ///
    /// The range is expressed as "greater than the previous batch's last key AND
    /// NOT greater than this batch's last key". Writing the upper bound as a
    /// negated greater-than rather than as its own less-than-or-equal keeps
    /// there being ONE key comparison in this file. A second one, written the
    /// other way round, is where an off-by-one lives — and an off-by-one here
    /// silently leaves rows holding real values.
    /// </summary>
    public static string UpdateConstantRange(TableMaskPlan plan, bool isFirstBatch)
    {
        RequireKey(plan, nameof(UpdateConstantRange));

        var builder = new StringBuilder();
        builder.Append($"UPDATE {Quote(plan.Table)} SET {ConstantAssignments(plan)} WHERE ");

        if (!isFirstBatch)
        {
            builder.Append($"({GreaterThanKey(plan, LowerBoundParameter)}) AND ");
        }

        builder.Append($"NOT ({GreaterThanKey(plan, UpperBoundParameter)});");

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="MaskMode.WholeTable"/>: one UPDATE, no key, no batching
    /// (SPEC 5.3's fallback for a table without a primary key). Every value here
    /// is a constant, which is exactly why this mode is possible at all.
    /// </summary>
    public static string UpdateWholeTable(TableMaskPlan plan) =>
        $"UPDATE {Quote(plan.Table)} SET {ConstantAssignments(plan)};";

    /// <summary>
    /// The write half of <see cref="MaskMode.RowByRow"/>: one UPDATE per row,
    /// concatenated into a single command so that a batch is one round trip.
    ///
    /// Each statement addresses its row by the full primary key, so a row can
    /// only ever be rewritten with the values computed from its own old ones.
    /// Constant columns ride along in the same SET, which is why a table mixing
    /// `scramble` and `static` is still one pass.
    /// </summary>
    /// <param name="rowCount">
    /// Rows in this command — bounded by <see cref="RowsPerCommand"/>, not by
    /// the config's batchSize alone.
    /// </param>
    public static string UpdateRows(TableMaskPlan plan, int rowCount)
    {
        RequireKey(plan, nameof(UpdateRows));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);

        var table = Quote(plan.Table);
        var builder = new StringBuilder();

        for (var row = 0; row < rowCount; row++)
        {
            var assignments = new List<string>();
            var computedOrdinal = 0;
            var constantOrdinal = 0;

            foreach (var column in plan.Columns)
            {
                var target = SqlIdentifier.Quote(column.Name);

                assignments.Add(column.Strategy switch
                {
                    // The keyword, not a null parameter — see MaskColumn.Value.
                    ColumnStrategy.Null => $"{target} = NULL",

                    // Bound once per command; the value is the same on every row.
                    ColumnStrategy.Static => $"{target} = {ConstantParameter(constantOrdinal++)}",

                    _ => $"{target} = {RowValueParameter(row, computedOrdinal++)}",
                });
            }

            var predicate = plan.KeyColumns
                .Select((c, ordinal) => $"{SqlIdentifier.Quote(c.Name)} = {RowKeyParameter(row, ordinal)}");

            builder.Append($"UPDATE {table} SET {string.Join(", ", assignments)}");
            builder.Append($" WHERE {string.Join(" AND ", predicate)};");

            if (row < rowCount - 1)
            {
                builder.Append(Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The row count used to reconcile a table after masking it.
    ///
    /// COUNT_BIG rather than COUNT: COUNT returns int and OVERFLOWS on a table
    /// past 2.1 billion rows, which is an error at the end of a long run rather
    /// than a wrong number — but an error in the check that proves the run was
    /// complete is still a failed run.
    /// </summary>
    public static string CountRows(SchemaTable table) =>
        $"SELECT COUNT_BIG(*) FROM {Quote(table)};";

    // ---- shared pieces -----------------------------------------------------

    /// <summary>
    /// SET assignments for the columns whose value is the same on every row.
    /// Constant parameters are numbered across the `static` columns only, in the
    /// order they appear — the executor binds them the same way.
    /// </summary>
    private static string ConstantAssignments(TableMaskPlan plan)
    {
        var assignments = new List<string>();
        var ordinal = 0;

        foreach (var column in plan.ConstantColumns)
        {
            var target = SqlIdentifier.Quote(column.Name);

            assignments.Add(column.Strategy == ColumnStrategy.Null
                ? $"{target} = NULL"
                : $"{target} = {ConstantParameter(ordinal++)}");
        }

        return string.Join(", ", assignments);
    }

    /// <summary>
    /// "This row's key sorts strictly after the given key", in the same order
    /// the ORDER BY uses.
    ///
    /// For a single-column key that is just <c>[k] &gt; @p0</c>. For a composite
    /// key it expands to the standard lexicographic form:
    ///
    ///   (a &gt; @p0) OR (a = @p0 AND b &gt; @p1) OR (a = @p0 AND b = @p1 AND c &gt; @p2)
    ///
    /// which reads as "greater in the first column that differs". Primary key
    /// columns are NOT NULL by definition, so none of these comparisons can
    /// evaluate to UNKNOWN and quietly drop a row.
    /// </summary>
    private static string GreaterThanKey(TableMaskPlan plan, Func<int, string> parameter)
    {
        var keys = plan.KeyColumns;
        var terms = new List<string>();

        for (var last = 0; last < keys.Count; last++)
        {
            var conjuncts = new List<string>();

            for (var i = 0; i < last; i++)
            {
                conjuncts.Add($"{SqlIdentifier.Quote(keys[i].Name)} = {parameter(i)}");
            }

            conjuncts.Add($"{SqlIdentifier.Quote(keys[last].Name)} > {parameter(last)}");

            terms.Add(conjuncts.Count == 1 ? conjuncts[0] : $"({string.Join(" AND ", conjuncts)})");
        }

        return string.Join(" OR ", terms);
    }

    private static string OrderByKey(TableMaskPlan plan) =>
        string.Join(", ", plan.KeyColumns.Select(c => SqlIdentifier.Quote(c.Name)));

    private static string Quote(SchemaTable table) => SqlIdentifier.Quote(table.Schema, table.Name);

    /// <summary>
    /// The keyed statements are unusable without a key, and building one anyway
    /// would produce SQL that updates every row in the table on every batch.
    /// <see cref="MaskPlanner"/> already guarantees this; the check is here
    /// because "already guaranteed elsewhere" is how that guarantee gets lost.
    /// </summary>
    private static void RequireKey(TableMaskPlan plan, string method)
    {
        if (plan.KeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"{method} needs a primary key, but {plan.QualifiedName} has none. "
                + "This is a bug in the planner: a keyless table must be MaskMode.WholeTable.");
        }
    }
}
