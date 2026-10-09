using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>Upgrade protection, the check of a newer major, refusals by key and by build, and the waits after them.</summary>
public class NewMajorAndRefusalTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private const long OpenMajor = 1_000_000;

    private static LicenseStatus Invalid(LicenseReason reason) => new() { State = LicenseState.Invalid, Reason = reason, Key = Key };

    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };

    /// <summary>An open lease (every version), carrying its upgrade window's end when given (ms).</summary>
    private static string OpenLease(Harness h, long? windowEnds = null, long now = Now, string model = "perpetual", long ttl = 30 * 24 * 3600) =>
        h.SignLease(new LeaseInput { Sub = Key, Major = OpenMajor, UpgradeUntil = windowEnds / 1000, Model = model, TtlSeconds = ttl }, now);

    /// <summary>A device on 3 holding a valid open lease, last heard by the server on 2.</summary>
    private static Harness OnNewMajor()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h), CheckedMajor = 2, LastSeen = Now };
        return h;
    }

    // --- upgrade protection: telling the server what this device runs -----------

    [Fact]
    public async Task Reports_a_newer_major_on_an_open_lease_online_at_once_and_notes_it_was_heard()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, Now + 10 * Day), CheckedMajor = 2, LastSeen = Now };
        var fresh = OpenLease(h, Now + 10 * Day);
        h.Http.Lease("validate", fresh);
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(3, h.Http.BodyOf("validate")["major"]!.GetValue<int>());
        Assert.Equal((fresh, 3), (h.Stored!.Lease, h.Stored.CheckedMajor));
    }

    [Fact]
    public async Task Keeps_the_valid_open_lease_and_the_app_running_when_that_check_cannot_reach_the_server()
    {
        var h = Create(3);
        var lease = OpenLease(h, Now + 10 * Day);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 2, LastSeen = Now };
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.True(h.Http.Called("validate"));
        Assert.Equal((lease, 2), (h.Stored!.Lease, h.Stored.CheckedMajor));
    }

    [Fact]
    public async Task Notes_the_run_all_the_same_when_it_cannot_be_saved()
    {
        var h = Create(3);
        var lease = OpenLease(h, Now + 10 * Day);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 2, LastSeen = Now };
        h.Http.Lease("validate", lease);
        h.FailWrites();
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(3, h.Http.BodyOf("validate")["keptMajor"]!.GetValue<int>());
    }

    [Fact]
    public async Task Does_not_ask_when_the_server_has_already_heard_this_major()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, Now + 10 * Day), CheckedMajor = 3, LastSeen = Now };
        await h.License.StatusAsync();
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Notes_the_major_it_runs_while_the_window_is_open_and_sends_it_on_the_next_validate()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, Now + 10 * Day), CheckedMajor = 3, LastSeen = Now };
        await h.License.StatusAsync();
        Assert.Equal(3, h.Stored!.KeptMajor);
        h.Http.Error("validate", 503, "unavailable");
        await h.License.RefreshAsync();
        Assert.Equal((3, 3), (h.Http.BodyOf("validate")["major"]!.GetValue<int>(), h.Http.BodyOf("validate")["keptMajor"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Stops_noting_at_the_window_s_end()
    {
        var h = Create(4);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, Now + 10 * Day), CheckedMajor = 4, KeptMajor = 3, LastSeen = Now };
        h.SetClock(Now + 11 * Day);
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal(3, h.Stored!.KeptMajor);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1000, 4)]
    public async Task Counts_the_window_s_last_second_as_closed_and_the_one_before_as_open(long offset, int kept)
    {
        var end = Now + 10 * Day;
        var h = Create(4);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, end), CheckedMajor = 4, KeptMajor = 3, LastSeen = Now };
        h.SetClock(end + offset);
        await h.License.StatusAsync();
        Assert.Equal(kept, h.Stored!.KeptMajor);
    }

    [Fact]
    public async Task Notes_nothing_on_a_lease_that_carries_no_window()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h), CheckedMajor = 3, LastSeen = Now };
        await h.License.StatusAsync();
        Assert.Null(h.Stored!.KeptMajor);
    }

    [Fact]
    public async Task Never_lands_a_kept_major_noted_for_one_activation_on_another_saved_meanwhile()
    {
        // Another instance activates another key while this one judges its lease: the major this run
        // noted (keptMajor) is about the activation it judged.
        Action swap = () => { };
        var h = Create(null, new Options
        {
            Major = 5,
            Fingerprint = new FuncFingerprinter(() =>
            {
                swap();
                return Task.FromResult("fp-test");
            }),
        });
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, Now + 10 * Day), CheckedMajor = 5, LastSeen = Now };
        var other = new StoredState { Key = "SLUICE-OTHR-OTHR-OTHR-OTHR", ActivationId = "act_2", Lease = "theirs", LastSeen = Now };
        swap = () =>
        {
            swap = () => { };
            h.Stored = other;
        };
        h.Http.Fail = true;

        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal("act_2", h.Stored!.ActivationId);
        Assert.Null(h.Stored.KeptMajor);
    }

    [Fact]
    public async Task Starts_a_key_s_notes_afresh_on_activate_and_drops_them_on_deactivate()
    {
        var h = Create(new StoredState { Key = "OLD-KEY", ActivationId = "act_old", KeptMajor = 7, CheckedMajor = 7 }, new Options { Major = 2 });
        h.Http.Lease("activate", OpenLease(h, Now + 10 * Day));
        await h.License.ActivateAsync(Key);
        Assert.Equal((Key, 2, 2), (h.Stored!.Key, h.Stored.CheckedMajor, h.Stored.KeptMajor));
        h.Http.Answer("deactivate", 200, new System.Text.Json.Nodes.JsonObject { ["ok"] = true });
        await h.License.DeactivateAsync();
        Assert.Null(h.Stored!.KeptMajor);
        Assert.Null(h.Stored.CheckedMajor);
    }

    // --- the check of a newer major: once, bounded, and backing off --------------

    [Theory]
    [InlineData("revoked", LicenseReason.Revoked)]
    [InlineData("expired", LicenseReason.Revoked)]
    [InlineData("deactivated", LicenseReason.Revoked)]
    [InlineData("upgrade-required", LicenseReason.Upgrade)]
    [InlineData("update-required", LicenseReason.Outdated)]
    public async Task Records_the_major_as_heard_on_a_refusal_keeps_the_refusal_and_does_not_ask_again(string error, LicenseReason reason)
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, error);
        Assert.Equal(Invalid(reason), await h.License.StatusAsync());
        Assert.Equal((Key, "act_1", 3), (h.Stored!.Key, h.Stored.ActivationId, h.Stored.CheckedMajor));
        Assert.Equal(reason.ToString(), h.Stored.Refused.ToString());
        Assert.Equal(Invalid(reason), await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("deactivated")]
    public async Task Drops_the_lease_on_a_verdict_about_the_key(string error)
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, error);
        await h.License.StatusAsync();
        Assert.Null(h.Stored!.Lease);
        Assert.Null(h.Stored.RefusedMajor);
    }

    [Theory]
    [InlineData("upgrade-required", Refusal.Upgrade)]
    [InlineData("update-required", Refusal.Outdated)]
    public async Task Keeps_the_lease_on_a_verdict_about_this_build_with_the_major_refused(string error, Refusal refusal)
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, error);
        await h.License.StatusAsync();
        Assert.Equal((refusal, 3), (h.Stored!.Refused, h.Stored.RefusedMajor));
        Assert.NotNull(h.Stored.Lease);
    }

    [Fact]
    public async Task Lets_a_rollback_to_a_covered_build_run_offline_after_its_newer_build_was_refused_an_upgrade()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, "upgrade-required");
        Assert.Equal(Invalid(LicenseReason.Upgrade), await h.LicenseFor(4).StatusAsync());
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Licensed, (await h.LicenseFor(3).StatusAsync()).State);
        Assert.Equal(Invalid(LicenseReason.Upgrade), await h.LicenseFor(4).StatusAsync());
        Assert.Equal(Invalid(LicenseReason.Upgrade), await h.LicenseFor(5).StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Lets_an_update_to_a_covered_build_run_offline_after_its_older_build_was_refused_as_out_of_date()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, "update-required");
        Assert.Equal(Invalid(LicenseReason.Outdated), await h.License.StatusAsync());
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Outdated), await h.LicenseFor(3).StatusAsync());
        Assert.Equal(LicenseState.Licensed, (await h.LicenseFor(4).StatusAsync()).State);
        Assert.Equal(Invalid(LicenseReason.Outdated), await h.LicenseFor(2).StatusAsync());
    }

    [Fact]
    public async Task Keeps_a_cancelled_subscriber_on_a_new_major_refused_offline_included()
    {
        var h = Create(3);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h, model: "subscription", ttl: 7 * 24 * 3600), CheckedMajor = 2, LastSeen = Now };
        h.Http.Error("validate", 403, "expired");
        Assert.Equal(Invalid(LicenseReason.Revoked), await h.License.StatusAsync());
        Assert.Equal(Invalid(LicenseReason.Revoked), await h.License.StatusAsync());
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Revoked), await h.License.StatusAsync());
        Assert.Equal(Invalid(LicenseReason.Revoked), await h.License.RefreshAsync());
    }

    [Fact]
    public async Task Heals_a_refused_key_when_the_server_licenses_it_again_and_forgets_the_refusal()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, "expired");
        Assert.Equal(LicenseState.Invalid, (await h.License.StatusAsync()).State);
        h.SetClock(Now + Offline.AskAgainMs);
        h.Http.Lease("validate", OpenLease(h));
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Null(h.Stored!.Refused);
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Keeps_a_lease_an_ambiguous_answer_comes_back_on_and_records_no_refusal()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 404, "unknown-product");
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Null(h.Stored!.Refused);
        Assert.NotNull(h.Stored.Lease);
    }

    [Fact]
    public async Task Starts_afresh_on_a_new_activation_after_a_refusal_and_on_a_deactivate()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, "deactivated");
        Assert.Equal(LicenseState.Invalid, (await h.License.StatusAsync()).State);
        h.Http.Lease("activate", h.SignLease(new LeaseInput { Sub = Key, Aid = "act_2", Major = OpenMajor }), "act_2");
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Null(h.Stored!.Refused);
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);

        h.Stored = h.Stored! with { Refused = Refusal.Revoked };
        await h.License.DeactivateAsync();
        Assert.Null(h.Stored!.Refused);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Asks_a_refused_key_again_only_once_the_wait_is_over_and_at_once_on_a_refresh()
    {
        var h = OnNewMajor();
        h.Http.Error("validate", 403, "revoked");
        Assert.Equal(LicenseState.Invalid, (await h.License.StatusAsync()).State);
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.AskAgainMs - 1);
        Assert.Equal(Invalid(LicenseReason.Revoked), await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(LicenseState.Invalid, (await h.License.RefreshAsync()).State);
        Assert.Equal(2, h.Http.Count("validate"));
        // The refresh's refusal starts the wait afresh.
        h.SetClock(Now + Offline.AskAgainMs - 1 + Offline.AskAgainMs - 1);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
        h.SetClock(Now + Offline.AskAgainMs - 1 + Offline.AskAgainMs);
        await h.License.StatusAsync();
        Assert.Equal(3, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Asks_for_an_expired_lease_again_only_once_the_wait_is_over_and_at_once_on_a_refresh()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", 3600, Now - 2 * Day), LastSeen = Now };
        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await h.License.StatusAsync());
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        h.SetClock(Now + Offline.RetryUnansweredMs - 1);
        Assert.Equal(LicenseState.Expired, (await h.License.StatusAsync()).State);
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
        await h.License.RefreshAsync();
        Assert.Equal(3, h.Http.Count("validate"));
        // A lease from the server ends the wait.
        h.Http.Fail = false;
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600, Now + Offline.RetryUnansweredMs));
        Assert.Equal(LicenseState.Licensed, (await h.License.RefreshAsync()).State);
        Assert.Null(h.Stored!.BackoffSince);
        Assert.Null(h.Stored.BackoffMs);
    }

    [Fact]
    public async Task Serialises_overlapping_status_calls_one_question_and_the_refusal_survives()
    {
        var h = OnNewMajor();
        var answer = new TaskCompletionSource<HttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        h.Http.On("validate", _ => Interlocked.Increment(ref asked) > 1
            ? Task.FromResult(new HttpResult(503, new System.Text.Json.Nodes.JsonObject { ["error"] = "busy" }))
            : answer.Task);
        var first = h.License.StatusAsync();
        var second = h.License.StatusAsync();
        await Eventually.Until(() => Volatile.Read(ref asked) > 0, "the first ask");
        answer.SetResult(new HttpResult(403, new System.Text.Json.Nodes.JsonObject { ["error"] = "revoked" }));
        Assert.Equal(Invalid(LicenseReason.Revoked), await first);
        Assert.Equal(Invalid(LicenseReason.Revoked), await second);
        Assert.Equal(1, asked);
        Assert.Equal((Refusal.Revoked, 3), (h.Stored!.Refused, h.Stored.CheckedMajor));
        Assert.Null(h.Stored.Lease);
    }

    [Fact]
    public async Task Records_the_major_as_heard_on_an_answer_it_cannot_use_and_does_not_ask_again()
    {
        var h = OnNewMajor();
        h.Http.Lease("validate", "not.a.lease");
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(3, h.Stored!.CheckedMajor);
        // The server DID answer: the hour's wait, not the minute's.
        Assert.Equal((Now, Offline.AskAgainMs), (h.Stored.BackoffSince, h.Stored.BackoffMs));
    }

    [Fact]
    public async Task Waits_the_hour_after_a_200_with_no_usable_lease_not_the_minute()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", 3600, Now - 2 * Day), LastSeen = Now };
        h.Http.Answer("validate", 200, new System.Text.Json.Nodes.JsonObject { ["lease"] = "not.a.lease" });
        Assert.Equal(LicenseState.Expired, (await h.License.StatusAsync()).State);
        Assert.Equal((Now, Offline.AskAgainMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.AskAgainMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Backs_off_after_no_answer_and_asks_again_once_the_interval_has_passed()
    {
        var h = OnNewMajor();
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal((2, Now, Offline.RetryUnansweredMs), (h.Stored!.CheckedMajor, h.Stored.BackoffSince, h.Stored.BackoffMs));
        h.SetClock(Now + Offline.RetryUnansweredMs - 1);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    public async Task Treats_an_outage_as_no_verdict_keeps_the_lease_and_waits_the_hour(int status)
    {
        var h = OnNewMajor();
        h.Http.Error("validate", status, "busy");
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal((2, Now, Offline.AskAgainMs), (h.Stored!.CheckedMajor, h.Stored.BackoffSince, h.Stored.BackoffMs));
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.AskAgainMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Returns_the_valid_lease_when_the_network_never_answers_after_at_most_the_check_s_bound()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Major = 3, Time = time });
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = OpenLease(h), CheckedMajor = 2, LastSeen = Now };
        h.Http.Hang = true;
        var pending = h.License.StatusAsync();
        await Eventually.Until(() => h.Http.Called("validate") && time.HasTimerDueIn(TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs)), "the bounded check");
        time.Advance(TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs - 1));
        await Eventually.Quiet();
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Licensed, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
    }
}
