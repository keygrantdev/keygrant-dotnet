using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.PurchaseUrlTests;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The pickup: with no key held and a purchase secret held, a status (and a trial start) asks the server
/// for the key bought with it (the claim), and saves it exactly as an activation saves one.
/// </summary>
public class PickupTests
{
    internal const string BoughtKey = "SLUICE-BUY1-BUY1-BUY1-BUY1";
    internal static readonly LicenseStatus Bought = new() { State = LicenseState.Licensed, Key = BoughtKey };
    internal static readonly LicenseStatus RunningTrial = new() { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 };
    internal static readonly LicenseStatus Unlicensed = new() { State = LicenseState.Unlicensed };

    /// <summary>A running trial, synced within the day (so no launch report is due), with <paramref name="held"/>'s secret.</summary>
    internal static StoredState TrialState(Harness h, StoredState held) => held with
    {
        TrialStart = Now - 2 * Day,
        TrialEndsAt = Now + 5 * Day,
        TrialLease = h.TrialLease(Now + 5 * Day),
        TrialAt = Now - Day / 2,
        LastSeen = Now,
    };

    /// <summary>The server's answer when the key bought with the secret is this device's: as an activation's.</summary>
    internal static HttpResult BoughtAnswer(Harness h, string model = "perpetual") => new(200, new JsonObject
    {
        ["key"] = BoughtKey,
        ["activationId"] = "act_1",
        ["lease"] = h.Sign(BoughtKey, model, 30 * 24 * 3600),
        ["inGrace"] = false,
        ["validUntil"] = null,
    });

    internal static HttpResult Pending => new(200, new JsonObject { ["pending"] = true });

