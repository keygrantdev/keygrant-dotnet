namespace KeyGrant.Licensing;

/// <summary>
/// Why a key pickup call (<see cref="License.PickUpAsync(string, CancellationToken)"/>,
/// <see cref="License.PickUpAsync(string, string, CancellationToken)"/>) did not do what it asked.
/// </summary>
public enum PickupError
{
    /// <summary>The address is blank or longer than any address: nothing was asked.</summary>
    InvalidEmail,

    /// <summary>
    /// The code is not six digits (nothing was asked, so the address lost none of its wrong tries), or it is not the
    /// address's live code: ask again.
    /// </summary>
    WrongCode,

    /// <summary>
    /// The address has no live code: it expired (10 minutes), was used, was burnt, or was never asked for. Ask for a
    /// new one with <see cref="License.PickUpAsync(string, CancellationToken)"/>.
    /// </summary>
    CodeExpired,

    /// <summary>
    /// Thirty wrong codes for the address this hour, or too many asks or confirms from this network: wait
    /// <c>RetryAfter</c> seconds.
    /// </summary>
    TooManyAttempts,

    /// <summary>The product's Key pickup is off: offer another way (support).</summary>
    PickupDisabled,

    /// <summary>A count could not be made just now: retry (after <c>RetryAfter</c> seconds, when the server said).</summary>
    TryAgain,

    /// <summary>
    /// No answer, or an answer that is none of a pickup's (a proxy's page, an unknown product, a 200 that holds no
    /// keys): retry.
    /// </summary>
    Network,
}

