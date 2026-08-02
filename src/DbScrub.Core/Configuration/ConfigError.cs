namespace DbScrub.Core.Configuration;

/// <summary>
/// One problem found in a config file, positioned at the line and column of
/// the offending JSON. The formatted output deliberately matches the
/// `file(line,col): error CODE: message` shape that compilers and editors use,
/// so a terminal that hyperlinks build errors will hyperlink these too.
/// </summary>
/// <param name="Code">A stable DBSnnn code — greppable, and safe to reference from docs.</param>
/// <param name="Path">Dotted path into the document, e.g. tables[0].columns[3].strategy.</param>
/// <param name="Message">What is wrong, in plain English.</param>
/// <param name="Suggestion">Optional: the JSON to write instead. SPEC section 4 wants failures to be helpful, not merely correct.</param>
/// <param name="Line">1-based. 0 when the position could not be resolved.</param>
/// <param name="Column">1-based. 0 when the position could not be resolved.</param>
public sealed record ConfigError(
    string Code,
    string Path,
    string Message,
    string? Suggestion,
    int Line,
    int Column)
{
    /// <summary>Renders the error against a source name, e.g. the config file path.</summary>
    public string Format(string sourceName)
    {
        var position = Line > 0 ? $"({Line},{Column})" : string.Empty;
        var header = $"{sourceName}{position}: error {Code}: {Message}";

        return Suggestion is null ? header : $"{header}{Environment.NewLine}  {Suggestion}";
    }

    public override string ToString() => Format("config");
}

/// <summary>
/// The DBSnnn codes. Constants rather than an enum so they can appear verbatim
/// in messages, docs, and tests, and so `grep DBS006` finds every use.
/// </summary>
public static class ConfigErrorCodes
{
    /// <summary>The file is not well-formed JSON at all.</summary>
    public const string InvalidJson = "DBS001";

    /// <summary>A property name we do not recognize — usually a typo, so we refuse rather than ignore.</summary>
    public const string UnknownProperty = "DBS002";

    /// <summary>A required property is absent.</summary>
    public const string MissingProperty = "DBS003";

    /// <summary>A property is present but is the wrong JSON type (a string where an array belongs).</summary>
    public const string InvalidType = "DBS004";

    /// <summary>Right type, unusable content: an unknown strategy name, a negative batchSize, an empty name.</summary>
    public const string InvalidValue = "DBS005";

    /// <summary>`strategy: "static"` without a `value`.</summary>
    public const string StrategyRequiresValue = "DBS006";

    /// <summary>A `value` on a strategy that ignores it — dead config, which reads as intent that never happens.</summary>
    public const string ValueNotAllowed = "DBS007";

    /// <summary>The same table or column named twice.</summary>
    public const string Duplicate = "DBS008";

    /// <summary>Internally contradictory: truncate plus columns, or a table entry that asks for nothing.</summary>
    public const string Contradictory = "DBS009";
}
