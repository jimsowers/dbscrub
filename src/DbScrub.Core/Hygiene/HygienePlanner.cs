using DbScrub.Core.Configuration;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Hygiene;

/// <summary>
/// Builds the statements for the hygiene pass (SPEC 5.2) — the steps that
/// surround masking, because old copies of personal data hide in places no
/// column-level rule can reach.
///
/// The pass is in TWO phases, and the split is the important part.
/// SPEC 5.2 describes the temporal dance as three adjacent steps (detach, empty,
/// reattach) because it is describing hygiene on its own. Once masking exists,
/// masking has to happen BETWEEN the detach and the reattach — otherwise every
/// masking UPDATE writes its pre-update row into a history table that was
/// emptied moments earlier, and the run refills history with exactly the data it
/// just removed.
///
/// So: <see cref="BuildPreMask"/> detaches and empties, masking runs, and
/// <see cref="BuildPostMask"/> reattaches. There is deliberately no method that
/// returns all of them in one list — a caller that got one could run them
/// adjacently and reintroduce the bug this split exists to prevent.
///
/// This class only BUILDS statements; it never executes them. That split is
/// what lets every SQL string in the destructive path be asserted in a unit
/// test, and what lets a human read exactly what will run before it runs.
/// </summary>
public static class HygienePlanner
{
    /// <summary>
    /// Everything that must happen BEFORE the first masking UPDATE. Order is not
    /// cosmetic:
    ///
    ///   1. Change tracking OFF, before anything else. Its capture tables hold
    ///      full copies of changed rows; masking first would just add the
    ///      masked versions alongside the originals.
    ///
    ///   2. Temporal versioning OFF for every system-versioned table, and its
    ///      history emptied unless the config asked for the history to be masked
    ///      instead.
    ///
    ///   3. Configured truncations last, once nothing is capturing them.
    /// </summary>
    public static IReadOnlyList<HygieneStep> BuildPreMask(ScrubPlan plan)
    {
        var steps = new List<HygieneStep>();

        // SPEC 5.1, not 5.2, but it belongs at the front of the same list so the
        // operator reads every statement that runs in one place.
        //
        // Under FULL recovery every masking UPDATE is kept in the log until a
        // backup truncates it, and nobody backs up a disposable local copy — so
        // the log grows to hold the entire run. SIMPLE lets each committed batch
        // release its space, which is what makes batching worth doing at all.
        // Re-running it on a database that is already SIMPLE does nothing.
        steps.Add(new HygieneStep(
            Description: $"Set {plan.Schema.DatabaseName} to SIMPLE recovery so batches release log space",
            Sql: $"ALTER DATABASE {SqlIdentifier.Quote(plan.Schema.DatabaseName)} SET RECOVERY SIMPLE;",
            Target: plan.Schema.DatabaseName,
            Kind: HygieneStepKind.SetSimpleRecovery));

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
            steps.Add(BuildDetach(table));

            // `history: "mask"` means the history rows are wanted, cleaned — so
            // they must survive to be masked. Emptying them here would make the
            // setting a no-op that looked like it worked.
            if (table.History == HistoryMode.Truncate)
            {
                steps.Add(BuildTruncateHistory(table));
            }
        }

        foreach (var table in plan.Truncated)
        {
            steps.Add(BuildTruncate(table));
        }

        return steps;
    }

    /// <summary>
    /// Everything that must happen AFTER the last masking UPDATE: reattach
    /// system versioning to every table it was detached from.
    ///
    /// This must run even when masking fails partway, or the database is left
    /// with versioning silently off — which would quietly stop recording history
    /// for anything that touches it afterwards. The caller owns that guarantee;
    /// see CleanRunner.
    /// </summary>
    public static IReadOnlyList<HygieneStep> BuildPostMask(ScrubPlan plan) =>
        plan.Temporal.Select(BuildReattach).ToList();

    /// <summary>
    /// Turning versioning off is what makes masking a temporal table safe.
    ///
    /// While SYSTEM_VERSIONING is ON, every UPDATE writes the PRE-UPDATE row
    /// into the history table. So masking a temporal table without turning
    /// versioning off first does not clean it — it COPIES the unmasked row into
    /// history, where nothing is looking. The database ends up with the same
    /// personal data in a less obvious place, and the run reports success.
    /// </summary>
    private static HygieneStep BuildDetach(TablePlan table) =>
        new(Description: $"Detach history from {table.QualifiedName} so masking cannot copy rows into it",
            Sql: $"ALTER TABLE {Quote(table)} SET (SYSTEM_VERSIONING = OFF);",
            Target: table.QualifiedName,
            Kind: HygieneStepKind.DisableVersioning);

    private static HygieneStep BuildTruncateHistory(TablePlan table)
    {
        var (schema, name) = RequireHistory(table);

        return new HygieneStep(
            Description: $"Empty history table {schema}.{name}",
            // TRUNCATE, not DELETE: a history table has no foreign keys
            // pointing at it by definition, and it is the one place where the
            // row count can be large enough for the difference to matter.
            Sql: $"TRUNCATE TABLE {SqlIdentifier.Quote(schema, name)};",
            Target: $"{schema}.{name}",
            Kind: HygieneStepKind.TruncateHistory);
    }

    private static HygieneStep BuildReattach(TablePlan table)
    {
        var (schema, name) = RequireHistory(table);
        var quotedHistory = SqlIdentifier.Quote(schema, name);

        // DATA_CONSISTENCY_CHECK verifies that no history row overlaps the
        // current row's period. Left ON it is an expensive full scan of history;
        // turned OFF it is skipped. It is safe to skip here because nothing this
        // run did could break that invariant: emptying history removes rows
        // (never creates an overlap), and masking rewrites non-period columns
        // only — the period columns are GENERATED ALWAYS and refused for
        // masking, so their values are exactly what SQL Server itself wrote.
        return new HygieneStep(
            Description: $"Reattach history to {table.QualifiedName}",
            Sql: $"ALTER TABLE {Quote(table)} SET (SYSTEM_VERSIONING = ON "
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
    private static HygieneStep BuildTruncate(TablePlan table) =>
        new(Description: $"Empty {table.QualifiedName}",
            Sql: $"DELETE FROM {Quote(table)};",
            Target: table.QualifiedName,
            Kind: HygieneStepKind.TruncateTable);

    private static string Quote(TablePlan table) =>
        SqlIdentifier.Quote(table.Table.Schema, table.Table.Name);

    private static (string Schema, string Name) RequireHistory(TablePlan table)
    {
        if (table.Table is { HistorySchema: { } schema, HistoryName: { } name })
        {
            return (schema, name);
        }

        // Should be impossible: temporal_type = 2 always has a history table.
        // Refusing loudly beats emptying — or reattaching to — the wrong thing.
        throw new InvalidOperationException(
            $"{table.QualifiedName} is system-versioned but reports no history table. "
            + "Refusing to build hygiene steps for it.");
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
    /// <summary>SPEC 5.1 preflight, carried in the pre-mask list so it is printed with the rest.</summary>
    SetSimpleRecovery,

    DisableChangeTracking,
    DisableVersioning,
    TruncateHistory,
    ReEnableVersioning,
    TruncateTable,
}
