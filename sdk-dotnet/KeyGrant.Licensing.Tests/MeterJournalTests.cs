using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.Metered;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The journal of unsynced spends: kept in the stored state, in memory when a save fails, merged across processes as
/// the rest of the state is, judged again where a spend lands, and dropped with its seat.
/// </summary>
public class MeterJournalTests
{
    private static UseResult SpentOffline(long left) => UseResult.Spent(left, offline: true);

    private static UseResult Refused(UseError error, long? left = null) => UseResult.Refused(error, left);

    private static long Total(IEnumerable<SpendEntry> journal) => journal.Sum(e => e.N);

    private static StoredState Parsed(string json) => StoredState.FromJson(json)!;

    // --- the journal ------------------------------------------------------------------------------------

    [Fact]
    public async Task Survives_a_save_that_fails_the_customer_has_the_use_it_counts_toward_the_allowance_and_the_status_and_lands_with_the_next_save_that_can()
    {
        var h = Holding(Exports(90, 100, 2));
        h.Http.Fail = true;
        h.FailWrites();
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        Assert.Equal(SpentOffline(88), await h.License.UseAsync("exports"));
        Assert.Empty(Journal(h));
        // It counts: the allowance is spent, and the status shows it.
        Assert.Equal(Refused(UseError.OfflineLimit, 88), await h.License.UseAsync("exports"));
        Assert.Equal(new MeterStatus(88, 100, 2, 2, false), (await h.License.StatusAsync()).Meters["exports"]);
        h.FailWrites(false);
        await h.License.StatusAsync();
        // The third call was asked, and in doubt: the server is told it is taken back.
        Assert.Equal(2, Total(Journal(h).Where(e => e.Kind != SpendKind.Void)));
        Assert.Single(Journal(h), e => e.Kind == SpendKind.Void);
        // Landed once: a failing disk again changes nothing the view shows.
        h.FailWrites();
        Assert.Equal(2, (await h.License.StatusAsync()).Meters["exports"].Unsynced);
    }

