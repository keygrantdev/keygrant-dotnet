using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The metered-uses groups, which close the file: a lease's met claim, what a call to use asks for, the figures
// a lease and the device's own saved figures give, when a call asks the server, how a use answer and the journal
// of unsynced spends land, and the report a request carries and the confirmations an answer gives back.
public partial class ConformanceTests
{
    /// <summary>The metered-uses groups.</summary>
    private static string[] MeterGroups =>
    [
        "meterClaims", "meterLeases", "useAmounts", "meterFigures", "meterStatuses", "offlineSpends", "useAsks", "syncAsks",
        "useOutcomes", "journals", "spendReports", "spendConfirmations",
    ];

    public static TheoryData<int, string> MeterClaimCases => Cases("meterClaims");
    public static TheoryData<int, string> MeterLeaseCases => Cases("meterLeases");
    public static TheoryData<int, string> UseAmountCases => Cases("useAmounts", v => v["jsOnly"] is null);
    public static TheoryData<int, string> MeterFigureCases => Cases("meterFigures");
    public static TheoryData<int, string> MeterStatusCases => Cases("meterStatuses");
    public static TheoryData<int, string> OfflineSpendCases => Cases("offlineSpends");
    public static TheoryData<int, string> UseAskCases => Cases("useAsks");
    public static TheoryData<int, string> SyncAskCases => Cases("syncAsks");
    public static TheoryData<int, string> UseOutcomeCases => Cases("useOutcomes");
    public static TheoryData<int, string> JournalCases => Cases("journals");
    public static TheoryData<int, string> SpendReportCases => Cases("spendReports");
    public static TheoryData<int, string> SpendConfirmationCases => Cases("spendConfirmations");

    [Fact]
    public void Holds_every_metered_group_to_the_vectors_it_has_but_the_two_a_typed_call_cannot_hold()
    {
        // 412 vectors in 12 groups; the fraction and the text that no typed call can hold are the only ones left out.
        Assert.Equal(412, MeterGroups.Sum(g => Group(g).Count));
        Assert.Equal(2, Group("useAmounts").Count(v => v!["jsOnly"] is not null));
        Assert.Equal(Group("useAmounts").Count - 2, UseAmountCases.Count());
    }

    [Fact]
    public void The_metered_groups_close_the_file_in_their_order()
    {
        var groups = All.Select(p => p.Key).Where(k => !NotGroups.Contains(k)).ToList();
        Assert.Equal(MeterGroups, groups.TakeLast(MeterGroups.Length));
    }

    [Theory]
    [MemberData(nameof(MeterClaimCases))]
    public void Meters_are_read_from_a_lease_s_claim_as_the_reference_reads_them(int index, string name)
    {
        var v = Item("meterClaims", index);
        var read = Str(v["json"]) is { } text ? ClaimsParser.MetersOf(text) : ClaimsParser.MetersOf((RawJson?)null);
        AssertJson.Equal(v["expect"], FiguresJson(read), name);
    }

    [Theory]
    [MemberData(nameof(MeterLeaseCases))]
    public void Leases_that_carry_meters_verify_and_check_as_the_reference(int index, string name)
    {
        var v = Item("meterLeases", index);
        var verified = Verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value);
        var verify = new JsonObject { ["valid"] = verified.Valid };
        if (!verified.Valid) verify["reason"] = VerifyResult.NameOf(verified.Reason!.Value);
        if (verified.Claims is { } claims) verify["claims"] = ClaimsJson(claims);
        AssertJson.Equal(v["verify"], verify, $"{name}: verify");

