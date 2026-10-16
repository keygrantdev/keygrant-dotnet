using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.Metered;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// <c>UseAsync(meter, n)</c>: exact online, from the offline allowance when the server gives no answer, with the unsynced
/// spends kept in the stored state and reported by the next request that can carry them.
/// </summary>
public class UseTests
{
    private static UseResult Spent(long left) => UseResult.Spent(left);

    private static UseResult SpentOffline(long left) => UseResult.Spent(left, offline: true);

    private static UseResult Refused(UseError error, long? left = null) => UseResult.Refused(error, left);

    // --- use online -----------------------------------------------------------------------------------

    [Fact]
    public async Task Spends_exactly_sending_the_key_the_seat_the_meter_the_amount_and_a_fresh_id_and_saves_the_figures_it_was_given()
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(89, Now + 1));
        Assert.Equal(Spent(89), await h.License.UseAsync("exports"));
        var body = UseBody(h);
        Assert.Equal((Key, "act_1", "exports", 1L, 1L), (Str(body["key"]), Str(body["activationId"]), Str(body["meter"]), Long(body["n"]), Long(body["major"])));
        Assert.Matches("^[A-Za-z0-9_-]{22}$", Str(body["id"])!);
        Assert.Single(body["kids"]!.AsArray());
        Assert.Single(body["fingerprints"]!.AsArray());
        Assert.False(body.ContainsKey("spends"));
        Assert.False(body.ContainsKey("keptMajor"));
        Assert.Equal(new MeterFigure(89, 100, 10, false, Now + 1), Assert.Single(h.Stored!.MeterFigures!).Value);
        Assert.Empty(Journal(h));
    }

    [Fact]
    public async Task Spends_a_batch_whole_in_one_request()
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(50));
        Assert.Equal(Spent(50), await h.License.UseAsync("exports", 40));
        Assert.Equal(40, Long(UseBody(h)["n"]));
        Assert.Equal(1, h.Http.Count("use"));
    }

    [Fact]
    public async Task Makes_a_new_id_for_each_call_two_calls_are_two_spends()
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(89));
        await h.License.UseAsync("exports");
        await h.License.UseAsync("exports");
        Assert.NotEqual(Str(UseBody(h, 0)["id"]), Str(UseBody(h, 1)["id"]));
    }

    [Fact]
    public async Task Answers_from_the_figure_the_server_gave_net_of_what_this_device_has_not_reported()
    {
        var h = Holding(s => s with { Spends = [Entry(n: 3)] });
        h.Http.On("use", _ => Metered.Spent(80, Now));
        // The server's 80 does not count the 3 unreported, so 77 is what the customer has.
        Assert.Equal(Spent(77), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Answers_from_the_base_figures_as_they_were_when_the_server_gave_none_for_the_meter()
    {
        var h = Holding();
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject(), ["at"] = Now }));
        Assert.Equal(Spent(90), await h.License.UseAsync("exports"));
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true }));
        Assert.Equal(Spent(90), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Answers_a_meter_the_lease_and_the_figures_both_lack_with_none_left()
    {
        var h = Holding();
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject(), ["at"] = Now }));
        Assert.Equal(Spent(0), await h.License.UseAsync("credits"));
    }

    [Fact]
    public async Task Keeps_a_figure_the_lease_is_newer_than_only_until_the_lease_is_renewed()
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(70, Now + 5_000));
        await h.License.UseAsync("exports");
        Assert.Equal(70, (await h.License.StatusAsync()).Meters["exports"].Left);
        // A renewal brings a lease signed after that figure, whose own figures win.
        var renewed = Lease(h, Exports(60, 100, 10), now: Now + 10_000);
        h.Http.Lease("validate", renewed);
        h.SetClock(Now + 10_000);
        Assert.Equal(60, (await h.License.RefreshAsync()).Meters["exports"].Left);
        Assert.Equal(60, (await h.License.StatusAsync()).Meters["exports"].Left);
    }

    [Fact]
    public async Task Answers_used_up_with_what_the_server_says_is_left_saves_the_figures_and_the_reports_it_confirms_and_spends_nothing_offline_after()
    {
        var h = Holding(s => s with { Spends = [Entry(Id('B'), n: 2, at: LongAgo, kind: SpendKind.Unanswered)] });
        h.Http.On("use", _ => new HttpResult(409, new JsonObject
        {
            ["error"] = "used-up",
            ["left"] = 1,
            ["meters"] = Exports(1, 100, 10),
            ["spends"] = new JsonArray(new JsonObject { ["id"] = Id('B'), ["n"] = 2 }),
            ["at"] = Now,
        }));
        Assert.Equal(Refused(UseError.UsedUp, 1), await h.License.UseAsync("exports", 5));
        Assert.Empty(Journal(h));
        Assert.Equal(1, h.Stored!.MeterFigures!["exports"].Left);
        Assert.Equal(Now, h.Stored.MeterFigures["exports"].At);
        // The server's word stands offline too: one is left, and a spend of two is past it.
        h.Http.Fail = true;
        h.SetClock(Now + 2 * Minute);
        Assert.Equal(Refused(UseError.OfflineLimit, 1), await h.License.UseAsync("exports", 2));
    }

    [Fact]
    public async Task A_meter_used_up_unknown_or_past_its_allowance_refuses_the_spend_and_never_the_licence()
    {
        var h = Holding();
        async Task StillLicensed(string because)
        {
            var status = await h.License.StatusAsync();
            Assert.True(status is { State: LicenseState.Licensed, Reason: null, Key: Key }, $"{because}: {status}");
            Assert.Null(h.Stored!.Refused);
            Assert.NotNull(h.Stored.Lease);
        }
        h.Http.Answer("use", 409, new JsonObject { ["error"] = "used-up", ["left"] = 0, ["meters"] = Exports(0, 100, 10), ["at"] = Now });
        Assert.Equal(Refused(UseError.UsedUp, 0), await h.License.UseAsync("exports", 5));
        await StillLicensed("used up");
        Assert.Equal(0, (await h.License.StatusAsync()).Meters["exports"].Left);
        h.Http.Answer("use", 404, new JsonObject { ["error"] = "unknown-meter" });
        Assert.Equal(Refused(UseError.NoMeter), await h.License.UseAsync("exports"));
        await StillLicensed("an unknown meter");
        h.Http.Fail = true;
        h.SetClock(Now + 2 * Minute);
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        await StillLicensed("past the allowance");
        // And a refresh asks and renews as it always did: no meter was read to decide it.
        h.Http.Fail = false;
        h.Http.Lease("validate", Lease(h, TwoMeters()));
        Assert.Equal(LicenseState.Licensed, (await h.License.RefreshAsync()).State);
    }

    [Fact]
    public async Task Answers_used_up_with_no_left_when_the_answer_gave_none()
    {
        var h = Holding();
        h.Http.Answer("use", 409, new JsonObject { ["error"] = "used-up" });
        Assert.Equal(Refused(UseError.UsedUp), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Reads_the_server_s_words_on_any_status_but_a_200_a_5xx_and_a_429()
    {
        var h = Holding();
        h.Http.Answer("use", 400, new JsonObject { ["error"] = "used-up", ["left"] = 0 });
        Assert.Equal(Refused(UseError.UsedUp, 0), await h.License.UseAsync("exports"));
        h.Http.Answer("use", 200, new JsonObject { ["error"] = "used-up", ["left"] = 0 });
        // A 200 is a spend only with spent: true; anything else is no answer: made from the allowance, in doubt.
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        Assert.Equal([(SpendKind.Unanswered, 1L)], KindsAndCounts(h));
    }

    [Fact]
    public async Task Answers_no_meter_for_a_meter_the_server_does_not_know_and_confirms_the_reports_it_carried()
    {
        var h = Holding(s => s with { Spends = [Entry(Id('C'), kind: SpendKind.Unanswered, at: LongAgo)] });
        h.Http.Answer("use", 404, new JsonObject { ["error"] = "unknown-meter", ["spends"] = new JsonArray(new JsonObject { ["id"] = Id('C'), ["n"] = 1 }) });
        Assert.Equal(Refused(UseError.NoMeter), await h.License.UseAsync("exports"));
        Assert.Empty(Journal(h));
    }

    public static TheoryData<string, UseError, string> Verdicts => new()
    {
        { "revoked", UseError.Revoked, "revoked" },
        { "expired", UseError.Revoked, "revoked" },
        { "deactivated", UseError.Revoked, "revoked" },
        { "upgrade-required", UseError.Upgrade, "upgrade" },
        { "update-required", UseError.Outdated, "outdated" },
        { "device", UseError.Device, "device" },
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public async Task Stores_a_verdict_as_a_validate_s_and_answers_it_as_status_will(string word, UseError error, string refusal)
    {
        var h = Holding();
        h.Http.Error("use", 403, word);
        Assert.Equal(Refused(error), await h.License.UseAsync("exports"));
        Assert.Equal(Names.RefusalOf(refusal), h.Stored!.Refused);
        var status = await h.License.StatusAsync();
        Assert.Equal((LicenseState.Invalid, refusal), (status.State, Names.Of(status.Reason!.Value)));
        // The refusal stands: the next spend is not licensed, and asks nothing.
        Assert.Equal(Refused(UseError.NotLicensed), await h.License.UseAsync("exports"));
        Assert.Equal(1, h.Http.Count("use"));
    }

    [Fact]
    public async Task Records_the_reports_a_verdict_confirms()
    {
        var h = Holding(s => s with { Spends = [Entry(Id('D'), kind: SpendKind.Void, at: LongAgo)] });
        h.Http.Answer("use", 403, new JsonObject { ["error"] = "revoked", ["spends"] = new JsonArray(new JsonObject { ["id"] = Id('D'), ["void"] = true }) });
        await h.License.UseAsync("exports");
        Assert.Empty(Journal(h));
    }

    [Fact]
    public async Task Waits_the_use_timeout_for_the_server_to_the_millisecond_and_spends_from_the_allowance_when_nothing_answers()
    {
        var time = new ManualTime();
        var h = Holding(options: new Options { Time = time });
        h.Http.Hang = true;
        var asking = h.License.UseAsync("exports");
        await Eventually.Until(() => h.Http.Called("use") && time.HasTimerDueIn(TimeSpan.FromMilliseconds(Meters.UseTimeoutMs)), "the bounded use");
        time.Advance(TimeSpan.FromMilliseconds(Meters.UseTimeoutMs - 1));
        await Eventually.Quiet();
        Assert.False(asking.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(SpentOffline(89), await asking.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Waits_for_the_http_bound_instead_when_that_is_shorter()
    {
        var time = new ManualTime();
        var h = Holding(options: new Options { Time = time, HttpTimeout = TimeSpan.FromSeconds(1) });
        h.Http.Hang = true;
        var asking = h.License.UseAsync("exports");
        await Eventually.Until(() => h.Http.Called("use") && time.HasTimerDueIn(TimeSpan.FromSeconds(1)), "the bounded use");
        time.Advance(TimeSpan.FromMilliseconds(999));
        await Eventually.Quiet();
        Assert.False(asking.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var answer = await asking.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(answer.Ok);
        Assert.True(answer.Offline);
    }

    // --- use with no answer ---------------------------------------------------------------------------

    [Fact]
    public async Task Spends_from_the_allowance_journals_the_spend_under_the_id_it_sent_and_waits_a_minute_before_asking_again()
    {
        var h = Holding();
        h.Http.Fail = true;
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        var sent = Str(UseBody(h)["id"])!;
        Assert.Equal([new SpendEntry(sent, "exports", 1, Now, SpendKind.Unanswered)], Journal(h));
        Assert.Equal((Now, Offline.RetryUnansweredMs), (h.Stored!.BackoffSince, h.Stored.BackoffMs));
        // Inside the wait it asks nothing: the spend is made with no request, and grows one entry.
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports", 2));
        Assert.Equal(1, h.Http.Count("use"));
        Assert.Equal([(SpendKind.Unanswered, 1), (SpendKind.Offline, 2)], KindsAndCounts(h));
        Assert.Equal(SpentOffline(86), await h.License.UseAsync("exports"));
        Assert.Equal([(SpendKind.Unanswered, 1), (SpendKind.Offline, 3)], KindsAndCounts(h));
    }

    [Fact]
    public async Task Refuses_a_spend_past_the_allowance_and_past_what_is_left()
    {
        var h = Holding(Exports(90, 100, 3));
        h.Http.Fail = true;
        Assert.True((await h.License.UseAsync("exports", 2)).Ok);
        Assert.Equal(Refused(UseError.OfflineLimit, 88), await h.License.UseAsync("exports", 2));
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports", 1));
        Assert.Equal(Refused(UseError.OfflineLimit, 87), await h.License.UseAsync("exports", 1));
        var low = Holding(Exports(2, 100, 50));
        low.Http.Fail = true;
        Assert.Equal(Refused(UseError.OfflineLimit, 2), await low.License.UseAsync("exports", 3));
        Assert.Equal(SpentOffline(0), await low.License.UseAsync("exports", 2));
        Assert.Equal(Refused(UseError.OfflineLimit, 0), await low.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Refuses_every_spend_with_no_answer_on_a_meter_whose_allowance_is_0()
    {
        var h = Holding();
        h.Http.Fail = true;
        Assert.Equal(Refused(UseError.OfflineLimit, 5), await h.License.UseAsync("renders"));
        // Online it spends as any other.
        h.Http.Fail = false;
        h.SetClock(Now + 2 * Minute);
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now + 2 * Minute }));
        Assert.Equal(Spent(4), await h.License.UseAsync("renders"));
    }

    [Fact]
    public async Task Journals_a_refusal_of_a_spend_that_was_sent_as_a_void_for_the_server_to_take_it_back_if_it_recorded_it()
    {
        var h = Holding(Exports(90, 100, 1));
        h.Http.Fail = true;
        await h.License.UseAsync("exports");
        h.SetClock(Now + 2 * Minute);
        Assert.Equal(Refused(UseError.OfflineLimit, 89), await h.License.UseAsync("exports"));
        var journal = Journal(h);
        Assert.Equal((SpendKind.Unanswered, 1), (journal[0].Kind, journal[0].N));
        Assert.Equal(new SpendEntry(Str(UseBody(h, 1)["id"])!, "exports", 1, Now + 2 * Minute, SpendKind.Void), journal[1]);
    }

    [Fact]
    public async Task Asks_again_for_a_spend_the_allowance_refuses_though_the_minute_has_not_passed_and_journals_it_as_a_void_when_nothing_answers()
    {
        var h = Holding(Exports(90, 100, 1));
        h.Http.Fail = true;
        await h.License.UseAsync("exports");
        // Inside the wait, but the allowance cannot cover it: asked all the same, so an online customer is not refused.
        Assert.Equal(Refused(UseError.OfflineLimit, 89), await h.License.UseAsync("exports"));
        Assert.Equal(2, h.Http.Count("use"));
        Assert.Equal([SpendKind.Unanswered, SpendKind.Void], Journal(h).Select(e => e.Kind));
    }

    [Fact]
    public async Task Serves_a_customer_who_is_back_online_at_once_though_the_last_outage_left_a_wait()
    {
        var h = Holding();
        h.Http.Fail = true;
        // The outage: a spend the allowance covers is made with no answer, and the minute's wait begins.
        Assert.True((await h.License.UseAsync("exports")).Offline);
        h.Http.Fail = false;
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now }));
        Assert.Equal(Spent(4), await h.License.UseAsync("renders"));
        h.Http.On("use", _ => Metered.Spent(70));
        // The server's 70 less the one spend not yet reported.
        Assert.Equal(Spent(69), await h.License.UseAsync("exports", 50));
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["credits"] = Meter(9, 10, 0) }, ["at"] = Now }));
        Assert.Equal(Spent(9), await h.License.UseAsync("credits"));
        // A spend the allowance covers still waits out the minute.
        var asked = h.Http.Count("use");
        Assert.True((await h.License.UseAsync("exports")).Offline);
        Assert.Equal(asked, h.Http.Count("use"));
    }

    public static TheoryData<int, string?> NoAnswers => new()
    {
        { 500, "{\"error\":\"boom\"}" },
        { 503, null },
        { 429, "{\"error\":\"rate-limited\"}" },
    };

    [Theory]
    [MemberData(nameof(NoAnswers))]
    public async Task Counts_a_5xx_and_a_429_as_no_answer_saves_nothing_of_them_and_journals_the_spend_in_doubt(int status, string? json)
    {
        var h = Holding();
        h.Http.On("use", _ => new HttpResult(status, json is null ? null : JsonNode.Parse(json)));
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        Assert.Equal((SpendKind.Unanswered, 1), KindsAndCounts(h)[0]);
        Assert.Null(h.Stored!.BackoffSince);
    }

    public static TheoryData<int, string?> UnusableAnswers => new()
    {
        { 200, "{\"spent\":false}" },
        { 200, "\"ok\"" },
        { 200, null },
        { 400, "{\"error\":\"invalid-request\"}" },
        { 404, "{\"error\":\"invalid-key\"}" },
        { 403, "{\"error\":\"something-new\"}" },
        { 401, "\"denied\"" },
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task Counts_any_answer_it_cannot_use_as_no_answer_too_and_saves_nothing_of_it(int status, string? json)
    {
        var h = Holding();
        h.Http.On("use", _ => new HttpResult(status, json is null ? null : JsonNode.Parse(json)));
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        Assert.Equal(SpendKind.Unanswered, Journal(h)[0].Kind);
        Assert.Null(h.Stored!.BackoffSince);
        Assert.Null(h.Stored.Refused);
    }

    [Fact]
    public async Task Does_not_take_the_confirmations_of_an_answer_it_cannot_use()
    {
        var h = Holding(s => s with { Spends = [Entry(Id('E'), kind: SpendKind.Unanswered)] });
        h.Http.Answer("use", 500, new JsonObject { ["spends"] = new JsonArray(new JsonObject { ["id"] = Id('E'), ["n"] = 1 }) });
        h.SetClock(Now + 2 * Minute);
        await h.License.UseAsync("exports");
        Assert.Contains(Id('E'), Journal(h).Select(e => e.Id));
    }

    [Fact]
    public async Task Asks_again_once_the_minute_is_over_and_keeps_the_journal_s_own_wait_two_minutes_after_two_unanswered_then_four_to_an_hour()
    {
        var h = Holding(Exports(1_000_000, 1_000_000, 1_000_000));
        h.Http.Fail = true;
        var at = Now;
        long[] waits = [1, 2, 4, 8, 16, 32, 60, 60];
        for (var i = 0; i < waits.Length; i++)
        {
            h.SetClock(at);
            await h.License.UseAsync("exports");
            Assert.Equal(i + 1, h.Http.Count("use"));
            // One millisecond short of the wait: not asked.
            h.SetClock(at + waits[i] * Minute - 1);
            await h.License.UseAsync("exports");
            Assert.Equal(i + 1, h.Http.Count("use"));
            at += waits[i] * Minute;
        }
    }

    [Fact]
    public async Task Keeps_the_journal_s_wait_when_the_device_clock_is_set_back_as_the_guarded_clock_does_not_go_back()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.SetClock(Now + 10 * Minute);
        await h.License.UseAsync("exports");
        h.SetClock(Now);
        await h.License.UseAsync("exports");
        Assert.Equal(1, h.Http.Count("use"));
    }

    [Fact]
    public async Task Asks_at_once_when_a_stored_no_answer_wait_begins_after_the_device_clock()
    {
        var h = Holding(s => s with { BackoffSince = Now + 10 * Minute, BackoffMs = Offline.RetryUnansweredMs });
        h.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Spent(89), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Asks_when_the_newest_unanswered_spend_was_opened_after_the_guarded_clock_not_days_later()
    {
        var ahead = Holding(s => s with { Spends = [Entry(kind: SpendKind.Unanswered, at: Now + 5 * Day)] });
        ahead.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Spent(88), await ahead.License.UseAsync("exports"));
        Assert.Equal(1, ahead.Http.Count("use"));
        var present = Holding(s => s with { Spends = [Entry(kind: SpendKind.Unanswered, at: Now)] });
        present.Http.On("use", _ => Metered.Spent(89));
        await present.License.UseAsync("exports");
        Assert.Equal(0, present.Http.Count("use"));
    }

    [Fact]
    public async Task Does_not_wait_on_a_longer_wait_a_validate_set_an_answer_without_a_lease_the_use_endpoint_is_asked_all_the_same()
    {
        var h = Holding(s => s with { BackoffSince = Now, BackoffMs = Offline.AskAgainMs });
        h.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Spent(89), await h.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Refuses_a_meter_the_key_has_no_figure_for_as_no_meter_and_journals_it_as_a_void_when_it_was_in_doubt()
    {
        var h = Holding();
        h.Http.Fail = true;
        Assert.Equal(Refused(UseError.NoMeter), await h.License.UseAsync("credits"));
        Assert.Equal((SpendKind.Void, "credits", 1L), (Journal(h)[0].Kind, Journal(h)[0].Meter, Journal(h)[0].N));
        // A wait running does not stop the ask: the server may know a meter the lease does not name.
        var waiting = Holding(s => s with { BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs });
        Assert.Equal(Refused(UseError.NoMeter), await waiting.License.UseAsync("credits"));
        Assert.Equal(1, waiting.Http.Count("use"));
        Assert.Equal((SpendKind.Void, "credits"), (Journal(waiting)[0].Kind, Journal(waiting)[0].Meter));
    }

    [Fact]
    public async Task Fills_the_journal_to_64_and_refuses_the_65th_spend_as_past_the_allowance()
    {
        var full = Enumerable.Range(0, Meters.MaxPendingSpends).Select(i => Entry(i.ToString().PadLeft(22, 'x'), $"m{i}", kind: SpendKind.Unanswered, at: Now - 1)).ToList();
        var h = Holding(Exports(90, 100, 10), s => s with { Spends = full, BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs });
        h.SetClock(Now);
        // A new entry has no room: the spend is refused, though the allowance had it.
        Assert.Equal(Refused(UseError.OfflineLimit, 90), await h.License.UseAsync("exports"));
        Assert.Equal(Meters.MaxPendingSpends, Journal(h).Count);
    }

    [Fact]
    public async Task Lets_an_offline_entry_that_is_already_open_grow_when_the_journal_is_full()
    {
        var full = new List<SpendEntry> { Entry(new string('x', 22), n: 2) };
        full.AddRange(Enumerable.Range(0, Meters.MaxPendingSpends - 1).Select(i => Entry(i.ToString().PadLeft(22, 'y'), $"m{i}", kind: SpendKind.Unanswered, at: Now - 1)));
        var h = Holding(Exports(90, 100, 10), s => s with { Spends = full, BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs });
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports"));
        Assert.Equal(Meters.MaxPendingSpends, Journal(h).Count);
        Assert.Equal(3, Journal(h)[0].N);
    }

    // --- what is asked ----------------------------------------------------------------------------------

    public static TheoryData<long> BadAmounts => new() { 0, -1, Meters.MaxMeterUses + 1, long.MinValue, long.MaxValue, 2L * int.MaxValue };

    [Theory]
    [MemberData(nameof(BadAmounts))]
    public async Task Refuses_an_amount_that_is_not_a_whole_number_from_1_to_the_bound_asking_nothing(long n)
    {
        var h = Holding();
        h.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Refused(UseError.InvalidAmount), await h.License.UseAsync("exports", n));
        Assert.Equal(0, h.Http.Count("use"));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Takes_the_bound_itself_and_1_when_no_amount_is_given()
    {
        var h = Holding(Exports(Meters.MaxMeterUses, Meters.MaxMeterUses, 10));
        h.Http.On("use", _ => Metered.Spent(5));
        Assert.True((await h.License.UseAsync("exports", Meters.MaxMeterUses)).Ok);
        Assert.Equal(Meters.MaxMeterUses, Long(UseBody(h)["n"]));
        await h.License.UseAsync("exports");
        Assert.Equal(1, Long(UseBody(h, 1)["n"]));
    }

    [Fact]
    public async Task Judges_the_amount_before_the_meter_and_refuses_a_code_of_no_meter_asking_nothing()
    {
        var h = Holding();
        Assert.Equal(Refused(UseError.InvalidAmount), await h.License.UseAsync("", 0));
        foreach (var code in new[] { "", new string('x', 33), new string('x', 31) + "\U0001F600", null! })
        {
            Assert.True(Refused(UseError.NoMeter).Equals(await h.License.UseAsync(code)), code ?? "null");
        }
        Assert.Equal(0, h.Http.Count("use"));
    }

    [Fact]
    public async Task Takes_a_code_of_32_UTF_16_units_a_surrogate_pair_at_the_edge_included_and_matches_it_exactly()
    {
        var edge = new string('x', 30) + "\U0001F600";
        var h = Holding(new JsonObject { [edge] = Meter(5, 5, 1), [new string('x', 32)] = Meter(5, 5, 1) });
        h.Http.Fail = true;
        Assert.Equal(32, edge.Length);
        Assert.Equal(SpentOffline(4), await h.License.UseAsync(edge));
        Assert.Equal(SpentOffline(4), await h.License.UseAsync(new string('x', 32)));
        Assert.Equal(Refused(UseError.NoMeter), await h.License.UseAsync(new string('X', 32)));
    }

    [Fact]
    public async Task Spends_a_meter_named_like_a_member_of_Object_prototype_only_when_the_key_has_it()
    {
        var h = Holding();
        h.Http.Fail = true;
        foreach (var code in new[] { "constructor", "toString", "__proto__", "hasOwnProperty" })
        {
            Assert.True(Refused(UseError.NoMeter).Equals(await h.License.UseAsync(code)), code);
        }
        var own = Holding(new JsonObject { ["constructor"] = Meter(3, 3, 1) });
        own.Http.Fail = true;
        Assert.Equal(SpentOffline(2), await own.License.UseAsync("constructor"));
    }

    [Fact]
    public void Makes_an_id_of_22_base64url_characters_a_new_one_each_time()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Meters.NewSpendId()).ToHashSet();
        Assert.Equal(200, ids.Count);
        Assert.All(ids, id => Assert.Matches("^[A-Za-z0-9_-]{22}$", id));
    }

    // --- when it is not licensed -------------------------------------------------------------------------

    [Fact]
    public async Task Is_not_licensed_with_no_licence_a_trial_an_expired_or_tampered_lease_and_asks_nothing()
    {
        var none = Create();
        Assert.Equal(Refused(UseError.NotLicensed), await none.License.UseAsync("exports"));

        var trial = Create();
        trial.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now, ["trialEndsAt"] = Now + Day, ["lease"] = trial.TrialLease(Now + Day) });
        await trial.License.StartTrialAsync();
        Assert.Equal(LicenseState.Trial, (await trial.License.StatusAsync()).State);
        Assert.Equal(Refused(UseError.NotLicensed), await trial.License.UseAsync("exports"));

        var expired = Holding();
        expired.SetClock(Now + 40 * Day);
        Assert.Equal(Refused(UseError.NotLicensed), await expired.License.UseAsync("exports"));

        var other = Holding();
        other.Stored = other.Stored! with { Key = "SLUICE-ZZZZ-ZZZZ-ZZZZ-ZZZZ" };
        Assert.Equal(Refused(UseError.NotLicensed), await other.License.UseAsync("exports"));

        foreach (var h in new[] { none, trial, expired, other }) Assert.Equal(0, h.Http.Count("use"));
    }

    [Fact]
    public async Task Is_not_licensed_with_a_stored_refusal_that_stands_and_for_a_build_the_lease_does_not_cover()
    {
        var refused = Holding(s => s with { Refused = Refusal.Revoked });
        Assert.Equal(Refused(UseError.NotLicensed), await refused.License.UseAsync("exports"));
        var outdated = Holding(s => s with { Refused = Refusal.Upgrade, RefusedMajor = 1 });
        Assert.Equal(Refused(UseError.NotLicensed), await outdated.License.UseAsync("exports"));
        // A build above the lease's majors.
        var newer = Holding(options: new Options { Major = 4 });
        Assert.Equal(Refused(UseError.NotLicensed), await newer.License.UseAsync("exports"));
        Assert.Equal(0, refused.Http.Count("use") + outdated.Http.Count("use") + newer.Http.Count("use"));
    }

    [Fact]
    public async Task Is_not_licensed_with_an_offline_licence_file_which_has_no_meters()
    {
        var h = Holding();
        h.Stored = h.Stored! with { LicenseFile = LicenseFileSupport.OfflineFile(h.Signer) };
        // The file answers before the key beside it, and a file has no meters, so neither has the status nor a spend.
        var status = await h.License.StatusAsync();
        Assert.Equal(LicenseSource.File, status.Source);
        Assert.Empty(status.Meters);
        Assert.Equal(Refused(UseError.NotLicensed), await h.License.UseAsync("exports"));
        Assert.Equal(0, h.Http.Count("use"));
    }

    [Fact]
    public async Task Answers_storage_when_the_state_cannot_be_read_just_now_asking_nothing_and_throws_never()
    {
        var h = Holding();
        h.FailReadAt(1);
        Assert.Equal(Refused(UseError.Storage), await h.License.UseAsync("exports"));
        Assert.Equal(0, h.Http.Count("use"));
        h.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Spent(89), await h.License.UseAsync("exports"));
    }

    private sealed class ThrowingClock : IClock
    {
        public long Now() => throw new InvalidOperationException("the clock");
    }

    private sealed class ThrowingHttp : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the adapter");
    }

    [Fact]
    public async Task Never_throws_when_an_adapter_throws_a_clock_and_a_request_that_throws_is_no_answer()
    {
        var h = Holding();
        Assert.Equal(Refused(UseError.Storage), await AppOver(h, clock: new ThrowingClock()).UseAsync("exports"));
        // A request that throws, even before it is sent, is no answer: the allowance serves.
        Assert.Equal(SpentOffline(89), await AppOver(h, http: new ThrowingHttp()).UseAsync("exports"));
        var unknown = Holding(options: new Options { Fingerprint = new FuncFingerprinter(() => Task.FromException<string>(new InvalidOperationException("no machine id"))) });
        unknown.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(Spent(89), await unknown.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Answers_a_null_meter_as_no_meter_never_an_exception()
    {
        var h = Holding();
        Assert.Equal(Refused(UseError.NoMeter), await h.License.UseAsync(null!));
    }
}
