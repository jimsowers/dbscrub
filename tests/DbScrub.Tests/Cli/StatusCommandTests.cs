using DbScrub.Cli;
using DbScrub.Core.Stamp;
using Xunit;

namespace DbScrub.Tests.Cli;

public class StatusCommandTests
{
    [Fact]
    public async Task AStampedDatabaseExitsZero()
    {
        var result = await RunAsync(new SanitizationStatus(
            IsSanitized: true,
            SanitizedUtc: new DateTime(2026, 8, 2, 18, 30, 0, DateTimeKind.Utc),
            ConfigHash: "9f2b",
            ToolVersion: "0.1.0"));

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("SANITIZED", result.Output);
        Assert.Contains("2026-08-02 18:30:00 UTC", result.Output);
    }

    [Fact]
    public async Task AnUnstampedDatabaseExitsTwo()
    {
        var result = await RunAsync(SanitizationStatus.NotSanitized);

        Assert.Equal(ExitCode.VerifyFailed, result.ExitCode);
        Assert.Contains("NOT SANITIZED", result.Output);
        Assert.Contains("may contain real personal data", result.Output);
    }

    [Fact]
    public async Task WithoutAConfigTheBuiltInLocalDefaultsApply()
    {
        var result = await RunAsync(SanitizationStatus.NotSanitized, server: "localhost");

        // Reached the reader, so the allowlist let it through.
        Assert.Equal(ExitCode.VerifyFailed, result.ExitCode);
    }

    [Fact]
    public async Task WithoutAConfigANamedInstanceIsRefused()
    {
        // The consequence of the strict rule, stated as a test so it is a
        // decision rather than a surprise: this dev box has only
        // localhost\MSSQLSERVER02, so `status` needs a config naming it.
        var result = await RunAsync(SanitizationStatus.NotSanitized, server: @"localhost\MSSQLSERVER02");

        Assert.Equal(ExitCode.SafetyCheckRefused, result.ExitCode);
        Assert.Contains("Refusing to connect", result.Error);
        Assert.Contains("built-in defaults", result.Error);
    }

    [Fact]
    public async Task ANonLocalServerIsRefusedBeforeAnyConnectionIsOpened()
    {
        var reader = new ThrowingStampReader();

        var result = await RunAsync(reader, server: "prod-sql-01", configPath: null);

        Assert.Equal(ExitCode.SafetyCheckRefused, result.ExitCode);
        Assert.False(reader.WasCalled, "The safety checks must refuse BEFORE the reader is constructed or used.");
    }

    [Fact]
    public async Task AFailingReadExitsOne()
    {
        var result = await RunAsync(new ThrowingStampReader(), server: "localhost", configPath: null);

        Assert.Equal(ExitCode.UnexpectedError, result.ExitCode);
        Assert.Contains("Could not read the sanitization stamp", result.Error);
    }

    // ---- harness ------------------------------------------------------------

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private sealed class FakeStampReader(SanitizationStatus status) : IStampReader
    {
        public Task<SanitizationStatus> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);
    }

    private sealed class ThrowingStampReader : IStampReader
    {
        public bool WasCalled { get; private set; }

        public Task<SanitizationStatus> ReadAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("connection refused");
        }
    }

    private static Task<RunResult> RunAsync(SanitizationStatus status, string server = "localhost") =>
        RunAsync(new FakeStampReader(status), server, configPath: null);

    private static async Task<RunResult> RunAsync(IStampReader reader, string server, string? configPath)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            server: server,
            database: "DbScrubTest",
            configPath: configPath,
            readerFactory: _ => reader,
            output: output,
            error: error);

        return new RunResult(exitCode, output.ToString(), error.ToString());
    }
}