        var h = v["holder"]!;
        var holder = new LeaseHolder(Str(h["key"])!, Str(h["activationId"])!, Strings(h["claims"]), Bool(h["canTell"])!.Value);
        var check = Offline.CheckLease(verified, Str(v["product"])!, Long(v["major"])!.Value, holder);
        var expect = check.Ok
            ? new JsonObject { ["ok"] = true, ["test"] = check.Test }
            : new JsonObject { ["ok"] = false, ["reason"] = check.Reason == LeaseCheckFailure.UnknownKey ? "unknown-key" : check.Reason!.Value.ToString().ToLowerInvariant() };
        AssertJson.Equal(v["expect"], expect, $"{name}: expect");
    }

    [Theory]
    [MemberData(nameof(UseAmountCases))]
    public void What_a_call_asks_for_is_what_the_reference_reads(int index, string name)
    {
        var v = Item("useAmounts", index);
        // A vector with no n is a call that gave none: 1.
        var asked = Meters.AskedFor(Str(v["meter"]), v["n"] is null ? 1 : Long(v["n"])!.Value);
        var expect = asked.Refused is { } refused
            ? new JsonObject { ["refused"] = Names.Of(refused) }
            : new JsonObject { ["meter"] = asked.Meter, ["n"] = asked.N };
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(MeterFigureCases))]
    public void Base_figures_are_the_reference_s(int index, string name)
    {
        var v = Item("meterFigures", index);
        var figures = Meters.FiguresOf(MeterSupport.Figures(v["met"]), Long(v["iat"])!.Value, MeterSupport.StoredFigures(v["figures"]));
        AssertJson.Equal(v["expect"], FiguresJson(figures), name);
    }

    [Theory]
    [MemberData(nameof(MeterStatusCases))]
    public void Statuses_show_the_meters_as_the_reference_does(int index, string name)
    {
        var v = Item("meterStatuses", index);
        var shown = Meters.MeterStatusOf(MeterSupport.Figures(v["figures"]), MeterSupport.Spends(v["spends"]));
        var expect = new JsonObject();
        foreach (var (code, m) in shown)
        {
            expect[code] = new JsonObject { ["left"] = m.Left, ["total"] = m.Total, ["offline"] = m.Offline, ["unsynced"] = m.Unsynced, ["resets"] = m.Resets };
        }
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(OfflineSpendCases))]
    public void A_spend_with_no_answer_is_allowed_as_the_reference_allows_it(int index, string name)
    {
        var v = Item("offlineSpends", index);
        var verdict = Meters.OfflineSpend(MeterSupport.Figures(v["figures"]), MeterSupport.Spends(v["spends"]), Str(v["meter"])!, Long(v["n"])!.Value);
        var expect = verdict.Allowed
            ? new JsonObject { ["ok"] = true, ["left"] = verdict.Left, ["limit"] = verdict.Limit }
            : verdict.Error == UseError.NoMeter
                ? new JsonObject { ["ok"] = false, ["error"] = "no-meter" }
                : new JsonObject { ["ok"] = false, ["error"] = "offline-limit", ["left"] = verdict.Left };
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(UseAskCases))]
    public void A_call_asks_the_server_when_the_reference_does(int index, string name)
    {
        var v = Item("useAsks", index);
        var asks = Meters.UseAsksNow(State(v["state"]), Long(v["clockNow"])!.Value, Long(v["now"])!.Value, Bool(v["covered"])!.Value);
        Assert.True(Bool(v["expect"]) == asks, name);
    }

    [Theory]
    [MemberData(nameof(SyncAskCases))]
    public void A_valid_lease_asks_to_report_a_journal_when_the_reference_does(int index, string name)
    {
        var v = Item("syncAsks", index);
        var c = v["claims"]!;
        var claims = Claims(iat: Long(c["iat"]), exp: Long(c["exp"]), major: Long(c["major"]));
        var asks = Renewal.AsksOnValidLease(claims, State(v["state"]), Int(v["major"])!.Value, Long(v["now"])!.Value, Long(v["clockNow"])!.Value);
        Assert.True(Bool(v["expect"]) == asks, name);
    }

    [Theory]
    [MemberData(nameof(UseOutcomeCases))]
    public void A_use_answer_does_what_the_reference_does(int index, string name)
    {
        var v = Item("useOutcomes", index);
        var d = v["device"]!;
        var device = new Device { Kind = Names.DeviceKindOf(Str(d["kind"])), OwnClaim = Str(d["ownClaim"]) };
        var outcome = Meters.OutcomeOf(MeterSupport.Response(v["response"]), Long(v["askedAt"])!.Value, Int(v["major"])!.Value, device);

        var expect = new JsonObject { ["outcome"] = MeterSupport.OutcomeName(outcome.Kind) };
        if (outcome.Refusal is { } refusal) expect["refusal"] = Names.Of(refusal);
        if (outcome.Left is { } left) expect["left"] = left;
        expect["patch"] = PatchJson(outcome.Patch);
        var figures = new JsonObject();
        foreach (var (code, f) in outcome.Figures) figures[code] = new JsonObject { ["left"] = f.Left, ["total"] = f.Total, ["offline"] = f.Offline, ["resets"] = f.Resets, ["at"] = f.At };
        expect["figures"] = figures;
        expect["confirmed"] = MeterSupport.ConfirmedJson(outcome.Confirmed);
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(JournalCases))]
    public void The_journal_lands_as_the_reference_lands_it(int index, string name)
    {
        var v = Item("journals", index);
        var latest = new StoredState
        {
            ActivationId = Str(v["latest"]!["activationId"]),
            Spends = v["latest"]!["spends"] is null ? null : MeterSupport.Spends(v["latest"]!["spends"]),
            MeterFigures = v["latest"]!["meterFigures"] is null ? null : MeterSupport.StoredFigures(v["latest"]!["meterFigures"]),
        };
        var landed = Journal.Landed(latest, MeterSupport.Op(v["op"]!), Long(v["now"])!.Value);
        var figures = new JsonObject();
        foreach (var (code, f) in landed.MeterFigures) figures[code] = new JsonObject { ["left"] = f.Left, ["total"] = f.Total, ["offline"] = f.Offline, ["resets"] = f.Resets, ["at"] = f.At };
        var expect = new JsonObject { ["spends"] = MeterSupport.SpendsJson(landed.Spends), ["meterFigures"] = figures, ["refused"] = landed.Refused };
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(SpendReportCases))]
    public void A_report_carries_the_journal_as_the_reference_reports_it(int index, string name)
    {
        var v = Item("spendReports", index);
        AssertJson.Equal(v["expect"], Meters.SpendsReported(MeterSupport.Spends(v["spends"])), name);
    }

    [Theory]
    [MemberData(nameof(SpendConfirmationCases))]
    public void An_answer_confirms_the_reports_the_reference_says_it_does(int index, string name)
    {
        var v = Item("spendConfirmations", index);
        AssertJson.Equal(v["expect"], MeterSupport.ConfirmedJson(Meters.SpendsConfirmed(MeterSupport.Response(v["response"]))), name);
    }

    private static JsonObject FiguresJson(IReadOnlyDictionary<string, BaseFigure> figures)
    {
        var json = new JsonObject();
        foreach (var (code, f) in figures) json[code] = new JsonObject { ["left"] = f.Left, ["total"] = f.Total, ["offline"] = f.Offline, ["resets"] = f.Resets };
        return json;
    }
}
