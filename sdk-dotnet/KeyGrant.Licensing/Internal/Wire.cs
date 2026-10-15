using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// What the SDK sends and reads on the wire, and the small pieces of it that need no engine state:
/// the server's error words, the bounds the endpoints hold strings to, and the shapes of their
/// answers.
/// </summary>
internal static class Wire
{
    /// <summary>What <c>/v1/products/&lt;slug&gt;/activate</c> accepts for a device name. Longer is truncated, not refused.</summary>
    public const int MaxDeviceName = 120;

    /// <summary>What it accepts for a licence key. A longer one is not a key it could hold.</summary>
    public const int MaxKey = 120;

    /// <summary>How long a check <c>status()</c> makes on a valid lease may hold it up.</summary>
    public const int StatusCheckTimeoutMs = 5_000;

    /// <summary>Map a server error string to an SDK <see cref="LicenseError"/>.</summary>
    public static LicenseError MapServerError(string? error) => error switch
    {
        "invalid-key" => LicenseError.InvalidKey,
        "limit" => LicenseError.Limit,
        "revoked" or "expired" => LicenseError.Revoked,
        "upgrade-required" => LicenseError.Upgrade,
        "update-required" => LicenseError.Outdated,
        // A user-locked key's new device: each word is its own error.
        "user-email-required" => LicenseError.UserEmailRequired,
        "user-email-mismatch" => LicenseError.UserEmailMismatch,
        "user-code-required" => LicenseError.UserCodeRequired,
        "user-code-invalid" => LicenseError.UserCodeInvalid,
        "too-many-codes" => LicenseError.TooManyCodes,
        "too-many-attempts" => LicenseError.TooManyAttempts,
        "try-again" => LicenseError.TryAgain,
        _ => LicenseError.Network,
    };

    /// <summary>The longest address the endpoint takes; a longer one is no key's address.</summary>
    public const int MaxEmail = 254;

    /// <summary>How many digits a code has.</summary>
    private const int CodeDigits = 6;

    /// <summary>
    /// What an activation sends for the address and code a person typed for a user-locked key, each only
    /// when there is one, or the error that refuses them before anything is sent (an address longer than
    /// any is <see cref="LicenseError.UserEmailMismatch"/>, a code that is not six digits
    /// <see cref="LicenseError.UserCodeInvalid"/>; the address is judged first).
    /// </summary>
    public static Typed TypedFor(string? email, string? code)
    {
        // The address, trimmed as a key is: a blank is none, and a longer one can match no key's address.
        // The server compares it without regard to case, so it is sent as typed.
        var enteredEmail = email is null ? "" : JsTrim(email);
        if (enteredEmail.Length > MaxEmail) return Typed.Refuse(LicenseError.UserEmailMismatch);
        // The code, its ends trimmed as a key's are and then its spaces (U+0020) dropped, since a code read
        // off an email is typed as 123 456 as often as 123456: exactly six ASCII digits, else refused.
        var enteredCode = code is null ? "" : JsTrim(code).Replace(" ", "", StringComparison.Ordinal);
        if (enteredCode.Length > 0 && !(enteredCode.Length == CodeDigits && enteredCode.All(c => c is >= '0' and <= '9')))
        {
            return Typed.Refuse(LicenseError.UserCodeInvalid);
        }
        return new Typed(enteredEmail.Length == 0 ? null : enteredEmail, enteredCode.Length == 0 ? null : enteredCode, null);
    }

    /// <summary>
    /// The key a person entered, NORMALISED (trimmed, upper case), which is the one sent and stored;
    /// or null when it cannot be a key at all (nothing, or longer than the endpoint accepts).
    /// </summary>
    public static string? EnteredKey(string key)
    {
        var entered = JsTrim(key).ToUpperInvariant();
        return entered.Length > 0 && entered.Length <= MaxKey ? entered : null;
    }

    /// <summary>Cut a string to <paramref name="max"/> UTF-16 units without splitting a character.</summary>
    public static string? TrimToLength(string? value, int max)
    {
        if (value is null || value.Length <= max) return value;
        var cut = value[..max];
        // A high surrogate at the end has lost its pair.
        return char.IsHighSurrogate(cut[max - 1]) ? cut[..^1] : cut;
    }