/// <summary>
/// What <see cref="License.PickUpAsync(string, CancellationToken)"/> did: a code is on its way when the address holds
/// a key, and the answer is the same when it does not.
/// </summary>
public sealed record PickUpSentResult
{
    private PickUpSentResult(bool ok, PickupError? error, long? retryAfter)
    {
        Ok = ok;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>Whether the code was asked for (it is mailed when the address holds a key of the product).</summary>
    public bool Ok { get; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public PickupError? Error { get; }

    /// <summary>
    /// The seconds to wait before asking again, when the server said so on <see cref="PickupError.TooManyAttempts"/>
    /// or <see cref="PickupError.TryAgain"/> (read from its answer's body).
    /// </summary>
    public long? RetryAfter { get; }

    /// <summary>The code was asked for.</summary>
    public static PickUpSentResult Sent { get; } = new(true, null, null);

    /// <summary>The code was not asked for, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <param name="retryAfter">The seconds to wait, when the server said.</param>
    /// <returns>A failed result.</returns>
    public static PickUpSentResult Failure(PickupError error, long? retryAfter = null) => new(false, error, retryAfter);
}

/// <summary>What <see cref="License.PickUpAsync(string, string, CancellationToken)"/> did: the address's keys of the product, or why not.</summary>
public sealed record PickUpKeysResult
{
    private PickUpKeysResult(bool ok, IReadOnlyList<PickedKey>? keys, PickupError? error, long? retryAfter)
    {
        Ok = ok;
        Keys = keys;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>Whether the code was taken and the keys read (there may be none).</summary>
    public bool Ok { get; }

    /// <summary>The address's keys of the product, first sold first, when <see cref="Ok"/>; null otherwise.</summary>
    public IReadOnlyList<PickedKey>? Keys { get; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public PickupError? Error { get; }

    /// <summary>The seconds to wait before asking again, when the server said so (see <see cref="PickUpSentResult.RetryAfter"/>).</summary>
    public long? RetryAfter { get; }

    /// <summary>The code was taken.</summary>
    /// <param name="keys">The address's keys.</param>
    /// <returns>A successful result.</returns>
    public static PickUpKeysResult Success(IReadOnlyList<PickedKey> keys) => new(true, keys, null, null);

    /// <summary>The code was not taken, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <param name="retryAfter">The seconds to wait, when the server said.</param>
    /// <returns>A failed result.</returns>
    public static PickUpKeysResult Failure(PickupError error, long? retryAfter = null) => new(false, null, error, retryAfter);
}

/// <summary>The versions a picked key covers: from <see cref="MinMajor"/> up to <see cref="MaxMajor"/>, and every major from <see cref="MinMajor"/> up while <see cref="UpgradeUntil"/> is ahead.</summary>
/// <param name="MinMajor">The lowest major the key covers.</param>
/// <param name="MaxMajor">The highest major it covers; null for no top.</param>
/// <param name="UpgradeUntil">When (milliseconds since the Unix epoch) its upgrade window ends; null for no window.</param>
public sealed record PickedVersions(long MinMajor, long? MaxMajor, long? UpgradeUntil);

/// <summary>The sale a picked key came with.</summary>
/// <param name="Id">The sale's id.</param>
/// <param name="Index">This key's place in the sale, from 1.</param>
/// <param name="Size">How many keys the sale made, from 1.</param>
public sealed record PickedSale(string Id, long Index, long Size);

/// <summary>
/// One key a pickup handed back: enough for the app to let the customer pick one (by <see cref="ModelName"/>), or to
/// let <see cref="License.RestoreByEmailAsync"/> choose and activate one. Never show <see cref="Key"/> and never ask the
/// customer to type anything but the address and the code.
/// </summary>
/// <param name="Key">
/// The licence key. Activate it with <c>ActivateAsync(picked.Key, new ActivateOptions { Email = address, Code = picked.ActivationCode })</c>.
/// </param>
/// <param name="Model">The licence model's kind (<c>perpetual</c>, <c>subscription</c>); empty when the server did not say.</param>
/// <param name="ModelName">The licence model's name, to show.</param>
/// <param name="State">The key's state (<c>active</c>, ...); empty when the server did not say.</param>
/// <param name="Entitlements">What the key grants, read as a lease's <c>ent</c> is.</param>
/// <param name="ExpiresAt">When (milliseconds since the Unix epoch) the key ends; null for a key that does not.</param>
/// <param name="Versions">
/// The versions the key covers. Null is a key sold without a version and not yet bound: its first activation binds it to
/// the build's major, so it covers any build.
/// </param>
/// <param name="DeviceLimit">How many devices the key allows.</param>
/// <param name="ActiveDevices">How many devices hold a seat now.</param>
/// <param name="Held">True when this device already holds a seat on the key (a reinstall takes it back).</param>
/// <param name="Sale">The sale the key came with, when it came with one.</param>
/// <param name="ActivationCode">
/// The code a user-locked key's activation takes, already proven by the pickup (the address's code is all a lock's code
/// proves); null on any other key.
/// </param>
public sealed record PickedKey(
    string Key,
    string Model,
    string? ModelName,
    string State,
    IReadOnlyDictionary<string, Entitlement> Entitlements,
    long? ExpiresAt,
    PickedVersions? Versions,
    long DeviceLimit,
    long ActiveDevices,
    bool Held,
    PickedSale? Sale,
    string? ActivationCode);

/// <summary>
/// What <see cref="License.RestoreByEmailAsync"/> did: a key of the address activated on this device, or why not.
/// A refusal is in one member of three: <see cref="PickupError"/> (the code was not taken), <see cref="NoKey"/> (the
/// address holds no key of the product) or <see cref="Error"/> (what activating answers, or the keys' own
/// <see cref="LicenseError.Upgrade"/> and <see cref="LicenseError.Outdated"/>).
/// </summary>
public sealed record RestoreResult
{
    private RestoreResult(bool ok, PickupError? pickupError, bool noKey, LicenseError? error, long? retryAfter)
    {
        Ok = ok;
        PickupError = pickupError;
        NoKey = noKey;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>Whether a key of the address was activated on this device.</summary>
    public bool Ok { get; }

    /// <summary>The pickup's own refusal (the confirm's), when the code was not taken.</summary>
    public PickupError? PickupError { get; }

    /// <summary>True when the address holds no key of the product.</summary>
    public bool NoKey { get; }

    /// <summary>
    /// Why the key was not activated: the keys' <see cref="LicenseError.Upgrade"/> (they are for an older major than this
    /// build: buy an upgrade) or <see cref="LicenseError.Outdated"/> (their lowest version is newer: update the app),
    /// activating nothing; or what activating the key answered (<see cref="LicenseError.Limit"/>, ...). Also
    /// <see cref="LicenseError.Network"/> behind a call that has not finished, as <see cref="License.ActivateAsync(string, CancellationToken)"/> answers.
    /// </summary>
    public LicenseError? Error { get; }

    /// <summary>The seconds to wait before asking again, when the server said so on a pickup refusal.</summary>
    public long? RetryAfter { get; }

    /// <summary>A key was activated.</summary>
    public static RestoreResult Success { get; } = new(true, null, false, null, null);

    /// <summary>The address holds no key of the product.</summary>
    public static RestoreResult NoKeyFound { get; } = new(false, null, true, null, null);

    /// <summary>The code was not taken, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <param name="retryAfter">The seconds to wait, when the server said.</param>
    /// <returns>A failed result.</returns>
    public static RestoreResult Failure(PickupError error, long? retryAfter = null) => new(false, error, false, null, retryAfter);

    /// <summary>The key was not activated, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <returns>A failed result.</returns>
    public static RestoreResult Failure(LicenseError error) => new(false, null, false, error, null);
}
