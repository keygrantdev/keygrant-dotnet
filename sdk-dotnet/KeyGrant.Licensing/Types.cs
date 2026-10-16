using System.Collections.ObjectModel;
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
    /// The state file could not be read just now (an antivirus scan holding it, say), reason
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

    /// <summary>The build is NEWER than the key (or, from a licence file, the file) covers: the customer needs an upgrade.</summary>
    Upgrade,

    /// <summary>The build is OLDER than the key's (or, from a licence file, the file's) lowest version: update the app.</summary>
    Outdated,

    /// <summary>The lease ran out while the server could not be reached.</summary>
    Offline,

    /// <summary>
    /// The lease is not for this licence at all (another key, activation or product, or a bad signature), or the
    /// stored licence file is not one (the state file was edited).
    /// </summary>
    Tampered,

    /// <summary>
    /// The licence was activated on another device (this machine changed, or the state file was
    /// copied from another one): activate again on this one.
    /// </summary>
    Device,

    /// <summary>The trial has ended.</summary>
    Trial,

    /// <summary>The state file could not be read just now (state <see cref="LicenseState.Unknown"/>).</summary>
    Storage,

    /// <summary>
    /// The lease is signed by a key this build does not carry (<see cref="LicenseConfig.PublicJwks"/>). Asked
    /// online, the server signs one with the key it uses now, which a build carrying that key verifies. A stored
    /// licence file signed by such a key needs the app's update that carries it, or the same licence's file
    /// signed again.
    /// </summary>
    UnknownKey,

    /// <summary>
    /// The stored offline licence file is one this build lists as revoked
    /// (<see cref="LicenseConfig.RevokedLicenseFiles"/>).
    /// </summary>
    RevokedFile,
}

/// <summary>
/// Why <see cref="License.ActivateAsync(string, CancellationToken)"/> did not activate.
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

    /// <summary>
    /// A user-locked key asks a NEW device who it is. Ask the person for the address the key was sold to and
    /// activate again with it (<see cref="ActivateOptions.Email"/>).
    /// </summary>
    UserEmailRequired,

    /// <summary>Not the key's address (or longer than any); nothing is said of the real one: ask again.</summary>
    UserEmailMismatch,

    /// <summary>A code was just mailed to that address: ask for it and activate again with the address and the code.</summary>
    UserCodeRequired,

    /// <summary>The code is wrong, expired, used or not six digits: ask again, or activate with the address alone for a new one.</summary>
    UserCodeInvalid,

    /// <summary>Three codes were sent for the key this hour: wait, or use the code sent.</summary>
    TooManyCodes,

    /// <summary>Too many wrong addresses or codes for the key this hour: try again later.</summary>
    TooManyAttempts,

    /// <summary>The code could not be sent just now and nothing was activated: try again.</summary>
    TryAgain,
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
/// What <see cref="License.RequireAsync"/> throws when the licence does not grant an entitlement: names it as
/// <see cref="Entitlement"/>.
/// </summary>
public sealed class MissingEntitlementException : Exception
{
    /// <summary>The licence does not grant <paramref name="entitlement"/>.</summary>
    /// <param name="entitlement">The entitlement's code, as the app asked for it.</param>
    public MissingEntitlementException(string entitlement)
        : base($"the licence does not grant \"{entitlement}\"") => Entitlement = entitlement;

    /// <summary>The entitlement's code, as the app asked for it.</summary>
    public string Entitlement { get; }
}

/// <summary>
/// What <see cref="License.StatusAsync"/> answers.
/// </summary>
public sealed record LicenseStatus
{
    /// <summary>The map of no entitlements, which every status holds until something grants one.</summary>
    private static readonly IReadOnlyDictionary<string, Entitlement> NoEntitlements = ReadOnlyDictionary<string, Entitlement>.Empty;

    private readonly IReadOnlyDictionary<string, Entitlement> entitlements = NoEntitlements;

    /// <summary>The map of no meters, which every status holds until a key's lease names one.</summary>
    private static readonly IReadOnlyDictionary<string, MeterStatus> NoMeters = ReadOnlyDictionary<string, MeterStatus>.Empty;

    private readonly IReadOnlyDictionary<string, MeterStatus> meters = NoMeters;

    /// <summary>The licence's state.</summary>
    public LicenseState State { get; init; }

    /// <summary>Why, for <see cref="LicenseState.Invalid"/>, <see cref="LicenseState.Expired"/> and <see cref="LicenseState.Unknown"/>.</summary>
    public LicenseReason? Reason { get; init; }

    /// <summary>
    /// The licence key, when the device holds one; on a status from a licence file that is licensed, or
    /// invalid with <see cref="LicenseReason.RevokedFile"/>, <see cref="LicenseReason.Upgrade"/> or
    /// <see cref="LicenseReason.Outdated"/>, the key the file records.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Whole days left of a trial, rounded up; absent when the trial's end is not known.</summary>
    public int? TrialDaysLeft { get; init; }

