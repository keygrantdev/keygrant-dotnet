using System.Text.Json;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

/// <summary>How a <see cref="License"/> is set up.</summary>
public sealed class LicenseConfig
{
    /// <summary>How long a request may take, unless the call or <see cref="HttpTimeout"/> says otherwise: 15 seconds.</summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The product slug, matching the KeyGrant product (e.g. <c>"sluice"</c>).</summary>
    public required string Product { get; init; }

    /// <summary>The KeyGrant API base, e.g. <c>"https://api.keygrant.dev"</c>.</summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>
    /// The product's public keys, embedded at build time for offline verification: the key that signs its
    /// leases, and the next one once the product has one staged (the <c>keys</c> of
    /// <c>GET https://api.keygrant.dev/v1/products/&lt;product&gt;/pubkey</c>; <see cref="PublicJwk.ParseSet"/>
    /// reads that JSON). A lease naming its key (<c>kid</c>) is verified by that key; one naming none by each
    /// in turn. Give this or <see cref="PublicJwk"/>.
    /// </summary>
    public IReadOnlyList<PublicJwk>? PublicJwks { get; init; }

    /// <summary>
    /// One public key: a key set of one (the <c>publicJwk</c> of the same endpoint). A JWK JSON string converts
    /// to it implicitly. Give this or <see cref="PublicJwks"/>.
    /// </summary>
    public PublicJwk? PublicJwk { get; init; }

    /// <summary>
    /// The running app's major version. Defaults to 1; set it, as the server binds keys by it. Read as the
    /// server reads a version: a 0.x build (0) runs as major 1, and so does any value below 1.
    /// </summary>
    public int Major { get; init; } = 1;

    /// <summary>A human device name reported to the server (cut to 120 characters).</summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Whether a licence is held to the device it was activated on (default true). False for a product
    /// deployed on non-persistent, pooled VDI with roaming profiles, where every session may be another
    /// machine: the SDK then reports no kind for its fingerprint, judges no lease by its device, and
    /// sends the server no fingerprints to check. The device limit still counts seats.
    /// </summary>
    public bool DeviceHold { get; init; } = true;

    /// <summary>
    /// How long a request may take before the SDK abandons it, for every call that names no shorter
    /// bound of its own (default <see cref="DefaultHttpTimeout"/>). Kept by the SDK whatever the HTTP
    /// adapter does with it.
    /// </summary>
    public TimeSpan HttpTimeout { get; init; } = DefaultHttpTimeout;

    /// <summary>Adapter overrides. Any left out uses the default.</summary>
    public LicenseAdapters? Adapters { get; init; }

    /// <summary>
    /// What the SDK's timers run on (request bounds, the wait behind a stuck call). Default
    /// <see cref="TimeProvider.System"/>; tests give a fake one. The licence clock is <see cref="LicenseAdapters.Clock"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// A product's Ed25519 public key as a JWK (<c>{"kty":"OKP","crv":"Ed25519","x":"..."}</c>). A JWK JSON
/// string converts to it implicitly, so <c>PublicJwk = "{...}"</c> works.
/// </summary>
public sealed class PublicJwk
{
    /// <summary>The key type: <c>"OKP"</c>, or null.</summary>
    public string? Kty { get; init; }

    /// <summary>The curve: <c>"Ed25519"</c>, or null.</summary>
    public string? Crv { get; init; }

    /// <summary>The public key, base64url.</summary>
    public required string X { get; init; }

