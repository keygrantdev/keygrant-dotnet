using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// The trial machinery under License: asking the server for this device's trial and saving its answer,
// even when the disk refuses it for now; a trial's status from what the device holds; asking again for
// a trial whose lease does not verify while its stored end is still ahead; and a running trial's
// launch, reported at most once a day in the background. License.cs decides when.
public sealed partial class License
{
    /// <summary>
    /// The server's trial when it could not be saved (<see cref="StartTrialAsync"/>), kept for this
    /// process: merged into every state a status reads, and saved by the next call that can.
    /// </summary>
    private UnsavedTrial? unsavedTrial;

    private sealed record UnsavedTrial(StatePatch Patch, Basis Basis);

    /// <summary>The stored state for a status, with a trial this process could not save; or null when it cannot be read just now.</summary>
    private async Task<StoredState?> LoadForStatusAsync(bool readOnly)
    {
        StoredState? state;
        try
        {
            state = await LoadAsync(readOnly).ConfigureAwait(false);
        }
        catch (Exception)
        {
            state = null;
        }
        var unsaved = Volatile.Read(ref unsavedTrial);
        if (state is null || unsaved is null) return state;
        // As a save would merge it: a newer trial answer stored since wins.
        return Offline.Merged(state, unsaved.Patch, unsaved.Basis);
    }

    /// <summary>Save the trial this process could not, and forget it once saved. Best-effort.</summary>
    private async Task SaveUnsavedTrialAsync()
    {
        var unsaved = Volatile.Read(ref unsavedTrial);
        if (unsaved is not null && await SaveBestEffortAsync(unsaved.Patch, null, unsaved.Basis).ConfigureAwait(false))
        {
            Interlocked.CompareExchange(ref unsavedTrial, null, unsaved);
        }
    }

