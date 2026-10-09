using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// One call at a time, every request bounded, and the calls behind one stuck on an adapter the SDK does
/// not bound going ahead READ-ONLY once its HTTP bound plus 15 s have passed.
/// </summary>
public class SerialAndStalledTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private static readonly TimeSpan Grace = LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15);
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };

    private static StoredState LicensedState(Harness h) =>
        new() { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", 30 * 24 * 3600), CheckedMajor = 1, LastSeen = Now };

    private static (Harness H, ManualTime Time) OnManualTime(Options? options = null)
    {
        var time = new ManualTime();
        return (Create(null, (options ?? new Options()) with { Time = time }), time);
    }

    [Fact]
    public async Task Bounds_a_call_whose_adapter_never_settles_so_the_calls_behind_it_still_answer()
    {
        var (h, time) = OnManualTime();
        h.Stored = LicensedState(h);
        h.Http.Hang = true;
        var refreshed = h.License.RefreshAsync();
        var later = h.License.StatusAsync();
        await Eventually.Until(() => h.Http.Called("validate") && time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout), "the bounded validate");
        time.Advance(LicenseConfig.DefaultHttpTimeout);
        Assert.Equal(Licensed, await refreshed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Licensed, await later.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Bounds_every_request_by_the_configured_timeout_when_the_call_names_none()
    {
        var (h, time) = OnManualTime(new Options { HttpTimeout = TimeSpan.FromSeconds(2) });
        h.Http.Hang = true;
        var activating = h.License.ActivateAsync(Key);
        await Eventually.Until(() => h.Http.Called("activate") && time.HasTimerDueIn(TimeSpan.FromSeconds(2)), "the bounded activate");
        time.Advance(TimeSpan.FromMilliseconds(1_999));
        await Eventually.Quiet();
        Assert.False(activating.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await activating.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Lets_a_later_call_go_ahead_read_only_when_one_is_stuck_on_an_adapter_the_SDK_does_not_bound()
    {
        var (h, time) = OnManualTime();
        h.Stored = LicensedState(h);
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        var later = h.License.StatusAsync();
        time.Advance(Grace - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(later.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        // Answered from storage behind the stuck call, and saying so.
        Assert.Equal(Licensed with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Lets_no_call_go_ahead_behind_a_stuck_one_with_a_write_a_late_stale_write_could_revert()
    {
        var (h, time) = OnManualTime();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = LicensedState(h) with { Lease = lease };
        h.Http.Error("validate", 403, "revoked");
        var release = h.HoldNextWrite();
        var a = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes == 1, "A's held write");
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var b = h.License.RefreshAsync();
        time.Advance(Grace);
        // Read-only: the lease still stored, licensed; not asked, nothing written.
        Assert.Equal(Licensed with { Stalled = true }, await b.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(1, h.Writes);
        Assert.Equal(lease, h.Stored!.Lease);
        release();
        Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key }, await a.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Refusal.Revoked, h.Stored!.Refused);
        // A settled: the next call writes again, and heals.
        Assert.Equal(Licensed, await h.License.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, h.Http.Count("validate"));
    }

    /// <summary>A refresh whose refusal write is held inside the adapter, and time past the grace: whatever is called now runs read-only behind it.</summary>
    private static async Task<(Harness H, ManualTime Time)> BehindAStuckCall(Func<Harness, StoredState> stored)
    {
        var (h, time) = OnManualTime();
        h.Stored = stored(h);
        h.Http.Error("validate", 403, "revoked");
        h.HoldNextWrite();
        _ = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes == 1, "the held write");
        time.Advance(Grace);
        return (h, time);
    }

    [Fact]
    public async Task Answers_activate_network_behind_a_stuck_call_no_request_no_write()
    {
        var (h, _) = await BehindAStuckCall(LicensedState);
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await h.License.ActivateAsync(Key).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Http.Count("activate"));
        Assert.Equal(1, h.Writes);
    }

    [Fact]
    public async Task Throws_from_deactivate_behind_a_stuck_call_no_request_and_the_key_still_stored()
    {
        var (h, _) = await BehindAStuckCall(LicensedState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.License.DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Http.Count("deactivate"));
        Assert.Equal(Key, h.Stored!.Key);
        Assert.Equal(1, h.Writes);
    }

    [Fact]
    public async Task Answers_start_trial_with_the_stored_trial_behind_a_stuck_call()
    {
        var (h, _) = await BehindAStuckCall(x => LicensedState(x) with { TrialStart = Now, TrialEndsAt = Now + 5 * Day, TrialLease = x.TrialLease(Now + 5 * Day) });
        Assert.Equal(
            new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5, Stalled = true },
            await h.License.StartTrialAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Http.Count("trial"));
        Assert.Equal(1, h.Writes);
    }

    [Fact]
    public async Task Answers_a_stored_refusal_behind_a_stuck_call_without_asking()
    {
        var (h, _) = await BehindAStuckCall(_ => new StoredState { Key = Key, ActivationId = "act_1", Refused = Refusal.Revoked, LastSeen = Now });
        Assert.Equal(
            new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key, Stalled = true },
            await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Asks_no_trial_again_behind_a_stuck_call()
    {
        // A trial whose lease another key signed, its stored end ahead: the refresh asks for it again,
        // and its save is the one held. Behind it, a status answers from storage, asking nothing.
        var (h, _) = await BehindAStuckCall(_ => new StoredState
        {
            TrialStart = Now - 2 * Day,
            TrialEndsAt = Now + 5 * Day,
            TrialLease = Create().TrialLease(Now + 5 * Day),
            LastSeen = Now,
        });
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Equal(
            new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial, Stalled = true },
            await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Equal(1, h.Writes);
    }

    [Fact]
    public async Task Answers_an_expired_stored_lease_as_expired_offline_behind_a_stuck_call_without_asking()
    {
        var (h, _) = await BehindAStuckCall(x => new StoredState
        {
            Key = Key,
            ActivationId = "act_1",
            Lease = x.Sign(Key, "perpetual", 3600, Now - 2 * Day),
            LastSeen = Now,
        });
        Assert.Equal(
            new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key, Stalled = true },
            await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Is_unknown_still_writing_nothing_behind_a_stuck_call_when_the_state_cannot_be_read()
    {
        var (h, time) = OnManualTime();
        h.Stored = LicensedState(h);
        // The stuck read is held first, then every read after it fails.
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        h.FailReads();
        var status = h.License.StatusAsync();
        var trial = h.License.StartTrialAsync();
        time.Advance(Grace);
        var unreadable = new LicenseStatus { State = LicenseState.Unknown, Reason = LicenseReason.Storage, Stalled = true };
        Assert.Equal(unreadable, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(unreadable, await trial.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, h.Reads);
        Assert.Equal(0, h.Writes);
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Times_the_grace_from_when_the_stuck_call_started_however_late_the_next_is_queued()
    {
        var (h, time) = OnManualTime();
        h.Stored = LicensedState(h);
        h.Http.Error("validate", 403, "revoked");
        h.HoldNextWrite();
        _ = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes == 1, "the held write");
        // 20 s into the stuck call, the next is queued: it goes ahead 10 s later, when the stuck
        // call's grace is up, not 30 s after it was queued.
        time.Advance(TimeSpan.FromSeconds(20));
        var later = h.License.StatusAsync();
        time.Advance(Grace - TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(later.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Licensed with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Runs_three_calls_queued_at_once_on_a_slow_network_one_after_another_each_asking()
    {
        // Each answer takes 40 s, inside a 60 s bound. Timed from when they were queued, the third
        // would have gone ahead at 75 s while the second was still asking; timed from when the second
        // started, it waits for it.
        var (h, time) = OnManualTime(new Options { HttpTimeout = TimeSpan.FromSeconds(60) });
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = LicensedState(h) with { Lease = lease };
        var inFlight = 0;
        var most = 0;
        h.Http.On("validate", async _ =>
        {
            var answered = Task.Delay(TimeSpan.FromSeconds(40), time);
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            await answered;
            Interlocked.Decrement(ref inFlight);
            return new HttpResult(200, new JsonObject { ["lease"] = lease, ["activationId"] = "act_1" });
        });
        var calls = new[] { h.License.RefreshAsync(), h.License.RefreshAsync(), h.License.RefreshAsync() };
        var all = Task.WhenAll(calls);
        for (var step = 0; step < 40 && !all.IsCompleted; step++)
        {
            await Eventually.Until(() => Volatile.Read(ref inFlight) > 0 || all.IsCompleted, "a validate in flight");
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(5);
        }
        foreach (var call in calls) Assert.Equal(Licensed, await call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, h.Http.Count("validate"));
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task A_call_cancelled_while_queued_stands_aside_and_the_one_behind_it_still_waits_its_turn()
    {
        var (h, time) = OnManualTime();
        h.Stored = LicensedState(h);
        var answer = new TaskCompletionSource<HttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Http.On("validate", _ => answer.Task);
        var first = h.License.RefreshAsync();
        await Eventually.Until(() => h.Http.Called("validate"), "the first ask");
        using var cancel = new CancellationTokenSource();
        var cancelled = h.License.StatusAsync(cancellationToken: cancel.Token);
        var third = h.License.StatusAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Eventually.Quiet();
        Assert.False(third.IsCompleted);
        answer.SetResult(new HttpResult(200, new JsonObject { ["lease"] = h.Stored!.Lease, ["activationId"] = "act_1" }));
        Assert.Equal(Licensed, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        // Not stalled: it waited for the first to settle.
        Assert.Equal(Licensed, await third.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, time.LiveTimers);
    }

    [Fact]
    public async Task A_caller_cancelling_an_ask_gets_the_offline_answer_with_no_wait_recorded_and_nothing_written()
    {
        var h = Create();
        h.Stored = LicensedState(h);
        using var cancel = new CancellationTokenSource();
        h.Http.On("validate", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(500, null);
        });
        // The valid lease is the answer, as when nothing answers: never an exception.
        Assert.Equal(Licensed, await h.License.RefreshAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(h.Stored!.BackoffSince);
        Assert.Equal(0, h.Writes);
        // And the next call is not held up by it.
        Assert.Equal(Licensed, await h.License.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_caller_cancelling_the_early_renewal_or_a_trial_start_still_gets_an_answer()
    {
        // A lease in its last days: the status asks, bounded to 5 s, and the caller stops waiting.
        var h = Create();
        h.Stored = LicensedState(h) with { Lease = h.Sign(Key, "subscription", 7 * 24 * 3600) };
        h.SetClock(Now + 5 * Day);
        using var cancel = new CancellationTokenSource();
        h.Http.On("validate", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(500, null);
        });
        Assert.Equal(Licensed, await h.License.StatusAsync(cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(h.Stored!.BackoffSince);

        // A trial start the caller gives up on answers what the device holds.
        var trial = Create();
        trial.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = trial.TrialLease(Now + 6 * Day), LastSeen = Now };
        using var stop = new CancellationTokenSource();
        trial.Http.On("trial", async _ =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, stop.Token);
            return new HttpResult(500, null);
        });
        var status = await trial.License.StartTrialAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((LicenseState.Trial, 6), (status.State, status.TrialDaysLeft));
    }

    [Fact]
    public async Task A_caller_cancelling_before_the_activation_is_asked_gets_the_cancellation()
    {
        var h = Create();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.ActivateAsync(Key, cancel.Token));
        Assert.False(h.Http.Called("activate"));
        Assert.Null(h.Stored);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
