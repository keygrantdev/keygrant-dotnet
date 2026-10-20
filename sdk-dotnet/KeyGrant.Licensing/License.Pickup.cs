using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// Key pickup under License: a code mailed to the address a key was sold to (PickUpAsync(email)), that address's keys of the
// product for the code (PickUpAsync(email, code)), and a restore that confirms and activates the key that fits this build
// (RestoreByEmailAsync), so an App Store build licenses itself with no key typed. The two PickUpAsync calls store nothing and run
// beside any call; a restore runs in the one-at-a-time order as ActivateAsync does, and what it activates is stored as any
// activation is. Nothing here takes a licence away.
public sealed partial class License
{
    /// <summary>The keys the last confirm answered, with the address and code as sent and when (the clock adapter, ms): in memory only.</summary>
    private sealed record KeptKeys(string Email, string Code, IReadOnlyList<PickedKey> Keys, long At);

    private readonly object keptGate = new();

    private KeptKeys? keptKeys;

    /// <summary>
    /// Ask KeyGrant to mail a code to <paramref name="email"/>, the address a key was sold to. For a build that may not take a
    /// typed key (an App Store build): the customer types the code, then
    /// <see cref="PickUpAsync(string, string, CancellationToken)"/> hands back that address's keys, or
    /// <see cref="RestoreByEmailAsync"/> activates the one that fits. <see cref="PickUpSentResult.Ok"/> means the code is on its
    /// way when the address holds a key: the answer is the same when it holds none.
    /// <para>
    /// The product's Key pickup must be on (<see cref="PickupError.PickupDisabled"/> otherwise). A blank address, or one longer
    /// than any, is <see cref="PickupError.InvalidEmail"/>, asking nothing. Stores nothing and runs beside any call. It never
    /// throws for an outcome (no answer at all is <see cref="PickupError.Network"/>); the one exception is
    /// <paramref name="cancellationToken"/>: cancelled before the request is answered, it throws
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    /// <param name="email">The address as the customer entered it.</param>
    /// <param name="cancellationToken">As described above.</param>
    /// <returns>Asked for, or why not.</returns>
    public async Task<PickUpSentResult> PickUpAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var ask = Pickup.AskFor(email, null);
        if (ask.Refused is { } refused) return PickUpSentResult.Failure(refused);
        HttpResult response;
        try
        {
            response = await PostAsync("pickup", new JsonObject { ["email"] = ask.Email }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PickUpSentResult.Failure(PickupError.Network);
        }
        if (response.Status == 200) return PickUpSentResult.Sent;
        var (error, retryAfter) = Pickup.Refusal(response.Json);
        return PickUpSentResult.Failure(error, retryAfter);
    }

