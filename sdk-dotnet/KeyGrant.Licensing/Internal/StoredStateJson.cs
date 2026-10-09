using System.Text;
using System.Text.Json;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// <see cref="StoredState"/> as the Electron SDK's state file has it: <c>JSON.stringify</c> of the
/// state object, camelCase names, absent fields omitted.
/// </summary>
internal static class StoredStateJson
{
    /// <summary>Written back as it was read, however deep: a value is written as its text, never walked.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = Json.Encoder, MaxDepth = Json.AnyDepth };

    /// <summary>
    /// The state in <paramref name="json"/>; null for JSON null. Read to any depth <c>JSON.parse</c>
    /// reads, in one pass, so no file the reference would read is set aside, and whatever is read is
    /// written back. Throws <see cref="JsonException"/> when it is not JSON.
    /// </summary>
    public static StoredState? Read(string json)
    {
        var top = TopLevelJson.Read(json);
        if (top.Kind == JsonValueKind.Null) return null;
        // A number, a string or an array parses, and is no state anything reads: an empty one.
        if (top.Kind != JsonValueKind.Object) return new StoredState();

        var state = new StoredState();
        Dictionary<string, JsonElement>? extra = null;
        Dictionary<string, string>? deep = null;
        // JSON.parse keeps the last of a repeated name; a name .NET cannot hold is dropped.
        foreach (var (name, raw) in top.Members)
        {
            if (!raw.Holdable)
            {
                // Nested deeper than any field this SDK types: kept as its exact text, written back as it was.
                deep ??= new Dictionary<string, string>(StringComparer.Ordinal);
                deep[name] = raw.Text;
                continue;
            }
            var value = raw.Element();
            if (Typed(state, name, value) is { } typed)
            {
                state = typed;
                continue;
            }
            // A field this SDK does not know, or a known one holding what it cannot type (a word a
            // later SDK added, a number written as text): kept as it was, and written back as it was
            // unless a save changes the field. Absent to every decision here.
            extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            extra[name] = value;
        }
        if (extra is not null) state = state with { AdditionalFields = extra };
        if (deep is not null) state = state with { DeepFields = deep };
        return state;
    }