    /// <summary>An invalid status, for <paramref name="reason"/>, naming the key.</summary>
    public static LicenseStatus InvalidStatus(LicenseReason reason, string? key) =>
        new() { State = LicenseState.Invalid, Reason = reason, Key = key };

    /// <summary>The <c>error</c> of a body, when it is a string.</summary>
    public static string? ErrorOf(JsonNode? json) => Json.TryString(Member(json, "error"), out var word) ? word : null;

    /// <summary>The lease and activation id of a server body, keeping only string values.</summary>
    public static (string? Lease, string? ActivationId) ParseLeaseBody(JsonNode? json)
    {
        var lease = Json.TryString(Member(json, "lease"), out var ls) ? ls : null;
        var activationId = Json.TryString(Member(json, "activationId"), out var aid) ? aid : null;
        return (lease, activationId);
    }

    /// <summary>The (unsigned) trial response body, or null when it is not one.</summary>
    public static TrialBody? ParseTrialBody(JsonNode? json)
    {
        if (!Json.TryNumber(Member(json, "trialStart"), out var trialStart)) return null;
        if (!Json.TryNumber(Member(json, "trialEndsAt"), out var trialEndsAt)) return null;
        var lease = Json.TryString(Member(json, "lease"), out var ls) ? ls : null;
        return new TrialBody(Json.ToLong(trialStart), Json.ToLong(trialEndsAt), lease);
    }

    /// <summary>
    /// A member of an object body, or null: never an exception, whatever node the body is (an object
    /// with a repeated name throws on first use in System.Text.Json, where JSON.parse keeps the last).
    /// </summary>
    private static JsonNode? Member(JsonNode? json, string name)
    {
        try
        {
            return json is JsonObject body && body.TryGetPropertyValue(name, out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The smallest of the values given, or null when none is.</summary>
    public static long? Earliest(params long?[] values)
    {
        long? smallest = null;
        foreach (var value in values)
        {
            if (value is { } v && (smallest is null || v < smallest)) smallest = v;
        }
        return smallest;
    }

    /// <summary>
    /// The definitive refusal a validate's error is, or null for any other error, which is never taken
    /// as a verdict (a proxy's 4xx, an unknown product, a body the server could not read).
    /// </summary>
    public static Refusal? RefusalOf(string? error) => error switch
    {
        "upgrade-required" => Refusal.Upgrade,
        "update-required" => Refusal.Outdated,
        "revoked" or "expired" or "deactivated" => Refusal.Revoked,
        // The server's own check of this device's fingerprints against the seat.
        "device" => Refusal.Device,
        _ => null,
    };

    /// <summary>Whether a response is no answer at all: an outage (5xx) or a refusal to serve now (429).</summary>
    public static bool NoAnswer(int status) => status >= 500 || status == 429;

    /// <summary>
    /// <c>String.prototype.trim</c>: JavaScript's white space and line terminators, which differ from
    /// <see cref="string.Trim()"/> (U+FEFF is trimmed, U+0085 is not).
    /// </summary>
    public static string JsTrim(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsJsWhiteSpace(value[start])) start++;
        while (end > start && IsJsWhiteSpace(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>A character JavaScript's <c>trim</c> and regular-expression <c>\s</c> count as white space.</summary>
    public static bool IsJsWhiteSpace(char c) => Array.IndexOf(JsWhiteSpaceCodes, (int)c) >= 0;

    /// <summary>The code points of JavaScript's white space and line terminators.</summary>
    private static readonly int[] JsWhiteSpaceCodes =
    [
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0xA0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
    ];

    /// <summary>Those characters, for a regular-expression character class (none of them is special in one).</summary>
    public static readonly string JsWhiteSpaceChars = new(JsWhiteSpaceCodes.Select(code => (char)code).ToArray());
}

/// <summary>The trial endpoint's answer.</summary>
internal sealed record TrialBody(long TrialStart, long TrialEndsAt, string? Lease);

/// <summary>
/// What an activation sends for what a person typed for a user-locked key: the address and the code, each
/// null when there is none, or the error that refuses them before anything is sent.
/// </summary>
internal sealed record Typed(string? Email, string? Code, LicenseError? Refused)
{
    /// <summary>Refused, for <paramref name="error"/>.</summary>
    public static Typed Refuse(LicenseError error) => new(null, null, error);
}

