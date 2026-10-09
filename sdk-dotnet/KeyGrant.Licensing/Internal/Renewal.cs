namespace KeyGrant.Licensing.Internal;

/// <summary>What an ask's answer is judged from.</summary>
/// <param name="Response">The answer; null when there was none at all (the request threw or timed out).</param>
/// <param name="Check">
/// The offline check (<see cref="Offline.CheckLease"/>) of the lease a 200 carried, made as of
/// <paramref name="Now"/> on this device; null when it carried none.
/// </param>
/// <param name="AskedAt">When the ask was made, by the device clock (ms): where the wait before asking again starts.</param>
/// <param name="Now">The time the lease was judged at (ms): the guarded clock, reconciled to the server's.</param>
/// <param name="Major">This build's major, sent with the ask.</param>
/// <param name="Key">The stored key, which a status names.</param>
/// <param name="KeptMajor">The stored kept major (<see cref="Offline.KeptWith"/>).</param>
/// <param name="Device">
/// Who this device is, as the ask read it: a machine id read keys the install
/// (<see cref="Offline.KeyedBy"/>), and a device refusal is held to its own claim.
/// </param>
internal sealed record RenewalInput(
    HttpResult? Response,
    LeaseCheck? Check,
    long AskedAt,
    long Now,
    int Major,
    string? Key,
    int? KeptMajor,
    Device Device);

/// <summary>What an ask's answer does.</summary>
/// <param name="Answered">Whether the server answered at all: false for no answer, a 5xx or a 429.</param>
/// <param name="Status">The status the answer gives; null when it gives none, and the caller's own answer stands.</param>
/// <param name="Patch">What is saved, as the answer to the ask (<see cref="Offline.Merged"/>, basis answer).</param>
internal sealed record RenewalOutcome(bool Answered, LicenseStatus? Status, StatePatch Patch);

/// <summary>
/// The online validate (a renewal), decided without I/O: when a status asks the server while it holds
/// a lease that licenses this build (<see cref="AsksOnValidLease"/>), and what an ask's answer saves
/// and says (<see cref="Outcome"/>). The engine makes the request, judges the lease a 200 carried, and
/// saves the patch as the answer to its ask.
/// <para>
/// Licensing fails in favour of the paying customer: only an explicit verdict (a lease, or a refusal
/// <see cref="Wire.RefusalOf"/> names) gives a status. No answer at all, a 5xx, a 429, any other error,
/// and a 200 without a lease that can be used give none, and the caller's own answer (a valid lease, a
/// stored refusal, the lease's own end) stands.
/// </para>
/// </summary>
internal static class Renewal
{
    /// <summary>
    /// What a validate's answer saves and says. Every patch carries <c>deviceKind: machine</c> when this
    /// run read a machine id (<see cref="Offline.KeyedBy"/>): the seat is re-keyed by it whatever the
    /// answer, so the install is keyed by it from now on.
    /// <list type="bullet">
    /// <item>no answer at all: wait <see cref="Offline.RetryUnansweredMs"/>, no status;</item>
    /// <item>a 5xx or a 429: wait <see cref="Offline.AskAgainMs"/>, no status;</item>
    /// <item>a 200 with a lease that licenses this build: the lease, the major heard, the kept major, any refusal and wait cleared: licensed, with the lease's entitlements;</item>
    /// <item>a 200 with a lease the server stands behind that does not license this build or this device (upgrade, outdated, device): kept as above, with no kept major, so every later status says the same offline: invalid, with none;</item>
    /// <item>any other 200 (no lease, or one expired or not this licence's): the major heard and a wait of an hour, no status; never a lockout on a bad body;</item>
    /// <item>a refusal (the body's error word, on any status but a 200, a 5xx or a 429): the major heard, the wait, and the refusal: invalid with it;</item>
    /// <item>any other answer (a proxy's 4xx, an unknown product, a body that is not an object): the major heard and the wait, no status.</item>
    /// </list>
    /// </summary>
    public static RenewalOutcome Outcome(RenewalInput input)
    {
        var keyed = Offline.KeyedBy(input.Device);
        if (input.Response is not { } response)
        {
            return new RenewalOutcome(
                false,
                null,
                new StatePatch { [StateField.BackoffSince] = input.AskedAt, [StateField.BackoffMs] = Offline.RetryUnansweredMs }.With(keyed));
        }
        var backoff = new StatePatch { [StateField.BackoffSince] = input.AskedAt, [StateField.BackoffMs] = Offline.AskAgainMs }.With(keyed);
        if (Wire.NoAnswer(response.Status)) return new RenewalOutcome(false, null, backoff);
        if (response.Status == 200) return LeaseOutcome(input, response, backoff);
        // Lock out ONLY on an explicit definitive negative verdict. Everything else (a proxy's or WAF's
        // 4xx, an unknown-product 404, a malformed-body 400, any answer not recognised) is treated like
        // a network failure, so a still-valid lease is never revoked by an ambiguous answer.
        var refused = Wire.RefusalOf(Wire.ErrorOf(response.Json));
        // On a verdict, the next status must not license offline what the server has just refused.
        var verdict = refused is { } r ? Offline.RefusalPatch(r, input.Major, input.Device.OwnClaim) : new StatePatch();
        return new RenewalOutcome(
            true,
            refused is { } refusal ? Wire.InvalidStatus(Names.ReasonOf(refusal), input.Key) : null,
            new StatePatch { [StateField.CheckedMajor] = input.Major }.With(backoff).With(verdict));
    }

