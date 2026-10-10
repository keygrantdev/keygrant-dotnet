namespace KeyGrant.Licensing.Internal;

// What an activation saves, the major a build runs as, and when a running trial reports its launch,
// decided without I/O.
internal static partial class Offline
{
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
    /// What an activation saves, stamped as an activation: the key, the activation, the lease, the major
    /// heard, the kept major (<see cref="KeptWith"/> from none), any refusal and wait cleared
    /// (<see cref="Leased"/>), and what the install is keyed by (cleared for a fingerprint of unknown kind).
    /// </summary>
    public static StatePatch ActivationPatch(string key, string activationId, string lease, LeaseClaims claims, long now, int major, DeviceKind? kind)
    {
        var patch = new StatePatch
        {
            [StateField.Key] = key,
            [StateField.ActivationId] = activationId,
            [StateField.Lease] = lease,
            [StateField.CheckedMajor] = major,
            [StateField.KeptMajor] = KeptWith(null, claims, now, major),
        }.With(Leased);
        patch.Set(StateField.DeviceKind, kind);
        return patch;
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
