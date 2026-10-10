using System.Collections.Concurrent;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// Who this device is, for holding a lease to it and telling the server: as far as can be told this
/// call, and never guessed. A fingerprint that cannot be read in time is "cannot tell"
/// (<see cref="CanTell"/> false), not a stand-in, and is read again next call.
/// </summary>
internal sealed record Device
{
    /// <summary>This run's own fingerprint, or null when it cannot be told this time.</summary>
    public string? Own { get; init; }

    /// <summary>What <see cref="Own"/> hashes; null when the fingerprinter does not say, or with no <see cref="Own"/>.</summary>
    public DeviceKind? Kind { get; init; }

    public bool CanTell { get; init; }

    /// <summary>Every fingerprint this device answers to: its own first, then its alternates.</summary>
    public IReadOnlyList<string> Fingerprints { get; init; } = [];

    /// <summary>Their <c>fp</c> claims.</summary>
    public IReadOnlyList<string> Claims { get; init; } = [];

    /// <summary>The claim of <see cref="Own"/> alone: what a <c>device</c> refusal is held to.</summary>
    public string? OwnClaim { get; init; }

    /// <summary>The host-name fingerprint to activate under when the machine id cannot be read even in an activation's time.</summary>
    public string? Hostname { get; init; }
}

/// <summary>Who a device is, decided without I/O.</summary>
internal static class Devices
{
    /// <summary>
    /// The fingerprint an activation is made under, and what it hashes; or null: an activation answers
    /// network for the app to retry.
    /// The device's own; when even an activation's time cannot tell it: on an install already keyed by a
    /// machine id, nothing; otherwise the host name, so a machine whose id never reads in time can still
    /// activate (its first check-in that reads the id moves the seat to it).
    /// </summary>
    public static (string Fingerprint, DeviceKind? Kind)? ActivationIdentity(Device device, DeviceKind? installKind)
    {
        if (device.Own is not null) return (device.Own, device.Kind);
        if (installKind == DeviceKind.Machine || device.Hostname is null) return null;
        return (device.Hostname, DeviceKind.Hostname);
    }

    /// <summary>
    /// Who this device is, from what its fingerprinter said this time (<paramref name="identity"/>), on an
    /// install keyed as <paramref name="installKind"/>, held to its device or not
    /// (<paramref name="deviceHold"/>). <paramref name="claimOf"/> is
    /// <see cref="Hashes.FingerprintClaim"/>, which a reader may remember.
    /// <para>
    /// Install memory: once a machine id has keyed this install, a host-name answer is not this device
    /// either. The id being unreadable now says nothing about whether the machine changed, so it is
    /// "cannot tell", never the host name. A fingerprint of no kind is not judged by it.
    /// </para>
    /// <para>
    /// Install memory applies only to a held device. Not held, a device has no kind, so no seat is
    /// keyed to it, and is judged as a run that cannot tell, so no lease is refused for its device and
    /// the server is told no fingerprints. Its own still activates.
    /// </para>
    /// </summary>
    public static Device From(DeviceIdentity identity, DeviceKind? installKind, bool deviceHold, Func<string, string> claimOf)
    {
        var keyedAs = deviceHold ? installKind : null;
        // A kind with no fingerprint says nothing.
        DeviceKind? read = identity.Fingerprint is null ? null : identity.Kind;
        var own = read == DeviceKind.Hostname && keyedAs == DeviceKind.Machine ? null : identity.Fingerprint;
        // A custom fingerprinter's null alternate is no fingerprint: left out, never thrown on.
        var alternates = (identity.Alternates ?? []).Where(f => f is not null).ToList();
        var fingerprints = new List<string>();
        if (own is not null) fingerprints.Add(own);
        fingerprints.AddRange(alternates.Where(f => f != own));
        var hostname = read == DeviceKind.Hostname ? identity.Fingerprint : (alternates.Count > 0 ? alternates[0] : null);
        var claims = fingerprints.Select(claimOf).ToList();
        var device = new Device { Own = own, CanTell = own is not null, Fingerprints = fingerprints, Claims = claims, Hostname = hostname };
        if (!deviceHold) return device with { CanTell = false };
        return device with { OwnClaim = own is not null ? claims[0] : null, Kind = own is not null ? read : null };
    }
}

/// <summary>Reads, and once settled keeps, who this device is.</summary>
internal sealed class DeviceIdentityReader
{
    private readonly IFingerprinter fingerprinter;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, string> claims = new(StringComparer.Ordinal);
    private DeviceIdentity? known;

    public DeviceIdentityReader(IFingerprinter fingerprinter, TimeProvider time)
    {
        this.fingerprinter = fingerprinter;
        this.time = time;
    }

    /// <summary>
    /// Who this device is (<see cref="Devices.From"/>), reading for at most about
    /// <paramref name="timeout"/>, on an install keyed as <paramref name="installKind"/>, held to its
    /// device unless <paramref name="deviceHold"/> is false. A fingerprinter that fails, or does not
    /// answer in time, gives "cannot tell", not kept: a failed read must neither stand in for the
    /// device nor stick.
    /// </summary>
    public async Task<Device> ReadAsync(TimeSpan timeout, DeviceKind? installKind, bool deviceHold, CancellationToken cancellationToken)
    {
        var identity = Volatile.Read(ref known) ?? await IdentityAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (identity.Fingerprint is not null) Volatile.Write(ref known, identity);
        return Devices.From(identity, installKind, deviceHold, ClaimOf);
    }

    private string ClaimOf(string fingerprint) => claims.GetOrAdd(fingerprint, Hashes.FingerprintClaim);

    /// <summary>The fingerprinter's answer, bounded; a fingerprinter with only <c>GetAsync</c> says nothing of what it hashes.</summary>
    private async Task<DeviceIdentity> IdentityAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? pending = null;
        try
        {
            var identifying = Tasks.Start(() => fingerprinter.IdentifyAsync(timeout, abandon.Token));
            pending = identifying;
            var identified = await Tasks.Bounded(identifying, timeout + TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            if (identified is not null) return identified;
            var getting = Tasks.Start(() => fingerprinter.GetAsync(abandon.Token));
            pending = getting;
            var fingerprint = await Tasks.Bounded(getting, timeout, time, cancellationToken).ConfigureAwait(false);
            return new DeviceIdentity(fingerprint, null, []);
        }
        catch (Exception)
        {
            // Abandoned (out of time, or the caller stopped waiting): a fingerprinter still at it is told
            // to stop; one that answered, or failed, never is.
            if (pending is { IsCompleted: false }) Tasks.TellToStop(abandon);
            // A failed read, a read out of time, or a caller who stopped waiting: cannot tell, this time
            // only (never kept). Never an exception: a status answers from what the device holds.
            return new DeviceIdentity(null, null, []);
        }
    }
}
