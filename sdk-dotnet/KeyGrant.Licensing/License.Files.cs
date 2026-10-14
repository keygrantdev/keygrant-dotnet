using System.Text;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// Licence files: taking one an app was handed, removing it, and the status a stored one gives. Never online:
// a key file's key is activated as a typed key is.
public sealed partial class License
{
    /// <summary>
    /// Take a licence file the customer was sent (<c>.kglicense</c>, <see cref="LicenseFileFormat.Extension"/>):
    /// its content, as a drop handler or an open dialog reads it (<c>File.ReadAllBytesAsync(path)</c>), never
    /// its path. Verified offline against the build's key set, for this product and, for an offline file,
    /// this build's major; refused when <see cref="LicenseConfig.RevokedLicenseFiles"/> lists it
    /// (<see cref="ImportLicenseFileResult.Error"/>, nothing stored). An OFFLINE file is then stored, replacing
    /// any stored before, and licenses the app with no network, ever: <see cref="StatusAsync"/> answers it with
    /// <see cref="LicenseSource.File"/>. A KEY file's key is activated online exactly as
    /// <see cref="ActivateAsync"/> would, and its errors are that call's
    /// (<see cref="ImportLicenseFileResult.ActivateError"/>).
    /// </summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="cancellationToken">As for <see cref="ActivateAsync"/>.</param>
    /// <returns>Taken, or why not.</returns>
    /// <exception cref="InvalidOperationException">A call before it has not settled: try again.</exception>
    /// <remarks>Throws, as <see cref="ActivateAsync"/> does, when the state cannot be read or the file cannot be saved.</remarks>
    public Task<ImportLicenseFileResult> ImportLicenseFileAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        ImportAsync(() => LicenseFiles.TokenOf(content.Span), cancellationToken);

    /// <summary>
    /// <see cref="ImportLicenseFileAsync(ReadOnlyMemory{byte}, CancellationToken)"/> for a file's content as
    /// text (pasted, or read as a string), which is read as its UTF-8 bytes.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="cancellationToken">As for <see cref="ActivateAsync"/>.</param>
    /// <returns>Taken, or why not.</returns>
    /// <exception cref="InvalidOperationException">A call before it has not settled: try again.</exception>
    public Task<ImportLicenseFileResult> ImportLicenseFileAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        // A text longer in characters than the bound is longer in UTF-8 bytes too: never encoded.
        return ImportAsync(
            () => content.Length > LicenseFileFormat.MaxBytes ? null : LicenseFiles.TokenOf(Encoding.UTF8.GetBytes(content)),
            cancellationToken);
    }

    /// <summary>
    /// Drop the stored offline licence file, if there is one; a key this device holds keeps working.
    /// <see cref="DeactivateAsync"/> leaves a stored file alone: it releases the key's seat, and a file holds none.
    /// </summary>
    /// <param name="cancellationToken">Throws <see cref="OperationCanceledException"/> while the call waits its turn.</param>
    /// <returns>A task that completes once the file is dropped.</returns>
    /// <exception cref="InvalidOperationException">A call before it has not settled: try again.</exception>
    /// <remarks>Throws, as <see cref="DeactivateAsync"/> does, when that cannot be saved.</remarks>
    public Task RemoveLicenseFileAsync(CancellationToken cancellationToken = default) =>
        Serial(
            async readOnly =>
            {
                if (readOnly) throw new InvalidOperationException("a previous licence call has not finished; try again");
                await SaveLicenseFileAsync(null).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>The import, in its turn: refused behind a call that has not settled, as <see cref="DeactivateAsync"/> is.</summary>
    private Task<ImportLicenseFileResult> ImportAsync(Func<string?> tokenOf, CancellationToken cancellationToken) =>
        Serial(
            readOnly => readOnly
                ? throw new InvalidOperationException("a previous licence call has not finished; try again")
                : ImportNowAsync(tokenOf(), cancellationToken),
            cancellationToken);

    private async Task<ImportLicenseFileResult> ImportNowAsync(string? token, CancellationToken cancellationToken)
    {
        if (token is null) return ImportLicenseFileResult.Refused(LicenseFileError.Malformed);
        if (!CheckFile(token).TryTake(out var claims, out var refused)) return ImportLicenseFileResult.Refused(refused);
        var licensedTo = LicenseFiles.LicensedToOf(claims);
        if (claims.Kind == LicenseFileKind.Key)
        {
            var activated = await ActivateNowAsync(claims.Sub, cancellationToken).ConfigureAwait(false);
            return activated.Error is { } error ? ImportLicenseFileResult.NotActivated(error) : ImportLicenseFileResult.Taken(LicenseFileKind.Key, licensedTo);
        }
        await SaveLicenseFileAsync(token).ConfigureAwait(false);
        return ImportLicenseFileResult.Taken(LicenseFileKind.Offline, licensedTo);
    }

    /// <summary>Store (or, with null, drop) the offline licence file. Based on nothing: it is about no activation, so it lands as it is.</summary>
    private Task SaveLicenseFileAsync(string? token) => SaveAsync(new StatePatch { [StateField.LicenseFile] = token });

    /// <summary>A licence file's token verified against the build's key set and judged for this app (<see cref="LicenseFiles.Check"/>).</summary>
    private LicenseFileCheck CheckFile(string token) => LicenseFiles.Check(LicenseFiles.Verify(token, verifier), product, major, revokedFiles);

    /// <summary>The status the stored offline licence file gives, verified again now, or null when none is stored.</summary>
    private LicenseStatus? FileStatus(StoredState state) =>
        state.LicenseFile is { } token ? LicenseFiles.StatusOf(CheckFile(token)) : null;

    /// <summary>
    /// The denylist a configuration gives, copied: none when absent. A list holding null is a configuration
    /// that would revoke nothing it was meant to, and throws when the <see cref="License"/> is made.
    /// </summary>
    private static IReadOnlyList<string> RevokedOf(LicenseConfig config)
    {
        if (config.RevokedLicenseFiles is not { } revoked) return [];
        if (revoked.Any(id => id is null))
        {
            throw new ArgumentException("RevokedLicenseFiles is a list of licence file ids, none of them null", nameof(config));
        }
        return revoked.ToArray();
    }
}
