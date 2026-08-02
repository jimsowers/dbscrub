namespace DbScrub.Core.Configuration;

/// <summary>
/// Thrown when a config cannot be loaded. Carries every error found, not just
/// the first: fixing a config one error per run is a miserable loop, and the
/// validator has already done the work of finding them all.
///
/// The CLI maps this to exit code 5 (SPEC section 2).
/// </summary>
public sealed class ConfigInvalidException : Exception
{
    public ConfigInvalidException(string sourceName, IReadOnlyList<ConfigError> errors)
        : base(BuildMessage(sourceName, errors))
    {
        SourceName = sourceName;
        Errors = errors;
    }

    /// <summary>The config file path (or a test-supplied label).</summary>
    public string SourceName { get; }

    public IReadOnlyList<ConfigError> Errors { get; }

    private static string BuildMessage(string sourceName, IReadOnlyList<ConfigError> errors)
    {
        var lines = errors.Select(e => e.Format(sourceName));
        var summary = errors.Count == 1 ? "1 error" : $"{errors.Count} errors";

        return string.Join(Environment.NewLine, lines)
            + Environment.NewLine
            + $"Config is invalid ({summary}).";
    }
}
