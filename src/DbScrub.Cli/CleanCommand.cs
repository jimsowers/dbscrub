using DbScrub.Core.Configuration;
using DbScrub.Core.Execution;
using DbScrub.Core.Planning;
using DbScrub.Core.Reporting;
using DbScrub.Core.Safety;
using DbScrub.Core.Schema;
using DbScrub.Core.Stamp;
using DbScrub.Core.Verdicts;
using DbScrub.Core.Verify;

namespace DbScrub.Cli;

/// <summary>
/// `dbscrub clean` (SPEC section 2) — the only command that modifies anything.
///
/// The order below is the safety design, and every step before the first
/// mutation is there to make the run refusable:
///
///   1. config loads, or exit 5
///   2. server is on the allowlist, or exit 4        (SPEC 3.1, no override flag)
///   3. database is not already stamped, or exit 0    (SPEC 3.3, idempotence)
///   4. the plan resolves with no problems, or exit 5
///   5. unclassified columns, in fail mode, or exit 3 (SPEC section 4)
///   6. the plan is printed in full
///   7. the operator types the database name, or exit 4 (SPEC 3.2)
///   ---- only now does anything change ----
///   8. hygiene, mask, reattach                       (SPEC 5.1–5.3)
///
/// What this command does NOT do yet: verify, stamp, rename, repair users.
/// Those are step 5, and the order matters — CLAUDE.md allows a stamp only after
/// a clean verify pass. Until the verify gate exists, a finished `clean` leaves
/// an UNSTAMPED database, and `dbscrub status` keeps reporting it as unsafe.
/// That is the honest state of the tool, and it is why this command says so in
/// its closing summary rather than quietly exiting 0 as if it were finished.
/// </summary>
internal static class CleanCommand
{
    public static async Task<int> RunAsync(
        string server,
        string database,
        string configPath,
        bool yes,
        bool dryRun,
        bool failOnUnclassified,
        Func<string, ISchemaReader> schemaReaderFactory,
        Func<string, IStampReader> stampReaderFactory,
        Func<string, ICleanSession> sessionFactory,
        Func<string, IVerifier> verifierFactory,
        Func<string, IStampWriter> stampWriterFactory,
        TextWriter output,
        TextWriter error,
        Func<string?> readLine,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        // ---- 1. config -----------------------------------------------------

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not read config file '{configPath}': {ex.Message}");
            return ExitCode.ConfigInvalid;
        }

        // ---- 2. server allowlist (SPEC 3.1) --------------------------------

        // Before any socket is opened. There is no override flag and there is no
        // exception for any command (DECISIONS.md D14).
        if (!ServerAllowlist.IsAllowed(server, config.Defaults.AllowedServers))
        {
            error.WriteLine(ServerAllowlist.DescribeRefusal(
                server, config.Defaults.AllowedServers, configPath));
            return ExitCode.SafetyCheckRefused;
        }

        var connectionString = SchemaInventory.BuildConnectionString(server, database);

        // ---- 3. refuse an already-stamped database (SPEC 3.3) --------------

        SanitizationStatus status;
        try
        {
            status = await stampReaderFactory(connectionString).ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not read the sanitization stamp of '{database}' on '{server}'.");
            error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            return ExitCode.UnexpectedError;
        }

        if (status.IsSanitized)
        {
            // Exit 0, not an error. Running clean twice is a mistake with no
            // consequence, and a wrapper script that re-runs it should carry on.
            output.WriteLine($"'{database}' is already sanitized — nothing to do.");
            output.WriteLine($"  Cleaned {Format(status.SanitizedUtc)} by {status.ToolVersion ?? "(unknown)"}.");
            output.WriteLine("  Restore a fresh copy first if you meant to clean it again.");
            return ExitCode.Success;
        }

        // ---- 4. the plan ---------------------------------------------------

        DatabaseSchema schema;
        try
        {
            schema = await schemaReaderFactory(connectionString).ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not read the schema of '{database}' on '{server}'.");
            error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            return ExitCode.UnexpectedError;
        }

        var plan = CleanPlan.Build(VerdictResolver.Resolve(schema, config));

