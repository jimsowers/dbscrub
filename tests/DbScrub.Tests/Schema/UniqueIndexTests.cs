using DbScrub.Core.Schema;
using Xunit;

namespace DbScrub.Tests.Schema;

/// <summary>
/// The uniqueness facts the planner refuses on (DECISIONS.md D27). The model
/// half; the refusals themselves are in Verdicts/UniqueColumnTests.
/// </summary>
public class UniqueIndexTests
{
    [Fact]
    public void ATableWithNoUniqueIndexHasNone()
    {
        // The common case, and the reason this is an init property with a
        // default: most tables have nothing but a primary key.
        Assert.Empty(Table().UniqueIndexes);
        Assert.Empty(Table().UniqueIndexesContaining("Email"));
    }

    [Fact]
    public void MembershipIsCaseInsensitiveLikeSqlServer()
    {
        var index = new UniqueIndex("UQ_Person_Email", ["Email"]);

        Assert.True(index.Contains("email"));
        Assert.True(index.Contains("EMAIL"));
        Assert.False(index.Contains("PersonId"));
    }

    [Fact]
    public void ColumnsStayInKeyOrder()
    {
        // Not load-bearing the way the primary key's order is, but a refusal
        // names these columns, and naming them in a different order than the
        // schema declares them reads as a different index.
        var index = new UniqueIndex("UQ_Person_Tenant", ["TenantId", "Email"]);

        Assert.Equal(["TenantId", "Email"], index.Columns);
        Assert.Equal("UQ_Person_Tenant (TenantId, Email)", index.ToString());
    }

    [Fact]
    public void OneColumnIsNotComposite()
    {
        Assert.False(new UniqueIndex("UQ_Person_Email", ["Email"]).IsComposite);
        Assert.True(new UniqueIndex("UQ_Person_Tenant", ["TenantId", "Email"]).IsComposite);
    }

    [Fact]
    public void AColumnFindsEveryIndexItTakesPartIn()
    {
        // A column can sit in more than one, and the planner only needs the
        // first to refuse — but finding all of them is what makes that choice
        // the planner's rather than an accident of this lookup.
        var table = Table() with
        {
            UniqueIndexes =
            [
                new UniqueIndex("UQ_Person_Email", ["Email"]),
                new UniqueIndex("UQ_Person_Tenant", ["TenantId", "Email"]),
            ],
        };

        Assert.Equal(
            ["UQ_Person_Email", "UQ_Person_Tenant"],
            table.UniqueIndexesContaining("Email").Select(i => i.Name));

        Assert.Equal(
            ["UQ_Person_Tenant"],
            table.UniqueIndexesContaining("TenantId").Select(i => i.Name));
    }

    [Fact]
    public void WithLeavesThePrimaryKeyAndColumnsAlone()
    {
        // Both are init properties set through `with`, and the second must not
        // wipe the first — the planner reads them together on every column.
        var table = Table() with { PrimaryKey = ["PersonId"] } with
        {
            UniqueIndexes = [new UniqueIndex("UQ_Person_Email", ["Email"])],
        };

        Assert.Equal(["PersonId"], table.PrimaryKey);
        Assert.Equal(["PersonId", "TenantId", "Email"], table.Columns.Select(c => c.Name));
        Assert.Equal("UQ_Person_Email", Assert.Single(table.UniqueIndexes).Name);
    }

    [Fact]
    public void TheBuilderAddsIndexesToTheTableItJustAdded()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "Email")
            .WithPrimaryKey("PersonId")
            .WithUniqueIndex("UQ_Person_Email", "Email")
            .Table("dbo.Other", "Id")
            .Build();

        Assert.Equal("UQ_Person_Email", Assert.Single(schema.Tables[0].UniqueIndexes).Name);
        Assert.Empty(schema.Tables[1].UniqueIndexes);
    }

    private static SchemaTable Table() =>
        new("dbo", "Person", TemporalType.None, false, null, null,
        [
            SchemaBuilder.Column("PersonId"),
            SchemaBuilder.Column("TenantId"),
            SchemaBuilder.Column("Email"),
        ]);
}
