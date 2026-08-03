using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;

namespace DbScrub.Core.Verdicts;

/// <summary>
/// Projects the config onto the live schema (SPEC section 4). Pure: same inputs,
/// same plan, no database, no clock, no filesystem. Everything the report prints
/// and everything `clean` executes is decided here.
///
/// Also runs the validations that could not happen at config-load time because
/// they need the real schema — `null` on a NOT NULL column, a strategy aimed at
/// a column that cannot be written, and config entries pointing at things that
/// no longer exist.
/// </summary>
public static class VerdictResolver
{
    /// <summary>The tool's own bookkeeping table, exempt per SPEC section 4.</summary>
    public const string SanitizationLogTable = "dbo.__SanitizationLog";

    public static ScrubPlan Resolve(DatabaseSchema schema, MaskingConfig config)
    {
        var problems = new List<ConfigError>();
        var unclassified = new List<ColumnVerdict>();

        var configByTable = config.Tables
            .ToDictionary(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase);

        // History tables are driven by their parent's `history` setting, so they
        // must not be reported as unconfigured. Collect them before the walk.
        var historyTables = schema.Tables
            .Where(t => t.TemporalType == TemporalType.SystemVersioned)
            .Select(t => t.QualifiedHistoryName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plans = new List<TablePlan>();

        foreach (var table in schema.Tables)
        {
            configByTable.Remove(table.QualifiedName, out var tableConfig);
            plans.Add(ResolveTable(table, tableConfig, historyTables, problems, unclassified));
        }

        // Whatever is left in the dictionary is configured but not present.
        foreach (var orphan in configByTable.Values)
        {
            problems.Add(new ConfigError(
                ConfigErrorCodes.InvalidValue,
                Path: $"tables[{orphan.QualifiedName}]",
                Message: $"Config lists {orphan.QualifiedName}, which does not exist in {schema.DatabaseName}.",
                Suggestion: "Remove the entry, or correct the name. A stale entry masks nothing and hides "
                    + "the fact that its columns are unprotected.",
                Line: 0,
                Column: 0));
        }

        return new ScrubPlan(schema, plans, unclassified, problems);
    }

    private static TablePlan ResolveTable(
        SchemaTable table,
        TableConfig? tableConfig,
        HashSet<string> historyTables,
        List<ConfigError> problems,
        List<ColumnVerdict> unclassified)
    {
        // ---- exemptions first, so they can never land in UNCLASSIFIED --------

        if (table.QualifiedName.Equals(SanitizationLogTable, StringComparison.OrdinalIgnoreCase))
        {
            return new TablePlan(table, TableAction.ToolOwned,
                VerdictFor(table, VerdictKind.Exempt, reason: "dbscrub's own log table"),
                IsConfigured: tableConfig is not null);
        }

        if (table.TemporalType == TemporalType.HistoryTable || historyTables.Contains(table.QualifiedName))
        {
            return new TablePlan(table, TableAction.HistoryOfTemporalTable,
                VerdictFor(table, VerdictKind.CoveredByParent,
                    reason: "temporal history; the parent table's \"history\" setting decides"),
                IsConfigured: tableConfig is not null);
        }

        // ---- no config entry: every column is unclassified -------------------

        if (tableConfig is null)
        {
            var verdicts = VerdictFor(table, VerdictKind.Unclassified, reason: null);

            // Not every verdict here IS unclassified — system-generated columns
            // were swapped to Exempt inside VerdictFor.
            unclassified.AddRange(verdicts.Where(v => v.Kind == VerdictKind.Unclassified));
            return new TablePlan(table, TableAction.Untouched, verdicts, IsConfigured: false);
        }

        // ---- table-level truncate -------------------------------------------

        if (tableConfig.Strategy == TableStrategy.Truncate)
        {
            return new TablePlan(table, TableAction.Truncate,
                VerdictFor(table, VerdictKind.Truncated, reason: "table is truncated"),
                IsConfigured: true);
        }

        // ---- table-level keep -------------------------------------------------

        if (tableConfig.Strategy == TableStrategy.Keep)
        {
            // Every column, including ones added since the config was written.
            // That is the trade this feature makes: one line of config in
            // exchange for covering the table blind. The loader requires a
            // reason so the trade is at least recorded, and the report counts
            // these tables separately so they never become invisible.
            return new TablePlan(table, TableAction.Keep,
                VerdictFor(table, VerdictKind.Kept, tableConfig.Reason),
                IsConfigured: true);
        }

        // ---- per-column ------------------------------------------------------

        var configByColumn = tableConfig.Columns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var columns = new List<ColumnVerdict>();
        var anyMasked = false;

        foreach (var column in table.Columns)
        {
            if (!configByColumn.Remove(column.Name, out var columnConfig))
            {
                var verdict = MakeVerdict(table, column, VerdictKind.Unclassified, reason: null);

                columns.Add(verdict);

                if (verdict.Kind == VerdictKind.Unclassified)
                {
                    unclassified.Add(verdict);
                }

                continue;
            }

            ValidateAgainstSchema(table, column, columnConfig, problems);

            var kind = columnConfig.Strategy == ColumnStrategy.Keep ? VerdictKind.Kept : VerdictKind.Masked;
            anyMasked |= kind == VerdictKind.Masked;

            columns.Add(new ColumnVerdict(
                table.Schema, table.Name, column.Name, kind, columnConfig.Strategy, columnConfig.Reason));
        }

        // Columns configured but absent from the table — same danger as a stale
        // table entry: it reads as protected and is not.
        foreach (var orphan in configByColumn.Values)
        {
            problems.Add(new ConfigError(
                ConfigErrorCodes.InvalidValue,
                Path: $"tables[{table.QualifiedName}].columns[{orphan.Name}]",
                Message: $"Config lists {table.QualifiedName}.{orphan.Name}, which does not exist.",
                Suggestion: "Remove the entry, or correct the name.",
                Line: 0,
                Column: 0));
        }

        return new TablePlan(table, anyMasked ? TableAction.Mask : TableAction.Untouched, columns,
            IsConfigured: true);
    }

    /// <summary>
    /// The checks that need the real column. Config-load time cannot do these —
    /// it has never seen the database (DECISIONS.md D12).
    /// </summary>
    private static void ValidateAgainstSchema(
        SchemaTable table,
        SchemaColumn column,
        ColumnConfig config,
        List<ConfigError> problems)
    {
        var path = $"tables[{table.QualifiedName}].columns[{column.Name}]";

        if (config.Strategy == ColumnStrategy.Null && !column.IsNullable)
        {
            problems.Add(new ConfigError(
                ConfigErrorCodes.InvalidValue,
                path,
                $"{table.QualifiedName}.{column.Name} uses strategy \"null\" but the column is NOT NULL.",
                $"Use \"scramble\", or \"static\" with a placeholder value. The column is {column.DataType}.",
                Line: 0,
                Column: 0));
        }

        if (config.Strategy != ColumnStrategy.Keep && !column.IsWritable)
        {
            var (why, fix) = column switch
            {
                { IsComputed: true } =>
                    ("a computed column", "Mask the columns its expression reads instead."),
                { IsIdentity: true } =>
                    ("an identity column", "Use \"keep\" — an identity value is a surrogate key, not PII."),
                _ =>
                    ($"maintained by SQL Server ({column.GeneratedAlwaysDescription})",
                     "Remove the entry. SQL Server rejects any UPDATE of a GENERATED ALWAYS column, "
                     + "and these carry row-version timestamps, never PII."),
            };

            problems.Add(new ConfigError(
                ConfigErrorCodes.InvalidValue,
                path,
                $"{table.QualifiedName}.{column.Name} cannot be masked because it is {why}.",
                fix,
                Line: 0,
                Column: 0));
        }
    }

    /// <summary>Gives every column in a table the same verdict, minus the exemptions.</summary>
    private static List<ColumnVerdict> VerdictFor(SchemaTable table, VerdictKind kind, string? reason) =>
        table.Columns
            .Select(c => MakeVerdict(table, c, kind, reason))
            .ToList();

    /// <summary>
    /// One column's verdict, with the one override that applies everywhere: a
    /// system-generated column is never UNCLASSIFIED. Asking a human to
    /// classify ValidFrom is asking a question with only one legal answer, and
    /// every such row makes the list people actually need to read longer.
    ///
    /// Note this only fires for Unclassified. Inside a truncated table the
    /// honest verdict is still Truncated — the rows go either way.
    /// </summary>
    private static ColumnVerdict MakeVerdict(
        SchemaTable table,
        SchemaColumn column,
        VerdictKind kind,
        string? reason)
    {
        if (kind == VerdictKind.Unclassified && column.IsSystemGenerated)
        {
            return new ColumnVerdict(table.Schema, table.Name, column.Name, VerdictKind.Exempt, null,
                $"maintained by SQL Server ({column.GeneratedAlwaysDescription})");
        }

        return new ColumnVerdict(table.Schema, table.Name, column.Name, kind, null, reason);
    }
}
