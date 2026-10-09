using System.Text;
using System.Text.Json;

namespace KeyGrant.Licensing.Internal;

/// <summary>A JSON value's exact text, and how deeply it nests: 0 for a string, number, true, false or null; 1 for <c>[]</c> or <c>{}</c>.</summary>
internal readonly record struct RawJson(ReadOnlyMemory<byte> Utf8, int Depth)
{
    private static readonly JsonDocumentOptions ElementOptions = new() { MaxDepth = TopLevelJson.ElementDepth };

    /// <summary>Its exact text.</summary>
    public string Text => Encoding.UTF8.GetString(Utf8.Span);

    /// <summary>Whether it nests shallowly enough to be held as a <see cref="JsonElement"/> (<see cref="Element"/>).</summary>
    public bool Holdable => Depth <= TopLevelJson.ElementDepth;

    /// <summary>The value as a <see cref="JsonElement"/>; only for one that is <see cref="Holdable"/>.</summary>
    public JsonElement Element()
    {
        using var document = JsonDocument.Parse(Utf8, ElementOptions);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A JSON text's top-level value, read as <c>JSON.parse</c> reads it, to any depth, in ONE pass over the
/// text. System.Text.Json's <see cref="JsonDocument"/> takes time that grows with the square of the nesting
/// (a state file with a member 100 000 levels deep would take seconds to read, a million minutes, and hang
/// a status at launch), so only a value at most <see cref="ElementDepth"/> deep is ever held as a
/// <see cref="JsonElement"/>; a deeper one is kept as its text, or not read at all. Nothing the SDK reads
/// nests: its state fields, a server's answer and a lease's claims are all at the top level.
/// </summary>
internal sealed class TopLevelJson
{
    /// <summary>How deep a value may nest and still be held as a <see cref="JsonElement"/>.</summary>
    public const int ElementDepth = 64;

    /// <summary>No bound on the nesting but memory (a bit a level), as <c>JSON.parse</c>.</summary>
    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = int.MaxValue };

    private TopLevelJson(JsonValueKind kind, RawJson value, IReadOnlyList<KeyValuePair<string, RawJson>> members)
    {
        Kind = kind;
        Value = value;
        Members = members;
    }

    /// <summary>What the value is.</summary>
    public JsonValueKind Kind { get; }

    /// <summary>The whole value.</summary>
    public RawJson Value { get; }

    /// <summary>
    /// An object's members as <c>JSON.parse</c> keeps them: the last of a repeated name, in the place
    /// of its first; one whose name .NET cannot hold (an escaped lone surrogate) left out. None for any
    /// other value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, RawJson>> Members { get; }

    /// <summary>
    /// The top-level value of <paramref name="text"/>. Throws (a <see cref="JsonException"/>) where
    /// <c>JSON.parse</c> would: not JSON, or anything but white space after the value.
    /// </summary>
    public static TopLevelJson Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        if (!reader.Read()) throw new JsonException("no JSON value");
        var kind = KindOf(reader.TokenType);
        TopLevelJson top;
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var start = (int)reader.TokenStartIndex;
            var (members, deepest) = ReadMembers(ref reader, utf8);
            top = new TopLevelJson(kind, new RawJson(utf8.AsMemory(start, (int)reader.BytesConsumed - start), deepest + 1), members);
        }
        else
        {
            top = new TopLevelJson(kind, ValueAt(ref reader, utf8), []);
        }
        // White space only: the reader itself refuses anything else after the value.
        if (reader.Read()) throw new JsonException("more than one JSON value");
        return top;
    }

    /// <summary>The members of the object the reader has just entered, and the deepest of them; the reader ends on its end.</summary>
    private static (IReadOnlyList<KeyValuePair<string, RawJson>> Members, int Deepest) ReadMembers(ref Utf8JsonReader reader, byte[] utf8)
    {
        var order = new List<string>();
        var values = new Dictionary<string, RawJson>(StringComparer.Ordinal);
        var deepest = 0;
        while (true)
        {
            if (!reader.Read()) throw new JsonException("an object not closed");
            if (reader.TokenType == JsonTokenType.EndObject) break;
            string? name;
            try
            {
                name = reader.GetString();
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                // A name .NET cannot hold: JSON.parse keeps it, and nothing here reads it.
                name = null;
            }
            if (!reader.Read()) throw new JsonException("a member without a value");
            var value = ValueAt(ref reader, utf8);
            deepest = Math.Max(deepest, value.Depth);
            if (name is null) continue;
            if (!values.ContainsKey(name)) order.Add(name);
            values[name] = value;
        }
        return (order.Select(name => KeyValuePair.Create(name, values[name])).ToList(), deepest);
    }

    /// <summary>The value the reader is on, read to its end however it nests, without holding any of it.</summary>
    private static RawJson ValueAt(ref Utf8JsonReader reader, byte[] utf8)
    {
        var start = (int)reader.TokenStartIndex;
        var depth = 0;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            // A container's end is at the depth of its start; what it holds is deeper.
            var outer = reader.CurrentDepth;
            var deepest = outer;
            while (true)
            {
                if (!reader.Read()) throw new JsonException("a value not closed");
                var token = reader.TokenType;
                if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) deepest = Math.Max(deepest, reader.CurrentDepth);
                else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray && reader.CurrentDepth == outer) break;
            }
            depth = deepest - outer + 1;
        }
        return new RawJson(utf8.AsMemory(start, (int)reader.BytesConsumed - start), depth);
    }

    private static JsonValueKind KindOf(JsonTokenType token) => token switch
    {
        JsonTokenType.StartObject => JsonValueKind.Object,
        JsonTokenType.StartArray => JsonValueKind.Array,
        JsonTokenType.String => JsonValueKind.String,
        JsonTokenType.Number => JsonValueKind.Number,
        JsonTokenType.True => JsonValueKind.True,
        JsonTokenType.False => JsonValueKind.False,
        JsonTokenType.Null => JsonValueKind.Null,
        _ => throw new JsonException("not a JSON value"),
    };
}
