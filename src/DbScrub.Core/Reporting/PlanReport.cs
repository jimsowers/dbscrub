using System.Text;
using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Reporting;

/// <summary>
/// Renders a <see cref="ScrubPlan"/> as text (SPEC section 6): plan first, then
/// the summary, then the UNCLASSIFIED block.
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
    public static string Render(ScrubPlan plan, string server, string configPath)
    {
        var builder = new StringBuilder();

        AppendHeader(builder, plan, server, configPath);
        AppendSchemaFacts(builder, plan);
        AppendHygiene(builder, plan);
        AppendPlan(builder, plan);
        AppendSummary(builder, plan);
        AppendProblems(builder, plan);
        AppendUnclassified(builder, plan);

        return builder.ToString();
    }

    private static void AppendHeader(StringBuilder builder, ScrubPlan plan, string server, string configPath)
    {
        builder.AppendLine("dbscrub report (read-only — nothing is modified)");
        builder.AppendLine();
        builder.AppendLine($"  Server    {server}");
        builder.AppendLine($"  Database  {plan.Schema.DatabaseName}");
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
    /// SPEC 5.2 — the steps that run BEFORE masking, because PII copies hide in
    /// CDC capture tables and temporal history.
    /// </summary>
    private static void AppendHygiene(StringBuilder builder, ScrubPlan plan)
    {
        // Built by HygienePlanner rather than described separately here, so the
        // report shows the statements that will ACTUALLY run. A report that
        // paraphrases the destructive step is a report you cannot check.
        var steps = HygienePlanner.Build(plan);

        if (steps.Count == 0)
        {
            return;
        }

        builder.AppendLine($"Hygiene ({steps.Count} statement(s), run before masking)");

        foreach (var step in steps)
        {
            builder.AppendLine($"  {step.Description}");
            builder.AppendLine($"      {step.Sql}");
        }

        builder.AppendLine();
    }

    private static void AppendPlan(StringBuilder builder, ScrubPlan plan)
    {
        builder.AppendLine("Plan");

        var actionable = plan.Tables
            .Where(t => t.Action is TableAction.Truncate or TableAction.Mask)
            .ToList();

        if (actionable.Count == 0)
        {
            builder.AppendLine("  (nothing — no table has a truncate or a masking strategy)");
            builder.AppendLine();
            return;
        }

        foreach (var table in actionable)
        {
            if (table.Action == TableAction.Truncate)
            {
                builder.AppendLine($"  TRUNCATE  {table.QualifiedName}");
                continue;
            }

            var masked = table.Columns.Where(c => c.Kind == VerdictKind.Masked).ToList();
            builder.AppendLine($"  MASK      {table.QualifiedName}  ({masked.Count} of {table.Columns.Count} columns)");

            var width = masked.Max(c => c.Column.Length);
            foreach (var column in masked)
            {
                builder.AppendLine($"              {column.Column.PadRight(width)}  {Describe(column)}");
            }
        }

        builder.AppendLine();
    }

    private static string Describe(ColumnVerdict verdict) => verdict.Strategy switch
    {
        ColumnStrategy.Null => "null      set to NULL",
        ColumnStrategy.Scramble => "scramble  letters->x, digits->9, length preserved",
        ColumnStrategy.Static => "static    fixed replacement value",
        _ => verdict.Strategy?.ToString() ?? string.Empty,
    };

    private static void AppendSummary(StringBuilder builder, ScrubPlan plan)
    {
        var kept = plan.Tables.SelectMany(t => t.Columns).Count(c => c.Kind == VerdictKind.Kept);

        var keptWholesale = plan.KeptWholesale.ToList();

        builder.AppendLine("Summary");
        builder.AppendLine($"  Tables truncated    {plan.Truncated.Count()}");
        builder.AppendLine($"  Tables masked       {plan.Masked.Count()}");
        builder.AppendLine($"  Columns masked      {plan.ColumnsToMask}");
        builder.AppendLine($"  Columns kept        {kept}");
        builder.AppendLine($"  UNCLASSIFIED        {plan.Unclassified.Count}");
        builder.AppendLine();

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

    private static void AppendProblems(StringBuilder builder, ScrubPlan plan)
    {
        if (plan.Problems.Count == 0)
        {
            return;
        }

        builder.AppendLine($"Problems ({plan.Problems.Count}) — these block `clean`");
        foreach (var problem in plan.Problems)
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
