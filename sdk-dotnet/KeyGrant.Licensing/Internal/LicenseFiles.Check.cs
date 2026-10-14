using System.Diagnostics.CodeAnalysis;

namespace KeyGrant.Licensing.Internal;

/// <summary>What a licence file says about this app, offline: taken, or refused with why (and its claims once the signature held).</summary>
internal sealed record LicenseFileCheck(LicenseFileError? Reason, LicenseFileClaims? Claims)
{
    public static LicenseFileCheck Taken(LicenseFileClaims claims) => new(null, claims);

    public static LicenseFileCheck Refused(LicenseFileError reason, LicenseFileClaims? claims = null) => new(reason, claims);

    /// <summary>Whether it was taken: no reason to refuse it.</summary>
    public bool Ok => Reason is null;

    /// <summary>Whether a taken file is a test licence's.</summary>
    public bool Test => Ok && Claims?.Test == true;

    /// <summary>The claims of a file taken, or why it was refused.</summary>
    public bool TryTake([NotNullWhen(true)] out LicenseFileClaims? claims, out LicenseFileError reason)
    {
        claims = Ok ? Claims : null;
        reason = Reason ?? default;
        return claims is not null;
    }
}

// What the SDK decides of a verified licence file: whether this app takes it, and the status a stored one gives.
internal static partial class LicenseFiles
{
    /// <summary>
    /// A verified licence file held to this product, this build's major and the app's denylist, in this
    /// order: not valid (unknown-key, bad-signature and not-a-license-file as they are; bad-claims and
    /// malformed as malformed); another product (wrong-product); its id in <paramref name="revoked"/>,
    /// matched exactly (revoked-file); for an offline file, the major below <c>minMajor</c> (absent: 1,
    /// outdated) or above <c>major</c> (upgrade). A key file covers what its key does, which only the server
    /// can say, so no version is judged. A check that fails once the signature held keeps the claims.
    /// </summary>
    public static LicenseFileCheck Check(FileVerifyResult v, string product, int major, IReadOnlyCollection<string> revoked)
    {
        if (v.Claims is not { } claims)
        {
            var reason = v.Reason switch
            {
                FileVerifyFailure.UnknownKey => LicenseFileError.UnknownKey,
                FileVerifyFailure.BadSignature => LicenseFileError.BadSignature,
                FileVerifyFailure.NotALicenseFile => LicenseFileError.NotALicenseFile,
                _ => LicenseFileError.Malformed,
            };
            return LicenseFileCheck.Refused(reason);
        }
        if (claims.Product != product) return LicenseFileCheck.Refused(LicenseFileError.WrongProduct, claims);
        if (revoked.Contains(claims.Fid, StringComparer.Ordinal)) return LicenseFileCheck.Refused(LicenseFileError.RevokedFile, claims);
        return VersionRefusal(claims, major) is { } versions ? LicenseFileCheck.Refused(versions, claims) : LicenseFileCheck.Taken(claims);
    }

    /// <summary>Why an offline file does not cover <paramref name="major"/> (below <c>minMajor</c>, absent 1: outdated; above <c>major</c>: upgrade), or null; a key file is never judged.</summary>
    private static LicenseFileError? VersionRefusal(LicenseFileClaims claims, int major)
    {
        if (claims.Kind != LicenseFileKind.Offline) return null;
        if (major < (claims.MinMajor ?? 1)) return LicenseFileError.Outdated;
        return major > claims.Major ? LicenseFileError.Upgrade : null;
    }

    /// <summary>Who a file is licensed to, as it signs it.</summary>
    public static LicensedTo LicensedToOf(LicenseFileClaims claims) => new(claims.Name, claims.Email);

    /// <summary>
    /// The status a STORED offline licence file gives, every one with <see cref="LicenseSource.File"/>:
    /// licensed, with its key, who it is licensed to, <c>test</c> when it says so and its entitlements;
    /// invalid with revoked-file, upgrade or outdated, its key and licensee, but no entitlements; invalid /
    /// unknown-key when the build does not carry its key (nothing of it read); and invalid / tampered for
    /// anything else, a key file included (one is never stored: the state was edited).
    /// </summary>
    public static LicenseStatus StatusOf(LicenseFileCheck check)
    {
        if (check.TryTake(out var taken, out _) && taken.Kind == LicenseFileKind.Offline)
        {
            return new LicenseStatus
            {
                State = LicenseState.Licensed,
                Key = taken.Sub,
                Source = LicenseSource.File,
                LicensedTo = LicensedToOf(taken),
                Test = check.Test,
                Entitlements = taken.Ent,
            };
        }
        LicenseReason? named = check.Reason switch
        {
            LicenseFileError.RevokedFile => LicenseReason.RevokedFile,
            LicenseFileError.Upgrade => LicenseReason.Upgrade,
            LicenseFileError.Outdated => LicenseReason.Outdated,
            _ => null,
        };
        if (named is { } reason && check.Claims is { } claims)
        {
            return new LicenseStatus { State = LicenseState.Invalid, Reason = reason, Key = claims.Sub, Source = LicenseSource.File, LicensedTo = LicensedToOf(claims) };
        }
        var unread = check.Reason == LicenseFileError.UnknownKey ? LicenseReason.UnknownKey : LicenseReason.Tampered;
        return new LicenseStatus { State = LicenseState.Invalid, Reason = unread, Source = LicenseSource.File };
    }
}