        output.Write(PlanReport.Render(plan, server, configPath));
        output.WriteLine();

        if (!plan.CanRun)
        {
            error.WriteLine(
                $"{plan.Problems.Count} problem(s) block this run. Nothing was modified. See Problems above.");
            return ExitCode.ConfigInvalid;
        }

        // ---- 5. unclassified columns (SPEC section 4) ----------------------

        // The CLI flag can only ever tighten the config, never loosen it —
        // there is deliberately no --allow-unclassified.
        var mode = failOnUnclassified ? UnclassifiedMode.Fail : config.Defaults.UnclassifiedColumns;

        if (mode == UnclassifiedMode.Fail && !plan.Scrub.IsFullyClassified)
        {
            error.WriteLine($"{plan.Scrub.Unclassified.Count} unclassified column(s) and unclassified "
                + "columns are set to fail. Nothing was modified.");
            return ExitCode.Unclassified;
        }

        // ---- 6. dry run ----------------------------------------------------

        if (dryRun)
        {
            output.WriteLine("--dry-run: the plan above is what would run. Nothing was modified.");
            return ExitCode.Success;
        }

        // ---- 7. typed confirmation (SPEC 3.2) ------------------------------

        if (!Confirm(plan, server, database, yes, output, error, readLine))
        {
            return ExitCode.SafetyCheckRefused;
        }

        // ---- 8. execute ----------------------------------------------------

        output.WriteLine();

        // Hashed from the file's bytes as they are on disk, so the stamp records
        // the config a human could go and look at (SPEC 5.5).
        var options = new CleanOptions(
            BatchSize: config.Defaults.BatchSize,
            ConfigHash: StampRecord.HashConfigFile(configPath),
            ToolVersion: StampRecord.CurrentToolVersion,
            Clock: clock ?? TimeProvider.System);

        CleanOutcome outcome;
        await using (var session = sessionFactory(connectionString))
        {
            outcome = await CleanRunner.RunAsync(
                plan,
                options,
                session,
                verifierFactory(connectionString),
                stampWriterFactory(connectionString),
                new Progress<string>(message => output.WriteLine($"  {message}")),
                cancellationToken);
        }

        output.Write(Summarize(outcome));

