using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Internal;

/// <summary>What a claim's answer does (<see cref="Pickup.ClaimOutcome"/>): a bought key licenses, a kept secret waits, a dropped one goes.</summary>
internal enum ClaimResult
{
    Bought,
    Kept,
    Dropped,
}

/// <summary>What a claim's answer saves and says: the status only for a bought key.</summary>
internal sealed record ClaimOutcome(ClaimResult Result, LicenseStatus? Status, StatePatch Patch);

/// <summary>What a claim sends (<see cref="Pickup.ClaimRequest"/>).</summary>
/// <param name="Token">The purchase secret.</param>
/// <param name="Fingerprint">The fingerprint the claim is made under, as read.</param>
/// <param name="FingerprintKind">What it hashes; null for none.</param>
/// <param name="Fingerprints">Every fingerprint the run read, within the server's bounds; null when none is left.</param>
/// <param name="Name">The device name, already cut; null when the app gave none.</param>
/// <param name="Major">This build's major.</param>
internal sealed record ClaimRequest(string Token, string Fingerprint, DeviceKind? FingerprintKind, IReadOnlyList<string>? Fingerprints, string? Name, int Major)
{
    /// <summary>The body as sent: absent members left out, never null.</summary>
    public JsonObject ToJson()
    {
        var body = new JsonObject { ["token"] = Token, ["fingerprint"] = Fingerprint };
        if (FingerprintKind is { } kind) body["fingerprintKind"] = Names.Of(kind);
        if (Fingerprints is not null) body["fingerprints"] = new JsonArray(Fingerprints.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        if (Name is not null) body["name"] = Name;
        body["major"] = Major;
        return body;
    }
}

/// <summary>What a claim's answer is judged from (<see cref="Pickup.ClaimOutcome"/>).</summary>
/// <param name="Response">The answer; null when there was none (it threw or timed out, or no fingerprint could be sent).</param>
/// <param name="Check">
/// The offline check (as of <paramref name="Now"/>, for the key and activation it came with, on this
/// device) of the lease a 200 carried with a key and an activation id; null otherwise.
/// </param>
/// <param name="ClockNow">The device clock when the claim was asked: where the wait before asking again starts.</param>
/// <param name="Now">The time the lease was judged at: the guarded clock, reconciled to the server's.</param>
/// <param name="Major">This build's major.</param>
/// <param name="Kind">What the claim's fingerprint hashes; null for none.</param>
internal sealed record ClaimInput(HttpResult? Response, LeaseCheck? Check, long ClockNow, long Now, int Major, DeviceKind? Kind);

/// <summary>
/// Buying from the app with no key typed, decided without I/O: the purchase secret
/// <see cref="License.PurchaseUrlAsync"/> keeps (<see cref="NewPurchaseSecret"/>), the reference to it a
/// link carries (<see cref="PurchaseReference"/>, <see cref="PurchaseLink"/>, built as
/// <see cref="License.UpgradeUrlAsync"/>'s link is: <see cref="ReferencedLink"/>), and what the claim's
/// answer saves and says (<see cref="ClaimOutcome"/>).
/// </summary>
internal static class Pickup
{
    /// <summary>What starts every purchase reference, so the server tells one apart from an activation id or a key.</summary>
    public const string PurchaseReferencePrefix = "kgp_";

    /// <summary>At most this many fingerprints in a claim's <c>fingerprints</c>: the server's bound.</summary>
    public const int MaxClaimFingerprints = 4;

    /// <summary>At most this many characters (UTF-16 units) in each of a claim's <c>fingerprints</c>: the server's bound.</summary>
    public const int MaxClaimFingerprintLength = 200;

    /// <summary>The query parameter a payment link names its checkout's buyer by.</summary>
    private const string Reference = "client_reference_id";

