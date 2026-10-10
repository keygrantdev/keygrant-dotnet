using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

/// <summary>
/// Client-side KeyGrant licensing for a desktop app. Verifies signed JWT leases OFFLINE and only contacts
/// the server to activate, to renew a lease nearing its end or past it, to tell it of a newer major, or to
/// report a running trial's launch (at most once a day), so an outage never locks out a paying customer while the lease is live (the grace
/// window is the lease's life). Licensing failure fails in favour of the paying customer: no network, a
/// 5xx, a 429, an ambiguous 4xx or an unusable answer never refuses a valid lease; only the server's
/// explicit verdicts do. Trial start is anti-reset (only moves earlier) and a monotonic clock guard stops a
/// clock put back from reviving an expired lease. A running trial reports its launch at most once a day, in
/// the background.
/// <para>
/// Calls on one instance run one at a time. Create ONE <see cref="License"/> per app process and share it.
/// The launch report runs on a thread-pool thread beside the calls, so the HTTP adapter and the
/// fingerprinter may be called from more than one thread at once.
/// </para>
/// </summary>
public sealed partial class License
{
    /// <summary>What <see cref="StatusAsync"/> answers when the stored state cannot be read just now.</summary>
    private static readonly LicenseStatus Unreadable = new() { State = LicenseState.Unknown, Reason = LicenseReason.Storage };

    private static readonly LicenseStatus Unlicensed = new() { State = LicenseState.Unlicensed };

    private readonly string? deviceName;

    /// <summary>Set up licensing for one product.</summary>
    /// <param name="config">The product, the API, its public key and this build's major.</param>
    /// <exception cref="ArgumentException">The configuration is incomplete, or the public key is not an Ed25519 public JWK.</exception>
    public License(LicenseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(config.Product, nameof(config.Product));
        ArgumentException.ThrowIfNullOrEmpty(config.ApiBaseUrl, nameof(config.ApiBaseUrl));
        // Every request is bounded (a day at most: timers take no longer), never left open.
        if (config.HttpTimeout <= TimeSpan.Zero || config.HttpTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.HttpTimeout, "HttpTimeout must be positive, and at most a day");
        }

        product = config.Product;
        // One trailing slash, as the reference strips it.
        baseUrl = config.ApiBaseUrl.EndsWith('/') ? config.ApiBaseUrl[..^1] : config.ApiBaseUrl;
        // As the server reads it (Offline.MajorOf): one reading, used wherever the major is.
        major = Offline.MajorOf(config.Major);
        httpTimeout = config.HttpTimeout;
        deviceHold = config.DeviceHold;
        time = config.TimeProvider ?? TimeProvider.System;
        verifier = new LeaseVerifier(KeySetOf(config).Select(jwk => jwk.KeyBytes()));
        // TRUNCATED, because the integrating app supplies it: the activate endpoint bounds every
        // string it takes, and a long name would turn a first activation into a 400.
        deviceName = Wire.TrimToLength(config.DeviceName, Wire.MaxDeviceName);

