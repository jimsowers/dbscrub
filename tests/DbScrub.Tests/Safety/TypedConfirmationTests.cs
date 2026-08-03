using DbScrub.Core.Configuration;
using DbScrub.Core.Safety;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Safety;

public class TypedConfirmationTests
{
    // ---- what counts as confirmation ---------------------------------------

    [Fact]
    public void TheExactNameConfirms() =>
        Assert.True(TypedConfirmation.Matches("DbScrubTest", "DbScrubTest"));

    [Theory]
    [InlineData("  DbScrubTest  ")]
    [InlineData("DbScrubTest\t")]
    public void SurroundingWhitespaceIsForgiven(string typed) =>
        Assert.True(TypedConfirmation.Matches(typed, "DbScrubTest"));

    [Theory]
    [InlineData("dbscrubtest")]
    [InlineData("DBSCRUBTEST")]
    [InlineData("DbScrubtest")]
    public void WrongCaseDoesNotConfirm(string typed)
    {
        // Stricter than SQL Server, which would treat these as the same
        // database. That is the point: the check exists to force a careful
        // read, and forgiving approximate input forgives inattention.
        Assert.False(TypedConfirmation.Matches(typed, "DbScrubTest"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("DbScrubTest2")]
    [InlineData("DbScrub")]
    public void EverythingElseAborts(string? typed) =>
        Assert.False(TypedConfirmation.Matches(typed, "DbScrubTest"));

    [Fact]
    public void AnEmptyDatabaseNameCannotBeConfirmedByEmptyInput()
    {
        // Defensive: a blank name reaching here would otherwise mean pressing
        // Enter authorizes the run.
        Assert.False(TypedConfirmation.Matches("", ""));
    }

    // ---- what the operator is agreeing to ----------------------------------

    [Fact]
    public void TheSummaryCountsEveryDestructiveAction()
    {
        var plan = Plan("""
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "Email", "strategy": "scramble" },
                  { "name": "Ssn",   "strategy": "scramble" }
                ]},
                { "name": "dbo.LoginAudit", "strategy": "truncate" }
              ]
            }
            """);

        var summary = TypedConfirmation.BuildSummary(plan, @"localhost\MSSQLSERVER02", renameTo: null);

        Assert.Contains("cannot be undone", summary);
        Assert.Contains(@"localhost\MSSQLSERVER02", summary);
        Assert.Contains("Tables emptied    1", summary);
        Assert.Contains("Tables masked     1", summary);
        Assert.Contains("Columns masked    2", summary);
    }

    [Fact]
    public void ChangeTrackingAndHistoryAreCalledOutBecauseTheyAreNotObvious()
    {
        // Someone reading "2 tables masked" would not guess that change
        // tracking gets switched off and history tables get emptied.
        var schema = SchemaBuilder.Database()
            .WithCdcEnabled()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "Email")
            .Build();

        var plan = VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """, "test"));

        var summary = TypedConfirmation.BuildSummary(plan, "localhost", renameTo: null);

        Assert.Contains("WILL BE DISABLED", summary);
        Assert.Contains("history will be emptied", summary);
    }

    [Fact]
    public void UnclassifiedColumnsAreStatedAsAnOutcomeNotACount()
    {
        // Proceeding past this is the decision most likely to be regretted, so
        // it says what happens rather than reporting a number.
        var plan = Plan("""{ "tables": [] }""");

        var summary = TypedConfirmation.BuildSummary(plan, "localhost", renameTo: null);

        Assert.Contains("UNCLASSIFIED", summary);
        Assert.Contains("real data included", summary);
    }

    [Fact]
    public void ACleanPlanDoesNotWarnAboutUnclassifiedColumns()
    {
        var plan = Plan("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" },
                { "name": "Ssn",   "strategy": "scramble" } ]},
              { "name": "dbo.LoginAudit", "strategy": "truncate" } ] }
            """);

        Assert.DoesNotContain("UNCLASSIFIED", TypedConfirmation.BuildSummary(plan, "localhost", null));
    }

    [Fact]
    public void ARenameIsShownOnlyWhenItActuallyChangesTheName()
    {
        var plan = Plan("""{ "tables": [] }""");

        Assert.Contains("Rename to", TypedConfirmation.BuildSummary(plan, "localhost", "AAVSB_CLEAN"));

        // DECISIONS.md D10: the default flow renames to the same name, i.e.
        // not at all. Announcing a no-op rename would be noise.
        Assert.DoesNotContain("Rename to", TypedConfirmation.BuildSummary(plan, "localhost", "AAVSB"));
        Assert.DoesNotContain("Rename to", TypedConfirmation.BuildSummary(plan, "localhost", null));
    }

    [Fact]
    public void TheSummaryNeverContainsDataValues()
    {
        // CLAUDE.md hard guardrail. The summary counts actions; it never shows
        // what is in a column, not even the replacement value.
        var plan = Plan("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid" } ]} ] }
            """);

        Assert.DoesNotContain("dev@example.invalid",
            TypedConfirmation.BuildSummary(plan, "localhost", null));
    }

    [Fact]
    public void ThePromptNamesTheDatabaseSoItCanBeCopied()
    {
        var prompt = TypedConfirmation.BuildPrompt("AAVSB");

        Assert.Contains("AAVSB", prompt);
        Assert.Contains("abort", prompt);
    }

    [Fact]
    public void TheRefusalSaysNothingWasModified()
    {
        var refusal = TypedConfirmation.BuildRefusal("AAVSB");

        Assert.Contains("AAVSB", refusal);
        Assert.Contains("Nothing was modified", refusal);
    }

    // ---- helpers -----------------------------------------------------------

    private static ScrubPlan Plan(string configJson)
    {
        var schema = SchemaBuilder.Database("AAVSB")
            .Table("dbo.Person", "Email", "Ssn")
            .Table("dbo.LoginAudit", "Id")
            .Build();

        return VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));
    }
}
