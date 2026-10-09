using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// Buying from the app with no key typed, under License: the purchase link (PurchaseLinkAsync, which keeps
// this install's one-time secret and hands out the reference to it) and the pickup (PickUpAsync): while no
// key is held and a secret is, asking the server for the key bought with it (the claim) and saving it
// exactly as an activation saves one. What each answer does is Internal/Pickup.cs; License.cs decides when
// to ask.
public sealed partial class License
{
    /// <summary>
    /// How a pickup asks: for a trial start (<c>Start</c>: always, under the device the trial start read
    /// for both its asks, <c>Device</c>, bounded as a status check is, a host-name fallback allowed), or
    /// for a status (when <c>Force</c>d, under the HTTP bound, or when <see cref="Offline.ClaimDue"/>,
    /// bounded as a status check is), reading the device as a status does and asking only under its own
    /// fingerprint.
    /// </summary>
    private readonly record struct PickupAsk(bool Start, bool Force, Device? Device = null);

    /// <summary>What a pickup did: the licensed status when a bought key licensed this device, else the state to answer from.</summary>
    private sealed record PickedUp(LicenseStatus? Status, StoredState State);

    /// <summary>Where a new purchase secret's bytes come from: the platform's CSPRNG (<see cref="Pickup.Csprng"/>). Tests only replace it.</summary>
    internal Func<int, byte[]> SecretBytes { get; set; } = Pickup.Csprng;

