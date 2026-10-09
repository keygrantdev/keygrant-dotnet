namespace KeyGrant.Licensing.Internal;

/// <summary>The words the wire, the state file and the reference SDK use for this SDK's enums.</summary>
internal static class Names
{
    public static string Of(Refusal refusal) => refusal switch
    {
        Refusal.Revoked => "revoked",
        Refusal.Upgrade => "upgrade",
        Refusal.Outdated => "outdated",
        Refusal.Device => "device",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    public static Refusal? RefusalOf(string? word) => word switch
    {
        "revoked" => Refusal.Revoked,
        "upgrade" => Refusal.Upgrade,
        "outdated" => Refusal.Outdated,
        "device" => Refusal.Device,
        _ => null,
    };

    public static string Of(DeviceKind kind) => kind switch
    {
        DeviceKind.Machine => "machine",
        DeviceKind.Hostname => "hostname",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DeviceKind? DeviceKindOf(string? word) => word switch
    {
        "machine" => DeviceKind.Machine,
        "hostname" => DeviceKind.Hostname,
        _ => null,
    };

    public static string Of(LicenseState state) => state switch
    {
        LicenseState.Licensed => "licensed",
        LicenseState.Trial => "trial",
        LicenseState.Expired => "expired",
        LicenseState.Invalid => "invalid",
        LicenseState.Unlicensed => "unlicensed",
        LicenseState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string Of(LicenseReason reason) => reason switch
    {
        LicenseReason.Revoked => "revoked",
        LicenseReason.Upgrade => "upgrade",
        LicenseReason.Outdated => "outdated",
        LicenseReason.Offline => "offline",
        LicenseReason.Tampered => "tampered",
        LicenseReason.Device => "device",
        LicenseReason.Trial => "trial",
        LicenseReason.Storage => "storage",
        LicenseReason.UnknownKey => "unknown-key",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public static string Of(LicenseError error) => error switch
    {
        LicenseError.InvalidKey => "invalid-key",
        LicenseError.Limit => "limit",
        LicenseError.Revoked => "revoked",
        LicenseError.Upgrade => "upgrade",
        LicenseError.Outdated => "outdated",
        LicenseError.Network => "network",
        LicenseError.NotConfigured => "not-configured",
        LicenseError.InvalidLease => "invalid-lease",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    /// <summary>The status reason a refusal is answered with.</summary>
    public static LicenseReason ReasonOf(Refusal refusal) => refusal switch
    {
        Refusal.Revoked => LicenseReason.Revoked,
        Refusal.Upgrade => LicenseReason.Upgrade,
        Refusal.Outdated => LicenseReason.Outdated,
        Refusal.Device => LicenseReason.Device,
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}
