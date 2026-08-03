using System.Text.RegularExpressions;

namespace DbScrub.Core.Verify;

/// <summary>
/// One shape the verify gate looks for (SPEC 5.4). Each pattern carries two
/// forms of itself, and the pair is the design:
///
///   * <see cref="LikePattern"/> runs on the SERVER, over every row, as a
///     `WHERE col LIKE`. Crude, but set-based and cheap, and on a clean database
///     it returns nothing — which is the case that has to be fast.
///   * <see cref="Matches"/> runs in C# over only the rows the LIKE flagged.
///     Precise, because a real regex can say things T-SQL's LIKE cannot.
///
/// Doing it in one step would mean either dragging every string column across
/// the wire, or accepting LIKE's precision for the verdict that blocks a stamp.
/// </summary>
public sealed class VerifyPattern
{
    private readonly Regex _regex;

    private VerifyPattern(string name, string description, string likePattern, string regex)
    {
        Name = name;
        Description = description;
        LikePattern = likePattern;

        // Compiled: this runs once per candidate value, and a scrub can produce
        // a lot of candidates. CultureInvariant so the same database verifies
        // the same way on any machine.
        _regex = new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    /// <summary>Short name, used in the failure report. Never contains a value.</summary>
    public string Name { get; }

    /// <summary>What a human should picture when they see the name in a report.</summary>
    public string Description { get; }

    /// <summary>The server-side pre-filter. See the class comment for why it is separate.</summary>
    public string LikePattern { get; }

    public bool Matches(string value) => _regex.IsMatch(value);

    public override string ToString() => Name;

    /// <summary>
    /// The v0 set, hard-coded (SPEC 5.4). Configurable extra patterns are a
    /// v0.x follow-up; hard-coding them keeps the gate identical everywhere it
    /// runs, which is what makes a passing verify mean the same thing to
    /// everyone.
    /// </summary>
    public static IReadOnlyList<VerifyPattern> All { get; } =
    [
        new("email", "name@host.tld",
            // Deliberately loose: _ is LIKE's single-character wildcard, so this
            // reads "something, @, something, dot, something".
            likePattern: "%_@_%_._%",
            regex: @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"),

        new("ssn", "###-##-####",
            likePattern: "%[0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][0-9][0-9]%",
            regex: @"\d{3}-\d{2}-\d{4}"),

        new("phone-dashed", "###-###-####",
            likePattern: "%[0-9][0-9][0-9]-[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]%",
            regex: @"\d{3}-\d{3}-\d{4}"),

        new("phone-parens", "(###) ###-####",
            likePattern: "%([0-9][0-9][0-9]) [0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]%",
            regex: @"\(\d{3}\)\s*\d{3}-\d{4}"),

        new("phone-dotted", "###.###.####",
            likePattern: "%[0-9][0-9][0-9].[0-9][0-9][0-9].[0-9][0-9][0-9][0-9]%",
            regex: @"\d{3}\.\d{3}\.\d{4}"),
    ];
}
