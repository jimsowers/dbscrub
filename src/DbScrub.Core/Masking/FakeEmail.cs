using System.Text.RegularExpressions;

namespace DbScrub.Core.Masking;

/// <summary>
/// The `email` strategy: a distinct, obviously-fake, non-deliverable address per
/// row — <c>fakeemail15@notreal.invalid</c>, where 15 is the row's primary key.
///
/// Why the tool generates this rather than letting the config supply a value:
///
/// A `static` replacement is OPAQUE to the code. `dev@example.invalid` is
/// email-shaped precisely because a config author needed the dev database to
/// keep working — which makes it indistinguishable from a real address by shape
/// alone. The verify gate therefore has to be handed the literal value, and that
/// breaks down the moment the value varies per row.
///
/// A GENERATED address has a shape defined here, in code. The gate can recognise
/// it directly, for a million distinct values, with no list to maintain. That is
/// the whole reason this is a strategy and not a modifier on `static`.
///
/// The domain is the load-bearing part. RFC 2606 reserves the `.invalid` TLD
/// permanently: it can never be registered and never resolves. So
/// <see cref="Looks"/> is not a careful argument about whether a real address
/// might match — nothing real can exist at this domain, full stop. It also means
/// a dev environment that tries to send mail here fails to send it, which is the
/// outcome you want.
/// </summary>
public static class FakeEmail
{
    /// <summary>The local part, before the row's key. Short on purpose — every character costs column width.</summary>
    public const string Prefix = "fakeemail";

    /// <summary>Reserved by RFC 2606. Can never be registered, never resolves, never delivers.</summary>
    public const string Domain = "notreal.invalid";

    /// <summary>
    /// The fixed cost of one generated address, before the key is added. Used by
    /// the planner to refuse a column too narrow to hold one.
    /// </summary>
    public static int OverheadCharacters => Prefix.Length + 1 + Domain.Length;

    private static readonly Regex Shape = new(
        $@"^{Regex.Escape(Prefix)}[0-9A-Za-z\-]+@{Regex.Escape(Domain)}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The address for one row.</summary>
    public static string For(string discriminator) => $"{Prefix}{discriminator}@{Domain}";

    /// <summary>
    /// Whether a value is one this strategy produced. Anchored at both ends, so
    /// a real address that merely CONTAINS the domain — in a free-text note, say
    /// — is not excused.
    /// </summary>
    public static bool Looks(string value) => Shape.IsMatch(value);

    /// <summary>The narrowest column that can hold an address for this key shape.</summary>
    public static int RequiredCharacters(int maxKeyWidth) => OverheadCharacters + maxKeyWidth;
}
