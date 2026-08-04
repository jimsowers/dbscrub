using DbScrub.Core.Configuration;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Verdicts;

/// <summary>
/// The plan-time refusal for a column SQL Server requires to be unique
/// (DECISIONS.md D23, D27).
///
/// What is being prevented: `static` writes the same value to every row, so on a
/// unique column the second row raises error 2601 — during the UPDATE, after
/// earlier tables have already been rewritten. The run stops with the database
/// neither raw nor clean, and nothing about it looks wrong afterwards. The test
/// that matters most is therefore not that a message is produced but that it is
/// produced BEFORE anything is modified, which is what living in the verdict
/// pass buys.
/// </summary>
public class UniqueColumnTests
{
    // ---- refused ------------------------------------------------------------

    [Fact]
    public void StaticOnAUniqueColumnIsRefused()
    {
        var problem = Assert.Single(Resolve("""{ "name": "Email", "strategy": "static", "value": "d@e.invalid" }""")
            .Problems);

        Assert.Equal(ConfigErrorCodes.InvalidValue, problem.Code);
        Assert.Contains("UQ_Person_Email", problem.Message);
        Assert.Contains("same value to every row", problem.Message);
    }

    [Fact]
    public void NullOnAUniqueColumnIsRefused()
    {
        // The case a config author is most likely to think is safe. SQL Server
        // permits exactly ONE null in a unique index — this is where it differs
        // from the standard, and where "nulls are not equal" stops being true.
        var problem = Assert.Single(Resolve("""{ "name": "Email", "strategy": "null" }""").Problems);

        Assert.Contains("only ONE null", problem.Message);
    }

    [Fact]
    public void ScrambleWithoutUniqueOnAUniqueColumnIsRefused()
    {
        // Scramble collides whenever two values share a shape, which on a
        // column of same-length identifiers is every pair of them.
        var problem = Assert.Single(Resolve("""{ "name": "Email", "strategy": "scramble" }""").Problems);

        Assert.Contains("same shape", problem.Message);
        Assert.Contains("\"unique\": \"key\"", problem.Suggestion);
    }

    [Fact]
    public void TheRefusalNamesTheIndexAndAFixThatWorks()
    {
        var problem = Assert.Single(Resolve("""{ "name": "Email", "strategy": "static", "value": "x" }""")
            .Problems);

        // Naming the index matters on a schema nobody in the room wrote: without
        // it the reader has to go find which rule they broke.
        Assert.Contains("UQ_Person_Email", problem.Message);

        // Both survivors are named, and both are real strategies this tool
        // implements — a refusal whose fix is "do something else" is a dead end.
        Assert.Contains("\"email\"", problem.Suggestion);
        Assert.Contains("\"unique\": \"key\"", problem.Suggestion);

        // And it says what would otherwise happen, because "would fail partway
        // through" is the fact that makes this worth stopping for.
        Assert.Contains("half masked", problem.Suggestion);
    }

