using System.CommandLine;
using DbScrub.Core.Schema;

namespace DbScrub.Cli;

/// <summary>
/// Command wiring only (SPEC section 2). Every option lives here; every
/// decision lives in Core.
///
/// Slice 1 wires `report`. `clean` and `status` are deliberately absent —
/// CLAUDE.md's slice order puts the safety interlock before anything that can
/// mutate a database, and a half-wired `clean` is exactly the accident this
/// tool exists to prevent.
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

        var root = new RootCommand("dbscrub — scrub PII from a locally restored SQL Server database.")
        {
            reportCommand,
        };

        return await root.Parse(args).InvokeAsync();
    }
}
