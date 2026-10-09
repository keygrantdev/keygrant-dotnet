using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace KeyGrant.Licensing;

/// <summary>
/// What <see cref="License.StatusAsync"/> says of the licence.
/// </summary>
public enum LicenseState
{
    /// <summary>A valid lease licenses this build on this device.</summary>
    Licensed,

    /// <summary>A trial is running.</summary>
    Trial,

    /// <summary>
    /// The lease ran out and could not be renewed (reason <see cref="LicenseReason.Offline"/>),
    /// or the trial has ended (reason <see cref="LicenseReason.Trial"/>).
    /// </summary>
    Expired,

    /// <summary>The licence does not license this build or this device; <see cref="LicenseStatus.Reason"/> says why.</summary>
    Invalid,

    /// <summary>No licence and no trial on this device.</summary>
    Unlicensed,

    /// <summary>
    /// The licence file could not be read just now (an antivirus scan holding it, say), reason
    /// <see cref="LicenseReason.Storage"/>. Nothing is known either way: keep running as before, ask
    /// again shortly, and never show the activation screen for it.
    /// </summary>
    Unknown,
}

/// <summary>
/// Why a licence does not license this build, or why it expired.
/// </summary>
public enum LicenseReason
{
    /// <summary>The key was revoked or expired, or this device was released.</summary>
    Revoked,

    /// <summary>The build is NEWER than the key covers: the customer needs an upgrade.</summary>
    Upgrade,

    /// <summary>The build is OLDER than the key's lowest version: update the app.</summary>
    Outdated,

    /// <summary>The lease ran out while the server could not be reached.</summary>
    Offline,

    /// <summary>The lease is not for this licence at all (another key, activation or product, or a bad signature).</summary>
    Tampered,

    /// <summary>
    /// The licence was activated on another device (this machine changed, or the licence file was
    /// copied from another one): activate again on this one.
    /// </summary>
    Device,

    /// <summary>The trial has ended.</summary>
    Trial,

    /// <summary>The licence file could not be read just now (state <see cref="LicenseState.Unknown"/>).</summary>
    Storage,
}

/// <summary>
/// Why <see cref="License.ActivateAsync"/> did not activate.
/// </summary>
public enum LicenseError
{
    /// <summary>The server knows no such key, or what was entered cannot be a key.</summary>
    InvalidKey,

    /// <summary>The key is already active on as many devices as it allows.</summary>
    Limit,

    /// <summary>The key was revoked or has expired.</summary>
    Revoked,

    /// <summary>The key does not cover this (newer) build.</summary>
    Upgrade,

    /// <summary>The key is for a later version than this build: update the app.</summary>
    Outdated,

    /// <summary>
    /// The server could not be reached, or its answer could not be used, or the device could not be
    /// identified in time, or an earlier call on this <see cref="License"/> has not finished: try again.
    /// </summary>
    Network,

    /// <summary>Reserved: the product is not set up for activation.</summary>
    NotConfigured,

    /// <summary>The server returned a lease that failed offline verification (an integrity or configuration error).</summary>
    InvalidLease,
}

/// <summary>
/// A definitive refusal from the server, as <see cref="StoredState.Refused"/> keeps it.
/// </summary>
public enum Refusal
{
    /// <summary>The key was revoked or expired, or this device released.</summary>
    Revoked,

    /// <summary>This build is newer than the key covers.</summary>
    Upgrade,

    /// <summary>This build is older than the key's lowest version.</summary>
    Outdated,

    /// <summary>The seat is held by another device.</summary>
    Device,
}

/// <summary>
/// What a fingerprint hashes: the operating system's machine id, or the host name.
/// </summary>
public enum DeviceKind
{
    /// <summary>The operating system's own machine id. Only a seat keyed by one is held to its device.</summary>
    Machine,

    /// <summary>The host name, platform and architecture.</summary>
    Hostname,
}

/// <summary>
/// One thing a licence grants beyond the base product, as its lease signs it (a member of the lease's
/// <c>ent</c> claim): exactly one of a flag, a number or a text. <c>{"edition": "pro", "seats": 5,
/// "beta": true}</c> is three entitlements: a text, a number and a flag.
/// <para>
/// Read one through the accessor of the kind you expect, which is null for the other kinds:
/// <c>status.Entitlements.TryGetValue("edition", out var edition) &amp;&amp; edition.Text == "pro"</c>.
/// Two are equal when they are the same kind holding the same value. Numbers are doubles, as JSON and
/// JavaScript read them, so <c>5</c> and <c>5.0</c> are the same entitlement. Immutable.
/// </para>
/// </summary>
public sealed class Entitlement : IEquatable<Entitlement>
{
    /// <summary>A flag.</summary>
    /// <param name="flag">Its value.</param>
    public Entitlement(bool flag) => Flag = flag;

