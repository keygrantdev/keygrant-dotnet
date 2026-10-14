using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// Licence-file test support: Ada's offline file and key file signed as the server signs them, a file's text,
/// the statuses a file gives, a lease for the key, and a state holding a file.
/// </summary>
internal static class LicenseFileSupport
{
    public const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    public const string AdaName = "Ada Lovelace";
    public const string AdaEmail = "ada@example.com";

    public static readonly LicensedTo Ada = new(AdaName, AdaEmail);

    /// <summary>
    /// Ada's OFFLINE file (<c>lf_1</c>, the harness's product and <see cref="Key"/>, majors 1 to 3, issued at
    /// <see cref="Harness.Now"/>) with <paramref name="over"/> laid over its claims (a null member removes one),
    /// signed by <paramref name="signer"/> under a file header naming it.
    /// </summary>
    public static string OfflineFile(this TestSigner signer, JsonObject? over = null) => signer.LicenseFile(
        new JsonObject
        {
            ["iss"] = "keygrant", ["kind"] = "offline", ["fid"] = "lf_1", ["sub"] = Key, ["product"] = Harness.Product,
            ["name"] = AdaName, ["email"] = AdaEmail, ["major"] = 3, ["ent"] = new JsonObject(), ["iat"] = Harness.Now / 1000,
        },
        over);

    /// <summary>Ada's KEY file (<c>lf_2</c>, <see cref="Key"/> in lower case, her email alone), with <paramref name="over"/> laid over it.</summary>
    public static string KeyFile(this TestSigner signer, JsonObject? over = null) => signer.LicenseFile(
        new JsonObject
        {
            ["iss"] = "keygrant", ["kind"] = "key", ["fid"] = "lf_2", ["sub"] = Key.ToLowerInvariant(), ["product"] = Harness.Product,
            ["email"] = AdaEmail, ["iat"] = Harness.Now / 1000,
        },
        over);

    private static string LicenseFile(this TestSigner signer, JsonObject claims, JsonObject? over)
    {
        foreach (var (name, value) in over ?? [])
        {
            if (value is null) claims.Remove(name);
            else claims[name] = value.DeepClone();
        }
        return signer.RawFile(claims.ToJsonString(Json.Options));
    }

    /// <summary>A licence file's text as the server writes one: notes, then the token on a line of its own, CRLF as a mail gateway may leave it.</summary>
    public static string FileText(string token) => $"# KeyGrant license file\r\n# Product: {Harness.Product}\r\n\r\n{token}\r\n";

    /// <summary><paramref name="token"/> with its payload swapped for <paramref name="other"/>'s, under its own header and signature.</summary>
    public static string PayloadSwapped(string token, string other)
    {
        var parts = token.Split('.');
        return $"{parts[0]}.{other.Split('.')[1]}.{parts[2]}";
    }

    /// <summary>What a status gives for Ada's offline file.</summary>
    public static LicenseStatus FileLicensed { get; } =
        new() { State = LicenseState.Licensed, Key = Key, Source = LicenseSource.File, LicensedTo = Ada };

    /// <summary>A stored file's status that does not license this build, with whose it is.</summary>
    public static LicenseStatus FileInvalid(LicenseReason reason) => FileLicensed with { State = LicenseState.Invalid, Reason = reason };

    /// <summary>A stored file's status with nothing of the file read.</summary>
    public static LicenseStatus FileUnread(LicenseReason reason) => new() { State = LicenseState.Invalid, Reason = reason, Source = LicenseSource.File };

    /// <summary><see cref="Key"/> licensed by its lease.</summary>
    public static LicenseStatus KeyLicensed { get; } = new() { State = LicenseState.Licensed, Key = Key };

    /// <summary>A licence lease for <see cref="Key"/> on act_1, covering majors 1 to 3, for thirty days.</summary>
    public static string LeaseTo3(Harness h) => h.SignLease(new LeaseInput { Sub = Key, Major = 3 });

    /// <summary>A licence file signed over a payload given as its exact text, under a file header naming <paramref name="signer"/>'s key.</summary>
    public static string RawFile(this TestSigner signer, string payloadText)
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes($"{{\"alg\":\"EdDSA\",\"typ\":\"kglicense+jwt\",\"kid\":\"{signer.Kid}\"}}"));
        return signer.SignInput($"{header}.{Base64Url.Encode(Encoding.UTF8.GetBytes(payloadText))}");
    }

    /// <summary>A state holding <paramref name="token"/> as its licence file, and nothing else.</summary>
    public static StoredState StoredFile(string token) => new() { LicenseFile = token };
}
