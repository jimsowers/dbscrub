using DbScrub.Core.Hygiene;
using Xunit;

namespace DbScrub.Tests.Hygiene;

/// <summary>
/// Identifiers cannot be parameterized, so this is the only thing standing
/// between a config file and injected SQL. Table and column names come from
/// user input (CLAUDE.md).
/// </summary>
public class SqlIdentifierTests
{
    [Theory]
    [InlineData("Person", "[Person]")]
    [InlineData("dbo", "[dbo]")]
    [InlineData("Order Details", "[Order Details]")]
    [InlineData("Weird.Name", "[Weird.Name]")]
    public void OrdinaryNamesAreBracketed(string input, string expected) =>
        Assert.Equal(expected, SqlIdentifier.Quote(input));

    [Fact]
    public void SchemaQualifiedNamesBracketBothParts() =>
        Assert.Equal("[dbo].[Person]", SqlIdentifier.Quote("dbo", "Person"));

    [Fact]
    public void AClosingBracketIsDoubledSoItCannotEndTheIdentifier()
    {
        // The whole mechanism, in one assertion. Without doubling, the "]"
        // would close the identifier and everything after it would be parsed
        // as SQL.
        var quoted = SqlIdentifier.Quote("Person] ; DROP DATABASE Foo --");

        Assert.Equal("[Person]] ; DROP DATABASE Foo --]", quoted);

        // Every "]" inside the body is doubled, so the only unescaped bracket
        // is the final one.
        var body = quoted[1..^1];
        Assert.DoesNotContain("]", body.Replace("]]", string.Empty));
    }

    [Theory]
    [InlineData("]")]
    [InlineData("]]")]
    [InlineData("a]b]c")]
    [InlineData("'; DROP TABLE Person; --")]
    [InlineData("[Person]")]
    public void InjectionAttemptsProduceOneHarmlessIdentifier(string input)
    {
        var quoted = SqlIdentifier.Quote(input);

        Assert.StartsWith("[", quoted);
        Assert.EndsWith("]", quoted);
        Assert.DoesNotContain("]", quoted[1..^1].Replace("]]", string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNamesAreRefused(string? input) =>
        Assert.ThrowsAny<ArgumentException>(() => SqlIdentifier.Quote(input!));

    [Fact]
    public void OverlongNamesAreRefused()
    {
        // SQL Server caps identifiers at 128 characters, so a longer one cannot
        // name a real object — it is a config error or an attack.
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote(new string('a', 129)));
        Assert.Equal($"[{new string('a', 128)}]", SqlIdentifier.Quote(new string('a', 128)));
    }

    [Fact]
    public void NulCharactersAreRefused()
    {
        // A NUL can truncate the string in native layers below us, so what SQL
        // Server receives would not be what we validated.
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote("Person\0extra"));
    }
}
