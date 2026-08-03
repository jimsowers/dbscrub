namespace DbScrub.Core.Safety;

/// <summary>
/// The first half of the safety checks (SPEC section 3.1): refuse to talk to
/// any server that is not on the config's list.
///
/// Two properties are deliberate and must not be "improved":
///
/// 1. **Exact match, not prefix match.** `localhost\SQL2022` is allowed only if
///    that exact string is listed. It is tempting to accept anything starting
///    with "localhost" — resist it. A hosts-file entry, an SSH tunnel, or a
///    SQL Server alias can all make a localhost-looking name resolve somewhere
///    else entirely. The entire value of this check is that it is boring and
///    literal.
///
/// 2. **There is no override flag.** Not a hidden one, not an environment
///    variable. If someone needs another server they edit the config, which
///    lives in source control and shows up in a diff (SPEC section 3.1). A
///    flag would be typed in anger at 6pm; a config change gets reviewed.
/// </summary>
public static class ServerAllowlist
{
    /// <summary>
    /// The servers that may skip typed confirmation via `--yes` (SPEC section
    /// 3.2). Note this is NARROWER than the default allowlist: `(local)` is
    /// absent on purpose, and no named instance qualifies however it is
    /// spelled. Unattended runs are for the unambiguous local case only.
    /// </summary>
    private static readonly string[] UnattendedSafeServers = ["localhost", ".", "127.0.0.1"];

    /// <summary>
    /// Whether <paramref name="server"/> appears in <paramref name="allowedServers"/>.
    /// Case-insensitive, since SQL Server names are; whitespace-trimmed, since a
    /// stray space in a config or a shell argument is a typo, not intent.
    /// </summary>
    public static bool IsAllowed(string server, IReadOnlyList<string> allowedServers)
    {
        var candidate = Normalize(server);

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (var allowed in allowedServers)
        {
            if (string.Equals(candidate, Normalize(allowed), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether `--yes` may skip typed confirmation for this server. Being on
    /// the allowlist is NOT enough — see <see cref="UnattendedSafeServers"/>.
    /// </summary>
    public static bool AllowsUnattendedConfirmation(string server)
    {
        var candidate = Normalize(server);

        return UnattendedSafeServers.Any(s =>
            string.Equals(candidate, s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The refusal text. Says what was rejected, what would be accepted, and
    /// how to change it — a refusal the reader cannot act on just becomes a
    /// reason to go looking for a way around the tool.
    /// </summary>
    public static string DescribeRefusal(string server, IReadOnlyList<string> allowedServers, string configPath)
    {
        var listed = allowedServers.Count == 0
            ? "    (the list is empty)"
            : string.Join(Environment.NewLine, allowedServers.Select(s => $"    {s}"));

        return $"""
            Refusing to connect to '{server}'.

            dbscrub only talks to servers listed in "defaults.allowedServers".
            Allowed by {configPath}:
            {listed}

            There is no override flag. If this server is genuinely a local,
            disposable copy, add it to the config — that change is reviewable.

            Note that matching is exact: a named instance must be spelled out in
            full, e.g. "localhost\\SQL2022". "localhost" does not cover it.
            """;
    }

    private static string Normalize(string value) => value.Trim();
}
