namespace KeyGrant.Licensing.Internal;

/// <summary>What a lease says about this build, offline (<see cref="Offline.CheckLease"/>).</summary>
internal sealed record LeaseCheck(bool Ok, bool Test, LeaseClaims? Claims, LeaseCheckFailure? Reason)
{
    public static LeaseCheck Licensed(bool test, LeaseClaims claims) => new(true, test, claims, null);

    public static LeaseCheck Refused(LeaseCheckFailure reason) => new(false, false, null, reason);
}

/// <summary>Why a lease does not license this build on this device.</summary>
internal enum LeaseCheckFailure
{
    Expired,
    Upgrade,
    Outdated,
    Tampered,
    Device,
}

/// <summary>Whose a licence lease must be: the key and activation stored, and this device.</summary>
/// <param name="Key">The stored key.</param>
/// <param name="ActivationId">The stored activation.</param>
/// <param name="Claims">Every <c>fp</c> claim this device answers to.</param>
/// <param name="CanTell">Whether this run could tell who the device is.</param>
internal sealed record LeaseHolder(string Key, string ActivationId, IReadOnlyList<string> Claims, bool CanTell);

/// <summary>A fresh lease's claims as the clock guard reads them (<see cref="Offline.ClockCorrection"/>): its key, activation and signing time (seconds).</summary>
internal sealed record SignedAt(string Sub, string Aid, long Iat)
{
    public static SignedAt? Of(LeaseClaims? claims) => claims is null ? null : new(claims.Sub, claims.Aid, claims.Iat);
}

/// <summary>When something was asked of the server (guarded device clock, ms), and the stored stamp of that kind the asker saw.</summary>
internal record Stamp(long At, long? Seen);

/// <summary>An ask of the server about an activation, as a save of its answer sees it.</summary>
internal sealed record Asked(long At, string? ActivationId, long? Seen) : Stamp(At, Seen);

/// <summary>A save about an activation that is not an answer (what a check-in noted it ran).</summary>
internal sealed record About(string? ActivationId);

/// <summary>What a save is based on (<see cref="Offline.Merged"/>).</summary>
internal sealed record Basis
{
    public static readonly Basis None = new();

    /// <summary>The answer to an ask about an activation: stamped <c>verdictAt</c>.</summary>
    public Asked? Answer { get; init; }

    /// <summary>An activation's answer, about the activation it names: stamped <c>verdictAt</c>.</summary>
    public Asked? Activation { get; init; }

    /// <summary>A save about an activation that is not an answer.</summary>
    public About? About { get; init; }

    /// <summary>The server's answer to a trial ask: stamped <c>trialAt</c>.</summary>
    public Stamp? Trial { get; init; }

    /// <summary>
    /// A save about a purchase secret (its drop, or the wait after a claim): its purchase fields
    /// (<see cref="StateFields.Purchase"/>) land only while that secret is the one stored, so one another
    /// process made meanwhile is neither dropped nor stamped by it. A wait carries the
    /// <see cref="StoredState.PurchaseTokenAt"/> its asker read, and lands only while that is stored too: a
    /// link handed out again since cleared the wait so that the next status asks at once, and a late answer
    /// must not start it again. A drop carries none: a spent secret is dead however recently its link was
    /// handed out.
    /// </summary>
    public AboutToken? Purchase { get; init; }
}

/// <summary>A save about the purchase secret <paramref name="Token"/> (<see cref="Basis.Purchase"/>): a wait names the link stamp its asker read (<paramref name="At"/>), a drop none.</summary>
internal sealed record AboutToken(string Token, long? At = null)
{
    /// <summary>Whether this save is about the secret stored in <paramref name="latest"/>, its link as handed out when its asker read it (<c>At</c>, compared for equality only).</summary>
    public bool AboutStored(StoredState latest) => latest.PurchaseToken == Token && (At is null || latest.PurchaseTokenAt == At);
}

