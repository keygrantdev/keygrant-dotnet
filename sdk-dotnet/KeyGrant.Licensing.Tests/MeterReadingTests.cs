using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What the SDK reads leniently and holds to the contract's numbers, whoever signed or stored it, answered from the SDK's own
/// behaviour (the conformance vectors that pin the same rules are not in every checkout): a lease's <c>met</c>, the journal's ids,
/// and the journal's wait at its longest.
/// </summary>
public class MeterReadingTests
{
    private const string Key = Metered.Key;
    private const long Minute = Metered.Minute;
    private const long Hour = Metered.Hour;
    private const long Month = Metered.Month;

    /// <summary>A licence lease whose <c>met</c> claim is exactly <paramref name="metJson"/> (any JSON text, written as it is), signed by the harness's key.</summary>
    private static string LeaseWithMet(Harness h, string metJson)
    {
        var claims = new JsonObject
        {
            ["iss"] = "keygrant",
            ["sub"] = Key,
            ["product"] = Product,
            ["aid"] = "act_1",
            ["model"] = "subscription",
            ["lim"] = 3,
            ["major"] = 3,
            ["ent"] = new JsonObject(),
            ["met"] = JsonNode.Parse(metJson),
            ["iat"] = Now / 1000,
            ["exp"] = Now / 1000 + Month,
        };
        return h.Signer.Sign(claims);
    }

