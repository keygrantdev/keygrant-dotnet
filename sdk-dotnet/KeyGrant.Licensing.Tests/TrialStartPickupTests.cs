using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.PickupTests;
using static KeyGrant.Licensing.Tests.PurchaseUrlTests;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The pickup at a trial start: the device read once (10 s) for both asks, the claim asked first whatever
/// the wait says and bounded as a status check is, a bought key the answer with no trial asked, and the
/// calls behind it held for its longest run.
/// </summary>
public class TrialStartPickupTests
{
    [Fact]
    public async Task At_a_trial_start_reads_the_device_once_for_10_s_for_both_asks_and_with_nothing_to_claim_under_starts_the_wait()
    {
        // A device with no fingerprint and no host name: no claim (no answer: the secret kept, the wait
        // started), and no trial asked, from the one read.
        var time = new ManualTime();
        var hanging = new FuncFingerprinter(() => new TaskCompletionSource<string>().Task);
        var h = Create(Held(), new Options { Fingerprint = hanging, Time = time });
        var bound = TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs);
        var call = h.License.StartTrialAsync();
        await Eventually.Until(() => Volatile.Read(ref hanging.Calls) == 1 && time.HasTimerDueIn(bound), "the trial start's machine-id read");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Unlicensed, await call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref hanging.Calls));
        Assert.Empty(h.Http.Calls);
        Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
    }

    [Fact]
    public async Task At_a_trial_start_reads_the_device_once_and_may_claim_under_the_host_name()
    {
        var calls = 0;
        var hostOnly = new FuncFingerprinter(
            () => Task.FromResult("unused"),
            () =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<DeviceIdentity?>(new DeviceIdentity(null, null, ["fp-host"]));
            });
        var h = Create(Held(), new Options { Fingerprint = hostOnly });
        h.Http.On("claim", _ => Pending);
        Assert.Equal(Unlicensed, await h.License.StartTrialAsync());
        Assert.Equal(1, Volatile.Read(ref calls));
        AssertJson.Equal(
            new JsonObject { ["token"] = Secret, ["fingerprint"] = "fp-host", ["fingerprintKind"] = "hostname", ["fingerprints"] = new JsonArray("fp-host"), ["major"] = 1 },
            h.Http.BodyOf("claim"));
    }

    [Fact]
    public async Task At_a_trial_start_bounds_its_claim_as_a_status_check_is()
    {
        var time = new ManualTime();
        var h = Create(Held(), new Options { Time = time });
        h.Http.Hang = true;
        var bound = TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs);
        _ = h.License.StartTrialAsync();
        await Eventually.Until(() => h.Http.Called("claim") && time.HasTimerDueIn(bound), "the bounded claim");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(h.Http.Called("trial"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.Until(() => h.Http.Called("trial"), "the trial ask");
        Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
    }

    [Fact]
    public async Task Claims_nothing_and_writes_nothing_behind_a_call_that_has_not_finished()
    {
        var (h, time) = await StuckBehind(Held());
        var later = h.License.StartTrialAsync();
        time.Advance(Grace);
        Assert.Equal(Unlicensed with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Http.Called("claim"));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Holds_the_calls_behind_a_trial_start_exactly_5_s_longer_than_behind_another_call()
    {
        // The grace each call gives the one behind it, from when it starts: the HTTP bound + 15 s, and + 5
        // s more for a trial start.
        var time = new ManualTime();
        var h = Create(Held(), new Options { Time = time });
        h.HangNextRead();
        _ = h.License.StartTrialAsync();
        await Eventually.Until(() => time.LiveTimers > 0, "the trial start's grace");
        Assert.True(time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(20)));
        Assert.False(time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15)));
        Assert.Equal(1, time.LiveTimers);
    }

    [Fact]
    public async Task Holds_the_calls_behind_a_trial_start_for_its_longest_run_not_running_them_read_only()
    {
        // A machine id that reads just inside an activation's time, then a claim and a trial ask that never
        // answer: 10.5 s + 5 s + 15 s, past the 30 s another call is given, inside the 35 s a trial start is.
        var time = new ManualTime();
        var slow = new FuncFingerprinter(
            () => Task.FromResult("unused"),
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10_500), time);
                return new DeviceIdentity("fp-slow", DeviceKind.Machine, []);
            });
        var h = Create(Held(), new Options { Fingerprint = slow, Time = time });
        h.Http.Hang = true;
        var started = h.License.StartTrialAsync();
        var behind = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(10_500)), "the slow machine-id read");
        time.Advance(TimeSpan.FromMilliseconds(10_500));
        var claimBound = TimeSpan.FromMilliseconds(Wire.StatusCheckTimeoutMs);
        await Eventually.Until(() => h.Http.Called("claim") && time.HasTimerDueIn(claimBound), "the bounded claim");
        time.Advance(claimBound);
        await Eventually.Until(() => h.Http.Called("trial") && time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout), "the bounded trial ask");
        time.Advance(LicenseConfig.DefaultHttpTimeout + TimeSpan.FromMilliseconds(1));
        Assert.Equal(Unlicensed, await started.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Unlicensed, await behind.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task At_a_trial_start_asks_the_claim_first_whatever_the_wait_and_a_bought_key_is_the_answer_with_no_trial_asked()
    {
        var h = Create(Held() with { ClaimAskedAt = Now - 1000 });
        h.Http.On("claim", _ => BoughtAnswer(h));
        Assert.Equal(Bought, await h.License.StartTrialAsync());
        Assert.Equal(new[] { "claim" }, h.Http.Calls.Select(c => c.Action));
    }

    [Fact]
    public async Task At_a_trial_start_asks_for_the_trial_after_a_claim_that_brought_no_key()
    {
        var h = Create(Held());
        h.Http.On("claim", _ => Pending);
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = h.TrialLease(Now + 5 * Day) });
        Assert.Equal(RunningTrial, await h.License.StartTrialAsync());
        Assert.Equal(new[] { "claim", "trial" }, h.Http.Calls.Select(c => c.Action));
        Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
    }
}
