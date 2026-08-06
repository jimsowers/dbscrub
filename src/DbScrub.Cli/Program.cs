using System.CommandLine;
using DbScrub.Core.Execution;
using DbScrub.Core.Schema;
using DbScrub.Core.Stamp;
using DbScrub.Core.Verify;

namespace DbScrub.Cli;

/// <summary>
/// Command wiring only (SPEC section 2). Every option lives here; every
/// decision lives in Core.
///
/// `report` and `status` are read-only. `clean` modifies, and every dependency
/// it needs to do so is injected from here — the schema reader, the stamp
/// reader, the session that owns the writes, and even the console read for the
/// typed confirmation. That is what lets the whole command, including its
/// refusals, be tested without a SQL Server anywhere near it.
///
/// NOT wired: --rename-to and --replace. SPEC section 2 lists them, and they are
/// deliberately absent until step 5, because SPEC 5.5 renames only after a clean
/// verify pass and there is no verify gate yet. An option that silently did
/// nothing would be worse than a "command not recognized".
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

        var reviewAllOption = new Option<bool>("--review-all")
        {
            Description = "List every column with no rule as paste-ready JSON, however many there are. "
                + "Without it a large database is summarised — the counts are identical either way, "
                + "only the listing is held back.",
        };

        var reportCommand = new Command("report",
            "Read-only. Print the schema-vs-config plan and the columns with no rule.")
        {
            serverOption,
            databaseOption,
            configOption,
            reviewAllOption,
        };

        reportCommand.SetAction((parseResult, cancellationToken) => ReportCommand.RunAsync(
            server: parseResult.GetValue(serverOption)!,
            database: parseResult.GetValue(databaseOption)!,
            configPath: parseResult.GetValue(configOption)!,
            readerFactory: connectionString => new SchemaInventory(connectionString),
            output: Console.Out,
            error: Console.Error,
            reviewAll: parseResult.GetValue(reviewAllOption),
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

        var yesOption = new Option<bool>("--yes")
        {
            Description = "Skip the typed confirmation. Allowed only for localhost, ., or 127.0.0.1 "
                + "(SPEC 3.2) — never for a remote server, whatever the allowlist says.",
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the plan and stop. Identical to `report`, but reached through clean's "
                + "own preflight, so it also proves the safety checks would pass.",
        };

        var failOnUnclassifiedOption = new Option<bool>("--fail-on-unclassified")
        {
            Description = "Refuse to run while any column has no verdict, whatever the config says. "
                + "Tightens the config; there is no flag that loosens it.",
        };

        var cleanCommand = new Command("clean",
            "Mask the database in place. The only command that modifies anything.")
        {
            serverOption,
            databaseOption,
            configOption,
            yesOption,
            dryRunOption,
            failOnUnclassifiedOption,
            reviewAllOption,
        };

        cleanCommand.SetAction((parseResult, cancellationToken) => CleanCommand.RunAsync(
            server: parseResult.GetValue(serverOption)!,
            database: parseResult.GetValue(databaseOption)!,
            configPath: parseResult.GetValue(configOption)!,
            yes: parseResult.GetValue(yesOption),
            dryRun: parseResult.GetValue(dryRunOption),
            failOnUnclassified: parseResult.GetValue(failOnUnclassifiedOption),
            schemaReaderFactory: connectionString => new SchemaInventory(connectionString),
            stampReaderFactory: connectionString => new StampReader(connectionString),
            sessionFactory: connectionString => new SqlCleanSession(connectionString),
            verifierFactory: connectionString => new SqlVerifier(connectionString),
            stampWriterFactory: connectionString => new StampWriter(connectionString),
            output: Console.Out,
            error: Console.Error,
            readLine: Console.ReadLine,
            reviewAll: parseResult.GetValue(reviewAllOption),
            cancellationToken: cancellationToken));

        var root = new RootCommand("dbscrub — scrub PII from a locally restored SQL Server database.")
        {
            reportCommand,
            statusCommand,
            cleanCommand,
        };

        return await root.Parse(args).InvokeAsync();
    }
}