/// <summary>
/// What the SDK's engine decides from what the device holds, without asking the server: whether a lease
/// licenses this build, what a trial shows, which refusal still stands, and when to ask the server again.
/// </summary>
internal static partial class Offline
{
    /// <summary>How long after the server answered without a lease before the SDK asks again on its own.</summary>
    public const long AskAgainMs = 60 * 60_000;

    /// <summary>How long after an ask that got no answer at all before the SDK asks again on its own.</summary>
    public const long RetryUnansweredMs = 60_000;

    /// <summary>A valid lease is renewed early once less than this is left of it, or less than half its life.</summary>
    public const long RenewWithinMs = 7 * 24 * 60 * 60_000L;

    /// <summary>How far the clock guard may run ahead of a fresh lease's signing time before it is brought back.</summary>
    public const long ClockToleranceMs = 60 * 60_000;

    /// <summary>The lease's <c>major</c> claim for a key with no upper bound.</summary>
    public const long OpenMajor = 1_000_000;

    /// <summary>Whether two licence keys are the same key: the server matches them without regard to case.</summary>
    public static bool SameKey(string a, string b) =>
        string.Equals(a.ToUpperInvariant(), b.ToUpperInvariant(), StringComparison.Ordinal);

    /// <summary>
    /// A verified licence lease held to this product, this holder and this build's major: another
    /// product, key or activation is <c>tampered</c>; a machine-keyed seat's lease for another device
    /// is <c>device</c> (only when this run can tell); a build below <c>minMajor</c> is
    /// <c>outdated</c>, one above <c>major</c> needs an <c>upgrade</c>.
    /// </summary>
    public static LeaseCheck CheckLease(VerifyResult v, string product, long major, LeaseHolder holder)
    {
        if (!v.Valid || v.Claims is null)
        {
            return LeaseCheck.Refused(v.Reason == VerifyFailure.Expired ? LeaseCheckFailure.Expired : LeaseCheckFailure.Tampered);
        }
        var claims = v.Claims;
        if (claims.Product != product) return LeaseCheck.Refused(LeaseCheckFailure.Tampered);
        var theirs = !SameKey(claims.Sub, holder.Key) || claims.Aid != holder.ActivationId;
        if (theirs) return LeaseCheck.Refused(LeaseCheckFailure.Tampered);
        // Held to its device only when its seat is keyed by a machine id (`fpk`).
        var held = claims.Fp is not null && claims.Fpk == DeviceKind.Machine;
        if (held && holder.CanTell && !holder.Claims.Contains(claims.Fp!, StringComparer.Ordinal))
        {
            return LeaseCheck.Refused(LeaseCheckFailure.Device);
        }
        if (major < (claims.MinMajor ?? 1)) return LeaseCheck.Refused(LeaseCheckFailure.Outdated);
        if (major > claims.Major) return LeaseCheck.Refused(LeaseCheckFailure.Upgrade);
        return LeaseCheck.Licensed(claims.Test == true, claims);
    }

