using System.Text;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Reporting;

/// <summary>
/// The columns with no rule, summarised instead of enumerated.
///
/// Measured against a real database, the full paste-ready listing runs to 4,538
/// lines for 2,958 columns across 220 tables. The list exists to say "if any of
/// these holds personal data, that data survives the run" — a sentence worth
/// reading at forty columns and unreadable at three thousand. A warning that
/// always fires at that size is one people stop reading, which is the failure
/// the list exists to prevent.
///
/// So this leads with the columns most likely to matter and puts the full
/// listing behind a flag. Nothing is hidden: the counts are exact, and
/// `--review-all` still prints every line.
///
/// The ranking comes in through <c>hint</c> rather than being hardcoded, so a
/// later change can rank by a declared scope instead of by name guessing
/// without rewriting any of this (DECISIONS.md D29).
/// </summary>
public static class UnclassifiedSummary
{
    /// <summary>
    /// How many tables to name before collapsing the rest to a count. Twelve
    /// fits on one screen alongside the surrounding prose, which is the only
    /// thing that matters — a list you have to scroll is a list that has already
    /// lost the reader it was written for.
    /// </summary>
    private const int MaxTablesNamed = 12;

    /// <summary>
    /// How many columns to name per table. The point is to show what KIND of
    /// data the table holds, not to enumerate it.
    /// </summary>
    private const int MaxColumnsPerTable = 6;

    public static string Format(
        ScrubPlan plan,
        Func<string, PersonalDataHints.Hint?> hint,
        string reviewAllCommand)
    {
        var builder = new StringBuilder();

        var byTable = plan.Unclassified
            .GroupBy(u => u.QualifiedTable, StringComparer.Ordinal)
            .Select(g => new
            {
                Table = g.Key,
                Strong = g.Where(c => hint(c.Column) is { Strong: true })
                          .Select(c => c.Column)
                          .ToList(),
                Weak = g.Select(c => (c.Column, Hint: hint(c.Column)))
                        .Where(x => x.Hint is { Strong: false })
                        .ToList(),
            })
            .ToList();

        var flagged = byTable
            .Where(t => t.Strong.Count > 0)
            // Most-loaded table first; name as the tiebreak so repeated runs
            // produce identical text and the report stays diffable.
            .OrderByDescending(t => t.Strong.Count)
            .ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

        builder.AppendLine("Most likely to hold personal information");
        builder.AppendLine("  A guess from column names only. Nothing here has been looked at.");
        builder.AppendLine();

        if (flagged.Count == 0)
        {
            // Said out loud rather than left as an empty space. Silence here
            // reads as "the check did not run", and the reader cannot tell the
            // difference between a clean result and a missing one.
            builder.AppendLine("  No column name looks like personal information. That is NOT the same");
            builder.AppendLine("  as there being none — a column called \"Field7\" gives nothing away.");
            builder.AppendLine();
        }
        else
        {
            var shown = flagged.Take(MaxTablesNamed).ToList();
            var width = shown.Max(t => t.Table.Length);

            foreach (var table in shown)
            {
                var columns = string.Join(", ", table.Strong.Take(MaxColumnsPerTable));

                if (table.Strong.Count > MaxColumnsPerTable)
                {
                    columns += $", +{table.Strong.Count - MaxColumnsPerTable} more";
                }

                builder.AppendLine($"  {table.Table.PadRight(width)}  {columns}");
            }

            if (flagged.Count > shown.Count)
            {
                var rest = flagged.Count - shown.Count;
                builder.AppendLine();
                builder.AppendLine($"  ...and {Plural(rest, "more table")} with columns that look personal.");
            }

            builder.AppendLine();
        }

        // The weak hints are summarised by KIND rather than listed. Free text is
        // the case that matters: no scan can tell whether a Notes column holds a
        // phone number, and only the person who runs the application knows.
        var weakByKind = byTable
            .SelectMany(t => t.Weak)
            .GroupBy(x => x.Hint!.Description, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (weakByKind.Count > 0)
        {
            builder.AppendLine("Worth a look, less clear cut");

            foreach (var kind in weakByKind)
            {
                builder.AppendLine($"  {Plural(kind.Count(), "column"),-14}  {kind.Key}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("None of the above is certain. These are matches on column names, not");
        builder.AppendLine("inspections of the data. A column called \"Notes\" may hold anything at all,");
        builder.AppendLine("and one called \"KnownCapacity\" is not an address.");
        builder.AppendLine();
        builder.AppendLine($"For the full paste-ready list of all {plan.Unclassified.Count:N0} columns:");
        builder.AppendLine($"  {reviewAllCommand}");

        return builder.ToString();
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
}
