using DbScrub.Core.Configuration;
using DbScrub.Core.Safety;
using Xunit;

namespace DbScrub.Tests.Safety;

/// <summary>
/// The allowlist is the difference between "scrubbed my laptop copy" and "an
/// incident". These tests exist as much to stop a future session relaxing the
/// rules as to prove they work today.
/// </summary>
public class ServerAllowlistTests
{
    private static readonly IReadOnlyList<string> Defaults = ConfigDefaults.DefaultAllowedServers;

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData(".")]
    [InlineData("(local)")]
    [InlineData("127.0.0.1")]
    [InlineData("  localhost  ")]
    public void TheDefaultLocalNamesAreAllowed(string server) =>
        Assert.True(ServerAllowlist.IsAllowed(server, Defaults));

    [Theory]
    [InlineData("prod-sql-01")]
    [InlineData("10.0.0.5")]
    [InlineData("localhost.evil.example")]   // starts with localhost, is not localhost
    [InlineData("notlocalhost")]
    [InlineData("localhost2")]
    [InlineData("")]
    [InlineData("   ")]
    public void EverythingElseIsRefused(string server) =>
        Assert.False(ServerAllowlist.IsAllowed(server, Defaults));

    [Fact]
    public void ANamedInstanceIsNotCoveredByTheBareHostname()
    {
        // The rule that bit this project: SPEC section 3.1 says a named
        // instance counts only if spelled out. The dev machine has ONLY
        // localhost\MSSQLSERVER02 and no default instance, so the defaults
        // refuse it — deliberately.
        Assert.False(ServerAllowlist.IsAllowed(@"localhost\MSSQLSERVER02", Defaults));

        Assert.True(ServerAllowlist.IsAllowed(
            @"localhost\MSSQLSERVER02", [.. Defaults, @"localhost\MSSQLSERVER02"]));
    }

    [Fact]
    public void MatchingIsExactNotPrefix()
    {
        // A hosts entry, an SSH tunnel, or a SQL Server alias can all make a
        // localhost-shaped name resolve elsewhere. Prefix matching would let
        // every one of them through.
        IReadOnlyList<string> allowed = ["localhost"];

        Assert.False(ServerAllowlist.IsAllowed(@"localhost\SQL2022", allowed));
        Assert.False(ServerAllowlist.IsAllowed("localhost,1433", allowed));
        Assert.False(ServerAllowlist.IsAllowed("localhost.corp.example", allowed));
    }

    [Fact]
    public void AnEmptyAllowlistAllowsNothing()
    {
        // The config loader already rejects an empty list (DBS005), so this is
        // belt-and-braces: if one ever reaches here, it must fail closed.
        Assert.False(ServerAllowlist.IsAllowed("localhost", []));
    }

    // ---- the narrower rule for unattended runs -----------------------------

    [Theory]
    [InlineData("localhost")]
    [InlineData(".")]
    [InlineData("127.0.0.1")]
    public void UnattendedConfirmationIsAllowedForTheUnambiguousLocalNames(string server) =>
        Assert.True(ServerAllowlist.AllowsUnattendedConfirmation(server));

    [Theory]
    [InlineData("(local)")]                    // on the default allowlist, still not --yes-able
    [InlineData("127.0.0.2")]
    [InlineData("prod-sql-01")]
    [InlineData("localhost.corp.example")]     // host is NOT localhost
    [InlineData("notlocalhost")]
    [InlineData(@"10.0.0.5\SQL2022")]          // named instance on another host
    [InlineData(@"prod-sql-01\localhost")]     // "localhost" in the instance part proves nothing
    public void UnattendedConfirmationIsNarrowerThanTheAllowlist(string server)
    {
        // SPEC section 3.2: --yes may skip typed confirmation only for the
        // unambiguously-local names. Being allowlisted is not enough.
        Assert.False(ServerAllowlist.AllowsUnattendedConfirmation(server));
    }

    [Theory]
    [InlineData(@"localhost\MSSQLSERVER02")]
    [InlineData(@"localhost\SQL2022")]
    [InlineData(@".\SQLEXPRESS")]
    [InlineData(@"127.0.0.1\SQL2022")]
    [InlineData(@"LOCALHOST\mssqlserver02")]
    public void ANamedInstanceOnALocalHostMayUseYes(string server)
    {
        // DECISIONS.md D18. SPEC 3.2 listed three literal strings, which by
        // accident banned every named instance — including the only instance on
        // this dev machine, making `clean` interactive-only and breaking the
        // restore-then-scrub wrapper. The intent was "unambiguously this
        // machine", and the host portion is what carries that.
        Assert.True(ServerAllowlist.AllowsUnattendedConfirmation(server));
    }

    [Fact]
    public void TheDefaultAllowlistContainsAServerThatCannotUseYes()
    {
        // Guards the relationship rather than the two lists separately: if a
        // future edit collapses them into one list, this fails.
        Assert.Contains("(local)", Defaults);
        Assert.False(ServerAllowlist.AllowsUnattendedConfirmation("(local)"));
    }

    // ---- the refusal message -----------------------------------------------

    [Fact]
    public void TheRefusalSaysWhatWasRejectedWhatIsAllowedAndHowToChangeIt()
    {
        var message = ServerAllowlist.DescribeRefusal("prod-sql-01", Defaults, "config/aavsb.json");

        Assert.Contains("prod-sql-01", message);
        Assert.Contains("localhost", message);
        Assert.Contains("config/aavsb.json", message);
        Assert.Contains("no override flag", message);
    }

    [Fact]
    public void TheRefusalHandlesAnEmptyAllowlistWithoutLookingBroken()
    {
        var message = ServerAllowlist.DescribeRefusal("prod-sql-01", [], "config/aavsb.json");

        Assert.Contains("the list is empty", message);
    }
}
