using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What the app's own adapters may do with the token the SDK hands them, and what a caller's token does
/// to a call: an answer always stands, a call the SDK gave up on is told to stop, and a status never
/// turns into an exception.
/// </summary>
public class AdapterContractTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };

    private static StoredState Holding(string lease) => new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 1, LastSeen = Now };

    /// <summary>A License over the harness, with adapters of the test's own in place of the harness's.</summary>
    private static License With(Harness h, IHttpAdapter? http = null, IFingerprinter? fingerprint = null) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test",
        PublicJwk = h.Signer.Jwk,
        TimeProvider = h.Time,
        Adapters = new LicenseAdapters
        {
            Storage = h.Adapters.Storage,
            Registry = h.Adapters.Registry,
            Clock = h.Adapters.Clock,
            Http = http ?? h.Adapters.Http,
            Fingerprint = fingerprint ?? h.Adapters.Fingerprint,
        },
    });

    /// <summary>
    /// An adapter that answers after registering on its token a callback that throws once it has: the
    /// common <c>ct.Register(() => tcs.SetCanceled())</c>, its registration never disposed.
    /// </summary>
    private sealed class ThrowsWhenTold(IHttpAdapter inner) : IHttpAdapter
    {
        public async Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<HttpResult>();
            cancellationToken.Register(() => answer.SetCanceled());
            answer.SetResult(await inner.PostAsync(url, body, timeout, cancellationToken));
            return await answer.Task;
        }
    }

    /// <summary>A fingerprinter that answers as <see cref="ThrowsWhenTold"/> does.</summary>
    private sealed class FingerprinterThatThrowsWhenTold : IFingerprinter
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Answer("fp-test", cancellationToken);

        public Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Answer<DeviceIdentity?>(new DeviceIdentity("fp-test", null, []), cancellationToken);

        private static Task<T> Answer<T>(T value, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<T>();
            cancellationToken.Register(() => answer.SetCanceled());
            answer.SetResult(value);
            return answer.Task;
        }
    }

    [Fact]
    public async Task Keeps_a_renewal_s_answer_whatever_the_adapter_s_token_callbacks_do()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        var fresh = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Http.Lease("validate", fresh);
        Assert.Equal(Licensed, await With(h, http: new ThrowsWhenTold(h.Http)).StatusAsync());
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Fact]
    public async Task Keeps_an_activation_s_and_a_deactivation_s_answer_whatever_the_adapter_s_token_callbacks_do()
    {
        var h = Create();
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        var license = With(h, http: new ThrowsWhenTold(h.Http));
        Assert.Equal(ActivateResult.Success, await license.ActivateAsync(Key));
        Assert.Equal(Key, h.Stored!.Key);
        Assert.Equal(new DeactivateResult(Released: true), await license.DeactivateAsync());
    }

    [Fact]
    public async Task Answers_a_status_and_a_trial_whatever_the_fingerprinter_s_token_callbacks_do()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var license = With(h, fingerprint: new FingerprinterThatThrowsWhenTold());
        Assert.Equal(Licensed, await license.StatusAsync());
        // And who the device is was told, as the fingerprinter answered: its fingerprint goes with an ask.
        Assert.Equal(Licensed, await license.RefreshAsync());
        AssertJson.Equal(new JsonArray("fp-test"), h.Http.BodyOf("validate")["fingerprints"]);

        var trial = Create();
        trial.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = trial.TrialLease(Now + 6 * Day), LastSeen = Now };
        trial.Http.Fail = true;
        var status = await With(trial, fingerprint: new FingerprinterThatThrowsWhenTold()).StartTrialAsync();
        Assert.Equal((LicenseState.Trial, 6), (status.State, status.TrialDaysLeft));
    }

    // --- a call the SDK gave up on is told to stop -------------------------------------

    /// <summary>An adapter that never answers, and says when its token tells it to stop.</summary>
    private sealed class HangsUntilTold : IHttpAdapter, IFingerprinter
    {
        public TaskCompletionSource Told { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) => Hang<HttpResult>(cancellationToken);

        public Task<string> GetAsync(CancellationToken cancellationToken) => Hang<string>(cancellationToken);

        public Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken) => Hang<DeviceIdentity?>(cancellationToken);

        private Task<T> Hang<T>(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => Told.TrySetResult());
            return new TaskCompletionSource<T>().Task;
        }
    }

    [Fact]
    public async Task Tells_an_HTTP_call_it_gave_up_on_to_stop()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        var adapter = new HangsUntilTold();
        var status = With(h, http: adapter).StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout), "the bounded validate");
        Assert.False(adapter.Told.Task.IsCompleted);
        time.Advance(LicenseConfig.DefaultHttpTimeout);
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        await adapter.Told.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Tells_a_fingerprinter_it_gave_up_on_to_stop()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var fingerprinter = new HangsUntilTold();
        var status = With(h, fingerprint: fingerprinter).StatusAsync();
        var bound = TimeSpan.FromMilliseconds(Internal.MachineId.TimeoutMs) + TimeSpan.FromSeconds(1);
        await Eventually.Until(() => time.HasTimerDueIn(bound), "the bounded device read");
        Assert.False(fingerprinter.Told.Task.IsCompleted);
        time.Advance(bound);
        // Who the device is cannot be told this time: the lease, held to no device, licenses.
        Assert.Equal(Licensed, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        await fingerprinter.Told.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // --- a caller's token --------------------------------------------------------------------

    [Fact]
    public async Task Throws_from_an_activation_the_caller_cancels_in_flight_rather_than_answer_network()
    {
        var h = Create();
        using var cancel = new CancellationTokenSource();
        h.Http.On("activate", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(500, null);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.ActivateAsync(Key, cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Throws_from_a_deactivation_the_caller_cancels_in_flight_and_keeps_the_licence()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(lease);
        using var cancel = new CancellationTokenSource();
        h.Http.On("deactivate", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(200, new JsonObject { ["ok"] = true });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.DeactivateAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((Key, lease), (h.Stored!.Key, h.Stored.Lease));
        Assert.Equal(0, h.Writes);
    }

    /// <summary>A fingerprinter that cancels the caller's token as it is asked, then waits on its own.</summary>
    private sealed class CancelsTheCaller(CancellationTokenSource caller) : IFingerprinter
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("not asked");

        public async Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            caller.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task Answers_a_status_the_caller_cancels_during_the_device_read_never_throwing()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        using var cancel = new CancellationTokenSource();
        var status = await With(h, fingerprint: new CancelsTheCaller(cancel)).StatusAsync(cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Licensed, status);
    }

    [Fact]
    public async Task Stops_the_device_read_when_the_caller_stops_waiting_not_at_its_bound()
    {
        // On manual time the device read's own bound never passes: only the caller's token ends it.
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var fingerprinter = new HangsUntilTold();
        using var cancel = new CancellationTokenSource();
        var status = With(h, fingerprint: fingerprinter).StatusAsync(cancellationToken: cancel.Token);
        await Eventually.Until(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(Internal.MachineId.TimeoutMs) + TimeSpan.FromSeconds(1)), "the device read");
        cancel.Cancel();
        Assert.Equal(Licensed, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(fingerprinter.Told.Task.IsCompleted);
    }

    // --- what a custom fingerprinter says ----------------------------------------------------

    [Fact]
    public async Task Leaves_out_a_null_alternate_a_custom_fingerprinter_gives_never_throwing()
    {
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        var custom = new FuncFingerprinter(
            () => Task.FromResult("fp-test"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-test", DeviceKind.Machine, ["fp-old", null!])));
        var license = With(h, fingerprint: custom);
        Assert.Equal(Licensed, await license.StatusAsync());
        Assert.Equal(Licensed, await license.RefreshAsync());
        AssertJson.Equal(new JsonArray("fp-test", "fp-old"), h.Http.BodyOf("validate")["fingerprints"]);
    }
}
