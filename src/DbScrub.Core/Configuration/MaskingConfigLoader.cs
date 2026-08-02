using System.Text;
using System.Text.Json;

namespace DbScrub.Core.Configuration;

/// <summary>
/// Loads and validates a masking config (SPEC section 4).
///
/// Two rules shape this class:
///   1. Collect every error, then report. Fixing a config one error per run is
///      a miserable loop.
///   2. Unknown properties are errors, not noise. `"stragety": "scramble"` that
///      parses "successfully" and silently masks nothing is exactly the failure
///      this tool exists to prevent.
///
/// Validation that needs the live database — `null` on a NOT NULL column, a
/// `static` value that will not fit the column type — is NOT here. It belongs
/// with the schema inventory, and lives in the verdict pass.
/// </summary>
public static class MaskingConfigLoader
{
    private const string Comment = "$comment";

    private static readonly string[] RootProperties = [Comment, "defaults", "tables"];

    private static readonly string[] DefaultsProperties =
        [Comment, "allowedServers", "unclassifiedColumns", "batchSize", "renameTo", "repairUsers"];

    private static readonly string[] TableProperties = [Comment, "name", "strategy", "history", "columns"];

    private static readonly string[] ColumnProperties = [Comment, "name", "strategy", "value", "reason"];

    /// <summary>Reads and validates a config file. Throws <see cref="ConfigInvalidException"/> on any problem.</summary>
    public static MaskingConfig LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json, path);
    }

    /// <summary>Validates config text. <paramref name="sourceName"/> is what error messages are prefixed with.</summary>
    public static MaskingConfig Parse(string json, string sourceName)
    {
        if (TryParse(json, sourceName, out var config, out var errors))
        {
            return config!;
        }

        throw new ConfigInvalidException(sourceName, errors);
    }

    /// <summary>Non-throwing variant. Returns false with a populated error list.</summary>
    public static bool TryParse(
        string json,
        string sourceName,
        out MaskingConfig? config,
        out IReadOnlyList<ConfigError> errors)
    {
        _ = sourceName; // Only used by the caller when formatting; kept for a symmetric API.
        config = null;

        var utf8 = Encoding.UTF8.GetBytes(json);
        var collected = new List<ConfigError>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            // JsonException DOES carry a position for parse errors — use it.
            errors =
            [
                new ConfigError(
                    ConfigErrorCodes.InvalidJson,
                    Path: string.Empty,
                    Message: $"The file is not valid JSON. {ex.Message}",
                    Suggestion: null,
                    Line: (int)(ex.LineNumber + 1 ?? 0),
                    Column: (int)(ex.BytePositionInLine + 1 ?? 0)),
            ];
            return false;
        }

        using (document)
        {
            var index = JsonPositionIndex.Build(utf8);
            var validator = new Validator(index, collected);
            config = validator.ReadRoot(document.RootElement);
        }

        errors = collected;

        if (collected.Count > 0)
        {
            config = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walks the document once, appending to <c>errors</c> as it goes and
    /// returning the best config it can build. The returned value is discarded
    /// whenever any error was recorded, so partial results never escape.
    /// </summary>
    private sealed class Validator(JsonPositionIndex index, List<ConfigError> errors)
    {
        public MaskingConfig? ReadRoot(JsonElement root)
        {
            if (!RequireObject(root, path: string.Empty, what: "The config root"))
            {
                return null;
            }

            RejectUnknownProperties(root, string.Empty, RootProperties);

            var defaults = root.TryGetProperty("defaults", out var defaultsElement)
                ? ReadDefaults(defaultsElement)
                : ConfigDefaults.CreateDefault();

            var tables = ReadTables(root);

            return new MaskingConfig(defaults, tables);
        }

        private ConfigDefaults ReadDefaults(JsonElement element)
        {
            const string path = "defaults";

            if (!RequireObject(element, path, "\"defaults\""))
            {
                return ConfigDefaults.CreateDefault();
            }

            RejectUnknownProperties(element, path, DefaultsProperties);

            var allowedServers = ConfigDefaults.DefaultAllowedServers;
            if (element.TryGetProperty("allowedServers", out var serversElement))
            {
                var servers = ReadStringArray(serversElement, $"{path}.allowedServers");

                if (servers is { Count: 0 })
                {
                    // Fail-closed would refuse every server, which is safe but
                    // useless; an empty allowlist is far more likely a mistake.
                    Add(ConfigErrorCodes.InvalidValue, $"{path}.allowedServers",
                        "\"allowedServers\" is empty, so the safety interlock would refuse every server.",
                        "Remove the property to accept the default [\"localhost\", \".\", \"(local)\", \"127.0.0.1\"], "
                        + "or list the servers you mean.");
                }
                else if (servers is not null)
                {
                    allowedServers = servers;
                }
            }

            var unclassified = ConfigDefaults.DefaultUnclassifiedColumns;
            if (element.TryGetProperty("unclassifiedColumns", out var modeElement))
            {
                unclassified = ReadUnclassifiedMode(modeElement, $"{path}.unclassifiedColumns") ?? unclassified;
            }

            var batchSize = ConfigDefaults.DefaultBatchSize;
            if (element.TryGetProperty("batchSize", out var batchElement))
            {
                batchSize = ReadBatchSize(batchElement, $"{path}.batchSize") ?? batchSize;
            }

            string? renameTo = null;
            if (element.TryGetProperty("renameTo", out var renameElement))
            {
                renameTo = ReadNonEmptyString(renameElement, $"{path}.renameTo", "\"renameTo\"");
            }

            IReadOnlyList<string> repairUsers = [];
            if (element.TryGetProperty("repairUsers", out var usersElement))
            {
                repairUsers = ReadStringArray(usersElement, $"{path}.repairUsers") ?? [];
            }

            return new ConfigDefaults(allowedServers, unclassified, batchSize, renameTo, repairUsers);
        }

        private IReadOnlyList<TableConfig> ReadTables(JsonElement root)
        {
            if (!root.TryGetProperty("tables", out var tablesElement))
            {
                Add(ConfigErrorCodes.MissingProperty, "tables",
                    "The config has no \"tables\" property.",
                    "Add \"tables\": [] and run `dbscrub report` — it prints every column as UNCLASSIFIED "
                    + "in paste-into-config form, which is the intended way to start an inventory.");
                return [];
            }

            if (tablesElement.ValueKind != JsonValueKind.Array)
            {
                Add(ConfigErrorCodes.InvalidType, "tables",
                    $"\"tables\" must be an array, but it is {Describe(tablesElement.ValueKind)}.");
                return [];
            }

            var tables = new List<TableConfig>();

            // Case-insensitive because SQL Server's default collation is, and a
            // config with dbo.Person and dbo.PERSON is a mistake either way.
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var elementIndex = 0;
            foreach (var tableElement in tablesElement.EnumerateArray())
            {
                var path = $"tables[{elementIndex}]";
                var table = ReadTable(tableElement, path);
                elementIndex++;

                if (table is null)
                {
                    continue;
                }

                if (seen.TryGetValue(table.QualifiedName, out var firstIndex))
                {
                    Add(ConfigErrorCodes.Duplicate, $"{path}.name",
                        $"Table {table.QualifiedName} is listed twice (also at tables[{firstIndex}]).",
                        "Merge the two entries — a later entry does not override an earlier one.");
                    continue;
                }

                seen[table.QualifiedName] = elementIndex - 1;
                tables.Add(table);
            }

            return tables;
        }

        private TableConfig? ReadTable(JsonElement element, string path)
        {
            if (!RequireObject(element, path, "Each entry in \"tables\""))
            {
                return null;
            }

            RejectUnknownProperties(element, path, TableProperties);

            var qualifiedName = ReadRequiredString(element, path, "name", "A table entry");
            if (qualifiedName is null)
            {
                return null;
            }

            var parts = SplitQualifiedName(qualifiedName, $"{path}.name");
            if (parts is null)
            {
                return null;
            }

            TableStrategy? strategy = null;
            if (element.TryGetProperty("strategy", out var strategyElement))
            {
                strategy = ReadTableStrategy(strategyElement, $"{path}.strategy");
            }

            var history = HistoryMode.Truncate;
            if (element.TryGetProperty("history", out var historyElement))
            {
                history = ReadHistoryMode(historyElement, $"{path}.history") ?? history;
            }

            var hasColumnsProperty = element.TryGetProperty("columns", out var columnsElement);
            var columns = hasColumnsProperty
                ? ReadColumns(columnsElement, $"{path}.columns")
                : [];

            // A truncated table has no rows left to mask, so per-column
            // strategies are instructions that will never run. Refuse rather
            // than pick one — the author meant one or the other.
            if (strategy == TableStrategy.Truncate && columns.Count > 0)
            {
                Add(ConfigErrorCodes.Contradictory, $"{path}.columns",
                    $"{qualifiedName} is truncated, so its \"columns\" strategies would never run.",
                    "Drop \"columns\", or drop \"strategy\": \"truncate\" if the table should be masked instead.");
            }

            if (strategy is null && !hasColumnsProperty)
            {
                Add(ConfigErrorCodes.Contradictory, path,
                    $"{qualifiedName} asks for nothing — no \"strategy\" and no \"columns\".",
                    "Add \"strategy\": \"truncate\", or a \"columns\" list. To say \"this table is fine as-is\", "
                    + "give each of its columns \"strategy\": \"keep\".");
            }

            return new TableConfig(parts.Value.Schema, parts.Value.Name, strategy, history, columns);
        }

        private IReadOnlyList<ColumnConfig> ReadColumns(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"\"columns\" must be an array, but it is {Describe(element.ValueKind)}.");
                return [];
            }

            var columns = new List<ColumnConfig>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var elementIndex = 0;
            foreach (var columnElement in element.EnumerateArray())
            {
                var columnPath = $"{path}[{elementIndex}]";
                var column = ReadColumn(columnElement, columnPath);
                elementIndex++;

                if (column is null)
                {
                    continue;
                }

                if (seen.TryGetValue(column.Name, out var firstIndex))
                {
                    Add(ConfigErrorCodes.Duplicate, $"{columnPath}.name",
                        $"Column {column.Name} is listed twice (also at {path}[{firstIndex}]).",
                        "Keep the entry you meant and delete the other.");
                    continue;
                }

                seen[column.Name] = elementIndex - 1;
                columns.Add(column);
            }

            return columns;
        }

        private ColumnConfig? ReadColumn(JsonElement element, string path)
        {
            if (!RequireObject(element, path, "Each entry in \"columns\""))
            {
                return null;
            }

            RejectUnknownProperties(element, path, ColumnProperties);

            var name = ReadRequiredString(element, path, "name", "A column entry");
            if (name is null)
            {
                return null;
            }

            if (!element.TryGetProperty("strategy", out var strategyElement))
            {
                Add(ConfigErrorCodes.MissingProperty, path,
                    $"Column {name} has no \"strategy\".",
                    $"{{ \"name\": \"{name}\", \"strategy\": \"keep\", \"reason\": \"no PII\" }}");
                return null;
            }

            var strategy = ReadColumnStrategy(strategyElement, $"{path}.strategy", name);
            if (strategy is null)
            {
                return null;
            }

            var value = ReadStaticValue(element, path, name, strategy.Value);

            string? reason = null;
            if (element.TryGetProperty("reason", out var reasonElement))
            {
                reason = ReadNonEmptyString(reasonElement, $"{path}.reason", "\"reason\"");
            }

            return new ColumnConfig(name, strategy.Value, value, reason);
        }

        /// <summary>
        /// Enforces the two halves of the `value` rule: static must have one,
        /// and nothing else may. The second half matters as much as the first —
        /// a `value` sitting on a scramble column reads as intent that silently
        /// never happens.
        /// </summary>
        private ConfigValue? ReadStaticValue(JsonElement element, string path, string name, ColumnStrategy strategy)
        {
            var hasValue = element.TryGetProperty("value", out var valueElement);

            if (strategy != ColumnStrategy.Static)
            {
                if (hasValue)
                {
                    Add(ConfigErrorCodes.ValueNotAllowed, $"{path}.value",
                        $"Column {name} uses strategy \"{Name(strategy)}\", which ignores \"value\".",
                        "Remove \"value\", or change the strategy to \"static\" if the fixed value was the point.");
                }

                return null;
            }

            if (!hasValue)
            {
                Add(ConfigErrorCodes.StrategyRequiresValue, path,
                    $"Column {name} uses strategy \"static\" but has no \"value\".",
                    $"{{ \"name\": \"{name}\", \"strategy\": \"static\", \"value\": \"[redacted]\" }}");
                return null;
            }

            switch (valueElement.ValueKind)
            {
                case JsonValueKind.String:
                    return new ConfigValue(JsonValueKind.String, valueElement.GetString() ?? string.Empty);

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return new ConfigValue(valueElement.ValueKind, valueElement.GetRawText());

                case JsonValueKind.Null:
                    // A JSON null here is ambiguous: it looks like "set this to
                    // NULL", which is a different strategy with a different
                    // requirement (the column must be nullable).
                    Add(ConfigErrorCodes.InvalidValue, $"{path}.value",
                        $"Column {name} has \"value\": null, which is ambiguous.",
                        $"Use {{ \"name\": \"{name}\", \"strategy\": \"null\" }} to write NULL, "
                        + "or give \"static\" a real value.");
                    return null;

                default:
                    Add(ConfigErrorCodes.InvalidType, $"{path}.value",
                        $"Column {name} has a \"value\" that is {Describe(valueElement.ValueKind)}; "
                        + "it must be a string, number, or boolean.");
                    return null;
            }
        }

        // ---- small readers -------------------------------------------------

        private (string Schema, string Name)? SplitQualifiedName(string qualifiedName, string path)
        {
            var parts = qualifiedName.Split('.');

            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                Add(ConfigErrorCodes.InvalidValue, path,
                    $"Table name \"{qualifiedName}\" must be schema-qualified as schema.table.",
                    parts.Length == 1
                        ? $"Write \"dbo.{qualifiedName}\" if it is in the dbo schema."
                        : "Exactly one dot, with a non-empty schema and table name.");
                return null;
            }

            return (parts[0], parts[1]);
        }

        private ColumnStrategy? ReadColumnStrategy(JsonElement element, string path, string column)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"Column {column} has a \"strategy\" that is {Describe(element.ValueKind)}; it must be a string.");
                return null;
            }

            return element.GetString() switch
            {
                "null" => ColumnStrategy.Null,
                "static" => ColumnStrategy.Static,
                "scramble" => ColumnStrategy.Scramble,
                "keep" => ColumnStrategy.Keep,
                var other => Reject(other),
            };

            ColumnStrategy? Reject(string? other)
            {
                Add(ConfigErrorCodes.InvalidValue, path,
                    $"Column {column} has unknown strategy \"{other}\".",
                    "Valid strategies are: null, static, scramble, keep.");
                return null;
            }
        }

        private TableStrategy? ReadTableStrategy(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"A table \"strategy\" must be a string, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            if (element.GetString() == "truncate")
            {
                return TableStrategy.Truncate;
            }

            Add(ConfigErrorCodes.InvalidValue, path,
                $"Unknown table strategy \"{element.GetString()}\".",
                "The only table-level strategy is \"truncate\". Per-column strategies go in \"columns\".");
            return null;
        }

        private HistoryMode? ReadHistoryMode(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"\"history\" must be a string, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            return element.GetString() switch
            {
                "truncate" => HistoryMode.Truncate,
                "mask" => HistoryMode.Mask,
                var other => Reject(other),
            };

            HistoryMode? Reject(string? other)
            {
                Add(ConfigErrorCodes.InvalidValue, path,
                    $"Unknown history mode \"{other}\".",
                    "Valid values are \"truncate\" (default) and \"mask\".");
                return null;
            }
        }

        private UnclassifiedMode? ReadUnclassifiedMode(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"\"unclassifiedColumns\" must be a string, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            return element.GetString() switch
            {
                "warn" => UnclassifiedMode.Warn,
                "fail" => UnclassifiedMode.Fail,
                var other => Reject(other),
            };

            UnclassifiedMode? Reject(string? other)
            {
                Add(ConfigErrorCodes.InvalidValue, path,
                    $"Unknown unclassifiedColumns mode \"{other}\".",
                    "Valid values are \"warn\" (v0 default) and \"fail\".");
                return null;
            }
        }

        private int? ReadBatchSize(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"\"batchSize\" must be a whole number, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            if (value < 1 || value > ConfigDefaults.MaxBatchSize)
            {
                Add(ConfigErrorCodes.InvalidValue, path,
                    $"\"batchSize\" is {value}; it must be between 1 and {ConfigDefaults.MaxBatchSize:N0}.",
                    $"The default is {ConfigDefaults.DefaultBatchSize:N0}. This is rows held in memory per "
                    + "transaction, so a very large value is a memory problem, not a speed win.");
                return null;
            }

            return value;
        }

        private IReadOnlyList<string>? ReadStringArray(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"\"{LastSegment(path)}\" must be an array, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            var values = new List<string>();
            var elementIndex = 0;

            foreach (var item in element.EnumerateArray())
            {
                var itemPath = $"{path}[{elementIndex}]";
                elementIndex++;

                if (item.ValueKind != JsonValueKind.String)
                {
                    Add(ConfigErrorCodes.InvalidType, itemPath,
                        $"Every entry in \"{LastSegment(path)}\" must be a string, "
                        + $"but this one is {Describe(item.ValueKind)}.");
                    continue;
                }

                var text = item.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    Add(ConfigErrorCodes.InvalidValue, itemPath,
                        $"An entry in \"{LastSegment(path)}\" is empty.");
                    continue;
                }

                values.Add(text);
            }

            return values;
        }

        private string? ReadRequiredString(JsonElement parent, string parentPath, string property, string what)
        {
            if (!parent.TryGetProperty(property, out var element))
            {
                Add(ConfigErrorCodes.MissingProperty, parentPath,
                    $"{what} has no \"{property}\".");
                return null;
            }

            return ReadNonEmptyString(element, $"{parentPath}.{property}", $"\"{property}\"");
        }

        private string? ReadNonEmptyString(JsonElement element, string path, string what)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(ConfigErrorCodes.InvalidType, path,
                    $"{what} must be a string, but it is {Describe(element.ValueKind)}.");
                return null;
            }

            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                Add(ConfigErrorCodes.InvalidValue, path, $"{what} is empty.");
                return null;
            }

            return text;
        }

        // ---- shared checks -------------------------------------------------

        private bool RequireObject(JsonElement element, string path, string what)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            Add(ConfigErrorCodes.InvalidType, path,
                $"{what} must be a JSON object, but it is {Describe(element.ValueKind)}.");
            return false;
        }

        /// <summary>
        /// Refusing unknown properties is the single highest-value check here.
        /// A misspelled strategy key does not fail — it masks nothing, quietly,
        /// and the verify gate is the only thing left standing between that and
        /// real data in a dev database.
        /// </summary>
        private void RejectUnknownProperties(JsonElement element, string path, string[] allowed)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (allowed.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                var suggestion = FindNearest(property.Name, allowed);

                Add(ConfigErrorCodes.UnknownProperty, propertyPath,
                    $"Unknown property \"{property.Name}\".",
                    suggestion is not null
                        ? $"Did you mean \"{suggestion}\"?"
                        : $"Valid properties here: {string.Join(", ", allowed.Where(a => a != Comment))}.");
            }
        }

        private void Add(string code, string path, string message, string? suggestion = null)
        {
            var (line, column) = index.For(path);
            errors.Add(new ConfigError(code, path, message, suggestion, line, column));
        }
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>Turns a JsonValueKind into something a human wants to read in an error.</summary>
    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "missing",
    };

    private static string Name(ColumnStrategy strategy) => strategy switch
    {
        ColumnStrategy.Null => "null",
        ColumnStrategy.Static => "static",
        ColumnStrategy.Scramble => "scramble",
        ColumnStrategy.Keep => "keep",
        _ => strategy.ToString().ToLowerInvariant(),
    };

    private static string LastSegment(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

    /// <summary>
    /// Cheap typo suggestion: the allowed name within edit distance 2. Good
    /// enough to catch "stragety" and "colums" without pretending to be clever.
    /// </summary>
    private static string? FindNearest(string actual, string[] allowed)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in allowed)
        {
            if (candidate == Comment)
            {
                continue;
            }

            var distance = EditDistance(actual.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= 2 ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
