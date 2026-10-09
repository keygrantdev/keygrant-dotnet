using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// The machinery under License: the adapters, one call at a time, every request bounded, the stored state
// and its clock guard, and the online validate with what its answer does to the stored state. The public
// half (License.cs) decides when to go online and what to answer.
public sealed partial class License
{
    /// <summary>How long past its HTTP bound a call may hold the next one waiting: room for the storage reads and writes around its one request.</summary>
    internal static readonly TimeSpan LockGrace = TimeSpan.FromSeconds(15);

    private readonly string product;
    private readonly int major;
    private readonly string baseUrl;
    private readonly TimeSpan httpTimeout;
    private readonly bool deviceHold;
    private readonly IStorageAdapter storage;
    private readonly IRegistryStash registry;
    private readonly IClock clock;
    private readonly IHttpAdapter http;
    private readonly TimeProvider time;
    private readonly LeaseVerifier verifier;
    private readonly DeviceIdentityReader deviceReader;

    private readonly object gate = new();

    /// <summary>The last call queued: when it started running, and when it settled (<see cref="Serial{T}"/>).</summary>
    private Slot last = Slot.Done();

    /// <summary>Calls running with writes allowed that have not settled yet (<see cref="Serial{T}"/>).</summary>
    private int writing;

    /// <summary>A queued call's place: its grace once it starts, and its end.</summary>
    private sealed class Slot
    {
        public readonly TaskCompletionSource<Task> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Slot Done()
        {
            var slot = new Slot();
            slot.Started.SetResult(Task.CompletedTask);
            slot.Settled.SetResult();
            return slot;
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> once every call before it on this instance has finished, so each starts
    /// from what the last one left and two overlapping checks ask the server once. A call waits for the
    /// one before it to settle, but not past the HTTP bound plus <see cref="LockGrace"/> from when that
    /// one STARTED running. Storage, fingerprint and registry adapters are not bounded (a storage write
    /// cut short could be left half-written), so a call stuck on one is left to finish on its own, and
    /// the calls behind it go ahead READ-ONLY (<c>readOnly</c> true): they answer from what is stored,
    /// with no ask and no write, for as long as any call before them is still unsettled. A call that may
    /// make a second, shorter request (a trial start: a claim, then its trial ask) holds the calls behind
    /// it <paramref name="extra"/> longer.
    /// </summary>
    private Task<T> Serial<T>(Func<bool, Task<T>> work, CancellationToken cancellationToken, TimeSpan extra = default)
    {
        var slot = new Slot();
        Slot previous;
        lock (gate)
        {
            previous = last;
            last = slot;
        }
        return RunInTurnAsync(previous, slot, work, extra, cancellationToken);
    }

    private async Task<T> RunInTurnAsync<T>(Slot previous, Slot slot, Func<bool, Task<T>> work, TimeSpan extra, CancellationToken cancellationToken)
    {
        try
        {
            // The grace runs from when the call before started, however late this one was queued.
            var graceOver = await previous.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAny(previous.Settled.Task, graceOver).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never started: stand aside, so the call behind waits for the one before as if this one
            // had never been queued.
            _ = previous.Started.Task.ContinueWith(t => slot.Started.TrySetResult(t.Result), TaskScheduler.Default);
            _ = previous.Settled.Task.ContinueWith(_ => slot.Settled.TrySetResult(), TaskScheduler.Default);
            throw;
        }

        bool readOnly;
        lock (gate)
        {
            readOnly = writing > 0;
            if (!readOnly) writing += 1;
        }
        var grace = new GraceTimer(httpTimeout + LockGrace + extra, time);
        slot.Started.SetResult(grace.Over);
        try
        {
            return await Tasks.Start(() => work(readOnly)).ConfigureAwait(false);
        }
        finally
        {
            if (!readOnly)
            {
                lock (gate) writing -= 1;
            }
            grace.Cancel();
            slot.Settled.TrySetResult();
        }
    }

    /// <summary>
    /// POST to the product's endpoint, ALWAYS bounded: by <paramref name="bound"/>, else by the
    /// configured HTTP timeout. The bound is passed to the adapter and also kept by the SDK itself.
    /// </summary>
    private async Task<HttpResult> PostAsync(string action, JsonObject body, TimeSpan? bound, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/v1/products/{product}/{action}";
        var limit = bound ?? httpTimeout;
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var call = Tasks.Start(() => http.PostAsync(url, body, limit, abandon.Token));
        HttpResult result;
        try
        {
            result = await Tasks.Bounded(call, limit, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!call.IsCompleted)
        {
            // Abandoned (its bound passed, or the caller stopped waiting): an adapter still at it is told
            // to stop. One that answered never is: its answer stands, whatever its token's callbacks do.
            Tasks.TellToStop(abandon);
            throw;
        }
        // Whatever the adapter built the body with, read as JSON.parse reads it: a node that throws on
        // first use (a repeated name) must not fail a status.
        return result with { Json = Json.Lenient(result.Json) };
    }

    private VerifyResult Verify(string lease, long now) => verifier.Verify(lease, now);

    /// <summary>A licence lease verified offline and held to this product and build, and to the key and activation it is for, on this device.</summary>
    private async Task<LeaseCheck> EvaluateAsync(string lease, long now, string? key, string? activationId, DeviceKind? deviceKind, CancellationToken cancellationToken)
    {
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), deviceKind, cancellationToken).ConfigureAwait(false);
        var holder = new LeaseHolder(key ?? "", activationId ?? "", device.Claims, device.CanTell);
        return Offline.CheckLease(Verify(lease, now), product, major, holder);
    }

