using System.Data;
using Microsoft.Data.SqlClient;

namespace DbScrub.Core.Schema;

/// <summary>
/// The one place sys.* is queried (CLAUDE.md). Five reads, no ORM:
/// the database-level CDC flag, the table list, all columns, all primary
/// key columns, then every other uniqueness rule.
///
/// Why separate queries rather than one join: the table list, the column list
/// and the key list are different grains, and joining them means
/// re-materializing table rows per column and de-duplicating in memory. Flat
/// reads plus a dictionary lookup is less code and less surprising.
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

    private const string PrimaryKeysSql = """
        SELECT  s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                          AND ic.index_id  = i.index_id
        INNER JOIN sys.columns AS c  ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        INNER JOIN sys.tables  AS t  ON t.object_id = i.object_id
        INNER JOIN sys.schemas AS s  ON s.schema_id = t.schema_id
        WHERE i.is_primary_key = 1
          -- Included columns are stored in the leaf pages but are NOT part of
          -- the key, so they cannot be seeked on or ordered by. A primary key
          -- never has them, but the join above would happily return them if one
          -- ever did, and an extra "key" column would corrupt the walk.
          AND ic.is_included_column = 0
          AND t.is_ms_shipped = 0
          AND s.name NOT IN ('sys', 'cdc', 'INFORMATION_SCHEMA')
        -- key_ordinal is the declared position within the key. Ordering by it is
        -- the whole point of this query: the mask engine's batching predicate is
        -- only correct if it compares columns in key order.
        ORDER BY s.name, t.name, ic.key_ordinal;
        """;

    /// <summary>
    /// Every uniqueness rule EXCEPT the primary key. Deliberately the same query
    /// shape as the one above, because it reads the same catalog views for the
    /// same reason — the only differences are the three flags in the WHERE
    /// clause, and each is worth explaining:
    ///
    ///   is_unique = 1        catches UNIQUE constraints and CREATE UNIQUE INDEX
    ///                        alike; a constraint is a unique index with a flag.
    ///   is_primary_key = 0   the key has its own read, and a masked key column
    ///                        is already refused with a better message (D20).
    ///   is_disabled = 0      a disabled index enforces nothing, so refusing a
    ///                        strategy on its account would be a refusal with no
    ///                        failure behind it.
    ///
    /// Filtered unique indexes (has_filter = 1) are read like any other. The
    /// filter is arbitrary SQL this tool does not parse, so it cannot know
    /// whether the rows it is about to write fall inside it — and a conservative
    /// refusal costs a config edit, while the other mistake costs a half-masked
    /// database (DECISIONS.md D27).
    /// </summary>
    private const string UniqueIndexesSql = """
        SELECT  s.name  AS SchemaName,
                t.name  AS TableName,
                i.name  AS IndexName,
                c.name  AS ColumnName
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                          AND ic.index_id  = i.index_id
        INNER JOIN sys.columns AS c  ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        INNER JOIN sys.tables  AS t  ON t.object_id = i.object_id
        INNER JOIN sys.schemas AS s  ON s.schema_id = t.schema_id
        WHERE i.is_unique      = 1
          AND i.is_primary_key = 0
          AND i.is_disabled    = 0
          -- An included column sits in the leaf pages and is NOT part of the
          -- key, so it takes no part in uniqueness. Treating one as constrained
          -- would refuse a config that would have worked.
          AND ic.is_included_column = 0
          AND t.is_ms_shipped = 0
          AND s.name NOT IN ('sys', 'cdc', 'INFORMATION_SCHEMA')
        ORDER BY s.name, t.name, i.name, ic.key_ordinal;
        """;

    public async Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var (databaseName, isCdcEnabled) = await ReadDatabaseAsync(connection, cancellationToken);
        var columnsByTable = await ReadColumnsAsync(connection, cancellationToken);
        var keysByTable = await ReadPrimaryKeysAsync(connection, cancellationToken);
        var uniqueByTable = await ReadUniqueIndexesAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(
            connection, columnsByTable, keysByTable, uniqueByTable, cancellationToken);

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

    /// <summary>
    /// The ordered key columns of every primary key in the database, grouped by
    /// table. Tables with no primary key simply do not appear, which is what
    /// leaves <see cref="SchemaTable.PrimaryKey"/> empty for a heap.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> ReadPrimaryKeysAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        await using var command = new SqlCommand(PrimaryKeysSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";

            if (!byTable.TryGetValue(key, out var columns))
            {
                columns = [];
                byTable[key] = columns;
            }

            // Appended in the order the query returned them, which is
            // key_ordinal order. Never sort this list afterwards.
            columns.Add(reader.GetString(2));
        }

        return byTable;
    }

    /// <summary>
    /// Every unique index and UNIQUE constraint in the database, grouped by
    /// table. Tables with no uniqueness rule beyond their primary key simply do
    /// not appear.
    /// </summary>
    private static async Task<Dictionary<string, List<UniqueIndex>>> ReadUniqueIndexesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        // One index is many rows, so the read is flat and the grouping happens
        // here. Read into a list first rather than growing nested dictionaries
        // in the loop: the grouping is then one expression a reader can check
        // against the query, instead of state carried between iterations.
        var rows = new List<(string Table, string Index, string Column)>();

        await using var command = new SqlCommand(UniqueIndexesSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                Table: $"{reader.GetString(0)}.{reader.GetString(1)}",
                Index: reader.GetString(2),
                Column: reader.GetString(3)));
        }

        // GroupBy preserves the order rows arrived in, and they arrived in
        // key_ordinal order, so each index keeps its columns in key order. That
        // is not load-bearing here the way it is for the primary key — the
        // planner only asks whether a column is IN an index — but a refusal
        // names the columns, and naming them in a different order than the
        // schema declares them reads as a different index.
        return rows
            .GroupBy(row => row.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                byTable => byTable.Key,
                byTable => byTable
                    .GroupBy(row => row.Index, StringComparer.OrdinalIgnoreCase)
                    .Select(byIndex => new UniqueIndex(
                        byIndex.Key,
                        byIndex.Select(row => row.Column).ToList()))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<SchemaTable>> ReadTablesAsync(
        SqlConnection connection,
        Dictionary<string, List<SchemaColumn>> columnsByTable,
        Dictionary<string, List<string>> keysByTable,
        Dictionary<string, List<UniqueIndex>> uniqueByTable,
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
                Columns: columnsByTable.TryGetValue(key, out var columns) ? columns : [])
            {
                PrimaryKey = keysByTable.TryGetValue(key, out var keyColumns) ? keyColumns : [],
                UniqueIndexes = uniqueByTable.TryGetValue(key, out var indexes) ? indexes : [],
            });
        }

        return tables;
    }

    /// <summary>
    /// Builds a connection string for a local, integrated-security connection.
    /// TrustServerCertificate is on because Microsoft.Data.SqlClient defaults to
    /// Encrypt=true, and a local developer instance normally has a self-signed
    /// certificate. That is acceptable ONLY because the safety checks
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
