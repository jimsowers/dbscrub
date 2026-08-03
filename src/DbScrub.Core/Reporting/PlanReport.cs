using System.Text;
using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using DbScrub.Core.Planning;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Reporting;

/// <summary>
/// Renders a <see cref="CleanPlan"/> as text (SPEC section 6): the steps in the
/// order they run, then the summary, then the UNCLASSIFIED block.
///
/// A pure string function, so the report is testable without a console and
/// without a database. It also means the exact bytes a user will see are what
/// the tests assert on.
///
/// NEVER prints a data value. The only config value that appears is a `static`
/// replacement, which is by definition the fake — printing the real one would
/// violate CLAUDE.md's hard guardrail.
/// </summary>
public static class PlanReport
{
    public static string Render(CleanPlan plan, string server, string configPath)
    {
        var builder = new StringBuilder();

        AppendHeader(builder, plan, server, configPath);
        AppendSchemaFacts(builder, plan.Scrub);

        // The three phases in the order they execute. Printing them in any other
        // order would hide the thing that makes the temporal case correct.
        AppendHygiene(builder, plan.PreMask, "Before masking");
        AppendMask(builder, plan);
        AppendHygiene(builder, plan.PostMask, "After masking");

        AppendSummary(builder, plan);
        AppendProblems(builder, plan);
        AppendUnclassified(builder, plan.Scrub);

        return builder.ToString();
    }

    private static void AppendHeader(StringBuilder builder, CleanPlan plan, string server, string configPath)
    {
        builder.AppendLine("dbscrub report (read-only — nothing is modified)");
        builder.AppendLine();
        builder.AppendLine($"  Server    {server}");
        builder.AppendLine($"  Database  {plan.Scrub.Schema.DatabaseName}");
        builder.AppendLine($"  Config    {configPath}");
        builder.AppendLine();
    }

    private static void AppendSchemaFacts(StringBuilder builder, ScrubPlan plan)
    {
        var columnCount = plan.Schema.Tables.Sum(t => t.Columns.Count);
        var temporal = plan.Temporal.Count();
        var cdcTracked = plan.CdcTracked.Count();

        builder.AppendLine("Schema");
        builder.AppendLine($"  Tables              {plan.Schema.Tables.Count}");
        builder.AppendLine($"  Columns             {columnCount}");
        builder.AppendLine($"  CDC enabled         {(plan.Schema.IsCdcEnabled ? "yes" : "no")}");
        builder.AppendLine($"  CDC-tracked tables  {cdcTracked}");
        builder.AppendLine($"  Temporal tables     {temporal}");
        builder.AppendLine();
    }

