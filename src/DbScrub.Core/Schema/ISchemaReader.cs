namespace DbScrub.Core.Schema;

/// <summary>
/// Reads the live schema. Exists so everything downstream — verdict
/// resolution, the diff, the report — can be tested against a hand-built
/// <see cref="DatabaseSchema"/> instead of a running SQL Server.
///
/// There is exactly one production implementation, <see cref="SchemaInventory"/>,
/// per CLAUDE.md's rule that all sys.* access lives in one place.
/// </summary>
public interface ISchemaReader
{
    Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default);
}
