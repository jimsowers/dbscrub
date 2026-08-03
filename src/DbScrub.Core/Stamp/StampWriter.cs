using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Stamp;

/// <summary>Writes the sanitization stamp. An interface so `clean` can be tested without a server.</summary>
public interface IStampWriter
{
    Task WriteAsync(StampRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the stamp that says a database has been cleaned (SPEC 5.5).
///
/// This is the most consequential write the tool makes, and not because of what
/// it costs to undo. Since DECISIONS.md D10 removed the naming distinction, the
/// stamp is the ONLY thing separating a scrubbed copy from a raw one — `status`
/// reads it, and the read-only Guard in SPEC section 8 eventually will too. A
/// stamp on a database that was not actually cleaned makes every one of those
/// answers confidently wrong.
///
/// So it is written in exactly one place, called from exactly one place, and
/// only after the verify gate has passed (CLAUDE.md).
///
/// Two records, deliberately:
///   * Database-level extended properties — the fast check. No table needs to
///     exist and no elevated rights are needed to read them, which is what lets
///     a consuming application's own low-privilege login ask the question
///     (DECISIONS.md D16).
///   * A row in dbo.__SanitizationLog — the audit trail. Says what this run
///     actually did, and survives being overwritten by the next one.
/// </summary>
public sealed class StampWriter(string connectionString) : IStampWriter
{
    private const int NoTimeout = 0;

    private const string CreateLogTableSql = """
        IF OBJECT_ID(N'dbo.__SanitizationLog', N'U') IS NULL
        CREATE TABLE dbo.__SanitizationLog
        (
            SanitizationLogId int IDENTITY(1,1) NOT NULL
                CONSTRAINT PK___SanitizationLog PRIMARY KEY,
            RunUtc            datetime2(3)  NOT NULL,
            ToolVersion       nvarchar(64)  NOT NULL,
            ConfigHash        nvarchar(64)  NOT NULL,
            TablesTouched     int           NOT NULL,
            RowsUpdated       bigint        NOT NULL,
            DurationSeconds   decimal(18,2) NOT NULL
        );
        """;

    private const string InsertLogRowSql = """
        INSERT INTO dbo.__SanitizationLog
            (RunUtc, ToolVersion, ConfigHash, TablesTouched, RowsUpdated, DurationSeconds)
        VALUES
            (@runUtc, @toolVersion, @configHash, @tablesTouched, @rowsUpdated, @durationSeconds);
        """;

    /// <summary>
    /// sp_updateextendedproperty fails if the property is absent and
    /// sp_addextendedproperty fails if it is present, so which one to call
    /// depends on the current state. Asking first is the readable way to do it —
    /// the alternative is calling one and swallowing its error, which also
    /// swallows the errors worth seeing.
    /// </summary>
    private const string UpsertPropertySql = """
        IF EXISTS (SELECT 1 FROM sys.extended_properties
                   WHERE class = 0 AND major_id = 0 AND minor_id = 0 AND name = @name)
            EXEC sys.sp_updateextendedproperty @name = @name, @value = @value;
        ELSE
            EXEC sys.sp_addextendedproperty @name = @name, @value = @value;
        """;

    public async Task WriteAsync(StampRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The extended properties go first. If the run dies between the two
        // writes, this order leaves a database that reads as SANITIZED with no
        // audit row — annoying. The other order would leave one that reads as
        // NOT sanitized despite being clean, which costs a full re-run.
        await WritePropertiesAsync(connection, record, cancellationToken);
        await WriteLogRowAsync(connection, record, cancellationToken);
    }

    private static async Task WritePropertiesAsync(
        SqlConnection connection,
        StampRecord record,
        CancellationToken cancellationToken)
    {
        var properties = new (string Name, string Value)[]
        {
            (SanitizationStatus.PropertyNames.Sanitized, "true"),

            // Round-trip format ("o"), so the value that comes back out parses
            // to the same instant regardless of the machine's locale.
            (SanitizationStatus.PropertyNames.SanitizedUtc,
                record.RunUtc.ToString("o", CultureInfo.InvariantCulture)),

            (SanitizationStatus.PropertyNames.ConfigHash, record.ConfigHash),
            (SanitizationStatus.PropertyNames.ToolVersion, record.ToolVersion),
        };

        foreach (var (name, value) in properties)
        {
            await using var command = new SqlCommand(UpsertPropertySql, connection)
            {
                CommandTimeout = NoTimeout,
            };

            command.Parameters.AddWithValue("@name", name);

            // sql_variant. Passing it as nvarchar keeps the value readable by
            // the CAST in StampReader, which is the code that has to agree.
            command.Parameters.Add("@value", System.Data.SqlDbType.NVarChar, 4000).Value = value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task WriteLogRowAsync(
        SqlConnection connection,
        StampRecord record,
        CancellationToken cancellationToken)
    {
        await using (var create = new SqlCommand(CreateLogTableSql, connection)
        {
            CommandTimeout = NoTimeout,
        })
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new SqlCommand(InsertLogRowSql, connection)
        {
            CommandTimeout = NoTimeout,
        };

        insert.Parameters.AddWithValue("@runUtc", record.RunUtc);
        insert.Parameters.AddWithValue("@toolVersion", record.ToolVersion);
        insert.Parameters.AddWithValue("@configHash", record.ConfigHash);
        insert.Parameters.AddWithValue("@tablesTouched", record.TablesTouched);
        insert.Parameters.AddWithValue("@rowsUpdated", record.RowsUpdated);
        insert.Parameters.AddWithValue("@durationSeconds", Math.Round(record.Duration.TotalSeconds, 2));

        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>What one cleaning run is recording about itself (SPEC 5.5).</summary>
/// <param name="ConfigHash">
/// SHA-256 of the config file. Lets a later run notice the config has changed
/// since the database was cleaned — the stamp only ever claims "clean according
/// to THAT config", which is a weaker statement than "clean".
/// </param>
public sealed record StampRecord(
    DateTime RunUtc,
    string ToolVersion,
    string ConfigHash,
    int TablesTouched,
    long RowsUpdated,
    TimeSpan Duration)
{
    /// <summary>
    /// Hashes the config file's BYTES, exactly as they are on disk. Not the
    /// parsed model: a comment or reordered key changes the file a human is
    /// looking at, and "did this file change?" is the question being answered.
    /// </summary>
    public static string HashConfigFile(string path) => HashConfig(File.ReadAllBytes(path));

    public static string HashConfig(ReadOnlySpan<byte> contents) =>
        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    public static string HashConfigText(string contents) => HashConfig(Encoding.UTF8.GetBytes(contents));

    /// <summary>
    /// The tool version recorded in the stamp. Read from the assembly rather
    /// than a constant, so it cannot drift from what actually ran.
    /// </summary>
    public static string CurrentToolVersion =>
        typeof(StampRecord).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StampRecord).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
