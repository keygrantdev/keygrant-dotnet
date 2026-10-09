namespace KeyGrant.Licensing;

/// <summary>Whether entitlements grant a code: what <see cref="License.HasAsync"/> answers, for a status already in hand.</summary>
public static class EntitlementGrants
{
    /// <summary>
    /// A flag that is on, a number that is not zero, or a text that is not empty. Absent, off, zero or empty
    /// is not granted. The code is matched exactly, case included.
    /// </summary>
    /// <param name="entitlements">A status's <see cref="LicenseStatus.Entitlements"/>.</param>
    /// <param name="code">The entitlement's code (<c>"cloud_sync"</c>).</param>
    /// <returns>Whether it is granted.</returns>
    public static bool Grants(IReadOnlyDictionary<string, Entitlement> entitlements, string code)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(code);
        if (!entitlements.TryGetValue(code, out var value)) return false;
        if (value.Flag is { } flag) return flag;
        if (value.Number is { } number) return number != 0;
        return value.Text is { Length: > 0 };
    }
}
