using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Masking;

/// <summary>
/// Decides HOW each table gets masked (SPEC 5.3), and refuses the combinations
/// it cannot do safely.
///
/// Pure: a <see cref="ScrubPlan"/> in, a <see cref="MaskPlan"/> out, no database
/// and no clock. Everything it refuses, it refuses before `clean` has modified
/// anything — which is the point of a separate planning pass. The same problem
/// found by the executor would be found on table seven of twelve.
/// </summary>
public static class MaskPlanner
{
    public static MaskPlan Build(ScrubPlan plan)
    {
        var problems = new List<ConfigError>();
        var tables = new List<TableMaskPlan>();

        foreach (var table in plan.Masked)
        {
            var columns = ResolveColumns(table.Table, table.MaskedColumns, table.QualifiedName, problems);

            if (columns.Count > 0)
            {
                tables.Add(BuildTable(table.Table, columns, problems));
            }

            // SPEC 5.2: `history: "mask"` applies the SAME column strategies to
            // the history table instead of emptying it. Planned here, right
            // beside its parent, because the two have to agree column for
            // column — a history table masked with a different rule set is a
            // copy of the data with different holes in it.
            if (table.Table.TemporalType == TemporalType.SystemVersioned
                && table.History == HistoryMode.Mask)
            {
                var history = FindHistoryTable(plan, table);

                if (history is null)
                {
                    problems.Add(Problem(
                        $"tables[{table.QualifiedName}].history",
                        $"{table.QualifiedName} asks for its history to be masked, but its history table "
                        + "is not readable in this schema.",
                        "Use \"history\": \"truncate\" (the default), which does not need to read it."));
                }
                else
                {
                    var historyColumns = ResolveColumns(
                        history, table.MaskedColumns, history.QualifiedName, problems);

                    if (historyColumns.Count > 0)
                    {
                        tables.Add(BuildTable(history, historyColumns, problems));
                    }
                }
            }
        }

        return new MaskPlan(tables, problems);
    }

    /// <summary>
    /// Turns the verdicts for one table into columns the executor can act on,
    /// converting each `static` value against the real column type on the way
    /// through (SPEC section 4).
    /// </summary>
    private static List<MaskColumn> ResolveColumns(
        SchemaTable table,
        IEnumerable<ColumnVerdict> verdicts,
        string reportAs,
        List<ConfigError> problems)
    {
        var columns = new List<MaskColumn>();

        foreach (var verdict in verdicts)
        {
            var column = table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, verdict.Column, StringComparison.OrdinalIgnoreCase));

            if (column is null)
            {
                // Only reachable for a history table whose shape has drifted
                // from its parent's. For the parent itself the resolver has
                // already matched every verdict to a live column.
                problems.Add(Problem(
                    $"tables[{reportAs}].columns[{verdict.Column}]",
                    $"{reportAs} has no column named {verdict.Column}, so the strategy configured on its "
                    + "parent table cannot be applied to it.",
                    "Use \"history\": \"truncate\" for this table — a history table whose shape no longer "
                    + "matches its parent cannot be masked column for column."));
                continue;
            }

            // For the parent table these are already reported by
            // VerdictResolver, with a better message than this pass could give,
            // so skipping keeps one mistake to one error. For a history table
            // they are unreachable in practice — a history table has no key and
            // no computed columns — and skipping is still the right answer.
            if (!column.IsWritable || table.IsKeyColumn(column.Name))
            {
                continue;
            }

            var resolved = ResolveColumn(table, column, verdict, reportAs, problems);

