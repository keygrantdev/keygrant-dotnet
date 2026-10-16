namespace KeyGrant.Licensing;

/// <summary>
/// What <see cref="License.ActivateAsync(string, CancellationToken)"/> answers: activated, or why not.
/// </summary>
public sealed record ActivateResult
{
    private ActivateResult(bool ok, LicenseError? error)
    {
        Ok = ok;
        Error = error;
    }

    /// <summary>Whether the key was activated on this device.</summary>
    public bool Ok { get; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public LicenseError? Error { get; }

    /// <summary>The key was activated.</summary>
    public static ActivateResult Success { get; } = new(true, null);

    /// <summary>The key was not activated, for <paramref name="error"/>.</summary>
    /// <param name="error">Why not.</param>
    /// <returns>A failed result.</returns>
    public static ActivateResult Failure(LicenseError error) => new(false, error);
}

/// <summary>
/// What a person typed for a user-locked key, for <see cref="License.ActivateAsync(string, ActivateOptions?, CancellationToken)"/>:
/// the address the key was sold to and, for a key that mails a code to it, the code that came. Each is
/// optional, and neither is ever stored.
/// </summary>
/// <remarks>
/// A licence model can let only the person a key was sold to activate a NEW device on it. The first
/// activation of such a key answers <see cref="LicenseError.UserEmailRequired"/>; with the address,
/// <see cref="LicenseError.UserCodeRequired"/> (a code was just mailed) or a lease; with the code, a lease.
/// A key that is not locked ignores both once they reach the server, but an address longer than any, or a
/// code that is not six digits, is refused here for any key (<see cref="LicenseError.UserEmailMismatch"/>,
/// <see cref="LicenseError.UserCodeInvalid"/>) before anything is asked of the server.
/// </remarks>
public sealed class ActivateOptions
{
    /// <summary>
    /// The address the key was sold to, as the person typed it. The ends are trimmed and a blank is none; the
    /// server compares it without regard to case.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// The code mailed to that address, as the person typed it. The ends are trimmed and the spaces inside
    /// dropped (<c>123 456</c> is <c>123456</c>); a blank is none. It must be exactly six digits.
    /// </summary>
    public string? Code { get; init; }
}

/// <summary>
/// What <see cref="License.DeactivateAsync"/> did with this device's seat. The local licence is dropped
/// either way.
/// </summary>
/// <param name="Released">
/// True when KeyGrant confirmed the release (a 200), or when this device held no activation to release
/// (none at all, or only another device's, carried by a state file copied from it: never released from here).
/// False when it could not be asked, or answered anything else (a 5xx, a 429, a proxy's page): the seat
/// may still be held, and the app should say so (the developer's dashboard, or support, can release it),
/// not tell the customer it is free.
/// </param>
public sealed record DeactivateResult(bool Released);
