using DbScrub.Core.Stamp;
using Xunit;

namespace DbScrub.Tests.Stamp;

/// <summary>
/// The parts of the stamp that can be tested without a server: the config hash
/// and the version, both of which end up in a record people later trust.
/// </summary>
public class StampWriterTests
{
    [Fact]
    public void TheConfigHashIsStableForTheSameContent()
    {
        const string config = """{ "tables": [] }""";

        Assert.Equal(StampRecord.HashConfigText(config), StampRecord.HashConfigText(config));
    }

    [Fact]
    public void AnyChangeToTheConfigChangesTheHash()
    {
        // The hash answers "is this the config that database was cleaned with?".
        // Whitespace counts, because the question is about the file a human is
        // looking at, not about the parsed model.
        var original = StampRecord.HashConfigText("""{ "tables": [] }""");

        Assert.NotEqual(original, StampRecord.HashConfigText("""{ "tables": []  }"""));
        Assert.NotEqual(original, StampRecord.HashConfigText("""{ "tables": [ { "name": "dbo.X" } ] }"""));
    }

    [Fact]
    public void TheHashIsLowercaseHexSoItReadsTheSameEverywhere()
    {
        var hash = StampRecord.HashConfigText("anything");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigitLower(c), $"Unexpected character '{c}'."));
    }

    [Fact]
    public void TheHashFitsTheColumnItIsStoredIn()
    {
        // __SanitizationLog.ConfigHash is nvarchar(64), and the extended
        // property is nvarchar(4000). A hash longer than its column would fail
        // the write at the very end of a successful run.
        Assert.True(StampRecord.HashConfigText("anything").Length <= 64);
    }

    [Fact]
    public void TheToolVersionIsReadFromTheAssemblyRatherThanAConstant()
    {
        // So it cannot drift from what actually ran.
        var version = StampRecord.CurrentToolVersion;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("unknown", version);
        Assert.StartsWith("0.1.0", version);
    }

    [Fact]
    public void AStampRecordCarriesEverythingSpecFiveFiveAsksFor()
    {
        var record = new StampRecord(
            RunUtc: new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
            ToolVersion: "0.1.0",
            ConfigHash: StampRecord.HashConfigText("{}"),
            TablesTouched: 3,
            RowsUpdated: 1234,
            Duration: TimeSpan.FromSeconds(42.5));

        Assert.Equal(DateTimeKind.Utc, record.RunUtc.Kind);
        Assert.Equal(3, record.TablesTouched);
        Assert.Equal(1234, record.RowsUpdated);
        Assert.Equal(42.5, record.Duration.TotalSeconds);
    }

    [Fact]
    public void WhatTheWriterWritesIsWhatTheReaderReadsBack()
    {
        // The two halves have to agree on the property names, and they are in
        // different files. Round-tripping the round-trip format ("o") through
        // the reader's parser is the part most likely to drift.
        var runUtc = new DateTime(2026, 8, 3, 12, 34, 56, DateTimeKind.Utc);

        var status = StampReader.Interpret(new Dictionary<string, string?>
        {
            [SanitizationStatus.PropertyNames.Sanitized] = "true",
            [SanitizationStatus.PropertyNames.SanitizedUtc] =
                runUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            [SanitizationStatus.PropertyNames.ConfigHash] = "abc123",
            [SanitizationStatus.PropertyNames.ToolVersion] = "0.1.0",
        });

        Assert.True(status.IsSanitized);
        Assert.Equal(runUtc, status.SanitizedUtc);
        Assert.Equal("abc123", status.ConfigHash);
        Assert.Equal("0.1.0", status.ToolVersion);
    }
}
