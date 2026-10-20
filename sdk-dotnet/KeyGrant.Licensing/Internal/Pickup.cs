using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// Key pickup (the SDK's section on it), decided without I/O: what a pickup call sends for what a person typed, the error
/// each refusal of the server is, the keys a confirm hands back (read leniently), and the key a restore activates. The
/// calls themselves are License.Pickup.cs.
/// </summary>
internal static class Pickup
{
    /// <summary>How many digits a code has.</summary>
    private const int CodeDigits = 6;

    /// <summary>How long (ms) the keys a confirm answered are kept in memory, never stored, for a confirm or a restore with the same address and code: a code's own life.</summary>
    public const long KeysKeepMs = 10 * 60_000;

    /// <summary>
    /// What a pickup call sends for what a person typed, or the error that refuses it before anything is sent.
    /// <paramref name="Code"/> null with no refusal is the ask for a code.
    /// </summary>
    internal sealed record Ask(string? Email, string? Code, PickupError? Refused)
    {
        public static Ask Refuse(PickupError error) => new(null, null, error);
    }

    /// <summary>
    /// What a pickup call sends for what a person typed. <paramref name="code"/> null is the ask for a code
    /// (<c>PickUpAsync(email)</c>); a code, even a blank one, is a confirm. The address is entered as an activation's is:
    /// trimmed, and a blank one, or one longer than any address, is <see cref="PickupError.InvalidEmail"/>. The code is
    /// entered as an activation's is (ends trimmed, spaces dropped): a blank one, or one that is not six ASCII digits, is
    /// <see cref="PickupError.WrongCode"/>, refused here without costing the address one of its wrong tries. The
    /// address is judged first.
    /// </summary>
    public static Ask AskFor(string email, string? code)
    {
        var entered = Wire.JsTrim(email);
        if (entered.Length == 0 || entered.Length > Wire.MaxEmail) return Ask.Refuse(PickupError.InvalidEmail);
        if (code is null) return new Ask(entered, null, null);
        var typed = Wire.JsTrim(code).Replace(" ", "", StringComparison.Ordinal);
        return typed.Length == CodeDigits && typed.All(c => c is >= '0' and <= '9') ? new Ask(entered, typed, null) : Ask.Refuse(PickupError.WrongCode);
    }

    /// <summary>
    /// The error a refused pickup call is, from the server's body (<paramref name="json"/>; null for none): each pickup
    /// word as itself; the per-network limit (<c>rate-limited</c>) as <see cref="PickupError.TooManyAttempts"/>; a count
    /// that could not be made (<c>unavailable</c>) as <see cref="PickupError.TryAgain"/>; a body the server refused to
    /// read (<c>invalid-request</c>) as <see cref="PickupError.InvalidEmail"/>, since what a person typed is all a pickup
    /// sends; and anything else, a proxy's page or an unknown product included, as <see cref="PickupError.Network"/>. The
    /// status is never read. <c>retryAfter</c> is kept, on the two errors that wait, when the body gives it as a whole
    /// number of seconds.
    /// </summary>
    public static (PickupError Error, long? RetryAfter) Refusal(JsonNode? json)
    {
        var error = Wire.ErrorOf(json) switch
        {
            "pickup-disabled" => PickupError.PickupDisabled,
            "wrong-code" => PickupError.WrongCode,
            "code-expired" => PickupError.CodeExpired,
            "too-many-attempts" or "rate-limited" => PickupError.TooManyAttempts,
            "try-again" or "unavailable" => PickupError.TryAgain,
            "invalid-request" => PickupError.InvalidEmail,
            _ => PickupError.Network,
        };
        if (error is not (PickupError.TooManyAttempts or PickupError.TryAgain)) return (error, null);
        var body = Meters.ObjectMembers(json);
        long? wait = body is not null && body.TryGetValue("retryAfter", out var seconds) && WholeFrom0(seconds) is { } whole ? whole : null;
        return (error, wait);
    }

