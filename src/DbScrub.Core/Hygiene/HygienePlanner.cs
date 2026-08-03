using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Hygiene;

/// <summary>
/// Builds the statements for the hygiene pass (SPEC 5.2) — the steps that run
/// BEFORE masking, because old copies of personal data hide in places no
/// column-level rule can reach.
///
/// This class only BUILDS statements; it never executes them. That split is
/// what lets every SQL string in the destructive path be asserted in a unit
/// test, and what lets a human read exactly what will run before it runs.
/// </summary>
public static class HygienePlanner
{
    /// <summary>
    /// The ordered statements. Order is not cosmetic:
    ///
    ///   1. Change tracking OFF, before anything else. Its capture tables hold
    ///      full copies of changed rows; masking first would just add the
    ///      masked versions alongside the originals.
    ///
    ///   2. Temporal history, via the SYSTEM_VERSIONING dance. This is the step
    ///      whose absence is a silent disaster — see BuildTemporalSteps.
    ///
    ///   3. Configured truncations last, once nothing is capturing them.
    /// </summary>
    public static IReadOnlyList<HygieneStep> Build(ScrubPlan plan)
    {
        var steps = new List<HygieneStep>();

        if (plan.Schema.IsCdcEnabled)
        {
            steps.Add(new HygieneStep(
                Description: $"Disable change data capture on {plan.Schema.DatabaseName}",
                Sql: "EXEC sys.sp_cdc_disable_db;",
                Target: plan.Schema.DatabaseName,
                Kind: HygieneStepKind.DisableChangeTracking));
        }

        foreach (var table in plan.Temporal)
        {
            steps.AddRange(BuildTemporalSteps(table));
        }

        foreach (var table in plan.Truncated)
        {
            steps.Add(BuildTruncate(table));
        }

        return steps;
    }

    /// <summary>
    /// The versioning dance for one system-versioned table.
    ///
    /// Why it exists: while SYSTEM_VERSIONING is ON, every UPDATE writes the
    /// PRE-UPDATE row into the history table. So masking a temporal table
    /// without turning versioning off first does not clean it — it COPIES the
    /// unmasked row into history, where nothing is looking. The database ends
    /// up with the same personal data in a less obvious place, and the run
    /// reports success.
    ///
    /// The sequence is therefore: detach history, empty it, reattach. Masking
    /// happens later, with versioning still off, and reattaching only after
    /// everything is masked.
    /// </summary>
    private static IEnumerable<HygieneStep> BuildTemporalSteps(TablePlan table)
    {
        var quoted = SqlIdentifier.Quote(table.Table.Schema, table.Table.Name);
        var historySchema = table.Table.HistorySchema;
        var historyName = table.Table.HistoryName;

        yield return new HygieneStep(
            Description: $"Detach history from {table.QualifiedName} so masking cannot copy rows into it",
            Sql: $"ALTER TABLE {quoted} SET (SYSTEM_VERSIONING = OFF);",
            Target: table.QualifiedName,
            Kind: HygieneStepKind.DisableVersioning);

        if (historySchema is null || historyName is null)
        {
            // Should be impossible: temporal_type = 2 always has a history
            // table. Refusing loudly beats emptying the wrong thing.
            throw new InvalidOperationException(
                $"{table.QualifiedName} is system-versioned but reports no history table. "
                + "Refusing to build hygiene steps for it.");
        }

        var quotedHistory = SqlIdentifier.Quote(historySchema, historyName);

        yield return new HygieneStep(
            Description: $"Empty history table {historySchema}.{historyName}",
            // TRUNCATE, not DELETE: a history table has no foreign keys
            // pointing at it by definition, and it is the one place where the
            // row count can be large enough for the difference to matter.
            Sql: $"TRUNCATE TABLE {quotedHistory};",
            Target: $"{historySchema}.{historyName}",
            Kind: HygieneStepKind.TruncateHistory);

        yield return new HygieneStep(
            Description: $"Reattach history to {table.QualifiedName}",
            // DATA_CONSISTENCY_CHECK = OFF because the history table is now
            // empty; there is nothing to check, and the check is expensive.
            Sql: $"ALTER TABLE {quoted} SET (SYSTEM_VERSIONING = ON "
                + $"(HISTORY_TABLE = {quotedHistory}, DATA_CONSISTENCY_CHECK = OFF));",
            Target: table.QualifiedName,
            Kind: HygieneStepKind.ReEnableVersioning);
    }

    /// <summary>
    /// A configured table-level truncate (DECISIONS.md D5).
    ///
    /// Uses DELETE rather than TRUNCATE. TRUNCATE is faster, but SQL Server
    /// refuses it outright on any table referenced by a foreign key — even a
    /// key with no rows behind it. Choosing per-table would mean asking the
    /// database about every FK and getting it wrong occasionally; DELETE always
    /// works, and audit tables are being emptied for correctness rather than
    /// for speed.
    /// </summary>
    private static HygieneStep BuildTruncate(TablePlan table)
    {
        var quoted = SqlIdentifier.Quote(table.Table.Schema, table.Table.Name);

        return new HygieneStep(
            Description: $"Empty {table.QualifiedName}",
            Sql: $"DELETE FROM {quoted};",
            Target: table.QualifiedName,
            Kind: HygieneStepKind.TruncateTable);
    }
}

/// <summary>One statement in the hygiene pass, with the sentence that explains it.</summary>
public sealed record HygieneStep(
    string Description,
    string Sql,
    string Target,
    HygieneStepKind Kind)
{
    public override string ToString() => $"{Description}{Environment.NewLine}    {Sql}";
}

public enum HygieneStepKind
{
    DisableChangeTracking,
    DisableVersioning,
    TruncateHistory,
    ReEnableVersioning,
    TruncateTable,
}
