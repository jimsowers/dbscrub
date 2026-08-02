using System.Data;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Schema;

/// <summary>
/// The one place sys.* is queried (CLAUDE.md). Three reads, no ORM:
/// the database-level CDC flag, the table list, then all columns.
///
/// Why three queries rather than one join: the table list and the column list
/// are different grains, and joining them means re-materializing table rows per
/// column and de-duplicating in memory. Two flat reads plus a dictionary lookup
/// is less code and less surprising.
///
/// Note on parameters: these queries take NO user input at all — no table or
/// column name from the config reaches them, because the inventory reads
/// everything and the diff happens in memory afterwards. The SQL-injection
/// surface here is zero by construction, which is a stronger guarantee than
/// parameterizing would give.
/// </summary>
public sealed class SchemaInventory(string connectionString) : ISchemaReader
{
    /// <summary>
    /// Schemas that are never part of "the live schema". SPEC section 4 exempts
    /// these from UNCLASSIFIED; excluding them at the source means nothing
    /// downstream has to remember to.
    /// </summary>
    private static readonly string[] SystemSchemas = ["sys", "cdc", "INFORMATION_SCHEMA"];

    private const string DatabaseSql = """
        SELECT DB_NAME() AS DatabaseName,
               CAST(is_cdc_enabled AS bit) AS IsCdcEnabled
        FROM sys.databases
        WHERE database_id = DB_ID();
        """;

    private const string TablesSql = """
        SELECT  s.name              AS SchemaName,
                t.name              AS TableName,
                t.temporal_type     AS TemporalType,
                t.is_tracked_by_cdc AS IsTrackedByCdc,
                hs.name             AS HistorySchema,
                h.name              AS HistoryName
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s  ON s.schema_id  = t.schema_id
        -- history_table_id is populated only for temporal_type = 2, so this is
        -- a LEFT JOIN and both history columns are null for ordinary tables.
        LEFT  JOIN sys.tables  AS h  ON h.object_id  = t.history_table_id
        LEFT  JOIN sys.schemas AS hs ON hs.schema_id = h.schema_id
        WHERE t.is_ms_shipped = 0
          AND s.name NOT IN ('sys', 'cdc', 'INFORMATION_SCHEMA')
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsSql = """
        SELECT  s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName,
                ty.name AS DataType,
                c.is_nullable  AS IsNullable,
                c.max_length   AS MaxLength,
                c.is_computed  AS IsComputed,
                c.is_identity  AS IsIdentity,
                -- Temporal period columns and ledger columns. NOT covered by
                -- is_computed or is_identity (both 0 for a period column), and
                -- deliberately not is_hidden, which is only about visibility.
                c.generated_always_type      AS GeneratedAlwaysType,
                c.generated_always_type_desc AS GeneratedAlwaysDescription
        FROM sys.columns AS c
        INNER JOIN sys.tables  AS t  ON t.object_id     = c.object_id
        INNER JOIN sys.schemas AS s  ON s.schema_id     = t.schema_id
        -- user_type_id (not system_type_id) so an alias type reports its own
        -- name; the mask engine cares about the declared type.
        INNER JOIN sys.types   AS ty ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0
          AND s.name NOT IN ('sys', 'cdc', 'INFORMATION_SCHEMA')
        ORDER BY s.name, t.name, c.column_id;
        """;

    public async Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var (databaseName, isCdcEnabled) = await ReadDatabaseAsync(connection, cancellationToken);
        var columnsByTable = await ReadColumnsAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(connection, columnsByTable, cancellationToken);

        return new DatabaseSchema(databaseName, isCdcEnabled, tables);
    }

    private static async Task<(string DatabaseName, bool IsCdcEnabled)> ReadDatabaseAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(DatabaseSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // sys.databases always has a row for the current database, so this
            // means the connection is not where we think it is.
            throw new InvalidOperationException(
                "Could not read the current database from sys.databases.");
        }

        return (reader.GetString(0), reader.GetBoolean(1));
    }

    private static async Task<Dictionary<string, List<SchemaColumn>>> ReadColumnsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var byTable = new Dictionary<string, List<SchemaColumn>>(StringComparer.OrdinalIgnoreCase);

        await using var command = new SqlCommand(ColumnsSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var column = new SchemaColumn(
                Name: reader.GetString(2),
                DataType: reader.GetString(3),
                IsNullable: reader.GetBoolean(4),
                // max_length is smallint and is -1 for the (max) types.
                MaxLength: reader.GetInt16(5),
                IsComputed: reader.GetBoolean(6),
                IsIdentity: reader.GetBoolean(7),
                // generated_always_type is tinyint; the _desc is nvarchar and
                // is 'NOT_APPLICABLE' rather than NULL for ordinary columns,
                // but defend against null anyway rather than trust it.
                GeneratedAlwaysType: reader.GetByte(8),
                GeneratedAlwaysDescription: reader.IsDBNull(9) ? "NOT_APPLICABLE" : reader.GetString(9));

            if (!byTable.TryGetValue(key, out var columns))
            {
                columns = [];
                byTable[key] = columns;
            }

            columns.Add(column);
        }

        return byTable;
    }

    private static async Task<List<SchemaTable>> ReadTablesAsync(
        SqlConnection connection,
        Dictionary<string, List<SchemaColumn>> columnsByTable,
        CancellationToken cancellationToken)
    {
        var tables = new List<SchemaTable>();

        await using var command = new SqlCommand(TablesSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var key = $"{schemaName}.{tableName}";

            tables.Add(new SchemaTable(
                Schema: schemaName,
                Name: tableName,
                // temporal_type is tinyint; the enum values match SQL Server's.
                TemporalType: (TemporalType)reader.GetByte(2),
                IsTrackedByCdc: reader.GetBoolean(3),
                HistorySchema: reader.IsDBNull(4) ? null : reader.GetString(4),
                HistoryName: reader.IsDBNull(5) ? null : reader.GetString(5),
                Columns: columnsByTable.TryGetValue(key, out var columns) ? columns : []));
        }

        return tables;
    }

    /// <summary>
    /// Builds a connection string for a local, integrated-security connection.
    /// TrustServerCertificate is on because Microsoft.Data.SqlClient defaults to
    /// Encrypt=true, and a local developer instance normally has a self-signed
    /// certificate. That is acceptable ONLY because the safety interlock
    /// (SPEC section 3) confines this tool to localhost.
    /// </summary>
    public static string BuildConnectionString(string server, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ApplicationName = "dbscrub",
            ConnectTimeout = 15,
        }.ConnectionString;

    /// <summary>Exposed so tests and the report can name what was excluded.</summary>
    public static IReadOnlyList<string> ExcludedSystemSchemas => SystemSchemas;
}
