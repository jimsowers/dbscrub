using DbScrub.Core.Schema;
using Xunit;

namespace DbScrub.Tests.Schema;

/// <summary>
/// The primary key facts the mask engine batches on, and the byte-versus-
/// character trap in sys.columns.max_length.
/// </summary>
public class PrimaryKeyTests
{
    [Fact]
    public void ATableWithNoPrimaryKeyIsAHeap()
    {
        Assert.False(Table().HasPrimaryKey);
        Assert.Empty(Table().PrimaryKey);
    }

    [Fact]
    public void KeyColumnsComeBackInKeyOrderNotColumnOrder()
    {
        // Key order is what an index seek uses, and it is the order the batching
        // predicate has to compare in. Sorting or reordering this list would not
        // throw — it would produce a walk that quietly skips rows.
        var table = Table() with { PrimaryKey = ["PersonId", "TenantId"] };

        Assert.Equal(["PersonId", "TenantId"], table.KeyColumns.Select(c => c.Name));
    }

    [Fact]
    public void KeyColumnsCarryTheirTypesSoParametersCanBeBound()
    {
        var table = Table() with { PrimaryKey = ["TenantId"] };

        Assert.Equal("int", Assert.Single(table.KeyColumns).DataType);
    }

    [Fact]
    public void KeyMembershipIsCaseInsensitiveLikeSqlServer()
    {
        var table = Table() with { PrimaryKey = ["PersonId"] };

        Assert.True(table.IsKeyColumn("personid"));
        Assert.False(table.IsKeyColumn("Email"));
    }

    [Fact]
    public void AKeyNamingAColumnThatIsNotThereIsSkippedRatherThanCrashing()
    {
        // Only reachable if the column and index reads disagree, which would
        // mean the schema changed underneath the inventory. Returning what is
        // known beats throwing from a property.
        var table = Table() with { PrimaryKey = ["PersonId", "Ghost"] };

        Assert.Equal(["PersonId"], table.KeyColumns.Select(c => c.Name));
    }

    [Fact]
    public void WithLeavesEverythingElseAlone()
    {
        // The key is an init property, not a positional parameter, so `with`
        // is how it gets set — and it must not disturb the columns the rest of
        // the engine reads.
        var keyed = Table() with { PrimaryKey = ["PersonId"] };

        Assert.Equal(["PersonId", "TenantId", "Email"], keyed.Columns.Select(c => c.Name));
        Assert.Equal("dbo.Person", keyed.QualifiedName);
        Assert.Equal(["PersonId"], keyed.PrimaryKey);
    }

    // ---- max_length is in bytes --------------------------------------------

    [Theory]
    [InlineData("nvarchar", 200, 100)]
    [InlineData("nchar", 22, 11)]
    [InlineData("varchar", 100, 100)]
    [InlineData("char", 11, 11)]
    public void UnicodeTypesReportTwiceTheirCharacterLength(string dataType, int maxLength, int expected)
    {
        Assert.Equal(expected, Column(dataType, maxLength).MaxLengthInCharacters);
    }

    [Theory]
    [InlineData("nvarchar", -1)]
    [InlineData("varchar", -1)]
    public void TheMaxTypesReportNoLimit(string dataType, int maxLength)
    {
        Assert.Null(Column(dataType, maxLength).MaxLengthInCharacters);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("ntext")]
    public void TheDeprecatedBlobTextTypesReportNoLimit(string dataType)
    {
        // These report the size of a pointer rather than of the data, so the
        // number is meaningless and treating it as a limit would refuse valid
        // values.
        Assert.Null(Column(dataType, 16).MaxLengthInCharacters);
    }

    [Fact]
    public void ANonTextColumnHasNoCharacterLength()
    {
        Assert.Null(Column("int", 4).MaxLengthInCharacters);
    }

    // ---- helpers -----------------------------------------------------------

    private static SchemaColumn Column(string dataType, int maxLength) =>
        new("Value", dataType, IsNullable: true, maxLength, IsComputed: false, IsIdentity: false);

    private static SchemaTable Table() =>
        new("dbo", "Person", TemporalType.None, false, null, null,
        [
            Column("int", 4) with { Name = "PersonId" },
            Column("int", 4) with { Name = "TenantId" },
            Column("nvarchar", 200) with { Name = "Email" },
        ]);
}
