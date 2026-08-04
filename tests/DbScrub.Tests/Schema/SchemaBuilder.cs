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

    /// <summary>
    /// Gives the table added most recently a primary key over the named columns,
    /// in key order. Separate from Table() so the common case stays a one-liner
    /// and the tests that care about keys say so out loud.
    /// </summary>
    public SchemaBuilder WithPrimaryKey(params string[] keyColumns)
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException("Add a table before giving it a primary key.");
        }

        _tables[^1] = _tables[^1] with { PrimaryKey = keyColumns };
        return this;
    }

    /// <summary>
    /// Gives a NAMED table a primary key. Needed after TemporalTable(), which
    /// adds two tables — the parent and its history — so "the last one" is the
    /// history table, which in a real database never has a primary key anyway.
    /// </summary>
    public SchemaBuilder WithPrimaryKeyOn(string qualifiedName, params string[] keyColumns)
    {
        var index = _tables.FindIndex(t =>
            string.Equals(t.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            throw new InvalidOperationException($"No table named {qualifiedName} has been added.");
        }

        _tables[index] = _tables[index] with { PrimaryKey = keyColumns };
        return this;
    }

    /// <summary>
    /// Gives the table added most recently a unique index or UNIQUE constraint
    /// over the named columns. The two are the same object to SQL Server, so
    /// there is one method rather than two.
    /// </summary>
    public SchemaBuilder WithUniqueIndex(string name, params string[] columns)
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException("Add a table before giving it a unique index.");
        }

        _tables[^1] = _tables[^1] with
        {
            UniqueIndexes = [.. _tables[^1].UniqueIndexes, new UniqueIndex(name, columns)],
        };

        return this;
    }

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

    /// <summary>
    /// A temporal period column, with the exact flag combination SQL Server
    /// reports for one — verified against the DbScrubTest fixture:
    /// is_computed = 0, is_identity = 0, is_nullable = 0, generated_always_type
    /// = 1 (AS_ROW_START) or 2 (AS_ROW_END). The first two being 0 is the whole
    /// reason this case needed its own flag.
    /// </summary>
    public static SchemaColumn PeriodStart(string name = "ValidFrom") =>
        new(name, "datetime2", IsNullable: false, MaxLength: 8, IsComputed: false, IsIdentity: false,
            GeneratedAlwaysType: 1, GeneratedAlwaysDescription: "AS_ROW_START");

    public static SchemaColumn PeriodEnd(string name = "ValidTo") =>
        new(name, "datetime2", IsNullable: false, MaxLength: 8, IsComputed: false, IsIdentity: false,
            GeneratedAlwaysType: 2, GeneratedAlwaysDescription: "AS_ROW_END");

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
