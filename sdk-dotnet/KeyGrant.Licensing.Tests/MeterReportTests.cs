using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.Metered;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The journal of unsynced spends is reported with the next validate, <c>use</c> and deactivate, dropped only when the
/// server confirms it, and never costs a customer a renewal or a release; and a status shows the key's meters.
/// </summary>
public class MeterReportTests
{
    private static JsonObject Confirms(params JsonNode?[] spends) => new() { ["spends"] = new JsonArray(spends) };

    private static JsonObject Count(string id, long n) => new() { ["id"] = id, ["n"] = n };

    private static JsonObject Void(string id) => new() { ["id"] = id, ["void"] = true };

    /// <summary>A renewal's answer: the lease the harness's key signs for the key, and the confirmations the server names.</summary>
    private static HttpResult Renewal(Harness h, JsonArray? spends = null)
    {
        var body = new JsonObject { ["lease"] = Lease(h, TwoMeters()) };
        if (spends is not null) body["spends"] = spends;
        return new HttpResult(200, body);
    }

    private static JsonArray Spends(params JsonNode?[] spends) => new(spends);

    private static Func<StoredState, StoredState> Journaled(params SpendEntry[] spends) => s => s with { Spends = spends };

    private static string Describe(JsonArray? report) => report is null ? "none" : report.ToJsonString();

    // --- what a validate reports ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_validate_carries_the_journal_oldest_first_a_void_marked_and_no_spends_at_all_when_there_is_none()
    {
        var h = Holding(Journaled(
            Entry(Id('A'), n: 3, at: Now - 3000, kind: SpendKind.Offline),
            Entry(Id('B'), n: 1, at: Now - 2000, kind: SpendKind.Unanswered),
            Entry(Id('C'), n: 2, at: Now - 1000, kind: SpendKind.Void)));
        h.Http.On("validate", _ => Renewal(h));
        await h.License.RefreshAsync();
        AssertJson.Equal(
            new JsonArray(
                new JsonObject { ["id"] = Id('A'), ["meter"] = "exports", ["n"] = 3, ["at"] = Now - 3000 },
                new JsonObject { ["id"] = Id('B'), ["meter"] = "exports", ["n"] = 1, ["at"] = Now - 2000 },
                new JsonObject { ["id"] = Id('C'), ["meter"] = "exports", ["n"] = 2, ["at"] = Now - 1000, ["void"] = true }),
            ReportOf(h, "validate"));
        var bare = Holding();
        bare.Http.On("validate", _ => Renewal(bare));
        await bare.License.RefreshAsync();
        Assert.False(BodyOf(bare, "validate").ContainsKey("spends"));
    }

    [Fact]
    public async Task A_validate_reports_only_what_is_valid_of_a_stored_journal_and_renews_all_the_same()
    {
        var h = Holding();
        var entries = new JsonArray(
            new JsonObject { ["id"] = "A", ["meter"] = "exports", ["n"] = 2, ["at"] = Now, ["kind"] = "offline" },
            new JsonObject { ["id"] = 4 },
            null,
            "x",
            new JsonObject { ["id"] = "Z", ["meter"] = "exports", ["n"] = 0, ["at"] = Now, ["kind"] = "offline" });
        h.Stored = StoredState.FromJson(new JsonObject { ["key"] = Key, ["activationId"] = "act_1", ["lease"] = h.Stored!.Lease, ["spends"] = entries }.ToJsonString());
        h.Http.On("validate", _ => Renewal(h));
        Assert.Equal(LicenseState.Licensed, (await h.License.RefreshAsync()).State);
        Assert.Single(ReportOf(h, "validate")!);

        var junk = Holding();
        junk.Stored = StoredState.FromJson(new JsonObject { ["key"] = Key, ["activationId"] = "act_1", ["lease"] = junk.Stored!.Lease, ["spends"] = new JsonObject { ["not"] = "a list" } }.ToJsonString());
        junk.Http.On("validate", _ => Renewal(junk));
        Assert.Equal(LicenseState.Licensed, (await junk.License.RefreshAsync()).State);
        Assert.False(BodyOf(junk, "validate").ContainsKey("spends"));
    }

