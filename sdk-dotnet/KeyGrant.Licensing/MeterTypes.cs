namespace KeyGrant.Licensing;

/// <summary>
/// One meter of a key as <see cref="LicenseStatus.Meters"/> shows it. Figures are whole numbers; the server
/// signs none past 1,000,000,000.
/// </summary>
/// <param name="Left">
/// What is left: the uses the lease said were left, less what this device spent since and has not reported
/// (never below 0).
/// </param>
/// <param name="Total">The uses the key holds, for good or for this billing period.</param>
/// <param name="Offline">How many uses this device may spend while KeyGrant cannot be reached.</param>
/// <param name="Unsynced">Uses this device spent that KeyGrant has not heard of yet.</param>
/// <param name="Resets">True for a meter that comes back to its full uses each billing period.</param>
public sealed record MeterStatus(long Left, long Total, long Offline, long Unsynced, bool Resets);

/// <summary>
/// Why <see cref="License.UseAsync"/> did not spend. A refusal is an answer, never an exception.
/// </summary>
public enum UseError
{
    /// <summary>The amount is not a whole number from 1 to 1,000,000,000: a bug in the app. Nothing was asked.</summary>
    InvalidAmount,

    /// <summary>
    /// The key has no meter of that code: the server's word, or, with no answer, no figure on this device names
    /// it, or the code is one no meter can have (empty, or longer than 32 UTF-16 units).
    /// </summary>
    NoMeter,

    /// <summary>Online: fewer left than asked. <see cref="UseResult.Left"/> says how many (when the server did).</summary>
    UsedUp,

    /// <summary>
    /// KeyGrant could not be reached, and the spend is past this device's offline allowance for the meter, or past
    /// what it knows is left (every spend, for an allowance of 0). <see cref="UseResult.Left"/> says how many.
    /// </summary>
    OfflineLimit,

    /// <summary>
    /// No key's lease licenses this build on this device (a device trial and an offline licence file have no
    /// meters). Nothing was asked: read <see cref="License.StatusAsync"/>.
    /// </summary>
    NotLicensed,

    /// <summary>The state could not be read just now, or an adapter threw where there is no word for it: try again shortly.</summary>
    Storage,

    /// <summary>The server's verdict on the key, stored as a renewal's is (<see cref="LicenseReason.Revoked"/>).</summary>
    Revoked,

    /// <summary>The server's verdict on this build: the key does not cover it (<see cref="LicenseReason.Upgrade"/>).</summary>
    Upgrade,

    /// <summary>The server's verdict on this build: the key's lowest version is newer (<see cref="LicenseReason.Outdated"/>).</summary>
    Outdated,

    /// <summary>The server's verdict on this device: the seat is another device's (<see cref="LicenseReason.Device"/>).</summary>
    Device,
}

/// <summary>
/// What <see cref="License.UseAsync"/> did: spent, or why not. It is never an exception, whatever the outcome.
/// </summary>
public sealed record UseResult
{
    private UseResult(bool ok, long? left, bool offline, UseError? error)
    {
        Ok = ok;
        Left = left;
        Offline = offline;
        Error = error;
    }

    /// <summary>Whether the uses were spent: do the work they count.</summary>
    public bool Ok { get; }

    /// <summary>
    /// What is left of the meter now, net of this device's unsynced spends. Given when <see cref="Ok"/>, and on a
    /// refusal for <see cref="UseError.UsedUp"/> (the server's word) and <see cref="UseError.OfflineLimit"/>; null otherwise.
    /// </summary>
    public long? Left { get; }

    /// <summary>
    /// True when KeyGrant could not be asked and the spend came from this device's offline allowance (it is reported with
    /// the next request that can carry it).
    /// </summary>
    public bool Offline { get; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public UseError? Error { get; }

    /// <summary>The uses were spent, <paramref name="left"/> are left; <paramref name="offline"/> when from the allowance.</summary>
    /// <param name="left">What is left of the meter.</param>
    /// <param name="offline">Whether the spend came from the offline allowance.</param>
    /// <returns>A spent result.</returns>
    public static UseResult Spent(long left, bool offline = false) => new(true, left, offline, null);

    /// <summary>The uses were not spent, for <paramref name="error"/>; <paramref name="left"/> where the error gives one.</summary>
    /// <param name="error">Why not.</param>
    /// <param name="left">What is left, when the error says.</param>
    /// <returns>A refused result.</returns>
    public static UseResult Refused(UseError error, long? left = null) => new(false, left, false, error);
}

/// <summary>Which kind of entry a <see cref="SpendEntry"/> is in the journal of unsynced spends.</summary>
public enum SpendKind
{
    /// <summary>Spends made with no request sent: one entry for each meter, its count growing with each later such spend.</summary>
    Offline,

    /// <summary>One spend whose own request got no answer, made from the allowance. Its count never changes: the server may hold it.</summary>
    Unanswered,

    /// <summary>One spend whose own request got no answer and that was refused offline, reported so that the server takes it back if it recorded it.</summary>
    Void,
}

/// <summary>
/// A spend this device made that KeyGrant has not confirmed (the journal, <see cref="StoredState.Spends"/>): kept
/// in the stored state, reported with the next request, and dropped only when the server says it recorded it.
/// </summary>
/// <param name="Id">The spend's id: 16 random bytes in base64url (22 characters), never reused.</param>
/// <param name="Meter">The meter's code.</param>
/// <param name="N">How many uses.</param>
/// <param name="At">When (the guarded device clock, in milliseconds since the Unix epoch) the entry was opened.</param>
/// <param name="Kind">Which kind of entry it is.</param>
public sealed record SpendEntry(string Id, string Meter, long N, long At, SpendKind Kind);

/// <summary>
/// A meter's figures as the newest <c>use</c> answer gave them (<see cref="StoredState.MeterFigures"/>): newer than
/// the stored lease's own until the lease is renewed.
/// </summary>
/// <param name="Left">What the key had left when the server answered, every device's reported spends counted.</param>
/// <param name="Total">The uses the key holds.</param>
/// <param name="Offline">The allowance each of its devices may spend with no answer.</param>
/// <param name="Resets">Whether the meter comes back each billing period.</param>
/// <param name="At">The server's time (milliseconds since the Unix epoch).</param>
public sealed record MeterFigure(long Left, long Total, long Offline, bool Resets, long At);