    /// <summary>
    /// A trial lease verified offline, for this product and this device, with the end it was signed to
    /// (ms) and its entitlements when it is valid (<see cref="Offline.TrialLeaseOf"/>).
    /// </summary>
    private async Task<TrialLease> TrialLeaseAsync(string lease, long now, DeviceKind? installKind, CancellationToken cancellationToken)
    {
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), installKind, cancellationToken).ConfigureAwait(false);
        return Offline.TrialLeaseOf(Verify(lease, now), product, device.Claims, device.CanTell);
    }

    /// <summary>
    /// Who this device is (<see cref="Devices.From"/>), read for at most about <paramref name="timeout"/>,
    /// on an install keyed as <paramref name="installKind"/>, held to its device unless <c>DeviceHold</c>
    /// is off.
    /// </summary>
    private Task<Device> DeviceAsync(TimeSpan timeout, DeviceKind? installKind, CancellationToken cancellationToken) =>
        deviceReader.ReadAsync(timeout, installKind, deviceHold, cancellationToken);

    /// <summary>The install's kind as it bears on this device: none when nothing is held.</summary>
    private DeviceKind? InstallKind(DeviceKind? stored) => deviceHold ? stored : null;

    /// <summary>The stored state; <paramref name="readOnly"/> behind a call that has not finished, so the read writes nothing either.</summary>
    private async Task<StoredState> LoadAsync(bool readOnly = false) =>
        await storage.ReadAsync(readOnly).ConfigureAwait(false) ?? new StoredState();

    /// <summary>
    /// Persist a patch onto the state as it is stored NOW, not onto a snapshot a call took earlier,
    /// and advance the monotonic clock guard, unless the guard is being brought back on the server's
    /// evidence (<paramref name="guard"/>). What the patch is (<paramref name="basis"/>) decides what of
    /// it lands on a state another process changed meanwhile.
    /// </summary>
    private async Task SaveAsync(StatePatch patch, long? guard = null, Basis? basis = null)
    {
        StoredState Merge(StoredState latest) =>
            StateFields.Set(Offline.Merged(latest, patch, basis), StateField.LastSeen, Offline.SavedLastSeen(latest.LastSeen, clock.Now(), guard));

        if (await storage.UpdateAsync(Merge).ConfigureAwait(false)) return;
        await storage.WriteAsync(Merge(await LoadAsync().ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>A save whose failure fails nothing: the answer stands. Whether it was saved.</summary>
    private async Task<bool> SaveBestEffortAsync(StatePatch patch, long? guard = null, Basis? basis = null)
    {
        try
        {
            await SaveAsync(patch, guard, basis).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Never earlier than the highest wall-clock ever seen (anti clock-rollback).</summary>
    private long EffectiveNow(StoredState state) => Offline.GuardedNow(clock.Now(), state.LastSeen);

    /// <summary>
    /// Now for judging a lease the server has just signed, with the clock guard reconciled to it: when
    /// the guard is more than <see cref="Offline.ClockToleranceMs"/> past its signing time, the guard
    /// comes back to the device clock, never below the signing time. Only a lease signed for this key
    /// and this activation, no older than the one the device holds, moves it.
    /// </summary>
    private async Task<long> ServerNowAsync(string lease, string key, string activationId, bool bestEffort)
    {
        var stored = await LoadAsync().ConfigureAwait(false);
        var now = EffectiveNow(stored);
        var claims = Verify(lease, now).Claims;
        var heldIat = !string.IsNullOrEmpty(stored.Lease) ? Verify(stored.Lease, now).Claims?.Iat : null;
        if (Offline.ClockCorrection(clock.Now(), stored.LastSeen, SignedAt.Of(claims), heldIat, key, activationId) is not { } corrected) return now;
        if (bestEffort) await SaveBestEffortAsync(new StatePatch(), corrected).ConfigureAwait(false);
        else await SaveAsync(new StatePatch(), corrected).ConfigureAwait(false);
        return corrected;
    }

    /// <summary>An online validate: a status on a definitive answer, else null.</summary>
    private async Task<LicenseStatus?> TryRenewAsync(StoredState state, TimeSpan? bound, CancellationToken cancellationToken)
    {
        var (answered, status) = await RenewAsync(state, bound, cancellationToken).ConfigureAwait(false);
        return answered ? status : null;
    }

    /// <summary>
    /// An online validate, and whether the server gave a verdict: what its answer saves and says is
    /// <see cref="Renewal.Outcome"/>'s, saved as the answer to this ask (a save that fails leaves the
    /// answer standing). A caller who stops waiting gets no answer, and nothing is recorded of the ask.
    /// </summary>
    private async Task<(bool Answered, LicenseStatus? Status)> RenewAsync(StoredState state, TimeSpan? bound, CancellationToken cancellationToken)
    {
        // The wait runs on the device clock, as AskDue reads it; the answer is stamped by the guarded
        // one, which a clock put back does not move back, so an answer asked for later still reads as
        // later to another process.
        var askedAt = clock.Now();
        var asked = new Asked(EffectiveNow(state), state.ActivationId, state.VerdictAt);
        // A machine id read here keys this install from now on, written with whatever this ask writes.
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        HttpResult? response;
        try
        {
            response = await PostAsync("validate", ValidateBody(state, device), bound, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            response = null;
        }
        catch (Exception)
        {
            // The caller stopped waiting: no answer, and no evidence of anything (no wait recorded).
            // The status falls back to what the device holds, as for no answer: never an exception.
            return (false, null);
        }
        var (check, now) = await JudgeFreshAsync(state, response, cancellationToken).ConfigureAwait(false);
        var outcome = Renewal.Outcome(new RenewalInput(response, check, askedAt, now, major, state.Key, state.KeptMajor, device));
        await SaveBestEffortAsync(outcome.Patch, null, new Basis { Answer = asked }).ConfigureAwait(false);
        return outcome.Answered ? (true, outcome.Status) : (false, null);
    }

    /// <summary>
    /// The lease a validate's 200 carried, checked on this device as of the server's time (the clock
    /// guard brought back best-effort: the server's time even when the state cannot be read or saved
    /// just now); no check for any other answer. With the time it was judged at.
    /// </summary>
    private async Task<(LeaseCheck? Check, long Now)> JudgeFreshAsync(StoredState state, HttpResult? response, CancellationToken cancellationToken)
    {
        var lease = response?.Status == 200 ? Wire.ParseLeaseBody(response.Json).Lease : null;
        if (string.IsNullOrEmpty(lease)) return (null, EffectiveNow(state));
        long now;
        try
        {
            now = await ServerNowAsync(lease, state.Key ?? "", state.ActivationId ?? "", bestEffort: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            now = EffectiveNow(state);
        }
        return (await EvaluateAsync(lease, now, state.Key, state.ActivationId, state.DeviceKind, cancellationToken).ConfigureAwait(false), now);
    }

    /// <summary>
    /// A validate's body: with every fingerprint this device answers to when it can tell, so the server
    /// refuses a licence file copied from another machine; with none when it cannot, so a slow read never
    /// costs an honest device its renewal.
    /// </summary>
    private JsonObject ValidateBody(StoredState state, Device device)
    {
        var body = new JsonObject();
        if (state.Key is not null) body["key"] = state.Key;
        if (state.ActivationId is not null) body["activationId"] = state.ActivationId;
        body["major"] = major;
        if (state.KeptMajor is { } kept) body["keptMajor"] = kept;
        if (device.CanTell) body["fingerprints"] = new JsonArray(device.Fingerprints.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        if (device.CanTell && device.Kind is { } kind) body["fingerprintKind"] = Names.Of(kind);
        return body;
    }

    private static LicenseStatus Invalid(LicenseReason reason, string? key) => Wire.InvalidStatus(reason, key);
}