    [Fact]
    public async Task A_validate_renews_with_a_journal_that_is_full()
    {
        var full = Enumerable.Range(0, Meters.MaxPendingSpends).Select(i => Entry(i.ToString().PadLeft(22, 'q'), $"m{i}", kind: SpendKind.Unanswered, at: LongAgo)).ToArray();
        var h = Holding(Journaled(full));
        h.Http.On("validate", _ => Renewal(h));
        Assert.Equal(LicenseState.Licensed, (await h.License.RefreshAsync()).State);
        Assert.Equal(Meters.MaxPendingSpends, ReportOf(h, "validate")!.Count);
    }

    [Fact]
    public async Task A_validate_reports_the_first_64_of_a_longer_journal()
    {
        var longer = Enumerable.Range(0, Meters.MaxPendingSpends + 6).Select(i => Entry(i.ToString().PadLeft(22, 'q'), $"m{i}", kind: SpendKind.Unanswered, at: LongAgo + i)).ToArray();
        var h = Holding(Journaled(longer));
        h.Http.On("validate", _ => Renewal(h));
        await h.License.RefreshAsync();
        var report = ReportOf(h, "validate")!;
        Assert.Equal(Meters.MaxPendingSpends, report.Count);
        Assert.Equal("m0", Str(report[0]!["meter"]));
        Assert.Equal($"m{Meters.MaxPendingSpends - 1}", Str(report[^1]!["meter"]));
    }

    [Fact]
    public async Task A_validate_drops_exactly_the_entries_the_answer_names_as_recorded()
    {
        var h = Holding(Journaled(
            Entry(Id('A'), n: 3, at: LongAgo, kind: SpendKind.Unanswered),
            Entry(Id('B'), "renders", 5, LongAgo, SpendKind.Offline),
            Entry(Id('C'), n: 1, at: LongAgo, kind: SpendKind.Void),
            Entry(Id('D'), "renders", 1, LongAgo, SpendKind.Void)));
        h.Http.On("validate", _ => Renewal(h, Spends(
            Count(Id('A'), 3),
            // More recorded than ever held: dropped. Less than held: kept.
            Count(Id('B'), 4),
            // A void is named void; a count for a void entry does not name it.
            Void(Id('C')),
            Count(Id('D'), 1),
            // Another shape confirms nothing, and an id the journal has not is nothing.
            Count(Id('E'), 1),
            new JsonObject { ["n"] = 2 },
            "x",
            null)));
        await h.License.RefreshAsync();
        Assert.Equal([Id('B'), Id('D')], Journal(h).Select(e => e.Id));
    }

