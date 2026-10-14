using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// A licence file's claims, as the reference's schema reads them (seconds for times): unknown claims ignored,
/// an optional claim absent or of its type (null is not absent, but for <c>ent</c>), and an integer any whole
/// JSON number (<c>4.0</c> is 4). A key file's versions and entitlements are not read even when present.
/// </summary>
internal sealed record LicenseFileClaims
{
    public required LicenseFileKind Kind { get; init; }
    public required string Fid { get; init; }
    public required string Sub { get; init; }
    public required string Product { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public long Iat { get; init; }
    public bool? Test { get; init; }

    /// <summary>An offline file's highest major (<c>OPEN_MAJOR</c> for no upper bound); 0 on a key file.</summary>
    public long Major { get; init; }

    /// <summary>An offline file's lowest major, when it names one.</summary>
    public long? MinMajor { get; init; }

    /// <summary>An offline file's entitlements, read leniently as a lease's are.</summary>
    public IReadOnlyDictionary<string, Entitlement> Ent { get; init; } = ReadOnlyDictionary<string, Entitlement>.Empty;
}

/// <summary>Why a licence file's token did not verify.</summary>
internal enum FileVerifyFailure
{
    Malformed,
    NotALicenseFile,
    UnknownKey,
    BadSignature,
    BadClaims,
}

/// <summary>A licence file's token verified offline: valid with its claims, or why not.</summary>
internal sealed record FileVerifyResult(bool Valid, FileVerifyFailure? Reason, LicenseFileClaims? Claims)
{
    public static string NameOf(FileVerifyFailure reason) => reason switch
    {
        FileVerifyFailure.Malformed => "malformed",
        FileVerifyFailure.NotALicenseFile => "not-a-license-file",
        FileVerifyFailure.UnknownKey => "unknown-key",
        FileVerifyFailure.BadSignature => "bad-signature",
        FileVerifyFailure.BadClaims => "bad-claims",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>
/// Licence files: the token a file's content holds (<see cref="TokenOf"/>) and the token verified against
/// a key set (<see cref="Verify"/>). A licence file is a compact JWS signed with the product's Ed25519 key,
/// as a lease is, but never taken for one, either way: its header is exactly
/// <c>{"alg":"EdDSA","typ":"kglicense+jwt","kid":"&lt;kid&gt;"}</c>, and its claims carry none of the
/// <c>aid</c>, <c>lim</c>, <c>exp</c> and <c>model</c> every lease reader requires.
/// </summary>
internal static partial class LicenseFiles
{
    private const string FileHeaderStart = "{\"alg\":\"EdDSA\",\"typ\":\"kglicense+jwt\",\"kid\":\"";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The token a licence file's content holds, or null when it is no licence file's container: more than
    /// <see cref="LicenseFileFormat.MaxBytes"/>; not UTF-8 (read strictly); or not exactly one line once each
    /// line (split on LF) is trimmed of spaces, tabs and carriage returns at both ends, and empty lines and
    /// lines starting with <c>#</c> are left out. One leading byte-order mark is dropped; a lone CR breaks no line.
    /// </summary>
    public static string? TokenOf(ReadOnlySpan<byte> content)
    {
        if (content.Length > LicenseFileFormat.MaxBytes) return null;
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (text.Length > 0 && text[0] == (char)0xFEFF) text = text[1..];
        string? token = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim(' ', '\t', '\r');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (token is not null) return null;
            token = trimmed;
        }
        return token;
    }

    /// <summary>
    /// Verify a licence file's token against a key set, in this order: three parts (malformed); a licence
    /// file's header, matched as bytes (not-a-license-file); the signature canonical base64url (malformed);
    /// the key the header names in the set (unknown-key); a signature of 64 bytes verified by THAT key alone
    /// (bad-signature: no other key is tried); the payload canonical base64url of JSON matching the claims
    /// (bad-claims). It never expires, so there is no time to judge it at. Never throws.
    /// </summary>
    public static FileVerifyResult Verify(string token, LeaseVerifier keys)
    {
        try
        {
            return VerifyOrThrow(token, keys);
        }
        catch (Exception)
        {
            // Nothing below is expected to throw; a token that makes it is no licence file.
            return Failed(FileVerifyFailure.Malformed);
        }
    }

    private static FileVerifyResult VerifyOrThrow(string token, LeaseVerifier keys)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return Failed(FileVerifyFailure.Malformed);
        var (header, payload, signature) = (parts[0], parts[1], parts[2]);
        if (LeaseVerifier.NamedKeyId(header, FileHeaderStart) is not { } kid) return Failed(FileVerifyFailure.NotALicenseFile);
        var signatureBytes = Base64Url.Decode(signature);
        if (signatureBytes is null) return Failed(FileVerifyFailure.Malformed);
        var signed = keys.SignedByNamed(kid, Encoding.UTF8.GetBytes($"{header}.{payload}"), signatureBytes);
        if (signed is null) return Failed(FileVerifyFailure.UnknownKey);
        if (signed == false) return Failed(FileVerifyFailure.BadSignature);
        var payloadBytes = Base64Url.Decode(payload);
        var claims = payloadBytes is null ? null : ParseClaims(LeaseVerifier.Utf8Text(payloadBytes));
        return claims is null ? Failed(FileVerifyFailure.BadClaims) : new FileVerifyResult(true, null, claims);
    }

