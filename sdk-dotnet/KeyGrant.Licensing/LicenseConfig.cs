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
    /// The product's public key (the <c>publicJwk</c> of
    /// <c>GET https://api.keygrant.dev/v1/products/&lt;product&gt;/pubkey</c>), embedded at build time
    /// for offline verification. A JWK JSON string converts to it implicitly.
    /// </summary>
    public required PublicJwk PublicJwk { get; init; }

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
    /// The key in a JWK's JSON. Refuses one that carries a private key (<c>d</c>): that must never
    /// ship in an app.
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
            if (root.TryGetProperty("d", out _))
            {
                throw new ArgumentException("this JWK holds a PRIVATE key: embed the public one (no \"d\")", nameof(json));
            }
            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var x = Text("x") ?? throw new ArgumentException("a JWK names its key as \"x\"", nameof(json));
            var jwk = new PublicJwk { Kty = Text("kty"), Crv = Text("crv"), X = x };
            jwk.KeyBytes();
            return jwk;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("a JWK is JSON", nameof(json), error);
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