    /// <summary>A fingerprinter whose machine id never reads: only the host-name fingerprint, as an alternate.</summary>
    internal static FuncFingerprinter HostOnly() => new(
        () => Task.FromResult("unused"),
        () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity(null, null, ["fp-host"])));

    private static void AssertNoPurchase(StoredState? state) =>
        Assert.Equal((null, null, null), (state?.PurchaseToken, state?.PurchaseTokenAt, state?.ClaimAskedAt));

    [Fact]
    public async Task Saves_a_bought_key_exactly_as_an_activation_does_clears_the_secret_and_answers_licensed()
    {
        var h = Create(null, new Options { DeviceName = "Rae's laptop" });
        h.Stored = TrialState(h, Held());
        h.Http.On("claim", _ => BoughtAnswer(h));
        Assert.Equal(Bought, await h.License.StatusAsync());
        // The secret, never the reference, and every fingerprint this device answers to, as a validate sends them.
        AssertJson.Equal(
            new JsonObject { ["token"] = Secret, ["fingerprint"] = "fp-test", ["fingerprints"] = new JsonArray("fp-test"), ["name"] = "Rae's laptop", ["major"] = 1 },
            h.Http.BodyOf("claim"));
        var stored = h.Stored!;
        Assert.Equal((BoughtKey, "act_1", 1, Now), (stored.Key, stored.ActivationId, stored.CheckedMajor, stored.VerdictAt));
        AssertNoPurchase(stored);
        // Licensed from then on, offline, and never asked again.
        h.Http.Fail = true;
        Assert.Equal(Bought, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("claim"));
        Assert.False(h.Http.Called("trial"));
    }

    [Fact]
    public async Task Claims_under_the_device_s_own_fingerprint_with_its_kind_and_keys_the_install_by_it()
    {
        var machine = new FuncFingerprinter(
            () => Task.FromResult("fp-machine"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, [])));
        var h = Create(Held(), new Options { Fingerprint = machine });
        h.Http.On("claim", _ => BoughtAnswer(h));
        Assert.Equal(Bought, await h.License.StatusAsync());
        AssertJson.Equal(
            new JsonObject { ["token"] = Secret, ["fingerprint"] = "fp-machine", ["fingerprintKind"] = "machine", ["fingerprints"] = new JsonArray("fp-machine"), ["major"] = 1 },
            h.Http.BodyOf("claim"));
        Assert.Equal(DeviceKind.Machine, h.Stored!.DeviceKind);
    }

    [Fact]
    public async Task On_not_bought_yet_answers_the_trial_keeps_the_secret_and_asks_again_only_after_the_wait()
    {
        var h = Create();
        h.Stored = TrialState(h, Held());
        h.Http.On("claim", _ => Pending);
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
        h.SetClock(Now + Offline.ClaimWaitMs - 1);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("claim"));
        h.SetClock(Now + Offline.ClaimWaitMs);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("claim"));
    }

    [Fact]
    public async Task Asks_at_once_on_refresh_and_when_the_clock_was_set_back_before_the_last_ask()
    {
        var h = Create();
        h.Stored = TrialState(h, Held() with { ClaimAskedAt = Now - 1000 });
        h.Http.On("claim", _ => Pending);
        await h.License.RefreshAsync();
        Assert.Equal(1, h.Http.Count("claim"));
        h.Stored = h.Stored! with { ClaimAskedAt = Now + 60_000 };
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("claim"));
    }

    [Fact]
    public async Task Keeps_the_secret_when_nothing_answers_or_the_answer_carries_no_error_word()
    {
        var answers = new HttpResult?[]
        {
            null,
            new(503, new JsonObject { ["error"] = "internal" }),
            new(404, JsonValue.Create("<html>")),
            new(200, JsonValue.Create("portal")),
        };
        foreach (var answer in answers)
        {
            var h = Create(Held());
            if (answer is null) h.Http.Fail = true;
            else h.Http.On("claim", _ => answer);
            Assert.Equal(Unlicensed, await h.License.StatusAsync());
            Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
        }
    }

    [Fact]
    public async Task Drops_the_secret_on_an_error_word_and_on_a_key_whose_lease_does_not_license_this_build()
    {
        var h = Create();
        var elsewhere = h.SignLease(new LeaseInput { Sub = BoughtKey, Product = "another-product", TtlSeconds = 86_400 });
        var answers = new[]
        {
            new HttpResult(409, new JsonObject { ["error"] = "spent" }),
            new HttpResult(403, new JsonObject { ["error"] = "pickup-off" }),
            new HttpResult(403, new JsonObject { ["error"] = "limit" }),
            new HttpResult(200, new JsonObject { ["key"] = BoughtKey, ["activationId"] = "act_1", ["lease"] = elsewhere }),
        };
        foreach (var answer in answers)
        {
            h.Stored = TrialState(h, Held());
            h.Http.On("claim", _ => answer);
            Assert.Equal(RunningTrial, await h.License.StatusAsync());
            AssertNoPurchase(h.Stored);
            Assert.Null(h.Stored!.Key);
        }
        await h.License.RefreshAsync();
        Assert.Equal(answers.Length, h.Http.Count("claim"));
    }

    [Fact]
    public async Task Drops_a_secret_past_its_bound_without_sending_it()
    {
        var h = Create(Held(Offline.PurchaseTokenMs));
        Assert.Equal(Unlicensed, await h.License.RefreshAsync());
        Assert.False(h.Http.Called("claim"));
        Assert.Null(h.Stored!.PurchaseToken);
    }

    [Fact]
    public async Task Saves_a_drop_about_its_secret_so_a_secret_another_process_made_meanwhile_stays()
    {
        var h = Create(Held());
        h.Http.On("claim", _ =>
        {
            h.Stored = new StoredState { PurchaseToken = "kgp_another-process-made-this-one-meanwhile", PurchaseTokenAt = Now };
            return new HttpResult(409, new JsonObject { ["error"] = "spent" });
        });
        await h.License.StatusAsync();
        Assert.Equal(("kgp_another-process-made-this-one-meanwhile", (long?)Now), (h.Stored!.PurchaseToken, h.Stored.PurchaseTokenAt));
    }

    [Theory]
    [InlineData(false, Wire.StatusCheckTimeoutMs)]
    [InlineData(true, 15_000)]
    public async Task Bounds_a_status_s_claim_as_a_status_check_and_a_refresh_s_by_the_HTTP_bound_keeping_the_secret(bool refresh, int boundMs)
    {
        var time = new ManualTime();
        var h = Create(Held(), new Options { Time = time });
        h.Http.Hang = true;
        var bound = TimeSpan.FromMilliseconds(boundMs);
        var status = refresh ? h.License.RefreshAsync() : h.License.StatusAsync();
        await Eventually.Until(() => h.Http.Called("claim") && time.HasTimerDueIn(bound), "the bounded claim");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(status.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Unlicensed, await status.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((Secret, (long?)Now), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_the_device_2_s_for_a_status_s_claim_and_sends_none_and_saves_nothing_when_it_cannot_tell(bool refresh)
    {
        var time = new ManualTime();
        var hanging = new FuncFingerprinter(() => new TaskCompletionSource<string>().Task);
        var h = Create(Held(), new Options { Fingerprint = hanging, Time = time });
        var bound = TimeSpan.FromMilliseconds(MachineId.TimeoutMs);
        var call = refresh ? h.License.RefreshAsync() : h.License.StatusAsync();
        await Eventually.Until(() => Volatile.Read(ref hanging.Calls) == 1 && time.HasTimerDueIn(bound), "the claim's machine-id read");
        time.Advance(bound - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Unlicensed, await call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Http.Called("claim"));
        Assert.Equal((Secret, (long?)null), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Records_nothing_of_a_claim_the_caller_stopped_waiting_for_and_answers_as_with_no_secret()
    {
        var h = Create(Held());
        using var cancel = new CancellationTokenSource();
        h.Http.On("claim", async _ =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancel.Token);
            return new HttpResult(500, null);
        });
        Assert.Equal(Unlicensed, await h.License.StatusAsync(cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Writes);
        Assert.Equal((Secret, (long?)null), (h.Stored!.PurchaseToken, h.Stored.ClaimAskedAt));
    }

    [Fact]
    public async Task Answers_a_bought_key_even_when_the_disk_refuses_it_and_the_next_status_asks_again_with_the_same_fingerprints()
    {
        var h = Create(Held());
        h.Http.On("claim", _ => BoughtAnswer(h));
        h.FailWrites();
        Assert.Equal(Bought, await h.License.StatusAsync());
        h.FailWrites(false);
        Assert.Equal(Bought, await h.License.StatusAsync());
        var bodies = h.Http.Calls.Where(c => c.Action == "claim").Select(c => c.Body).ToList();
        Assert.Equal(2, bodies.Count);
        AssertJson.Equal(bodies[0], bodies[1]);
        AssertJson.Equal(new JsonArray("fp-test"), bodies[0]["fingerprints"]);
        Assert.Equal(BoughtKey, h.Stored!.Key);
    }

    [Fact]
    public async Task Sends_every_fingerprint_the_run_read_when_the_device_is_not_held_too()
    {
        // Not held, a device has no kind and cannot tell; its own fingerprint and its host name go all the same.
        var machine = new FuncFingerprinter(
            () => Task.FromResult("unused"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-own", DeviceKind.Machine, ["fp-host"])));
        var h = Create(Held(), new Options { DeviceHold = false, Fingerprint = machine });
        h.Http.On("claim", _ => Pending);
        await h.License.StatusAsync();
        AssertJson.Equal(
            new JsonObject { ["token"] = Secret, ["fingerprint"] = "fp-own", ["fingerprints"] = new JsonArray("fp-own", "fp-host"), ["major"] = 1 },
            h.Http.BodyOf("claim"));
    }

    [Fact]
    public async Task Sends_the_server_at_most_4_fingerprints_own_first_each_of_1_to_200_characters_never_cut()
    {
        var many = new FuncFingerprinter(
            () => Task.FromResult("unused"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity(
                "fp-own",
                DeviceKind.Machine,
                ["fp-a1", new string('x', 201), "fp-a2", "", new string('y', 200), "fp-a4"])));
        var h = Create(Held(), new Options { Fingerprint = many });
        h.Http.On("claim", _ => Pending);
        await h.License.StatusAsync();
        var body = h.Http.BodyOf("claim");
        Assert.Equal("fp-own", body["fingerprint"]!.GetValue<string>());
        AssertJson.Equal(new JsonArray("fp-own", "fp-a1", "fp-a2", new string('y', 200)), body["fingerprints"]);
    }

    [Fact]
    public async Task Never_claims_under_a_host_name_fallback_from_a_status_or_a_refresh_no_ask_and_no_wait_started()
    {
        var h = Create(Held(), new Options { Fingerprint = HostOnly() });
        h.Http.On("claim", _ => Pending);
        Assert.Equal(Unlicensed, await h.License.StatusAsync());
        Assert.Equal(Unlicensed, await h.License.RefreshAsync());
        Assert.False(h.Http.Called("claim"));
        Assert.Null(h.Stored!.ClaimAskedAt);
    }

    [Fact]
    public async Task Asks_unforced_only_for_a_day_after_the_link_then_only_a_refresh_asks()
    {
        var fresh = Create(Held(Offline.ClaimEagerMs - 1));
        fresh.Http.On("claim", _ => Pending);
        await fresh.License.StatusAsync();
        Assert.Equal(1, fresh.Http.Count("claim"));

        var stale = Create(Held(Offline.ClaimEagerMs));
        stale.Http.On("claim", _ => Pending);
        Assert.Equal(Unlicensed, await stale.License.StatusAsync());
        Assert.False(stale.Http.Called("claim"));
        await stale.License.RefreshAsync();
        Assert.Equal(1, stale.Http.Count("claim"));
    }

    [Fact]
    public async Task A_link_handed_out_again_while_a_claim_is_out_keeps_its_cleared_wait()
    {
        var h = Create(Held(Day / 2));
        h.Http.On("claim", _ =>
        {
            // Another process's PurchaseUrlAsync hands the same secret's link out again, clearing the wait.
            h.Stored = h.Stored! with { PurchaseTokenAt = Now, ClaimAskedAt = null };
            return Pending;
        });
        await h.License.StatusAsync();
        Assert.Equal((Secret, (long?)Now, (long?)null), (h.Stored!.PurchaseToken, h.Stored.PurchaseTokenAt, h.Stored.ClaimAskedAt));
    }

    [Fact]
    public async Task Asks_nothing_while_a_key_is_held_or_behind_a_call_that_has_not_finished()
    {
        var keyed = Create(Held() with { Key = "KEY-1" });
        await keyed.License.RefreshAsync();
        Assert.False(keyed.Http.Called("claim"));

        var (h, time) = await StuckBehind(Held());
        var later = h.License.StatusAsync(true);
        time.Advance(Grace);
        Assert.Equal(Unlicensed with { Stalled = true }, await later.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Http.Called("claim"));
    }

    [Fact]
    public async Task A_typed_key_clears_the_secret_so_a_later_deactivate_leaves_no_pickup_pending()
    {
        var h = Create(Held());
        h.Http.Lease("activate", h.Sign("KEY-1", "perpetual", 30 * 24 * 3600));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync("KEY-1"));
        AssertNoPurchase(h.Stored);
    }

    [Fact]
    public async Task Deactivate_clears_the_secret()
    {
        var h = Create(Held() with { Key = "KEY-1", ActivationId = "act_1" });
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.DeactivateAsync();
        AssertNoPurchase(h.Stored);
    }

    [Fact]
    public async Task A_key_bought_from_a_trial_model_s_own_price_licenses_the_app_and_is_never_read_as_a_trial()
    {
        // Item 7: a licence lease whose model is trial is judged as any licence lease, never as a trial.
        var h = Create(Held());
        h.Http.On("claim", _ => BoughtAnswer(h, "trial"));
        Assert.Equal(Bought, await h.License.StatusAsync());
        h.Http.Fail = true;
        Assert.Equal(Bought, await h.License.StatusAsync());
        Assert.Null(h.Stored!.TrialLease);
        Assert.Null(h.Stored.TrialStart);
    }

    [Fact]
    public async Task A_typed_key_whose_lease_model_is_trial_is_licensed_offline()
    {
        var h = Create();
        h.Stored = new StoredState { Key = "KEY-1", ActivationId = "act_1", Lease = h.Sign("KEY-1", "trial", 30 * 24 * 3600), CheckedMajor = 1, LastSeen = Now };
        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = "KEY-1" }, await h.License.StatusAsync());
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Http.Calls);
    }
}