    /// <summary>A number.</summary>
    /// <param name="number">Its value.</param>
    public Entitlement(double number) => Number = number;

    /// <summary>A text.</summary>
    /// <param name="text">Its value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public Entitlement(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The flag, when this entitlement is one (<c>"beta": true</c>); otherwise null.</summary>
    public bool? Flag { get; }

    /// <summary>The number, when this entitlement is one (<c>"seats": 5</c>); otherwise null.</summary>
    public double? Number { get; }

    /// <summary>The text, when this entitlement is one (<c>"edition": "pro"</c>); otherwise null.</summary>
    public string? Text { get; }

    /// <summary>Whether <paramref name="other"/> is the same kind holding the same value (texts compared ordinally).</summary>
    /// <param name="other">The entitlement to compare with.</param>
    /// <returns>True when they are equal.</returns>
    public bool Equals(Entitlement? other) =>
        other is not null && Flag == other.Flag && Nullable.Equals(Number, other.Number) && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Entitlement);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Flag, Number, Text);

    /// <summary>The value as text: a flag as <c>true</c> or <c>false</c>, a number in the invariant culture, a text as it is.</summary>
    /// <returns>The value as text.</returns>
    public override string ToString() =>
        Flag is { } flag ? (flag ? "true" : "false")
        : Number is { } number ? number.ToString("R", CultureInfo.InvariantCulture)
        : Text!;

    /// <summary>Whether two entitlements are equal (<see cref="Equals(Entitlement?)"/>).</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are equal, or both null.</returns>
    public static bool operator ==(Entitlement? left, Entitlement? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Whether two entitlements differ.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are not equal.</returns>
    public static bool operator !=(Entitlement? left, Entitlement? right) => !(left == right);
}

/// <summary>
/// What <see cref="License.StatusAsync"/> answers.
/// </summary>
public sealed record LicenseStatus
{
    /// <summary>The map of no entitlements, which every status holds until something grants one.</summary>
    private static readonly IReadOnlyDictionary<string, Entitlement> NoEntitlements = ReadOnlyDictionary<string, Entitlement>.Empty;

    private readonly IReadOnlyDictionary<string, Entitlement> entitlements = NoEntitlements;

    /// <summary>The licence's state.</summary>
    public LicenseState State { get; init; }

    /// <summary>Why, for <see cref="LicenseState.Invalid"/>, <see cref="LicenseState.Expired"/> and <see cref="LicenseState.Unknown"/>.</summary>
    public LicenseReason? Reason { get; init; }

    /// <summary>The licence key, when the device holds one.</summary>
    public string? Key { get; init; }

    /// <summary>Whole days left of a trial, rounded up; absent when the trial's end is not known.</summary>
    public int? TrialDaysLeft { get; init; }

    /// <summary>When the trial ends, in milliseconds since the Unix epoch.</summary>
    public long? TrialEndsAt { get; init; }

    /// <summary>
    /// True when the licence is a TEST licence: one the developer made to exercise their app, which
    /// lapses on its own within 30 days. It licenses the app like any other; check this to refuse
    /// test licences in a release build.
    /// </summary>
    public bool Test { get; init; }

    /// <summary>
    /// What the licence grants beyond the base product, by name, as its lease signs them (the
    /// <c>ent</c> claim): <c>status.Entitlements.TryGetValue("edition", out var edition) &amp;&amp;
    /// edition.Text == "pro"</c>. On a <see cref="LicenseState.Licensed"/> status, from the licence's
    /// lease; on a <see cref="LicenseState.Trial"/> status, from the trial's. EMPTY on every other
    /// status, and when the lease grants none. Never null (null given is read as none).
    /// <para>
    /// A member of the claim that is not a flag, a number or a text is left out, never the licence.
    /// Read-only, and the status's own copy of what it was given: nothing done with one status, or with the
    /// map it was built from, reaches another.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, Entitlement> Entitlements
    {
        get => entitlements;
        init => entitlements = value is null || value.Count == 0
            ? NoEntitlements
            : new ReadOnlyDictionary<string, Entitlement>(new Dictionary<string, Entitlement>(value, StringComparer.Ordinal));
    }

    /// <summary>
    /// True when an earlier call on this <see cref="License"/> has not finished (an adapter stuck on
    /// storage, say), so this answer comes from what is stored, with no ask and no write. A restart
    /// clears it.
    /// </summary>
    public bool Stalled { get; init; }

