using DbScrub.Core.Configuration;
using DbScrub.Core.Safety;
using DbScrub.Core.Schema;
using DbScrub.Core.Stamp;

namespace DbScrub.Cli;

/// <summary>
/// `dbscrub status` (SPEC section 2) — answers "is the copy in front of me
/// clean?" in one command.
///
/// This command exists because DECISIONS.md D10 made cleaning happen in place.
/// A raw restore and a scrubbed copy are both named AAVSB, so the name tells
/// you nothing and the stamp is the only signal. Exit 0 stamped, 2 unstamped,
/// so a script or an MSBuild target can branch on it.
///
/// Read-only, and unlike `report` it needs no config to do its job — see the
/// note on the config parameter.
/// </summary>
internal static class StatusCommand
{
    public static async Task<int> RunAsync(
        string server,
        string database,
        string? configPath,
        Func<string, IStampReader> readerFactory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        // SPEC section 2 gives `status` no --config. But the allowlist lives in
        // the config, and gating every command is DECISIONS.md D14. Resolution:
        // --config is OPTIONAL here. Without it the built-in defaults apply,
        // which covers a plain localhost box; a named instance needs a config
        // naming it, exactly as `report` and `clean` do (DECISIONS.md D15).
        IReadOnlyList<string> allowedServers;
        var allowlistSource = "the built-in defaults";

        if (configPath is not null)
        {
            try
            {
                allowedServers = MaskingConfigLoader.LoadFile(configPath).Defaults.AllowedServers;
                allowlistSource = configPath;
            }
            catch (ConfigInvalidException ex)
            {
                error.WriteLine(ex.Message);
                return ExitCode.ConfigInvalid;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Could not read config file '{configPath}': {ex.Message}");
                return ExitCode.ConfigInvalid;
            }
        }
        else
        {
            allowedServers = ConfigDefaults.DefaultAllowedServers;
        }

        if (!ServerAllowlist.IsAllowed(server, allowedServers))
        {
            error.WriteLine(ServerAllowlist.DescribeRefusal(server, allowedServers, allowlistSource));
            return ExitCode.SafetyCheckRefused;
        }

        SanitizationStatus status;
        try
        {
            var connectionString = SchemaInventory.BuildConnectionString(server, database);
            status = await readerFactory(connectionString).ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not read the sanitization stamp of '{database}' on '{server}'.");
            error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            return ExitCode.UnexpectedError;
        }

        output.Write(Describe(server, database, status));

        // Exit 2 doubles as "verify failed" elsewhere (SPEC section 2). Both
        // mean the same thing to a caller: this database is not safe to use.
        return status.IsSanitized ? ExitCode.Success : ExitCode.VerifyFailed;
    }

    private static string Describe(string server, string database, SanitizationStatus status)
    {
        var lines = new List<string>
        {
            $"  Server    {server}",
            $"  Database  {database}",
            string.Empty,
        };

        if (!status.IsSanitized)
        {
            lines.Add("  NOT SANITIZED - this database may contain real personal data.");
            lines.Add(string.Empty);
            lines.Add("  Run `dbscrub clean` before using it for development.");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        lines.Add("  SANITIZED");
        lines.Add($"    Cleaned    {Format(status.SanitizedUtc)}");
        lines.Add($"    Config     {status.ConfigHash ?? "(unknown)"}");
        lines.Add($"    Tool       {status.ToolVersion ?? "(unknown)"}");
        lines.Add(string.Empty);

        // The stamp says "clean according to THAT config". If the config has
        // moved on, the claim is weaker than it looks — worth saying out loud
        // once the compare exists (it needs the current config, which status
        // may not have been given).
        lines.Add("  The stamp records the config used. A config edited since then may");
        lines.Add("  classify columns this run never touched.");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Format(DateTime? utc) =>
        utc is null ? "(unknown)" : $"{utc:yyyy-MM-dd HH:mm:ss} UTC";
}