    [Fact]
    public void AUniqueConstraintOverSeveralColumnsRefusesEachOfThem()
    {
        // Conservative on purpose (D27). Whether the OTHER columns still vary is
        // a fact about the data, and this pass reads none — so it refuses rather
        // than guesses, and says which columns the rule spans.
        var schema = Schema(new UniqueIndex("UQ_Person_Tenant", ["TenantId", "Email"]));

        var plan = Resolve(schema, """
            { "name": "Email", "strategy": "static", "value": "d@e.invalid" }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("(TenantId, Email)", problem.Message);
    }

    [Fact]
    public void WithNoPrimaryKeyTheRefusalSaysTheFixNeedsOne()
    {
        // Both surviving strategies seed from the primary key, so on a heap
        // there is no fix at the column level. Saying so here stops the author
        // fixing this error and landing straight on the planner's next one.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Import", "Email")
            .WithUniqueIndex("UQ_Import_Email", "Email")
            .Build();

        var plan = Resolve(schema, """
            { "name": "Email", "strategy": "static", "value": "d@e.invalid" }
            """, table: "dbo.Import");

        Assert.Contains("has none", Assert.Single(plan.Problems).Suggestion);
    }

    // ---- allowed ------------------------------------------------------------

    [Fact]
    public void EmailOnAUniqueColumnIsAllowed()
    {
        // The strategy exists precisely because the tool can prove what it
        // wrote: fakeemail15@notreal.invalid varies with the key, and the key is
        // unique already.
        Assert.Empty(Resolve("""{ "name": "Email", "strategy": "email" }""").Problems);
    }

    [Fact]
    public void ScrambleWithUniqueKeyOnAUniqueColumnIsAllowed()
    {
        Assert.Empty(Resolve("""{ "name": "Email", "strategy": "scramble", "unique": "key" }""").Problems);
    }

    [Fact]
    public void KeepOnAUniqueColumnIsAllowed()
    {
        // Nothing is written, so nothing can collide.
        Assert.Empty(Resolve("""{ "name": "Email", "strategy": "keep", "reason": "no PII" }""").Problems);
    }

    [Fact]
    public void StaticOnAColumnWithNoUniqueIndexIsUntouched()
    {
        // The check has to be scoped to the columns that carry the rule, or it
        // becomes a ban on `static` — which is the strategy most columns want.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "TenantId", "Email")
            .WithPrimaryKey("PersonId")
            .WithUniqueIndex("UQ_Person_Email", "Email")
            .Build();

        var plan = Resolve(schema, """
            { "name": "TenantId", "strategy": "static", "value": "1" }
            """);

        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void AUniqueIndexOnAnotherTableDoesNotReachThisOne()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "TenantId", "Email")
            .WithPrimaryKey("PersonId")
            .Table("dbo.Other", "Email")
            .WithUniqueIndex("UQ_Other_Email", "Email")
            .Build();

        var plan = Resolve(schema, """
            { "name": "Email", "strategy": "static", "value": "d@e.invalid" }
            """);

        Assert.Empty(plan.Problems);
    }

    // ---- one mistake, one message -------------------------------------------

    [Fact]
    public void APrimaryKeyColumnStillGetsTheKeyRefusalNotThisOne()
    {
        // A primary key is a unique index too, so both checks could fire on the
        // same column. The key one is more specific and is the only one whose
        // fix ("keep") is right, so it wins — and the inventory keeps primary
        // keys out of UniqueIndexes to make sure of it.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "Email")
            .WithPrimaryKey("PersonId")
            .WithUniqueIndex("UQ_Person_Email", "Email")
            .Build();

        var plan = Resolve(schema, """
            { "name": "PersonId", "strategy": "static", "value": "1" }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("part of the primary key", problem.Message);
    }

    [Fact]
    public void AColumnThatCannotBeWrittenAtAllGetsThatMessageInstead()
    {
        // A unique index over a computed column is legal SQL Server. The useful
        // answer is still "this column cannot be written", not advice about
        // which masking strategy to pick — that advice would not work.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Column("PersonId"), SchemaBuilder.Computed("Slug"))
            .WithPrimaryKey("PersonId")
            .WithUniqueIndex("UQ_Person_Slug", "Slug")
            .Build();

        var plan = Resolve(schema, """
            { "name": "Slug", "strategy": "static", "value": "x" }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("computed column", problem.Message);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>dbo.Person with a key, a plain column, and a unique index on Email.</summary>
    private static DatabaseSchema Schema(UniqueIndex? index = null)
    {
        var builder = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "TenantId", "Email")
            .WithPrimaryKey("PersonId");

        return index is null
            ? builder.WithUniqueIndex("UQ_Person_Email", "Email").Build()
            : builder.WithUniqueIndex(index.Name, [.. index.Columns]).Build();
    }

    private static ScrubPlan Resolve(string columnJson) => Resolve(Schema(), columnJson);

    private static ScrubPlan Resolve(DatabaseSchema schema, string columnJson, string table = "dbo.Person") =>
        VerdictResolver.Resolve(
            schema,
            MaskingConfigLoader.Parse(
                $$"""{ "tables": [ { "name": "{{table}}", "columns": [ {{columnJson}} ] } ] }""",
                "test"));
}