    /// <summary>
    /// Whether <paramref name="other"/> says the same: every member equal, and the
    /// <see cref="Entitlements"/> compared by CONTENT (the same names, each with an equal
    /// <see cref="Entitlement"/>). A record compares a dictionary member by reference, which would make
    /// two statuses from the same lease unequal; so this is written out, and must name every member.
    /// </summary>
    /// <param name="other">The status to compare with.</param>
    /// <returns>True when they say the same.</returns>
    public bool Equals(LicenseStatus? other) =>
        other is not null
        && State == other.State
        && Reason == other.Reason
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && TrialDaysLeft == other.TrialDaysLeft
        && TrialEndsAt == other.TrialEndsAt
        && Test == other.Test
        && Stalled == other.Stalled
        && SameEntitlements(Entitlements, other.Entitlements);

    /// <summary>A hash agreeing with <see cref="Equals(LicenseStatus?)"/>: the entitlements' taken without regard to their order.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode()
    {
        var granted = 0;
        foreach (var (name, entitlement) in Entitlements) granted ^= HashCode.Combine(name, entitlement);
        return HashCode.Combine(State, Reason, Key, TrialDaysLeft, TrialEndsAt, Test, Stalled, granted);
    }

    /// <summary>
    /// The members as a record prints them (<see cref="object.ToString"/>), the
    /// <see cref="Entitlements"/> by content, in name order (<c>Entitlements = { edition = pro, seats = 5 }</c>),
    /// where a record would print the dictionary's type name. Like <see cref="Equals(LicenseStatus?)"/>,
    /// it must name every member.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("State = ").Append(State)
            .Append(", Reason = ").Append(Reason)
            .Append(", Key = ").Append(Key)
            .Append(", TrialDaysLeft = ").Append(TrialDaysLeft)
            .Append(", TrialEndsAt = ").Append(TrialEndsAt)
            .Append(", Test = ").Append(Test)
            .Append(", Entitlements = {");
        var first = true;
        foreach (var (name, entitlement) in Entitlements.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(first ? " " : ", ").Append(name).Append(" = ").Append(entitlement);
            first = false;
        }
        builder.Append(" }, Stalled = ").Append(Stalled);
        return true;
    }

    private static bool SameEntitlements(IReadOnlyDictionary<string, Entitlement> a, IReadOnlyDictionary<string, Entitlement> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (name, entitlement) in a)
        {
            if (!b.TryGetValue(name, out var other) || entitlement != other) return false;
        }
        return true;
    }
}

/// <summary>
/// What <see cref="License.ActivateAsync"/> answers: activated, or why not.
/// </summary>
public sealed record ActivateResult
{
    private ActivateResult(bool ok, LicenseError? error)
    {
        Ok = ok;
        Error = error;
    }

    /// <summary>Whether the key was activated on this device.</summary>
    public bool Ok { get; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public LicenseError? Error { get; }

    /// <summary>The key was activated.</summary>
    public static ActivateResult Success { get; } = new(true, null);

    /// <summary>The key was not activated, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <returns>A failed result.</returns>
    public static ActivateResult Failure(LicenseError error) => new(false, error);
}

/// <summary>
/// What <see cref="License.DeactivateAsync"/> did with this device's seat. The local licence is dropped
/// either way.
/// </summary>
/// <param name="Released">
/// True when KeyGrant confirmed the release (a 200), or when this device held no activation to release
/// (none at all, or only another device's, carried by a state file copied from it: never released from here).
/// False when it could not be asked, or answered anything else (a 5xx, a 429, a proxy's page): the seat
/// may still be held, and the app should say so (the developer's dashboard, or support, can release it),
/// not tell the customer it is free.
/// </param>
public sealed record DeactivateResult(bool Released);

/// <summary>
/// Who this device is, as far as can be told this time (<see cref="IFingerprinter.IdentifyAsync"/>).
/// </summary>
/// <param name="Fingerprint">What a normal run gives, or null when it cannot be told this time.</param>
/// <param name="Kind">
/// What <paramref name="Fingerprint"/> hashes. Only a seat keyed by a <see cref="DeviceKind.Machine"/> id
/// is held to its device; null means unknown, and such a seat never is.
/// </param>
/// <param name="Alternates">
/// Other fingerprints this device answers to (such as the host-name fingerprint).
/// </param>
public sealed record DeviceIdentity(string? Fingerprint, DeviceKind? Kind, IReadOnlyList<string> Alternates);