        return ReportOutcome(outcome, database, output, error);
    }

    /// <summary>
    /// SPEC 3.2. The server allowlist catches the wrong MACHINE; this catches the
    /// wrong DATABASE on a machine that is legitimately allowed.
    /// </summary>
    private static bool Confirm(
        CleanPlan plan,
        string server,
        string database,
        bool yes,
        TextWriter output,
        TextWriter error,
        Func<string?> readLine)
    {
        output.Write(TypedConfirmation.BuildSummary(plan.Scrub, server, renameTo: null));
        output.WriteLine();

        if (yes)
        {
            // `--yes` is narrower than the allowlist: only a server that is
            // unambiguously this machine may skip a human (SPEC 3.2, D18).
            // `(local)` is allowlisted by default and still cannot use it.
            if (!ServerAllowlist.AllowsUnattendedConfirmation(server))
            {
                error.WriteLine($"--yes is not allowed for server '{server}'. It may only skip confirmation "
                    + "for localhost, ., or 127.0.0.1 (with or without an instance name).");
                error.WriteLine("Run without --yes and type the database name. Nothing was modified.");
                return false;
            }

            output.WriteLine($"--yes: proceeding without confirmation on {server}.");
            return true;
        }

        output.Write(TypedConfirmation.BuildPrompt(database));
        output.Flush();

        // Compared against the name the CALLER asked for, not the one the server
        // reported. They are the same in every normal run; if they ever differ,
        // agreeing to what you typed is the safer of the two.
        if (!TypedConfirmation.Matches(readLine(), database))
        {
            output.WriteLine();
            error.WriteLine(TypedConfirmation.BuildRefusal(database));
            return false;
        }

        return true;
    }

    private static string Summarize(CleanOutcome outcome)
    {
        var lines = new List<string>
        {
            string.Empty,
            "Done",
            $"  Hygiene statements  {outcome.HygieneCompleted.Count}",
            $"  Tables masked       {outcome.Masked.Count}",
            $"  Rows rewritten      {outcome.RowsUpdated:N0}",
        };

        foreach (var table in outcome.Masked)
        {
            lines.Add($"    {table}");
        }

        if (outcome.Verify is { } verify)
        {
            lines.Add(string.Empty);
            lines.Add("Verify");
            lines.Add($"  String columns swept  {verify.ColumnsScanned}");
            lines.Add($"  Values inspected      {verify.RowsInspected:N0}");
            lines.Add($"  Result                {(verify.Passed ? "PASS" : $"FAIL — {verify.TotalHits:N0} hit(s)")}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// Turns what happened into an exit code and the sentence that explains it.
    ///
    /// The success path deliberately does NOT claim the database is clean. It is
    /// masked but unstamped, because the verify gate that earns a stamp is step 5
    /// and does not exist yet — and a tool that says "done" about a database
    /// nothing has checked is the failure this whole design is arranged against.
    /// </summary>
    private static int ReportOutcome(CleanOutcome outcome, string database, TextWriter output, TextWriter error)
    {
        if (outcome.ReattachFailure is not null)
        {
            // The loudest failure available. Versioning is off on at least one
            // table, which nothing about the database looks wrong about.
            error.WriteLine();
            error.WriteLine("SYSTEM VERSIONING WAS NOT RE-ENABLED on at least one table.");
            error.WriteLine($"  {outcome.ReattachFailure.GetType().Name}: {outcome.ReattachFailure.Message}");
            error.WriteLine("Re-enable it by hand before using this database — until you do, it silently");
            error.WriteLine("records no history. The statements are in the \"After masking\" section above.");
            return ExitCode.UnexpectedError;
        }

        if (outcome.Failure is not null)
        {
            error.WriteLine();
            error.WriteLine($"clean FAILED partway through. '{database}' is partly masked and NOT safe to use.");
            error.WriteLine($"  {outcome.Failure.GetType().Name}: {outcome.Failure.Message}");
            error.WriteLine("Restore a fresh copy and run again.");
            return ExitCode.UnexpectedError;
        }

        if (outcome.Incomplete.Count > 0)
        {
            // Rows the walk did not visit still hold their original values. This
            // is the check that turns a silent batching bug into a failed run.
            error.WriteLine();
            error.WriteLine("Some tables rewrote fewer rows than they contain, so rows were LEFT UNMASKED:");

            foreach (var table in outcome.Incomplete)
            {
                error.WriteLine($"  {table}");
            }

            error.WriteLine("Restore a fresh copy and run again. Do not use this database.");
            return ExitCode.UnexpectedError;
        }

        if (outcome.Verify is { Passed: false } verify)
        {
            // SPEC 5.4: the gate. Hits mean no stamp and no rename, and the
            // report names the column and the shape but NEVER the value — the
            // whole premise of this branch is that the database may still hold
            // real personal data.
            error.WriteLine();
            error.WriteLine($"VERIFY FAILED. '{database}' still holds values that look like personal data,");
            error.WriteLine("so it has NOT been stamped and is NOT safe to use.");
            error.WriteLine();

            var width = verify.Hits.Max(h => h.QualifiedColumn.Length);
            foreach (var hit in verify.Hits.OrderByDescending(h => h.Count))
            {
                error.WriteLine($"  {hit.QualifiedColumn.PadRight(width)}  {hit.Pattern,-13} {hit.Count:N0} value(s)");
            }

            error.WriteLine();
            error.WriteLine("Each of these columns needs a strategy in your config. Add them, restore a");
            error.WriteLine("fresh copy, and run again — the values above were not modified by this run.");

            return ExitCode.VerifyFailed;
        }

        output.WriteLine();
        output.WriteLine($"'{database}' is sanitized.");
        output.WriteLine("  Verify swept every string column and found nothing. The stamp is written,");
        output.WriteLine("  so `dbscrub status` will now report this database as SANITIZED.");

        return ExitCode.Success;
    }

    private static string Format(DateTime? utc) =>
        utc is null ? "(date unknown)" : $"{utc:yyyy-MM-dd HH:mm:ss} UTC";
}