    [Fact]
    public async Task A_validate_keeps_an_offline_entry_another_process_grew_meanwhile_with_its_count()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 3, at: LongAgo)));
        h.Http.On("validate", _ =>
        {
            // Another process spent two more while this request was out.
            h.Stored = h.Stored! with { Spends = [Entry(Id('A'), n: 5, at: LongAgo)] };
            return Renewal(h, Spends(Count(Id('A'), 3)));
        });
        await h.License.RefreshAsync();
        Assert.Equal([Entry(Id('A'), n: 5, at: LongAgo)], Journal(h));
    }

    [Fact]
    public async Task A_validate_drops_nothing_when_the_answer_names_nothing_and_waits_an_hour_before_status_offers_it_again()
    {
        var h = Holding(Journaled(Entry(at: LongAgo)));
        h.Http.On("validate", _ => Renewal(h));
        await h.License.RefreshAsync();
        Assert.Single(Journal(h));
        Assert.Equal(Offline.AskAgainMs, h.Stored!.BackoffMs);
        // A valid, far lease with a journal asks only once the wait is over.
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.SetClock(Now + Hour);
        await h.License.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
    }

    public static TheoryData<string> Unheard => new() { "503", "429", "none" };

    [Theory]
    [MemberData(nameof(Unheard))]
    public async Task A_validate_drops_nothing_on_a_5xx_or_429_whatever_their_body_says_and_nothing_on_no_answer(string kind)
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 3, at: LongAgo)));
        if (kind == "none") h.Http.Fail = true;
        else h.Http.Answer("validate", int.Parse(kind), Confirms(Count(Id('A'), 3)));
        await h.License.RefreshAsync();
        Assert.Single(Journal(h));
    }

    [Fact]
    public async Task A_validate_drops_what_a_refusal_confirms_whatever_its_status()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 3, at: LongAgo)));
        h.Http.Answer("validate", 403, new JsonObject { ["error"] = "revoked", ["spends"] = Spends(Count(Id('A'), 3)) });
        var status = await h.License.RefreshAsync();
        Assert.Equal((LicenseState.Invalid, LicenseReason.Revoked), (status.State, status.Reason!.Value));
        Assert.Empty(Journal(h));
        // A 200 without a lease is an answer too.
        var g = Holding(Journaled(Entry(Id('A'), n: 3, at: LongAgo)));
        g.Http.Answer("validate", 200, Confirms(Count(Id('A'), 3)));
        await g.License.RefreshAsync();
        Assert.Empty(Journal(g));
    }

    [Fact]
    public async Task A_validate_keeps_the_confirmation_in_memory_when_it_cannot_be_saved_so_the_entry_is_not_reported_again_and_saves_it_next()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 3, at: LongAgo)));
        h.Http.On("validate", _ => Renewal(h, Spends(Count(Id('A'), 3))));
        h.FailWrites();
        await h.License.RefreshAsync();
        Assert.Single(Journal(h));
        Assert.Equal(0, (await h.License.StatusAsync()).Meters["exports"].Unsynced);
        await h.License.RefreshAsync();
        Assert.Null(ReportOf(h, "validate", 1));
        h.FailWrites(false);
        await h.License.StatusAsync();
        Assert.Empty(Journal(h));
    }

    [Fact]
    public async Task A_valid_lease_reports_at_the_next_status_once_the_journal_s_own_wait_is_not_running_and_not_before()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 1, at: Now - 30_000, kind: SpendKind.Unanswered)));
        h.Http.On("validate", _ => Renewal(h, Spends(Count(Id('A'), 1))));
        // The journal's wait: a minute after one unanswered spend.
        await h.License.StatusAsync();
        Assert.Equal(0, h.Http.Count("validate"));
        h.SetClock(Now - 30_000 + Minute);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Empty(Journal(h));
        // Nothing left to report: a valid, far lease asks nothing.
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task A_valid_lease_reports_an_offline_only_journal_at_once_and_waits_for_the_ask_s_own_wait_after_a_request_that_got_no_answer()
    {
        var h = Holding(Journaled(Entry(n: 2, at: Now)));
        h.Http.Fail = true;
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(Offline.RetryUnansweredMs, h.Stored!.BackoffMs);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        h.Http.Fail = false;
        h.Http.On("validate", _ => Renewal(h, Spends(Count("AAAAAAAAAAAAAAAAAAAAAA", 2))));
        h.SetClock(Now + Minute);
        await h.License.StatusAsync();
        Assert.Empty(Journal(h));
    }

    [Fact]
    public async Task A_lease_past_its_end_asks_to_heal_it_with_the_journal()
    {
        var h = Holding(Journaled(Entry(at: Now - 40 * Day)));
        h.SetClock(Now + 40 * Day);
        h.Http.On("validate", _ => Renewal(h));
        await h.License.StatusAsync();
        Assert.Single(ReportOf(h, "validate")!);
    }

    [Fact]
    public async Task A_use_request_carries_the_journal_too_and_is_dropped_by_what_that_request_confirms()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 2, at: LongAgo, kind: SpendKind.Unanswered)));
        h.Http.On("use", _ =>
        {
            var json = (JsonObject)Metered.Spent(87).Json!;
            json["spends"] = Spends(Count(Id('A'), 2));
            return new HttpResult(200, json);
        });
        Assert.Equal(UseResult.Spent(87), await h.License.UseAsync("exports"));
        AssertJson.Equal(new JsonArray(new JsonObject { ["id"] = Id('A'), ["meter"] = "exports", ["n"] = 2, ["at"] = LongAgo }), ReportOf(h, "use"));
        Assert.Empty(Journal(h));
    }

    // --- what a deactivate reports -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_deactivate_carries_the_journal_with_the_device_s_fingerprints_then_clears_it_with_the_rest_of_the_licence()
    {
        var h = Holding(Journaled(Entry(n: 2), Entry(Id('B'), kind: SpendKind.Void)));
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        Assert.Equal(new DeactivateResult(true), await h.License.DeactivateAsync());
        var body = BodyOf(h, "deactivate");
        Assert.Equal((Key, "act_1"), (Str(body["key"]), Str(body["activationId"])));
        Assert.Equal(2, body["spends"]!.AsArray().Count);
        Assert.Single(body["fingerprints"]!.AsArray());
        Assert.Null(h.Stored!.Spends);
        Assert.Null(h.Stored.MeterFigures);
        Assert.Null(h.Stored.Key);
        Assert.Null(h.Stored.ActivationId);
    }

    [Fact]
    public async Task A_deactivate_reports_the_first_64_of_a_longer_journal_and_clears_every_entry_with_the_licence()
    {
        var longer = Enumerable.Range(0, Meters.MaxPendingSpends + 6).Select(i => Entry(i.ToString().PadLeft(22, 'q'), $"m{i}", kind: SpendKind.Unanswered, at: LongAgo + i)).ToArray();
        var h = Holding(Journaled(longer));
        h.Http.Answer("deactivate", 200, new JsonObject());
        Assert.Equal(new DeactivateResult(true), await h.License.DeactivateAsync());
        var report = ReportOf(h, "deactivate")!;
        Assert.Equal((Meters.MaxPendingSpends, "m0", $"m{Meters.MaxPendingSpends - 1}"), (report.Count, Str(report[0]!["meter"]), Str(report[^1]!["meter"])));
        Assert.Null(h.Stored!.Spends);
    }

    [Fact]
    public async Task A_deactivate_releases_all_the_same_with_a_journal_that_is_not_one()
    {
        var h = Holding();
        h.Stored = StoredState.FromJson(new JsonObject { ["key"] = Key, ["activationId"] = "act_1", ["lease"] = h.Stored!.Lease, ["spends"] = "junk", ["meterFigures"] = 7 }.ToJsonString());
        h.Http.Answer("deactivate", 200, new JsonObject());
        Assert.Equal(new DeactivateResult(true), await h.License.DeactivateAsync());
        AssertJson.Equal(new JsonObject { ["key"] = Key, ["activationId"] = "act_1" }, BodyOf(h, "deactivate"));
        Assert.Null(h.Stored!.Key);
        // The junk went with the licence.
        Assert.False(JsonNode.Parse(h.Stored.ToJson())!.AsObject().ContainsKey("spends"));
        Assert.False(JsonNode.Parse(h.Stored.ToJson())!.AsObject().ContainsKey("meterFigures"));
    }

    [Fact]
    public async Task A_deactivate_sends_exactly_the_key_and_the_activation_when_there_is_nothing_to_report_as_it_always_did()
    {
        var h = Holding(s => s with { MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(5, 100, 10, false, Now) } });
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.DeactivateAsync();
        AssertJson.Equal(new JsonObject { ["key"] = Key, ["activationId"] = "act_1" }, BodyOf(h, "deactivate"));
        Assert.Null(h.Stored!.MeterFigures);
    }

    [Fact]
    public async Task A_deactivate_names_no_fingerprints_when_it_cannot_tell_who_the_device_is_and_the_server_holds_it_to_none()
    {
        var h = Holding(Journaled(Entry()), new Options { Fingerprint = new FuncFingerprinter(() => Task.FromException<string>(new InvalidOperationException("no id"))) });
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.DeactivateAsync();
        var body = BodyOf(h, "deactivate");
        Assert.Single(body["spends"]!.AsArray());
        Assert.False(body.ContainsKey("fingerprints"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_deactivate_clears_the_journal_locally_and_says_the_seat_may_be_held_when_the_release_got_no_200(bool fail)
    {
        var h = Holding(Journaled(Entry()));
        if (fail) h.Http.Fail = true;
        else h.Http.Answer("deactivate", 503, new JsonObject());
        Assert.Equal(new DeactivateResult(false), await h.License.DeactivateAsync());
        Assert.Null(h.Stored!.Spends);
    }

    [Fact]
    public async Task A_deactivate_reports_what_this_process_could_not_save_and_forgets_it_with_the_rest()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.FailWrites();
        await h.License.UseAsync("exports");
        h.FailWrites(false);
        h.Http.Fail = false;
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.DeactivateAsync();
        Assert.Single(ReportOf(h, "deactivate")!);
        // Nothing of it comes back with the next licence.
        h.Http.Lease("activate", Lease(h, TwoMeters()), "act_1");
        await h.License.ActivateAsync(Key);
        Assert.Equal(0, (await h.License.StatusAsync()).Meters["exports"].Unsynced);
        Assert.Empty(Journal(h));
        Assert.Null(h.Stored!.Spends);
    }

    [Fact]
    public async Task A_deactivate_that_cannot_land_what_it_holds_goes_with_what_is_stored_and_the_call_then_throws_with_the_key_kept()
    {
        var h = Holding(Journaled(Entry(Id('S'), n: 4, kind: SpendKind.Offline, at: LongAgo)));
        h.Http.Fail = true;
        // A spend in doubt, and a void, held in memory: the disk refuses.
        h.FailWrites();
        Assert.True((await h.License.UseAsync("exports", 2)).Offline);
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        h.Http.Fail = false;
        h.Http.Answer("deactivate", 200, new JsonObject());
        await Assert.ThrowsAsync<DiskSaidNoException>(() => h.License.DeactivateAsync());
        // The release went out with the stored journal and the held unanswered spend and void, and the key is kept.
        var report = ReportOf(h, "deactivate")!;
        Assert.Equal(
            [("exports", 4L, false), ("exports", 2L, false), ("renders", 1L, true)],
            report.Select(e => (Str(e!["meter"])!, Long(e["n"])!.Value, e["void"] is not null)));
        Assert.Equal(Key, h.Stored!.Key);
    }

    [Fact]
    public async Task A_deactivate_reports_nothing_for_a_seat_that_is_another_device_s_the_release_is_not_its_to_make()
    {
        var h = Holding(Journaled(Entry()));
        // Its lease is held to a machine this one is not: a state file copied from another PC.
        var held = h.SignLease(new LeaseInput { Sub = Key, Model = "subscription", Major = 3, Fp = new string('f', 64), Fpk = "machine", Met = TwoMeters() });
        h.Stored = h.Stored! with { Lease = held };
        Assert.Equal(new DeactivateResult(true), await h.License.DeactivateAsync());
        Assert.Equal(0, h.Http.Count("deactivate"));
    }

    // --- status().Meters ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_shows_each_meter_of_the_lease_with_its_figures_and_nothing_when_the_lease_has_none()
    {
        var h = Holding();
        Assert.Equal(
            new LicenseStatus
            {
                State = LicenseState.Licensed,
                Key = Key,
                Meters = new Dictionary<string, MeterStatus>
                {
                    ["exports"] = new(90, 100, 10, 0, false),
                    ["renders"] = new(5, 5, 0, 0, true),
                },
            },
            await h.License.StatusAsync());
        Assert.Equal(0, h.Http.Count("validate"));
        var bare = Holding(met: null);
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, await bare.License.StatusAsync());
        Assert.Empty((await bare.License.StatusAsync()).Meters);
    }

    [Fact]
    public async Task Status_meters_are_net_of_the_unsynced_spends_of_each_kind_never_below_0_and_count_no_void()
    {
        var h = Holding(s => s with
        {
            Spends =
            [
                Entry(n: 4, kind: SpendKind.Offline),
                Entry(Id('B'), n: 3, kind: SpendKind.Unanswered),
                Entry(Id('C'), n: 50, kind: SpendKind.Void),
                Entry(Id('D'), "renders", 9),
            ],
            BackoffSince = Now,
            BackoffMs = Offline.RetryUnansweredMs,
        });
        var meters = (await h.License.StatusAsync()).Meters;
        Assert.Equal(new MeterStatus(83, 100, 10, 7, false), meters["exports"]);
        Assert.Equal(new MeterStatus(0, 5, 0, 9, true), meters["renders"]);
    }

    [Fact]
    public async Task Status_works_behind_a_call_that_has_not_finished_saying_so()
    {
        var time = new ManualTime();
        var h = Holding(TwoMeters(), s => s with { Spends = [Entry(n: 2)], LastSeen = Now }, new Options { Time = time });
        h.Http.Error("validate", 403, "revoked");
        h.HoldNextWrite();
        _ = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes >= 1, "the held save");
        var later = h.License.StatusAsync();
        await Eventually.Quiet();
        time.Advance(Grace);
        var status = await later.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((LicenseState.Licensed, true), (status.State, status.Stalled));
        Assert.Equal((88L, 2L), (status.Meters["exports"].Left, status.Meters["exports"].Unsynced));
    }

    [Fact]
    public async Task A_spend_behind_a_call_that_has_not_finished_asks_saves_nothing_of_the_answer_and_keeps_the_spend_in_memory()
    {
        var time = new ManualTime();
        var h = Holding(TwoMeters(), s => s with { LastSeen = Now }, new Options { Time = time });
        h.Http.Error("validate", 403, "revoked");
        var release = h.HoldNextWrite();
        var refreshing = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes >= 1, "the held save");
        var writes = h.Writes;
        h.Http.On("use", _ => Metered.Spent(89));
        var first = h.License.UseAsync("exports");
        await Eventually.Quiet();
        time.Advance(Grace);
        Assert.Equal(UseResult.Spent(89), await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("use"));
        // Offline now: the spend is kept in memory and counts.
        h.Http.Fail = true;
        // The answer the first call was given was not saved either, so the lease's own figure is the base.
        Assert.Equal(UseResult.Spent(88, offline: true), await h.License.UseAsync("exports", 2).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(writes, h.Writes);
        release();
        // The call that was stuck finishes with its verdict, and the spend kept in memory lands beside it.
        await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Refusal.Revoked, h.Stored!.Refused);
        await h.License.StatusAsync();
        // The second call was asked (no wait ran) and got no answer: its spend is in doubt, and kept.
        Assert.Equal([(SpendKind.Unanswered, 2L)], KindsAndCounts(h));
    }

    [Fact]
    public async Task Status_has_no_meters_on_every_status_but_a_key_s_licensed_one_expired_invalid_none()
    {
        var expired = Holding();
        expired.SetClock(Now + 40 * Day);
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }, await expired.License.StatusAsync());
        var revoked = Holding(s => s with { Refused = Refusal.Revoked });
        Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key }, await revoked.License.StatusAsync());
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await Create().License.StatusAsync());
    }

    [Fact]
    public async Task Status_has_no_meters_on_a_trial_whose_lease_carries_them_and_on_an_offline_licence_file_that_does()
    {
        var trial = Create();
        var claim = Hashes.FingerprintClaim("fp-test");
        var lease = trial.SignLease(new LeaseInput { Sub = $"trial:{claim}", Aid = claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = 5 * 86_400, Met = TwoMeters() });
        trial.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = lease });
        var started = await trial.License.StartTrialAsync();
        Assert.Equal(LicenseState.Trial, started.State);
        Assert.Empty(started.Meters);
        Assert.Empty((await trial.License.StatusAsync()).Meters);

        var file = Holding();
        var token = LicenseFileSupport.OfflineFile(file.Signer, new JsonObject { ["met"] = TwoMeters() });
        Assert.True((await file.License.ImportLicenseFileAsync(token)).Ok);
        // The file answers before the key beside it, and a file has no meters, so neither has the status nor a spend.
        Assert.Equal(LicenseFileSupport.FileLicensed, await file.License.StatusAsync());
        Assert.Equal(UseResult.Refused(UseError.NotLicensed), await file.License.UseAsync("exports"));
    }

    [Fact]
    public async Task Status_keeps_a_copy_of_its_own_in_each_status()
    {
        var h = Holding();
        var a = await h.License.StatusAsync();
        var b = await h.License.StatusAsync();
        Assert.NotSame(a.Meters, b.Meters);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, MeterStatus>)a.Meters).Add("x", new MeterStatus(1, 1, 1, 0, false)));
        Assert.Equal(2, b.Meters.Count);
    }

    [Fact]
    public async Task Status_shows_a_meter_named_proto_or_like_a_member_of_Object_prototype_as_a_meter_like_any_other()
    {
        var met = JsonNode.Parse("{\"__proto__\":{\"left\":1,\"total\":2,\"offline\":0},\"constructor\":{\"left\":3,\"total\":3,\"offline\":1}}")!;
        var h = Holding(met);
        var meters = (await h.License.StatusAsync()).Meters;
        Assert.Equal(["__proto__", "constructor"], meters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new MeterStatus(1, 2, 0, 0, false), meters["__proto__"]);
    }

    [Fact]
    public async Task Status_takes_a_figure_a_use_answer_gave_in_place_of_the_lease_s_until_the_lease_is_renewed()
    {
        MeterFigure At(long at) => new(40, 100, 10, false, at);
        var newer = Holding(s => s with { MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = At(Now + 1000) } });
        Assert.Equal(40, (await newer.License.StatusAsync()).Meters["exports"].Left);
        // A figure from before the lease's own second is older than it; from the same second, newer.
        var older = Holding(s => s with { MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = At(Now - 1000) } });
        Assert.Equal(90, (await older.License.StatusAsync()).Meters["exports"].Left);
        var same = Holding(s => s with { MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = At(Now) } });
        Assert.Equal(40, (await same.License.StatusAsync()).Meters["exports"].Left);
    }

    [Fact]
    public async Task A_renewal_shows_its_meters_net_of_the_journal_after_what_the_answer_confirmed()
    {
        var h = Holding(Journaled(Entry(Id('A'), n: 2, at: LongAgo), Entry(Id('B'), n: 1, at: LongAgo, kind: SpendKind.Void)));
        var lease = Lease(h, Exports(70, 100, 10), now: Now + 5000);
        h.Http.Answer("validate", 200, new JsonObject { ["lease"] = lease, ["spends"] = Spends(Count(Id('A'), 2)) });
        h.SetClock(Now + 5000);
        var meters = (await h.License.RefreshAsync()).Meters;
        Assert.Equal(new Dictionary<string, MeterStatus> { ["exports"] = new(70, 100, 10, 0, false) }, meters.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public async Task Status_stops_showing_a_meter_the_newest_lease_no_longer_names()
    {
        var h = Holding(s => s with { MeterFigures = new Dictionary<string, MeterFigure> { ["renders"] = new(1, 5, 0, true, Now - 5000) } });
        h.Stored = h.Stored! with { Lease = Lease(h, Exports(70, 100, 10)) };
        Assert.Equal(["exports"], (await h.License.StatusAsync()).Meters.Keys);
    }

    // --- open leases ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spend_on_an_open_ended_lease_is_as_on_any_other()
    {
        var h = Create();
        h.Stored = new StoredState
        {
            Key = Key,
            ActivationId = "act_1",
            Lease = h.SignLease(new LeaseInput { Sub = Key, Major = Offline.OpenMajor, TtlSeconds = 86_400 * 30, Met = TwoMeters() }),
        };
        h.Http.On("use", _ => Metered.Spent(89));
        Assert.Equal(UseResult.Spent(89), await h.License.UseAsync("exports"));
    }

    // --- an entry the server did not confirm ----------------------------------------------------------------------------------------

    private static readonly SpendEntry Held = Entry(Id('A'), n: 3, at: LongAgo);

    public static TheoryData<string> Unconfirming => new() { "a lease naming nothing", "a 503", "a 429", "no answer at all", "a count lower than the entry holds" };

    private static void Unconfirmed(Harness h, string how)
    {
        switch (how)
        {
            case "a lease naming nothing": h.Http.On("validate", _ => Renewal(h)); break;
            case "a 503": h.Http.Error("validate", 503, "down"); break;
            case "a 429": h.Http.Error("validate", 429, "rate-limited"); break;
            case "no answer at all": h.Http.Fail = true; break;
            default: h.Http.On("validate", _ => Renewal(h, Spends(Count(Held.Id, 2)))); break;
        }
    }

    [Theory]
    [MemberData(nameof(Unconfirming))]
    public async Task An_entry_the_server_did_not_confirm_is_reported_again_with_the_same_id_meter_count_and_time(string how)
    {
        var h = Holding(Journaled(Held));
        Unconfirmed(h, how);
        await h.License.RefreshAsync();
        Assert.Equal([Held], Journal(h));
        // Past every wait, the next validate and the next use carry it as it was.
        h.Http.Fail = false;
        h.SetClock(Now + 2 * Hour);
        h.Http.On("validate", _ => Renewal(h));
        await h.License.RefreshAsync();
        var report = new JsonArray(new JsonObject { ["id"] = Held.Id, ["meter"] = Held.Meter, ["n"] = Held.N, ["at"] = Held.At });
        AssertJson.Equal(report, ReportOf(h, "validate", h.Http.Count("validate") - 1));
        h.Http.On("use", _ => Metered.Spent(89));
        await h.License.UseAsync("exports");
        AssertJson.Equal(report, ReportOf(h, "use"));
    }

    [Fact]
    public async Task A_failed_use_carries_the_id_it_sent_into_the_next_request_s_report()
    {
        var h = Holding();
        h.Http.Fail = true;
        await h.License.UseAsync("exports", 2);
        var sent = Str(UseBody(h)["id"]);
        h.Http.Fail = false;
        h.SetClock(Now + 2 * Minute);
        h.Http.On("validate", _ => Renewal(h));
        await h.License.RefreshAsync();
        AssertJson.Equal(new JsonArray(new JsonObject { ["id"] = sent, ["meter"] = "exports", ["n"] = 2, ["at"] = Now }), ReportOf(h, "validate"), Describe(ReportOf(h, "validate")));
        h.Http.On("use", _ => Metered.Spent(89));
        await h.License.UseAsync("exports");
        Assert.Equal(sent, Str(UseBody(h, 1)["spends"]!.AsArray()[0]!["id"]));
    }

    // --- a request that carries no report ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_activation_or_a_trial_sends_no_spends_though_the_journal_holds_some()
    {
        var h = Holding(Journaled(Entry(at: LongAgo)));
        h.Http.Lease("activate", Lease(h, TwoMeters()), "act_1");
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        await h.License.StartTrialAsync();
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.False(BodyOf(h, "activate").ContainsKey("spends"));
        Assert.False(BodyOf(h, "trial").ContainsKey("spends"));
    }

    // --- a full journal ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_journal_refuses_a_spend_the_allowance_covers_though_the_request_was_sent_and_appends_no_void_it_has_no_room_for()
    {
        var full = Enumerable.Range(0, Meters.MaxPendingSpends).Select(i => Entry(i.ToString().PadLeft(22, 'q'), $"m{i}", kind: SpendKind.Unanswered, at: LongAgo)).ToArray();
        var h = Holding(Journaled(full));
        h.Http.Fail = true;
        Assert.Equal(UseResult.Refused(UseError.OfflineLimit, 90), await h.License.UseAsync("exports"));
        Assert.Equal(1, h.Http.Count("use"));
        Assert.Equal(Meters.MaxPendingSpends, Journal(h).Count);
        Assert.DoesNotContain(Journal(h), e => e.Kind == SpendKind.Void);
    }

    // --- the bodies the reference's own worker parses ------------------------------------------------------------------------------------------

    [WireBodiesFact]
    public async Task The_requests_that_carry_a_report_are_the_bodies_the_reference_sends()
    {
        var captured = WireBodies.Read();
        var h = Create(null, new Options { Major = 3 });
        var met = new JsonObject { ["exports"] = Meter(90, 100, 10), ["renders"] = Meter(5, 5, 0, resets: true) };
        h.Stored = new StoredState
        {
            Key = Key,
            ActivationId = "act_1",
            Lease = h.SignLease(new LeaseInput { Sub = Key, Model = "subscription", Major = 3, TtlSeconds = 30 * 86_400, Met = met }),
            Spends =
            [
                new("offlineoffline0000001", "exports", 3, Now - 2 * Hour, SpendKind.Offline),
                new("unansweredunanswered2", "renders", 1, Now - 2 * Hour + 1, SpendKind.Unanswered),
                new("voidvoidvoidvoid00003", "exports", 2, Now - 2 * Hour + 2, SpendKind.Void),
            ],
        };
        // Answers that confirm nothing, so the journal is still there for the next request.
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = met.DeepClone(), ["at"] = Now }));
        h.Http.Answer("validate", 200, new JsonObject());
        h.Http.Answer("deactivate", 200, new JsonObject());
        await h.License.UseAsync("exports", 2);
        await h.License.RefreshAsync();
        await h.License.DeactivateAsync();

        // The spend's id is random, and the key ids are this suite's own key's.
        var use = BodyOf(h, "use");
        Assert.Matches("^[A-Za-z0-9_-]{22}$", Str(use["id"])!);
        use["id"] = captured["use"]!["id"]!.DeepClone();
        foreach (var name in new[] { "use", "validate" })
        {
            var body = name == "use" ? use : BodyOf(h, name);
            Assert.Equal([h.Signer.Kid], body["kids"]!.AsArray().Select(k => Str(k)));
            body.Remove("kids");
            var expected = captured[name]!.DeepClone().AsObject();
            expected.Remove("kids");
            AssertJson.Equal(expected, body, name);
        }
        AssertJson.Equal(captured["deactivate"], BodyOf(h, "deactivate"), "deactivate");
    }
}
