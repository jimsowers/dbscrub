using System.Text;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Reporting;

/// <summary>
/// Renders the columns with no rule as JSON the user can paste straight into
/// their config (SPEC section 6).
///
/// The important detail is that there are TWO shapes, not one. A table already
/// in the config needs only its missing columns — pasting a whole table entry
/// would create a duplicate, which the loader rejects (DBS008), sending the
/// user round a loop the tool created. A table absent from the config needs the
/// full entry. Getting this wrong turns a helpful feature into a trap, so the
/// output says which is which.
/// </summary>
public static class UnclassifiedFormatter
{
    /// <summary>
    /// The placeholder reason. `keep` is the right default only because the
    /// alternative is a strategy that rewrites data nobody has looked at yet —
    /// but it must not read as a considered decision, hence TODO.
    /// </summary>
    public const string PlaceholderReason = "TODO: decide what to do with this";

    public static string Format(ScrubPlan plan)
    {
        if (plan.Unclassified.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        // Group in schema order so repeated runs produce identical text — a
        // diffable report is worth more than a cleverly sorted one.
        var byTable = plan.Unclassified
            .GroupBy(u => u.QualifiedTable, StringComparer.Ordinal)
            .ToList();

        var configuredTables = plan.Tables
            .Where(t => t.IsConfigured)
            .Select(t => t.QualifiedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in byTable)
        {
            var columns = group.Select(g => g.Column).ToList();

            if (configuredTables.Contains(group.Key))
            {
                builder.AppendLine($"// {group.Key} is already in your config — add these to its \"columns\":");
                AppendColumnEntries(builder, columns, indent: "  ", trailingComma: false);
            }
            else
            {
                builder.AppendLine($"// {group.Key} is not in your config — add this to \"tables\":");
                builder.AppendLine("  {");
                builder.AppendLine($"    \"name\": \"{group.Key}\",");
                builder.AppendLine("    \"columns\": [");
                AppendColumnEntries(builder, columns, indent: "      ", trailingComma: false);
                builder.AppendLine("    ]");
                builder.AppendLine("  },");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendColumnEntries(
        StringBuilder builder,
        IReadOnlyList<string> columns,
        string indent,
        bool trailingComma)
    {
        // Pad names so the strategy column lines up; a ragged block is harder to
        // scan for the one entry you meant to change.
        var width = columns.Max(c => c.Length);

        for (var i = 0; i < columns.Count; i++)
        {
            var name = $"\"{columns[i]}\",".PadRight(width + 3);
            var comma = trailingComma || i < columns.Count - 1 ? "," : string.Empty;

            builder.AppendLine(
                $"{indent}{{ \"name\": {name} \"strategy\": \"keep\", \"reason\": \"{PlaceholderReason}\" }}{comma}");
        }
    }
}