    private static async Task<IReadOnlyDictionary<string, MeterStatus>> MetersOfLease(string metJson)
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = LeaseWithMet(h, metJson) };
        var status = await h.License.StatusAsync();
        // However the claim reads, the lease it rides on stands: a meter never takes a licence away.
        Assert.Equal(LicenseState.Licensed, status.State);
        Assert.Empty(h.Http.Calls);
        return status.Meters;
    }

    public static TheoryData<string, string> NotMeters => new()
    {
        { "a list", "[{\"left\":1,\"total\":1,\"offline\":0}]" },
        { "an empty list", "[]" },
        { "a text", "\"exports\"" },
        { "a number", "7" },
        { "a flag", "true" },
        { "null", "null" },
        { "an empty object", "{}" },
        { "a meter that is a list", "{\"a\":[1,2,3]}" },
        { "a meter that is a text", "{\"a\":\"x\"}" },
        { "a meter that is null", "{\"a\":null}" },
        { "a figure missing", "{\"a\":{\"left\":1,\"total\":2}}" },
        { "a negative figure", "{\"a\":{\"left\":-1,\"total\":2,\"offline\":0}}" },
        { "a negative total", "{\"a\":{\"left\":0,\"total\":-2,\"offline\":0}}" },
        { "a fractional figure", "{\"a\":{\"left\":1.5,\"total\":2,\"offline\":0}}" },
        { "a figure written as text", "{\"a\":{\"left\":\"1\",\"total\":2,\"offline\":0}}" },
        { "a figure that is null", "{\"a\":{\"left\":null,\"total\":2,\"offline\":0}}" },
        { "a figure past 2^53 - 1", "{\"a\":{\"left\":9007199254740993,\"total\":9007199254740993,\"offline\":0}}" },
        { "a figure past a double's range", "{\"a\":{\"left\":1e400,\"total\":2,\"offline\":0}}" },
    };

    [Theory]
    [MemberData(nameof(NotMeters))]
    public async Task Reads_a_met_claim_that_is_not_well_formed_as_no_meter_and_the_lease_stands(string _, string met)
    {
        Assert.Empty(await MetersOfLease(met));
    }

    [Fact]
    public async Task Drops_only_the_member_that_is_malformed_not_the_meters_beside_it()
    {
        var meters = await MetersOfLease("{\"bad\":{\"left\":-1,\"total\":2,\"offline\":0},\"also\":\"x\",\"good\":{\"left\":2,\"total\":3,\"offline\":1},\"fraction\":{\"left\":1,\"total\":1.5,\"offline\":0}}");
        Assert.Equal(["good"], meters.Keys);
        Assert.Equal(new MeterStatus(2, 3, 1, 0, false), meters["good"]);
    }

    [Fact]
    public async Task Reads_a_whole_number_however_it_is_written_and_the_largest_a_double_holds_exactly()
    {
        var meters = await MetersOfLease("{\"a\":{\"left\":4.0,\"total\":1e1,\"offline\":-0},\"b\":{\"left\":9007199254740991,\"total\":9007199254740991,\"offline\":0}}");
        Assert.Equal(new MeterStatus(4, 10, 0, 0, false), meters["a"]);
        Assert.Equal(new MeterStatus(9_007_199_254_740_991, 9_007_199_254_740_991, 0, 0, false), meters["b"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("null", false)]
    [InlineData("[true]", false)]
    [InlineData("{\"x\":true}", false)]
    public async Task Reads_resets_as_true_only_for_JSON_true(string resets, bool expected)
    {
        var meters = await MetersOfLease("{\"a\":{\"left\":1,\"total\":2,\"offline\":0,\"resets\":" + resets + "}}");
        Assert.Equal(expected, meters["a"].Resets);
    }

    [Fact]
    public async Task Keeps_a_meter_s_name_as_it_is_and_ignores_its_other_members_and_the_servers_caps()
    {
        // __proto__ is a name like any other; left above total, more than eight meters and a figure past the server's own cap are all kept.
        var many = string.Join(",", Enumerable.Range(0, 10).Select(i => $"\"m{i}\":{{\"left\":5,\"total\":2,\"offline\":0,\"later\":{{\"deep\":[1,2,{{}}]}}}}"));
        var meters = await MetersOfLease("{" + many + ",\"__proto__\":{\"left\":1,\"total\":2,\"offline\":0},\"big\":{\"left\":2000000000,\"total\":2000000000,\"offline\":0}}");
        Assert.Equal(12, meters.Count);
        Assert.Equal(5, meters["m9"].Left);
        Assert.Equal(1, meters["__proto__"].Left);
        Assert.Equal(2_000_000_000, meters["big"].Total);
    }

    [Fact]
    public async Task Spends_from_a_meter_read_leniently_as_from_any_other()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = LeaseWithMet(h, "{\"gone\":{\"left\":-1,\"total\":2,\"offline\":0},\"kept\":{\"left\":3.0,\"total\":3,\"offline\":2}}") };
        h.Http.Fail = true;
        Assert.Equal(UseResult.Refused(UseError.NoMeter), await h.License.UseAsync("gone"));
        Assert.Equal(UseResult.Spent(2, offline: true), await h.License.UseAsync("kept"));
    }

    // --- the journal's ids -------------------------------------------------------------------------------------------

    private static StoredState Held(params SpendEntry[] spends) => new() { ActivationId = "act_1", Spends = spends };

    private static SpendOp Op(SpendKind kind, string id, long n = 1, long? limit = null, bool told = false) =>
        new(kind, "act_1", id, "exports", n, Now, limit, told);

    [Theory]
    [InlineData(SpendKind.Unanswered)]
    [InlineData(SpendKind.Void)]
    public void An_unanswered_spend_or_a_void_whose_id_the_journal_holds_is_left_as_it_is_whatever_its_limit_says(SpendKind kind)
    {
        foreach (var heldKind in new[] { SpendKind.Offline, SpendKind.Unanswered, SpendKind.Void })
        {
            var state = Held(new SpendEntry("same", "exports", 3, Now - 10, heldKind));
            var landed = Journal.Landed(state, Op(kind, "same", n: 9, limit: 1), Now);
            Assert.False(landed.Refused);
            Assert.Equal(state.Spends, landed.Spends);
        }
    }

    [Fact]
    public void An_offline_spend_grows_the_open_entry_of_its_meter_and_its_own_id_is_not_read()
    {
        var state = Held(new SpendEntry("first", "exports", 3, Now - 10, SpendKind.Offline));
        var grown = Journal.Landed(state, Op(SpendKind.Offline, "first", n: 2), Now);
        Assert.Equal([new SpendEntry("first", "exports", 5, Now - 10, SpendKind.Offline)], grown.Spends);
        var other = Journal.Landed(state, Op(SpendKind.Offline, "second", n: 2), Now);
        Assert.Equal([new SpendEntry("first", "exports", 5, Now - 10, SpendKind.Offline)], other.Spends);
        // Another meter has no open entry: it opens one.
        var renders = Journal.Landed(state, new SpendOp(SpendKind.Offline, "act_1", "third", "renders", 1, Now), Now);
        Assert.Equal(["first", "third"], renders.Spends.Select(e => e.Id));
    }

    [Fact]
    public void A_spend_whose_id_the_journal_holds_is_judged_before_its_limit_and_one_with_a_new_id_is_not()
    {
        var state = Held(new SpendEntry("held", "exports", 8, Now - 10, SpendKind.Unanswered));
        // A new id past the limit is refused; the same id is the spend already there.
        Assert.True(Journal.Landed(state, Op(SpendKind.Unanswered, "new", n: 3, limit: 10), Now).Refused);
        Assert.False(Journal.Landed(state, Op(SpendKind.Unanswered, "held", n: 3, limit: 10), Now).Refused);
    }

    // --- the journal's wait at its longest ------------------------------------------------------------------------

    private static SpendEntry[] Unanswered(int count, long at = Now) =>
        Enumerable.Range(0, count).Select(i => new SpendEntry($"u{i}", "exports", 1, at, SpendKind.Unanswered)).ToArray();

    public static TheoryData<int, long> Waits => new()
    {
        { 1, Minute },
        { 2, 2 * Minute },
        { 3, 4 * Minute },
        { 4, 8 * Minute },
        { 5, 16 * Minute },
        { 6, 32 * Minute },
        { 7, Hour },
        { 8, Hour },
        { 20, Hour },
        { 63, Hour },
        { 64, Hour },
    };

    [Theory]
    [MemberData(nameof(Waits))]
    public void The_journal_waits_a_minute_doubling_with_each_entry_to_an_hour_and_never_overflows(int entries, long wait)
    {
        var spends = Unanswered(entries);
        Assert.True(Meters.JournalWaiting(spends, Now + wait - 1), $"{entries} entries, one millisecond short of {wait}");
        Assert.False(Meters.JournalWaiting(spends, Now + wait), $"{entries} entries, at {wait}");
        Assert.False(Meters.JournalAsks(spends, Now + wait - 1));
        Assert.True(Meters.JournalAsks(spends, Now + wait));
        // Whatever the journal holds, a spend is asked for once the wait is over, and not before.
        var state = new StoredState { Spends = spends };
        Assert.False(Meters.UseAsksNow(state, Now + wait - 1, Now + wait - 1, covered: true));
        Assert.True(Meters.UseAsksNow(state, Now + wait, Now + wait, covered: true));
    }

    [Fact]
    public void The_journal_waits_from_the_newest_unanswered_entry_whatever_the_order_and_counts_no_offline_entry()
    {
        var spends = new[]
        {
            new SpendEntry("new", "exports", 1, Now - 10, SpendKind.Unanswered),
            new SpendEntry("old", "exports", 1, Now - 90 * Minute, SpendKind.Void),
            new SpendEntry("off", "exports", 4, Now, SpendKind.Offline),
        };
        // Two entries count (the offline one does not): two minutes from the newest, which is not the last of the list.
        Assert.True(Meters.JournalWaiting(spends, Now - 10 + 2 * Minute - 1));
        Assert.False(Meters.JournalWaiting(spends, Now - 10 + 2 * Minute));
    }
}