            if (resolved is not null)
            {
                columns.Add(resolved);
            }
        }

        return columns;
    }

    private static MaskColumn? ResolveColumn(
        SchemaTable table,
        SchemaColumn column,
        ColumnVerdict verdict,
        string reportAs,
        List<ConfigError> problems)
    {
        var path = $"tables[{reportAs}].columns[{column.Name}]";

        switch (verdict.Strategy)
        {
            case ColumnStrategy.Null:
                // Nullability was checked by VerdictResolver against the live
                // column; nothing type-dependent is left to decide.
                return new MaskColumn(column, ColumnStrategy.Null, Value: null);

            case ColumnStrategy.Email:
                // The tool generates the address, so it knows the shape and can
                // recognise it later — which is the whole reason this is a
                // strategy rather than a `static` value (DECISIONS.md D26).
                if (!column.IsTextual)
                {
                    problems.Add(Problem(
                        path,
                        $"{reportAs}.{column.Name} is {column.DataType}, which cannot hold an email address.",
                        "\"email\" writes text. Use \"null\" or \"static\" for a non-text column."));
                    return null;
                }

                if (!RequireKey(table, column, "\"email\"",
                        "give every row a different address", path, problems))
                {
                    return null;
                }

                if (!RequireWidth(table, column,
                        FakeEmail.RequiredCharacters(RowDiscriminator.MaxWidth(table.KeyColumns)),
                        $"an address like {FakeEmail.For("15")}",
                        "Use \"scramble\", or \"static\" with a short placeholder.",
                        path, reportAs, problems))
                {
                    return null;
                }

                return new MaskColumn(column, ColumnStrategy.Email, Value: null);

            case ColumnStrategy.Scramble:
                if (!column.IsTextual)
                {
                    // Scramble replaces letters and digits CHARACTER BY
                    // CHARACTER. On a non-text column there is nothing coherent
                    // for it to do: an int has no letters, and a date whose
                    // digits became 9s is not a date at all.
                    problems.Add(Problem(
                        path,
                        $"{reportAs}.{column.Name} is {column.DataType}, which \"scramble\" cannot rewrite.",
                        "\"scramble\" preserves the shape of TEXT. For a number or a date, use \"static\" "
                        + "with a fixed placeholder, or \"null\" if the column is nullable."));
                    return null;
                }

                if (verdict.Unique == UniqueMode.Key)
                {
                    // The discriminator overwrites the tail, so a value can
                    // always hold it — UNLESS the whole column is narrower than
                    // the widest key the table could produce, in which case the
                    // result would be truncated mid-run.
                    if (!RequireKey(table, column, "\"unique\": \"key\"",
                            "give every row a different value", path, problems)
                        || !RequireWidth(table, column, RowDiscriminator.MaxWidth(table.KeyColumns),
                            "a row key on the end of each value",
                            "Drop \"unique\", or widen the column.",
                            path, reportAs, problems))
                    {
                        return null;
                    }
                }

                return new MaskColumn(column, ColumnStrategy.Scramble, Value: null, verdict.Unique);

            case ColumnStrategy.Static:
                if (verdict.Value is not { } configured)
                {
                    // The loader rejects `static` without a `value`, so reaching
                    // here means the plan was built by hand or the loader was
                    // bypassed. Refuse rather than write something invented.
                    problems.Add(Problem(
                        path,
                        $"{reportAs}.{column.Name} uses \"static\" but carries no value.",
                        $"{{ \"name\": \"{column.Name}\", \"strategy\": \"static\", \"value\": \"[redacted]\" }}"));
                    return null;
                }

                if (!StaticValue.TryConvert(configured, column, out var value, out var problem, out var fix))
                {
                    problems.Add(Problem(path, $"{reportAs}.{column.Name} {problem}", fix));
                    return null;
                }

                return new MaskColumn(column, ColumnStrategy.Static, value);

            default:
                // Keep never reaches here — a kept column is not a masked one.
                return null;
        }
    }

    /// <summary>
    /// Picks the mode for one table, and refuses the one combination that has no
    /// safe implementation.
    ///
    /// The deciding question is whether any replacement depends on the row's
    /// current value. If none does, the whole table is one set-based statement
    /// and no key is needed. If one does — `scramble` is the only such strategy
    /// today — then each row has to be addressed individually, and addressing a
    /// row means having a primary key.
    /// </summary>
    private static TableMaskPlan BuildTable(
        SchemaTable table,
        IReadOnlyList<MaskColumn> columns,
        List<ConfigError> problems)
    {
        var needsRowValues = columns.Any(c => !c.IsConstant);

        if (!needsRowValues)
        {
            return new TableMaskPlan(table, columns,
                table.HasPrimaryKey ? MaskMode.BatchedConstant : MaskMode.WholeTable);
        }

        if (!table.HasPrimaryKey)
        {
            // No safe fallback exists, so this is a refusal rather than a
            // warning. SPEC 5.3 offers a single set-based UPDATE for keyless
            // tables and notes that all the strategies are expressible in
            // T-SQL — which is true of `null` and `static` and NOT true of
            // `scramble`. The closest T-SQL equivalent, TRANSLATE over an ASCII
            // alphabet, leaves every accented and non-Latin letter untouched:
            // it would report success while preserving exactly the characters
            // most likely to identify someone. See DECISIONS.md D19.
            var scrambled = columns.Where(c => c.Strategy == ColumnStrategy.Scramble).Select(c => c.Name);

            problems.Add(Problem(
                $"tables[{table.QualifiedName}]",
                $"{table.QualifiedName} has no primary key, so \"scramble\" cannot be applied to "
                + $"{string.Join(", ", scrambled)}.",
                "Masking a value into a shape derived from the old value means rewriting one row at a "
                + "time, which needs a key to address the row by. Either add a primary key, or use "
                + "\"static\"/\"null\", which are the same for every row and need no key. A temporal "
                + "history table never has one — for those, use \"history\": \"truncate\"."));
        }

        return new TableMaskPlan(table, columns, MaskMode.RowByRow);
    }

    /// <summary>
    /// A strategy that varies per row has to be able to ADDRESS a row, and that
    /// means a primary key. Same refusal shape as scramble's (DECISIONS.md D19).
    /// </summary>
    private static bool RequireKey(
        SchemaTable table,
        SchemaColumn column,
        string what,
        string purpose,
        string path,
        List<ConfigError> problems)
    {
        if (table.HasPrimaryKey)
        {
            return true;
        }

        problems.Add(Problem(
            path,
            $"{table.QualifiedName}.{column.Name} uses {what}, which needs a primary key.",
            $"To {purpose}, dbscrub seeds each row's value from its primary key — and this table has "
            + "none, so there is nothing to seed from. Add a primary key, or use a strategy that "
            + "writes the same value everywhere (\"static\", \"null\")."));

        return false;
    }

    /// <summary>
    /// Refuses a column too narrow to hold what the strategy would write.
    ///
    /// Computed from the key's declared TYPES, not from any row, so it is a
    /// plan-time refusal rather than SQL Server error 8152 partway through a
    /// run — the same reasoning as the `static` length check.
    /// </summary>
    private static bool RequireWidth(
        SchemaTable table,
        SchemaColumn column,
        int required,
        string what,
        string fix,
        string path,
        string reportAs,
        List<ConfigError> problems)
    {
        var available = column.MaxLengthInCharacters;

        if (available is null || available >= required)
        {
            return true;
        }

        problems.Add(Problem(
            path,
            $"{reportAs}.{column.Name} is {column.DataType}({available}), too narrow for {what}. "
            + $"It needs at least {required} characters, because {table.QualifiedName}'s primary key "
            + $"can be up to {RowDiscriminator.MaxWidth(table.KeyColumns)} characters wide.",
            fix));

        return false;
    }

    private static SchemaTable? FindHistoryTable(ScrubPlan plan, TablePlan table) =>
        table.Table.QualifiedHistoryName is { } name
            ? plan.Schema.Tables.FirstOrDefault(t =>
                string.Equals(t.QualifiedName, name, StringComparison.OrdinalIgnoreCase))
            : null;

    private static ConfigError Problem(string path, string message, string? suggestion) =>
        new(ConfigErrorCodes.InvalidValue, path, message, suggestion, Line: 0, Column: 0);
}
