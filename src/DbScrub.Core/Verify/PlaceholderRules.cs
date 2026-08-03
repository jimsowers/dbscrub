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
    ];

    /// <summary>
    /// True when this value is recognisably something the mask engine wrote, and
    /// should therefore not be reported as a leak.
    /// </summary>
    public static bool IsMaskedOutput(string? value) =>
        value is not null && Rules.Any(rule => rule.IsMatch(value));

    /// <summary>The rule names, so a future report can say WHY a value was excused.</summary>
    public static IReadOnlyList<string> RuleNames => Rules.Select(r => r.Name).ToList();
}
