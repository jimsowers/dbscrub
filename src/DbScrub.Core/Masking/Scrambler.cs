namespace DbScrub.Core.Masking;

/// <summary>
/// The `scramble` strategy (SPEC section 4): same-length replacement that keeps
/// a value's shape while destroying its content.
///
///   letters      -> x, or X if the original was upper case
///   digits       -> 9
///   everything else (punctuation, spaces, symbols) is preserved
///
/// Shape is preserved deliberately. A dev database still has to work: forms
/// validate, fixed-width reports line up, and a column declared varchar(11)
/// still holds eleven characters. Replacing everything with "REDACTED" breaks
/// all of that.
///
/// The cost of preserving shape is that a scrambled value still LOOKS like what
/// it was — `999-99-9999` is unmistakably SSN-shaped. That is why the verify
/// gate has to ignore all-placeholder values (DECISIONS.md D17), and why
/// columns whose shape is the sensitive part are better served by `static`.
/// </summary>
public static class Scrambler
{
    /// <summary>Replacement for a lower-case letter.</summary>
    public const char LowerLetter = 'x';

    /// <summary>Replacement for an upper-case letter.</summary>
    public const char UpperLetter = 'X';

    /// <summary>Replacement for any digit.</summary>
    public const char Digit = '9';

    /// <summary>
    /// Scrambles a value. NULL stays NULL — a null is already the absence of
    /// data, and inventing a value for it would change what the row means.
    /// </summary>
    public static string? Scramble(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // Same length by construction: exactly one output char per input char.
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                destination[i] = ScrambleChar(source[i]);
            }
        });
    }

    /// <summary>
    /// char.IsLetter and char.IsDigit are Unicode-aware, so an accented letter
    /// scrambles like any other letter rather than surviving as itself — which
    /// would leak the very characters most likely to identify someone.
    ///
    /// Combining marks are scrambled too. "é" can be stored either as one
    /// codepoint (U+00E9, a letter) or as "e" plus a combining acute (U+0301,
    /// a non-spacing MARK, not a letter). Without this branch the second form
    /// would keep its accent — "José" scrambling to "Xxxx́" — which leaks that
    /// the name was accented and looks broken besides.
    /// </summary>
    public static char ScrambleChar(char c)
    {
        if (char.IsLetter(c))
        {
            return char.IsUpper(c) ? UpperLetter : LowerLetter;
        }

        if (char.IsDigit(c))
        {
            return Digit;
        }

        // Marks have no case of their own, so they take the lower-case form.
        return char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            ? LowerLetter
            : c;
    }

    /// <summary>
    /// Scrambles, then overwrites the TAIL with the row's key so no two rows
    /// share a value (DECISIONS.md D23).
    ///
    /// Overwriting rather than appending is what preserves length. `Xxxxxxx`
    /// with key 42 becomes `Xxxxx42`, still seven characters — so a fixed-width
    /// column still fits and scramble keeps the one guarantee it exists for.
    /// Appending would overflow a char(11) and break it.
    ///
    /// When the value is SHORTER than the key there is nothing to overwrite into
    /// and the result is the key alone. That grows the value, which is why the
    /// planner refuses this strategy on a column too narrow to hold the widest
    /// key the table could produce.
    /// </summary>
    public static string? ScrambleUnique(string? value, string discriminator)
    {
        var scrambled = Scramble(value);

        if (scrambled is null)
        {
            // NULL stays NULL. A null row is not a duplicate of anything, so
            // uniqueness has nothing to say about it.
            return null;
        }

        if (scrambled.Length <= discriminator.Length)
        {
            return discriminator;
        }

        return string.Concat(scrambled.AsSpan(0, scrambled.Length - discriminator.Length), discriminator);
    }

    /// <summary>
    /// True when every character in the value is something Scramble could have
    /// produced — i.e. it contains no letter other than x/X and no digit other
    /// than 9.
    ///
    /// The verify gate uses this to tell "an SSN" from "a scrambled SSN". It is
    /// deliberately conservative about what it will call scrambled: a value has
    /// to be entirely placeholders, so a real value that happens to contain a 9
    /// or an x is never mistaken for masked output.
    /// </summary>
    public static bool LooksScrambled(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var sawPlaceholder = false;

        foreach (var c in value)
        {
            if (char.IsLetter(c))
            {
                if (c != LowerLetter && c != UpperLetter)
                {
                    return false;
                }

                sawPlaceholder = true;
            }
            else if (char.IsDigit(c))
            {
                if (c != Digit)
                {
                    return false;
                }

                sawPlaceholder = true;
            }
        }

        // Punctuation alone ("---") is not evidence of scrambling; something
        // has to have actually been replaced.
        return sawPlaceholder;
    }

    /// <summary>
    /// True when the value is scrambler output carrying a row key on the end —
    /// what <see cref="ScrambleUnique"/> produces.
    ///
    /// Needed because the discriminator's digits are real digits, not 9s, so
    /// `xxx@xxxxxxx.xxxxx42` fails <see cref="LooksScrambled"/> outright. Left
    /// unhandled, the verify gate would flag every uniquely-masked column and no
    /// correctly scrubbed database could be stamped — the same trap D17 records.
    ///
    /// Stays conservative by peeling only a TRAILING run of digits and hyphens
    /// (the composite-key separator) and requiring everything before it to be
    /// scrambler output. A real Social Security number peels to `123-45-` whose
    /// remaining digits are not 9s, so it is not excused.
    /// </summary>
    public static bool LooksScrambledWithKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var end = value.Length;
        while (end > 0 && (char.IsAsciiDigit(value[end - 1]) || value[end - 1] == '-'))
        {
            end--;
        }

        // Nothing peeled means there is no discriminator, and LooksScrambled has
        // already had its say. Everything peeled means the value is digits alone,
        // which this must not excuse.
        return end != value.Length && end != 0 && LooksScrambled(value[..end]);
    }
}
