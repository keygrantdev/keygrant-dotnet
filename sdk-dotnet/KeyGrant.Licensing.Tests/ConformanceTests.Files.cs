using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The licence-file groups: a file's container read for its token, a file's token verified, judged and given as
// a status, and a file given where a lease goes.
public partial class ConformanceTests
{
    public static TheoryData<int, string> LicenseFileContainerCases => Cases("licenseFileContainers");
    public static TheoryData<int, string> LicenseFileCases => Cases("licenseFiles");
    public static TheoryData<int, string> FileAsLeaseCases => Cases("filesAsLeases");

    [Theory]
    [MemberData(nameof(LicenseFileContainerCases))]
    public void Licence_file_containers_hold_the_token_the_reference_reads(int index, string name)
    {
        var v = Item("licenseFileContainers", index);
        var bytes = Base64Url.Decode(Str(v["bytes"])!) ?? throw new InvalidOperationException($"{name}: bytes not base64url");
        Assert.True(Str(v["expect"]) == LicenseFiles.TokenOf(bytes), name);
    }

    [Theory]
    [MemberData(nameof(LicenseFileCases))]
    public void Licence_files_verify_check_and_give_the_status_the_reference_gives(int index, string name)
    {
        var v = Item("licenseFiles", index);
        var keys = new LeaseVerifier(v["keys"]!.AsArray().Select(k => PublicJwk.Parse(k!.ToJsonString()).KeyBytes()));
        var verified = LicenseFiles.Verify(Str(v["token"])!, keys);
        var verify = new JsonObject { ["valid"] = verified.Valid };
        if (verified.Reason is { } failure) verify["reason"] = FileVerifyResult.NameOf(failure);
        if (verified.Claims is { } claims) verify["claims"] = FileClaimsJson(claims);
        AssertJson.Equal(v["verify"], verify, $"{name}: verify");

        var check = LicenseFiles.Check(verified, Str(v["product"])!, Int(v["major"])!.Value, Strings(v["revoked"]));
        var expect = check.TryTake(out var taken, out var refused)
            ? new JsonObject { ["ok"] = true, ["kind"] = Names.Of(taken.Kind), ["test"] = check.Test }
            : new JsonObject { ["ok"] = false, ["reason"] = Names.Of(refused) };
        AssertJson.Equal(v["expect"], expect, $"{name}: expect");
        AssertJson.Equal(v["status"], StatusJson(LicenseFiles.StatusOf(check)), $"{name}: status");
    }

    [Theory]
    [MemberData(nameof(FileAsLeaseCases))]
    public void Licence_files_given_as_leases_are_refused_by_the_lease_verifier_as_it_is(int index, string name)
    {
        var v = Item("filesAsLeases", index);
        var verified = Verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value);
        var verify = new JsonObject { ["valid"] = verified.Valid };
        if (verified.Reason is { } failure) verify["reason"] = VerifyResult.NameOf(failure);
        if (verified.Claims is { } claims) verify["claims"] = ClaimsJson(claims);
        AssertJson.Equal(v["verify"], verify, $"{name}: verify");

        var check = Offline.CheckLease(verified, Str(v["product"])!, Long(v["major"])!.Value, HolderOf(v["holder"]!));
        var expect = check.Reason is { } reason
            ? new JsonObject { ["ok"] = false, ["reason"] = ReasonName(reason) }
            : new JsonObject { ["ok"] = true, ["test"] = check.Test };
        AssertJson.Equal(v["expect"], expect, $"{name}: expect");
        var holder = v["holder"]!;
        var trial = Offline.TrialLeaseValid(verified, Str(v["product"])!, Strings(holder["claims"]), Bool(holder["canTell"])!.Value);
        Assert.True(Bool(v["trial"]) == trial, $"{name}: trial");
    }

    /// <summary>A licence file's claims as parsed: an offline file's versions and <c>ent</c>, a key file's none.</summary>
    private static JsonObject FileClaimsJson(LicenseFileClaims claims)
    {
        var json = new JsonObject
        {
            ["iss"] = "keygrant",
            ["kind"] = Names.Of(claims.Kind),
            ["fid"] = claims.Fid,
            ["sub"] = claims.Sub,
            ["product"] = claims.Product,
        };
        if (claims.Name is not null) json["name"] = claims.Name;
        if (claims.Email is not null) json["email"] = claims.Email;
        if (claims.Kind == LicenseFileKind.Offline)
        {
            json["major"] = claims.Major;
            if (claims.MinMajor is { } min) json["minMajor"] = min;
            json["ent"] = EntitlementsJson(claims.Ent);
        }
        json["iat"] = claims.Iat;
        if (claims.Test is { } test) json["test"] = test;
        return json;
    }
}
