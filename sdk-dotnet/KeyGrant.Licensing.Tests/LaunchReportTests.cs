using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// A running trial reports its launch from a status, at most once a day, in the background: never
/// awaited, never failing offline, its answer not read.
/// </summary>
public class LaunchReportTests
{
    private static readonly LicenseStatus RunningTrial = new() { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 };

    /// <summary>A running trial (its lease valid), no trial answer or report stamped.</summary>
    private static StoredState Running(Harness h) =>
        new() { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = h.TrialLease(Now + 5 * Day), LastSeen = Now };

    private static Task Settled(Harness h) => h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Is_sent_from_a_status_stamped_first_then_POSTed_in_the_background_as_a_trial_start_would_its_answer_not_read()
    {
        var h = Create();
        h.Stored = Running(h);
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now - 9 * Day, ["trialEndsAt"] = Now + 30 * Day });
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        Assert.Equal(Now, h.Stored!.TrialReportedAt);
        await Settled(h);
        Assert.Equal(new[] { "trial" }, h.Http.Calls.Select(c => c.Action));
        AssertJson.Equal(new JsonObject { ["fingerprint"] = "fp-test", ["clientStart"] = Now - 2 * Day }, h.Http.BodyOf("trial"));
        // The answer is not read: the stored trial is as it was.
        Assert.Equal((Now - 2 * Day, Now + 5 * Day), (h.Stored!.TrialStart, h.Stored.TrialEndsAt));
    }

    [Fact]
    public async Task Is_sent_at_most_once_a_day_a_trial_start_s_answer_counting_as_a_report()
    {
        var h = Create();
        h.Stored = Running(h);
        await h.License.StatusAsync();
        await Settled(h);
        h.SetClock(Now + Offline.TrialReportMs - 1);
        await h.License.StatusAsync();
        await Settled(h);
        Assert.Equal(1, h.Http.Count("trial"));
        h.SetClock(Now + Offline.TrialReportMs);
        await h.License.StatusAsync();
        await Settled(h);
        Assert.Equal(2, h.Http.Count("trial"));

        var synced = Create();
        synced.Stored = Running(synced) with { TrialAt = Now - Day / 2 };
        await synced.License.StatusAsync();
        await Settled(synced);
        Assert.False(synced.Http.Called("trial"));
    }

    [Fact]
    public async Task Never_holds_up_the_launch_a_status_answers_while_the_report_hangs()
    {
        // On manual time the report's 5 s bound never passes: a status that awaited it would never answer.
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Running(h);
        h.Http.Hang = true;
        Assert.Equal(RunningTrial, await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await Eventually.Until(() => h.Http.Called("trial"), "the report");
        Assert.False(h.License.LaunchReportSettled.IsCompleted);
    }

    [Fact]
    public async Task Reads_the_device_for_2_s_off_the_caller_s_path_and_sends_nothing_when_it_cannot_tell()
    {
        // The status's own lease check cannot tell (kept for no one); the report's read then never settles.
        var time = new ManualTime();
        var reads = 0;
        var flaky = new FuncFingerprinter(() => Interlocked.Increment(ref reads) == 1
            ? Task.FromException<string>(new IOException("busy"))
            : new TaskCompletionSource<string>().Task);
        var h = Create(null, new Options { Fingerprint = flaky, Time = time });
        h.Stored = Running(h);
        // Answered with no time passing: the report's device read is not on the status's path.
        Assert.Equal(RunningTrial, await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        var bound = TimeSpan.FromMilliseconds(MachineId.TimeoutMs);
        await Eventually.Until(() => Volatile.Read(ref reads) == 2 && time.HasTimerDueIn(bound), "the report's machine-id read");
        var report = h.License.LaunchReportSettled;
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(report.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await report.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Sends_nothing_from_a_device_whose_own_fingerprint_does_not_read_after_its_stamp()
    {
        var h = Create(null, new Options { Fingerprint = HostOnly() });
        h.Stored = Running(h);
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        Assert.Equal(Now, h.Stored!.TrialReportedAt);
        await Settled(h);
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Sends_the_fingerprint_s_kind_and_the_stored_start_never_the_stash_s()
    {
        var machine = new FuncFingerprinter(
            () => Task.FromResult("unused"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, [])));
        var h = Create(null, new Options { Fingerprint = machine });
        h.Stored = Running(h);
        h.Registry = Now - 9 * Day;
        await h.License.StatusAsync();
        await Settled(h);
        AssertJson.Equal(
            new JsonObject { ["fingerprint"] = "fp-machine", ["fingerprintKind"] = "machine", ["clientStart"] = Now - 2 * Day },
            h.Http.BodyOf("trial"));
    }

    [Fact]
    public async Task Runs_on_a_thread_of_its_own_never_on_the_caller_s()
    {
        // An HTTP adapter that blocks the thread it is called on: the status still answers.
        var h = Create();
        h.Stored = Running(h);
        using var gate = new ManualResetEventSlim();
        h.Http.On("trial", _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return new HttpResult(200, new JsonObject());
        });
        Assert.Equal(RunningTrial, await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await Eventually.Until(() => h.Http.Called("trial"), "the report");
        Assert.False(h.License.LaunchReportSettled.IsCompleted);
        gate.Set();
        await Settled(h);
    }

    [Fact]
    public async Task Is_bounded_as_a_status_check_is()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Running(h);
        h.Http.Hang = true;
        await h.License.StatusAsync();
        var bound = TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs);
        await Eventually.Until(() => h.Http.Called("trial") && time.HasTimerDueIn(bound), "the bounded report");
        var report = h.License.LaunchReportSettled;
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(report.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await report.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Never_fails_offline()
    {
        var h = Create();
        h.Stored = Running(h);
        h.Http.Fail = true;
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        await Settled(h);
        Assert.True(h.License.LaunchReportSettled.IsCompletedSuccessfully);
        Assert.True(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Sends_nothing_when_its_stamp_cannot_be_saved_so_it_is_never_sent_twice_in_a_day()
    {
        var h = Create();
        h.Stored = Running(h);
        h.FailWrites();
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        await Settled(h);
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Is_not_made_by_an_expired_trial_with_a_key_held_or_behind_a_call_that_has_not_finished()
    {
        var expired = Create();
        expired.Stored = Running(expired) with { TrialLease = null, TrialEndsAt = Now - 1 };
        await expired.License.StatusAsync();
        await Settled(expired);
        Assert.False(expired.Http.Called("trial"));

        var keyed = Create();
        keyed.Stored = Running(keyed) with { Key = "KEY-1" };
        await keyed.License.StatusAsync();
        await Settled(keyed);
        Assert.False(keyed.Http.Called("trial"));

        var time = new ManualTime();
        var stalled = Create(null, new Options { Time = time });
        stalled.Stored = Running(stalled);
        stalled.HangNextRead();
        _ = stalled.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        var later = stalled.License.StatusAsync();
        time.Advance(Grace);
        Assert.Equal(RunningTrial with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
        await Settled(stalled);
        Assert.False(stalled.Http.Called("trial"));
        Assert.Equal(0, stalled.Writes);
    }

    [Fact]
    public async Task Every_answer_parser_ignores_members_it_does_not_know()
    {
        // Activate, validate and trial answers alike.
        JsonObject With(JsonObject answer)
        {
            answer["future"] = new JsonObject { ["nested"] = new JsonArray(1, 2) };
            answer["note"] = "added by a later server";
            return answer;
        }
        var h = Create();
        var lease = h.Sign("KEY-1", "perpetual", 30 * 24 * 3600);
        h.Http.On("activate", _ => new HttpResult(200, With(new JsonObject { ["lease"] = lease, ["activationId"] = "act_1" })));
        h.Http.On("validate", _ => new HttpResult(200, With(new JsonObject { ["lease"] = lease })));
        var licensed = new LicenseStatus { State = LicenseState.Licensed, Key = "KEY-1" };
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync("KEY-1"));
        Assert.Equal(licensed, await h.License.StatusAsync());
        Assert.Equal(licensed, await h.License.RefreshAsync());
        Assert.True(h.Http.Called("validate"));

        var trial = Create();
        trial.Http.On("trial", _ => new HttpResult(200, With(new JsonObject { ["trialStart"] = Now, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = trial.TrialLease(Now + 5 * Day) })));
        Assert.Equal(RunningTrial, await trial.License.StartTrialAsync());
    }

    [Fact]
    public async Task Is_due_by_the_guarded_clock()
    {
        // Reported an hour ago by the device clock, a day and an hour ago by the guarded one (lastSeen a day ahead): due.
        var h = Create();
        h.Stored = Running(h) with { TrialReportedAt = Now - 3_600_000, LastSeen = Now + Day };
        await h.License.StatusAsync();
        await Settled(h);
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Equal(Now + Day, h.Stored!.TrialReportedAt);
    }

    [Fact]
    public async Task Is_stamped_by_the_guarded_clock()
    {
        var h = Create();
        h.Stored = Running(h) with { LastSeen = Now + Day };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
        Assert.Equal(Now + Day, h.Stored!.TrialReportedAt);
        await Settled(h);
    }
}
