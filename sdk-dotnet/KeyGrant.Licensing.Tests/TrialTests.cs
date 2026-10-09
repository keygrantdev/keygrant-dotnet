using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

public class TrialTests
{
    /// <summary>The harness device's claim: a trial lease names its device by it (sub <c>trial:&lt;claim&gt;</c>, aid the claim).</summary>
    private static readonly string Claim = Hashes.FingerprintClaim("fp-test");

    /// <summary>A trial lease as the server signs one: to the trial's own end (<paramref name="endsAt"/>, ms).</summary>
    private static string TrialLease(Harness h, long endsAt = Now + 7 * Day, long now = Now) =>
        h.SignLease(new LeaseInput { Sub = $"trial:{Claim}", Aid = Claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = (endsAt - now) / 1000 }, now);

    private static void TrialAnswer(Harness h, long start, long ends, string? lease)
    {
        var body = new JsonObject { ["trialStart"] = start, ["trialEndsAt"] = ends };
        if (lease is not null) body["lease"] = lease;
        h.Http.Answer("trial", 200, body);
    }

    [Fact]
    public async Task Starts_a_trial_verifies_the_lease_and_stashes_the_start()
    {
        var h = Create();
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, TrialLease(h, Now + 5 * Day));
        var status = await h.License.StartTrialAsync();
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 }, status);
        Assert.Equal(Now - 2 * Day, h.Stored!.TrialStart);
        Assert.Equal(Now, h.Stored.TrialAt);
        Assert.Equal(Now - 2 * Day, h.Registry);
        // The reference's body: the fingerprint, no kind for a GetAsync-only fingerprinter, no clientStart with no evidence.
        AssertJson.Equal(new JsonObject { ["fingerprint"] = "fp-test" }, h.Http.BodyOf("trial"));
    }

    [Fact]
    public async Task Never_moves_the_trial_start_forward_anti_reset()
    {
        var h = Create(new StoredState { TrialStart = Now - 10 * Day });
        h.Registry = Now - 8 * Day;
        TrialAnswer(h, Now - 2 * Day, Now + 4 * Day, TrialLease(h, Now + 4 * Day));
        await h.License.StartTrialAsync();
        Assert.Equal(Now - 10 * Day, h.Stored!.TrialStart);
        Assert.Equal(Now - 10 * Day, h.Registry);
        Assert.Equal(Now - 10 * Day, h.Http.BodyOf("trial")["clientStart"]!.GetValue<long>());
    }

    [Fact]
    public async Task Waits_the_activation_bound_for_the_machine_id_when_a_trial_is_started_not_the_2_s_a_status_gets()
    {
        var time = new ManualTime();
        var hanging = new FuncFingerprinter(() => new TaskCompletionSource<string>().Task);
        var h = Create(null, new Options { Fingerprint = hanging, Time = time });
        var bound = TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs);
        var starting = h.License.StartTrialAsync();
        await Eventually.Until(() => Volatile.Read(ref hanging.Calls) == 1 && time.HasTimerDueIn(bound), "the bounded machine-id read");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(starting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        // This device could not be told: no trial is asked for, and none is held.
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await starting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Answers_unlicensed_for_a_trial_lease_stored_without_a_start_asking_nothing()
    {
        // Only a stored start is a trial this device began; no save leaves a lease or an end without one.
        var h = Create();
        h.Stored = new StoredState { TrialEndsAt = Now + 5 * Day, TrialLease = TrialLease(h, Now + 5 * Day), LastSeen = Now };
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Restores_the_earliest_start_from_the_registry_when_local_storage_was_wiped()
    {
        var h = Create();
        h.Registry = Now - 10 * Day;
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, TrialLease(h, Now + 5 * Day));
        await h.License.StartTrialAsync();
        Assert.Equal(Now - 10 * Day, h.Stored!.TrialStart);
        Assert.Equal(Now - 10 * Day, h.Http.BodyOf("trial")["clientStart"]!.GetValue<long>());
    }

    [Fact]
    public async Task Falls_back_to_local_trial_evidence_when_offline_or_refused()
    {
        var offline = Create();
        offline.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = TrialLease(offline, Now + 6 * Day), LastSeen = Now };
        offline.Http.Fail = true;
        Assert.Equal(LicenseState.Trial, (await offline.License.StartTrialAsync()).State);

        var refused = Create();
        refused.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = TrialLease(refused, Now + 6 * Day), LastSeen = Now };
        refused.Http.Error("trial", 400, "bad");
        Assert.Equal(LicenseState.Trial, (await refused.License.StartTrialAsync()).State);
    }

    [Fact]
    public async Task Ignores_a_malformed_trial_body()
    {
        var h = Create();
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = "oops", ["lease"] = "x" });
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StartTrialAsync());
        Assert.Null(h.Stored?.TrialStart);
    }

    private static readonly LicenseStatus TrialOver = new() { State = LicenseState.Expired, Reason = LicenseReason.Trial };

    [Fact]
    public async Task Runs_no_trial_on_a_stored_end_alone_unsigned_it_could_be_edited_into_any_trial()
    {
        var h = Create(new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day });
        Assert.Equal(TrialOver, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Ends_a_trial_when_its_signed_lease_ends_whatever_end_is_stored_beside_it()
    {
        // Someone edited the stored end a year out: the end shown is the lease's, the one signed.
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 5 * Day, TrialEndsAt = Now + 365 * Day, TrialLease = TrialLease(h, Now + 2 * Day), LastSeen = Now };
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 2 * Day, TrialDaysLeft = 2 }, await h.License.StatusAsync());

        // And the same file, read once the lease has ended: the edit buys nothing.
        h.SetClock(Now + 2 * Day + 1000);
        Assert.Equal(TrialOver, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Reports_expired_once_the_trial_window_has_passed()
    {
        var h = Create(new StoredState { TrialStart = Now - 10 * Day, TrialEndsAt = Now - Day });
        Assert.Equal(TrialOver, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Takes_the_trial_s_end_from_its_lease_when_none_is_stored()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialLease = TrialLease(h, Now + 7 * Day), LastSeen = Now };
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 7 * Day, TrialDaysLeft = 7 }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Keeps_a_14_day_trial_live_on_day_10_offline_on_a_lease_signed_to_its_end_with_no_renewal()
    {
        var h = Create();
        TrialAnswer(h, Now, Now + 14 * Day, TrialLease(h, Now + 14 * Day));
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 14 * Day, TrialDaysLeft = 14 }, await h.License.StartTrialAsync());

        // Ten days on, offline: the lease the trial started with still runs it, to its end. The only
        // request is the day's launch report, which fails offline and changes nothing.
        h.Http.Fail = true;
        h.SetClock(Now + 10 * Day);
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 14 * Day, TrialDaysLeft = 4 }, await h.License.StatusAsync());
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, h.Http.Count("trial"));
        Assert.Equal(2, h.Http.Calls.Count);

        h.SetClock(Now + 14 * Day + 1);
        Assert.Equal(TrialOver, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Keeps_the_trial_lease_it_can_verify_when_the_server_answers_with_one_it_cannot()
    {
        // The product's key rotated ahead of this build: every answer is signed with a key the build
        // does not carry. Taking it would end the running trial at the next sync.
        var h = Create();
        var ours = TrialLease(h, Now + 5 * Day);
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = ours, LastSeen = Now };
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, TrialLease(Create(), Now + 5 * Day));

        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 }, await h.License.StartTrialAsync());
        Assert.Equal(ours, h.Stored!.TrialLease);
    }

    [Fact]
    public async Task Takes_a_valid_trial_lease_the_server_answers_with_over_the_valid_one_stored()
    {
        // An extension, or a lease carrying new entitlements: the server's word wins whenever its lease
        // verifies here.
        var h = Create();
        var ours = TrialLease(h, Now + 3 * Day);
        h.Stored = new StoredState { TrialStart = Now - 4 * Day, TrialEndsAt = Now + 3 * Day, TrialLease = ours, LastSeen = Now };
        var extended = h.SignLease(new LeaseInput
        {
            Sub = $"trial:{Claim}",
            Aid = Claim,
            Model = "trial",
            Lim = 1,
            Major = 1_000_000,
            Ent = new JsonObject { ["edition"] = "pro" },
            TtlSeconds = 6 * 24 * 3600,
        });
        TrialAnswer(h, Now - 4 * Day, Now + 6 * Day, extended);

        Assert.Equal(
            new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 6 * Day, TrialDaysLeft = 6, Entitlements = new Dictionary<string, Entitlement> { ["edition"] = new("pro") } },
            await h.License.StartTrialAsync());
        Assert.Equal(extended, h.Stored!.TrialLease);
    }

    [Fact]
    public async Task Takes_the_server_s_word_when_it_says_the_trial_is_over_whatever_lease_is_held()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = TrialLease(h, Now + 5 * Day), LastSeen = Now };
        TrialAnswer(h, Now - 9 * Day, Now - 2 * Day, null);

        Assert.Equal(TrialOver, await h.License.StartTrialAsync());
        // An answer with no lease is taken as it is: the lease held goes with the trial.
        Assert.Null(h.Stored!.TrialLease);
    }

    // --- a trial whose lease does not verify while its stored end is ahead --------------

    /// <summary>A trial stored with a lease signed by another key: one the product signed before a rotation.</summary>
    private static Harness Rotated(Func<StoredState, StoredState>? change = null, Options? options = null)
    {
        var h = Create(null, options);
        var stale = Create().TrialLease(Now + 5 * Day);
        var stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = stale, LastSeen = Now };
        h.Stored = change is null ? stored : change(stored);
        return h;
    }

    [Fact]
    public async Task Asks_for_the_trial_again_and_the_server_s_fresh_lease_runs_it()
    {
        var h = Rotated();
        var fresh = h.TrialLease(Now + 5 * Day);
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, fresh);

        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 }, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("trial"));
        // Asked as StartTrialAsync asks, and saved as it saves.
        AssertJson.Equal(new JsonObject { ["fingerprint"] = "fp-test", ["clientStart"] = Now - 2 * Day }, h.Http.BodyOf("trial"));
        Assert.Equal((fresh, Now), (h.Stored!.TrialLease, h.Stored.TrialAt));
        // Healed: the next status needs no ask.
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("trial"));
    }

    [Fact]
    public async Task Heals_a_trial_lease_signed_short_of_the_trial_s_end()
    {
        // Seven days on a fourteen-day trial.
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now, TrialEndsAt = Now + 14 * Day, TrialLease = h.TrialLease(Now + 7 * Day), LastSeen = Now };
        h.SetClock(Now + 8 * Day);
        TrialAnswer(h, Now, Now + 14 * Day, h.TrialLease(Now + 14 * Day, Now + 8 * Day));

        var status = await h.License.StatusAsync();
        Assert.Equal((LicenseState.Trial, 6), (status.State, status.TrialDaysLeft));
    }

    [Fact]
    public async Task Answers_expired_and_waits_a_minute_when_the_server_cannot_be_reached()
    {
        var h = Rotated();
        h.Http.Fail = true;

        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        // Within the wait, no ask: a status offline does not pay a timeout each time.
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("trial"));
        h.SetClock(Now + Offline.RetryUnansweredMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("trial"));
        // A refresh asks whatever the wait says.
        await h.License.RefreshAsync();
        Assert.Equal(3, h.Http.Count("trial"));
    }

    [Fact]
    public async Task Waits_an_hour_when_the_server_answers_with_nothing_this_build_can_verify()
    {
        // A build carrying a key the product no longer signs with: the answer cannot verify either. It is
        // signed with another foreign key than the stored lease, so the two differ byte for byte.
        var h = Rotated();
        var stale = h.Stored!.TrialLease;
        var stillStale = Create().TrialLease(Now + 5 * Day);
        Assert.NotEqual(stale, stillStale);
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, stillStale);

        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.Equal((Now, Offline.AskAgainMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        // Neither verifies, so nothing is kept over the server's: its lease is the one stored.
        Assert.Equal(stillStale, h.Stored.TrialLease);
        h.SetClock(Now + Offline.AskAgainMs - 1);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("trial"));
    }

    [Fact]
    public async Task Takes_the_server_s_word_that_the_trial_is_over()
    {
        var h = Rotated();
        TrialAnswer(h, Now - 9 * Day, Now - 2 * Day, null);

        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.Equal((Now - 9 * Day, Now - 2 * Day), (h.Stored!.TrialStart, h.Stored.TrialEndsAt));
    }

    [Fact]
    public async Task Records_no_wait_after_an_ask_that_brings_the_trial_back()
    {
        var h = Rotated();
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, h.TrialLease(Now + 5 * Day));
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
        Assert.Equal(((long?)null, (long?)null), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
    }

    [Fact]
    public async Task Times_the_wait_by_the_device_clock_not_the_clock_guard()
    {
        // The guard a day ahead of the device clock (it was put back): the wait starts at the device
        // clock, as AskDue reads it, so a status within the minute asks nothing.
        var h = Rotated(s => s with { LastSeen = Now + Day });
        h.Http.Fail = true;
        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        h.SetClock(Now + 30_000);
        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("trial"));
    }

    [Fact]
    public async Task Asks_again_inside_the_wait_when_refreshed()
    {
        var h = Rotated(s => s with { BackoffSince = Now - 60_000, BackoffMs = Offline.AskAgainMs });
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, h.TrialLease(Now + 5 * Day));

        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.False(h.Http.Called("trial"));
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 }, await h.License.RefreshAsync());
    }

    [Fact]
    public async Task Asks_nothing_when_the_stored_end_is_exactly_now()
    {
        var h = Rotated(s => s with { TrialEndsAt = Now });
        Assert.Equal(TrialOver, await h.License.StatusAsync());
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Reads_the_machine_id_for_its_ask_with_the_2_s_a_status_gets_not_the_activation_bound()
    {
        var time = new ManualTime();
        var hanging = new FuncFingerprinter(() => new TaskCompletionSource<string>().Task);
        var h = Rotated(options: new Options { Fingerprint = hanging, Time = time });
        var bound = TimeSpan.FromMilliseconds(MachineId.TimeoutMs);
        var status = h.License.StatusAsync();
        // The stored trial lease's check reads the device first; then the ask reads it again.
        await Eventually.Until(() => Volatile.Read(ref hanging.Calls) == 1 && time.HasTimerDueIn(bound), "the lease check's read");
        time.Advance(bound);
        await Eventually.Until(() => Volatile.Read(ref hanging.Calls) == 2 && time.HasTimerDueIn(bound), "the ask's read");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(status.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        // A device that could not be told asks for no trial, and waits a minute.
        Assert.Equal(TrialOver, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Http.Called("trial"));
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
    }

    [Fact]
    public async Task Asks_nothing_once_the_stored_end_has_passed_or_when_the_lease_verifies()
    {
        var h = Rotated(s => s with { TrialStart = Now - 9 * Day, TrialEndsAt = Now - 1 });
        Assert.Equal(TrialOver, await h.License.StatusAsync());

        // Synced within the day, so no launch report is due either.
        var valid = Create();
        valid.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = valid.TrialLease(Now + 5 * Day), TrialAt = Now - Day / 2, LastSeen = Now };
        Assert.Equal(LicenseState.Trial, (await valid.License.StatusAsync()).State);
        await valid.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Http.Count("trial"));
        Assert.Equal(0, valid.Http.Count("trial"));
    }

    [Fact]
    public async Task Bounds_the_ask_as_a_status_check_is_and_waits_a_minute_when_it_runs_out()
    {
        var time = new ManualTime();
        var h = Rotated(options: new Options { Time = time });
        h.Http.Hang = true;
        var status = h.License.StatusAsync();
        await Eventually.Until(() => h.Http.Called("trial") && time.HasTimerDueIn(TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs)), "the bounded trial ask");
        time.Advance(TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs));
        Assert.Equal(TrialOver, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
    }

    [Fact]
    public async Task Answers_expired_and_records_nothing_when_the_caller_stops_waiting_for_the_ask()
    {
        var h = Rotated();
        using var cancel = new CancellationTokenSource();
        h.Http.On("trial", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(500, null);
        });
        Assert.Equal(TrialOver, await h.License.StatusAsync(cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(h.Stored!.BackoffSince);
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Never_asks_from_the_answer_StartTrialAsync_gave()
    {
        // The server's own answer to a StartTrialAsync is not asked for again, whatever it says.
        var h = Rotated();
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, Create().TrialLease(Now + 5 * Day));
        Assert.Equal(TrialOver, await h.License.StartTrialAsync());
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Null(h.Stored!.BackoffSince);
    }

    [Fact]
    public async Task Starts_no_trial_on_a_run_that_cannot_tell_who_the_device_is()
    {
        var h = Create(null, new Options { Fingerprint = new FuncFingerprinter(() => throw new InvalidOperationException("fingerprint adapter failed")) });
        await h.License.StartTrialAsync();
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Sends_the_device_s_kind_with_a_fingerprinter_that_says_it()
    {
        var h = Create(null, new Options
        {
            Fingerprint = new FuncFingerprinter(() => Task.FromResult("fp-machine"), () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, []))),
        });
        TrialAnswer(h, Now, Now + 7 * Day, null);
        await h.License.StartTrialAsync();
        AssertJson.Equal(new JsonObject { ["fingerprint"] = "fp-machine", ["fingerprintKind"] = "machine" }, h.Http.BodyOf("trial"));
    }

    // --- a trial lease, held to its device --------------------------------------

    private static string HeldTrialLease(Harness h, string device, string kind = "machine")
    {
        var claim = Hashes.FingerprintClaim(device);
        return h.SignLease(new LeaseInput { Sub = $"trial:{claim}", Aid = claim, Fpk = kind, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = 7 * 24 * 3600 });
    }

    [Fact]
    public async Task Runs_the_trial_on_this_device_s_lease()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now, TrialLease = HeldTrialLease(h, "fp-test"), LastSeen = Now };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Does_not_run_it_on_another_device_s_lease()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 30 * Day, TrialLease = HeldTrialLease(h, "fp-another-machine"), LastSeen = Now };
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Runs_it_on_a_lease_started_under_a_host_name_whatever_device_it_names()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 30 * Day, TrialLease = HeldTrialLease(h, "fp-another-machine", "hostname"), LastSeen = Now };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Does_not_judge_it_on_a_run_that_cannot_tell()
    {
        var h = Create(null, new Options { Fingerprint = new FuncFingerprinter(() => throw new InvalidOperationException("fingerprint adapter failed")) });
        h.Stored = new StoredState { TrialStart = Now, TrialLease = HeldTrialLease(h, "fp-another-machine"), LastSeen = Now };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
    }

    /// <summary>A License over the harness, with the app's own stash in place of the harness's.</summary>
    private static License WithStash(Harness h, IRegistryStash stash) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test",
        PublicJwk = h.Signer.Jwk,
        Adapters = new LicenseAdapters
        {
            Storage = h.Adapters.Storage,
            Clock = h.Adapters.Clock,
            Http = h.Adapters.Http,
            Fingerprint = h.Adapters.Fingerprint,
            Registry = stash,
        },
    });

    public static TheoryData<string> Throwing => new() { "at once", "later" };

    [Theory]
    [MemberData(nameof(Throwing))]
    public async Task A_stash_that_cannot_be_written_costs_nothing_the_server_s_trial_is_still_saved(string when)
    {
        var h = Create();
        var lease = TrialLease(h, Now + 5 * Day);
        TrialAnswer(h, Now - 2 * Day, Now + 5 * Day, lease);
        var stash = new ThrowingStash(failReads: false, failWrites: true, atOnce: when == "at once");
        var status = await WithStash(h, stash).StartTrialAsync();
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 }, status);
        Assert.Equal((Now - 2 * Day, Now + 5 * Day, lease, Now), (h.Stored!.TrialStart, h.Stored.TrialEndsAt, h.Stored.TrialLease, h.Stored.TrialAt));
        Assert.Equal(1, stash.Writes);
    }

    [Theory]
    [MemberData(nameof(Throwing))]
    public async Task A_stash_that_cannot_be_read_is_no_evidence_and_the_trial_still_starts(string when)
    {
        var h = Create();
        TrialAnswer(h, Now, Now + 7 * Day, TrialLease(h));
        var stash = new ThrowingStash(failReads: true, failWrites: false, atOnce: when == "at once");
        var status = await WithStash(h, stash).StartTrialAsync();
        Assert.Equal((LicenseState.Trial, 7), (status.State, status.TrialDaysLeft));
        // No evidence from it: no clientStart is sent for it.
        Assert.False(h.Http.BodyOf("trial").ContainsKey("clientStart"));
        Assert.Equal(Now, h.Stored!.TrialStart);
    }

    [Fact]
    public async Task A_stash_that_cannot_be_read_leaves_the_stored_start_as_the_only_evidence()
    {
        var h = Create(new StoredState { TrialStart = Now - 3 * Day });
        TrialAnswer(h, Now, Now + 7 * Day, TrialLease(h));
        await WithStash(h, new ThrowingStash(failReads: true, failWrites: true, atOnce: false)).StartTrialAsync();
        Assert.Equal(Now - 3 * Day, h.Http.BodyOf("trial")["clientStart"]!.GetValue<long>());
        Assert.Equal(Now - 3 * Day, h.Stored!.TrialStart);
    }

    [Fact]
    public async Task Answers_the_stored_trial_offline_whatever_the_stash_does()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = TrialLease(h, Now + 6 * Day), LastSeen = Now };
        h.Http.Fail = true;
        var status = await WithStash(h, new ThrowingStash(failReads: true, failWrites: true, atOnce: true)).StartTrialAsync();
        Assert.Equal((LicenseState.Trial, 6), (status.State, status.TrialDaysLeft));
    }

    private sealed class ThrowingStash(bool failReads, bool failWrites, bool atOnce) : IRegistryStash
    {
        public int Writes;

        public Task<long?> ReadTrialStartAsync()
        {
            if (!failReads) return Task.FromResult<long?>(null);
            if (atOnce) throw new UnauthorizedAccessException("no registry for you");
            return Task.FromException<long?>(new IOException("the registry hiccupped"));
        }

        public Task WriteTrialStartAsync(long ms)
        {
            Interlocked.Increment(ref Writes);
            if (!failWrites) return Task.CompletedTask;
            if (atOnce) throw new UnauthorizedAccessException("no registry for you");
            return Task.FromException(new IOException("the registry hiccupped"));
        }
    }
}
