using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// A lease's claims, as the server signs them (seconds since the epoch for every time). Unknown claims are
/// ignored; <c>major</c> defaults to 0, and <c>ent</c> is read leniently (<see cref="ClaimsParser"/>):
/// absent or malformed is none.
/// </summary>
internal sealed record LeaseClaims
{
    public required string Iss { get; init; }
    public required string Sub { get; init; }
    public required string Product { get; init; }
    public required string Aid { get; init; }
    public string? Fp { get; init; }
    public DeviceKind? Fpk { get; init; }
    public required string Model { get; init; }
    public long Lim { get; init; }
    public long Major { get; init; }
    public long? MinMajor { get; init; }
    public long? UpgradeUntil { get; init; }
    public IReadOnlyDictionary<string, Entitlement> Ent { get; init; } = ReadOnlyDictionary<string, Entitlement>.Empty;
    public long Iat { get; init; }
    public long Exp { get; init; }
    public bool? Grace { get; init; }
    public bool? Test { get; init; }
}

/// <summary>Why a lease failed verification.</summary>
internal enum VerifyFailure
{
    Malformed,
    /// <summary>The lease names a key (<c>kid</c>) the key set does not hold.</summary>
    UnknownKey,
    BadSignature,
    BadClaims,
    Expired,
}

/// <summary>A lease verified offline: valid with its claims, or why not (an expired one with its claims).</summary>
internal sealed record VerifyResult(bool Valid, VerifyFailure? Reason, LeaseClaims? Claims)
{
    public static VerifyResult Ok(LeaseClaims claims) => new(true, null, claims);

    public static VerifyResult Failed(VerifyFailure reason, LeaseClaims? claims = null) => new(false, reason, claims);

