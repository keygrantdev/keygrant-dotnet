using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.PickupTests;
using static KeyGrant.Licensing.Tests.PurchaseUrlTests;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What a claim may and may not leave behind: a bought lease that does not verify is refused, the
/// secret goes in no request but the claim, the claim's lease is judged at the server's time, and its
/// waits and drops are saved about the secret, best-effort.
/// </summary>
public class ClaimSafetyTests
{
    public static TheoryData<string> Unverifiable => new() { "signed by another key", "its payload tampered" };

    /// <summary>A claim's 200 for the bought key whose lease does not verify here.</summary>
    private static HttpResult UnverifiableAnswer(Harness h, string which)
    {
        var lease = which == "signed by another key"
            ? Create().Sign(BoughtKey, "perpetual", 30 * 24 * 3600)
            : Tampered(h.Sign(BoughtKey, "perpetual", 30 * 24 * 3600));
        return new HttpResult(200, new JsonObject { ["key"] = BoughtKey, ["activationId"] = "act_1", ["lease"] = lease });
    }

    /// <summary>The lease with one claim of its payload changed (its <c>lim</c>), the payload still well-formed and the signature kept.</summary>
    private static string Tampered(string lease)
    {
        var parts = lease.Split('.');
        var text = parts[1].Replace('-', '+').Replace('_', '/');
        var payload = JsonNode.Parse(Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=')))!.AsObject();
        payload["lim"] = 9;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{parts[0]}.{encoded}.{parts[2]}";
    }

    private static void AssertRefused(StoredState? stored)
    {
        Assert.Equal((null, null, null), (stored?.Key, stored?.ActivationId, stored?.Lease));
        Assert.Equal((null, null, null), (stored?.PurchaseToken, stored?.PurchaseTokenAt, stored?.ClaimAskedAt));
    }

    [Theory]
    [MemberData(nameof(Unverifiable))]
    public async Task Refuses_at_a_status_a_bought_lease_that_does_not_verify_dropping_the_secret(string which)
    {
        var h = Create();
        h.Stored = TrialState(h, Held());
        h.Http.On("claim", _ => UnverifiableAnswer(h, which));
        Assert.Equal(RunningTrial, await h.License.StatusAsync());
        AssertRefused(h.Stored);
    }

    [Theory]
    [MemberData(nameof(Unverifiable))]
    public async Task Refuses_at_a_trial_start_a_bought_lease_that_does_not_verify_and_asks_for_the_trial(string which)
    {
        var h = Create(Held());
        h.Http.On("claim", _ => UnverifiableAnswer(h, which));
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = h.TrialLease(Now + 5 * Day) });
        Assert.Equal(RunningTrial, await h.License.StartTrialAsync());
        Assert.Equal(new[] { "claim", "trial" }, h.Http.Calls.Select(c => c.Action));
        AssertRefused(h.Stored);
    }

    [Fact]
    public async Task Sends_the_secret_in_no_request_but_the_claim()
    {
        var h = Create();
        var trialLease = h.TrialLease(Now + 5 * Day);
        h.Stored = Held() with { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = trialLease, LastSeen = Now };
        h.Http.On("claim", _ => new HttpResult(200, new JsonObject { ["pending"] = true }));
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now - 2 * Day, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = trialLease });
        var lease = h.Sign("KEY-1", "perpetual", 30 * 24 * 3600);
        h.Http.Lease("activate", lease);
        h.Http.Lease("validate", lease);
        await h.License.StatusAsync(); // a claim and the launch report
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        await h.License.StartTrialAsync(); // a claim and the trial ask
        await h.License.ActivateAsync("KEY-1");
        // The activation cleared the secret: store it again beside the key, so the validate and the
        // deactivate each have one to leak.
        h.Stored = h.Stored! with { PurchaseToken = Secret, PurchaseTokenAt = Now };
        await h.License.RefreshAsync();
        h.Stored = h.Stored! with { PurchaseToken = Secret, PurchaseTokenAt = Now };
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.DeactivateAsync();
        Assert.Equal(new[] { "claim", "trial", "claim", "trial", "activate", "validate", "deactivate" }, h.Http.Calls.Select(c => c.Action));
        Assert.All(h.Http.Calls, c => Assert.True(c.Body.ToJsonString().Contains(Secret) == (c.Action == "claim"), c.Action));
    }

    [Fact]
    public async Task Judges_the_bought_lease_at_the_server_s_time_bringing_the_clock_guard_back()
    {
        // The guard 60 days ahead (the clock once jumped): judged by it, the lease signed now would be expired.
        var h = Create(Held() with { PurchaseTokenAt = Now + 60 * Day, LastSeen = Now + 60 * Day });
        h.Http.On("claim", _ => BoughtAnswer(h));
        Assert.Equal(Bought, await h.License.StatusAsync());
        Assert.Equal((BoughtKey, (long?)Now), (h.Stored!.Key, h.Stored.LastSeen));
    }

    [Fact]
    public async Task Judges_the_bought_lease_at_the_server_s_time_even_when_the_disk_refuses_the_clock_guard()
    {
        // The guard is brought back best-effort: refused, the lease is still judged at the server's time.
        var h = Create(Held() with { PurchaseTokenAt = Now + 60 * Day, LastSeen = Now + 60 * Day });
        h.Http.On("claim", _ => BoughtAnswer(h));
        h.FailWrites();
        Assert.Equal(Bought, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Saves_a_drop_past_the_bound_about_its_secret_so_one_another_process_made_meanwhile_stays()
    {
        var h = Create(Held(Offline.PurchaseTokenMs));
        // Read 1 is the status's; read 2 is the drop's save, after another process made a new secret.
        h.SavedElsewhereBeforeRead(2, new StoredState { PurchaseToken = "another-process-made-this-one", PurchaseTokenAt = Now });
        Assert.Equal(Unlicensed, await h.License.StatusAsync());
        Assert.Equal(("another-process-made-this-one", (long?)Now), (h.Stored!.PurchaseToken, h.Stored.PurchaseTokenAt));
    }

    [Fact]
    public async Task Drops_a_spent_secret_even_after_its_link_was_handed_out_again()
    {
        // A drop carries no link stamp: a spent secret is dead however recently its link went out.
        var h = Create(Held());
        h.Http.On("claim", _ =>
        {
            h.Stored = Held() with { PurchaseTokenAt = Now + 1000 };
            return new HttpResult(409, new JsonObject { ["error"] = "spent" });
        });
        await h.License.StatusAsync();
        Assert.Equal((null, null), (h.Stored!.PurchaseToken, h.Stored.PurchaseTokenAt));
    }

    [Fact]
    public async Task Answers_when_the_disk_refuses_a_wait_or_a_drop()
    {
        var waiting = Create(Held());
        waiting.Http.Fail = true;
        waiting.FailWrites();
        Assert.Equal(Unlicensed, await waiting.License.StatusAsync());

        var dropping = Create(Held());
        dropping.Http.Error("claim", 409, "spent");
        dropping.FailWrites();
        Assert.Equal(Unlicensed, await dropping.License.StatusAsync());

        var pastBound = Create(Held(Offline.PurchaseTokenMs));
        pastBound.FailWrites();
        Assert.Equal(Unlicensed, await pastBound.License.StatusAsync());
    }
}
