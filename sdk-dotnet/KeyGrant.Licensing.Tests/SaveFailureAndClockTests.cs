using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>A save that fails once the server has answered; the waits and early renewal; the clock guard.</summary>
public class SaveFailureAndClockTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };
    private static readonly LicenseStatus Revoked = new() { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key };

    /// <summary>The harness device's claim: a trial lease names its device by it (sub <c>trial:&lt;claim&gt;</c>, aid the claim).</summary>
    private static readonly string Claim = Hashes.FingerprintClaim("fp-test");

    private static StoredState Holding(string lease, long lastSeen = Now) =>
        new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 1, LastSeen = lastSeen };

    /// <summary>A trial lease as the server signs one: to the trial's own end, <paramref name="days"/> from now.</summary>
    private static string TrialLease(Harness h, int days = 5) =>
        h.SignLease(new LeaseInput { Sub = $"trial:{Claim}", Aid = Claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = days * 24 * 3600 });

    private static Harness TrialServer(Harness h)
    {
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now - 2 * Day, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = TrialLease(h) });
        return h;
    }

    // --- a save that fails once the server has answered ------------------------

    [Fact]
    public async Task Is_the_server_s_refusal_all_the_same_the_stored_lease_untouched()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(lease);
        h.Http.Error("validate", 403, "revoked");
        h.FailWrites();
        Assert.Equal(Revoked, await h.License.RefreshAsync());
        Assert.True(h.Writes > 0);
        Assert.Equal(lease, h.Stored!.Lease);
    }

    [Fact]
    public async Task Is_the_server_s_renewal_all_the_same_for_a_lease_that_had_run_out_and_saved_by_the_next_call()
    {
        var h = Create();
        var old = h.Sign(Key, "perpetual", 3600, Now - 2 * Day);
        var fresh = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(old);
        h.Http.Lease("validate", fresh);
        h.FailWrites();
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(old, h.Stored!.Lease);
        h.FailWrites(false);
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Fact]
    public async Task Is_no_answer_at_all_rather_than_a_throw_when_nothing_answered_either()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        h.Http.Fail = true;
        h.FailWrites();
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Is_the_server_s_trial_all_the_same_from_start_trial()
    {
        var h = TrialServer(Create());
        h.FailWrites();
        var status = await h.License.StartTrialAsync();
        Assert.Equal((LicenseState.Trial, 5), (status.State, status.TrialDaysLeft));
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Still_fails_deactivate_which_the_customer_asked_for_the_key_kept_to_try_again()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(lease);
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        h.FailWrites();
        await Assert.ThrowsAsync<DiskSaidNoException>(() => h.License.DeactivateAsync());
        Assert.Equal((Key, lease), (h.Stored!.Key, h.Stored.Lease));
    }

    [Fact]
    public async Task Still_fails_activate_which_the_customer_asked_for_and_can_try_again()
    {
        var h = Create();
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.FailWrites();
        await Assert.ThrowsAsync<DiskSaidNoException>(() => h.License.ActivateAsync(Key));
    }

    [Fact]
    public async Task Keeps_a_trial_it_could_not_save_for_the_process_answers_it_and_saves_it_once_it_can()
    {
        var h = TrialServer(Create());
        h.FailWrites();
        Assert.Equal(5, (await h.License.StartTrialAsync()).TrialDaysLeft);
        // The next status reads no trial on the disk, and answers the one it holds.
        Assert.Equal((LicenseState.Trial, 5), await StateAndDays(h.License.StatusAsync()));
        Assert.Null(h.Stored);

        h.FailWrites(false);
        Assert.Equal((LicenseState.Trial, 5), await StateAndDays(h.License.StatusAsync()));
        // Ed25519 signs deterministically: the same claims, the same lease the server gave.
        Assert.Equal((Now - 2 * Day, Now + 5 * Day, TrialLease(h)), (h.Stored!.TrialStart, h.Stored.TrialEndsAt, h.Stored.TrialLease));
        // Saved once: not written again by every status after it.
        var writes = h.Writes;
        await h.License.StatusAsync();
        Assert.Equal(writes, h.Writes);
    }

    [Fact]
    public async Task Never_moves_a_trial_start_later_when_it_saves_the_trial_it_kept()
    {
        var h = TrialServer(Create());
        h.FailWrites();
        await h.License.StartTrialAsync();
        // Meanwhile another instance of the app saved an earlier start.
        h.Stored = new StoredState { TrialStart = Now - 9 * Day };
        h.FailWrites(false);
        await h.License.StatusAsync();
        Assert.Equal((Now - 9 * Day, Now + 5 * Day), (h.Stored!.TrialStart, h.Stored.TrialEndsAt));
    }

    /// <summary>startTrial asked at NOW, its answer (ending NOW + 5 days) kept unsaved; then another instance saves <paramref name="theirs"/>.</summary>
    private static async Task<Harness> KeptThen(StoredState theirs, bool stillFailing = false, Func<Harness, string>? lease = null)
    {
        var h = TrialServer(Create());
        h.FailWrites();
        Assert.Equal(5, (await h.License.StartTrialAsync()).TrialDaysLeft);
        h.Stored = lease is null ? theirs : theirs with { TrialLease = lease(h) };
        h.FailWrites(stillFailing);
        return h;
    }

    [Fact]
    public async Task A_kept_trial_never_makes_the_trial_longer_than_a_newer_answer_says()
    {
        var h = await KeptThen(new StoredState { TrialStart = Now - 9 * Day, TrialEndsAt = Now - 2 * Day, TrialAt = Now + 1 });
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, await h.License.StatusAsync());
        Assert.Equal((Now - 9 * Day, Now - 2 * Day, Now + 1), (h.Stored!.TrialStart, h.Stored.TrialEndsAt, h.Stored.TrialAt));

        var failing = await KeptThen(new StoredState { TrialStart = Now - 9 * Day, TrialEndsAt = Now - 2 * Day, TrialAt = Now + 1 }, stillFailing: true);
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, await failing.License.StatusAsync());
    }

    [Fact]
    public async Task A_kept_trial_takes_a_newer_answer_s_later_end_too_a_trial_the_server_extended()
    {
        // The answer the other instance had came with a lease signed to the new end.
        var h = await KeptThen(new StoredState { TrialStart = Now - 9 * Day, TrialEndsAt = Now + 20 * Day, TrialAt = Now + 1 }, lease: x => TrialLease(x, 20));
        Assert.Equal((LicenseState.Trial, 20), await StateAndDays(h.License.StatusAsync()));
        Assert.Equal(Now + 20 * Day, h.Stored!.TrialEndsAt);
    }

    [Fact]
    public async Task A_kept_trial_wins_over_a_stored_trial_with_no_stamp()
    {
        var h = await KeptThen(new StoredState { TrialStart = Now - 9 * Day, TrialEndsAt = Now - 2 * Day });
        Assert.Equal((LicenseState.Trial, 5), await StateAndDays(h.License.StatusAsync()));
        Assert.Equal((Now - 9 * Day, Now + 5 * Day, Now), (h.Stored!.TrialStart, h.Stored.TrialEndsAt, h.Stored.TrialAt));
    }

    [Fact]
    public async Task A_kept_trial_is_saved_after_a_deactivate_or_another_key_s_activation_the_licence_as_that_left_it()
    {
        var deactivated = await KeptThen(new StoredState { TrialStart = Now - 9 * Day });
        await deactivated.License.StatusAsync();
        Assert.Equal((Now - 9 * Day, Now + 5 * Day, Now, (string?)null), (deactivated.Stored!.TrialStart, deactivated.Stored.TrialEndsAt, deactivated.Stored.TrialAt, deactivated.Stored.Key));

        var other = await KeptThen(new StoredState { Key = "SLUICE-BBBB-BBBB-BBBB-BBBB", ActivationId = "act_2", Lease = "LB", CheckedMajor = 1 });
        await other.License.StatusAsync();
        var stored = other.Stored!;
        Assert.Equal(("SLUICE-BBBB-BBBB-BBBB-BBBB", "act_2", "LB", 1), (stored.Key, stored.ActivationId, stored.Lease, stored.CheckedMajor));
        Assert.Equal((Now - 2 * Day, Now + 5 * Day, Now), (stored.TrialStart, stored.TrialEndsAt, stored.TrialAt));
    }

    private static async Task<(LicenseState, int?)> StateAndDays(Task<LicenseStatus> status)
    {
        var s = await status;
        return (s.State, s.TrialDaysLeft);
    }

    // --- the waits and early renewal --------------------------------------------

    [Fact]
    public async Task Heals_a_lease_that_ran_out_offline_within_the_short_wait_once_the_network_is_back()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Expired, (await h.License.StatusAsync()).State);
        h.Http.Fail = false;
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.SetClock(Now + Offline.RetryUnansweredMs - 1);
        Assert.Equal(LicenseState.Expired, (await h.License.StatusAsync()).State);
        h.SetClock(Now + Offline.RetryUnansweredMs);
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Still_waits_the_hour_after_a_refusal()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        h.Http.Error("validate", 403, "revoked");
        Assert.Equal(LicenseReason.Revoked, (await h.License.StatusAsync()).Reason);
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Offline.AskAgainMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
    }

    [Theory]
    [InlineData("a stored refusal")]
    [InlineData("a lease past its end")]
    [InlineData("a valid lease near its end")]
    public async Task Times_the_wait_before_asking_again_by_the_device_clock_not_the_guard(string held)
    {
        // The guard (lastSeen) is a day ahead of the device clock, and the last ask was half an hour ago
        // by the device clock: its hour's wait is not over, though by the guard it would be.
        var h = Create();
        var stored = held switch
        {
            "a stored refusal" => new StoredState { Key = Key, ActivationId = "act_1", Refused = Refusal.Revoked },
            "a lease past its end" => Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day)),
            _ => Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600, Now - 26 * Day)),
        };
        h.Stored = stored with { BackoffSince = Now - 30 * 60_000, BackoffMs = Offline.AskAgainMs, LastSeen = Now + Day };
        h.Http.Fail = true;
        await h.License.StatusAsync();
        Assert.False(h.Http.Called("validate"));
    }

    [Fact]
    public async Task Starts_the_wait_after_a_renewal_by_the_device_clock_not_the_guard()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day), lastSeen: Now + Day);
        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await h.License.StatusAsync());
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
    }

    [Fact]
    public async Task Renews_a_lease_early_once_within_RENEW_WITHIN_MS_of_its_end_and_not_before()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var renewed = h.Sign(Key, "perpetual", 30 * 24 * 3600, Now + 30 * Day - Offline.RenewWithinMs);
        h.Http.Lease("validate", renewed);
        h.SetClock(Now + 30 * Day - Offline.RenewWithinMs - 1);
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal(0, h.Http.Count("validate"));
        h.SetClock(Now + 30 * Day - Offline.RenewWithinMs + 1);
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(renewed, h.Stored!.Lease);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Renews_a_short_lease_at_half_its_life_not_on_every_status()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "subscription", 7 * 24 * 3600));
        h.Http.Fail = true;
        h.SetClock(Now + (long)(3.5 * Day) - 1);
        await h.License.StatusAsync();
        Assert.Equal(0, h.Http.Count("validate"));
        h.SetClock(Now + (long)(3.5 * Day) + 1);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Keeps_the_valid_lease_when_an_early_renewal_gets_nothing_and_waits_before_trying_again()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(lease);
        h.Http.Fail = true;
        var at = Now + 25 * Day;
        h.SetClock(at);
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal((lease, at, Offline.RetryUnansweredMs), (h.Stored!.Lease, h.Stored.BackoffSince, h.Stored.BackoffMs));
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Answers_a_refusal_to_an_early_renewal()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "subscription", 7 * 24 * 3600));
        h.Http.Error("validate", 403, "expired");
        h.SetClock(Now + 5 * Day);
        Assert.Equal(Revoked, await h.License.StatusAsync());
    }

    // --- the clock guard ---------------------------------------------------------

    [Fact]
    public async Task Licenses_again_online_after_a_clock_that_jumped_60_days_ahead_is_put_right()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600, Now - Day), lastSeen: Now + 60 * Day);
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(Now, h.Stored!.LastSeen);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Stamps_an_activation_and_a_deactivation_with_their_time()
    {
        var h = Create();
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        await h.License.ActivateAsync(Key);
        Assert.Equal(Now, h.Stored!.VerdictAt);
        h.SetClock(Now + Day);
        await h.License.DeactivateAsync();
        Assert.Equal(Now + Day, h.Stored!.VerdictAt);
    }

    [Fact]
    public async Task Stamps_by_the_guarded_clock_never_earlier_than_the_last_time_seen()
    {
        var h = TrialServer(Create(new StoredState { LastSeen = Now + 5 * 60_000 }));
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        await h.License.ActivateAsync(Key);
        Assert.Equal(Now + 5 * 60_000, h.Stored!.VerdictAt);
        h.SetClock(Now - Day);
        await h.License.DeactivateAsync();
        Assert.Equal(Now + 5 * 60_000, h.Stored!.VerdictAt);
        await h.License.StartTrialAsync();
        Assert.Equal(Now + 5 * 60_000, h.Stored!.TrialAt);
    }

    [Fact]
    public async Task Saves_its_own_answers_after_the_clock_is_put_back()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600, Now - Day)) with { VerdictAt = Now + 60 * Day };
        h.Http.Error("validate", 403, "revoked");
        Assert.Equal(Revoked, await h.License.RefreshAsync());
        Assert.Equal((Refusal.Revoked, Now), (h.Stored!.Refused, h.Stored.VerdictAt));
        Assert.Null(h.Stored.Lease);
    }

    [Fact]
    public async Task Licenses_from_the_server_s_lease_when_the_guard_it_puts_right_cannot_be_saved()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600, Now - Day), lastSeen: Now + 60 * Day);
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.FailWrites();
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(Now + 60 * Day, h.Stored!.LastSeen);
    }

    /// <summary>The guard after a refresh answered with a fresh lease signed at NOW, the clock at <paramref name="clock"/>.</summary>
    private static async Task<long?> GuardAfterRefresh(long guard, long clock = Now)
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(lease, guard);
        h.SetClock(clock);
        h.Http.Lease("validate", lease);
        Assert.Equal(LicenseState.Licensed, (await h.License.RefreshAsync()).State);
        return h.Stored!.LastSeen;
    }

    [Fact]
    public async Task Leaves_the_guard_where_it_is_for_a_fresh_lease_within_the_tolerance()
    {
        Assert.Equal(Now + 30 * 60_000, await GuardAfterRefresh(Now + 30 * 60_000));
        Assert.Equal(Now + Offline.ClockToleranceMs, await GuardAfterRefresh(Now + Offline.ClockToleranceMs));
    }

    [Fact]
    public async Task Brings_the_guard_back_for_a_fresh_lease_signed_1_ms_past_the_tolerance()
    {
        Assert.Equal(Now, await GuardAfterRefresh(Now + Offline.ClockToleranceMs + 1));
    }

    [Fact]
    public async Task Brings_the_guard_back_to_the_lease_s_signing_time_not_to_a_device_clock_behind_it()
    {
        Assert.Equal(Now, await GuardAfterRefresh(Now + 60 * Day, Now - 2 * 60 * 60_000));
    }

    [Fact]
    public async Task Brings_the_guard_back_for_a_lease_naming_the_key_in_another_case()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600), lastSeen: Now + 60 * Day);
        h.Http.Lease("validate", h.Sign("Sluice-aaaa-BBBB-CCCC-DDDD", "perpetual", 30 * 24 * 3600));
        await h.License.RefreshAsync();
        Assert.Equal(Now, h.Stored!.LastSeen);
    }

    [Theory]
    [InlineData("SLUICE-OTHR-OTHR-OTHR-OTHR", "act_1", 0)]
    [InlineData(Key, "act_2", 0)]
    [InlineData(Key, "act_1", -Day)]
    public async Task Moves_no_guard_for_a_lease_signed_for_another_key_activation_or_older_than_the_one_held(string sub, string aid, long offset)
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600), lastSeen: Now + 60 * Day);
        h.Http.Lease("validate", h.SignLease(new LeaseInput { Sub = sub, Aid = aid }, Now + offset), aid);
        await h.License.RefreshAsync();
        Assert.Equal(Now + 60 * Day, h.Stored!.LastSeen);
    }
}
