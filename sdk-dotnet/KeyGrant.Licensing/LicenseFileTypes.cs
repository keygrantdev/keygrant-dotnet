namespace KeyGrant.Licensing;

/// <summary>
/// The kind of a licence file (<c>.kglicense</c>): one format, two kinds.
/// </summary>
public enum LicenseFileKind
{
    /// <summary>
    /// A key file: it carries the licence key, which the app activates online exactly as a typed key
    /// (seats, revocation and refunds as usual). Only the delivery differs.
    /// </summary>
    Key,

    /// <summary>
    /// An offline licence file: a lease that never expires and is bound to no device, for machines that
    /// never go online. It has no seat limit and cannot be revoked: a leaked file works until an app update
    /// lists its id (<see cref="LicenseConfig.RevokedLicenseFiles"/>).
    /// </summary>
    Offline,
}

/// <summary>
/// Why a licence file was not taken (<see cref="License.ImportLicenseFileAsync(ReadOnlyMemory{byte}, CancellationToken)"/>).
/// </summary>
public enum LicenseFileError
{
    /// <summary>Not a licence file's text, or its token or claims are broken.</summary>
    Malformed,

    /// <summary>A signed token, but not a licence file (a lease, say).</summary>
    NotALicenseFile,

    /// <summary>Not signed by the key it names.</summary>
    BadSignature,

    /// <summary>
    /// Signed by a key this build does not carry (<see cref="LicenseConfig.PublicJwks"/>): a newer one, after
    /// the product rotated its key, which an update of the app carries.
    /// </summary>
    UnknownKey,

    /// <summary>Another product's file.</summary>
    WrongProduct,

    /// <summary>Its id is one this build lists as revoked (<see cref="LicenseConfig.RevokedLicenseFiles"/>).</summary>
    RevokedFile,

    /// <summary>An offline file for older versions than this build.</summary>
    Upgrade,

    /// <summary>An offline file for later versions than this build: update the app.</summary>
    Outdated,
}

/// <summary>What a status rests on, when it is not a key's lease or a trial.</summary>
public enum LicenseSource
{
    /// <summary>A stored offline licence file. It holds no seat: offer to remove the file rather than deactivating.</summary>
    File,
}

/// <summary>Who a licence file is licensed to, as it signs it: a name, an email address, or both.</summary>
/// <param name="Name">The name, when the file names one.</param>
/// <param name="Email">The email address, when the file names one.</param>
public sealed record LicensedTo(string? Name, string? Email);

/// <summary>
/// What <see cref="License.ImportLicenseFileAsync(ReadOnlyMemory{byte}, CancellationToken)"/> did: an
/// offline file stored, a key file's key activated, or why neither.
/// </summary>
public sealed record ImportLicenseFileResult
{
    private ImportLicenseFileResult(bool ok, LicenseFileKind? kind, LicensedTo? licensedTo, LicenseFileError? error, LicenseError? activateError)
    {
        Ok = ok;
        Kind = kind;
        LicensedTo = licensedTo;
        Error = error;
        ActivateError = activateError;
    }

    /// <summary>Whether the file was taken: an offline file stored, or a key file's key activated.</summary>
    public bool Ok { get; }

    /// <summary>The kind of file taken, when <see cref="Ok"/>.</summary>
    public LicenseFileKind? Kind { get; }

    /// <summary>Who the file taken is licensed to, as it signs it, when <see cref="Ok"/>.</summary>
    public LicensedTo? LicensedTo { get; }

    /// <summary>Why the file was refused, when it was: nothing was stored.</summary>
    public LicenseFileError? Error { get; }

    /// <summary>
    /// Why a key file's key did not activate, when it did not: what
    /// <see cref="License.ActivateAsync(string, CancellationToken)"/> answers for it.
    /// </summary>
    public LicenseError? ActivateError { get; }

    /// <summary>The file was taken.</summary>
    /// <param name="kind">Its kind.</param>
    /// <param name="licensedTo">Who it is licensed to.</param>
    /// <returns>A result that is <see cref="Ok"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="licensedTo"/> is null.</exception>
    public static ImportLicenseFileResult Taken(LicenseFileKind kind, LicensedTo licensedTo)
    {
        ArgumentNullException.ThrowIfNull(licensedTo);
        return new(true, kind, licensedTo, null, null);
    }

    /// <summary>The file was refused.</summary>
    /// <param name="error">Why.</param>
    /// <returns>A failed result.</returns>
    public static ImportLicenseFileResult Refused(LicenseFileError error) => new(false, null, null, error, null);

    /// <summary>A key file's key did not activate.</summary>
    /// <param name="error">What the activation answered.</param>
    /// <returns>A failed result.</returns>
    public static ImportLicenseFileResult NotActivated(LicenseError error) => new(false, null, null, null, error);
}

/// <summary>The licence file's format, as an app's open dialog or drop handler needs it.</summary>
public static class LicenseFileFormat
{
    /// <summary>A licence file's extension, for both kinds: <c>.kglicense</c>.</summary>
    public const string Extension = ".kglicense";

    /// <summary>The media type of a licence file attached to an email: a vendor type, so a mail client hands it over byte for byte.</summary>
    public const string MediaType = "application/vnd.keygrant.license";

    /// <summary>The most a licence file may be, in bytes; a larger one is <see cref="LicenseFileError.Malformed"/>. A real one is well under 2 KB.</summary>
    public const int MaxBytes = 16_384;
}
