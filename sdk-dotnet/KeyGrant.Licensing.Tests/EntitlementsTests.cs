using System.Reflection;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What a status hands the app of the lease's entitlements (the <c>ent</c> claim), on every path that
/// licenses or runs a trial, and none on any other; and the <see cref="Entitlement"/> and
/// <see cref="LicenseStatus"/> value semantics an app compares them by.
/// </summary>
public class EntitlementsTests
{
    private const string Key = "SLUICE-ENTL-ENTL-ENTL-ENTL";
    private static readonly TimeSpan Grace = LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15);

    /// <summary>The device's claim: a trial lease names its device by it.</summary>
    private static readonly string Claim = Hashes.FingerprintClaim("fp-test");

    private static JsonObject ProJson() => new() { ["edition"] = "pro", ["seats"] = 5, ["beta"] = false };

    private static Dictionary<string, Entitlement> Pro() => new() { ["edition"] = new("pro"), ["seats"] = new(5), ["beta"] = new(false) };

    private static readonly LicenseStatus LicensedPro = new() { State = LicenseState.Licensed, Key = Key, Entitlements = Pro() };

    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };

    /// <summary>A licence lease for KEY / act_1 granting <paramref name="ent"/> (any JSON, as signed), signed at <paramref name="now"/>.</summary>
    private static string Lease(Harness h, JsonNode? ent, long now = Now, long ttlSeconds = 30 * 24 * 3600, long major = 1, string sub = Key, bool? test = null) =>
        h.SignLease(new LeaseInput { Sub = sub, Ent = ent, TtlSeconds = ttlSeconds, Major = major, Test = test }, now);

    /// <summary>A trial lease for the harness's device granting <paramref name="ent"/>, signed to the trial's end (<paramref name="endsAt"/>, ms).</summary>
    private static string TrialLease(Harness h, JsonNode? ent, long endsAt) =>
        h.SignLease(new LeaseInput { Sub = $"trial:{Claim}", Aid = Claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = (endsAt - Now) / 1000, Ent = ent });

    private static StoredState Holding(string lease) => new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 1, LastSeen = Now };

    private static async Task Activated(Harness h, string lease)
    {
        h.Http.Lease("activate", lease);
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
    }

    // --- licensed ------------------------------------------------------------------------------

    [Fact]
    public async Task Hands_the_app_what_the_lease_grants_once_activated()
    {
        var h = Create();
        await Activated(h, Lease(h, ProJson()));
        var status = await h.License.StatusAsync();
        Assert.Equal(LicensedPro, status);
        Assert.True(status.Entitlements.TryGetValue("edition", out var edition) && edition.Text == "pro");
        Assert.Equal(((double?)5, (bool?)false), (status.Entitlements["seats"].Number, status.Entitlements["beta"].Flag));
    }

    [Fact]
    public async Task Hands_none_when_the_lease_grants_none_the_status_equal_to_the_one_before()
    {
        // As Test: an app comparing whole statuses sees no change from a lease that grants nothing.
        var h = Create();
        await Activated(h, Lease(h, new JsonObject()));
        var status = await h.License.StatusAsync();
        Assert.Equal(Licensed, status);
        Assert.Empty(status.Entitlements);
    }

    [Fact]
    public async Task Keeps_the_good_members_of_a_malformed_ent_and_the_licence_it_rides_on()
    {
        var h = Create();
        var ent = new JsonObject { ["edition"] = "pro", ["nested"] = new JsonObject { ["tier"] = "gold" }, ["none"] = null, ["list"] = new JsonArray(1), ["seats"] = 2 };
        h.Stored = Holding(Lease(h, ent));
        var expected = Licensed with { Entitlements = new Dictionary<string, Entitlement> { ["edition"] = new("pro"), ["seats"] = new(2) } };
        Assert.Equal(expected, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Reads_an_ent_that_is_not_an_object_as_none_never_as_a_refusal()
    {
        var h = Create();
        h.Stored = Holding(Lease(h, new JsonArray("pro")));
        Assert.Equal(Licensed, await h.License.StatusAsync());
        h.Stored = Holding(Lease(h, null));
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Takes_them_from_the_renewed_lease_not_the_one_it_replaced()
    {
        // From an EXPIRED lease, so the status can only come from the renewal.
        var h = Create();
        await Activated(h, Lease(h, new JsonObject { ["edition"] = "basic" }, ttlSeconds: 60));
        var later = Now + 2 * 3_600_000;
        h.SetClock(later);
        h.Http.Lease("validate", Lease(h, ProJson(), later));
        Assert.Equal(LicensedPro, await h.License.StatusAsync());
        Assert.True(h.Http.Called("validate"));
    }

    [Fact]
    public async Task Refresh_hands_the_new_lease_s_when_the_server_answers_and_the_held_lease_s_when_it_cannot()
    {
        var h = Create();
        await Activated(h, Lease(h, new JsonObject { ["edition"] = "basic" }));
        h.Http.Lease("validate", Lease(h, ProJson()));
        Assert.Equal(LicensedPro, await h.License.RefreshAsync());

        h.Http.Fail = true;
        Assert.Equal(LicensedPro, await h.License.RefreshAsync());
        Assert.Equal(["activate", "validate", "validate"], h.Http.Calls.Select(c => c.Action));
    }

    [Fact]
    public async Task Carries_a_test_licence_s_beside_its_test_flag()
    {
        var h = Create();
        await Activated(h, Lease(h, ProJson(), test: true));
        Assert.Equal(LicensedPro with { Test = true }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Carries_none_on_a_status_that_does_not_license_though_the_lease_grants_them()
    {
        var h = Create(2);
        h.Http.Fail = true;
        // A newer build than the lease covers.
        h.Stored = Holding(Lease(h, ProJson()));
        Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Upgrade, Key = Key }, await h.License.StatusAsync());
        // A lease past its end, the server out of reach.
        h.Stored = Holding(Lease(h, ProJson(), Now - 2 * Day, ttlSeconds: 3600, major: 2));
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await h.License.StatusAsync());
        // A lease for another key.
        h.Stored = Holding(Lease(h, ProJson(), major: 2, sub: "SLUICE-ZZZZ-ZZZZ-ZZZZ-ZZZZ"));
        Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Tampered }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Carries_none_on_a_refusal_the_server_gave_though_the_lease_it_keeps_grants_them()
    {
        // An upgrade refusal keeps the lease (a rollback runs offline on it), so the entitlements are
        // still on the device: the refusal must not carry them.
        var h = Create();
        await Activated(h, Lease(h, ProJson()));
        h.Http.Error("validate", 403, "upgrade-required");
        var upgrade = new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Upgrade, Key = Key };
        Assert.Equal(upgrade, await h.License.RefreshAsync());
        Assert.NotNull(h.Stored!.Lease);
        // The stored refusal, answered offline.
        h.Http.Fail = true;
        Assert.Equal(upgrade, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Carries_none_when_the_server_revokes_the_key_though_the_lease_it_held_granted_them()
    {
        var h = Create();
        await Activated(h, Lease(h, ProJson()));
        h.Http.Error("validate", 403, "revoked");
        var revoked = new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key };
        Assert.Equal(revoked, await h.License.RefreshAsync());
        h.Http.Fail = true;
        Assert.Equal(revoked, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Carries_none_on_a_stored_refusal_answered_behind_a_call_that_has_not_finished()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Holding(Lease(h, ProJson())) with { Refused = Refusal.Upgrade, RefusedMajor = 1 };
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        var later = h.License.StatusAsync();
        time.Advance(Grace);
        Assert.Equal(
            new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Upgrade, Key = Key, Stalled = true },
            await later.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Keeps_the_held_lease_s_when_an_early_renewal_gets_no_answer()
    {
        // Five days left of thirty: due to renew early, and the server is out of reach.
        var h = Create();
        h.Stored = Holding(Lease(h, ProJson(), Now - 25 * Day));
        h.Http.Fail = true;
        Assert.Equal(LicensedPro, await h.License.StatusAsync());
        Assert.Equal(["validate"], h.Http.Calls.Select(c => c.Action));
    }

    [Fact]
    public async Task Still_carries_them_behind_a_call_that_has_not_finished_from_the_lease_held()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Holding(Lease(h, ProJson()));
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        var later = h.License.StatusAsync();
        time.Advance(Grace);
        Assert.Equal(LicensedPro with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Cannot_be_changed_through_a_status_which_leaves_the_next_status_as_the_lease_says()
    {
        var h = Create();
        h.Stored = Holding(Lease(h, ProJson()));
        var first = await h.License.StatusAsync();
        var map = Assert.IsAssignableFrom<IDictionary<string, Entitlement>>(first.Entitlements);
        Assert.True(map.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => map["edition"] = new Entitlement("studio"));
        Assert.Throws<NotSupportedException>(() => map.Remove("seats"));
        Assert.Equal(LicensedPro, await h.License.StatusAsync());
    }

    // --- trials --------------------------------------------------------------------------------

    [Fact]
    public async Task Hands_a_trial_the_trial_lease_s_from_StartTrialAsync_and_every_status_after()
    {
        var h = Create();
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now - 2 * Day, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = TrialLease(h, ProJson(), Now + 5 * Day) });
        var trial = new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5, Entitlements = Pro() };
        Assert.Equal(trial, await h.License.StartTrialAsync());
        h.Http.Fail = true;
        Assert.Equal(trial, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Gives_a_trial_none_once_its_lease_no_longer_runs_it()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 6 * Day, TrialEndsAt = Now + Day, TrialLease = TrialLease(h, ProJson(), Now + Day), LastSeen = Now };
        h.SetClock(Now + 2 * Day);
        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, await h.License.StatusAsync());
    }

    // --- the values an app compares --------------------------------------------------------------

    [Fact]
    public void An_entitlement_is_one_kind_read_through_its_own_accessor()
    {
        var text = new Entitlement("pro");
        var number = new Entitlement(5);
        var flag = new Entitlement(true);
        Assert.Equal(((bool?)null, (double?)null, "pro"), (text.Flag, text.Number, text.Text));
        Assert.Equal(((bool?)null, (double?)5, (string?)null), (number.Flag, number.Number, number.Text));
        Assert.Equal(((bool?)true, (double?)null, (string?)null), (flag.Flag, flag.Number, flag.Text));
        Assert.Throws<ArgumentNullException>(() => new Entitlement((string)null!));
    }

    [Fact]
    public void Entitlements_are_equal_when_the_same_kind_holds_the_same_value_numbers_compared_as_numbers()
    {
        Assert.Equal(new Entitlement(5), new Entitlement(5.0));
        Assert.Equal(new Entitlement(5).GetHashCode(), new Entitlement(5.0).GetHashCode());
        // JSON's -0 is 0 to JavaScript's ===.
        Assert.Equal(new Entitlement(0.0), new Entitlement(-0.0));
        Assert.Equal(new Entitlement(0.0).GetHashCode(), new Entitlement(-0.0).GetHashCode());
        Assert.True(new Entitlement("pro") == new Entitlement("pro"));
        Assert.True(new Entitlement("Pro") != new Entitlement("pro"));
        Assert.NotEqual(new Entitlement(true), new Entitlement("true"));
        Assert.NotEqual(new Entitlement(1), new Entitlement(true));
        Assert.NotEqual(new Entitlement(0), new Entitlement(false));
        Assert.NotEqual(new Entitlement(""), new Entitlement(false));
        Assert.False(new Entitlement("pro").Equals(null));
        Assert.True(new Entitlement("pro") != null);
    }

    [Theory]
    [InlineData("flag", "true", "true")]
    [InlineData("flag", "false", "false")]
    [InlineData("number", "5", "5")]
    [InlineData("number", "0.5", "0.5")]
    [InlineData("number", "-2", "-2")]
    [InlineData("text", "pro", "pro")]
    [InlineData("text", "", "")]
    public void Writes_an_entitlement_as_its_value(string kind, string value, string expected)
    {
        var entitlement = kind switch
        {
            "flag" => new Entitlement(bool.Parse(value)),
            "number" => new Entitlement(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => new Entitlement(value),
        };
        Assert.Equal(expected, entitlement.ToString());
    }

    [Fact]
    public void Two_statuses_with_the_same_entitlements_are_equal_whichever_maps_hold_them()
    {
        var a = LicensedPro;
        var b = Licensed with { Entitlements = new Dictionary<string, Entitlement> { ["beta"] = new(false), ["seats"] = new(5.0), ["edition"] = new("pro") } };
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Entitlements = Pro().Append(new("extra", new(true))).ToDictionary() });
        Assert.NotEqual(a, b with { Entitlements = Pro().Where(e => e.Key != "beta").ToDictionary() });
        Assert.NotEqual(a, b with { Entitlements = new Dictionary<string, Entitlement>(Pro()) { ["edition"] = new("studio") } });
        Assert.NotEqual(a, Licensed);
        // `with` keeps them, and every other member still counts.
        Assert.Equal(a, a with { });
        Assert.Equal(a, (a with { Stalled = true }) with { Stalled = false });
        Assert.NotEqual(a, a with { Stalled = true });
        Assert.NotEqual(a, a with { Test = true });
        Assert.NotEqual(a, a with { Key = "SLUICE-ZZZZ-ZZZZ-ZZZZ-ZZZZ" });
    }

    [Fact]
    public void A_status_granting_none_equals_one_built_before_entitlements_were_read()
    {
        var before = new LicenseStatus { State = LicenseState.Licensed, Key = Key };
        Assert.Empty(before.Entitlements);
        var none = before with { Entitlements = new Dictionary<string, Entitlement>() };
        Assert.Equal(before, none);
        Assert.Equal(before.GetHashCode(), none.GetHashCode());
        // Null given is none, never a null member.
        var given = before with { Entitlements = null! };
        Assert.NotNull(given.Entitlements);
        Assert.Equal(before, given);
    }

    [Fact]
    public void A_status_keeps_its_own_read_only_copy_of_the_map_it_was_built_from()
    {
        var mine = Pro();
        var status = new LicenseStatus { State = LicenseState.Licensed, Entitlements = mine };
        mine["edition"] = new Entitlement("studio");
        mine.Remove("seats");
        Assert.Equal(("pro", 3), (status.Entitlements["edition"].Text, status.Entitlements.Count));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Entitlement>)status.Entitlements).Clear());
    }

    /// <summary>A status with every member set away from its default.</summary>
    private static LicenseStatus EveryMemberSet() => new()
    {
        State = LicenseState.Trial,
        Reason = LicenseReason.Trial,
        Key = Key,
        TrialDaysLeft = 5,
        TrialEndsAt = Now + 5 * Day,
        Test = true,
        Stalled = true,
        Entitlements = Pro(),
    };

    /// <summary>Every member of a status, each of which the theory below changes on its own.</summary>
    private static readonly string[] MemberNames = { "State", "Reason", "Key", "TrialDaysLeft", "TrialEndsAt", "Test", "Stalled", "Entitlements" };

    public static TheoryData<string> Members => new(MemberNames);

    [Theory]
    [MemberData(nameof(Members))]
    public void Two_statuses_that_differ_in_one_member_are_unequal_whichever_member_it_is(string member)
    {
        // Equals is written out (a record compares a dictionary by reference): each member must count.
        var status = EveryMemberSet();
        var other = member switch
        {
            "State" => status with { State = LicenseState.Licensed },
            "Reason" => status with { Reason = LicenseReason.Revoked },
            "Key" => status with { Key = "SLUICE-ZZZZ-ZZZZ-ZZZZ-ZZZZ" },
            "TrialDaysLeft" => status with { TrialDaysLeft = 4 },
            "TrialEndsAt" => status with { TrialEndsAt = Now + 4 * Day },
            "Test" => status with { Test = false },
            "Stalled" => status with { Stalled = false },
            "Entitlements" => status with { Entitlements = new Dictionary<string, Entitlement>(Pro()) { ["edition"] = new("studio") } },
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "not a member of LicenseStatus this theory changes"),
        };
        Assert.False(status.Equals(other), $"{member}: equal");
        Assert.False(other.Equals(status), $"{member}: equal the other way");
        Assert.False(status.Equals((object)other), $"{member}: equal as an object");
        Assert.True(status != other, $"{member}: not different by !=");
        // Built again from nothing, the same status is equal, with the same hash.
        Assert.Equal(status, EveryMemberSet());
        Assert.Equal(status.GetHashCode(), EveryMemberSet().GetHashCode());
    }

    [Fact]
    public void The_members_that_theory_changes_are_every_public_member_of_a_status()
    {
        // A member added to LicenseStatus later must join the theory above (and Equals), or this fails.
        var properties = typeof(LicenseStatus)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        var changed = MemberNames.OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(properties, changed);
    }

    [Fact]
    public void Prints_its_entitlements_by_content_in_name_order_rather_than_the_map_s_type()
    {
        Assert.Equal(
            "LicenseStatus { State = Licensed, Reason = , Key = SLUICE-ENTL-ENTL-ENTL-ENTL, TrialDaysLeft = , TrialEndsAt = , Test = False, "
                + "Entitlements = { beta = false, edition = pro, seats = 5 }, Stalled = False }",
            LicensedPro.ToString());
        var trial = new LicenseStatus { State = LicenseState.Trial, TrialDaysLeft = 5, TrialEndsAt = 1_757_432_000_000, Test = true, Stalled = true };
        Assert.Equal(
            "LicenseStatus { State = Trial, Reason = , Key = , TrialDaysLeft = 5, TrialEndsAt = 1757432000000, Test = True, Entitlements = { }, Stalled = True }",
            trial.ToString());
        Assert.Equal(
            "LicenseStatus { State = Invalid, Reason = Revoked, Key = K, TrialDaysLeft = , TrialEndsAt = , Test = False, Entitlements = { }, Stalled = False }",
            new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = "K" }.ToString());
    }
}

/// <summary>Entitlements compared as values: the same names, each with an equal <see cref="Entitlement"/>, in any order.</summary>
internal static class AssertEntitlements
{
    public static void Equal(IReadOnlyDictionary<string, Entitlement> expected, IReadOnlyDictionary<string, Entitlement> actual)
    {
        static List<(string, Entitlement)> Sorted(IReadOnlyDictionary<string, Entitlement> map) =>
            map.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)).ToList();
        Assert.Equal(Sorted(expected), Sorted(actual));
    }
}
