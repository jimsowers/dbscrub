namespace DbScrub.Core.Stamp;

/// <summary>
/// Whether a database carries the `Sanitized` stamp, and what it says
/// (SPEC section 5.5).
///
/// This is the clean/dirty signal for the whole tool. Since DECISIONS.md D10
/// made cleaning happen in place, the database NAME no longer tells you
/// anything — a raw restore and a scrubbed copy are both called AAVSB. The
/// stamp is the only thing that distinguishes them, which is why `status`
/// exists and why the stamp is written last, after verify passes.
/// </summary>
/// <param name="ConfigHash">
/// SHA-256 of the config file used. Lets a future run notice that the config
/// changed since the database was cleaned — the stamp says "clean according to
/// THAT config", which is a weaker claim than "clean".
/// </param>
public sealed record SanitizationStatus(
    bool IsSanitized,
    DateTime? SanitizedUtc,
    string? ConfigHash,
    string? ToolVersion)
{
    /// <summary>What an unstamped database looks like. `status` exits 2 on this.</summary>
    public static SanitizationStatus NotSanitized { get; } =
        new(IsSanitized: false, SanitizedUtc: null, ConfigHash: null, ToolVersion: null);

    /// <summary>The extended-property names written at the database level.</summary>
    public static class PropertyNames
    {
        public const string Sanitized = "Sanitized";
        public const string SanitizedUtc = "SanitizedUtc";
        public const string ConfigHash = "ConfigHash";
        public const string ToolVersion = "ToolVersion";

        public static readonly string[] All = [Sanitized, SanitizedUtc, ConfigHash, ToolVersion];
    }
}