    /// <summary>
    /// A new purchase secret: the base64url (no padding) of 32 bytes from the platform's CSPRNG, 43
    /// characters. It stays on the device (<see cref="StoredState.PurchaseToken"/>) and is sent only in the
    /// claim: a link carries the reference to it (<see cref="PurchaseReference"/>), so a link that leaks
    /// (browser history, the tenant's Stripe session) claims nothing. Random, and never the fingerprint: a
    /// fingerprint is no secret.
    /// </summary>
    /// <param name="randomBytes">The byte source: <see cref="Csprng"/>, the platform's CSPRNG; another only in tests.</param>
    public static string NewPurchaseSecret(Func<int, byte[]> randomBytes) => Base64Url(randomBytes(32));

    /// <summary>The platform's CSPRNG (<see cref="RandomNumberGenerator.GetBytes(int)"/>): where every purchase secret's bytes come from.</summary>
    public static readonly Func<int, byte[]> Csprng = RandomNumberGenerator.GetBytes;

    /// <summary>
    /// The reference a payment link carries for <paramref name="secret"/>: <c>kgp_</c> and the base64url
    /// (no padding) of the SHA-256 of the secret's UTF-8 bytes, 47 characters. The checkout keeps it on the
    /// key it mints; the server derives it from the secret a claim sends, to find that key.
    /// </summary>
    public static string PurchaseReference(string secret) => PurchaseReferencePrefix + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The link <see cref="License.PurchaseUrlAsync"/> answers for a held <paramref name="secret"/>: the link with its reference (<see cref="ReferencedLink"/>).</summary>
    public static string PurchaseLink(string link, string secret) => ReferencedLink(link, PurchaseReference(secret));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A payment link read as an absolute URL; an <see cref="ArgumentException"/> for anything else (a
    /// path such as <c>/buy</c>, a bare host such as <c>buy.stripe.com/x</c>).
    /// </summary>
    public static Uri AbsoluteLink(string link)
    {
        // A bare path is a file URI to .NET on macOS and Linux, and no URL to the WHATWG parser.
        var parsed = Uri.TryCreate(link, UriKind.Absolute, out var uri);
        var implicitFile = parsed && uri!.IsFile && !link.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!parsed || implicitFile) throw new ArgumentException("not an absolute URL", nameof(link));
        return uri!;
    }

    /// <summary>
    /// <paramref name="link"/> with <paramref name="reference"/> as its one <c>client_reference_id</c>. The
    /// link is read as an absolute URL (<see cref="AbsoluteLink"/>), which writes its scheme, host and path
    /// in their normal form, and its query is then edited as text: every parameter named exactly
    /// <c>client_reference_id</c> (its text before the first <c>=</c>, or all of it) and every empty one is
    /// left out, the others are kept as written and in their order, and
    /// <c>client_reference_id=&lt;reference&gt;</c> (percent-encoded as a URI component) is added last. The
    /// fragment is kept.
    /// </summary>
    public static string ReferencedLink(string link, string reference)
    {
        var uri = AbsoluteLink(link);
        var query = uri.Query.Length > 0 ? uri.Query[1..] : "";
        var kept = query.Split('&').Where(part => part.Length > 0 && part.Split('=')[0] != Reference);
        var edited = string.Join("&", kept.Append($"{Reference}={EncodeUriComponent(reference)}"));
        return $"{uri.GetLeftPart(UriPartial.Path)}?{edited}{uri.Fragment}";
    }

