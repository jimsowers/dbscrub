using System.Text;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Safety;

/// <summary>
/// The second safety check (SPEC section 3.2): print exactly what is about to
/// happen, then require the operator to type the database name back.
///
/// The server allowlist catches the wrong MACHINE. This catches the wrong
/// DATABASE on a machine that is legitimately allowed — three restores on one
/// dev box, and the allowlist has nothing to say about which of them you meant.
///
/// Typing is the mechanism because it is the only one that cannot be satisfied
/// without reading. "Press y" is muscle memory; reproducing a name from the
/// summary is not.
/// </summary>
public static class TypedConfirmation
{
    /// <summary>
    /// Whether what the operator typed authorizes work on this database.
    ///
    /// Case-SENSITIVE, deliberately. SQL Server would treat AAVSB and aavsb as
    /// the same database, so this is stricter than it strictly needs to be —
    /// which is the point. The check exists to force a careful read, and a
    /// comparison that forgives approximate input forgives inattention too.
    /// Surrounding whitespace is trimmed, because a trailing space from a
    /// paste is not inattention.
    /// </summary>
    public static bool Matches(string? typed, string databaseName)
    {
        // Both must be non-empty. Without this, a blank database name reaching
        // here would mean pressing Enter authorizes the run — the one input a
        // distracted operator is guaranteed to produce.
        if (string.IsNullOrWhiteSpace(typed) || string.IsNullOrWhiteSpace(databaseName))
        {
            return false;
        }

        return string.Equals(typed.Trim(), databaseName.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary shown immediately above the prompt. Everything destructive
    /// this run would do, counted, so the operator is agreeing to something
    /// specific rather than to the word "continue".
    /// </summary>
    public static string BuildSummary(ScrubPlan plan, string server, string? renameTo)
    {
        var builder = new StringBuilder();

        builder.AppendLine("About to MODIFY this database. This cannot be undone.");
        builder.AppendLine();
        builder.AppendLine($"  Server            {server}");
        builder.AppendLine($"  Database          {plan.Schema.DatabaseName}");
        builder.AppendLine();

        if (plan.Schema.IsCdcEnabled)
        {
            builder.AppendLine("  Change tracking   WILL BE DISABLED (drops all capture tables)");
        }

        var temporal = plan.Temporal.Count();
        if (temporal > 0)
        {
            builder.AppendLine($"  History tables    {temporal} table(s) — history will be emptied");
        }

        builder.AppendLine($"  Tables emptied    {plan.Truncated.Count()}");
        builder.AppendLine($"  Tables masked     {plan.Masked.Count()}");
        builder.AppendLine($"  Columns masked    {plan.ColumnsToMask}");

        if (plan.Unclassified.Count > 0)
        {
            // Loudest line in the block. Proceeding past this is the decision
            // most likely to be regretted, so it is stated as an outcome, not
            // as a count.
            builder.AppendLine();
            builder.AppendLine($"  WARNING: {plan.Unclassified.Count} column(s) are UNCLASSIFIED and will");
            builder.AppendLine("           be left exactly as they are — real data included.");
        }

        if (renameTo is not null && !renameTo.Equals(plan.Schema.DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            builder.AppendLine($"  Rename to         {renameTo} (after a clean verify)");
        }

        return builder.ToString();
    }

    /// <summary>The prompt line itself.</summary>
    public static string BuildPrompt(string databaseName) =>
        $"Type the database name ({databaseName}) to continue, or anything else to abort: ";

    /// <summary>Shown when the typed value did not match. Says what was expected, never guesses.</summary>
    public static string BuildRefusal(string databaseName) =>
        $"Aborted — that did not match '{databaseName}'. Nothing was modified.";
}
