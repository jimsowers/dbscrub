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
}
