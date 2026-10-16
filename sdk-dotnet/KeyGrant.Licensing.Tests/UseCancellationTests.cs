using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.Metered;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The one thing <c>UseAsync</c> may throw: a <see cref="CancellationToken"/> cancelled while the call waits its turn, or before
/// its request is sent, with nothing spent. Cancelled while the request is out, the call stops waiting and spends as for no answer.
/// </summary>
public class UseCancellationTests
{
    [Fact]
    public async Task A_token_cancelled_before_the_call_throws_and_spends_nothing()
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(89));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.UseAsync("exports", 1, cancelled.Token));
        Assert.Equal(0, h.Http.Count("use"));
        Assert.Empty(Journal(h));
        // The call behind it is not held up.
        Assert.Equal(UseResult.Spent(89), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task A_token_cancelled_while_the_call_waits_its_turn_throws_and_the_calls_behind_it_still_run()
    {
        var time = new ManualTime();
        var h = Holding(options: new Options { Time = time });
        h.Http.Hang = true;
        var first = h.License.UseAsync("exports");
        await Eventually.Until(() => h.Http.Called("use") && time.HasTimerDueIn(TimeSpan.FromMilliseconds(Meters.UseTimeoutMs)), "the first call's request");
        using var cts = new CancellationTokenSource();
        var waiting = h.License.UseAsync("exports", 2, cts.Token);
        var behind = h.License.UseAsync("exports", 3);
        await Eventually.Quiet();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        // The first call's request times out, and the one behind the cancelled call goes on as if it had never been queued.
        h.Http.Hang = false;
        h.Http.Fail = true;
        time.Advance(TimeSpan.FromMilliseconds(Meters.UseTimeoutMs));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Offline);
        Assert.True((await behind.WaitAsync(TimeSpan.FromSeconds(10))).Offline);
        // The cancelled call spent nothing: the first and the third are the whole journal.
        // The third was made inside the first's wait (a minute after one that got no answer), so no request was made for it.
        Assert.Equal([(SpendKind.Unanswered, 1L), (SpendKind.Offline, 3L)], KindsAndCounts(h));
        Assert.Equal([1L], h.Http.Calls.Where(c => c.Action == "use").Select(c => Vectors.Long(c.Body["n"])!.Value));
    }

    [Fact]
    public async Task A_token_cancelled_before_the_request_is_sent_throws_and_spends_nothing()
    {
        // The device cannot be read until told to stop: a cancelled token ends the read, and the call then finds it was cancelled.
        using var cts = new CancellationTokenSource();
        var read = new TaskCompletionSource();
        var fingerprint = new FuncFingerprinter(
            () => Task.FromResult("fp-test"),
            async Task<DeviceIdentity?> () =>
            {
                read.TrySetResult();
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                return null;
            });
        var h = Holding(options: new Options { Fingerprint = fingerprint });
        // Licensed already, with the device read once: a lease not held to a machine does not need it, so the read that holds the call is the request's own.
        h.Http.On("use", _ => Metered.Spent(89));
        var asking = h.License.UseAsync("exports", 1, cts.Token);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Http.Count("use"));
        Assert.Empty(Journal(h));
        Assert.Null(h.Stored!.BackoffSince);
    }

    [Fact]
    public async Task A_token_cancelled_while_the_request_is_out_stops_waiting_and_spends_as_for_no_answer_keeping_the_spend_in_doubt()
    {
        var h = Holding();
        h.Http.Hang = true;
        using var cts = new CancellationTokenSource();
        var asking = h.License.UseAsync("exports", 2, cts.Token);
        await Eventually.Until(() => h.Http.Called("use"), "the request");
        await cts.CancelAsync();
        // Never an exception: the request may have been taken, so the spend is made from the allowance and reported.
        Assert.Equal(UseResult.Spent(88, offline: true), await asking.WaitAsync(TimeSpan.FromSeconds(10)));
        var entry = Assert.Single(Journal(h));
        Assert.Equal((SpendKind.Unanswered, 2L, Str(UseBody(h)["id"])), (entry.Kind, entry.N, entry.Id));
        // And no wait is recorded for the ask it cut short; the entry is, and the journal's own wait runs from it: the next spend it covers is made without asking.
        Assert.Null(h.Stored!.BackoffSince);
        h.Http.Hang = false;
        h.Http.On("use", _ => Metered.Spent(80));
        Assert.Equal(UseResult.Spent(87, offline: true), await h.License.UseAsync("exports", 1));
        Assert.Equal(1, h.Http.Count("use"));
        // A minute on it is asked again.
        h.SetClock(Now + Minute);
        // The server's 80, less the 3 it was not told of (the cut-short spend in doubt and the offline one after it).
        Assert.Equal(UseResult.Spent(77), await h.License.UseAsync("exports", 1));
        Assert.Equal(2, h.Http.Count("use"));
    }

    [Fact]
    public async Task A_spend_the_allowance_refuses_cancelled_while_the_request_is_out_is_refused_and_leaves_its_void_to_report()
    {
        var h = Holding();
        h.Http.Hang = true;
        using var cts = new CancellationTokenSource();
        // The renders meter has no offline allowance: with no answer the spend is refused, and a spend that was sent is taken back.
        var asking = h.License.UseAsync("renders", 1, cts.Token);
        await Eventually.Until(() => h.Http.Called("use"), "the request");
        await cts.CancelAsync();
        Assert.Equal(UseResult.Refused(UseError.OfflineLimit, 5), await asking.WaitAsync(TimeSpan.FromSeconds(10)));
        var entry = Assert.Single(Journal(h));
        Assert.Equal((SpendKind.Void, "renders", Str(UseBody(h)["id"])), (entry.Kind, entry.Meter, entry.Id));
        Assert.Null(h.Stored!.BackoffSince);
        // The void's minute is its own wait: refused again without asking, until it is over.
        h.Http.Hang = false;
        h.Http.Fail = true;
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        Assert.Equal(1, h.Http.Count("use"));
    }

    private static string? Str(System.Text.Json.Nodes.JsonNode? node) => Vectors.Str(node);
}
