using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The key-set groups: a key's id, a lease verified against a key set, and whether entitlements grant a code.
public partial class ConformanceTests
{
    public static TheoryData<int, string> KeyIdCases => Cases("keyIds");
    public static TheoryData<int, string> KeySetCases => Cases("keySets");
    public static TheoryData<int, string> GrantCases => Cases("grants");

    [Theory]
    [MemberData(nameof(KeyIdCases))]
    public void Key_ids_are_the_thumbprints_the_reference_computes(int index, string name)
    {
        var v = Item("keyIds", index);
        var json = v["jwk"]!.ToJsonString();
        // null for a JWK the SDK refuses.
        if (v["expect"] is null) Assert.Throws<ArgumentException>(() => PublicJwk.Parse(json));
        else Assert.True(Str(v["expect"]) == KeyIds.Of(PublicJwk.Parse(json).KeyBytes()), name);
    }

    [Theory]
    [MemberData(nameof(KeySetCases))]
    public void Key_sets_verify_as_the_reference(int index, string name)
    {
        var v = Item("keySets", index);
        var verifier = new LeaseVerifier(v["keys"]!.AsArray().Select(k => PublicJwk.Parse(k!.ToJsonString()).KeyBytes()));
        AssertJson.Equal(v["kids"], new JsonArray(verifier.KeyIdList.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()), $"{name}: kids");

        var verified = verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value);
        var verify = new JsonObject { ["valid"] = verified.Valid };
        if (!verified.Valid) verify["reason"] = VerifyResult.NameOf(verified.Reason!.Value);
        if (verified.Claims is { } claims) verify["claims"] = ClaimsJson(claims);
        AssertJson.Equal(v["verify"], verify, $"{name}: verify");
    }

    [Theory]
    [MemberData(nameof(GrantCases))]
    public void Entitlements_grant_a_code_as_the_reference_says(int index, string name)
    {
        var v = Item("grants", index);
        // As a lease's ent is read: leniently, through the payload's own reader.
        var ent = ClaimsParser.EntitlementsOf(TopLevelJson.Read(v["ent"]!.ToJsonString()).Value);
        Assert.True(Bool(v["expect"]) == EntitlementGrants.Grants(ent, Str(v["code"])!), name);
    }
}
