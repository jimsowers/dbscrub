using System.Text.Json;
using DbScrub.Core.Configuration;
using DbScrub.Core.Masking;
using DbScrub.Core.Schema;
using Xunit;

namespace DbScrub.Tests.Masking;

/// <summary>
/// SPEC section 4's "type-checked against column type", which could not happen
/// at config-load time because it needs the live column (DECISIONS.md D12).
///
/// Every failure here is one that SQL Server would otherwise raise partway
/// through a run, on whichever row happened to come first — leaving a database
/// that is neither raw nor clean.
/// </summary>
public class StaticValueTests
{
    // ---- text --------------------------------------------------------------

    [Theory]
    [InlineData("nvarchar")]
    [InlineData("varchar")]
    [InlineData("char")]
    [InlineData("nchar")]
    public void AStringFitsAnyTextColumn(string dataType)
    {
        Assert.True(Convert(Text("Dev"), Column(dataType, maxLength: 100), out var value));
        Assert.Equal("Dev", value);
    }

    [Fact]
    public void AStringLongerThanTheColumnIsRefused()
    {
        // SQL Server error 8152, but before the run instead of during it.
        Assert.False(Convert(Text("dev@example.invalid"), Column("char", maxLength: 11), out _,
            out var problem, out var suggestion));

        Assert.Contains("char(11)", problem!);
        Assert.Contains("19 characters", problem!);
        Assert.Contains("11 characters or fewer", suggestion!);
    }

    [Fact]
    public void UnicodeColumnLengthsAreCountedInCharactersNotBytes()
    {
        // sys.columns.max_length is in BYTES, so an nvarchar(10) reports 20.
        // Reading that as 20 characters would let a value through that does not
        // fit; reading a varchar(20) as 10 would refuse one that does.
        Assert.True(Convert(Text("0123456789"), Column("nvarchar", maxLength: 20), out _));
        Assert.False(Convert(Text("01234567890"), Column("nvarchar", maxLength: 20), out _));
    }

    [Fact]
    public void AMaxColumnHasNoLengthLimit()
    {
        // max_length is -1 for the (max) types, which is 2GB — nothing to check.
        var long_ = new string('x', 10_000);

        Assert.True(Convert(Text(long_), Column("nvarchar", maxLength: -1), out var value));
        Assert.Equal(long_, value);
    }

    [Fact]
    public void ANumberIsNotSilentlyAcceptedForATextColumn()
    {
        // The quotes the author left off were a decision. Reinterpreting them is
        // how a config comes to mean something other than what it says.
        Assert.False(Convert(Number("5000"), Column("nvarchar", maxLength: 100), out _,
            out _, out var suggestion));

        Assert.Contains("\"value\": \"5000\"", suggestion!);
    }

    // ---- numbers -----------------------------------------------------------

    [Fact]
    public void AnIntegerIsNarrowedToTheColumnsOwnType()
    {
        // So the parameter SqlClient infers matches the column rather than
        // forcing a conversion on every row.
        Assert.True(Convert(Number("42"), Column("int"), out var asInt));
        Assert.IsType<int>(asInt);

        Assert.True(Convert(Number("42"), Column("smallint"), out var asShort));
        Assert.IsType<short>(asShort);

        Assert.True(Convert(Number("42"), Column("bigint"), out var asLong));
        Assert.IsType<long>(asLong);
    }

    [Fact]
    public void TinyintIsUnsignedInSqlServer()
    {
        // 0 to 255, not -128 to 127 — the one integer range that differs from
        // every C-family language.
        Assert.True(Convert(Number("255"), Column("tinyint"), out var value));
        Assert.Equal((byte)255, value);

        Assert.False(Convert(Number("-1"), Column("tinyint"), out _));
    }

    [Fact]
    public void AnOutOfRangeIntegerIsRefusedWithTheRange()
    {
        Assert.False(Convert(Number("99999"), Column("smallint"), out _, out var problem));

        Assert.Contains("-32768 to 32767", problem!);
    }

    [Fact]
    public void ADecimalPointIsRefusedForAnIntegerColumn()
    {
        Assert.False(Convert(Number("1.5"), Column("int"), out _, out var problem));

        Assert.Contains("not a whole number", problem!);
    }

    [Fact]
    public void AQuotedNumberIsRefusedWithTheUnquotedFormToUse()
    {
        Assert.False(Convert(Text("42"), Column("int"), out _, out _, out var suggestion));

        Assert.Contains("\"value\": 42", suggestion!);
    }