    /// <summary>
    /// The keys of the product that <paramref name="email"/> was sold, for the <paramref name="code"/> mailed to it: each with
    /// what the app may show (its <see cref="PickedKey.ModelName"/>) and <see cref="PickedKey.Held"/> when this device already
    /// holds a seat on it (the fingerprints a validate sends go with the code, when the device can tell who it is). An address
    /// with no key answers an empty list. Activate one with <c>ActivateAsync(picked.Key, new ActivateOptions { Email = address,
    /// Code = picked.ActivationCode })</c>, or let <see cref="RestoreByEmailAsync"/> choose.
    /// <para>
    /// The address is judged first (<see cref="PickupError.InvalidEmail"/>), then the code: the ends trimmed, the spaces dropped,
    /// exactly six ASCII digits or <see cref="PickupError.WrongCode"/>, at once and asking nothing, so the address loses none of
    /// its wrong tries. The first confirm spends the code, so the keys it answered are kept in memory for 10 minutes by the
    /// address and code as sent: the same pair answers from them and asks nothing. Stores nothing (not the address, the code, the
    /// keys or a refusal), runs beside any call, and a 200 that holds no list of keys is <see cref="PickupError.Network"/>. It
    /// never throws for an outcome; cancelled before the request is answered, it throws <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    /// <param name="email">The address as the customer entered it.</param>
    /// <param name="code">The code as the customer entered it.</param>
    /// <param name="cancellationToken">As described above.</param>
    /// <returns>The keys, or why not.</returns>
    public Task<PickUpKeysResult> PickUpAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);
        return ConfirmPickupAsync(email, code, cancellationToken);
    }

    /// <summary>
    /// Confirm a pickup's code and activate, on this device, the key of the address that fits this build: the one this device
    /// already holds a seat on, else one with a free seat, else the first. It activates as
    /// <see cref="ActivateAsync(string, ActivateOptions?, CancellationToken)"/> would, with the address as the pickup sent it and,
    /// for a code-locked key, the code the pickup handed back, so nothing more is asked of the customer. The key is stored as an
    /// activation's is and never handed to the app; nothing else is stored. The keys the confirm answered are kept in memory for
    /// 10 minutes (see <see cref="PickUpAsync(string, string, CancellationToken)"/>), so a restore whose activation got no answer
    /// can be retried with the same code; a restore that activates drops them.
    /// <para>
    /// Answers the confirm's errors (<see cref="RestoreResult.PickupError"/>); then <see cref="RestoreResult.NoKey"/> for an address
    /// with no key, and <see cref="LicenseError.Upgrade"/> or <see cref="LicenseError.Outdated"/> (<see cref="RestoreResult.Error"/>)
    /// when none of its keys covers this build (<c>Upgrade</c>: they are for an older major, buy an upgrade; <c>Outdated</c>: update
    /// the app), activating nothing; then the activation's own errors, as they come. Behind a call that has not finished it answers
    /// <see cref="LicenseError.Network"/>, to retry, as <see cref="ActivateAsync(string, CancellationToken)"/> does.
    /// </para>
    /// </summary>
    /// <param name="email">The address as the customer entered it.</param>
    /// <param name="code">The code as the customer entered it.</param>
    /// <param name="cancellationToken">As for <see cref="ActivateAsync(string, CancellationToken)"/>.</param>
    /// <returns>Restored, or why not.</returns>
    public Task<RestoreResult> RestoreByEmailAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);
        return Serial(
            readOnly => readOnly ? Task.FromResult(RestoreResult.Failure(LicenseError.Network)) : RestoreNowAsync(email, code, cancellationToken),
            cancellationToken);
    }

    private async Task<PickUpKeysResult> ConfirmPickupAsync(string email, string code, CancellationToken cancellationToken)
    {
        var ask = Pickup.AskFor(email, code);
        if (ask.Refused is { } refused) return PickUpKeysResult.Failure(refused);
        if (KeptFor(ask.Email!, ask.Code!) is { } kept) return PickUpKeysResult.Success(kept);
        // Read-only: this runs outside the one-at-a-time order, beside any call.
        StoredState stored;
        try
        {
            stored = await LoadAsync(readOnly: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            stored = new StoredState();
        }
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), InstallKind(stored.DeviceKind), cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["email"] = ask.Email, ["code"] = ask.Code };
        if (device.CanTell) body["fingerprints"] = Fingerprints(device);
        HttpResult response;
        try
        {
            response = await PostAsync("pickup/confirm", body, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PickUpKeysResult.Failure(PickupError.Network);
        }
        if (response.Status != 200)
        {
            var (error, retryAfter) = Pickup.Refusal(response.Json);
            return PickUpKeysResult.Failure(error, retryAfter);
        }
        if (Pickup.KeysOf(response.Json) is not { } read) return PickUpKeysResult.Failure(PickupError.Network);
        // A copy no caller holds, so a caller that casts its list and changes it changes nothing kept.
        IReadOnlyList<PickedKey> keys = Array.AsReadOnly(read.ToArray());
        lock (keptGate) keptKeys = new KeptKeys(ask.Email!, ask.Code!, keys, clock.Now());
        return PickUpKeysResult.Success(keys);
    }

    /// <summary>The keys a confirm with this address and code answered, while they are kept (<see cref="Pickup.KeysKeepMs"/>); null otherwise.</summary>
    private IReadOnlyList<PickedKey>? KeptFor(string email, string code)
    {
        lock (keptGate)
        {
            if (keptKeys is not { } kept) return null;
            var age = clock.Now() - kept.At;
            return kept.Email == email && kept.Code == code && age >= 0 && age < Pickup.KeysKeepMs ? kept.Keys : null;
        }
    }

    /// <summary>The confirm, the choice, and the activation of the key chosen.</summary>
    private async Task<RestoreResult> RestoreNowAsync(string email, string code, CancellationToken cancellationToken)
    {
        var picked = await ConfirmPickupAsync(email, code, cancellationToken).ConfigureAwait(false);
        if (!picked.Ok) return RestoreResult.Failure(picked.Error!.Value, picked.RetryAfter);
        var choice = Pickup.Choose(picked.Keys!, major, clock.Now());
        if (choice.Refused is { } unchosen)
        {
            return unchosen switch
            {
                Pickup.Unchosen.NoKey => RestoreResult.NoKeyFound,
                Pickup.Unchosen.Upgrade => RestoreResult.Failure(LicenseError.Upgrade),
                _ => RestoreResult.Failure(LicenseError.Outdated),
            };
        }
        var key = picked.Keys![choice.Index!.Value];
        // The address as the pickup sent it (the confirm just accepted it), and a lock's code when the pickup handed one back.
        var address = Pickup.AskFor(email, null).Email ?? email;
        var activated = await ActivateNowAsync(key.Key, new ActivateOptions { Email = address, Code = key.ActivationCode }, cancellationToken).ConfigureAwait(false);
        if (!activated.Ok) return RestoreResult.Failure(activated.Error!.Value);
        // Activated: the keys (and a lock's single-use code among them) are done with.
        lock (keptGate) keptKeys = null;
        return RestoreResult.Success;
    }
}
