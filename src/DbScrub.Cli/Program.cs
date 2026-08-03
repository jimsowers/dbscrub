using System.CommandLine;
using DbScrub.Core.Schema;
using DbScrub.Core.Stamp;

namespace DbScrub.Cli;

/// <summary>
/// Command wiring only (SPEC section 2). Every option lives here; every
/// decision lives in Core.
///
/// Both commands wired here are READ-ONLY. `clean` is deliberately absent
/// until the steps that make it safe exist — the hygiene pass, the mask engine,
/// and the verify gate. A half-wired `clean` is exactly the accident this tool
/// exists to prevent.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var serverOption = new Option<string>("--server")
        {
            Description = "SQL Server instance, e.g. localhost or localhost\\SQL2022.",
            Required = true,
        };

        var databaseOption = new Option<string>("--database")
        {
            Description = "Database to inspect, e.g. AAVSB.",
            Required = true,
        };

        var configOption = new Option<string>("--config")
        {
            Description = "Path to the masking config JSON.",
            Required = true,
        };

        // `status` accepts --config but does not require it (DECISIONS.md D15),
        // so it needs its own option instance rather than sharing the required
        // one above.
        var optionalConfigOption = new Option<string?>("--config")
        {
            Description = "Optional path to the masking config. Only its allowedServers list is read; "
                + "without it the built-in localhost defaults apply.",
        };

        var reportCommand = new Command("report",
            "Read-only. Print the schema-vs-config plan and every UNCLASSIFIED column.")
        {
            serverOption,
            databaseOption,
            configOption,
        };

        reportCommand.SetAction((parseResult, cancellationToken) => ReportCommand.RunAsync(
            server: parseResult.GetValue(serverOption)!,
            database: parseResult.GetValue(databaseOption)!,
            configPath: parseResult.GetValue(configOption)!,
            readerFactory: connectionString => new SchemaInventory(connectionString),
            output: Console.Out,
            error: Console.Error,
            cancellationToken: cancellationToken));

        var statusCommand = new Command("status",
            "Read-only. Report whether the database carries the Sanitized stamp. Exit 0 stamped, 2 not.")
        {
            serverOption,
            databaseOption,
            optionalConfigOption,
        };

        statusCommand.SetAction((parseResult, cancellationToken) => StatusCommand.RunAsync(
            server: parseResult.GetValue(serverOption)!,
            database: parseResult.GetValue(databaseOption)!,
            configPath: parseResult.GetValue(optionalConfigOption),
            readerFactory: connectionString => new StampReader(connectionString),
            output: Console.Out,
            error: Console.Error,
            cancellationToken: cancellationToken));

        var root = new RootCommand("dbscrub — scrub PII from a locally restored SQL Server database.")
        {
            reportCommand,
            statusCommand,
        };

        return await root.Parse(args).InvokeAsync();
    }
}