    [Fact]
    public void DecimalAndFloatColumnsTakeNumbers()
    {
        Assert.True(Convert(Number("12.34"), Column("decimal"), out var asDecimal));
        Assert.Equal(12.34m, asDecimal);

        Assert.True(Convert(Number("12.34"), Column("float"), out var asDouble));
        Assert.IsType<double>(asDouble);

        Assert.True(Convert(Number("12.34"), Column("real"), out var asFloat));
        Assert.IsType<float>(asFloat);
    }

    // ---- other types -------------------------------------------------------

    [Theory]
    [InlineData(JsonValueKind.True, "true", true)]
    [InlineData(JsonValueKind.False, "false", false)]
    [InlineData(JsonValueKind.Number, "1", true)]
    [InlineData(JsonValueKind.Number, "0", false)]
    public void ABitTakesBooleansAndZeroOrOne(JsonValueKind kind, string text, bool expected)
    {
        Assert.True(Convert(new ConfigValue(kind, text), Column("bit"), out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void AQuotedBooleanIsRefused()
    {
        Assert.False(Convert(Text("true"), Column("bit"), out _, out _, out var suggestion));

        Assert.Contains("unquoted", suggestion!);
    }

    [Fact]
    public void ADateIsReadWithTheInvariantCulture()
    {
        // A config file is read on whatever machine runs the tool. "03/04/2020"
        // must not mean March here and April there.
        Assert.True(Convert(Text("1970-01-01"), Column("datetime2"), out var value));
        Assert.Equal(new DateTime(1970, 1, 1), value);
    }

    [Fact]
    public void ANonDateIsRefusedWithTheFormatToUse()
    {
        Assert.False(Convert(Text("last Tuesday"), Column("date"), out _, out _, out var suggestion));

        Assert.Contains("ISO 8601", suggestion!);
    }

    [Fact]
    public void ADateTimeOffsetKeepsItsOffset()
    {
        Assert.True(Convert(Text("1970-01-01T00:00:00+02:00"), Column("datetimeoffset"), out var value));

        var parsed = Assert.IsType<DateTimeOffset>(value);
        Assert.Equal(TimeSpan.FromHours(2), parsed.Offset);
    }

    [Fact]
    public void AGuidColumnTakesTheThirtySixCharacterForm()
    {
        Assert.True(Convert(Text("00000000-0000-0000-0000-000000000000"),
            Column("uniqueidentifier"), out var value));

        Assert.Equal(Guid.Empty, value);
    }

    [Fact]
    public void AnUnsupportedTypeIsRefusedWithSomethingToDoInstead()
    {
        // xml and varbinary really can hold PII; refusing has to come with an
        // answer or it just becomes a reason to work around the tool.
        Assert.False(Convert(Text("anything"), Column("xml"), out _, out var problem, out var suggestion));

        Assert.Contains("xml", problem!);
        Assert.Contains("\"null\"", suggestion!);
    }

    [Fact]
    public void ANotNullUnsupportedColumnIsToldThatNullIsNotAnOptionEither()
    {
        var column = new SchemaColumn("Blob", "varbinary", IsNullable: false, MaxLength: -1,
            IsComputed: false, IsIdentity: false);

        Assert.False(Convert(Text("anything"), column, out _, out _, out var suggestion));

        Assert.Contains("NOT NULL", suggestion!);
    }

    // ---- helpers -----------------------------------------------------------

    private static ConfigValue Text(string value) => new(JsonValueKind.String, value);

    private static ConfigValue Number(string value) => new(JsonValueKind.Number, value);

    private static SchemaColumn Column(string dataType, int maxLength = 8) =>
        new("Value", dataType, IsNullable: true, maxLength, IsComputed: false, IsIdentity: false);

    private static bool Convert(ConfigValue value, SchemaColumn column, out object? converted) =>
        StaticValue.TryConvert(value, column, out converted, out _, out _);

    private static bool Convert(
        ConfigValue value, SchemaColumn column, out object? converted, out string? problem) =>
        StaticValue.TryConvert(value, column, out converted, out problem, out _);

    private static bool Convert(
        ConfigValue value, SchemaColumn column,
        out object? converted, out string? problem, out string? suggestion) =>
        StaticValue.TryConvert(value, column, out converted, out problem, out suggestion);
}