    [Fact]
    public async Task Lands_with_the_next_use_that_can_save_in_order()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.FailWrites();
        await h.License.UseAsync("exports");
        h.FailWrites(false);
        h.Http.Fail = false;
        // The server records the report the request carries, and says so.
        h.Http.On("use", body =>
        {
            var json = (JsonObject)Metered.Spent(88).Json!;
            json["spends"] = new JsonArray(body["spends"]!.AsArray().Select(e => (JsonNode?)new JsonObject { ["id"] = e!["id"]!.DeepClone(), ["n"] = e["n"]!.DeepClone() }).ToArray());
            return new HttpResult(200, json);
        });
        h.SetClock(Now + 2 * Minute);
        Assert.Equal(UseResult.Spent(88), await h.License.UseAsync("exports"));
        // The earlier spend rode this request as a report, and its confirmation dropped it.
        Assert.Single(UseBody(h, 1)["spends"]!.AsArray());
        Assert.Empty(Journal(h));
    }

    [Fact]
    public async Task Lands_on_the_state_as_stored_now_so_a_second_process_s_spend_is_never_lost()
    {
        var h = Holding(Exports(90, 100, 10));
        h.Http.Fail = true;
        // Another process saves an offline entry between this one's read and its save.
        var app = AppOver(h, new RacingStorage(h.Adapters.Storage!, latest => latest with { Spends = [Entry(Id('O'), n: 4, at: Now - 5)], Rev = 7 }));
        // The first no-answer spend is journaled beside theirs; the second (in its wait) grows the offline entry that is there.
        await app.UseAsync("exports");
        await app.UseAsync("exports", 2);
        Assert.Equal([("theirs", 6L), ("Unanswered", 1L)], Journal(h).Select(e => (e.Id == Id('O') ? "theirs" : e.Kind.ToString(), e.N)));
    }

    [Fact]
    public async Task Keeps_an_entry_in_the_journal_of_the_activation_it_is_about_and_nothing_of_it_on_another_seat()
    {
        var h = Holding(s => s with { Spends = [Entry(n: 2)], MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(50, 100, 10, false, Now) } });
        // The same seat activated again keeps it.
        h.Http.Lease("activate", Lease(h, TwoMeters()), "act_1");
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Single(Journal(h));
        Assert.NotNull(h.Stored!.MeterFigures);
        // Another seat of the same key starts afresh.
        h.Http.Lease("activate", Lease(h, TwoMeters(), aid: "act_9"), "act_9");
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal("act_9", h.Stored!.ActivationId);
        Assert.Null(h.Stored.Spends);
        Assert.Null(h.Stored.MeterFigures);
    }

    [Fact]
    public async Task Drops_the_journal_of_an_old_activation_when_another_is_activated_unreported()
    {
        var h = Holding(s => s with { Spends = [Entry(n: 2)], MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(50, 100, 10, false, Now) } });
        const string another = "SLUICE-OTHER-AAAA-BBBB-CCCC";
        h.Http.Lease("activate", Lease(h, TwoMeters(), sub: another, aid: "act_2"), "act_2");
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(another));
        Assert.Equal(0, h.Http.Count("validate"));
        Assert.Equal(another, h.Stored!.Key);
        Assert.Null(h.Stored.Spends);
        Assert.Null(h.Stored.MeterFigures);
    }

    [Fact]
    public async Task Drops_entries_unreported_for_more_than_180_days_at_the_next_save_and_keeps_the_ones_inside()
    {
        var old = Entry(new string('o', 22), at: Now - 181 * Day, kind: SpendKind.Unanswered);
        var recent = Entry(new string('n', 22), at: Now - 179 * Day, kind: SpendKind.Unanswered);
        var h = Holding(s => s with { Spends = [old, recent] });
        h.Http.Fail = true;
        await h.License.UseAsync("exports");
        Assert.DoesNotContain(old.Id, Journal(h).Select(e => e.Id));
        Assert.Contains(recent.Id, Journal(h).Select(e => e.Id));
    }

    [Fact]
    public async Task Keeps_an_entry_opened_exactly_180_days_ago()
    {
        var edge = Entry(new string('e', 22), at: Now - Meters.PendingMaxMs, kind: SpendKind.Unanswered);
        var past = Entry(new string('p', 22), at: Now - Meters.PendingMaxMs - 1, kind: SpendKind.Unanswered);
        var h = Holding(s => s with { Spends = [edge, past] });
        h.Http.Fail = true;
        await h.License.UseAsync("exports");
        Assert.Contains(edge.Id, Journal(h).Select(e => e.Id));
        Assert.DoesNotContain(past.Id, Journal(h).Select(e => e.Id));
    }

    [Fact]
    public async Task Reads_a_stored_journal_that_is_not_one_as_an_empty_journal_and_an_entry_of_another_shape_as_none()
    {
        var h = Holding();
        h.Stored = Parsed(new JsonObject { ["key"] = Key, ["activationId"] = "act_1", ["lease"] = h.Stored!.Lease, ["spends"] = "junk" }.ToJsonString());
        h.Http.Fail = true;
        Assert.Equal(SpentOffline(89), await h.License.UseAsync("exports"));
        // The junk went with the journal that was written in its place.
        Assert.Single(Journal(h));

        var g = Holding();
        var entries = new JsonArray(
            new JsonObject { ["id"] = 3 },
            null,
            new JsonObject { ["id"] = "A", ["meter"] = "exports", ["n"] = 2, ["at"] = Now, ["kind"] = "offline" },
            new JsonObject { ["id"] = "x", ["meter"] = "exports", ["n"] = 0, ["at"] = 1, ["kind"] = "offline" },
            new JsonObject { ["id"] = "y", ["meter"] = "exports", ["n"] = 1.5, ["at"] = 1, ["kind"] = "offline" },
            new JsonObject { ["id"] = "z", ["meter"] = "exports", ["n"] = 1, ["at"] = -1, ["kind"] = "offline" },
            new JsonObject { ["id"] = "w", ["meter"] = "exports", ["n"] = 1, ["at"] = 1, ["kind"] = "unknown" });
        g.Stored = Parsed(new JsonObject { ["key"] = Key, ["activationId"] = "act_1", ["lease"] = g.Stored!.Lease, ["spends"] = entries }.ToJsonString());
        g.Http.Fail = true;
        Assert.Equal(2, (await g.License.StatusAsync()).Meters["exports"].Unsynced);
    }

    // --- the device -----------------------------------------------------------------------------------

    private static FuncFingerprinter Machine(params string[] alternates) => new(
        () => Task.FromResult("fp-machine"),
        () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, alternates)));

    [Fact]
    public async Task Names_the_device_as_a_validate_does_its_fingerprints_and_what_they_hash()
    {
        var h = Holding(options: new Options { Fingerprint = Machine("fp-host") });
        h.Http.On("use", _ => Metered.Spent(89));
        await h.License.UseAsync("exports");
        Assert.Equal("machine", Str(UseBody(h)["fingerprintKind"]));
        Assert.Equal(2, UseBody(h)["fingerprints"]!.AsArray().Count);
    }

    [Fact]
    public async Task Keys_the_install_by_the_machine_id_it_read_when_the_request_gets_no_answer()
    {
        var h = Holding(options: new Options { Fingerprint = Machine() });
        h.Http.Fail = true;
        await h.License.UseAsync("exports");
        Assert.Equal(DeviceKind.Machine, h.Stored!.DeviceKind);
    }

    [Fact]
    public async Task Sends_no_fingerprints_when_devices_are_not_held_or_the_device_cannot_be_told()
    {
        var pooled = Holding(options: new Options { DeviceHold = false });
        pooled.Http.On("use", _ => Metered.Spent(89));
        await pooled.License.UseAsync("exports");
        Assert.False(UseBody(pooled).ContainsKey("fingerprints"));
        Assert.False(UseBody(pooled).ContainsKey("fingerprintKind"));
        var unknown = Holding(options: new Options { Fingerprint = new FuncFingerprinter(() => Task.FromException<string>(new InvalidOperationException("no id"))) });
        unknown.Http.On("use", _ => Metered.Spent(89));
        await unknown.License.UseAsync("exports");
        Assert.False(UseBody(unknown).ContainsKey("fingerprints"));
    }

    // --- a spend held in memory -----------------------------------------------------------------------------

    /// <summary>The server renews with the lease it signs for the key.</summary>
    private static void Renewing(Harness h, JsonNode? met) => h.Http.Lease("validate", Lease(h, met));

    private static readonly Func<StoredState, StoredState> Waiting = s => s with { BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs };

    [Fact]
    public async Task A_spend_held_in_memory_is_not_reported_until_it_has_landed_so_the_entry_it_joins_is_reported_with_it_once()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        Renewing(h, Exports(90, 100, 10));
        h.FailWrites();
        Assert.Equal(SpentOffline(88), await h.License.UseAsync("exports", 2));
        // Held in memory, it counts toward the status but no request carries it: it has no place in the journal yet.
        var meters = (await h.License.RefreshAsync()).Meters["exports"];
        Assert.Equal((88L, 2L), (meters.Left, meters.Unsynced));
        Assert.Null(ReportOf(h, "validate", 0));
        // Another process lands an entry, and the disk is back: the spend lands in it, and the entry is what is reported.
        h.Stored = h.Stored! with { Spends = [Entry(Id('Y'), n: 1, at: Now - 5)] };
        h.FailWrites(false);
        await h.License.RefreshAsync();
        AssertJson.Equal(new JsonArray(new JsonObject { ["id"] = Id('Y'), ["meter"] = "exports", ["n"] = 3, ["at"] = Now - 5 }), ReportOf(h, "validate", 1));
    }

    [Fact]
    public async Task A_spend_held_in_memory_is_reported_with_a_release_once_it_has_landed_as_everything_the_journal_holds()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        h.Http.Answer("deactivate", 200, new JsonObject());
        h.FailWrites();
        await h.License.UseAsync("exports", 2);
        h.FailWrites(false);
        await h.License.DeactivateAsync();
        var spends = ReportOf(h, "deactivate")!;
        Assert.Equal(("exports", 2L), (Str(spends.Single()!["meter"]), Long(spends.Single()!["n"])));
    }

    [Fact]
    public async Task A_spend_held_in_memory_that_is_unanswered_or_a_void_opens_once_by_its_id_and_only_the_offline_spends_are_withheld()
    {
        var h = Holding(new JsonObject { ["exports"] = Meter(90, 100, 10), ["renders"] = Meter(5, 5, 0, resets: true) });
        Renewing(h, TwoMeters());
        h.FailWrites();
        h.Http.Fail = true;
        // In doubt, then refused after asking: an unanswered spend, and a void. A third, made while the journal waits, never left the device.
        Assert.Equal(SpentOffline(88), await h.License.UseAsync("exports", 2));
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports", 1));
        h.Http.Fail = false;
        await h.License.RefreshAsync();
        var report = ReportOf(h, "validate", 0)!;
        Assert.Equal(
            [("exports", 2L, false), ("renders", 1L, true)],
            report.Select(e => (Str(e!["meter"])!, Long(e["n"])!.Value, e["void"] is not null)));
    }

    [Fact]
    public async Task A_view_that_carried_held_spends_is_judged_by_what_is_stored_though_a_save_in_the_same_call_landed_them()
    {
        var h = Create();
        // An open lease inside its upgrade window, so a check-in notes the major it ran (a save of its own).
        var lease = h.SignLease(new LeaseInput
        {
            Sub = Key,
            Major = Offline.OpenMajor,
            UpgradeUntil = (Now + 10 * Day) / 1000,
            TtlSeconds = 30 * 86_400,
            Met = Exports(90, 100, 10),
        });
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = lease, BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs };
        var storage = new ScriptedStorage(h.Adapters.Storage!) { OnSave = (_, _) => throw new DiskSaidNoException() };
        var app = AppOver(h, storage);
        h.Http.Lease("validate", lease);
        Assert.Equal(SpentOffline(88), await app.UseAsync("exports", 2));
        // The next call's first save fails; before the second another process lands an entry the held spend joins.
        storage.Reset();
        storage.OnSave = (number, latest) => number switch
        {
            1 => throw new DiskSaidNoException(),
            2 => latest with { Spends = [Entry(Id('Y'), n: 1, at: Now - 5)] },
            _ => latest,
        };
        await app.RefreshAsync();
        // The spend landed in that entry before the request was made; the request reports the entry, not the spend under an id of its own.
        AssertJson.Equal(new JsonArray(new JsonObject { ["id"] = Id('Y'), ["meter"] = "exports", ["n"] = 3, ["at"] = Now - 5 }), ReportOf(h, "validate"));
    }

    [Fact]
    public async Task A_spend_held_in_memory_is_reported_with_nothing_of_another_seat_s_journal_stored_meanwhile()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        var storage = new ScriptedStorage(h.Adapters.Storage!);
        var app = AppOver(h, storage);
        h.FailWrites();
        h.Http.Fail = true;
        Assert.True((await app.UseAsync("exports", 2)).Offline);
        h.Http.Fail = false;
        Renewing(h, Exports(90, 100, 10));
        // By the time the request reports, another process has taken another seat with a journal of its own.
        var other = new StoredState { Key = Key, ActivationId = "act_9", Lease = h.Stored!.Lease, Spends = [Entry(Id('Z'), n: 5, at: LongAgo, kind: SpendKind.Unanswered)] };
        storage.ReadOverride = readOnly => readOnly ? other : null;
        await app.RefreshAsync();
        Assert.False(BodyOf(h, "validate").ContainsKey("spends"));
    }

    [Fact]
    public async Task A_spend_held_in_memory_does_not_make_status_ask_at_every_call_for_spends_no_request_carries()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        Renewing(h, Exports(90, 100, 10));
        h.FailWrites();
        await h.License.UseAsync("exports", 2);
        h.SetClock(Now + 2 * Minute);
        for (var i = 0; i < 3; i++) await h.License.StatusAsync();
        Assert.Equal(0, h.Http.Count("validate"));
        // Landed, the journal asks as it does, once.
        h.FailWrites(false);
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task An_answer_starts_the_hour_s_wait_only_when_the_request_carried_a_report_not_for_a_journal_that_only_it_read()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        Renewing(h, Exports(90, 100, 10));
        var writes = "refused";
        var storage = new ScriptedStorage(h.Adapters.Storage!)
        {
            OnSave = (_, latest) =>
            {
                if (writes == "refused") throw new DiskSaidNoException();
                if (writes != "refused once") return latest;
                writes = "allowed";
                throw new DiskSaidNoException();
            },
        };
        var app = AppOver(h, storage);
        Assert.Equal(SpentOffline(88), await app.UseAsync("exports", 2));
        // The next call's first save fails and its answer's save lands the spend: it read a journal, and sent none of it.
        writes = "refused once";
        await app.RefreshAsync();
        Assert.Null(ReportOf(h, "validate", 0));
        Assert.Single(Journal(h));
        Assert.NotEqual(Offline.AskAgainMs, h.Stored!.BackoffMs);
        // So the journal that has landed is offered at the next status() once the minute's wait is over, not an hour on.
        h.SetClock(Now + 2 * Minute);
        await app.StatusAsync();
        Assert.Equal(2, h.Http.Count("validate"));
        var report = ReportOf(h, "validate", 1)!;
        Assert.Equal(("exports", 2L), (Str(report.Single()!["meter"]), Long(report.Single()!["n"])));
    }

    [Fact]
    public async Task A_spend_held_in_memory_is_not_reported_with_a_use_either_whose_own_spend_is_the_report_is_what_is_stored()
    {
        var h = Holding(new JsonObject { ["exports"] = Meter(90, 100, 10), ["renders"] = Meter(5, 5, 0, resets: true) }, Waiting);
        h.FailWrites();
        await h.License.UseAsync("exports", 2);
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now }));
        Assert.Equal(UseResult.Spent(4), await h.License.UseAsync("renders"));
        Assert.Equal(("renders", 1L), (Str(UseBody(h)["meter"]), Long(UseBody(h)["n"])));
        Assert.False(UseBody(h).ContainsKey("spends"));
    }

    // --- calls one at a time ------------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_two_calls_made_together_one_after_the_other_each_a_spend_of_its_own()
    {
        var h = Holding(Exports(90, 100, 10));
        h.Http.Fail = true;
        var (a, b) = (h.License.UseAsync("exports", 3), h.License.UseAsync("exports", 4));
        Assert.Equal(SpentOffline(87), await a);
        Assert.Equal(SpentOffline(83), await b);
        Assert.Equal(7, Total(Journal(h)));
    }

    [Fact]
    public async Task Waits_behind_a_status_already_running_and_sees_what_it_saved()
    {
        var h = Holding();
        var lease = Lease(h, Exports(60, 100, 10), now: Now + 1000);
        h.SetClock(Now + 1000);
        h.Http.Lease("validate", lease);
        h.Http.On("use", _ => Metered.Spent(59, Now + 1000));
        var refreshing = h.License.RefreshAsync();
        var spending = h.License.UseAsync("exports");
        await refreshing;
        Assert.Equal(UseResult.Spent(59), await spending);
    }

    // --- a disk that keeps refusing ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_disk_that_keeps_refusing_costs_the_process_a_short_list_of_changes_however_many_spends_it_holds_and_none_is_lost()
    {
        var h = Holding(Exports(1_000_000, 1_000_000, 1_000_000));
        h.Http.Fail = true;
        h.FailWrites();
        for (var i = 0; i < 400; i++) Assert.True((await h.License.UseAsync("exports")).Ok);
        var meters = (await h.License.StatusAsync()).Meters["exports"];
        Assert.Equal((1_000_000L - 400, 400L), (meters.Left, meters.Unsynced));
        h.FailWrites(false);
        await h.License.StatusAsync();
        Assert.Equal(400, Total(Journal(h)));
    }

    [Fact]
    public async Task A_disk_that_keeps_refusing_leaves_the_process_holding_a_short_list_of_changes_not_one_for_each_spend()
    {
        var h = Holding(Exports(1_000_000, 1_000_000, 1_000_000));
        h.Http.Fail = true;
        h.FailWrites();
        for (var i = 0; i < 400; i++) await h.License.UseAsync("exports");
        // The first spend in doubt and the offline ones after it, folded into one: not four hundred.
        var held = h.License.HeldChanges;
        Assert.InRange(held.Count, 1, 4);
        Assert.Equal(400, held.OfType<SpendOp>().Sum(op => op.N));
        Assert.All(held.OfType<SpendOp>(), op => Assert.True(op.Told));
    }

    [Fact]
    public async Task A_disk_that_keeps_refusing_keeps_the_newest_voids_in_memory_and_refuses_no_covered_spend_for_them()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.FailWrites();
        for (var minute = 0; minute < 20; minute++)
        {
            h.SetClock(Now + minute * Minute);
            Assert.Equal(Refused(UseError.OfflineLimit, 5), await h.License.UseAsync("renders"));
        }
        // A spend the allowance covers is made, whatever the uncovered ones did to what this process holds.
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports", 3));
        h.FailWrites(false);
        await h.License.StatusAsync();
        var stored = Journal(h);
        // A void is no spend told: it lands under the journal's rules, the newest sixteen of the twenty.
        Assert.Equal(Enumerable.Range(0, Meters.MaxPendingVoids).Select(i => Now + (4 + i) * Minute), stored.Where(e => e.Kind == SpendKind.Void).Select(e => e.At));
        Assert.Equal([("exports", 3L)], stored.Where(e => e.Kind != SpendKind.Void).Select(e => (e.Meter, e.N)));
    }

    [Fact]
    public async Task A_disk_that_keeps_refusing_holds_in_memory_no_more_than_the_newest_voids_and_marks_none_of_them_told()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.FailWrites();
        for (var minute = 0; minute < 20; minute++)
        {
            h.SetClock(Now + minute * Minute);
            await h.License.UseAsync("renders");
        }
        var voids = h.License.HeldChanges.OfType<SpendOp>().Where(op => op.Kind == SpendKind.Void).ToList();
        Assert.Equal(Enumerable.Range(0, Meters.MaxPendingVoids).Select(i => Now + (4 + i) * Minute), voids.Select(op => op.At));
        Assert.All(voids, op => Assert.False(op.Told));
    }

    // --- two instances of the app over one state file -------------------------------------------------------------------

    private static readonly string StatePath = Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json");

    [Fact]
    public async Task Two_instances_over_one_state_file_never_lose_each_other_s_spends_a_save_that_finds_the_file_changed_under_it_merges_onto_it_and_lands_once()
    {
        var h = Holding(Exports(1_000, 1_000, 1_000));
        var fs = new MemoryFileSystem();
        fs.Files[StatePath] = h.Stored!.ToJson();
        h.Http.Fail = true;
        var (a, b) = (AppOver(h, new FileStorage(StatePath, null, fs, _ => Task.CompletedTask, null)), AppOver(h, new FileStorage(StatePath, null, fs, _ => Task.CompletedTask, null)));
        // A's first save has read the file and is making its temp file: there, the other instance spends and saves, so A's
        // revision check finds the file changed and merges onto it. No timing: B runs inside A's write.
        var held = fs.HoldNext(Op.Create);
        var first = a.UseAsync("exports", 5);
        await Eventually.Until(() => held.Reached, "A's first save");
        Assert.Equal(SpentOffline(993), await b.UseAsync("exports", 7));
        held.Release();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Offline);
        var saved = StoredState.FromJson(fs.Files[StatePath])!;
        Assert.Equal([(SpendKind.Unanswered, 5L), (SpendKind.Unanswered, 7L)], saved.Spends!.Select(e => (e.Kind, e.N)).Order());
        Assert.Equal(12, (await a.StatusAsync()).Meters["exports"].Unsynced);
        Assert.Equal(12, (await b.StatusAsync()).Meters["exports"].Unsynced);
    }

    // --- the allowance, judged where the spend lands -------------------------------------------------------------------------

    [Fact]
    public async Task Refuses_a_spend_that_another_process_took_the_allowance_from_meanwhile_and_keeps_theirs()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        var app = AppOver(h, new RacingStorage(h.Adapters.Storage!, latest => latest with { Spends = [Entry(Id('T'), n: 8, at: Now - 5)], Rev = 9 }));
        // Read with nothing unsynced, so the allowance covers 5; by the time it lands, 8 are there: 13 would pass 10.
        // What is left is as the landing found it: the 90, less the 8 the other process had spent by then.
        Assert.Equal(Refused(UseError.OfflineLimit, 82), await app.UseAsync("exports", 5));
        Assert.Equal([(SpendKind.Offline, 8L)], KindsAndCounts(h));
        // What the allowance still covers lands.
        Assert.True((await app.UseAsync("exports", 2)).Offline);
        Assert.Equal([10L], Journal(h).Select(e => e.N));
    }

    [Fact]
    public async Task Holds_a_spend_to_what_is_left_when_that_is_below_the_allowance_two_processes_spending_the_last_two_one_lands()
    {
        var h = Holding(Exports(2, 100, 50), Waiting);
        var app = AppOver(h, new RacingStorage(h.Adapters.Storage!, latest => latest with { Spends = [Entry(Id('T'), n: 2, at: Now - 5)], Rev = 9 }));
        // The allowance of 50 would take it; the 2 that are left, and the other process has spent them.
        Assert.Equal(Refused(UseError.OfflineLimit, 0), await app.UseAsync("exports", 2));
        Assert.Equal([(SpendKind.Offline, 2L)], KindsAndCounts(h));
        var alone = Holding(Exports(2, 100, 50), Waiting);
        Assert.Equal(SpentOffline(0), await alone.License.UseAsync("exports", 2));
        Assert.Equal(Refused(UseError.OfflineLimit, 0), await alone.License.UseAsync("exports", 1));
    }

    [Fact]
    public async Task Journals_a_void_for_a_spend_that_was_in_doubt_and_is_then_refused_at_landing()
    {
        var h = Holding(Exports(90, 100, 10));
        h.Http.Fail = true;
        var app = AppOver(h, new RacingStorage(h.Adapters.Storage!, latest => latest with { Spends = [Entry(Id('T'), n: 8, at: Now - 5)], Rev = 9 }));
        Assert.Equal(Refused(UseError.OfflineLimit, 82), await app.UseAsync("exports", 5));
        var sent = Str(UseBody(h)["id"]);
        Assert.Equal([(SpendKind.Offline, 8L), (SpendKind.Void, 5L)], KindsAndCounts(h));
        Assert.Equal(sent, Journal(h)[1].Id);
    }

    [Fact]
    public async Task Never_takes_back_a_spend_the_customer_was_told_was_made_it_lands_past_the_allowance()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        h.FailWrites();
        Assert.Equal(SpentOffline(84), await h.License.UseAsync("exports", 6));
        h.FailWrites(false);
        // Meanwhile another process of the app spent 8, and the journal is full.
        var crowd = Enumerable.Range(0, Meters.MaxPendingSpends - 1).Select(i => Entry(i.ToString().PadLeft(22, 'c'), $"m{i}", kind: SpendKind.Unanswered, at: Now - 100));
        h.Stored = h.Stored! with { Spends = [Entry(Id('T'), n: 8), .. crowd] };
        await h.License.StatusAsync();
        Assert.Equal(Meters.MaxPendingSpends, Journal(h).Count);
        Assert.Equal((Id('T'), 14L), (Journal(h)[0].Id, Journal(h)[0].N));
        var meters = (await h.License.StatusAsync()).Meters["exports"];
        Assert.Equal((14L, 76L), (meters.Unsynced, meters.Left));
    }

    [Fact]
    public async Task Lands_a_spend_the_customer_was_told_was_made_as_a_65th_entry_past_the_cap_when_it_opens_an_entry_of_its_own()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        h.FailWrites();
        Assert.True((await h.License.UseAsync("exports", 2)).Ok);
        h.FailWrites(false);
        h.Stored = h.Stored! with { Spends = Enumerable.Range(0, Meters.MaxPendingSpends).Select(i => Entry(i.ToString().PadLeft(22, 'c'), $"m{i}", kind: SpendKind.Unanswered, at: Now - 100)).ToList() };
        await h.License.StatusAsync();
        Assert.Equal(Meters.MaxPendingSpends + 1, Journal(h).Count);
        var last = Journal(h)[^1];
        Assert.Equal(("exports", 2L, SpendKind.Offline), (last.Meter, last.N, last.Kind));
    }

    // --- what is left when a landing refuses a spend and the state cannot say -----------------------------------------------------

    /// <summary>A License over the harness's state whose first save lands <paramref name="theirs"/> before it, then (when <paramref name="failReads"/>) reads no more.</summary>
    private static License Landing(Harness h, SpendEntry theirs, bool drop = false, bool failReads = false)
    {
        var storage = new ScriptedStorage(h.Adapters.Storage!);
        storage.OnSave = (_, latest) => latest with { Spends = [theirs], Rev = 9, MeterFigures = drop ? null : latest.MeterFigures };
        storage.AfterSave = _ => storage.FailReads = failReads;
        return AppOver(h, storage);
    }

    [Fact]
    public async Task Answers_0_left_when_the_state_cannot_be_read_again_just_after_the_refusal_however_much_the_figure_was()
    {
        var h = Holding(Exports(90, 100, 10), Waiting);
        var app = Landing(h, Entry(Id('T'), n: 8, at: Now - 5), failReads: true);
        Assert.Equal(Refused(UseError.OfflineLimit, 0), await app.UseAsync("exports", 5));
        // The refusal stands, and their spend is kept: only the figure was lost to the customer.
        Assert.Equal([(SpendKind.Offline, 8L)], KindsAndCounts(h));
    }

    [Fact]
    public async Task Answers_0_left_when_the_state_as_the_landing_found_it_has_no_figure_for_the_meter_any_more()
    {
        // The lease names no credits; the figure is a use answer's, stored beside it, and the other process's state has dropped it.
        var figures = new Dictionary<string, MeterFigure> { ["credits"] = new(20, 20, 10, false, Now) };
        var h = Holding(s => s with { BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs, MeterFigures = figures });
        var credits = (await h.License.StatusAsync()).Meters["credits"];
        Assert.Equal((20L, 10L), (credits.Left, credits.Offline));
        var app = Landing(h, Entry(Id('T'), "credits", 8, Now - 5), drop: true);
        Assert.Equal(Refused(UseError.OfflineLimit, 0), await app.UseAsync("credits", 5));
        Assert.Equal([("credits", SpendKind.Offline, 8L)], Journal(h).Select(e => (e.Meter, e.Kind, e.N)));
        // With the figure kept, the same refusal says what is left: 20 less their 8.
        var kept = Holding(s => s with { BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs, MeterFigures = figures });
        Assert.Equal(Refused(UseError.OfflineLimit, 12), await Landing(kept, Entry(Id('T'), "credits", 8, Now - 5)).UseAsync("credits", 5));
    }

    // --- what an answer stamps ---------------------------------------------------------------------------------------------------------

    private static async Task<Harness> Unconfirmed(HttpResult? answer)
    {
        var h = Holding(s => s with { VerdictAt = 111 });
        if (answer is null) h.Http.Fail = true;
        else h.Http.On("use", _ => answer);
        await h.License.UseAsync("exports");
        return h;
    }

    [Fact]
    public async Task Leaves_verdictAt_alone_for_a_spend_used_up_and_an_unknown_meter_they_are_no_verdict()
    {
        foreach (var answer in new[]
        {
            Metered.Spent(89, Now + 5),
            Word(409, "used-up", new JsonObject { ["left"] = 0, ["meters"] = TwoMeters(), ["at"] = Now }),
            Word(404, "unknown-meter"),
        })
        {
            Assert.Equal(111, (await Unconfirmed(answer)).Stored!.VerdictAt);
        }
    }

    [Fact]
    public async Task Sets_verdictAt_for_a_verdict_and_for_a_wait_as_a_validate_s_answer_does()
    {
        Assert.Equal(Now, (await Unconfirmed(Word(403, "revoked"))).Stored!.VerdictAt);
        Assert.Equal(Now, (await Unconfirmed(null)).Stored!.VerdictAt);
    }

    [Fact]
    public async Task Lets_a_validate_that_was_in_flight_across_a_spend_keep_its_lease()
    {
        var h = Holding();
        var other = h.LicenseFor(1);
        var renewed = Lease(h, TwoMeters(), now: Now + 1_000);
        var gate = new TaskCompletionSource();
        h.Http.On("validate", async _ =>
        {
            await gate.Task;
            return new HttpResult(200, new JsonObject { ["lease"] = renewed });
        });
        h.Http.On("use", _ => Metered.Spent(89, Now + 2_000));
        var validating = h.License.RefreshAsync();
        await Eventually.Until(() => h.Http.Called("validate"), "the validate");
        // Another process spends, asked later and answered first.
        h.SetClock(Now + 2_000);
        Assert.Equal(UseResult.Spent(89), await other.UseAsync("exports"));
        Assert.Null(h.Stored!.VerdictAt);
        gate.SetResult();
        await validating;
        Assert.Equal(renewed, h.Stored.Lease);
    }

    // --- a spend kept while a save is in flight ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spend_kept_while_a_save_is_in_flight_is_counted_once_beside_the_one_that_save_carries_not_folded_into_it_and_dropped()
    {
        var time = new ManualTime();
        var h = Holding(Exports(90, 100, 50), s => s with { BackoffSince = Now, BackoffMs = Offline.RetryUnansweredMs, LastSeen = Now }, new Options { Time = time });
        // A: kept in memory because the disk refused it.
        h.FailWrites();
        Assert.True((await h.License.UseAsync("exports", 2)).Offline);
        h.FailWrites(false);
        // A save in flight carries A and is held inside the storage.
        h.Http.Fail = true;
        var release = h.HoldNextWrite();
        var writes = h.Writes;
        var refreshing = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes > writes, "the held save");
        // B, of the same meter, is made behind it: kept in memory, beside A.
        var second = h.License.UseAsync("exports", 3);
        await Eventually.Quiet();
        time.Advance(Grace);
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10))).Offline);
        release();
        await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
        await h.License.StatusAsync();
        Assert.Equal(5, Total(Journal(h)));
        Assert.Equal(5, (await h.License.StatusAsync()).Meters["exports"].Unsynced);
    }

    // --- a spend the allowance cannot cover, while nothing answers ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_uncovered_spend_asks_once_a_minute_keeps_at_most_the_voids_and_leaves_the_journal_its_room_for_the_spends_it_can_make()
    {
        var h = Holding();
        h.Http.Fail = true;
        for (var minute = 0; minute < 30; minute++)
        {
            h.SetClock(Now + minute * Minute);
            Assert.Equal(Refused(UseError.OfflineLimit, 5), await h.License.UseAsync("renders"));
            // The same minute: the void is a wait of its own, and nothing is asked.
            Assert.Equal(Refused(UseError.OfflineLimit, 5), await h.License.UseAsync("renders"));
        }
        Assert.Equal(30, h.Http.Count("use"));
        var voids = Journal(h).Where(e => e.Kind == SpendKind.Void).ToList();
        Assert.Equal(Meters.MaxPendingVoids, voids.Count);
        // The newest sixteen stay: the first void to go was the oldest.
        Assert.Equal(Enumerable.Range(0, Meters.MaxPendingVoids).Select(i => Now + (14 + i) * Minute), voids.Select(e => e.At));
        // A spend on another meter that its allowance covers is made, whatever the uncovered ones did to the journal.
        Assert.Equal(SpentOffline(87), await h.License.UseAsync("exports", 3));
        Assert.Equal(Meters.MaxPendingVoids + 1, Journal(h).Count);
    }

    [Fact]
    public async Task An_uncovered_spend_asks_again_when_the_device_clock_is_set_back_below_the_void_though_the_guarded_clock_s_minute_is_still_running()
    {
        var h = Holding();
        h.Http.Fail = true;
        h.SetClock(Now + 10 * Minute);
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        // Inside the void's minute by both clocks: nothing is asked.
        h.SetClock(Now + 10 * Minute + 1000);
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        Assert.Equal(1, h.Http.Count("use"));
        // The device clock is set back to before the void while the guard stands at its minute: the wait is none, and a customer who is online is served.
        h.Http.Fail = false;
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now }));
        h.SetClock(Now);
        Assert.Equal(UseResult.Spent(4), await h.License.UseAsync("renders"));
        Assert.Equal(2, h.Http.Count("use"));
    }

    [Fact]
    public async Task An_uncovered_spend_is_served_at_once_when_the_server_is_back_and_its_minute_is_over_not_before()
    {
        var h = Holding();
        h.Http.Fail = true;
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        h.Http.Fail = false;
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now + Minute }));
        // Inside the void's minute: refused without asking.
        h.SetClock(Now + Minute - 1);
        Assert.Equal(UseError.OfflineLimit, (await h.License.UseAsync("renders")).Error);
        Assert.Equal(1, h.Http.Count("use"));
        h.SetClock(Now + Minute);
        Assert.Equal(UseResult.Spent(4), await h.License.UseAsync("renders"));
    }

    [Fact]
    public async Task An_uncovered_spend_asks_whatever_an_unanswered_spend_of_a_covered_meter_waits_for_and_a_void_that_is_ahead_of_the_clock()
    {
        var h = Holding(s => s with { Spends = [Entry(Id('U'), kind: SpendKind.Unanswered, at: Now - 1_000), Entry(Id('V'), kind: SpendKind.Void, at: Now + Day)] });
        h.Http.On("use", _ => new HttpResult(200, new JsonObject { ["spent"] = true, ["meters"] = new JsonObject { ["renders"] = Meter(4, 5, 0, resets: true) }, ["at"] = Now }));
        Assert.Equal(UseResult.Spent(4), await h.License.UseAsync("renders"));
    }

    // --- a wait that cannot be measured -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Leaves_out_only_the_entries_ahead_of_the_guarded_clock_and_the_others_wait_as_they_would_alone()
    {
        var ahead = Entry(Id('Z'), kind: SpendKind.Unanswered, at: Now + 5 * Day);
        var waiting = Holding(s => s with { Spends = [Entry(Id('A'), kind: SpendKind.Unanswered, at: Now - 30_000), ahead] });
        waiting.Http.On("use", _ => Metered.Spent(89));
        await waiting.License.UseAsync("exports");
        Assert.Equal(0, waiting.Http.Count("use"));
        var over = Holding(s => s with { Spends = [Entry(Id('A'), kind: SpendKind.Unanswered, at: Now - 2 * Minute), ahead] });
        over.Http.On("use", _ => Metered.Spent(89));
        await over.License.UseAsync("exports");
        Assert.Equal(1, over.Http.Count("use"));
    }
}
