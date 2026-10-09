using System.Text.Json;
using System.Text.Json.Serialization;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

/// <summary>
/// The licence state kept on the device (the default storage's <c>&lt;product&gt;.json</c>). Its JSON
/// (<see cref="ToJson"/>, <see cref="FromJson"/>) is the Electron SDK's, field for field: camelCase,
/// absent fields omitted, and fields this SDK does not know kept as they were. All times are
/// milliseconds since the Unix epoch.
/// </summary>
[JsonConverter(typeof(StoredStateJsonConverter))]
public sealed record StoredState
{
    /// <summary>The licence key, normalised as it was activated.</summary>
    public string? Key { get; init; }

    /// <summary>This device's activation of the key.</summary>
    public string? ActivationId { get; init; }

    /// <summary>The signed JWT lease for a paid licence.</summary>
    public string? Lease { get; init; }

    /// <summary>The earliest trial start ever seen. Only ever moves earlier.</summary>
    public long? TrialStart { get; init; }

    /// <summary>The server's trial end.</summary>
    public long? TrialEndsAt { get; init; }

    /// <summary>The signed JWT lease for a trial.</summary>
    public string? TrialLease { get; init; }

    /// <summary>
    /// When (guarded device clock) the trial answer stored here was asked for: an older answer, from
    /// another process, is not saved over it.
    /// </summary>
    public long? TrialAt { get; init; }

    /// <summary>The monotonic high-water clock, which guards against a clock put back.</summary>
    public long? LastSeen { get; init; }

    /// <summary>The app major sent with the last check-in the server answered.</summary>
    public int? CheckedMajor { get; init; }

    /// <summary>
    /// The newest app major run while holding a lease signed inside the key's upgrade window, and
    /// only before that window's end. Sent with every validate.
    /// </summary>
    public int? KeptMajor { get; init; }

    /// <summary>When the last ask of the server came back without a lease.</summary>
    public long? BackoffSince { get; init; }

    /// <summary>How long to wait after <see cref="BackoffSince"/> before asking again on its own.</summary>
    public long? BackoffMs { get; init; }

    /// <summary>The server's definitive refusal of this key on this device, while it stands.</summary>
    public Refusal? Refused { get; init; }

    /// <summary>The build major an <see cref="Refusal.Upgrade"/> or <see cref="Refusal.Outdated"/> refusal was given for.</summary>
    public int? RefusedMajor { get; init; }

    /// <summary>The device (its <c>fp</c> claim hashes, never fingerprints) a <see cref="Refusal.Device"/> refusal was given for.</summary>
    public IReadOnlyList<string>? RefusedFor { get; init; }

    /// <summary>What this install is keyed by: the KIND only, never a fingerprint.</summary>
    public DeviceKind? DeviceKind { get; init; }

    /// <summary>When (guarded device clock) the server's answer stored here was asked for.</summary>
    public long? VerdictAt { get; init; }

    /// <summary>The default storage's revision of this state, one more on every save.</summary>
    public long? Rev { get; init; }

    /// <summary>
    /// The one-time purchase SECRET <see cref="License.PurchaseUrlAsync"/> keeps (43 URL-safe characters:
    /// 256 random bits), held while no key is, until a key bought with it is picked up, the server answers
    /// that it never will be, or 30 days have passed since its link was last handed out. Sent nowhere but
    /// in the claim: a checkout link carries only a hash of it.
    /// </summary>
    public string? PurchaseToken { get; init; }

    /// <summary>When (guarded device clock) the purchase secret's link was last handed out: its 30-day bound, and the day a status still asks, run from here.</summary>
    public long? PurchaseTokenAt { get; init; }

    /// <summary>
    /// When (device clock) the claim was last asked and brought no key back: a status asks again on its
    /// own once 5 minutes have passed. Cleared by <see cref="License.PurchaseUrlAsync"/>, so the first
    /// status after a link asks.
    /// </summary>
    public long? ClaimAskedAt { get; init; }

