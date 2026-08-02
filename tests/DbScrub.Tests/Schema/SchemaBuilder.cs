using DbScrub.Core.Schema;

namespace DbScrub.Tests.Schema;

/// <summary>
/// Builds a <see cref="DatabaseSchema"/> by hand so verdict resolution can be
/// tested without a SQL Server. This is the whole reason ISchemaReader exists.
///
/// Deliberately terse: the tests below should read as "a table with these
/// columns", not as a wall of constructor arguments.
/// </summary>
internal sealed class SchemaBuilder
{
    private readonly List<SchemaTable> _tables = [];
    private string _databaseName = "AAVSB";
    private bool _isCdcEnabled;

    public static SchemaBuilder Database(string name = "AAVSB") =>
        new() { _databaseName = name };

    public SchemaBuilder WithCdcEnabled(bool enabled = true)
    {
        _isCdcEnabled = enabled;
        return this;
    }

    /// <summary>Adds an ordinary table. Columns default to nullable nvarchar(100).</summary>
    public SchemaBuilder Table(string qualifiedName, params string[] columnNames)
        => Table(qualifiedName, columnNames.Select(Column).ToArray());

    public SchemaBuilder Table(string qualifiedName, params SchemaColumn[] columns)
        => AddTable(qualifiedName, TemporalType.None, isTrackedByCdc: false, historyName: null, columns);

    /// <summary>Adds a system-versioned temporal table plus its history table.</summary>
    public SchemaBuilder TemporalTable(string qualifiedName, string historyQualifiedName, params string[] columnNames)
    {
        var columns = columnNames.Select(Column).ToArray();

        AddTable(qualifiedName, TemporalType.SystemVersioned, false, historyQualifiedName, columns);
        AddTable(historyQualifiedName, TemporalType.HistoryTable, false, null, columns);

        return this;
    }

    public SchemaBuilder CdcTable(string qualifiedName, params string[] columnNames)
        => AddTable(qualifiedName, TemporalType.None, isTrackedByCdc: true, historyName: null,
            columnNames.Select(Column).ToArray());

    public DatabaseSchema Build() => new(_databaseName, _isCdcEnabled, _tables);

    // ---- column shorthands -------------------------------------------------

    public static SchemaColumn Column(string name) =>
        new(name, "nvarchar", IsNullable: true, MaxLength: 200, IsComputed: false, IsIdentity: false);

    public static SchemaColumn NotNull(string name, string dataType = "nvarchar") =>
        new(name, dataType, IsNullable: false, MaxLength: 200, IsComputed: false, IsIdentity: false);

    public static SchemaColumn Identity(string name) =>
        new(name, "int", IsNullable: false, MaxLength: 4, IsComputed: false, IsIdentity: true);

    public static SchemaColumn Computed(string name) =>
        new(name, "nvarchar", IsNullable: true, MaxLength: 200, IsComputed: true, IsIdentity: false);

    private SchemaBuilder AddTable(
        string qualifiedName,
        TemporalType temporalType,
        bool isTrackedByCdc,
        string? historyName,
        SchemaColumn[] columns)
    {
        var (schema, name) = Split(qualifiedName);
        var (historySchema, history) = historyName is null ? (null, null) : Split(historyName);

        _tables.Add(new SchemaTable(schema, name, temporalType, isTrackedByCdc, historySchema, history, columns));
        return this;
    }

    private static (string Schema, string Name) Split(string qualifiedName)
    {
        var parts = qualifiedName.Split('.');
        return (parts[0], parts[1]);
    }
}