        var adapters = config.Adapters ?? new LicenseAdapters();
        storage = adapters.Storage ?? new FileStorage(FileStorage.DefaultPath(config.Product));
        registry = adapters.Registry ?? NoopRegistryStash.Instance;
        clock = adapters.Clock ?? SystemClock.Instance;
        http = adapters.Http ?? new HttpClientAdapter();
        deviceReader = new DeviceIdentityReader(adapters.Fingerprint ?? new MachineFingerprinter(null, time), time);
    }

    /// <summary>
    /// Activate a licence key on this device. The key is normalised (trimmed, upper case) before it is
    /// sent and stored. Throws when the stored state cannot be read, or the activation cannot be saved
    /// on this device: the customer asked for it and can try again.
    /// </summary>
    /// <param name="key">The key as the customer entered it.</param>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call waits its turn, or before its request is answered (nothing is saved).</param>
    /// <returns>Activated, or why not.</returns>
    public Task<ActivateResult> ActivateAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Behind a call that has not settled, nothing may be written: answered as not reaching the server, to retry.
        return Serial(
            readOnly => readOnly ? Task.FromResult(ActivateResult.Failure(LicenseError.Network)) : ActivateNowAsync(key, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// <paramref name="paymentLinkUrl"/> with this device's ACTIVATION id as its one
    /// <c>client_reference_id</c>, or null when the device holds no activated licence. Send a customer
    /// there to buy an upgrade for the key they hold. The activation id, never the licence key: a URL
    /// lands in browser history and logs, and the activation id grants nothing on its own. The link's
    /// own parameters and fragment are kept as written.
    /// </summary>
    /// <param name="paymentLinkUrl">The upgrade offer's payment link.</param>
    /// <returns>The link for this device, or null.</returns>
    /// <exception cref="ArgumentException">The device holds an activation and the link is not an absolute URL.</exception>
    public async Task<string?> UpgradeUrlAsync(string paymentLinkUrl)
    {
        ArgumentNullException.ThrowIfNull(paymentLinkUrl);
        // Read-only: this runs outside the one-at-a-time order, beside any call.
        var state = await LoadAsync(readOnly: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(state.Key) || string.IsNullOrEmpty(state.ActivationId)) return null;
        return Links.ReferencedLink(paymentLinkUrl, state.ActivationId);
    }

    /// <summary>
    /// Release this device's activation and drop the local licence, keeping only trial evidence, and
    /// say whether the seat is now free (<see cref="DeactivateResult.Released"/>): only a 200 from
    /// KeyGrant (or no activation held) says so; the local licence is dropped whatever the server
    /// answered. Never releases another device's seat: an activation stored here that is another
    /// device's (a state file copied or restored from another PC, its lease held to that machine, or a
    /// device refusal standing for this one) is only dropped here, with no request, and answers
    /// released, as this device holds no seat. Throws, releasing nothing, when a call before it has not
    /// settled (<see cref="InvalidOperationException"/>), or when the state cannot be read or saved.
    /// </summary>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call waits its turn, or before its request is answered (nothing is saved).</param>
    /// <returns>Whether the seat was released, once the local licence is dropped.</returns>
    public Task<DeactivateResult> DeactivateAsync(CancellationToken cancellationToken = default) =>
        Serial(
            readOnly => readOnly
                ? throw new InvalidOperationException("a previous licence call has not finished; try again")
                : DeactivateNowAsync(cancellationToken),
            cancellationToken);

    /// <summary>Re-check licensing: asks the server at once, whatever the waits say.</summary>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call still waits its turn; once it runs, stops waiting on the server or the device and answers from what the device holds (nothing is recorded of an ask it cut short). Never turns an answer into an exception.</param>
    /// <returns>The status.</returns>
    public Task<LicenseStatus> RefreshAsync(CancellationToken cancellationToken = default) => StatusAsync(true, cancellationToken);

    /// <summary>
    /// The licence's status. Offline-first: it answers from what the device holds and asks the server only
    /// when it should (a lease near or past its end, a newer major on an open lease, a stored refusal to
    /// heal), once the wait after the last ask is over. A running trial reports its launch at most once a day, in the background and never awaited:
    /// the app need not call <see cref="StartTrialAsync"/> on every launch. Never throws for storage: a
    /// state that cannot be read just now is <see cref="LicenseState.Unknown"/> (reason
    /// <see cref="LicenseReason.Storage"/>), and a save that fails leaves the answer standing. An app
    /// awaits it at launch.
    /// </summary>
    /// <param name="forceOnline">Ask the server at once (<see cref="RefreshAsync"/>).</param>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call still waits its turn; once it runs, stops waiting on the server or the device and answers from what the device holds (nothing is recorded of an ask it cut short). Never turns an answer into an exception.</param>
    /// <returns>The status.</returns>
    public Task<LicenseStatus> StatusAsync(bool forceOnline = false, CancellationToken cancellationToken = default) =>
        Serial(
            async readOnly =>
            {
                if (!readOnly) await SaveUnsavedTrialAsync().ConfigureAwait(false);
                var state = await LoadForStatusAsync(readOnly).ConfigureAwait(false);
                var status = state is null ? Unreadable : await StatusNowAsync(state, forceOnline, readOnly, cancellationToken).ConfigureAwait(false);
                // Answered from storage behind a call that has not finished: say so.
                return readOnly ? status with { Stalled = true } : status;
            },
            cancellationToken);

    /// <summary>
    /// Begin (or re-sync) a trial for this device. As <see cref="StatusAsync"/> for storage: unknown
    /// when the state cannot be read, and the server's trial when it cannot be saved. Offline, it
    /// answers what local trial evidence there is.
    /// </summary>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call still waits its turn; once it runs, stops waiting on the server or the device and answers from what the device holds (nothing is recorded of an ask it cut short). Never turns an answer into an exception.</param>
    /// <returns>The trial's status.</returns>
    public Task<LicenseStatus> StartTrialAsync(CancellationToken cancellationToken = default) =>
        Serial(
            async readOnly =>
            {
                var state = await LoadForStatusAsync(readOnly).ConfigureAwait(false);
                if (state is null) return readOnly ? Unreadable with { Stalled = true } : Unreadable;
                if (!readOnly) return await StartTrialNowAsync(state, cancellationToken).ConfigureAwait(false);
                // From storage behind a call that has not finished, and saying so.
                return (await TrialStatusAsync(state, EffectiveNow(state), cancellationToken).ConfigureAwait(false)) with { Stalled = true };
            },
            cancellationToken);

    private async Task<ActivateResult> ActivateNowAsync(string key, CancellationToken cancellationToken)
    {
        // Normalised, and refused here when it cannot be a key.
        var entered = Wire.EnteredKey(key);
        if (entered is null) return ActivateResult.Failure(LicenseError.InvalidKey);
        // Install memory protects the seat this install holds: re-activating that key keeps to it; a
        // first activation, or another key's, starts afresh.
        var stored = await LoadAsync().ConfigureAwait(false);
        var sameSeat = stored.Key is not null && Offline.SameKey(stored.Key, entered);
        var installKind = sameSeat ? InstallKind(stored.DeviceKind) : null;
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs), installKind, cancellationToken).ConfigureAwait(false);
        // Cancelled before anything was asked: nothing to answer the customer with but that.
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Devices.ActivationIdentity(device, installKind);
        if (identity is null) return ActivateResult.Failure(LicenseError.Network);
        var (fingerprint, kind) = identity.Value;
        // The answer is stamped with when it was asked for (guarded clock): an older ask's answer,
        // from another process, is not saved over it.
        var askedAt = EffectiveNow(stored);
        HttpResult response;
        try
        {
            var body = new JsonObject { ["key"] = entered, ["fingerprint"] = fingerprint };
            if (kind is { } k) body["fingerprintKind"] = Names.Of(k);
            if (deviceName is not null) body["name"] = deviceName;
            body["major"] = major;
            body["kids"] = KidsJson();
            response = await PostAsync("activate", body, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ActivateResult.Failure(LicenseError.Network);
        }
        if (response.Status != 200) return ActivateResult.Failure(Wire.MapServerError(Wire.ErrorOf(response.Json)));

        var (lease, activationId) = Wire.ParseLeaseBody(response.Json);
        if (string.IsNullOrEmpty(lease) || string.IsNullOrEmpty(activationId)) return ActivateResult.Failure(LicenseError.Network);
        var now = await ServerNowAsync(lease, entered, activationId, bestEffort: false).ConfigureAwait(false);
        var evaluated = await EvaluateAsync(lease, now, entered, activationId, kind, cancellationToken).ConfigureAwait(false);
        if (!evaluated.Ok) return ActivateResult.Failure(LicenseError.InvalidLease);

        var patch = Offline.ActivationPatch(entered, activationId, lease, evaluated.Claims!, now, major, kind);
        await SaveAsync(patch, null, new Basis { Activation = new Asked(askedAt, activationId, stored.VerdictAt) }).ConfigureAwait(false);
        return ActivateResult.Success;
    }

    private async Task<DeactivateResult> DeactivateNowAsync(CancellationToken cancellationToken)
    {
        var state = await LoadAsync().ConfigureAwait(false);
        // Nothing held is nothing to release: the seat is as free as it gets. Neither is a seat another
        // device holds this one's to release (a state file copied or restored from another PC): it is
        // only dropped here, and this device, which holds no seat, answers released.
        var released = true;
        if (!string.IsNullOrEmpty(state.Key) && !string.IsNullOrEmpty(state.ActivationId))
        {
            var elsewhere = await SeatElsewhereAsync(state, cancellationToken).ConfigureAwait(false);
            // Cancelled while who this device is was read, before anything was asked: nothing is dropped.
            cancellationToken.ThrowIfCancellationRequested();
            if (!elsewhere)
            {
                try
                {
                    // Only a 200 is KeyGrant saying the seat is free. A 5xx, a 429 or a proxy's page is an
                    // answer, but not that one.
                    var response = await PostAsync("deactivate", new JsonObject { ["key"] = state.Key, ["activationId"] = state.ActivationId }, null, cancellationToken)
                        .ConfigureAwait(false);
                    released = response.Status == 200;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Best-effort; still cleared locally.
                    released = false;
                }
            }
        }
        // Stamped, so that an answer to an older ask is not saved over it.
        var patch = Offline.Unkeyed;
        patch.Set(StateField.VerdictAt, EffectiveNow(state));
        await SaveAsync(patch).ConfigureAwait(false);
        return new DeactivateResult(released);
    }

    /// <summary>
    /// Whether the stored activation is another device's: a device refusal that stands for this one, or
    /// a lease held to a machine id this device does not have, expired or not
    /// (<see cref="Offline.HeldElsewhere"/>). A state file copied or restored from another PC carries
    /// that PC's activation; releasing it would lock that PC out at its next check-in.
    /// </summary>
    private async Task<bool> SeatElsewhereAsync(StoredState state, CancellationToken cancellationToken)
    {
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        if (Offline.StandingRefusal(state, major, device.OwnClaim, device.CanTell) == Refusal.Device) return true;
        return !string.IsNullOrEmpty(state.Lease) && Offline.HeldElsewhere(Verify(state.Lease, EffectiveNow(state)), device.Claims, device.CanTell);
    }

    /// <summary>
    /// The status, asking the server when it should. <paramref name="readOnly"/> (behind a call that has
    /// not settled): from what is stored only, with no ask and no write.
    /// </summary>
    private async Task<LicenseStatus> StatusNowAsync(StoredState state, bool forceOnline, bool readOnly, CancellationToken cancellationToken)
    {
        var now = EffectiveNow(state);
        // A device refusal stands only for the device it was given for: who this is.
        var device = state.Refused == Refusal.Device
            ? await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false)
            : null;
        var refused = !string.IsNullOrEmpty(state.Key) ? Offline.StandingRefusal(state, major, device?.OwnClaim, device?.CanTell ?? false) : null;
        if (refused is { } standing)
        {
            if (readOnly) return Invalid(Names.ReasonOf(standing), state.Key);
            return await RefusedStatusAsync(state, standing, forceOnline, cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(state.Key) && !string.IsNullOrEmpty(state.Lease))
        {
            return await LicenseStatusAsync(state, state.Lease, now, forceOnline, readOnly, cancellationToken).ConfigureAwait(false);
        }
        if (state.TrialStart is not null)
        {
            return await TrialStatusAsync(state, now, cancellationToken, new TrialAsk(forceOnline, readOnly)).ConfigureAwait(false);
        }
        return Unlicensed;
    }

    /// <summary>
    /// A refusal that stands for this build: answered as stored, and asked again once the wait after
    /// the last ask is over, or when forced, so a renewed or reinstated key heals.
    /// </summary>
    private async Task<LicenseStatus> RefusedStatusAsync(StoredState state, Refusal refused, bool force, CancellationToken cancellationToken)
    {
        var stored = Invalid(Names.ReasonOf(refused), state.Key);
        if (!Offline.AskDue(state, clock.Now(), force)) return stored;
        return await TryRenewAsync(state, null, cancellationToken).ConfigureAwait(false) ?? stored;
    }

    private async Task<LicenseStatus> LicenseStatusAsync(
        StoredState state,
        string lease,
        long now,
        bool forceOnline,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        // Not this licence (`tampered`), or not this device (`device`): the server is asked under the usual waits, below.
        var evaluated = await EvaluateAsync(lease, now, state.Key, state.ActivationId, state.DeviceKind, cancellationToken).ConfigureAwait(false);
        // What the lease says offline, entitlements included: the answer whenever the server gives no verdict.
        var offline = Offline.LeaseStatus(evaluated, state.Key);
        if (evaluated.Ok)
        {
            if (readOnly) return offline;
            var held = await NoteKeptAsync(state, evaluated.Claims!, now).ConfigureAwait(false);
            if (forceOnline) return await TryRenewAsync(held, null, cancellationToken).ConfigureAwait(false) ?? offline;
            // Asked, bounded in time, when the server should hear from this device before the lease
            // runs out: a newer major than it last heard, on an open lease; or a lease nearing its end.
            // The valid lease is the answer when the ask gets no verdict.
            if (!Renewal.AsksOnValidLease(evaluated.Claims!, held, major, now, clock.Now())) return offline;
            return await TryRenewAsync(held, TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs), cancellationToken).ConfigureAwait(false) ?? offline;
        }
        // Locally invalid: give the server a chance to heal it before locking the customer out, once
        // the wait after the last ask is over, or when forced. Only if the server is unreachable (or
        // agrees) is the local reason the answer.
        var renewed = !readOnly && Offline.AskDue(state, clock.Now(), forceOnline)
            ? await TryRenewAsync(state, null, cancellationToken).ConfigureAwait(false)
            : null;
        return renewed ?? offline;
    }

    /// <summary>The state with this run noted in its kept major, persisted when that changed.</summary>
    private async Task<StoredState> NoteKeptAsync(StoredState state, LeaseClaims claims, long now)
    {
        var keptMajor = Offline.KeptWith(state.KeptMajor, claims, now, major);
        if (keptMajor == state.KeptMajor) return state;
        // About this activation: not saved onto another one stored meanwhile.
        await SaveBestEffortAsync(new StatePatch { [StateField.KeptMajor] = keptMajor }, null, new Basis { About = new About(state.ActivationId) })
            .ConfigureAwait(false);
        return state with { KeptMajor = keptMajor };
    }
}