    /// <summary>A 200's answer (<see cref="Outcome"/>): by the lease it carried, as checked here.</summary>
    private static RenewalOutcome LeaseOutcome(RenewalInput input, HttpResult response, StatePatch backoff)
    {
        var (lease, _) = Wire.ParseLeaseBody(response.Json);
        if (!string.IsNullOrEmpty(lease) && input.Check is { } check)
        {
            // Licensed, with the lease's test flag and entitlements (Offline.LeaseStatus).
            if (check.Ok)
            {
                var kept = Offline.KeptWith(input.KeptMajor, check.Claims!, input.Now, input.Major);
                var leased = new StatePatch { [StateField.Lease] = lease, [StateField.CheckedMajor] = input.Major, [StateField.KeptMajor] = kept }
                    .With(Offline.Leased)
                    .With(Offline.KeyedBy(input.Device));
                return new RenewalOutcome(true, Offline.LeaseStatus(check, input.Key), leased);
            }
            // A lease the server stands behind that does not license this build, or this device: kept,
            // so every later status says the same offline.
            if (check.Reason is LeaseCheckFailure.Upgrade or LeaseCheckFailure.Outdated or LeaseCheckFailure.Device)
            {
                var kept = new StatePatch { [StateField.Lease] = lease, [StateField.CheckedMajor] = input.Major }
                    .With(Offline.Leased)
                    .With(Offline.KeyedBy(input.Device));
                return new RenewalOutcome(true, Offline.LeaseStatus(check, input.Key), kept);
            }
        }
        // 200 but no usable lease: nothing to heal with; whatever the caller has is kept (fail in favour
        // of the customer) rather than locked out on a bad body.
        return new RenewalOutcome(true, null, new StatePatch { [StateField.CheckedMajor] = input.Major }.With(backoff));
    }

    /// <summary>
    /// Whether a status, holding a lease that licenses this build, asks the server now (unforced; a
    /// refresh always asks). When the server should hear from this device before the lease runs out:
    /// a newer major than the server last heard, on an open lease (<c>major</c> at least
    /// <see cref="Offline.OpenMajor"/>; else it might not hear of it until the key's upgrade window has
    /// closed), or a lease nearing its end (<see cref="Offline.RenewsEarly"/>, by the guarded clock
    /// <paramref name="now"/>). And only once the wait after the last ask is over
    /// (<see cref="Offline.AskDue"/>, by the device clock <paramref name="clockNow"/>).
    /// </summary>
    public static bool AsksOnValidLease(LeaseClaims claims, StoredState state, int major, long now, long clockNow)
    {
        var newMajor = claims.Major >= Offline.OpenMajor && major > (state.CheckedMajor ?? 0);
        var due = newMajor || Offline.RenewsEarly(claims, now);
        return due && Offline.AskDue(state, clockNow, false);
    }
}
