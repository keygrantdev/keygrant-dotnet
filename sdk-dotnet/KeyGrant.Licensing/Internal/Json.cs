using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Internal;

/// <summary>JSON as the reference writes and reads it: unescaped text, numbers as JavaScript numbers.</summary>
internal static class Json
{
    /// <summary>
    /// Escaping as <c>JSON.stringify</c> does it: only what JSON requires, so a state file or a request
    /// body reads the same as the Electron SDK's. Never embedded in HTML.
    /// </summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// How deep the JSON this SDK writes may nest: no bound but memory, as <c>JSON.stringify</c> (a
    /// writer costs a bit a level, and writes a value it was handed without recursing).
    /// </summary>
    public const int AnyDepth = int.MaxValue;

    public static readonly JsonSerializerOptions Options = new() { Encoder = Encoder, MaxDepth = AnyDepth };

    /// <summary>A body as the text sent.</summary>
    public static string Text(JsonNode node) => node.ToJsonString(Options);

    /// <summary>
    /// An answer's body as <c>JSON.parse</c> reads it, as nodes the SDK's reads never throw on: at the
    /// top level, where every member the SDK reads is, the last of a repeated name kept
    /// (System.Text.Json's own nodes throw on first use of an object with one), a name .NET cannot hold
    /// dropped, and a string it cannot hold as nothing. Deeper values are kept as they were written; a
    /// member nested deeper than <see cref="TopLevelJson.ElementDepth"/> is left out (nothing the SDK
    /// reads nests, and holding it would take time growing with the square of its depth). Read in one
    /// pass however deep it nests. Null when it is not JSON.
    /// </summary>
    public static JsonNode? ParseBody(string text)
    {
        try
        {
            var top = TopLevelJson.Read(text);
            if (top.Kind != JsonValueKind.Object) return top.Value.Holdable ? ValueOf(top.Value.Element()) : null;
            var body = new JsonObject();
            foreach (var (name, value) in top.Members)
            {
                if (value.Holdable) body[name] = ValueOf(value.Element());
            }
            return body;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A body an HTTP adapter handed over, read again as <see cref="ParseBody"/> reads text, so a node
    /// built by <c>JsonNode.Parse</c> from a body with a repeated name cannot throw in the SDK. A body
    /// System.Text.Json cannot write whole (a string escaping a lone surrogate, anywhere in it) is read
    /// member by member instead, leaving out only a member that cannot be written: a nested value the
    /// SDK never reads costs nothing else in the answer.
    /// </summary>
    public static JsonNode? Lenient(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return ParseBody(node.ToJsonString(Options));
        }
        catch (Exception)
        {
            return node is JsonObject body ? MemberByMember(body) : null;
        }
    }

    /// <summary>An object's members each read again (<see cref="Lenient"/>), one that cannot be written left out; null when its members cannot be read at all.</summary>
    private static JsonObject? MemberByMember(JsonObject body)
    {
        try
        {
            var read = new JsonObject();
            foreach (var (name, value) in body)
            {
                try
                {
                    read[name] = value is null ? null : ParseBody(value.ToJsonString(Options));
                }
                catch (Exception)
                {
                    // Not a value .NET can write, and so none the SDK could read: left out.
                }
            }
            return read;
        }
        catch (Exception)
        {
            // Members that cannot be read at all (a repeated name in the adapter's own node, beside a
            // value that cannot be written): no body, as for any unreadable answer.
            return null;
        }
    }

    /// <summary>
    /// An object's properties as <c>JSON.parse</c> keeps them: the last of a repeated name (in the
    /// order names were first seen), and none whose name .NET cannot hold (an escaped lone surrogate).
    /// </summary>
    public static List<KeyValuePair<string, JsonElement>> Properties(JsonElement element)
    {
        var order = new List<string>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                continue;
            }
            if (!values.ContainsKey(name)) order.Add(name);
            values[name] = property.Value;
        }
        return order.Select(name => new KeyValuePair<string, JsonElement>(name, values[name])).ToList();
    }

    /// <summary>A value (an element of its own) as a node: a container kept as written (read only when used), a string only when .NET can hold it.</summary>
    private static JsonNode? ValueOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        JsonValueKind.String => StringOf(element) is { } text ? JsonValue.Create(text) : null,
        JsonValueKind.Number => JsonValue.Create(element),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        _ => null,
    };

    /// <summary>
    /// A JSON value's string, when it is a string .NET can hold: one escaping a lone surrogate, which
    /// System.Text.Json refuses to read, is no string (never an exception on a server's answer).
    /// </summary>
    public static bool TryString(JsonNode? node, out string value)
    {
        value = "";
        try
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
            if (v.TryGetValue(out string? text) && text is not null)
            {
                value = text;
                return true;
            }
            // A value of another CLR type that serialises as a string.
            var parsed = JsonSerializer.Deserialize<string>(v.ToJsonString());
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            return false;
        }
    }

    /// <summary>An element's string, or null when it is none (or one System.Text.Json cannot read: a lone surrogate).</summary>
    public static string? StringOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        try
        {
            return element.GetString();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A JSON value's number, when it is a number (any representation the node holds).</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        try
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
            return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
        catch (Exception)
        {
            value = 0;
            return false;
        }
    }

    /// <summary>A number as a whole millisecond count: floored, and held to the range of a long.</summary>
    public static long ToLong(double value)
    {
        var floored = Math.Floor(value);
        if (floored >= 9.2233720368547758E18) return long.MaxValue;
        if (floored <= -9.2233720368547758E18) return long.MinValue;
        return (long)floored;
    }

    /// <summary>An element's number as a long, when it is a number.</summary>
    public static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt64(out value)) return true;
        if (!element.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
        value = ToLong(d);
        return true;
    }

    /// <summary>An element's number as an int, when it is a whole number an int holds.</summary>
    public static bool TryInt(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt32(out value)) return true;
        if (!element.TryGetDouble(out var d) || !double.IsFinite(d) || Math.Floor(d) != d) return false;
        if (d < int.MinValue || d > int.MaxValue) return false;
        value = (int)d;
        return true;
    }
}
