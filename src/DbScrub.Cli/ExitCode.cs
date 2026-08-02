namespace DbScrub.Cli;

/// <summary>
/// SPEC section 2. CI depends on these (CLAUDE.md), so the numbers are the
/// contract — never renumber, only add.
/// </summary>
internal static class ExitCode
{
    /// <summary>Everything worked.</summary>
    public const int Success = 0;

    /// <summary>Anything not covered by a more specific code.</summary>
    public const int UnexpectedError = 1;

    /// <summary>Verify found PII hits, or `status` found the database unstamped.</summary>
    public const int VerifyFailed = 2;

    /// <summary>Unclassified columns while running in fail mode.</summary>
    public const int Unclassified = 3;

    /// <summary>The safety interlock refused (slice 2).</summary>
    public const int InterlockRefused = 4;

    /// <summary>The config is invalid.</summary>
    public const int ConfigInvalid = 5;
}