    /// <summary>
    /// Whether a licence lease is held to a machine that is not this device: its seat keyed by a
    /// machine id (<c>fp</c>, <c>fpk</c> machine) that this run can tell is not one of its own
    /// (<paramref name="claims"/>). Read from the claims of a lease whose signature holds, expired or
    /// not: a seat does not change hands when its lease runs out. A seat that is another device's is
    /// not this device's to release (<see cref="License.DeactivateAsync"/>).
    /// </summary>
    public static bool HeldElsewhere(VerifyResult v, IReadOnlyList<string> claims, bool canTell)
    {
        var signed = v.Valid ? v.Claims : v.Reason == VerifyFailure.Expired ? v.Claims : null;
        if (signed?.Fp is not { } fp || signed.Fpk != DeviceKind.Machine || !canTell) return false;
        return !claims.Contains(fp, StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="kept"/> after running <paramref name="major"/> on a lease with these claims at
    /// <paramref name="now"/>: raised to it while the lease carries an open upgrade window and now is
    /// before its end, and left as it was after that end.
    /// </summary>
    public static int? KeptWith(int? kept, LeaseClaims claims, long now, int major)
    {
        if (claims.UpgradeUntil is not { } until || now >= (double)until * 1000) return kept;
        return Math.Max(kept ?? 0, major);
    }

    /// <summary>
    /// A trial lease, verified: a trial's (<c>model</c>), for this product, not past its end, and for
    /// this device (its <c>aid</c>, judged only when started under a machine id and this run can tell).
    /// A licence's lease in the trial's place is not a trial: the server only ever signs a trial lease
    /// as <c>model: "trial"</c>.
    /// </summary>
    public static bool TrialLeaseValid(VerifyResult v, string product, IReadOnlyList<string> claims, bool canTell)
    {
        if (!v.Valid || v.Claims is null || v.Claims.Model != "trial" || v.Claims.Product != product) return false;
        return v.Claims.Fpk != DeviceKind.Machine || !canTell || claims.Contains(v.Claims.Aid, StringComparer.Ordinal);
    }

    /// <summary>
    /// Now, by the clock guard: never earlier than the latest time the device has seen
    /// (<paramref name="lastSeen"/>), so a clock put back cannot bring an expired lease back.
    /// </summary>
    public static long GuardedNow(long clockNow, long? lastSeen) => Math.Max(clockNow, lastSeen ?? 0);

    /// <summary>
    /// The latest time seen (<c>lastSeen</c>) that a save writes, onto the state as stored when it saves
    /// (<paramref name="stored"/>): the guard brought back on the server's evidence
    /// (<paramref name="guard"/>, a <see cref="ClockCorrection"/>) exactly as given when the save
    /// carries one (0 included); otherwise never earlier than what is stored, nor than the device clock
    /// now, so the guard only ever moves forward on its own.
    /// </summary>
    public static long SavedLastSeen(long? stored, long clockNow, long? guard) => guard ?? Math.Max(stored ?? 0, clockNow);

    /// <summary>
    /// Where the clock guard should be brought back to on a lease the server has just signed, or null
    /// to leave it. When the guard is MORE than <see cref="ClockToleranceMs"/> past the lease's signing
    /// time (<c>iat</c>), it comes back to the device clock, never below <c>iat</c>. Only a lease signed
    /// for THIS key (without regard to case) and activation, and no older than the lease the device
    /// holds (<paramref name="heldIat"/>), moves it. <paramref name="claims"/> is the fresh lease's,
    /// verified (valid or merely expired); null when it did not verify, which moves nothing.
    /// </summary>
    public static long? ClockCorrection(long clockNow, long? lastSeen, SignedAt? claims, long? heldIat, string key, string activationId)
    {
        if (claims is null || !SameKey(claims.Sub, key) || claims.Aid != activationId) return null;
        if ((lastSeen ?? 0) - (double)claims.Iat * 1000 <= ClockToleranceMs) return null;
        if (heldIat is { } held && claims.Iat < held) return null;
        return Math.Max(clockNow, claims.Iat * 1000);
    }

    /// <summary>
    /// What storing the server's refusal changes: a verdict on the KEY drops the lease; one on this
    /// BUILD keeps it, with the major refused; one on this DEVICE keeps it, with the device's own claim.
    /// </summary>
    public static StatePatch RefusalPatch(Refusal refused, int major, string? ownClaim) => refused switch
    {
        Refusal.Revoked => new StatePatch
        {
            [StateField.Lease] = null,
            [StateField.Refused] = refused,
            [StateField.RefusedMajor] = null,
            [StateField.RefusedFor] = null,
        },
        Refusal.Device => new StatePatch
        {
            [StateField.Refused] = refused,
            [StateField.RefusedMajor] = null,
            [StateField.RefusedFor] = ownClaim is not null ? new[] { ownClaim } : Array.Empty<string>(),
        },
        _ => new StatePatch
        {
            [StateField.Refused] = refused,
            [StateField.RefusedMajor] = major,
            [StateField.RefusedFor] = null,
        },
    };

    /// <summary>What a check-in with this device writes of the install's key: a machine id read here keys it.</summary>
    public static StatePatch KeyedBy(Device device) =>
        device.Kind == DeviceKind.Machine ? new StatePatch { [StateField.DeviceKind] = DeviceKind.Machine } : new StatePatch();

    /// <summary>
    /// Whether a stored stamp is a NEWER answer than <paramref name="stamp"/>'s: saved since that asker
    /// read the state, and asked for later than it.
    /// </summary>
    public static bool Outdated(long? stored, Stamp stamp) => stored != stamp.Seen && (stored ?? long.MinValue) > stamp.At;

    /// <summary>Whether an ask's answer is out of date by the time it is saved (see <see cref="Merged"/>).</summary>
    public static Superseded? SupersededBy(StoredState latest, Asked asked)
    {
        if (!string.Equals(latest.ActivationId, asked.ActivationId, StringComparison.Ordinal)) return Superseded.Activation;
        return Outdated(latest.VerdictAt, asked) ? Superseded.Answer : null;
    }

    /// <summary>A patch without the verdict fields: what of an out-of-date answer is still saved.</summary>
    public static StatePatch WithoutVerdict(StatePatch patch) => patch.Without(StateFields.Verdict);

    /// <summary>Only the trial evidence of a patch: what lands on another activation.</summary>
    public static StatePatch TrialOnly(StatePatch patch) => patch.Only(StateFields.Trial);

    private static Superseded? OutOfDate(StoredState latest, Basis basis)
    {
        if (basis.Answer is { } answer) return SupersededBy(latest, answer);
        // An activation's answer on the same seat, activated again: an answer like any.
        if (basis.Activation is { } activation)
        {
            var same = string.Equals(latest.ActivationId, activation.ActivationId, StringComparison.Ordinal);
            return same && Outdated(latest.VerdictAt, activation) ? Superseded.Answer : null;
        }
        if (basis.About is { } about && !string.Equals(latest.ActivationId, about.ActivationId, StringComparison.Ordinal))
        {
            return Superseded.Activation;
        }
        return null;
    }

    /// <summary>
    /// A patch merged onto the state as stored now, by what it is based on: an answer or an
    /// activation is stamped <c>verdictAt</c>, a trial answer <c>trialAt</c>; a save about an
    /// activation no longer stored keeps only its trial evidence; an answer older than the stored one
    /// loses its verdict fields; a trial answer older than the stored one loses its end and lease; a
    /// save about a purchase secret no longer stored (or, for a wait, one whose link was handed out again
    /// since) loses its purchase fields; the trial start only ever moves earlier; the kept major of the
    /// stored activation only ever up.
    /// </summary>
    public static StoredState Merged(StoredState latest, StatePatch patch, Basis? basis = null)
    {
        basis ??= Basis.None;
        Asked? stamp = basis.Answer ?? basis.Activation;
        var applied = patch.Copy();
        if (stamp is not null) applied.Set(StateField.VerdictAt, stamp.At);
        if (basis.Trial is not null) applied.Set(StateField.TrialAt, basis.Trial.At);
        var state = OutOfDate(latest, basis);
        if (state == Superseded.Activation) applied = TrialOnly(applied);
        if (state == Superseded.Answer) applied = WithoutVerdict(applied);
        if (basis.Trial is not null && Outdated(latest.TrialAt, basis.Trial)) applied = applied.Without(StateFields.TrialAnswer);
        if (basis.Purchase is { } about && !about.AboutStored(latest)) applied = applied.Without(StateFields.Purchase);

        var trialStart = Wire.Earliest(latest.TrialStart, applied.ValueOf(StateField.TrialStart) as long?);
        var result = applied.ApplyTo(latest);
        if (trialStart is not null) result = result with { TrialStart = trialStart };

        // About the stored activation: its kept major only goes up, a renewal's none included.
        // Unbased (a deactivation) or a fresh activation: as patched.
        var hasAbout = stamp is not null || basis.About is not null;
        var aboutId = stamp is not null ? stamp.ActivationId : basis.About?.ActivationId;
        if (hasAbout && string.Equals(latest.ActivationId, aboutId, StringComparison.Ordinal))
        {
            var noted = applied.ValueOf(StateField.KeptMajor) as int?;
            int? kept = (latest.KeptMajor, noted) switch
            {
                ({ } a, { } b) => Math.Max(a, b),
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => null,
            };
            if (kept is not null) result = result with { KeptMajor = kept };
        }
        return result;
    }

    /// <summary>What a new lease from the server clears: any refusal, and the wait before asking again.</summary>
    public static StatePatch Leased => new()
    {
        [StateField.Refused] = null,
        [StateField.RefusedMajor] = null,
        [StateField.RefusedFor] = null,
        [StateField.BackoffSince] = null,
        [StateField.BackoffMs] = null,
    };

    /// <summary>What a deactivated device keeps of its licence: nothing but its trial evidence and clock guard.</summary>
    public static StatePatch Unkeyed => new StatePatch
    {
        [StateField.Key] = null,
        [StateField.ActivationId] = null,
        [StateField.Lease] = null,
        [StateField.CheckedMajor] = null,
        [StateField.KeptMajor] = null,
        // Install memory protects a current seat; with none, the next activation is free of it.
        [StateField.DeviceKind] = null,
    }.With(Leased);

    /// <summary>
    /// The stored refusal that still stands for a build running <paramref name="major"/>, or null: a
    /// key-level one always; <c>upgrade</c> for the refused major and newer; <c>outdated</c> for it and
    /// older; one with no major stored for every build; a device one only while this run can tell it
    /// is still the device it was given for.
    /// </summary>
    public static Refusal? StandingRefusal(StoredState state, int major, string? ownClaim, bool canTell)
    {
        if (state.Refused is not { } refused) return null;
        if (refused == Refusal.Device && state.RefusedFor is { } refusedFor)
        {
            var own = canTell ? ownClaim : null;
            return own is not null && refusedFor.Contains(own, StringComparer.Ordinal) ? refused : null;
        }
        if (refused is Refusal.Revoked or Refusal.Device || state.RefusedMajor is null) return refused;
        var refusedMajor = state.RefusedMajor.Value;
        return (refused == Refusal.Upgrade ? major >= refusedMajor : major <= refusedMajor) ? refused : null;
    }

    /// <summary>
    /// Whether to ask the server now: when forced, when no ask has come back without a lease since
    /// the last lease, or once that ask's wait is over. <paramref name="clockNow"/> is the device clock
    /// as it is: a clock set back since the ask asks at once.
    /// </summary>
    public static bool AskDue(StoredState state, long clockNow, bool force)
    {
        var since = state.BackoffSince;
        if (force || since is null || clockNow < since) return true;
        return clockNow - since.Value >= (state.BackoffMs ?? AskAgainMs);
    }

    /// <summary>
    /// Whether a valid lease is near enough its end to renew it early: less than
    /// <see cref="RenewWithinMs"/> left, or less than half its life if that is shorter.
    /// </summary>
    public static bool RenewsEarly(LeaseClaims claims, long now)
    {
        var left = (double)claims.Exp * 1000 - now;
        var life = ((double)claims.Exp - claims.Iat) * 1000;
        return left < Math.Min(RenewWithinMs, life / 2);
    }
}

/// <summary>How an ask's answer is out of date by the time it is saved.</summary>
internal enum Superseded
{
    /// <summary>The state now names another activation, or none.</summary>
    Activation,

    /// <summary>A newer answer about the same activation has been saved.</summary>
    Answer,
}