    /// <summary>
    /// One phase of the hygiene pass, printed as the statements that will
    /// ACTUALLY run rather than as a paraphrase. A report that paraphrases the
    /// destructive step is a report you cannot check.
    /// </summary>
    private static void AppendHygiene(StringBuilder builder, IReadOnlyList<HygieneStep> steps, string heading)
    {
        if (steps.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{heading} ({steps.Count} statement(s))");

        foreach (var step in steps)
        {
            builder.AppendLine($"  {step.Description}");
            builder.AppendLine($"      {step.Sql}");
        }

        builder.AppendLine();
    }

    private static void AppendMask(StringBuilder builder, CleanPlan plan)
    {
        builder.AppendLine("Mask");

        if (plan.Mask.Tables.Count == 0)
        {
            builder.AppendLine("  (nothing — no column has a masking strategy)");
            builder.AppendLine();
            return;
        }

        foreach (var table in plan.Mask.Tables)
        {
            builder.AppendLine($"  {table.QualifiedName}  ({table.Columns.Count} column(s), {Describe(table.Mode)})");

            var width = table.Columns.Max(c => c.Name.Length);
            foreach (var column in table.Columns)
            {
                builder.AppendLine($"      {column.Name.PadRight(width)}  {Describe(column.Strategy)}");
            }
        }

        builder.AppendLine();
    }

    /// <summary>
    /// How a table gets rewritten. Worth printing because the choice is made for
    /// the reader, not by them, and the keyless case has a cost they should know
    /// about before it runs against a large table.
    /// </summary>
    private static string Describe(MaskMode mode) => mode switch
    {
        MaskMode.RowByRow => "row by row, batched on the primary key",
        MaskMode.BatchedConstant => "set-based, batched on the primary key",
        MaskMode.WholeTable => "set-based, ONE transaction — this table has no primary key",
        _ => mode.ToString(),
    };

    private static string Describe(ColumnStrategy strategy) => strategy switch
    {
        ColumnStrategy.Null => "null      set to NULL",
        ColumnStrategy.Scramble => "scramble  letters->x, digits->9, length preserved",
        ColumnStrategy.Static => "static    fixed replacement value",
        _ => strategy.ToString(),
    };

    private static void AppendSummary(StringBuilder builder, CleanPlan plan)
    {
        var scrub = plan.Scrub;
        var kept = scrub.Tables.SelectMany(t => t.Columns).Count(c => c.Kind == VerdictKind.Kept);
        var keptWholesale = scrub.KeptWholesale.ToList();

        builder.AppendLine("Summary");
        builder.AppendLine($"  Tables truncated    {scrub.Truncated.Count()}");
        builder.AppendLine($"  Tables masked       {plan.Mask.Tables.Count}");
        builder.AppendLine($"  Columns masked      {plan.Mask.ColumnCount}");
        builder.AppendLine($"  Columns kept        {kept}");
        builder.AppendLine($"  UNCLASSIFIED        {scrub.Unclassified.Count}");
        builder.AppendLine();

        // A table with no primary key is masked in one unbounded transaction.
        // That is correct but can be very slow and very hard on the log on a
        // large table, and it is not visible anywhere else in the output.
        var unbatched = plan.Unbatched.ToList();
        if (unbatched.Count > 0)
        {
            builder.AppendLine($"Masked in ONE transaction ({unbatched.Count}) — no primary key to batch on");
            builder.AppendLine("  Fine for a small table. On a large one this holds a single long");
            builder.AppendLine("  transaction; adding a primary key lets dbscrub batch it.");

            foreach (var table in unbatched)
            {
                builder.AppendLine($"  {table.QualifiedName}");
            }

            builder.AppendLine();
        }

        // A blanket exclusion is the one thing in this report that removes rows
        // from the UNCLASSIFIED list without anyone looking at a column. Listing
        // them keeps that visible: an exclusion nobody can see is a blind spot,
        // which is what the list exists to prevent.
        if (keptWholesale.Count > 0)
        {
            builder.AppendLine($"Excluded by a table-level \"keep\" ({keptWholesale.Count})");
            builder.AppendLine("  These are covered WHOLESALE, including any column added since.");

            var width = keptWholesale.Max(t => t.QualifiedName.Length);
            foreach (var table in keptWholesale)
            {
                var reason = table.Columns.FirstOrDefault()?.Reason ?? "(no reason given)";
                builder.AppendLine($"  {table.QualifiedName.PadRight(width)}  {reason}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendProblems(StringBuilder builder, CleanPlan plan)
    {
        var problems = plan.Problems;

        if (problems.Count == 0)
        {
            return;
        }

        builder.AppendLine($"Problems ({problems.Count}) — these block `clean`");
        foreach (var problem in problems)
        {
            builder.AppendLine($"  {problem.Code}: {problem.Message}");
            if (problem.Suggestion is not null)
            {
                builder.AppendLine($"    {problem.Suggestion}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendUnclassified(StringBuilder builder, ScrubPlan plan)
    {
        if (plan.Unclassified.Count == 0)
        {
            builder.AppendLine("UNCLASSIFIED columns: none. Every live column has a verdict.");
            return;
        }

        builder.AppendLine($"UNCLASSIFIED columns ({plan.Unclassified.Count})");
        builder.AppendLine("Every one of these is unprotected. Paste the blocks below into your config,");
        builder.AppendLine("changing \"keep\" to a real strategy wherever the column actually holds PII.");
        builder.AppendLine();
        builder.Append(UnclassifiedFormatter.Format(plan));
    }
}