    /// <summary>
    /// <see cref="PurchaseUrlAsync"/>'s answer: null while a key is held; else the link carrying the
    /// reference to the held secret, or to a new one, saved with when its link was handed out (the claim's
    /// wait cleared, so the next status asks). Read-only (behind a call that has not settled), a held
    /// secret's link is answered as it is and none held throws: a secret that is not saved could never be
    /// picked up.
    /// </summary>
    private async Task<string?> PurchaseLinkAsync(string link, bool readOnly)
    {
        var state = await LoadAsync(readOnly).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(state.Key)) return null;
        var now = EffectiveNow(state);
        var held = Offline.PurchaseTokenKept(state, now) ? state.PurchaseToken : null;
        if (readOnly)
        {
            return held is not null
                ? Pickup.PurchaseLink(link, held)
                : throw new InvalidOperationException("a previous licence call has not finished; try again");
        }
        var secret = held ?? Pickup.NewPurchaseSecret(SecretBytes);
        // Unbased: it lands as patched.
        await SaveAsync(new StatePatch
        {
            [StateField.PurchaseToken] = secret,
            [StateField.PurchaseTokenAt] = now,
            [StateField.ClaimAskedAt] = null,
        }).ConfigureAwait(false);
        return Pickup.PurchaseLink(link, secret);
    }

    /// <summary>
    /// The key bought through <see cref="PurchaseUrlAsync"/>, picked up: with no key held and a secret
    /// held, the secret is dropped once past its bound (<see cref="Offline.PurchaseTokenKept"/>), and
    /// otherwise the claim is asked as <paramref name="ask"/> says. Never asked behind a call that has not
    /// settled: the caller skips it.
    /// </summary>
    private async Task<PickedUp> PickUpAsync(StoredState state, PickupAsk ask, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(state.Key) || state.PurchaseToken is not { } secret) return new PickedUp(null, state);
        var now = EffectiveNow(state);
        if (!Offline.PurchaseTokenKept(state, now)) return new PickedUp(null, await DropSecretAsync(state, secret).ConfigureAwait(false));
        if (!ask.Start && !ask.Force && !Offline.ClaimDue(state, clock.Now(), now)) return new PickedUp(null, state);
        return await ClaimAsync(state, secret, ask, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The secret dropped (saved about it, best-effort): the state without it.</summary>
    private async Task<StoredState> DropSecretAsync(StoredState state, string secret)
    {
        var basis = new Basis { Purchase = new AboutToken(secret) };
        await SaveBestEffortAsync(Offline.NoPurchase, null, basis).ConfigureAwait(false);
        return Offline.Merged(state, Offline.NoPurchase, basis);
    }

    /// <summary>
    /// Ask for the key bought with <paramref name="secret"/> (the claim) and save what the answer does
    /// (<see cref="Pickup.ClaimOutcome"/>): a bought key as an activation, a wait or a drop about the
    /// secret. The body is <see cref="Pickup.ClaimRequest"/>'s: from a status, a device whose own
    /// fingerprint did not read is not asked at all, and nothing is saved, so the next status asks again;
    /// at a trial start, a device with no fingerprint at all is no answer. Saved best-effort: a bought key the disk refused is
    /// answered all the same, and the next claim answers it again (the server knows this device by any of
    /// the fingerprints it sends). A caller who stops waiting gets no answer, and nothing is recorded.
    /// </summary>
    private async Task<PickedUp> ClaimAsync(StoredState state, string secret, PickupAsk ask, CancellationToken cancellationToken)
    {
        var clockNow = clock.Now();
        var askedAt = EffectiveNow(state);
        var device = ask.Device ?? await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        var request = Pickup.ClaimRequest(secret, device, deviceName, major, ask.Start);
        if (request is null && !ask.Start) return new PickedUp(null, state);
        HttpResult? response = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bound = ask.Force && !ask.Start ? (TimeSpan?)null : TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs);
            if (request is not null)
            {
                var body = request.ToJson();
                body["kids"] = KidsJson();
                response = await PostAsync("claim", body, bound, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No answer: the secret is kept, and the wait started.
        }
        catch (Exception)
        {
            // The caller stopped waiting: no answer, and nothing recorded. Answered as with no secret.
            return new PickedUp(null, state);
        }
        var (check, now) = await JudgeClaimAsync(response, request?.FingerprintKind, askedAt, cancellationToken).ConfigureAwait(false);
        var outcome = Pickup.ClaimOutcome(new ClaimInput(response, check, clockNow, now, major, request?.FingerprintKind));
        if (outcome.Result == ClaimResult.Bought)
        {
            var activationId = (string?)outcome.Patch.ValueOf(StateField.ActivationId);
            await SaveBestEffortAsync(outcome.Patch, null, new Basis { Activation = new Asked(askedAt, activationId, state.VerdictAt) }).ConfigureAwait(false);
            return new PickedUp(outcome.Status, state);
        }
        // A wait is about the secret as its link was handed out when asked; a drop about the secret alone.
        var about = outcome.Result == ClaimResult.Kept ? new AboutToken(secret, state.PurchaseTokenAt) : new AboutToken(secret);
        var basis = new Basis { Purchase = about };
        await SaveBestEffortAsync(outcome.Patch, null, basis).ConfigureAwait(false);
        return new PickedUp(null, Offline.Merged(state, outcome.Patch, basis));
    }

    /// <summary>
    /// The lease a claim's 200 carried with a key and an activation id, checked for them on this device
    /// as of the server's time (the clock guard brought back best-effort: a guard the disk refuses still
    /// gives the server's time; <paramref name="askedAt"/> only when the state cannot be read just now); no
    /// check for any other answer. With the time it was judged at.
    /// </summary>
    private async Task<(LeaseCheck? Check, long Now)> JudgeClaimAsync(HttpResult? response, DeviceKind? kind, long askedAt, CancellationToken cancellationToken)
    {
        var body = response?.Status == 200 ? Wire.ParseClaimBody(response.Json) : null;
        if (body is not { Key: { } key, ActivationId: { } activationId, Lease: { } lease }) return (null, askedAt);
        long now;
        try
        {
            now = await ServerNowAsync(lease, key, activationId, bestEffort: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            now = askedAt;
        }
        return (await EvaluateAsync(lease, now, key, activationId, kind, cancellationToken).ConfigureAwait(false), now);
    }
}
