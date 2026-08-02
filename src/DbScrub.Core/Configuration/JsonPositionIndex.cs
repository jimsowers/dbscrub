using System.Text.Json;

namespace DbScrub.Core.Configuration;

/// <summary>
/// Maps a dotted JSON path (tables[0].columns[3].strategy) to the line and
/// column where it appears in the source text.
///
/// Why this exists: JsonDocument gives a convenient object model but throws
/// away positions, while JsonException only carries a position for *parse*
/// errors. Our validation errors are semantic ("static needs a value"), and
/// SPEC section 4 asks for line-level errors. So we make one extra pass with
/// Utf8JsonReader — which does expose a byte offset per token — and build the
/// lookup once. Validation then reports paths, and formatting resolves them.
///
/// This is the price of hand-rolled validation over a JSON Schema library
/// (DECISIONS.md D12); it is about seventy lines and it never changes.
/// </summary>
internal sealed class JsonPositionIndex
{
    private readonly Dictionary<string, (int Line, int Column)> _positions;

    private JsonPositionIndex(Dictionary<string, (int Line, int Column)> positions)
        => _positions = positions;

    /// <summary>An index that knows nothing — every lookup reports "no position".</summary>
    public static JsonPositionIndex Empty { get; } = new([]);

    /// <summary>
    /// Resolves a path to a 1-based position, or (0, 0) when the path is not in
    /// the document (which happens for errors about something *missing*).
    /// </summary>
    public (int Line, int Column) For(string path) =>
        _positions.TryGetValue(path, out var position) ? position : (0, 0);

    public static JsonPositionIndex Build(ReadOnlySpan<byte> utf8)
    {
        var positions = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var lineStarts = FindLineStarts(utf8);

        // A stack of the containers we are currently inside. Each frame knows
        // its own path so a child's path is just the parent's plus one segment.
        var containers = new Stack<Container>();
        var pendingProperty = string.Empty;

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            // The sample config uses "$comment" properties rather than real JSON
            // comments, but tolerate both — a rejected comment is a bad first
            // impression for a config file people are meant to hand-edit.
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingProperty = reader.GetString() ?? string.Empty;
                    // Record the property NAME position. Errors usually want to
                    // point at `"strategy"`, not at what follows the colon.
                    Add(positions, PathOf(containers, pendingProperty, advance: false),
                        reader.TokenStartIndex, lineStarts);
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                {
                    var path = PathOf(containers, pendingProperty, advance: true);
                    Add(positions, path, reader.TokenStartIndex, lineStarts);
                    containers.Push(new Container(path, isArray: reader.TokenType == JsonTokenType.StartArray));
                    pendingProperty = string.Empty;
                    break;
                }

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count > 0)
                    {
                        containers.Pop();
                    }

                    pendingProperty = string.Empty;
                    break;

                default:
                    // A scalar: string, number, true, false, null. Only worth
                    // recording for array elements — inside an object the
                    // property name above already claimed this path, and Add
                    // keeps the first position it is given.
                    Add(positions, PathOf(containers, pendingProperty, advance: true),
                        reader.TokenStartIndex, lineStarts);
                    pendingProperty = string.Empty;
                    break;
            }
        }

        return new JsonPositionIndex(positions);
    }

    /// <summary>
    /// Builds the path of the value about to be read. Inside an object that is
    /// parent + "." + property; inside an array it is parent + "[n]", where n
    /// advances only when we are actually consuming an element (hence
    /// <paramref name="advance"/> — a property name is not an array element).
    /// </summary>
    private static string PathOf(Stack<Container> containers, string property, bool advance)
    {
        if (containers.Count == 0)
        {
            return property;
        }

        var parent = containers.Peek();

        if (!parent.IsArray)
        {
            return parent.Path.Length == 0 ? property : $"{parent.Path}.{property}";
        }

        var index = advance ? parent.NextIndex++ : parent.NextIndex;
        return $"{parent.Path}[{index}]";
    }

    /// <summary>
    /// First position wins. The property name is read before its value, so this
    /// keeps errors pointing at the name rather than drifting to the value.
    /// </summary>
    private static void Add(
        Dictionary<string, (int, int)> positions,
        string path,
        long byteIndex,
        List<int> lineStarts)
    {
        if (path.Length > 0)
        {
            positions.TryAdd(path, Resolve(byteIndex, lineStarts));
        }
    }

    private static List<int> FindLineStarts(ReadOnlySpan<byte> utf8)
    {
        var starts = new List<int> { 0 };

        for (var i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>
    /// Byte offset to 1-based line and column. Column counts BYTES, not
    /// characters, so a line containing multi-byte UTF-8 before the error
    /// reports a slightly wide column. Accepted: the line number is what
    /// people navigate by, and config files here are ASCII in practice.
    /// </summary>
    private static (int Line, int Column) Resolve(long byteIndex, List<int> lineStarts)
    {
        var index = (int)byteIndex;

        // Binary search for the last line start at or before the token.
        var found = lineStarts.BinarySearch(index);
        var line = found >= 0 ? found : ~found - 1;

        if (line < 0)
        {
            line = 0;
        }

        return (line + 1, index - lineStarts[line] + 1);
    }

    /// <summary>Mutable because NextIndex advances as we walk an array's elements.</summary>
    private sealed class Container(string path, bool isArray)
    {
        public string Path { get; } = path;

        public bool IsArray { get; } = isArray;

        public int NextIndex;
    }
}
