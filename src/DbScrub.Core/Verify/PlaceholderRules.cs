using DbScrub.Core.Masking;

namespace DbScrub.Core.Verify;

/// <summary>
/// Decides whether a value that LOOKS like personal data is in fact this tool's
/// own output (DECISIONS.md D17).
///
/// This exists because the verify gate and the mask engine are in direct
/// tension, on purpose. `scramble` preserves a value's SHAPE so that forms still
/// validate and column widths still hold — and shape is exactly what a pattern
/// detector looks for. A scrambled Social Security number is `999-99-9999`,
/// which matches the SSN pattern perfectly. Without this class a correctly
/// scrubbed database could never pass verify, and so could never be stamped.
///
/// It is a LIST of rules rather than a single check because more masked shapes
/// are coming. DECISIONS.md D23 adds a uniqueness modifier whose output looks
/// like `Xxxxx42` — no letter but x/X, but digits that are not 9. That
/// satisfies no rule below, so it will need one of its own, and the whole point
/// of writing this as a list now is that adding it is an entry rather than a
/// rewrite of a hardcoded condition.
///
/// Every rule must be CONSERVATIVE. A false positive here is a value this tool
/// waves through as "obviously fake" when it is real, which is the one mistake
/// the gate exists to prevent. When in doubt, a rule should say no and let the
/// gate fail the run.
/// </summary>
public static class PlaceholderRules
{
    /// <summary>
    /// The rules, each answering "could the mask engine have produced this?".
    /// </summary>
    private static readonly (string Name, Func<string, bool> IsMatch)[] Rules =
    [
        // Scrambler output: no letter but x/X, no digit but 9, and something
        // was actually replaced — so punctuation alone ("---") does not qualify.
        ("scrambled", Scrambler.LooksScrambled),

        // Scrambler output carrying a row key, from `"unique": "key"`. The key's
        // digits are real digits, so the rule above rejects it outright and a
        // uniquely-masked column would be reported as a leak.
        ("scrambled-with-key", Scrambler.LooksScrambledWithKey),

        // A generated address from the `email` strategy. Safe to recognise by
        // its DOMAIN rather than by argument: RFC 2606 reserves `.invalid`
        // permanently, so nothing real can live there. This is the payoff of
        // the tool owning the shape instead of the config supplying it.
        ("generated-email", FakeEmail.Looks),
    ];

    /// <summary>
    /// True when this value is recognisably something the mask engine wrote, and
    /// should therefore not be reported as a leak.
    /// </summary>
    /// <param name="knownReplacements">
    /// The exact values this run wrote via `static`. They have to be passed in
    /// because no rule can DERIVE them: "dev@example.invalid" is indistinguishable
    /// from a real address by shape alone, which is the whole reason a config
    /// author chose it.
    ///
    /// Without this the gate failed a correctly masked database. Both sample
    /// configs replace Email with an email-shaped constant; every row then holds
    /// a value that matches the email pattern and satisfies no rule below, so
    /// verify reported the masked column as a leak and no run could ever earn a
    /// stamp. Same trap as the scrambled-SSN one in D17, one level along: the
    /// masking is correct and the detector is right to notice — it just needs to
    /// be told what this run put there.
    ///
    /// Matched EXACTLY and case-sensitively. A real value that merely contains
    /// the replacement is still a leak.
    /// </param>
    public static bool IsMaskedOutput(string? value, IReadOnlySet<string>? knownReplacements = null)
    {
        if (value is null)
        {
            return false;
        }

        if (knownReplacements is not null && knownReplacements.Contains(value))
        {
            return true;
        }

        return Rules.Any(rule => rule.IsMatch(value));
    }

    /// <summary>The rule names, so a future report can say WHY a value was excused.</summary>
    public static IReadOnlyList<string> RuleNames => Rules.Select(r => r.Name).ToList();
}
