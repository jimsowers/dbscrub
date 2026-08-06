using System.Text;

namespace DbScrub.Core.Reporting;

/// <summary>
/// Guesses, from a column's NAME alone, whether it is likely to hold personal
/// information. Used to lead the report with the interesting columns when a
/// database is too large to print in full.
///
/// This is a HINT, never a verdict. It has never seen a value and never will —
/// printing one would violate CLAUDE.md's hard guardrail. A column called
/// `Notes` may hold nothing or may hold a caseworker's account of someone's
/// medical history, and no amount of name matching will tell them apart. The
/// report says so where it prints these.
///
/// **Matching is on whole camel-case tokens, not substrings.** That is the whole
/// design and it is not fussiness: a substring search for "city" flags
/// `KnownCapacity`, which was measured against a real 2,958-column database
/// rather than imagined. Splitting `KnownCapacity` into `Known` + `Capacity` and
/// comparing whole tokens removes that entire class of false positive.
///
/// The suppression rules below come from the same measurement. A schema that has
/// `Email` also has `EmailType`, `EmailSentLogId` and
/// `ShouldReceiveDisciplineNotificationEmails` — a record category, a foreign
/// key and a yes/no preference. Flagging those buries the fifteen columns that
/// really do hold an address.
/// </summary>
public static class PersonalDataHints
{
    /// <summary>What a hint says about a column, and how sure it is.</summary>
    public sealed record Hint(string Description, bool Strong);

    /// <summary>
    /// Tokens that identify a person on their own. A column with one of these is
    /// worth looking at whatever else its name says.
    /// </summary>
    private static readonly Dictionary<string, string> StrongTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssn"] = "Social Security number",
        ["dob"] = "date of birth",
        ["birth"] = "birth details",
        ["birthdate"] = "date of birth",
        ["email"] = "email address",
        ["phone"] = "phone number",
        ["telephone"] = "phone number",
        ["fax"] = "fax number",
        ["address"] = "postal or email address",
        ["street"] = "street address",
        ["zip"] = "postal code",
        ["postcode"] = "postal code",
        ["password"] = "credential",
        ["pwd"] = "credential",
        ["passport"] = "identity document",
    };

    /// <summary>
    /// Tokens worth a look but far weaker on their own. `Name` is the clearest
    /// case: it is the most common column name in the schema measured, and most
    /// of them are the name of a status or a document type, not a person.
    /// </summary>
    private static readonly Dictionary<string, string> WeakTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "may be a person's name",
        ["city"] = "part of an address",
        ["county"] = "part of an address",
        ["note"] = "free text — may contain anything",
        ["notes"] = "free text — may contain anything",
        ["comment"] = "free text — may contain anything",
        ["comments"] = "free text — may contain anything",
        ["remarks"] = "free text — may contain anything",
        ["memo"] = "free text — may contain anything",
        ["gender"] = "personal characteristic",
        ["race"] = "personal characteristic",
        ["ethnicity"] = "personal characteristic",
    };

    /// <summary>
    /// Qualifiers that turn the weak `Name` into a strong signal. `FirstName` is
    /// a person; `StatusName` is not.
    /// </summary>
    private static readonly HashSet<string> NameQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "first", "last", "middle", "full", "maiden", "preferred", "given", "sur", "legal",
    };

    /// <summary>
    /// Two-token combinations that are personal together and innocuous apart.
    /// `License` alone is a category; `LicenseNumber` identifies a practitioner.
    /// </summary>
    private static readonly (string First, string Second, string Description)[] Compounds =
    [
        ("license", "number", "professional license number"),
        ("licence", "number", "professional license number"),
        ("account", "number", "account number"),
        ("card", "number", "payment card number"),
    ];

    /// <summary>
    /// Tokens that mean the column is ABOUT a record rather than about a person:
    /// a key, a category, a tally, a timestamp. `AddressTypeID` is a lookup
    /// reference; `EmailSentDate` is when a message went out.
    /// </summary>
    private static readonly HashSet<string> AboutTheRecord = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "ids", "type", "types", "count", "flag", "status", "code", "sent",
        "log", "key", "sequence", "sort", "order", "version", "format",
    };

    /// <summary>
    /// Leading words that mark a yes/no setting. `SendScoreTransferByEmail` says
    /// HOW to reach someone, never who they are.
    /// </summary>
    private static readonly HashSet<string> SettingPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "should", "send", "use", "is", "has", "can", "allow", "enable", "receive", "prefer",
        "attach", "include", "display", "show", "require", "auto",
    };

    /// <summary>
    /// What this column name suggests, or null if nothing does.
    /// </summary>
    public static Hint? Describe(string columnName)
    {
        var tokens = Tokenize(columnName);

        if (tokens.Count == 0)
        {
            return null;
        }

        // A setting is named for the action, not the subject, so the leading
        // word settles it before anything else is considered.
        if (SettingPrefixes.Contains(tokens[0]))
        {
            return null;
        }

        foreach (var (first, second, description) in Compounds)
        {
            if (tokens.Contains(first, StringComparer.OrdinalIgnoreCase)
                && tokens.Contains(second, StringComparer.OrdinalIgnoreCase))
            {
                return new Hint(description, Strong: true);
            }
        }

        // Checked AFTER compounds so `AccountNumber` survives while `AccountId`
        // does not — the compound is a stronger statement than the suppression.
        if (tokens.Any(AboutTheRecord.Contains))
        {
            return null;
        }

        foreach (var token in tokens)
        {
            if (StrongTokens.TryGetValue(token, out var strong))
            {
                return new Hint(strong, Strong: true);
            }
        }

        if (tokens.Contains("name", StringComparer.OrdinalIgnoreCase)
            && tokens.Any(NameQualifiers.Contains))
        {
            return new Hint("a person's name", Strong: true);
        }

        foreach (var token in tokens)
        {
            if (WeakTokens.TryGetValue(token, out var weak))
            {
                return new Hint(weak, Strong: false);
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a column name into words. Handles the three spellings that turn up
    /// in one real schema: `MailingCity`, `SSNLastFour` (a run of capitals
    /// followed by a word) and `mailing_city`.
    ///
    /// The rule for a capital letter starting a new word: it does, unless it is
    /// inside a run of capitals — and the LAST capital of a run belongs to the
    /// word that follows it, which is what keeps `SSNLastFour` from becoming
    /// `SSNL` + `ast` + `Four`.
    /// </summary>
    internal static List<string> Tokenize(string name)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (!char.IsLetterOrDigit(c))
            {
                Flush(tokens, current);
                continue;
            }

            var startsWord =
                char.IsUpper(c)
                && current.Length > 0
                && (char.IsLower(name[i - 1])
                    || char.IsDigit(name[i - 1])
                    || (i + 1 < name.Length && char.IsLower(name[i + 1])));

            if (startsWord)
            {
                Flush(tokens, current);
            }

            current.Append(c);
        }

        Flush(tokens, current);

        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
            current.Clear();
        }
    }
}
