using System.Collections.ObjectModel;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// A lease's claims, as the server signs them (seconds since the epoch for every time). Unknown claims are
/// ignored; <c>major</c> defaults to 0, and <c>ent</c> and <c>met</c> are read leniently
/// (<see cref="ClaimsParser"/>): absent or malformed is none.
/// </summary>
internal sealed record LeaseClaims
{
    public required string Iss { get; init; }
    public required string Sub { get; init; }
    public required string Product { get; init; }
    public required string Aid { get; init; }
    public string? Fp { get; init; }
    public DeviceKind? Fpk { get; init; }
    public required string Model { get; init; }
    public long Lim { get; init; }
    public long Major { get; init; }
    public long? MinMajor { get; init; }
    public long? UpgradeUntil { get; init; }
    public IReadOnlyDictionary<string, Entitlement> Ent { get; init; } = ReadOnlyDictionary<string, Entitlement>.Empty;

    /// <summary>The key's meters as the lease signs them (<c>met</c>), read leniently; null when the lease has no such claim.</summary>
    public IReadOnlyDictionary<string, BaseFigure>? Met { get; init; }

    public long Iat { get; init; }
    public long Exp { get; init; }
    public bool? Grace { get; init; }
    public bool? Test { get; init; }
}

/// <summary>Why a lease failed verification.</summary>
internal enum VerifyFailure
{
    Malformed,
    /// <summary>The lease names a key (<c>kid</c>) the key set does not hold.</summary>
    UnknownKey,
    BadSignature,
    BadClaims,
    Expired,
}

/// <summary>A lease verified offline: valid with its claims, or why not (an expired one with its claims).</summary>
internal sealed record VerifyResult(bool Valid, VerifyFailure? Reason, LeaseClaims? Claims)
{
    public static VerifyResult Ok(LeaseClaims claims) => new(true, null, claims);

    public static VerifyResult Failed(VerifyFailure reason, LeaseClaims? claims = null) => new(false, reason, claims);

    public static string NameOf(VerifyFailure reason) => reason switch
    {
        VerifyFailure.Malformed => "malformed",
        VerifyFailure.UnknownKey => "unknown-key",
        VerifyFailure.BadSignature => "bad-signature",
        VerifyFailure.BadClaims => "bad-claims",
        VerifyFailure.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