    /// <summary>
    /// When (guarded device clock) a running trial last reported its launch (sent in the background by a
    /// status): at most once a day, a trial answer (<see cref="TrialAt"/>) counting as a report.
    /// </summary>
    public long? TrialReportedAt { get; init; }

    /// <summary>
    /// Fields this SDK does not know (written by another SDK or a later version), kept as they were
    /// and written back. One nested more than 64 levels deep is kept, and written back, too, as its text
    /// only: holding it here would take time growing with the square of its depth.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? AdditionalFields { get; init; }

    /// <summary>Fields nested too deep to hold as a <see cref="JsonElement"/> in reasonable time, as their exact text.</summary>
    internal IReadOnlyDictionary<string, string>? DeepFields { get; init; }

    /// <summary>This state as JSON, in the Electron SDK's format.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => StoredStateJson.Write(this);

    /// <summary>
    /// A state read from JSON in the Electron SDK's format, or null for the JSON <c>null</c>. A known
    /// field holding something of the wrong type is read as absent; a value that is valid JSON but
    /// not an object is an empty state.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The state, or null.</returns>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static StoredState? FromJson(string json) => StoredStateJson.Read(json);

    /// <inheritdoc />
    public bool Equals(StoredState? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Key == other.Key
            && ActivationId == other.ActivationId
            && Lease == other.Lease
            && TrialStart == other.TrialStart
            && TrialEndsAt == other.TrialEndsAt
            && TrialLease == other.TrialLease
            && TrialAt == other.TrialAt
            && LastSeen == other.LastSeen
            && CheckedMajor == other.CheckedMajor
            && KeptMajor == other.KeptMajor
            && BackoffSince == other.BackoffSince
            && BackoffMs == other.BackoffMs
            && Refused == other.Refused
            && RefusedMajor == other.RefusedMajor
            && SameList(RefusedFor, other.RefusedFor)
            && DeviceKind == other.DeviceKind
            && VerdictAt == other.VerdictAt
            && Rev == other.Rev
            && PurchaseToken == other.PurchaseToken
            && PurchaseTokenAt == other.PurchaseTokenAt
            && ClaimAskedAt == other.ClaimAskedAt
            && TrialReportedAt == other.TrialReportedAt
            && SameFields(AdditionalFields, other.AdditionalFields)
            && SameText(DeepFields, other.DeepFields);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        hash.Add(ActivationId);
        hash.Add(Lease);
        hash.Add(TrialStart);
        hash.Add(TrialEndsAt);
        hash.Add(TrialLease);
        hash.Add(LastSeen);
        hash.Add(Refused);
        hash.Add(VerdictAt);
        hash.Add(Rev);
        hash.Add(PurchaseToken);
        return hash.ToHashCode();
    }

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool SameFields(IReadOnlyDictionary<string, JsonElement>? a, IReadOnlyDictionary<string, JsonElement>? b)
    {
        var left = a ?? EmptyFields;
        var right = b ?? EmptyFields;
        if (left.Count != right.Count) return false;
        foreach (var (name, value) in left)
        {
            if (!right.TryGetValue(name, out var theirs) || value.GetRawText() != theirs.GetRawText()) return false;
        }
        return true;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyFields = new Dictionary<string, JsonElement>();

    private static bool SameText(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        var left = a ?? EmptyText;
        var right = b ?? EmptyText;
        if (left.Count != right.Count) return false;
        foreach (var (name, text) in left)
        {
            if (!right.TryGetValue(name, out var theirs) || !string.Equals(text, theirs, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyText = new Dictionary<string, string>();
}

/// <summary>Reads and writes <see cref="StoredState"/> as the System.Text.Json converter.</summary>
internal sealed class StoredStateJsonConverter : JsonConverter<StoredState>
{
    public override StoredState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return StoredStateJson.Read(document.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, StoredState value, JsonSerializerOptions options) =>
        StoredStateJson.WriteTo(writer, value);
}
