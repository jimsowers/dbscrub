using DbScrub.Core.Masking;
using DbScrub.Core.Verify;
using Xunit;

namespace DbScrub.Tests.Verify;

/// <summary>
/// The patterns the gate looks for, and the rule that stops the gate flagging
/// this tool's own output.
///
/// NOTE ON TEST DATA: every value here is invented and drawn from ranges
/// reserved so they can never belong to a real person — example.invalid domains
/// (RFC 2606), 555-01xx phone numbers, and never-issued SSN prefixes. They are
/// shaped exactly like real personal data on purpose, because a detector can
/// only be trusted if it is tested against the shape it exists to catch.
/// </summary>
public class VerifyPatternTests
{
    // ---- what must be caught ----------------------------------------------

    [Theory]
    [InlineData("ada.lovelace@example.invalid")]
    [InlineData("Contact them at grace.hopper@example.invalid about it")]
    [InlineData("a@b.co")]
    public void EmailsAreCaught(string value) => AssertCaught("email", value);

    [Theory]
    [InlineData("123-45-6789")]
    [InlineData("SSN on file: 234-56-7890.")]
    public void SocialSecurityNumbersAreCaught(string value) => AssertCaught("ssn", value);

    [Theory]
    [InlineData("212-555-0100", "phone-dashed")]
    [InlineData("(212) 555-0100", "phone-parens")]
    [InlineData("212.555.0100", "phone-dotted")]
    public void PhoneNumbersAreCaught(string value, string pattern) => AssertCaught(pattern, value);

    [Fact]
    public void APatternHiddenInsideFreeTextIsStillCaught()
    {
        // The realistic case. A notes column is where masking gets forgotten,
        // and the value is never the whole cell.
        AssertCaught("phone-dashed", "Called from 212-555-0142 about enrollment.");
    }

    // ---- what must NOT be caught -------------------------------------------

    [Theory]
    [InlineData("Dev")]
    [InlineData("[redacted]")]
    [InlineData("2026-08-03")]
    [InlineData("12345")]
    [InlineData("")]
    public void OrdinaryValuesAreNotCaught(string value)
    {
        Assert.DoesNotContain(VerifyPattern.All, p => p.Matches(value));
    }

    [Fact]
    public void AnOrderNumberIsNotMistakenForAPhoneNumber()
    {
        // A bare run of ten digits is deliberately NOT a pattern. It would flag
        // every account number and invoice id in a legacy schema, and a gate
        // that cries wolf is a gate people route around.
        Assert.DoesNotContain(VerifyPattern.All, p => p.Matches("2125550100"));
    }

    // ---- the D17 tension ---------------------------------------------------

    [Fact]
    public void AScrambledSsnStillMatchesTheSsnPatternExactly()
    {
        // This is the conflict DECISIONS.md D17 exists for, pinned so nobody
        // "fixes" it later by weakening scramble. Scramble preserves shape ON
        // PURPOSE, and shape is what a detector looks for.
        var scrambled = Scrambler.Scramble("123-45-6789");

        Assert.Equal("999-99-9999", scrambled);
        Assert.Contains(VerifyPattern.All, p => p.Name == "ssn" && p.Matches(scrambled!));
    }

    [Fact]
    public void ButItIsRecognisedAsThisToolsOwnOutput()
    {
        // ...which is what keeps a correctly scrubbed database stampable.
        Assert.True(PlaceholderRules.IsMaskedOutput(Scrambler.Scramble("123-45-6789")));
        Assert.True(PlaceholderRules.IsMaskedOutput(Scrambler.Scramble("212-555-0100")));
        Assert.True(PlaceholderRules.IsMaskedOutput(Scrambler.Scramble("ada@example.invalid")));
    }

    [Fact]
    public void ARealValueIsNeverMistakenForMaskedOutput()
    {
        // The dangerous direction. A false positive here waves real data through
        // as "obviously fake", which is the one mistake the gate exists to stop.
        Assert.False(PlaceholderRules.IsMaskedOutput("123-45-6789"));
        Assert.False(PlaceholderRules.IsMaskedOutput("999-99-9998"));
        Assert.False(PlaceholderRules.IsMaskedOutput("ada.lovelace@example.invalid"));

        // Contains a 9 and an x, but is plainly not scrambler output.
        Assert.False(PlaceholderRules.IsMaskedOutput("box 9"));
    }

