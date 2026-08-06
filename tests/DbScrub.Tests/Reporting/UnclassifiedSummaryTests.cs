using DbScrub.Core.Configuration;
using DbScrub.Core.Planning;
using DbScrub.Core.Reporting;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Reporting;

/// <summary>
/// When the columns-with-no-rule list is too big to read, the report summarises
/// it instead of printing it (DECISIONS.md D29).
///
/// The thing these tests protect is that summarising HIDES NOTHING. The counts
/// stay exact, the flag that restores the full listing is named in the output,
/// and the report never implies the guess is a verdict.
/// </summary>
public class UnclassifiedSummaryTests
{
    /// <summary>The threshold is 50; this is comfortably over it.</summary>
    private const int Many = 60;

    // ---- small databases are unchanged -------------------------------------

    [Fact]
    public void ASmallDatabaseStillPrintsThePasteReadyBlocks()
    {
        var report = Render(SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "FirstName", "Email")
            .Build());

        Assert.Contains("Paste the blocks below into your config", report);
        Assert.Contains("\"strategy\": \"keep\"", report);
        Assert.DoesNotContain("Most likely to hold personal information", report);
    }

    // ---- large databases summarise -----------------------------------------

    [Fact]
    public void ALargeDatabaseSummarisesInsteadOfListing()
    {
        var report = Render(WideSchema());

        Assert.Contains("Most likely to hold personal information", report);
        Assert.DoesNotContain("Paste the blocks below into your config", report);
        Assert.DoesNotContain("\"strategy\": \"keep\"", report);
    }

    [Fact]
    public void TheCountIsStillExactAndStillNamesTheTables()
    {
        var report = Render(WideSchema());

        // Nothing is rounded or elided. A reader who wants the number gets the
        // number, whichever form the block takes.
        Assert.Contains($"Columns with no rule ({Many:N0} across 1 table)", report);
    }

    [Fact]
    public void TheWayBackToTheFullListingIsNamed()
    {
        var report = Render(WideSchema());

        Assert.Contains("--review-all", report);
        Assert.Contains($"full paste-ready list of all {Many:N0} columns", report);
    }

    [Fact]
    public void ReviewAllRestoresTheFullListingHoweverLargeItIs()
    {
        var report = Render(WideSchema(), reviewAll: true);

        Assert.Contains("Paste the blocks below into your config", report);
        Assert.Contains("\"strategy\": \"keep\"", report);
        Assert.DoesNotContain("Most likely to hold personal information", report);
    }

    // ---- the guess is presented as a guess ----------------------------------

    [Fact]
    public void TheSummarySaysItHasNotLookedAtAnything()
    {
        // The single most important sentence in the block. A ranked list reads
        // as authoritative unless it says otherwise, and this one has never seen
        // a value.
        var report = Render(WideSchema());

        Assert.Contains("A guess from column names only", report);
        Assert.Contains("None of the above is certain", report);
        Assert.Contains("KnownCapacity", report);
    }

    [Fact]
    public void FindingNothingIsStatedRatherThanLeftBlank()
    {
        // A schema whose column names give nothing away. The report must say the
        // check ran and found nothing, not simply omit the section — silence is
        // indistinguishable from a check that never happened.
        var report = Render(SchemaBuilder.Database()
            .Table("dbo.Opaque", Names("Field", Many))
            .Build());

        Assert.Contains("No column name looks like personal information", report);
        Assert.Contains("That is NOT the same", report);
    }

    [Fact]
    public void TablesWithPersonalDataComeFirst()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Lookup", Names("Code", 60))
            .Table("dbo.Person", "FirstName", "LastName", "SSN", "Email")
            .Build();

        var report = Render(schema);

        // dbo.Person has four flagged columns; dbo.Lookup has none, so it must
        // not appear in the "most likely" list at all.
        var section = report[report.IndexOf("Most likely", StringComparison.Ordinal)..];
        var lookupPosition = section.IndexOf("dbo.Lookup", StringComparison.Ordinal);

        Assert.Contains("dbo.Person", section);
        Assert.True(lookupPosition < 0, "a table with no personal-looking column should not be listed");
    }

    [Fact]
    public void WeakHintsAreCountedByKindRatherThanListed()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Wide", Names("Filler", 60).Concat(["CaseNotes", "ReviewNotes"]).ToArray())
            .Build();

        var report = Render(schema);

        Assert.Contains("Worth a look, less clear cut", report);
        Assert.Contains("free text", report);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Wide enough to trip the threshold, with two columns that look personal —
    /// the ordinary case on a real database.
    /// </summary>
    private static Core.Schema.DatabaseSchema WideSchema() =>
        SchemaBuilder.Database()
            .Table("dbo.Wide", Names("Column", Many - 2).Concat(["FirstName", "SSN"]).ToArray())
            .Build();

    private static string[] Names(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();

    private static string Render(Core.Schema.DatabaseSchema schema, bool reviewAll = false)
    {
        var config = MaskingConfigLoader.Parse("""{ "tables": [] }""", "test");
        var plan = CleanPlan.Build(VerdictResolver.Resolve(schema, config));

        return PlanReport.Render(plan, "localhost", "test.json", reviewAll);
    }
}
