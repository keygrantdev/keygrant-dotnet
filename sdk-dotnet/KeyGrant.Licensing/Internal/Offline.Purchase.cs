namespace KeyGrant.Licensing.Internal;

// The waits and bounds of buying from the app with no key typed, and of a running trial's launch
// report, decided without I/O; and the major a build runs as.
internal static partial class Offline
{
    /// <summary>
    /// How long after a claim that brought no key back (<see cref="StoredState.ClaimAskedAt"/>) before a
    /// status asks again on its own (<see cref="ClaimDue"/>): a customer back from the checkout finds the
    /// app licensed within minutes, and a device with a secret held does not ask on every status. A
    /// refresh and a trial start ask at once.
    /// </summary>
    public const long ClaimWaitMs = 5 * 60_000;

    /// <summary>
    /// How long after <see cref="License.PurchaseUrlAsync"/> last handed a link out
    /// (<see cref="StoredState.PurchaseTokenAt"/>) an unforced status still asks the claim
    /// (<see cref="ClaimDue"/>): a customer who buys does so soon after opening the link. After it, only a
    /// refresh and a trial start ask, until <see cref="PurchaseTokenMs"/>, so an abandoned checkout does not
    /// put the network in front of every launch.
    /// </summary>
    public const long ClaimEagerMs = DayMs;

    /// <summary>
    /// How long a purchase secret is held after <see cref="License.PurchaseUrlAsync"/> last handed out its
    /// link (<see cref="PurchaseTokenKept"/>, by the guarded clock): a checkout left that long is not coming
    /// back, and a secret past it is dropped, never sent. A drop is the device's alone: it revokes nothing
    /// on the server.
    /// </summary>
    public const long PurchaseTokenMs = 30 * DayMs;

    /// <summary>How often, at most, a running trial reports its launch (<see cref="TrialReportDue"/>), a trial answer counting as one.</summary>
    public const long TrialReportMs = DayMs;

    /// <summary>
    /// The major a build runs as: the configured one (absent: 1), read as the server reads a version (0.x
    /// as 1, and majors from 1 up). So a build configured as major 0 is major 1 everywhere the SDK uses the
    /// major (every body, the offline check, the kept major, a standing refusal), rather than refused
    /// offline as <c>outdated</c> by every lease.
    /// </summary>
    public static int MajorOf(int? configured) => Math.Max(1, configured ?? 1);

    /// <summary>
    /// What an activation (typed, or a bought key picked up) and a deactivation clear: the purchase secret
    /// and its stamps. A secret left on a keyed install would, after a later deactivation, activate the
    /// device on a key the customer may have meant for another machine.
    /// </summary>
    public static StatePatch NoPurchase => new()
    {
        [StateField.PurchaseToken] = null,
        [StateField.PurchaseTokenAt] = null,
        [StateField.ClaimAskedAt] = null,
    };

    /// <summary>
    /// Whether the stored purchase secret is still held at <paramref name="now"/> (the guarded clock):
    /// there is one, stamped with when its link was last handed out, and less than
    /// <see cref="PurchaseTokenMs"/> has passed since. A stamp ahead of now holds it too (no time has
    /// passed that can be measured); a secret with no stamp is not held.
    /// </summary>
    public static bool PurchaseTokenKept(StoredState state, long now) =>
        state.PurchaseToken is not null && state.PurchaseTokenAt is { } at && now - at < PurchaseTokenMs;

    /// <summary>
    /// Whether a status asks the claim now, unforced (a refresh and a trial start always ask). Only while
    /// the link is fresh: less than <see cref="ClaimEagerMs"/> since it was last handed out
    /// (<see cref="StoredState.PurchaseTokenAt"/>, judged at <paramref name="now"/>, the guarded clock; a
    /// stamp ahead of now is fresh). And only once the wait is over
    /// (<see cref="StoredState.ClaimAskedAt"/>, judged at <paramref name="clockNow"/>, the device clock, as
    /// <see cref="AskDue"/>: the wait is courtesy, not protection): not asked since the link, the device
    /// clock set back before the last ask, or <see cref="ClaimWaitMs"/> since it. The two clocks are never
    /// compared with each other.
    /// </summary>
    public static bool ClaimDue(StoredState state, long clockNow, long now)
    {
        if (state.PurchaseTokenAt is not { } at || now - at >= ClaimEagerMs) return false;
        return state.ClaimAskedAt is not { } asked || clockNow < asked || clockNow - asked >= ClaimWaitMs;
    }

    /// <summary>
    /// Whether a running trial reports its launch at <paramref name="now"/> (the guarded clock): neither
    /// its last trial answer (<see cref="StoredState.TrialAt"/>) nor its last report
    /// (<see cref="StoredState.TrialReportedAt"/>) is within <see cref="TrialReportMs"/> before now. A
    /// stamp that is absent, or at least that long before now, is not within it; one ahead of now is.
    /// </summary>
    public static bool TrialReportDue(StoredState state, long now)
    {
        bool Recent(long? at) => at is { } stamp && now - stamp < TrialReportMs;
        return !Recent(state.TrialAt) && !Recent(state.TrialReportedAt);
    }
}
