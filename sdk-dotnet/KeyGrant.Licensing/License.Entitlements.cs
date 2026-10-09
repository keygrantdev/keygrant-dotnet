namespace KeyGrant.Licensing;

// What the licence grants, asked offline: HasAsync and RequireAsync.
public sealed partial class License
{
    /// <summary>
    /// Whether the licence this device holds grants the entitlement <paramref name="code"/>: a flag that is
    /// on, a number that is not zero, or a text that is not empty. Offline, from the stored lease only: the
    /// entitlements of the status <see cref="StatusAsync"/> gives from what is stored, with no ask and no
    /// write, so a licence that does not license this build, and none at all, grant nothing. A running trial
    /// grants what its trial lease does. Never throws for storage: a state that cannot be read grants nothing
    /// just now.
    /// </summary>
    /// <param name="code">The entitlement's code, matched exactly (<c>"cloud_sync"</c>).</param>
    /// <param name="cancellationToken">Stops waiting on the device read; what is stored is then judged without it.</param>
    /// <returns>Whether it is granted.</returns>
    public async Task<bool> HasAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var state = await LoadForStatusAsync(true).ConfigureAwait(false);
        var status = state is null ? Unreadable : await StatusNowAsync(state, false, true, cancellationToken).ConfigureAwait(false);
        return EntitlementGrants.Grants(status.Entitlements, code);
    }

    /// <summary>
    /// As <see cref="HasAsync"/>, but throwing <see cref="MissingEntitlementException"/>, which names
    /// <paramref name="code"/>, when the licence does not grant that entitlement: for work that may run
    /// only when it does.
    /// </summary>
    /// <param name="code">The entitlement's code, matched exactly.</param>
    /// <param name="cancellationToken">As for <see cref="HasAsync"/>.</param>
    /// <returns>A task that completes when it is granted.</returns>
    /// <exception cref="MissingEntitlementException">The licence does not grant it.</exception>
    public async Task RequireAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!await HasAsync(code, cancellationToken).ConfigureAwait(false)) throw new MissingEntitlementException(code);
    }
}
