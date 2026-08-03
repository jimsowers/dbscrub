using DbScrub.Core.Masking;
using Xunit;

namespace DbScrub.Tests.Masking;

public class ScramblerTests
{
    // NOTE ON TEST DATA: every input here is invented and uses reserved ranges
    // (example.invalid, 555-01xx, never-issued SSN prefixes). CLAUDE.md forbids
    // realistic-looking PII even in tests.

    [Theory]
    [InlineData("Lovelace", "Xxxxxxxx")]
    [InlineData("ada", "xxx")]
    [InlineData("ADA", "XXX")]
    [InlineData("McDonald", "XxXxxxxx")]
    public void LettersBecomeXPreservingCase(string input, string expected) =>
        Assert.Equal(expected, Scrambler.Scramble(input));

    [Theory]
    [InlineData("123-45-6789", "999-99-9999")]
    [InlineData("212-555-0100", "999-999-9999")]
    [InlineData("0", "9")]
    public void DigitsBecomeNine(string input, string expected) =>
        Assert.Equal(expected, Scrambler.Scramble(input));

    [Theory]
    [InlineData("a.b-c_d e", "x.x-x_x x")]
    [InlineData("(212) 555-0100", "(999) 999-9999")]
    [InlineData("ada@example.invalid", "xxx@xxxxxxx.xxxxxxx")]
    public void PunctuationAndSpacingSurvive(string input, string expected) =>
        Assert.Equal(expected, Scrambler.Scramble(input));

    [Fact]
    public void LengthIsAlwaysPreserved()
    {
        // The property that lets a scrubbed database still satisfy column
        // widths and fixed-width layouts.
        foreach (var input in new[] { "", "a", "Lovelace", "123-45-6789", "  spaced  ", "!!!" })
        {
            Assert.Equal(input.Length, Scrambler.Scramble(input)!.Length);
        }
    }

    [Fact]
    public void NullStaysNull()
    {
        // A null is the absence of data. Inventing a value for it would change
        // what the row means — "no phone on file" is not the same as "xxx".
        Assert.Null(Scrambler.Scramble(null));
    }

    [Fact]
    public void EmptyStaysEmpty() => Assert.Equal(string.Empty, Scrambler.Scramble(string.Empty));

    [Fact]
    public void AccentedLettersAreScrambledNotPreserved()
    {
        // The characters most likely to identify someone are exactly the ones a
        // naive a-z check would leave untouched.
        Assert.Equal("xxx", Scrambler.Scramble("éàü"));   // é à ü, precomposed
        Assert.Equal("XXX", Scrambler.Scramble("ÉÀÜ"));   // É À Ü
    }

    [Fact]
    public void CombiningAccentsAreScrambledToo()
    {
        // "é" has two encodings. Precomposed (U+00E9) is a letter; decomposed
        // ("e" + U+0301 combining acute) is a letter followed by a MARK. If
        // marks survived, "Jose" + accent would scramble to "Xxxx" + accent —
        // leaking that the name was accented, and looking broken.
        // "Jos" + "e" + U+0301 COMBINING ACUTE ACCENT, written as an escape so
        // the encoding cannot be normalised away by an editor or a git filter.
        const string decomposed = "José";

        Assert.Equal("Xxxxx", Scrambler.Scramble(decomposed));
        Assert.True(Scrambler.LooksScrambled(Scrambler.Scramble(decomposed)));
    }

    [Fact]
    public void ScramblingIsIdempotent()
    {
        // Running clean twice must not degrade the data further. (The
        // already-stamped check should prevent a second run, but the property
        // is worth having independently.)
        var once = Scrambler.Scramble("Lovelace 123");
        Assert.Equal(once, Scrambler.Scramble(once));
    }

    [Fact]
    public void NothingOfTheOriginalSurvivesExceptShape()
    {
        const string secret = "Lovelace";
        var scrambled = Scrambler.Scramble(secret)!;

        foreach (var c in secret)
        {
            if (char.IsLetter(c) && c is not (Scrambler.LowerLetter or Scrambler.UpperLetter))
            {
                Assert.DoesNotContain(c, scrambled);
            }
        }
    }

    // ---- LooksScrambled: what lets the verify gate tell masked from real ----

    [Theory]
    [InlineData("999-99-9999")]      // a scrambled SSN
    [InlineData("999-999-9999")]     // a scrambled phone
    [InlineData("xxx@xxxxxxx.xxxxxxx")]
    [InlineData("Xxxxxxxx")]
    [InlineData("x")]
    public void ScramblerOutputLooksScrambled(string value) =>
        Assert.True(Scrambler.LooksScrambled(value));

    [Theory]
    [InlineData("123-45-6789")]                  // a real-shaped SSN
    [InlineData("212-555-0100")]
    [InlineData("ada@example.invalid")]
    [InlineData("999-99-9998")]                  // one digit off — not scrambler output
    [InlineData("xxx-xx-xxxy")]                  // one letter off
    public void RealValuesDoNotLookScrambled(string value) =>
        Assert.False(Scrambler.LooksScrambled(value));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("---")]      // punctuation only: nothing was actually replaced
    [InlineData("   ")]
    public void PunctuationAloneIsNotEvidenceOfScrambling(string? value) =>
        Assert.False(Scrambler.LooksScrambled(value));

    [Fact]
    public void AnythingScrambledLooksScrambled()
    {
        // The property the verify gate depends on. If this ever fails, verify
        // starts reporting masked columns as PII hits.
        foreach (var input in new[]
                 {
                     "Lovelace", "123-45-6789", "ada@example.invalid",
                     "212-555-0100", "A1", "Mixed Case 42",
                 })
        {
            var scrambled = Scrambler.Scramble(input)!;
            Assert.True(Scrambler.LooksScrambled(scrambled),
                $"Scramble(\"{input}\") produced \"{scrambled}\", which verify would flag as real data.");
        }
    }
}
