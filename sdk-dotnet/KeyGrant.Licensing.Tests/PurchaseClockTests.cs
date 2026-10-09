using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.PurchaseUrlTests;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The two clocks of the purchase and the launch report, told apart in the flow: with the clock guard
/// (<c>lastSeen</c>) a day ahead of the device clock, the link's stamp, its 30-day bound, the day a
/// status still claims and the report's stamp go by the guarded clock, and the claim's 5-minute wait by
/// the device clock. Swapping them fails here.
/// </summary>
public class PurchaseClockTests
{
    private const long Hour = 3_600_000;

    /// <summary>The guarded clock a day ahead of the device clock (at <see cref="Now"/>).</summary>
    private const long Ahead = Now + Day;

    private static HttpResult Pending => new(200, new JsonObject { ["pending"] = true });

    [Fact]
    public async Task Judges_the_link_s_day_by_the_guarded_clock_and_the_wait_by_the_device_clock()
    {
        // A link an hour old by the device clock is a day and an hour old by the guarded one: stale.
        var stale = Create(Held(Hour) with { LastSeen = Ahead });
        stale.Http.On("claim", _ => Pending);
        await stale.License.StatusAsync();
        Assert.False(stale.Http.Called("claim"));
        // Fresh by the guarded clock, but asked a second ago by the device clock: the wait still runs.
        var waiting = Create(Held(-(Day - Hour)) with { LastSeen = Ahead, ClaimAskedAt = Now - 1000 });
        waiting.Http.On("claim", _ => Pending);
        await waiting.License.StatusAsync();
        Assert.False(waiting.Http.Called("claim"));
        // Neither was dropped: a refresh asks.
        await stale.License.RefreshAsync();
        Assert.Equal(1, stale.Http.Count("claim"));
    }

    [Fact]
    public async Task Stamps_a_link_by_the_guarded_clock()
    {
        var h = Create(new StoredState { LastSeen = Ahead });
        await h.License.PurchaseUrlAsync(Link);
        Assert.Equal(Ahead, h.Stored!.PurchaseTokenAt);
    }

    [Fact]
    public async Task Hands_out_a_new_secret_for_one_past_its_30_days_by_the_guarded_clock()
    {
        // 29 days old by the device clock, 30 by the guarded one: not handed out again.
        var h = Create(Held(Offline.PurchaseTokenMs - Day) with { LastSeen = Ahead });
        var url = await h.License.PurchaseUrlAsync(Link);
        Assert.NotEqual(Secret, h.Stored!.PurchaseToken);
        Assert.DoesNotContain(Reference, url);

        // Behind a call that has not finished, it is no secret held: the call throws.
        var (stuck, time) = await StuckBehind(Held(Offline.PurchaseTokenMs - Day) with { LastSeen = Ahead });
        var refused = stuck.License.PurchaseUrlAsync(Link);
        time.Advance(Grace);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("has not finished", error.Message);
    }

    [Fact]
    public async Task Stamps_a_claim_s_wait_by_the_device_clock()
    {
        var h = Create(Held() with { PurchaseTokenAt = Ahead, LastSeen = Ahead });
        h.Http.On("claim", _ => Pending);
        await h.License.StatusAsync();
        Assert.True(h.Http.Called("claim"));
        Assert.Equal(Now, h.Stored!.ClaimAskedAt);
    }

    [Fact]
    public async Task Counts_the_secret_s_30_days_by_the_guarded_clock()
    {
        // 29 days old by the device clock, 30 by the guarded one: dropped, never sent.
        var h = Create(Held(Offline.PurchaseTokenMs - Day) with { LastSeen = Ahead });
        h.Http.On("claim", _ => Pending);
        await h.License.RefreshAsync();
        Assert.False(h.Http.Called("claim"));
        Assert.Null(h.Stored!.PurchaseToken);
    }

    [Fact]
    public async Task Judges_a_launch_report_due_by_the_guarded_clock()
    {
        // Reported an hour ago by the device clock, a day and an hour ago by the guarded one: due.
        var h = Create();
        h.Stored = new StoredState
        {
            TrialStart = Now - 2 * Day,
            TrialEndsAt = Now + 5 * Day,
            TrialLease = h.TrialLease(Now + 5 * Day),
            TrialReportedAt = Now - Hour,
            LastSeen = Ahead,
        };
        await h.License.StatusAsync();
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Equal(Ahead, h.Stored!.TrialReportedAt);
    }

    [Fact]
    public async Task Stamps_a_launch_report_by_the_guarded_clock()
    {
        var h = Create();
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = h.TrialLease(Now + 5 * Day), LastSeen = Ahead };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
        Assert.Equal(Ahead, h.Stored!.TrialReportedAt);
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
