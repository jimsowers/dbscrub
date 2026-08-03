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

    /// <summary>
    /// What is in this database, and — where it matters — what that MEANS.
    ///
    /// The two features named here are the ones that keep extra copies of rows
    /// where no column-level rule can reach, so they are the reason half this
    /// tool exists. Both were previously printed as bare counts against labels
    /// ("CDC enabled", "Temporal tables") that assume the reader already knows
    /// SQL Server's feature names, which is exactly the knowledge a new person
    /// does not have and should not need.
    ///
    /// The explanation appears only when the feature is actually present.
    /// Teaching someone about temporal tables on a database that has none is
    /// noise, and noise is what stops people reading output.
    /// </summary>
    private static void AppendSchemaFacts(StringBuilder builder, ScrubPlan plan)
    {
        var columnCount = plan.Schema.Tables.Sum(t => t.Columns.Count);
        var temporal = plan.Temporal.Count();
        var cdcTracked = plan.CdcTracked.Count();

        builder.AppendLine("What is in this database");
        builder.AppendLine($"  Tables   {plan.Schema.Tables.Count}");
        builder.AppendLine($"  Columns  {columnCount}");
        builder.AppendLine();

        if (plan.Schema.IsCdcEnabled)
        {
            builder.AppendLine($"  Change Data Capture is ON, tracking {Count(cdcTracked, "table")}.");
            builder.AppendLine("    SQL Server is keeping a copy of every row that changes, in hidden");
            builder.AppendLine("    tables. Those copies hold the original values, so dbscrub switches");
            builder.AppendLine("    the feature off — which deletes them — before masking anything.");
        }
        else
        {
            builder.AppendLine("  Change Data Capture is off — nothing is shadow-copying row changes.");
        }

        builder.AppendLine();

        if (temporal > 0)
        {
            builder.AppendLine($"  {Count(temporal, "table")} keeps a hidden history of past rows.");
            builder.AppendLine("    SQL Server calls these \"system-versioned\" or \"temporal\" tables: every");
            builder.AppendLine("    previous version of every row is kept in a companion table, which");
            builder.AppendLine("    holds the original values. dbscrub empties it, and pauses history");
            builder.AppendLine("    while masking so the old values are not written straight back in.");
        }
        else
        {
            builder.AppendLine("  No table keeps a hidden history of past rows.");
        }

        builder.AppendLine();
    }

    /// <summary>"1 table" / "3 tables" — an "(s)" makes a reader do the work.</summary>
    private static string Count(int value, string noun) =>
        value == 1 ? $"1 {noun}" : $"{value} {noun}s";

    /// <summary>
    /// The width to wrap prose at. 80 is the terminal every terminal is at
    /// least as wide as, and a report that only reads well maximised is a report
    /// half the people running it will see broken.
    /// </summary>
    private const int WrapWidth = 78;

    /// <summary>
    /// Wraps on whitespace, indenting continuation lines two spaces further than
    /// the first so the start of each sentence stays findable when several are
    /// stacked. Words longer than the line are left alone rather than broken —
    /// an over-long word here is a table name, and a split identifier is worse
    /// than a long line.
    /// </summary>
    private static void AppendWrapped(StringBuilder builder, string text, string indent)
    {
        var continuation = indent + "  ";
        var width = WrapWidth - indent.Length;

        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                builder.AppendLine($"{indent}{line}");
                line.Clear();

                // Continuation lines have less room, by exactly their extra indent.
                width = WrapWidth - continuation.Length;
                indent = continuation;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            builder.AppendLine($"{indent}{line}");
        }
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
            // Descriptions explain a SQL Server behaviour rather than naming it,
            // so they are sentences and they are long. Wrapped here rather than
            // shortened there: the explanation is the point, and a line that
            // runs off the right edge of a terminal is not read at all.
            AppendWrapped(builder, step.Description, indent: "  ");

            // The SQL itself is NEVER wrapped. It is the thing a person checks
            // before approving, and a statement broken across lines is a
            // statement you cannot copy, paste, or trust you have read whole.
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
        MaskMode.RowByRow => "one row at a time, in primary key order",
        MaskMode.BatchedConstant => "whole batches at once, in primary key order",
        MaskMode.WholeTable => "the whole table in one go — it has no primary key to split it up",
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
        builder.AppendLine($"  Tables emptied           {scrub.Truncated.Count()}");
        builder.AppendLine($"  Tables masked            {plan.Mask.Tables.Count}");
        builder.AppendLine($"  Columns masked           {plan.Mask.ColumnCount}");
        builder.AppendLine($"  Columns marked \"keep\"    {kept}");

        // The line most likely to be skimmed, and the only one describing data
        // this run does NOT protect. A bare count against a bare label is not
        // enough — it has to say what the number means for the database.
        builder.AppendLine(scrub.Unclassified.Count == 0
            ? "  Columns with no rule     0"
            : $"  Columns with no rule     {scrub.Unclassified.Count}"
                + "   <-- left untouched, real data included");

        builder.AppendLine();

        // A table with no primary key is masked in one unbounded transaction.
        // That is correct but can be very slow and very hard on the log on a
        // large table, and it is not visible anywhere else in the output.
        var unbatched = plan.Unbatched.ToList();
        if (unbatched.Count > 0)
        {
            builder.AppendLine($"Rewritten all at once ({unbatched.Count}) — no primary key to split them up");
            builder.AppendLine("  dbscrub normally works through a table in small batches, so each one");
            builder.AppendLine("  finishes and releases its lock. Without a primary key it cannot tell");
            builder.AppendLine("  the rows apart, so the whole table goes in a single step. Fine when it");
            builder.AppendLine("  is small; slow and lock-heavy when it is not. A primary key fixes it.");

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

    /// <summary>
    /// The columns nobody has decided about.
    ///
    /// This block used to be headed "UNCLASSIFIED", which is a poor word for it
    /// in two ways. It names the state of the CONFIG rather than the consequence
    /// for the DATABASE, and — worse in a tool about personal data — it collides
    /// head-on with the security marking, where UNCLASSIFIED means "not
    /// sensitive, safe to share". That is the exact opposite of what it means
    /// here, and a reader who takes the familiar meaning takes away the most
    /// dangerous possible conclusion.
    ///
    /// So the heading now says what happens, the subtitle says what it costs,
    /// and the config keyword is named once so the two can still be connected.
    /// </summary>
    private static void AppendUnclassified(StringBuilder builder, ScrubPlan plan)
    {
        if (plan.Unclassified.Count == 0)
        {
            builder.AppendLine("Every column has a rule. Nothing is being left untouched by accident.");
            return;
        }

        builder.AppendLine($"Columns with no rule ({plan.Unclassified.Count}) — dbscrub will NOT touch these");
        builder.AppendLine();
        builder.AppendLine("Nobody has told dbscrub what to do with the columns below, so it leaves them");
        builder.AppendLine("exactly as they are. If any of them holds personal information, that");
        builder.AppendLine("information survives this run.");
        builder.AppendLine();
        builder.AppendLine("Paste the blocks below into your config, then replace \"keep\" with a real");
        builder.AppendLine("strategy for every column that actually holds something worth hiding.");
        builder.AppendLine();
        builder.AppendLine("To make this stop a run instead of warning about it, set");
        builder.AppendLine("\"unclassifiedColumns\": \"fail\" in the config, or pass --fail-on-unclassified.");
        builder.AppendLine();
        builder.Append(UnclassifiedFormatter.Format(plan));
    }
}