    private static FileVerifyResult Failed(FileVerifyFailure reason) => new(false, reason, null);

    /// <summary>The claims the schema checks; any other is never read.</summary>
    private static readonly HashSet<string> Checked = new(StringComparer.Ordinal)
    {
        "iss", "kind", "fid", "sub", "product", "name", "email", "iat", "test", "major", "minMajor", "ent",
    };

    /// <summary>The claims in a payload's text, or null when it is not JSON or not a licence file's claims. Never throws.</summary>
    public static LicenseFileClaims? ParseClaims(string text)
    {
        try
        {
            var top = TopLevelJson.Read(text);
            if (top.Kind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, RawJson>(StringComparer.Ordinal);
            foreach (var (name, value) in top.Members)
            {
                if (Checked.Contains(name)) fields[name] = value;
            }
            return FromFields(fields);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LicenseFileClaims? FromFields(Dictionary<string, RawJson> fields)
    {
        if (KindOf(fields) is not { } kind || Text(fields, "iss") != "keygrant") return null;
        if (NonEmpty(fields, "fid") is not { } fid || NonEmpty(fields, "sub") is not { } sub || NonEmpty(fields, "product") is not { } product) return null;
        if (!Licensee(fields, out var name, out var email)) return null;
        if (Whole(fields, "iat") is not { } iat || iat < 0 || !Flag(fields, "test", out var test)) return null;
        var claims = new LicenseFileClaims { Kind = kind, Fid = fid, Sub = sub, Product = product, Name = name, Email = email, Iat = iat, Test = test };
        return kind == LicenseFileKind.Key ? claims : Offline(claims, fields);
    }

    /// <summary>The file's kind (<c>key</c> or <c>offline</c>), or null for any other.</summary>
    private static LicenseFileKind? KindOf(Dictionary<string, RawJson> fields) => Text(fields, "kind") switch
    {
        "key" => LicenseFileKind.Key,
        "offline" => LicenseFileKind.Offline,
        _ => null,
    };

    /// <summary>Who it is licensed to: a <c>name</c> and/or an <c>email</c>, each absent or non-empty text, at least one there.</summary>
    private static bool Licensee(Dictionary<string, RawJson> fields, out string? name, out string? email)
    {
        email = null;
        return Optional(fields, "name", out name) && Optional(fields, "email", out email) && (name is not null || email is not null);
    }

    /// <summary>An optional flag: absent (null, true), or <c>true</c> or <c>false</c>; anything else (null included) false.</summary>
    private static bool Flag(Dictionary<string, RawJson> fields, string name, out bool? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var raw)) return true;
        if (Element(raw) is not { ValueKind: JsonValueKind.True or JsonValueKind.False } flag) return false;
        value = flag.GetBoolean();
        return true;
    }

    /// <summary>An offline file's versions and entitlements: <c>major</c> positive, <c>minMajor</c> positive when given, <c>ent</c> read leniently.</summary>
    private static LicenseFileClaims? Offline(LicenseFileClaims claims, Dictionary<string, RawJson> fields)
    {
        if (Whole(fields, "major") is not { } major || major <= 0) return null;
        long? minMajor = null;
        if (fields.ContainsKey("minMajor"))
        {
            if (Whole(fields, "minMajor") is not { } min || min <= 0) return null;
            minMajor = min;
        }
        RawJson? ent = fields.TryGetValue("ent", out var raw) ? raw : null;
        return claims with { Major = major, MinMajor = minMajor, Ent = ClaimsParser.EntitlementsOf(ent) };
    }

    /// <summary>The value as an element, or null when it nests too deep to be one the schema reads.</summary>
    private static JsonElement? Element(RawJson raw) => raw.Holdable ? raw.Element() : null;

    private static string? Text(Dictionary<string, RawJson> fields, string name) =>
        fields.TryGetValue(name, out var raw) && Element(raw) is { } element ? Json.StringOf(element) : null;

    private static string? NonEmpty(Dictionary<string, RawJson> fields, string name) => Text(fields, name) is { Length: > 0 } text ? text : null;

    /// <summary>Absent (null, true), or a text of at least one character; anything else (null included) false.</summary>
    private static bool Optional(Dictionary<string, RawJson> fields, string name, out string? value)
    {
        value = null;
        if (!fields.ContainsKey(name)) return true;
        value = NonEmpty(fields, name);
        return value is not null;
    }

    /// <summary>
    /// A whole JSON number (as <c>Number.isInteger</c> reads one), held as a long: one past a long's range is
    /// held at its end, so no whole number is refused for its size, as the reference compares doubles.
    /// </summary>
    private static long? Whole(Dictionary<string, RawJson> fields, string name)
    {
        if (!fields.TryGetValue(name, out var raw) || Element(raw) is not { ValueKind: JsonValueKind.Number } element) return null;
        if (element.TryGetInt64(out var value)) return value;
        if (!element.TryGetDouble(out var d) || !double.IsFinite(d) || Math.Floor(d) != d) return null;
        return Json.ToLong(d);
    }
}
