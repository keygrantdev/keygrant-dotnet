using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The groups for buying from the trial with no key typed, answered from each vector's own JSON.
public partial class ConformanceTests
{
    /// <summary>
    /// The <c>majors</c> cases this SDK's config can hold: every one but a fraction (the <c>jsOnly</c>
    /// ones). A configured major is a signed <see cref="int"/> here, so a negative is held, and replayed.
    /// </summary>
    public static TheoryData<int, string> MajorCases => Cases("majors", v => v["configured"] is null || IsWhole(v["configured"]!));

    public static TheoryData<int, string> PurchaseUrlCases => Cases("purchaseUrls");
    public static TheoryData<int, string> PurchaseReferenceCases => Cases("purchaseReferences");
    public static TheoryData<int, string> ClaimBodyCases => Cases("claimBodies");
    public static TheoryData<int, string> PurchaseTokenKeptCases => Cases("purchaseTokenKept");
    public static TheoryData<int, string> ClaimDueCases => Cases("claimDue");
    public static TheoryData<int, string> ParseClaimBodyCases => Cases("parseClaimBody");
    public static TheoryData<int, string> ClaimOutcomeCases => Cases("claimOutcomes");
    public static TheoryData<int, string> TrialReportDueCases => Cases("trialReportDue");

    private static bool IsWhole(JsonNode node) => Json.TryNumber(node, out var d) && Math.Truncate(d) == d;

    [Fact]
    public void Skips_only_the_majors_this_SDK_s_config_cannot_hold_the_fractions()
    {
        var skipped = Group("majors").Where(v => v!["configured"] is { } c && !IsWhole(c)).ToList();
        Assert.Equal(4, skipped.Count);
        // The skipped are exactly the jsOnly ones: a negative is replayed.
        Assert.Equal(skipped, Group("majors").Where(v => Bool(v!["jsOnly"]) == true));
        Assert.Contains(Group("majors"), v => Json.TryNumber(v!["configured"], out var d) && d < 0 && IsWhole(v["configured"]!));
        Assert.Equal(Group("majors").Count - skipped.Count, MajorCases.Count());
    }

    [Theory]
    [MemberData(nameof(MajorCases))]
    public void Majors_run_as_the_reference_reads_them(int index, string name)
    {
        var v = Item("majors", index);
        Assert.True(Int(v["expect"]) == Offline.MajorOf(Int(v["configured"])), name);
    }

    [Theory]
    [MemberData(nameof(PurchaseUrlCases))]
    public void Purchase_links_carry_the_secret_s_reference_as_the_reference_builds_them(int index, string name)
    {
        var v = Item("purchaseUrls", index);
        Assert.True(Str(v["expect"]) == Pickup.PurchaseLink(Str(v["link"])!, Str(v["secret"])!), name);
    }

    [Theory]
    [MemberData(nameof(PurchaseReferenceCases))]
    public void Purchase_references_hash_the_secret_as_the_reference_does(int index, string name)
    {
        var v = Item("purchaseReferences", index);
        Assert.True(Str(v["expect"]) == Pickup.PurchaseReference(Str(v["secret"])!), name);
    }

    [Theory]
    [MemberData(nameof(PurchaseTokenKeptCases))]
    public void Purchase_tokens_are_held_as_the_reference_holds_them(int index, string name)
    {
        var v = Item("purchaseTokenKept", index);
        Assert.True(Bool(v["expect"]) == Offline.PurchaseTokenKept(State(v["state"]), Long(v["now"])!.Value), name);
    }

    [Theory]
    [MemberData(nameof(ClaimDueCases))]
    public void Claims_fall_due_as_the_reference_s(int index, string name)
    {
        var v = Item("claimDue", index);
        Assert.True(Bool(v["expect"]) == Offline.ClaimDue(State(v["state"]), Long(v["clockNow"])!.Value, Long(v["now"])!.Value), name);
    }

    [Theory]
    [MemberData(nameof(ParseClaimBodyCases))]
    public void Claim_bodies_read_as_the_reference(int index, string name)
    {
        var v = Item("parseClaimBody", index);
        var body = Wire.ParseClaimBody(v["body"]?.DeepClone());
        var read = new JsonObject();
        if (body.Key is not null) read["key"] = body.Key;
        if (body.ActivationId is not null) read["activationId"] = body.ActivationId;
        if (body.Lease is not null) read["lease"] = body.Lease;
        if (body.Pending) read["pending"] = true;
        if (body.Error is not null) read["error"] = body.Error;
        AssertJson.Equal(v["expect"], read, name);
    }

    [Theory]
    [MemberData(nameof(ClaimOutcomeCases))]
    public void Claims_save_and_say_as_the_reference(int index, string name)
    {
        var v = Item("claimOutcomes", index);
        // The answer: null when there was none at all; its JSON body as the adapter hands it over.
        var response = v["response"] is { } r ? new HttpResult(Int(r["status"])!.Value, r["json"]?.DeepClone()) : null;
        var outcome = Pickup.ClaimOutcome(new ClaimInput(
            response,
            CheckOf(v["check"]),
            Long(v["clockNow"])!.Value,
            Long(v["now"])!.Value,
            Int(v["major"])!.Value,
            Names.DeviceKindOf(Str(v["kind"]))));
        var expect = new JsonObject
        {
            ["result"] = outcome.Result.ToString().ToLowerInvariant(),
            ["status"] = outcome.Status is { } status ? StatusJson(status) : null,
            ["patch"] = PatchJson(outcome.Patch),
        };
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(TrialReportDueCases))]
    public void Launch_reports_fall_due_as_the_reference_s(int index, string name)
    {
        var v = Item("trialReportDue", index);
        Assert.True(Bool(v["expect"]) == Offline.TrialReportDue(State(v["state"]), Long(v["now"])!.Value), name);
    }

    [Theory]
    [MemberData(nameof(ClaimBodyCases))]
    public void Claim_bodies_are_built_as_the_reference_builds_them(int index, string name)
    {
        var v = Item("claimBodies", index);
        var i = v["identity"]!;
        var identity = new DeviceIdentity(Str(i["fingerprint"]), Names.DeviceKindOf(Str(i["kind"])), Strings(i["alternates"]));
        // As the devices group reads one, with no install kind: no key is held.
        var device = Devices.From(identity, null, Bool(v["deviceHold"])!.Value, Hashes.FingerprintClaim);
        var request = Pickup.ClaimRequest(Str(v["secret"])!, device, Str(v["deviceName"]), Int(v["major"])!.Value, Bool(v["start"])!.Value);
        AssertJson.Equal(v["expect"], request?.ToJson(), name);
    }

    /// <summary>The offline check of a lease a 200 carried, as a vector gives it (the lease itself is not verified); null for none.</summary>
    private static LeaseCheck? CheckOf(JsonNode? check) => check switch
    {
        null => null,
        var c when Bool(c["ok"]) == true => LeaseCheck.Licensed(Bool(c["test"])!.Value, ClaimsParser.Parse(c["claims"]!.ToJsonString())!),
        var c => LeaseCheck.Refused(Enum.Parse<LeaseCheckFailure>(Str(c["reason"])!, ignoreCase: true)),
    };
}
