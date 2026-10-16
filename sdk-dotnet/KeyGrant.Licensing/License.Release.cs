using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// Releasing a seat: the request carries the journal of unsynced spends, and the journal goes with the licence.
public sealed partial class License
{
    private async Task<DeactivateResult> DeactivateNowAsync(CancellationToken cancellationToken)
    {
        // What this process could not save lands first, so the release reports it: the journal goes with the seat.
        await SaveUnsavedJournalAsync().ConfigureAwait(false);
        var state = await LoadAsync().ConfigureAwait(false);
        // Nothing held is nothing to release: the seat is as free as it gets. Neither is a seat another
        // device holds this one's to release (a state file copied or restored from another PC): it is
        // only dropped here, and this device, which holds no seat, answers released.
        var released = true;
        if (!string.IsNullOrEmpty(state.Key) && !string.IsNullOrEmpty(state.ActivationId))
        {
            var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
            var elsewhere = SeatElsewhere(state, device);
            // Cancelled while who this device is was read, before anything was asked: nothing is dropped.
            cancellationToken.ThrowIfCancellationRequested();
            if (!elsewhere) released = await ReleaseAsync(state, device, cancellationToken).ConfigureAwait(false);
        }
        // Stamped, so that an answer to an older ask is not saved over it. The journal goes with the rest of the
        // licence: what the release carried is all the server will hear of it.
        var patch = Offline.Unkeyed;
        patch.Set(StateField.VerdictAt, EffectiveNow(state));
        await SaveWithAsync(patch, null, null, [new DeactivatedOp()]).ConfigureAwait(false);
        return new DeactivateResult(released);
    }

    /// <summary>
    /// The release request, carrying the journal of unsynced spends and, with it, the device's fingerprints as a validate
    /// sends them (so the server holds the reports to the seat's device; none when it cannot tell). Only a 200 is KeyGrant
    /// saying the seat is free: a 5xx, a 429 or a proxy's page is an answer, but not that one.
    /// </summary>
    private async Task<bool> ReleaseAsync(StoredState state, Device device, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["key"] = state.Key, ["activationId"] = state.ActivationId };
        if (Meters.SpendsReported(await ReportableAsync(state).ConfigureAwait(false)) is { } reports)
        {
            body["spends"] = reports;
            if (device.CanTell) body["fingerprints"] = Fingerprints(device);
        }
        try
        {
            return (await PostAsync("deactivate", body, null, cancellationToken).ConfigureAwait(false)).Status == 200;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Best-effort; still cleared locally.
            return false;
        }
    }

    /// <summary>
    /// Whether the stored activation is another device's: a device refusal that stands for this one, or
    /// a lease held to a machine id this device does not have, expired or not
    /// (<see cref="Offline.HeldElsewhere"/>). A state file copied or restored from another PC carries
    /// that PC's activation; releasing it would lock that PC out at its next check-in.
    /// </summary>
    private bool SeatElsewhere(StoredState state, Device device)
    {
        if (Offline.StandingRefusal(state, major, device.OwnClaim, device.CanTell) == Refusal.Device) return true;
        return !string.IsNullOrEmpty(state.Lease) && Offline.HeldElsewhere(Verify(state.Lease, EffectiveNow(state)), device.Claims, device.CanTell);
    }
}