    /// <summary>JavaScript's <c>encodeURIComponent</c>: every UTF-8 byte but the letters, digits and <c>-_.!~*'()</c> percent-encoded.</summary>
    private static string EncodeUriComponent(string text)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// The claim's body, or null when none is sent. The fingerprint is a first activation's
    /// (<see cref="Devices.ActivationIdentity"/>: the device's own, else the host name), and from a status
    /// or a refresh (<paramref name="start"/> false) only the device's own: a claim under the host-name
    /// fallback could take the seat under a fingerprint the next ask does not send, so with no own
    /// fingerprint nothing is sent (and nothing saved). <c>fingerprints</c> is every fingerprint the run
    /// read, its own first, then the host name and any other alternates, whether or not the device is held
    /// (they only match this claim to an earlier one, and judge no seat), within the server's bounds: those
    /// of 1 to <see cref="MaxClaimFingerprintLength"/> UTF-16 units, the first
    /// <see cref="MaxClaimFingerprints"/> of them, never one cut short (a cut fingerprint matches nothing);
    /// left out when none is. A body with too many, or one too long, would be refused with an error word,
    /// which would drop the secret.
    /// </summary>
    public static ClaimRequest? ClaimRequest(string secret, Device device, string? name, int major, bool start)
    {
        if (!start && device.Own is null) return null;
        if (Devices.ActivationIdentity(device, null) is not { } identity) return null;
        var fingerprints = device.Fingerprints
            .Where(f => f.Length >= 1 && f.Length <= MaxClaimFingerprintLength)
            .Take(MaxClaimFingerprints)
            .ToList();
        return new ClaimRequest(secret, identity.Fingerprint, identity.Kind, fingerprints.Count > 0 ? fingerprints : null, name, major);
    }

    /// <summary>
    /// What an activation saves, a typed key's and a bought key's claim alike, stamped as an activation:
    /// the key, the activation, the lease, the major heard, the kept major (<see cref="Offline.KeptWith"/>
    /// from none), any refusal and wait cleared (<see cref="Offline.Leased"/>), what the install is keyed by
    /// (cleared for a fingerprint of unknown kind), and the purchase secret and its stamps cleared
    /// (<see cref="Offline.NoPurchase"/>).
    /// </summary>
    public static StatePatch ActivationPatch(string key, string activationId, string lease, LeaseClaims claims, long now, int major, DeviceKind? kind)
    {
        var patch = new StatePatch
        {
            [StateField.Key] = key,
            [StateField.ActivationId] = activationId,
            [StateField.Lease] = lease,
            [StateField.CheckedMajor] = major,
            [StateField.KeptMajor] = Offline.KeptWith(null, claims, now, major),
        }.With(Offline.Leased);
        patch.Set(StateField.DeviceKind, kind);
        return patch.With(Offline.NoPurchase);
    }

    /// <summary>
    /// What a claim's answer saves and says:
    /// <list type="bullet">
    /// <item>a 200 with a key, an activation id and a lease (strings) whose lease licenses this build here:
    /// saved exactly as an activation saves (<see cref="ActivationPatch"/>), and licensed;</item>
    /// <item>a 200 with all three whose lease does not license this build here: the secret dropped (the
    /// customer has the key by email, and typing it gives the proper error);</item>
    /// <item>a 200 <c>{pending: true}</c> ("not bought yet"), no answer, a 5xx or a 429, and any other
    /// answer with no error word (a captive portal's page, a 404 page from a server with no claim
    /// endpoint): the secret kept, and the wait started;</item>
    /// <item>any other answer with an error word (<c>spent</c>, <c>pickup-off</c>, a refusal): the secret dropped.</item>
    /// </list>
    /// Only a bought key gives a status: otherwise the caller answers as it would with no secret held.
    /// </summary>
    public static ClaimOutcome ClaimOutcome(ClaimInput input)
    {
        var kept = new ClaimOutcome(ClaimResult.Kept, null, new StatePatch { [StateField.ClaimAskedAt] = input.ClockNow });
        var dropped = new ClaimOutcome(ClaimResult.Dropped, null, Offline.NoPurchase);
        if (input.Response is not { } response || Wire.NoAnswer(response.Status)) return kept;
        var body = Wire.ParseClaimBody(response.Json);
        if (response.Status == 200 && body is { Key: { } key, ActivationId: { } activationId, Lease: { } lease })
        {
            if (input.Check is not { Ok: true } check) return dropped;
            var patch = ActivationPatch(key, activationId, lease, check.Claims!, input.Now, input.Major, input.Kind);
            return new ClaimOutcome(ClaimResult.Bought, Offline.LeaseStatus(check, key), patch);
        }
        if (response.Status == 200 && body.Pending) return kept;
        return body.Error is null ? kept : dropped;
    }
}
