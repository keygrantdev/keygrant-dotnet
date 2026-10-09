using System.Collections.ObjectModel;

namespace KeyGrant.Licensing.Internal;

/// <summary>What a trial's status reads of its trial lease (<see cref="Offline.TrialLeaseOf"/>).</summary>
/// <param name="Valid">Whether it is valid here (<see cref="Offline.TrialLeaseValid"/>).</param>
/// <param name="EndsAt">The end it was signed to (its <c>exp</c>, ms), when it is valid.</param>
/// <param name="Entitlements">What it grants (its <c>ent</c>), when it is valid; none otherwise.</param>
internal sealed record TrialLease(bool Valid, long? EndsAt, IReadOnlyDictionary<string, Entitlement> Entitlements)
{
    public static readonly TrialLease NotValid = new(false, null, ReadOnlyDictionary<string, Entitlement>.Empty);
}

// The statuses the offline decisions give: what a stored licence lease says when the server gives no
// verdict, and what a trial shows.
internal static partial class Offline
{
    private const long DayMs = 86_400_000;

    /// <summary>
    /// The status a stored licence lease gives, judged offline (<see cref="CheckLease"/>), when the
    /// server gives no verdict on it: licensed, with the check's <c>test</c> flag (its one source: test
    /// licences verify like any other, signed with the product's real key, and the flag is how an app
    /// tells them apart) and the lease's entitlements (empty when it grants none; the status holds its
    /// own read-only copy);
    /// <c>expired</c> / <c>offline</c> once it has run out; <c>invalid</c> with the reason, and the key,
    /// when it is not for this build or this device; <c>invalid</c> / <c>tampered</c>, with no key, when
    /// it is not this licence's at all. Only a lease that licenses carries entitlements.
    /// </summary>
    public static LicenseStatus LeaseStatus(LeaseCheck check, string? key) => check switch
    {
        { Ok: true } => new LicenseStatus { State = LicenseState.Licensed, Key = key, Test = check.Test, Entitlements = check.Claims!.Ent },
        { Reason: LeaseCheckFailure.Expired } => new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = key },
        { Reason: LeaseCheckFailure.Upgrade } => Wire.InvalidStatus(LicenseReason.Upgrade, key),
        { Reason: LeaseCheckFailure.Outdated } => Wire.InvalidStatus(LicenseReason.Outdated, key),
        { Reason: LeaseCheckFailure.Device } => Wire.InvalidStatus(LicenseReason.Device, key),
        _ => new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Tampered },
    };

    /// <summary>
    /// A trial lease, verified, as a trial's status reads it: whether it is valid here
    /// (<see cref="TrialLeaseValid"/>), and, when it is, the end it was signed to (ms) and its
    /// entitlements. A lease that is not valid here says nothing of either.
    /// </summary>
    public static TrialLease TrialLeaseOf(VerifyResult v, string product, IReadOnlyList<string> claims, bool canTell)
    {
        if (!TrialLeaseValid(v, product, claims, canTell)) return TrialLease.NotValid;
        // Seconds to ms, held to the range of a long (a lease signed to a time beyond it ends "never").
        var exp = v.Claims!.Exp;
        return new TrialLease(true, exp > long.MaxValue / 1000 ? long.MaxValue : exp * 1000, v.Claims.Ent);
    }

    /// <summary>
    /// A trial's status from what the device holds: <paramref name="leaseValid"/> is its trial lease,
    /// checked (<see cref="TrialLeaseValid"/>), <paramref name="leaseEndsAt"/> the end that lease was
    /// signed to (its <c>exp</c>, in ms; null when it is not known), and
    /// <paramref name="leaseEntitlements"/> what it grants (its <c>ent</c>), which a running trial
    /// carries; every other answer carries none.
    /// <para>
    /// A trial runs only while its trial lease is valid. The <c>trialEndsAt</c> stored beside it is the
    /// server's word too, but UNSIGNED: trusted on its own, editing it in the state file was a trial that
    /// never ended, offline or not. KeyGrant signs a trial lease to the trial's own end, so an honest
    /// trial holds a valid one for every day it runs, and the signed end is the one shown. The stored
    /// end is shown only when the lease's is not known.
    /// </para>
    /// </summary>
    public static LicenseStatus TrialStatusOf(
        StoredState state,
        long now,
        bool leaseValid,
        long? leaseEndsAt = null,
        IReadOnlyDictionary<string, Entitlement>? leaseEntitlements = null)
    {
        // No trial evidence at all: the trial was never started, not expired.
        if (state.TrialStart is null && state.TrialEndsAt is null && state.TrialLease is null)
        {
            return new LicenseStatus { State = LicenseState.Unlicensed };
        }
        if (!leaseValid) return new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial };
        var trialEndsAt = leaseEndsAt ?? state.TrialEndsAt;
        int? daysLeft = null;
        if (trialEndsAt is { } end)
        {
            var days = Math.Max(0, Math.Ceiling((double)(end - now) / DayMs));
            daysLeft = days >= int.MaxValue ? int.MaxValue : (int)days;
        }
        return new LicenseStatus
        {
            State = LicenseState.Trial,
            TrialEndsAt = trialEndsAt,
            TrialDaysLeft = daysLeft,
            Entitlements = leaseEntitlements ?? ReadOnlyDictionary<string, Entitlement>.Empty,
        };
    }
}
