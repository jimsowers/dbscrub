using DbScrub.Core.Configuration;
using DbScrub.Core.Planning;
using DbScrub.Core.Reporting;
using DbScrub.Core.Safety;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;

namespace DbScrub.Cli;

/// <summary>
/// The body of `dbscrub report` (SPEC section 2), separated from the
/// System.CommandLine wiring so it can be tested with a fake schema reader and
/// a TextWriter instead of a console and a database.
/// </summary>
internal static class ReportCommand
{
    /// <summary>
    /// Read-only. Loads the config, reads the schema, resolves verdicts, prints
    /// the plan, and returns the exit code.
    /// </summary>
    /// <param name="readerFactory">
    /// Given a connection string, produces the schema reader. Injected so tests
    /// never open a socket.
    /// </param>
    public static async Task<int> RunAsync(
        string server,
        string database,
        string configPath,
        Func<string, ISchemaReader> readerFactory,
        TextWriter output,
        TextWriter error,
        bool reviewAll = false,
        CancellationToken cancellationToken = default)
    {
        MaskingConfig config;
        try
        {
            config = MaskingConfigLoader.LoadFile(configPath);
        }
        catch (ConfigInvalidException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.ConfigInvalid;
        }
        catch (IOException ex)
        {
            error.WriteLine($"Could not read config file '{configPath}': {ex.Message}");
            return ExitCode.ConfigInvalid;
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine($"Could not read config file '{configPath}': {ex.Message}");
            return ExitCode.ConfigInvalid;
        }

        // The allowlist gates `report` too, even though it only reads
        // (DECISIONS.md D14). It still opens a connection, and a rule with an
        // exception is a rule people have to remember.
        if (!ServerAllowlist.IsAllowed(server, config.Defaults.AllowedServers))
        {
            error.WriteLine(ServerAllowlist.DescribeRefusal(
                server, config.Defaults.AllowedServers, configPath));
            return ExitCode.SafetyCheckRefused;
        }

        DatabaseSchema schema;
        try
        {
            var connectionString = SchemaInventory.BuildConnectionString(server, database);
            schema = await readerFactory(connectionString).ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The message can name the server and database — neither is PII —
            // but never the exception's data payload.
            error.WriteLine($"Could not read the schema of '{database}' on '{server}'.");
            error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            return ExitCode.UnexpectedError;
        }

        var plan = CleanPlan.Build(VerdictResolver.Resolve(schema, config));
        output.Write(PlanReport.Render(plan, server, configPath, reviewAll));

        return ResolveExitCode(plan, config, error);
    }

    /// <summary>
    /// Report is read-only, so it cannot "fail" in the way clean does. It still
    /// carries a meaningful exit code, because a report that always returns 0
    /// cannot serve as the CI gate that DECISIONS.md D6 wants once the
    /// inventory is complete.
    /// </summary>
    private static int ResolveExitCode(CleanPlan plan, MaskingConfig config, TextWriter error)
    {
        if (plan.Problems.Count > 0)
        {
            error.WriteLine($"{plan.Problems.Count} config problem(s) would block `clean`. See Problems above.");
            return ExitCode.ConfigInvalid;
        }

        if (config.Defaults.UnclassifiedColumns == UnclassifiedMode.Fail && !plan.Scrub.IsFullyClassified)
        {
            error.WriteLine(
                $"{plan.Scrub.Unclassified.Count} unclassified column(s) and unclassifiedColumns is \"fail\".");
            return ExitCode.Unclassified;
        }

        return ExitCode.Success;
    }
}
