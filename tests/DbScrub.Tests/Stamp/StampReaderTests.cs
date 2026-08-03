using DbScrub.Core.Stamp;
using Xunit;

namespace DbScrub.Tests.Stamp;

/// <summary>
/// Tests the interpretation of the extended-property bag, which is where the
/// fail-safe polarity lives. The SQL that fetches the bag is exercised against
/// a real server, not here.
/// </summary>
public class StampReaderTests
{
    [Fact]
    public void NoPropertiesAtAllMeansNotSanitized()
    {
        var status = StampReader.Interpret(new Dictionary<string, string?>());

        Assert.False(status.IsSanitized);
        Assert.Null(status.SanitizedUtc);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("  true  ")]
    public void AnExplicitTrueMeansSanitized(string value)
    {
        var status = StampReader.Interpret(Bag(sanitized: value));

        Assert.True(status.IsSanitized);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("yes")]        // not a value we write
    [InlineData("tru")]        // half-written
    [InlineData("maybe")]
    [InlineData(null)]
    public void AnythingElseMeansNotSanitized(string? value)
    {
        // Fail-safe polarity: the dangerous mistake is calling a dirty database
        // clean. Reporting a clean one as dirty just costs a re-run.
        var status = StampReader.Interpret(Bag(sanitized: value));

        Assert.False(status.IsSanitized);
    }

    [Fact]
    public void AStampWithoutTheFlagIsNotSanitizedEvenIfTheOtherFieldsLookRight()
    {
        // Someone hand-adding SanitizedUtc and ConfigHash does not make a
        // database clean.
        var bag = new Dictionary<string, string?>
        {
            [SanitizationStatus.PropertyNames.SanitizedUtc] = "2026-08-02T18:00:00Z",
            [SanitizationStatus.PropertyNames.ConfigHash] = "abc123",
        };

        Assert.False(StampReader.Interpret(bag).IsSanitized);
    }

    [Fact]
    public void AllFieldsAreReadWhenPresent()
    {
        var bag = new Dictionary<string, string?>
        {
            [SanitizationStatus.PropertyNames.Sanitized] = "true",
            [SanitizationStatus.PropertyNames.SanitizedUtc] = "2026-08-02T18:30:00Z",
            [SanitizationStatus.PropertyNames.ConfigHash] = "9f2b",
            [SanitizationStatus.PropertyNames.ToolVersion] = "0.1.0",
        };

        var status = StampReader.Interpret(bag);

        Assert.True(status.IsSanitized);
        Assert.Equal(new DateTime(2026, 8, 2, 18, 30, 0, DateTimeKind.Utc), status.SanitizedUtc);
        Assert.Equal("9f2b", status.ConfigHash);
        Assert.Equal("0.1.0", status.ToolVersion);
    }

    [Fact]
    public void AnUnparseableDateStillLeavesTheDatabaseSanitized()
    {
        // The flag is the load-bearing part. A date we cannot read is reported
        // as unknown rather than downgrading the whole stamp.
        var bag = Bag(sanitized: "true");
        bag[SanitizationStatus.PropertyNames.SanitizedUtc] = "not a date";

        var status = StampReader.Interpret(bag);

        Assert.True(status.IsSanitized);
        Assert.Null(status.SanitizedUtc);
    }

    [Fact]
    public void PropertyNameMatchingIsCaseInsensitive()
    {
        // SQL Server's default collation is case-insensitive, so a stamp
        // written as "sanitized" must still be found.
        var bag = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sanitized"] = "true",
        };

        Assert.True(StampReader.Interpret(bag).IsSanitized);
    }

    private static Dictionary<string, string?> Bag(string? sanitized) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SanitizationStatus.PropertyNames.Sanitized] = sanitized,
        };
}