    /// <summary><see cref="StartTrialAsync"/>'s trial ask, under the device read with an activation's time.</summary>
    private async Task<LicenseStatus> StartTrialNowAsync(StoredState state, CancellationToken cancellationToken)
    {
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        var (_, latest) = await AskTrialAsync(state, device, null, cancellationToken).ConfigureAwait(false);
        // The server's trial even when it could not be saved: kept for this process until a later call saves it.
        return await TrialStatusAsync(latest, EffectiveNow(state), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the server for this device's trial (with the earliest start the device holds as
    /// <c>clientStart</c>) and save its answer: <c>Reached</c> when the server answered at all,
    /// <c>Latest</c> the state with its trial merged in. Asked under <paramref name="device"/>, as the
    /// caller read it, and bounded by <paramref name="bound"/> (null: the configured HTTP timeout).
    /// </summary>
    private async Task<(bool Reached, StoredState Latest)> AskTrialAsync(
        StoredState state,
        Device device,
        TimeSpan? bound,
        CancellationToken cancellationToken)
    {
        // The stash is the app's own adapter (the Windows registry, say), and it can cost nothing: one
        // that cannot be read is no evidence, and StartTrialAsync still answers rather than throw.
        var regStart = await SettleAsync(() => registry.ReadTrialStartAsync(), null).ConfigureAwait(false);
        var clientStart = Wire.Earliest(state.TrialStart, regStart);
        var latest = state;
        var reached = false;

        try
        {
            // A device that cannot be told this time starts no trial: as offline.
            if (device.Own is not null)
            {
                // The answer is stamped with when it was asked for (guarded clock): an older one, from
                // another process, is not saved over it.
                var asked = new Stamp(EffectiveNow(state), state.TrialAt);
                var body = new JsonObject { ["fingerprint"] = device.Own };
                if (device.Kind is { } kind) body["fingerprintKind"] = Names.Of(kind);
                if (clientStart is { } start) body["clientStart"] = start;
                var response = await PostAsync("trial", body, bound, cancellationToken).ConfigureAwait(false);
                reached = true;
                var trialBody = response.Status == 200 ? Wire.ParseTrialBody(response.Json) : null;
                if (trialBody is not null)
                {
                    // The earliest start across all evidence: never moved forward.
                    var trialStart = Wire.Earliest(trialBody.TrialStart, state.TrialStart, regStart) ?? trialBody.TrialStart;
                    // Mirrored to the stash before the save; a stash that cannot be written does not
                    // throw the server's trial away with it.
                    await SettleAsync(
                        async () =>
                        {
                            await registry.WriteTrialStartAsync(trialStart).ConfigureAwait(false);
                            return (long?)trialStart;
                        },
                        null).ConfigureAwait(false);
                    // A trial lease this build cannot verify (signed with a key the product rotated to
                    // ahead of this build, say) never replaces one it can: a trial runs only on a valid
                    // lease, so taking it would end a running trial at its next sync. As a renewal keeps
                    // the lease it has when the answer's will not verify. An answer with no lease (the
                    // trial is over) is taken as it is.
                    var keeps = await KeepsItsTrialLeaseAsync(state, trialBody.Lease, cancellationToken).ConfigureAwait(false);
                    var trial = new StatePatch
                    {
                        [StateField.TrialStart] = trialStart,
                        [StateField.TrialEndsAt] = trialBody.TrialEndsAt,
                        [StateField.TrialLease] = keeps ? state.TrialLease : trialBody.Lease,
                    };
                    var basis = new Basis { Trial = asked };
                    latest = Offline.Merged(state, trial, basis);
                    var saved = await SaveBestEffortAsync(trial, null, basis).ConfigureAwait(false);
                    Volatile.Write(ref unsavedTrial, saved ? null : new UnsavedTrial(trial, basis));
                }
            }
        }
        catch (Exception)
        {
            // Offline, or the caller stopped waiting: fall back to whatever local trial evidence there is.
        }
        return (reached, latest);
    }

    /// <summary>
    /// Whether a trial answer's lease (<paramref name="answered"/>) fails here while the stored one
    /// verifies (<see cref="AskTrialAsync"/>): only when the answer has a lease and the device holds one.
    /// Who the device is was read for the ask, so neither check waits on it.
    /// </summary>
    private async Task<bool> KeepsItsTrialLeaseAsync(StoredState state, string? answered, CancellationToken cancellationToken)
    {
        if (answered is null || state.TrialLease is null) return false;
        var now = EffectiveNow(state);
        if ((await TrialLeaseAsync(answered, now, state.DeviceKind, cancellationToken).ConfigureAwait(false)).Valid) return false;
        return (await TrialLeaseAsync(state.TrialLease, now, state.DeviceKind, cancellationToken).ConfigureAwait(false)).Valid;
    }

    /// <summary>How a status may ask for the trial again (<see cref="TrialStatusAsync"/>): forced (a refresh), or read-only behind a call that has not settled.</summary>
    private readonly record struct TrialAsk(bool ForceOnline, bool ReadOnly);

    /// <summary>
    /// The trial's status (<see cref="Offline.TrialStatusOf"/>): live only on a valid trial lease, until
    /// the end it was signed to. With <paramref name="ask"/> (a status), a trial whose lease does not
    /// verify while its stored end is still ahead is asked for again first (<see cref="HealTrialAsync"/>),
    /// and a running one reports its launch when due (<see cref="ReportLaunchAsync"/>); neither read-only.
    /// </summary>
    private async Task<LicenseStatus> TrialStatusAsync(StoredState state, long now, CancellationToken cancellationToken, TrialAsk? ask = null)
    {
        var lease = !string.IsNullOrEmpty(state.TrialLease)
            ? await TrialLeaseAsync(state.TrialLease, now, state.DeviceKind, cancellationToken).ConfigureAwait(false)
            : TrialLease.NotValid;
        var status = Offline.TrialStatusOf(state, now, lease.Valid, lease.EndsAt, lease.Entitlements);
        if (ask is not { ReadOnly: false } asking) return status;
        if (status.State == LicenseState.Trial)
        {
            await ReportLaunchAsync(state, now).ConfigureAwait(false);
            return status;
        }
        var storedEndAhead = state.TrialEndsAt is { } end && now < end;
        if (lease.Valid || !storedEndAhead) return status;
        return await HealTrialAsync(state, asking.ForceOnline, cancellationToken).ConfigureAwait(false) ?? status;
    }

    /// <summary>
    /// The launch report in flight, if any (<see cref="ReportLaunchAsync"/>): a task that never fails,
    /// which the SDK itself never awaits. For the tests to wait for it; not public API.
    /// </summary>
    internal Task LaunchReportSettled => Volatile.Read(ref launchReport);

    private Task launchReport = Task.CompletedTask;

    /// <summary>
    /// A running trial's launch, reported so the server sees the trial come back (its funnel counts a
    /// trial seen a day after it started): with no key held, at most once a day
    /// (<see cref="Offline.TrialReportDue"/>). The stamp (<c>trialReportedAt</c>) is saved first, and
    /// nothing is sent when it cannot be, so "once a day" holds; then the report is sent on a thread-pool
    /// thread of its own, never awaited, so a launch never waits on it and never fails for it offline
    /// (<see cref="SendLaunchAsync"/>). Its answer is not read: a change to the trial reaches the device at
    /// <see cref="StartTrialAsync"/>.
    /// </summary>
    private async Task ReportLaunchAsync(StoredState state, long now)
    {
        if (!string.IsNullOrEmpty(state.Key) || !Offline.TrialReportDue(state, now)) return;
        if (!await SaveBestEffortAsync(new StatePatch { [StateField.TrialReportedAt] = now }).ConfigureAwait(false)) return;
        var report = Task.Run(async () =>
        {
            try
            {
                await SendLaunchAsync(state).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Offline, out of time, or an adapter that threw: a report is never a failure.
            }
        });
        Volatile.Write(ref launchReport, report);
    }

    /// <summary>
    /// The report itself: the device read for a status's time, then <c>trial</c> with the body
    /// <see cref="StartTrialAsync"/> sends (the stored start as <c>clientStart</c>, no stash read), bounded
    /// as a status check is. A device that cannot be told sends nothing.
    /// </summary>
    private async Task SendLaunchAsync(StoredState state)
    {
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, CancellationToken.None).ConfigureAwait(false);
        if (device.Own is null) return;
        var body = new JsonObject { ["fingerprint"] = device.Own };
        if (device.Kind is { } kind) body["fingerprintKind"] = Names.Of(kind);
        if (state.TrialStart is { } start) body["clientStart"] = start;
        await PostAsync("trial", body, TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A trial whose lease does not verify here while its stored end is still ahead: a lease signed with
    /// the product's key before it was rotated (on a build that ships the new one), one signed to end short
    /// of the trial's end, or one never saved whole. The stored end is unsigned, so it runs no trial on its
    /// own: the server is asked for the trial again instead, under the usual waits
    /// (<see cref="Offline.AskDue"/>) and bounded as a status check is, and its answer stands. An ask that
    /// brings back no trial this build can verify starts the wait (<see cref="Offline.RetryUnansweredMs"/>
    /// when nothing answered, <see cref="Offline.AskAgainMs"/> when the server did), so a device that is
    /// offline, or that carries a key the product no longer signs with, is not asked on every status. Null
    /// when it did not ask, or nothing answered.
    /// </summary>
    private async Task<LicenseStatus?> HealTrialAsync(StoredState state, bool forceOnline, CancellationToken cancellationToken)
    {
        var askedAt = clock.Now();
        if (!Offline.AskDue(state, askedAt, forceOnline)) return null;
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        var (reached, latest) = await AskTrialAsync(state, device, TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs), cancellationToken).ConfigureAwait(false);
        // The caller stopped waiting before anything answered: nothing is recorded of the ask it cut short.
        if (!reached && cancellationToken.IsCancellationRequested) return null;
        var after = reached ? await TrialStatusAsync(latest, EffectiveNow(latest), cancellationToken).ConfigureAwait(false) : null;
        if (after is { State: LicenseState.Trial }) return after;
        await SaveBestEffortAsync(new StatePatch
        {
            [StateField.BackoffSince] = askedAt,
            [StateField.BackoffMs] = reached ? Offline.AskAgainMs : Offline.RetryUnansweredMs,
        }).ConfigureAwait(false);
        return after;
    }

    /// <summary>
    /// <paramref name="work"/>'s answer, or <paramref name="fallback"/> when it throws (synchronously or
    /// not): for the app's own registry stash, which may cost the licence nothing.
    /// </summary>
    private static async Task<long?> SettleAsync(Func<Task<long?>> work, long? fallback)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