    /// <summary>
    /// The key in a JWK's JSON, read as every KeyGrant SDK reads one: <c>kty</c> "OKP" and <c>crv</c>
    /// "Ed25519", both required; no private key (<c>d</c>), which must never ship in an app; <c>use</c>,
    /// when given, "sig"; <c>key_ops</c>, when given, a list naming "verify"; <c>alg</c>, when given,
    /// "EdDSA" or "Ed25519"; and an <c>x</c> of 32 bytes, canonical base64url once its trailing <c>=</c>
    /// are dropped. No other member is read.
    /// </summary>
    /// <param name="json">The JWK as JSON.</param>
    /// <returns>The public key.</returns>
    /// <exception cref="ArgumentException">It is not an Ed25519 public JWK.</exception>
    public static PublicJwk Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("a JWK is a JSON object", nameof(json));
            if (RefusalOf(root) is { } refused) throw new ArgumentException(refused, nameof(json));
            var jwk = new PublicJwk { Kty = TextOf(root, "kty"), Crv = TextOf(root, "crv"), X = TextOf(root, "x")! };
            jwk.KeyBytes();
            return jwk;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("a JWK is JSON", nameof(json), error);
        }
    }

    /// <summary>Why a JWK's members are not an Ed25519 public key's (<see cref="Parse"/>), or null when they are.</summary>
    private static string? RefusalOf(JsonElement root)
    {
        bool Has(string name) => root.TryGetProperty(name, out _);
        if (TextOf(root, "kty") != "OKP" || TextOf(root, "crv") != "Ed25519") return "an Ed25519 JWK has kty \"OKP\" and crv \"Ed25519\"";
        if (Has("d")) return "this JWK holds a PRIVATE key: embed the public one (no \"d\")";
        if (Has("use") && TextOf(root, "use") != "sig") return "a JWK for verifying has use \"sig\"";
        if (root.TryGetProperty("key_ops", out var ops) && !NamesVerify(ops)) return "a JWK for verifying has key_ops naming \"verify\"";
        if (Has("alg") && TextOf(root, "alg") is not ("EdDSA" or "Ed25519")) return "an Ed25519 JWK has alg \"EdDSA\"";
        return TextOf(root, "x") is null ? "a JWK names its key as \"x\"" : null;
    }

    private static string? TextOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool NamesVerify(JsonElement ops) =>
        ops.ValueKind == JsonValueKind.Array
        && ops.EnumerateArray().Any(op => op.ValueKind == JsonValueKind.String && op.GetString() == "verify");

    /// <summary>
    /// The keys of a JWK set's JSON (<c>{"keys": [...]}</c>, as <c>/pubkey</c> and the dashboard's Copy key set
    /// give it), or of a JSON list of JWKs, in their order. Refuses an empty set, and any key
    /// <see cref="Parse"/> refuses.
    /// </summary>
    /// <param name="json">The JWK set, or the list, as JSON.</param>
    /// <returns>The public keys.</returns>
    /// <exception cref="ArgumentException">It is not a set of Ed25519 public JWKs.</exception>
    public static IReadOnlyList<PublicJwk> ParseSet(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("keys", out var keys) ? keys : root;
            if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException("a JWK set is an object with a keys list, or a list of JWKs", nameof(json));
            var parsed = list.EnumerateArray().Select(jwk => Parse(jwk.GetRawText())).ToList();
            if (parsed.Count == 0) throw new ArgumentException("a JWK set holds at least one key", nameof(json));
            return parsed;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("a JWK set is JSON", nameof(json), error);
        }
    }

    /// <summary>A JWK's JSON, as its key.</summary>
    /// <param name="json">The JWK as JSON.</param>
    public static implicit operator PublicJwk(string json) => Parse(json);

    /// <summary>The raw 32-byte key, checked.</summary>
    internal byte[] KeyBytes()
    {
        if (Kty is not null && Kty != "OKP") throw new ArgumentException($"an Ed25519 JWK has kty \"OKP\", not \"{Kty}\"");
        if (Crv is not null && Crv != "Ed25519") throw new ArgumentException($"an Ed25519 JWK has crv \"Ed25519\", not \"{Crv}\"");
        var bytes = Base64Url.Decode((X ?? "").TrimEnd('='));
        if (bytes is null || bytes.Length != 32) throw new ArgumentException("an Ed25519 public key (x) is 32 bytes, base64url");
        return bytes;
    }
}