    /// <summary>
    /// Whether <paramref name="json"/> parses, and the state it holds (null for JSON null). What
    /// <c>JSON.parse</c> refuses is not parsed; a leading byte-order mark is forgiven. Never throws.
    /// </summary>
    public static bool TryRead(string json, out StoredState? state)
    {
        state = null;
        try
        {
            state = Read(json.Length > 0 && json[0] == (char)0xFEFF ? json[1..] : json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The state with the known field <paramref name="name"/> read from <paramref name="value"/>; null
    /// when it is no field this SDK knows, or holds what it cannot type.
    /// </summary>
    private static StoredState? Typed(StoredState state, string name, JsonElement value)
    {
        if (!StateFields.TryByName(name, out var field)) return null;
        object? typed = field switch
        {
            StateField.Refused => Names.RefusalOf(String(value)),
            StateField.DeviceKind => Names.DeviceKindOf(String(value)),
            StateField.RefusedFor => Strings(value),
            _ when StateFields.TypeOf(field) == typeof(string) => String(value),
            _ when StateFields.TypeOf(field) == typeof(long) => Long(value),
            _ when StateFields.TypeOf(field) == typeof(int) => Int(value),
            _ => null,
        };
        return typed is null ? null : StateFields.Set(state, field, typed);
    }

    public static string Write(StoredState state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteTo(writer, state);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void WriteTo(Utf8JsonWriter writer, StoredState state)
    {
        var extra = state.AdditionalFields;
        var deep = state.DeepFields;

        // What the file held under a name, untyped, as its exact text: re-encoding would refuse a
        // string .NET cannot hold. Null when it held nothing there.
        string? Untyped(string name) =>
            extra is not null && extra.TryGetValue(name, out var raw) ? raw.GetRawText()
            : deep is not null && deep.TryGetValue(name, out var text) ? text
            : null;

        // A known field: its typed value; else what the file held for it that could not be typed.
        bool Has(string name, object? value)
        {
            if (value is not null) return true;
            if (Untyped(name) is { } held)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(held, skipInputValidation: true);
            }
            return false;
        }

        writer.WriteStartObject();
        if (Has("key", state.Key)) writer.WriteString("key", state.Key);
        if (Has("activationId", state.ActivationId)) writer.WriteString("activationId", state.ActivationId);
        if (Has("lease", state.Lease)) writer.WriteString("lease", state.Lease);
        if (Has("trialStart", state.TrialStart)) writer.WriteNumber("trialStart", state.TrialStart!.Value);
        if (Has("trialEndsAt", state.TrialEndsAt)) writer.WriteNumber("trialEndsAt", state.TrialEndsAt!.Value);
        if (Has("trialLease", state.TrialLease)) writer.WriteString("trialLease", state.TrialLease);
        if (Has("trialAt", state.TrialAt)) writer.WriteNumber("trialAt", state.TrialAt!.Value);
        if (Has("lastSeen", state.LastSeen)) writer.WriteNumber("lastSeen", state.LastSeen!.Value);
        if (Has("checkedMajor", state.CheckedMajor)) writer.WriteNumber("checkedMajor", state.CheckedMajor!.Value);
        if (Has("keptMajor", state.KeptMajor)) writer.WriteNumber("keptMajor", state.KeptMajor!.Value);
        if (Has("backoffSince", state.BackoffSince)) writer.WriteNumber("backoffSince", state.BackoffSince!.Value);
        if (Has("backoffMs", state.BackoffMs)) writer.WriteNumber("backoffMs", state.BackoffMs!.Value);
        if (Has("refused", state.Refused)) writer.WriteString("refused", Names.Of(state.Refused!.Value));
        if (Has("refusedMajor", state.RefusedMajor)) writer.WriteNumber("refusedMajor", state.RefusedMajor!.Value);
        if (Has("refusedFor", state.RefusedFor))
        {
            writer.WriteStartArray("refusedFor");
            foreach (var claim in state.RefusedFor!) writer.WriteStringValue(claim);
            writer.WriteEndArray();
        }
        if (Has("deviceKind", state.DeviceKind)) writer.WriteString("deviceKind", Names.Of(state.DeviceKind!.Value));
        if (Has("verdictAt", state.VerdictAt)) writer.WriteNumber("verdictAt", state.VerdictAt!.Value);
        if (Has("rev", state.Rev)) writer.WriteNumber("rev", state.Rev!.Value);
        if (Has("purchaseToken", state.PurchaseToken)) writer.WriteString("purchaseToken", state.PurchaseToken);
        if (Has("purchaseTokenAt", state.PurchaseTokenAt)) writer.WriteNumber("purchaseTokenAt", state.PurchaseTokenAt!.Value);
        if (Has("claimAskedAt", state.ClaimAskedAt)) writer.WriteNumber("claimAskedAt", state.ClaimAskedAt!.Value);
        if (Has("trialReportedAt", state.TrialReportedAt)) writer.WriteNumber("trialReportedAt", state.TrialReportedAt!.Value);
        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                if (IsKnown(name)) continue;
                writer.WritePropertyName(name);
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
            }
        }
        if (deep is not null)
        {
            foreach (var (name, text) in deep)
            {
                if (IsKnown(name) || (extra?.ContainsKey(name) ?? false)) continue;
                writer.WritePropertyName(name);
                writer.WriteRawValue(text, skipInputValidation: true);
            }
        }
        writer.WriteEndObject();
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "key", "activationId", "lease", "trialStart", "trialEndsAt", "trialLease", "trialAt", "lastSeen",
        "checkedMajor", "keptMajor", "backoffSince", "backoffMs", "refused", "refusedMajor", "refusedFor",
        "deviceKind", "verdictAt", "rev", "purchaseToken", "purchaseTokenAt", "claimAskedAt", "trialReportedAt",
    };

    public static bool IsKnown(string name) => Known.Contains(name);

    private static string? String(JsonElement value) => Json.StringOf(value);

    private static long? Long(JsonElement value) => Json.TryLong(value, out var n) ? n : null;

    private static int? Int(JsonElement value) => Json.TryInt(value, out var n) ? n : null;

    private static IReadOnlyList<string>? Strings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (Json.StringOf(item) is { } claim) list.Add(claim);
        }
        return list;
    }
}
