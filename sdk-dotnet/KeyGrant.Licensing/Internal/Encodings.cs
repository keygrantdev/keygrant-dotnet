using System.Security.Cryptography;
using System.Text;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// A public key's id, the <c>kid</c> a lease's header names it by: its RFC 7638 thumbprint, the base64url
/// (no padding) of the SHA-256 of <c>{"crv":"Ed25519","kty":"OKP","x":"&lt;x&gt;"}</c>, with <c>x</c> the
/// key's 32 bytes written canonically. Computed from the key itself, so a JWK with no <c>kid</c> member has
/// one all the same.
/// </summary>
internal static class KeyIds
{
    public static string Of(ReadOnlySpan<byte> publicKey) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes($"{{\"crv\":\"Ed25519\",\"kty\":\"OKP\",\"x\":\"{Base64Url.Encode(publicKey)}\"}}")));
}

/// <summary>
/// CANONICAL base64url, as the server's verifier decodes it: the URL-safe alphabet, no padding, and nothing
/// the encoder would not have written (encoding the result gives the input back, so no stray bits in the
/// last character). Anything else is refused: every lease the server signs is canonical, so only a token
/// nobody issued is.
/// </summary>
internal static class Base64Url
{
    /// <summary>The bytes <paramref name="input"/> spells, or null when it is not canonical base64url.</summary>
    public static byte[]? Decode(string input)
    {
        if (input.Length % 4 == 1) return null;
        foreach (var c in input)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        }
        var standard = input.Replace('-', '+').Replace('_', '/');
        var pad = standard.Length % 4 == 0 ? 0 : 4 - (standard.Length % 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(standard + new string('=', pad));
        }
        catch (FormatException)
        {
            return null;
        }
        // Stray bits in the last character decode to the same bytes, and re-encode differently.
        return Encode(bytes) == input ? bytes : null;
    }

    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The SHA-256 hashes the SDK and the server agree on.</summary>
internal static class Hashes
{
    /// <summary>Lower-case hex SHA-256 of the UTF-8 of <paramref name="text"/>.</summary>
    public static string Sha256Hex(string text)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// The <c>fp</c> claim for a device fingerprint (<c>fingerprintClaim</c>): hex SHA-256 of it,
    /// namespaced, so a lease never carries the fingerprint itself.
    /// </summary>
    public static string FingerprintClaim(string fingerprint) => Sha256Hex($"keygrant-fp|{fingerprint}");
}
