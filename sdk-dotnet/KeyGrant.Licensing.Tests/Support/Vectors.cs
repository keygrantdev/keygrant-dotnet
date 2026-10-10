using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// <c>conformance/vectors.json</c> at the repo root (or the file <c>KEYGRANT_VECTORS</c> names): what every
/// KeyGrant SDK must answer, generated from the reference (the Electron SDK) and answered here from each
/// vector's own JSON. The file's conventions: times in ms (lease claims in seconds); a state is a stored
/// state with absent fields unset; in a patch, <c>null</c> clears a field and an absent one is left as it
/// is; an answer of nothing is <c>null</c>; a status carries <c>entitlements</c> only when there are any,
/// their numbers doubles (5 and 5.0 alike).
/// </summary>
internal static class Vectors
{
    private static readonly Lazy<JsonObject> Root = new(Load);

    public static JsonObject All => Root.Value;

    public static string PathOnDisk { get; } = Find();

    public static JsonArray Group(string name) => All[name]?.AsArray() ?? throw new InvalidOperationException($"the vectors have no {name} group");

    public static JsonObject Item(string group, int index) => Group(group)[index]!.AsObject();

    /// <summary>The cases of a group, for a theory: each case's index and name (or its index, unnamed).</summary>
    public static TheoryData<int, string> Cases(string group, Func<JsonObject, bool>? where = null)
    {
        var data = new TheoryData<int, string>();
        var items = Group(group);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]!.AsObject();
            if (where is not null && !where(item)) continue;
            data.Add(i, Str(item["name"]) ?? $"#{i}");
        }
        return data;
    }

    private static JsonObject Load() => JsonNode.Parse(File.ReadAllText(PathOnDisk))!.AsObject();

    private static string Find()
    {
        if (Environment.GetEnvironmentVariable("KEYGRANT_VECTORS") is { Length: > 0 } given) return given;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "conformance", "vectors.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("conformance/vectors.json was not found above the test's folder");
    }

    // --- reading the vectors' JSON ------------------------------------------------------

    public static string? Str(JsonNode? node) => node is null ? null : Json.TryString(node, out var s) ? s : null;

    public static long? Long(JsonNode? node) => node is null ? null : Json.TryNumber(node, out var d) ? (long)d : null;

    public static int? Int(JsonNode? node) => node is null ? null : Json.TryNumber(node, out var d) ? (int)d : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? v.GetValue<bool>() : null;

    public static IReadOnlyList<string> Strings(JsonNode? node) => node?.AsArray().Select(n => Str(n)!).ToList() ?? [];

    /// <summary>A state as the vectors write one (the state file's JSON).</summary>
    public static StoredState State(JsonNode? node) => node is null ? new StoredState() : StoredState.FromJson(node.ToJsonString())!;

    /// <summary>A patch as the vectors write one: every field it names SET, and <c>null</c> CLEARED.</summary>
    public static StatePatch Patch(JsonNode? node)
    {
        var patch = new StatePatch();
        if (node is null) return patch;
        foreach (var (name, value) in node.AsObject())
        {
            var field = Enum.GetValues<StateField>().Single(f => StateFields.JsonName(f) == name);
            patch.Set(field, FieldValue(field, value));
        }
        return patch;
    }

    private static object? FieldValue(StateField field, JsonNode? value)
    {
        if (value is null) return null;
        var type = StateFields.TypeOf(field);
        if (type == typeof(string)) return Str(value);
        if (type == typeof(long)) return Long(value);
        if (type == typeof(int)) return Int(value);
        if (type == typeof(Refusal)) return Names.RefusalOf(Str(value));
        if (type == typeof(DeviceKind)) return Names.DeviceKindOf(Str(value));
        if (type == typeof(IReadOnlyList<string>)) return Strings(value);
        throw new InvalidOperationException($"no reader for {field}");
    }

    /// <summary>A patch as the vectors write one: a cleared field is <c>null</c>.</summary>
    public static JsonObject PatchJson(StatePatch patch)
    {
        var json = new JsonObject();
        foreach (var (field, value) in patch.Entries)
        {
            var name = StateFields.JsonName(field);
            json[name] = value is null ? null : JsonNode.Parse(StateFields.Set(new StoredState(), field, value).ToJson())![name]!.DeepClone();
        }
        return json;
    }

    /// <summary>A basis as the vectors write one (null for none).</summary>
    public static Basis BasisOf(JsonNode? node)
    {
        if (node is null) return Basis.None;
        Asked? AskedOf(JsonNode? n) => n is null ? null : new Asked(Long(n["at"])!.Value, Str(n["activationId"]), Long(n["seen"]));
        var trial = node["trial"];
        return new Basis
        {
            Answer = AskedOf(node["answer"]),
            Activation = AskedOf(node["activation"]),
            About = node["about"] is { } about ? new About(Str(about["activationId"])) : null,
            Trial = trial is null ? null : new Stamp(Long(trial["at"])!.Value, Long(trial["seen"])),
        };
    }

    /// <summary>The claims the pure decisions read, on the vectors' base claims.</summary>
    public static LeaseClaims Claims(long? iat = null, long? exp = null, long? upgradeUntil = null, long? major = null) => new()
    {
        Iss = "keygrant",
        Sub = "SLUICE-AAAA-BBBB-CCCC-DDDD",
        Product = "sluice",
        Aid = "act_1",
        Model = "perpetual",
        Lim = 3,
        Major = major ?? 3,
        Iat = iat ?? 1_789_913_600,
        Exp = exp ?? 1_792_505_600,
        UpgradeUntil = upgradeUntil,
    };

    /// <summary>Claims as parsed (defaults applied, unknown claims gone).</summary>
    public static JsonObject ClaimsJson(LeaseClaims claims)
    {
        var json = new JsonObject
        {
            ["iss"] = claims.Iss,
            ["sub"] = claims.Sub,
            ["product"] = claims.Product,
            ["aid"] = claims.Aid,
        };
        if (claims.Fp is not null) json["fp"] = claims.Fp;
        if (claims.Fpk is { } fpk) json["fpk"] = Names.Of(fpk);
        json["model"] = claims.Model;
        json["lim"] = claims.Lim;
        json["major"] = claims.Major;
        if (claims.MinMajor is { } min) json["minMajor"] = min;
        if (claims.UpgradeUntil is { } until) json["upgradeUntil"] = until;
        json["ent"] = EntitlementsJson(claims.Ent);
        json["iat"] = claims.Iat;
        json["exp"] = claims.Exp;
        if (claims.Grace is { } grace) json["grace"] = grace;
        if (claims.Test is { } test) json["test"] = test;
        return json;
    }

    /// <summary>A status as the vectors write one: only the fields it has, entitlements only when there are any (as <c>test</c> only when true).</summary>
    public static JsonObject StatusJson(LicenseStatus status)
    {
        var json = new JsonObject { ["state"] = Names.Of(status.State) };
        if (status.Reason is { } reason) json["reason"] = Names.Of(reason);
        if (status.Key is { } key) json["key"] = key;
        if (status.TrialDaysLeft is { } days) json["trialDaysLeft"] = days;
        if (status.TrialEndsAt is { } ends) json["trialEndsAt"] = ends;
        if (status.Test) json["test"] = true;
        if (status.Entitlements.Count > 0) json["entitlements"] = EntitlementsJson(status.Entitlements);
        if (status.Stalled) json["stalled"] = true;
        return json;
    }

    /// <summary>Entitlements as JSON writes them: each a boolean, a number or a string.</summary>
    public static JsonObject EntitlementsJson(IReadOnlyDictionary<string, Entitlement> entitlements)
    {
        var json = new JsonObject();
        foreach (var (name, value) in entitlements)
        {
            json[name] = value.Flag is { } flag ? JsonValue.Create(flag)
                : value.Number is { } number ? JsonValue.Create(number)
                : JsonValue.Create(value.Text!);
        }
        return json;
    }
}