    /// <summary>
    /// The keys a confirm's 200 answers, read leniently, in the order given; null when the body is not a pickup's answer at
    /// all (not an object, or <c>keys</c> not a list), which the SDK takes as no answer (<see cref="PickupError.Network"/>).
    /// A member that is not an object with a <c>key</c> (text, 1 to <see cref="Wire.MaxKey"/> UTF-16 units) is left out; every
    /// other field falls back when it is not what it should be.
    /// </summary>
    public static IReadOnlyList<PickedKey>? KeysOf(JsonNode? json)
    {
        var body = Meters.ObjectMembers(json);
        if (body is null || !body.TryGetValue("keys", out var listed) || listed.ValueKind != JsonValueKind.Array) return null;
        var keys = new List<PickedKey>();
        foreach (var item in listed.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && PickedKeyOf(item) is { } key) keys.Add(key);
        }
        return keys;
    }

    /// <summary>One key of a confirm's answer, or null when it has no key.</summary>
    private static PickedKey? PickedKeyOf(JsonElement item)
    {
        var member = Json.Properties(item).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!member.TryGetValue("key", out var keyElement) || Json.StringOf(keyElement) is not { Length: >= 1 and <= Wire.MaxKey } key) return null;
        string? Text(string name) => member.TryGetValue(name, out var v) ? Json.StringOf(v) : null;
        long? Whole(string name) => member.TryGetValue(name, out var v) ? WholeFrom0(v) : null;
        return new PickedKey(
            key,
            Text("model") ?? "",
            Text("modelName"),
            Text("state") ?? "",
            member.TryGetValue("entitlements", out var ent) ? ClaimsParser.EntitlementsOf(new RawJson(Encoding.UTF8.GetBytes(ent.GetRawText()), 1)) : ClaimsParser.EntitlementsOf(null),
            Whole("expiresAt"),
            member.TryGetValue("versions", out var versions) ? VersionsOf(versions) : null,
            Whole("deviceLimit") ?? 0,
            Whole("activeDevices") ?? 0,
            member.TryGetValue("held", out var held) && held.ValueKind == JsonValueKind.True,
            member.TryGetValue("sale", out var sale) ? SaleOf(sale) : null,
            Text("activationCode") is { } code && IsCode(code) ? code : null);
    }

    /// <summary>A whole number from 0 a double holds exactly, however written; null for anything else.</summary>
    private static long? WholeFrom0(JsonElement value) => Json.SafeInteger(value) is { } whole and >= 0 ? whole : null;

    /// <summary>Exactly six ASCII digits.</summary>
    private static bool IsCode(string code) => code.Length == CodeDigits && code.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// A key's versions as read: null for a key not yet bound and for anything that is not an object with a whole
    /// <c>minMajor</c>; else <c>minMajor</c>, and <c>maxMajor</c> and <c>upgradeUntil</c> each a whole number or null.
    /// </summary>
    private static PickedVersions? VersionsOf(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var member = Json.Properties(raw).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        long? Whole(string name) => member.TryGetValue(name, out var v) ? WholeFrom0(v) : null;
        return Whole("minMajor") is { } min ? new PickedVersions(min, Whole("maxMajor"), Whole("upgradeUntil")) : null;
    }

    /// <summary>A key's sale, kept only with a text <c>id</c> and whole <c>index</c> and <c>size</c> of at least 1.</summary>
    private static PickedSale? SaleOf(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var member = Json.Properties(raw).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        long? AtLeastOne(string name) => member.TryGetValue(name, out var v) && WholeFrom0(v) is { } whole and >= 1 ? whole : null;
        return member.TryGetValue("id", out var idElement) && Json.StringOf(idElement) is { } id && AtLeastOne("index") is { } index && AtLeastOne("size") is { } size
            ? new PickedSale(id, index, size)
            : null;
    }

    /// <summary>
    /// Whether a key covers the build's <paramref name="major"/> at <paramref name="now"/> (ms): a key not yet bound always
    /// does; else from <c>MinMajor</c> up to <c>MaxMajor</c> (null: no top), and every major from <c>MinMajor</c> up while
    /// <c>UpgradeUntil</c> is after <paramref name="now"/>.
    /// </summary>
    public static bool Covers(PickedVersions? versions, long major, long now)
    {
        if (versions is null) return true;
        if (major < versions.MinMajor) return false;
        return versions.MaxMajor is null || major <= versions.MaxMajor || (versions.UpgradeUntil is { } until && until > now);
    }

    /// <summary>Why no picked key is activated.</summary>
    internal enum Unchosen
    {
        /// <summary>The address holds no key of the product.</summary>
        NoKey,

        /// <summary>The keys are for an older major than the build: buy an upgrade.</summary>
        Upgrade,

        /// <summary>The keys' lowest version is newer than the build: update the app.</summary>
        Outdated,
    }

    /// <summary>The place in the keys of the one a restore activates, or why none is.</summary>
    internal sealed record Choice(int? Index, Unchosen? Refused);

    /// <summary>
    /// The key <c>RestoreByEmailAsync</c> activates, by its place in <paramref name="keys"/> (the server's order: first sold
    /// first), or the error it answers. Of the keys that cover the build's <paramref name="major"/>: the first this device
    /// already holds a seat on (<c>Held</c>, a reinstall takes that seat back), else the first with a free seat, else the first
    /// (its activation answers <see cref="LicenseError.Limit"/>). No key at all is <see cref="Unchosen.NoKey"/>; none that
    /// covers the build is <see cref="Unchosen.Upgrade"/> when one of them is for an older major than the build (buy an
    /// upgrade), else <see cref="Unchosen.Outdated"/> (update the app).
    /// </summary>
    public static Choice Choose(IReadOnlyList<PickedKey> keys, long major, long now)
    {
        if (keys.Count == 0) return new Choice(null, Unchosen.NoKey);
        var covering = Enumerable.Range(0, keys.Count).Where(i => Covers(keys[i].Versions, major, now)).ToList();
        var pick = covering.Cast<int?>().FirstOrDefault(i => keys[i!.Value].Held)
            ?? covering.Cast<int?>().FirstOrDefault(i => keys[i!.Value].ActiveDevices < keys[i.Value].DeviceLimit)
            ?? covering.Cast<int?>().FirstOrDefault();
        if (pick is not null) return new Choice(pick, null);
        return new Choice(null, keys.Any(k => k.Versions is { } v && v.MinMajor <= major) ? Unchosen.Upgrade : Unchosen.Outdated);
    }

    /// <summary>The word for an error: the one the wire, the vectors and the reference use.</summary>
    public static string Of(PickupError error) => error switch
    {
        PickupError.InvalidEmail => "invalid-email",
        PickupError.WrongCode => "wrong-code",
        PickupError.CodeExpired => "code-expired",
        PickupError.TooManyAttempts => "too-many-attempts",
        PickupError.PickupDisabled => "pickup-disabled",
        PickupError.TryAgain => "try-again",
        PickupError.Network => "network",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    /// <summary>The word for a reason no key is activated.</summary>
    public static string Of(Unchosen refused) => refused switch
    {
        Unchosen.NoKey => "no-key",
        Unchosen.Upgrade => "upgrade",
        Unchosen.Outdated => "outdated",
        _ => throw new ArgumentOutOfRangeException(nameof(refused)),
    };
}
