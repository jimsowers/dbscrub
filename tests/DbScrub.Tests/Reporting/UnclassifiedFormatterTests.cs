using DbScrub.Core.Configuration;
using DbScrub.Core.Reporting;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Reporting;

public class UnclassifiedFormatterTests
{
    [Fact]
    public void NothingUnclassifiedProducesNoOutput()
    {
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Person", "Email").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Equal(string.Empty, UnclassifiedFormatter.Format(plan));
    }

    [Fact]
    public void AnUnconfiguredTableEmitsAWholeTableEntry()
    {
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Secrets", "Id", "Value").Build(),
            """{ "tables": [] }""");

        var output = UnclassifiedFormatter.Format(plan);

        Assert.Contains("dbo.Secrets is not in your config", output);
        Assert.Contains("\"name\": \"dbo.Secrets\"", output);
        Assert.Contains("\"columns\": [", output);
    }

    [Fact]
    public void AConfiguredTableEmitsOnlyTheMissingColumns()
    {
        // Emitting a whole table entry here would create a duplicate table, which
        // the loader rejects with DBS008 — the tool would be sending the user
        // round a loop it created.
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Person", "Email", "Nickname").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        var output = UnclassifiedFormatter.Format(plan);

        Assert.Contains("dbo.Person is already in your config", output);
        Assert.Contains("\"name\": \"Nickname\"", output);
        Assert.DoesNotContain("\"name\": \"dbo.Person\"", output);
        Assert.DoesNotContain("\"columns\": [", output);
    }

    [Fact]
    public void EveryEmittedEntryUsesKeepWithATodoReason()
    {
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Secrets", "Value").Build(),
            """{ "tables": [] }""");

        var output = UnclassifiedFormatter.Format(plan);

        Assert.Contains("\"strategy\": \"keep\"", output);
        Assert.Contains(UnclassifiedFormatter.PlaceholderReason, output);
    }

    // ---- the test that actually matters ------------------------------------

    [Fact]
    public void PastedOutputForAnUnconfiguredTableLoadsAsAValidConfig()
    {
        // SPEC section 6 promises the block can be pasted straight into the
        // config. This asserts the promise rather than trusting the format:
        // take the emitted text, paste it where the comment says, and load it.
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Secrets", "Id", "Value").Build(),
            """{ "tables": [] }""");

        var pasted = StripComments(UnclassifiedFormatter.Format(plan)).TrimEnd().TrimEnd(',');
        var config = MaskingConfigLoader.Parse($$"""{ "tables": [ {{pasted}} ] }""", "pasted");

        var table = Assert.Single(config.Tables);
        Assert.Equal("dbo.Secrets", table.QualifiedName);
        Assert.Equal(["Id", "Value"], table.Columns.Select(c => c.Name));
        Assert.All(table.Columns, c => Assert.Equal(ColumnStrategy.Keep, c.Strategy));
    }

    [Fact]
    public void PastedOutputForAConfiguredTableLoadsWhenAddedToItsColumns()
    {
        var plan = Resolve(
            SchemaBuilder.Database().Table("dbo.Person", "Email", "Nickname", "Nudge").Build(),
            """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        var pasted = StripComments(UnclassifiedFormatter.Format(plan)).TrimEnd().TrimEnd(',');

        var config = MaskingConfigLoader.Parse($$"""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble" },
                  {{pasted}}
                ]}
              ]
            }
            """, "pasted");

        Assert.Equal(["Email", "Nickname", "Nudge"],
            config.Tables.Single().Columns.Select(c => c.Name));
    }

    [Fact]
    public void PastingTheOutputBackInResolvesEveryUnclassifiedColumn()
    {
        // The full loop: report -> paste -> report again -> nothing left.
        var schema = SchemaBuilder.Database().Table("dbo.Secrets", "Id", "Value").Build();
        var first = Resolve(schema, """{ "tables": [] }""");

        var pasted = StripComments(UnclassifiedFormatter.Format(first)).TrimEnd().TrimEnd(',');
        var second = Resolve(schema, $$"""{ "tables": [ {{pasted}} ] }""");

        Assert.Equal(2, first.Unclassified.Count);
        Assert.Empty(second.Unclassified);
        Assert.True(second.IsFullyClassified);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The emitted block carries `//` guidance lines. JSON has no comments, and
    /// a human pasting the block would not paste those, so neither do we.
    /// </summary>
    private static string StripComments(string text) =>
        string.Join(Environment.NewLine,
            text.Split(Environment.NewLine)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static ScrubPlan Resolve(Core.Schema.DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));
}