    [Fact]
    public void PunctuationAloneIsNotEvidenceOfMasking()
    {
        // Something has to have actually been replaced.
        Assert.False(PlaceholderRules.IsMaskedOutput("---"));
        Assert.False(PlaceholderRules.IsMaskedOutput("()"));
    }

    [Fact]
    public void EveryScramblerOutputSatisfiesThePlaceholderRule()
    {
        // A property test over the shapes the fixture actually contains. If this
        // ever fails, some correctly scrubbed database has become unstampable.
        string[] inputs =
        [
            "Ada", "Lovelace", "ada.lovelace@example.invalid", "123-45-6789",
            "212-555-0100", "(212) 555-0100", "212.555.0100", "José", "O'Neill-Smith",
        ];

        Assert.All(inputs, input =>
            Assert.True(PlaceholderRules.IsMaskedOutput(Scrambler.Scramble(input)),
                $"Scrambling this input produced something the verify gate would flag as a leak: {input}"));
    }

    // ---- the same trap, one level along: static replacements ---------------

    [Fact]
    public void AStaticEmailReplacementLooksExactlyLikeAnEmailBecauseThatIsThePoint()
    {
        // Pinned so nobody "fixes" it by choosing a replacement that does not
        // look like the thing it replaces. A dev database has to keep working.
        const string replacement = "dev@example.invalid";

        Assert.Contains(VerifyPattern.All, p => p.Name == "email" && p.Matches(replacement));
        Assert.False(PlaceholderRules.IsMaskedOutput(replacement),
            "No rule can derive this from its shape — that is why the run has to hand it over.");
    }

    [Fact]
    public void AConfiguredReplacementIsExcusedOnceTheRunDeclaresIt()
    {
        // Without this, a CORRECTLY masked database fails verify: every row of
        // dbo.Person.Email holds the replacement, the email pattern matches it,
        // and nothing excuses it. No run could ever earn a stamp.
        var declared = new HashSet<string>(StringComparer.Ordinal) { "dev@example.invalid" };

        Assert.True(PlaceholderRules.IsMaskedOutput("dev@example.invalid", declared));
    }

    [Fact]
    public void OnlyTheExactReplacementIsExcused()
    {
        // The conservative direction. A real address that merely CONTAINS the
        // replacement, or differs from it in any way, is still a leak.
        var declared = new HashSet<string>(StringComparer.Ordinal) { "dev@example.invalid" };

        Assert.False(PlaceholderRules.IsMaskedOutput("ada.lovelace@example.invalid", declared));
        Assert.False(PlaceholderRules.IsMaskedOutput("dev@example.invalid.co", declared));
        Assert.False(PlaceholderRules.IsMaskedOutput("see dev@example.invalid", declared));
        Assert.False(PlaceholderRules.IsMaskedOutput("DEV@EXAMPLE.INVALID", declared));
    }

    [Fact]
    public void DeclaringNothingChangesNothing()
    {
        Assert.True(PlaceholderRules.IsMaskedOutput("999-99-9999", new HashSet<string>()));
        Assert.False(PlaceholderRules.IsMaskedOutput("123-45-6789", new HashSet<string>()));
    }

    [Fact]
    public void TheRuleListIsAnExtensionPointNotAHardcodedCheck()
    {
        // DECISIONS.md D23 adds a uniqueness modifier whose output is shaped
        // like Xxxxx42 — no letter but x/X, but digits that are not 9. It will
        // need a rule of its own, and this pins that adding one is an entry in a
        // list rather than a rewrite.
        Assert.NotEmpty(PlaceholderRules.RuleNames);
        Assert.Contains("scrambled", PlaceholderRules.RuleNames);
    }

    // ---- helpers -----------------------------------------------------------

    private static void AssertCaught(string patternName, string value)
    {
        var pattern = VerifyPattern.All.Single(p => p.Name == patternName);

        Assert.True(pattern.Matches(value),
            $"The {patternName} pattern did not match a value it exists to catch.");
    }
}