    /// <summary>When the trial ends, in milliseconds since the Unix epoch.</summary>
    public long? TrialEndsAt { get; init; }

    /// <summary>
    /// True when the licence is a TEST licence: one the developer made to exercise their app, which
    /// lapses on its own within 30 days (an offline licence file made for one never does: it is a file). It licenses the app like any other; check this to refuse
    /// test licences in a release build.
    /// </summary>
    public bool Test { get; init; }

    /// <summary>
    /// What the licence grants beyond the base product, by name, as its lease signs them (the
    /// <c>ent</c> claim): <c>status.Entitlements.TryGetValue("edition", out var edition) &amp;&amp;
    /// edition.Text == "pro"</c>. On a <see cref="LicenseState.Licensed"/> status, from the licence's
    /// lease, or the stored offline licence file's <c>ent</c>; on a <see cref="LicenseState.Trial"/> status, from the trial's. EMPTY on every other
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
    /// The key's meters, by code, as <see cref="License.UseAsync"/> counts them: on a <see cref="LicenseState.Licensed"/>
    /// status from a key's lease that names some, each with what is <see cref="MeterStatus.Left"/> net of this device's
    /// unsynced spends. EMPTY on every other status (a trial, an offline licence file, an expired or invalid licence), and
    /// when the lease names none. Never null (null given is read as none). Read-only, and the status's own copy of what it
    /// was given. A meter never takes the licence away: nothing reads one to decide whether the app is licensed.
    /// </summary>
    public IReadOnlyDictionary<string, MeterStatus> Meters
    {
        get => meters;
        init => meters = value is null || value.Count == 0
            ? NoMeters
            : new ReadOnlyDictionary<string, MeterStatus>(new Dictionary<string, MeterStatus>(value, StringComparer.Ordinal));
    }

    /// <summary>
    /// True when an earlier call on this <see cref="License"/> has not finished (an adapter stuck on
    /// storage, say), so this answer comes from what is stored, with no ask and no write. A restart
    /// clears it.
    /// </summary>
    public bool Stalled { get; init; }

    /// <summary>
    /// <see cref="LicenseSource.File"/> when the status rests on a stored offline licence file
    /// (<see cref="License.ImportLicenseFileAsync(ReadOnlyMemory{byte}, CancellationToken)"/>), licensed or
    /// not; null for a key's lease, a trial and no licence. A file licence has no seat to deactivate: offer
    /// "Remove licence file" (<see cref="License.RemoveLicenseFileAsync"/>) instead.
    /// </summary>
    public LicenseSource? Source { get; init; }

    /// <summary>
    /// Who the stored offline licence file is licensed to, as it signs it: show it ("Licensed to Ada
    /// Lovelace"). On a file status that is licensed, or invalid with <see cref="LicenseReason.RevokedFile"/>,
    /// <see cref="LicenseReason.Upgrade"/> or <see cref="LicenseReason.Outdated"/>; null otherwise.
    /// </summary>
    public LicensedTo? LicensedTo { get; init; }

    /// <summary>
    /// Whether <paramref name="other"/> says the same: every member equal, and the
    /// <see cref="Entitlements"/> and <see cref="Meters"/> compared by CONTENT (the same names, each with an equal
    /// value). A record compares a dictionary member by reference, which would make
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
        && Source == other.Source
        && LicensedTo == other.LicensedTo
        && SameEntitlements(Entitlements, other.Entitlements)
        && SameMeters(Meters, other.Meters);

    /// <summary>A hash agreeing with <see cref="Equals(LicenseStatus?)"/>: the entitlements' taken without regard to their order.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode()
    {
        var granted = 0;
        foreach (var (name, entitlement) in Entitlements) granted ^= HashCode.Combine(name, entitlement);
        var counted = 0;
        foreach (var (code, meter) in Meters) counted ^= HashCode.Combine(code, meter);
        return HashCode.Combine(HashCode.Combine(State, Reason, Key, TrialDaysLeft, TrialEndsAt, Test, Stalled, granted), Source, LicensedTo, counted);
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
        builder.Append(" }, Meters = {");
        first = true;
        foreach (var (code, meter) in Meters.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            builder.Append(first ? " " : ", ").Append(code).Append(" = ").Append(meter);
            first = false;
        }
        builder.Append(" }, Stalled = ").Append(Stalled)
            .Append(", Source = ").Append(Source)
            .Append(", LicensedTo = ").Append(LicensedTo);
        return true;
    }

    private static bool SameMeters(IReadOnlyDictionary<string, MeterStatus> a, IReadOnlyDictionary<string, MeterStatus> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (code, meter) in a)
        {
            if (!b.TryGetValue(code, out var other) || meter != other) return false;
        }
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