    public static string NameOf(VerifyFailure reason) => reason switch
    {
        VerifyFailure.Malformed => "malformed",
        VerifyFailure.UnknownKey => "unknown-key",
        VerifyFailure.BadSignature => "bad-signature",
        VerifyFailure.BadClaims => "bad-claims",
        VerifyFailure.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>
/// The lease verifier, against a product's key set: the key a lease names by its id (<c>kid</c>), or each
/// key in turn for a lease that names none; then the claims' shape, then the expiry.
/// </summary>
internal sealed class LeaseVerifier
{
    private readonly IReadOnlyList<(string Kid, byte[] Key)> keys;

    /// <summary>A key set of one.</summary>
    public LeaseVerifier(byte[] publicKey)
        : this([publicKey])
    {
    }

    /// <summary>A key set: each raw 32-byte key in the order given, a key given twice kept once.</summary>
    public LeaseVerifier(IEnumerable<byte[]> publicKeys)
    {
        var set = new List<(string, byte[])>();
        foreach (var publicKey in publicKeys)
        {
            if (publicKey.Length != Ed25519PublicKeyParameters.KeySize)
            {
                throw new ArgumentException($"an Ed25519 public key is {Ed25519PublicKeyParameters.KeySize} bytes", nameof(publicKeys));
            }
            var kid = KeyIds.Of(publicKey);
            // Held as bytes: 32 that are not a point on the curve are a key all the same, and verify nothing.
            if (!set.Exists(k => k.Item1 == kid)) set.Add((kid, publicKey.ToArray()));
        }
        if (set.Count == 0) throw new ArgumentException("a key set holds at least one public key", nameof(publicKeys));
        keys = set;
    }

    /// <summary>The ids of the keys in the set, in its order: what a check-in reports as <c>kids</c>.</summary>
    public IReadOnlyList<string> KeyIdList => keys.Select(k => k.Kid).ToList();

    /// <summary>Verify <paramref name="token"/> at <paramref name="nowMs"/>. Never throws.</summary>
    public VerifyResult Verify(string token, long nowMs)
    {
        try
        {
            return VerifyOrThrow(token, nowMs);
        }
        catch (Exception)
        {
            // Nothing below is expected to throw; a lease that makes it is no lease.
            return VerifyResult.Failed(VerifyFailure.Malformed);
        }
    }

    private VerifyResult VerifyOrThrow(string token, long nowMs)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return VerifyResult.Failed(VerifyFailure.Malformed);
        var (header, payload, signature) = (parts[0], parts[1], parts[2]);

        var signatureBytes = Base64Url.Decode(signature);
        if (signatureBytes is null) return VerifyResult.Failed(VerifyFailure.Malformed);
        // The key the header names, else each key in turn; one it names that the set lacks is told apart.
        var kid = KeyIdOf(header);
        var candidates = kid is null ? keys : keys.Where(k => k.Kid == kid).ToList();
        if (candidates.Count == 0) return VerifyResult.Failed(VerifyFailure.UnknownKey);
        var signed = Encoding.UTF8.GetBytes($"{header}.{payload}");
        if (!candidates.Any(k => SignatureHolds(k.Key, signed, signatureBytes)))
        {
            return VerifyResult.Failed(VerifyFailure.BadSignature);
        }

        var payloadBytes = Base64Url.Decode(payload);
        var claims = payloadBytes is null ? null : ClaimsParser.Parse(Utf8Text(payloadBytes));
        if (claims is null) return VerifyResult.Failed(VerifyFailure.BadClaims);

        // Times compared as JavaScript does them: whole seconds times a thousand against milliseconds.
        if ((double)claims.Exp * 1000 < nowMs) return VerifyResult.Failed(VerifyFailure.Expired, claims);
        return VerifyResult.Ok(claims);
    }

    /// <summary>
    /// UTF-8 decoded as <c>TextDecoder</c> decodes it: a malformed sequence becomes U+FFFD, and a leading
    /// byte-order mark is dropped.
    /// </summary>
    private static string Utf8Text(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;
    }

    /// <summary>
    /// The key a lease's header names (<c>kid</c>), or null when it names none. A header names a key only
    /// when it is, byte for byte, the header the server writes for one: canonical base64url of
    /// <c>{"alg":"EdDSA","typ":"JWT","kid":"&lt;kid&gt;"}</c>, <c>&lt;kid&gt;</c> 1 to 64 characters of the
    /// base64url alphabet. Any other header names no key. It is matched as bytes, never parsed.
    /// </summary>
    private static string? KeyIdOf(string header)
    {
        var bytes = Base64Url.Decode(header);
        if (bytes is null) return null;
        // Each byte as one character: a byte past ASCII matches nothing below.
        var text = Encoding.Latin1.GetString(bytes);
        // Long enough for both ends apart: `..."kid":"}` ends with `"}` only through the start's own quote.
        if (text.Length < NamedHeaderStart.Length + NamedHeaderEnd.Length) return null;
        if (!text.StartsWith(NamedHeaderStart, StringComparison.Ordinal) || !text.EndsWith(NamedHeaderEnd, StringComparison.Ordinal)) return null;
        var kid = text[NamedHeaderStart.Length..^NamedHeaderEnd.Length];
        return kid.Length is >= 1 and <= 64 && kid.All(IsBase64UrlChar) ? kid : null;
    }

    private const string NamedHeaderStart = "{\"alg\":\"EdDSA\",\"typ\":\"JWT\",\"kid\":\"";
    private const string NamedHeaderEnd = "\"}";

    private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    private static bool SignatureHolds(byte[] key, byte[] data, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The claims schema, checked as the server's own schema checks it.</summary>
internal static partial class ClaimsParser
{
    /// <summary>The claims the schema checks; any other is stripped and never read.</summary>
    private static readonly HashSet<string> Checked = new(StringComparer.Ordinal)
    {
        "iss", "sub", "product", "aid", "fp", "fpk", "model", "lim", "major", "minMajor", "upgradeUntil", "ent", "iat", "exp", "grace", "test",
    };

    /// <summary>
    /// The claims in a payload's text, or null when it is not JSON or not their shape. Read to any depth
    /// JSON.parse reads, in one pass: an unknown claim is never read however it nests, nor a member of
    /// <c>ent</c> that is not a flag, a number or a text (<see cref="EntitlementsOf"/>); every other claim
    /// the schema checks is a string, a number or a boolean, so one nested deeper than
    /// <see cref="TopLevelJson.ElementDepth"/> fails as the schema fails it. Never throws: a string .NET cannot
    /// hold (an escaped lone surrogate) in a claim the schema checks is no claim it could check either.
    /// </summary>
    public static LeaseClaims? Parse(string text)
    {
        try
        {
            var top = TopLevelJson.Read(text);
            if (top.Kind != JsonValueKind.Object) return null;
            // JSON.parse keeps the last of a repeated name; a name .NET cannot hold is no claim the schema
            // reads.
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            RawJson? ent = null;
            foreach (var (name, value) in top.Members)
            {
                if (!Checked.Contains(name)) continue;
                // Read leniently, however it nests: it never costs the lease.
                if (name == "ent")
                {
                    ent = value;
                    continue;
                }
                if (!value.Holdable) return null;
                fields[name] = value.Element();
            }
            return FromFields(fields, ent);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The claims, each group checked as the schema checks it, the first that fails failing them all; <c>ent</c> never fails.</summary>
    private static LeaseClaims? FromFields(Dictionary<string, JsonElement> fields, RawJson? ent)
    {
        if (!Holder(fields, out var sub, out var product, out var aid)) return null;
        if (!Seat(fields, out var fp, out var fpk)) return null;
        if (!Model(fields, out var model)) return null;
        if (!Coverage(fields, out var coverage)) return null;
        if (!Times(fields, out var iat, out var exp)) return null;
        if (!OptionalBoolean(fields, "grace", out var grace) || !OptionalBoolean(fields, "test", out var test)) return null;

        return new LeaseClaims
        {
            Iss = "keygrant",
            Sub = sub,
            Product = product,
            Aid = aid,
            Fp = fp,
            Fpk = fpk,
            Model = model,
            Lim = coverage.Lim,
            Major = coverage.Major,
            MinMajor = coverage.MinMajor,
            UpgradeUntil = coverage.UpgradeUntil,
            Ent = EntitlementsOf(ent),
            Iat = iat,
            Exp = exp,
            Grace = grace,
            Test = test,
        };
    }

    /// <summary><c>iss</c> exactly <c>"keygrant"</c>, and a <c>sub</c>, <c>product</c> and <c>aid</c> of at least one character.</summary>
    private static bool Holder(Dictionary<string, JsonElement> fields, out string sub, out string product, out string aid)
    {
        (sub, product, aid) = ("", "", "");
        if (!fields.TryGetValue("iss", out var iss) || iss.ValueKind != JsonValueKind.String || iss.GetString() != "keygrant") return false;
        return NonEmptyString(fields, "sub", out sub) && NonEmptyString(fields, "product", out product) && NonEmptyString(fields, "aid", out aid);
    }

    /// <summary>The device the seat is held to: <c>fp</c> of 1 to 128 characters, <c>fpk</c> a kind; each optional.</summary>
    private static bool Seat(Dictionary<string, JsonElement> fields, out string? fp, out DeviceKind? fpk)
    {
        (fp, fpk) = (null, null);
        if (fields.TryGetValue("fp", out var fpElement))
        {
            if (fpElement.ValueKind != JsonValueKind.String) return false;
            fp = fpElement.GetString()!;
            if (fp.Length is < 1 or > 128) return false;
        }
        if (fields.TryGetValue("fpk", out var fpkElement))
        {
            fpk = fpkElement.ValueKind == JsonValueKind.String ? Names.DeviceKindOf(fpkElement.GetString()) : null;
            if (fpk is null) return false;
        }
        return true;
    }

    /// <summary><c>model</c>: perpetual, subscription or trial.</summary>
    private static bool Model(Dictionary<string, JsonElement> fields, out string model)
    {
        model = "";
        if (!fields.TryGetValue("model", out var element) || element.ValueKind != JsonValueKind.String) return false;
        model = element.GetString()!;
        return model is "perpetual" or "subscription" or "trial";
    }

    /// <summary>What the key covers: its device limit and its range of majors.</summary>
    private readonly record struct Covers(long Lim, long Major, long? MinMajor, long? UpgradeUntil);

    /// <summary><c>lim</c> positive; <c>major</c> non-negative (0 when absent); <c>minMajor</c> positive and <c>upgradeUntil</c> non-negative, each optional.</summary>
    private static bool Coverage(Dictionary<string, JsonElement> fields, out Covers coverage)
    {
        coverage = default;
        if (!Integer(fields, "lim", required: true, out var lim) || lim <= 0) return false;
        if (!Integer(fields, "major", required: false, out var major) || major < 0) return false;
        long? minMajor = null;
        if (fields.ContainsKey("minMajor"))
        {
            if (!Integer(fields, "minMajor", required: true, out var min) || min <= 0) return false;
            minMajor = min;
        }
        long? upgradeUntil = null;
        if (fields.ContainsKey("upgradeUntil"))
        {
            if (!Integer(fields, "upgradeUntil", required: true, out var until) || until < 0) return false;
            upgradeUntil = until;
        }
        coverage = new Covers(lim, major, minMajor, upgradeUntil);
        return true;
    }

    /// <summary><c>iat</c> and <c>exp</c>: whole, non-negative seconds.</summary>
    private static bool Times(Dictionary<string, JsonElement> fields, out long iat, out long exp)
    {
        exp = 0;
        return Integer(fields, "iat", required: true, out iat) && iat >= 0 && Integer(fields, "exp", required: true, out exp) && exp >= 0;
    }

    private static bool NonEmptyString(Dictionary<string, JsonElement> fields, string name, out string value)
    {
        value = "";
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return value.Length >= 1;
    }

    /// <summary>
    /// A whole number (as <c>Number.isInteger</c> reads one), held as a long. Absent and not
    /// <paramref name="required"/>: 0 (the default of <c>major</c>).
    /// </summary>
    private static bool Integer(Dictionary<string, JsonElement> fields, string name, bool required, out long value)
    {
        value = 0;
        if (!fields.TryGetValue(name, out var element)) return !required;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt64(out value)) return true;
        if (!element.TryGetDouble(out var d) || !double.IsFinite(d) || Math.Floor(d) != d) return false;
        value = Json.ToLong(d);
        return true;
    }

    private static bool OptionalBoolean(Dictionary<string, JsonElement> fields, string name, out bool? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element)) return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }
}
