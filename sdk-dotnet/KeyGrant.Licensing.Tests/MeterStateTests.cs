using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

/// <summary>The journal and the figures in the stored state: the Electron SDK's JSON, field for field, read leniently, and equal by content.</summary>
public class MeterStateTests
{
    private static JsonObject Entry(object id, object meter, object n, object at, object kind) =>
        new() { ["id"] = JsonValue.Create(id), ["meter"] = JsonValue.Create(meter), ["n"] = JsonValue.Create(n), ["at"] = JsonValue.Create(at), ["kind"] = JsonValue.Create(kind) };

    private static StoredState Read(JsonObject json) => StoredState.FromJson(json.ToJsonString())!;

    [Fact]
    public void Writes_the_journal_and_the_figures_as_the_reference_writes_them()
    {
        var state = new StoredState
        {
            Key = "K",
            Spends = [new("AAAAAAAAAAAAAAAAAAAAAA", "exports", 2, 5, SpendKind.Offline), new("BBBBBBBBBBBBBBBBBBBBBB", "renders", 1, 6, SpendKind.Void)],
            MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(1, 2, 3, true, 9) },
        };
        AssertJson.Equal(
            new JsonObject
            {
                ["key"] = "K",
                ["spends"] = new JsonArray(
                    Entry("AAAAAAAAAAAAAAAAAAAAAA", "exports", 2, 5, "offline"),
                    Entry("BBBBBBBBBBBBBBBBBBBBBB", "renders", 1, 6, "void")),
                ["meterFigures"] = new JsonObject { ["exports"] = new JsonObject { ["left"] = 1, ["total"] = 2, ["offline"] = 3, ["resets"] = true, ["at"] = 9 } },
            },
            JsonNode.Parse(state.ToJson()));
        // And it reads back as it was written.
        Assert.Equal(state, StoredState.FromJson(state.ToJson()));
    }

    [Fact]
    public void Writes_an_empty_journal_and_no_figures_as_the_fields_absent()
    {
        var json = JsonNode.Parse((new StoredState { Key = "K", Spends = [], MeterFigures = new Dictionary<string, MeterFigure>() }).ToJson())!.AsObject();
        Assert.False(json.ContainsKey("spends"));
        Assert.False(json.ContainsKey("meterFigures"));
        Assert.Equal(new StoredState { Key = "K" }, new StoredState { Key = "K", Spends = [], MeterFigures = new Dictionary<string, MeterFigure>() });
    }

    [Fact]
    public void Reads_a_journal_an_Electron_build_wrote_numbers_written_with_a_point_included()
    {
        var read = StoredState.FromJson("{\"spends\":[{\"id\":\"x\",\"meter\":\"exports\",\"n\":4.0,\"at\":1790000000000.0,\"kind\":\"unanswered\"}],\"meterFigures\":{\"exports\":{\"left\":3.0,\"total\":4,\"offline\":1,\"resets\":false,\"at\":7}}}")!;
        Assert.Equal([new SpendEntry("x", "exports", 4, 1_790_000_000_000, SpendKind.Unanswered)], read.Spends);
        Assert.Equal(new MeterFigure(3, 4, 1, false, 7), read.MeterFigures!["exports"]);
    }

    public static TheoryData<string, string> BadEntries => new()
    {
        { "a count of 0", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":0,\"at\":1,\"kind\":\"offline\"}" },
        { "a negative count", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":-1,\"at\":1,\"kind\":\"offline\"}" },
        { "a fraction for a count", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":1.5,\"at\":1,\"kind\":\"offline\"}" },
        { "a count past 2^53 - 1", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":9007199254740993,\"at\":1,\"kind\":\"offline\"}" },
        { "a count that is text", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":\"2\",\"at\":1,\"kind\":\"offline\"}" },
        { "a negative time", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":1,\"at\":-1,\"kind\":\"offline\"}" },
        { "a time that is text", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":1,\"at\":\"1\",\"kind\":\"offline\"}" },
        { "a kind of another word", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":1,\"at\":1,\"kind\":\"Offline\"}" },
        { "no kind", "{\"id\":\"x\",\"meter\":\"exports\",\"n\":1,\"at\":1}" },
        { "an id that is a number", "{\"id\":3,\"meter\":\"exports\",\"n\":1,\"at\":1,\"kind\":\"offline\"}" },
        { "a meter that is a number", "{\"id\":\"x\",\"meter\":3,\"n\":1,\"at\":1,\"kind\":\"offline\"}" },
        { "no meter", "{\"id\":\"x\",\"n\":1,\"at\":1,\"kind\":\"offline\"}" },
        { "null", "null" },
        { "a list", "[1,2]" },
        { "a text", "\"x\"" },
    };

    [Theory]
    [MemberData(nameof(BadEntries))]
    public void Leaves_out_an_entry_of_another_shape_and_keeps_the_good_one_beside_it(string _, string entry)
    {
        var good = "{\"id\":\"ok\",\"meter\":\"exports\",\"n\":2,\"at\":1,\"kind\":\"offline\"}";
        var read = StoredState.FromJson($"{{\"spends\":[{entry},{good}]}}")!;
        Assert.Equal([new SpendEntry("ok", "exports", 2, 1, SpendKind.Offline)], read.Spends);
    }

    [Fact]
    public void Reads_a_journal_that_is_not_a_list_as_none_and_keeps_what_it_held_until_a_journal_lands()
    {
        var read = StoredState.FromJson("{\"key\":\"K\",\"spends\":\"junk\",\"meterFigures\":[1]}")!;
        Assert.Null(read.Spends);
        Assert.Null(read.MeterFigures);
        // Written back as it was while nothing lands.
        AssertJson.Equal(JsonNode.Parse("{\"key\":\"K\",\"spends\":\"junk\",\"meterFigures\":[1]}"), JsonNode.Parse(read.ToJson()));
        // A journal that lands replaces both fields whole.
        var landed = StateFields.WithJournal(read, [new SpendEntry("x", "exports", 1, 1, SpendKind.Offline)], new Dictionary<string, MeterFigure>());
        var json = JsonNode.Parse(landed.ToJson())!.AsObject();
        Assert.False(json.ContainsKey("meterFigures"));
        Assert.Single(json["spends"]!.AsArray());
        // And an empty one takes both away.
        var emptied = StateFields.WithJournal(read, [], new Dictionary<string, MeterFigure>());
        AssertJson.Equal(new JsonObject { ["key"] = "K" }, JsonNode.Parse(emptied.ToJson()));
    }

    [Fact]
    public void Reads_a_figure_only_with_three_whole_figures_and_a_time_and_resets_only_for_JSON_true()
    {
        var read = StoredState.FromJson(
            "{\"meterFigures\":{"
            + "\"a\":{\"left\":1,\"total\":2,\"offline\":3,\"at\":4},"
            + "\"b\":{\"left\":1,\"total\":2,\"offline\":3,\"at\":4,\"resets\":\"true\"},"
            + "\"c\":{\"left\":1,\"total\":2,\"offline\":3,\"at\":4,\"resets\":true},"
            + "\"d\":{\"left\":-1,\"total\":2,\"offline\":3,\"at\":4},"
            + "\"e\":{\"left\":1,\"total\":2,\"offline\":3},"
            + "\"f\":{\"left\":1.5,\"total\":2,\"offline\":3,\"at\":4},"
            + "\"g\":5,"
            + "\"__proto__\":{\"left\":9,\"total\":9,\"offline\":9,\"at\":1}}}")!;
        Assert.Equal(["__proto__", "a", "b", "c"], read.MeterFigures!.Keys.Order(StringComparer.Ordinal));
        Assert.False(read.MeterFigures["a"].Resets);
        Assert.False(read.MeterFigures["b"].Resets);
        Assert.True(read.MeterFigures["c"].Resets);
    }

    [Fact]
    public void Keeps_the_members_of_an_entry_this_SDK_does_not_know_out_of_the_journal_it_writes()
    {
        var read = StoredState.FromJson("{\"spends\":[{\"id\":\"x\",\"meter\":\"exports\",\"n\":1,\"at\":1,\"kind\":\"offline\",\"later\":true}]}")!;
        Assert.Single(read.Spends!);
        Assert.False(JsonNode.Parse(read.ToJson())!["spends"]![0]!.AsObject().ContainsKey("later"));
    }

    [Fact]
    public void Compares_states_by_the_content_of_the_journal_and_the_figures()
    {
        StoredState Made() => new()
        {
            Key = "K",
            Spends = [new("x", "exports", 2, 5, SpendKind.Offline)],
            MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(1, 2, 3, true, 9) },
        };
        Assert.Equal(Made(), Made());
        Assert.Equal(Made().GetHashCode(), Made().GetHashCode());
        Assert.NotEqual(Made(), Made() with { Spends = [new("x", "exports", 3, 5, SpendKind.Offline)] });
        Assert.NotEqual(Made(), Made() with { Spends = null });
        Assert.NotEqual(Made(), Made() with { MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(1, 2, 3, false, 9) } });
        Assert.NotEqual(Made(), Made() with { MeterFigures = null });
    }

    [Fact]
    public async Task Round_trips_through_the_default_storage_with_the_rest_of_the_state()
    {
        var fs = new MemoryFileSystem();
        var path = Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json");
        var storage = new FileStorage(path, null, fs, _ => Task.CompletedTask, null);
        var state = new StoredState
        {
            Key = "K",
            ActivationId = "act_1",
            Spends = [new("x", "exports", 2, 5, SpendKind.Unanswered)],
            MeterFigures = new Dictionary<string, MeterFigure> { ["exports"] = new(1, 2, 3, false, 9) },
        };
        await storage.WriteAsync(state);
        var read = await storage.ReadAsync(readOnly: false);
        Assert.Equal(state with { Rev = read!.Rev }, read);
        // A save of something else leaves them as they were.
        await storage.UpdateAsync(latest => Offline.Merged(latest, new StatePatch { [StateField.CheckedMajor] = 3 }));
        var after = await storage.ReadAsync(readOnly: false);
        Assert.Equal(state.Spends, after!.Spends);
        Assert.Equal(state.MeterFigures, after.MeterFigures);
        Assert.Equal(3, after.CheckedMajor);
    }

    [Fact]
    public void Lands_nothing_of_a_journal_on_a_state_with_another_activation_and_keeps_what_it_held()
    {
        var latest = new StoredState { ActivationId = "act_2", Spends = [new("x", "exports", 1, 1, SpendKind.Offline)] };
        var landed = Journal.Land(latest, [new SpendOp(SpendKind.Offline, "act_1", "y", "exports", 1, 1)], 1);
        Assert.Equal(latest, landed.State);
        Assert.Equal([false], landed.Refused);
    }

    [Fact]
    public void Lands_ops_in_order_and_is_the_state_itself_with_none()
    {
        var state = new StoredState { ActivationId = "act_1" };
        Assert.Same(state, Journal.Land(state, [], 1).State);
        var landed = Journal.Land(
            state,
            [new SpendOp(SpendKind.Offline, "act_1", "a", "exports", 2, 1), new SpendOp(SpendKind.Offline, "act_1", "b", "exports", 3, 2), new SpendOp(SpendKind.Unanswered, "act_1", "c", "renders", 1, 3)],
            10);
        Assert.Equal([new SpendEntry("a", "exports", 5, 1, SpendKind.Offline), new SpendEntry("c", "renders", 1, 3, SpendKind.Unanswered)], landed.State.Spends);
    }

    [Fact]
    public void Folds_the_changes_a_process_keeps_where_landing_the_fold_lands_what_landing_each_does()
    {
        var offline = (long n, long at) => new SpendOp(SpendKind.Offline, "act_1", $"o{at}", "exports", n, at, Told: true);
        var figure = (long left, long at) => new FiguresOp("act_1", new Dictionary<string, MeterFigure> { ["exports"] = new(left, 10, 2, false, at) });
        var ops = new List<JournalOp>
        {
            offline(1, 1), offline(2, 2),
            figure(8, 5), figure(7, 6), figure(9, 4),
            new ConfirmedOp("act_1", [new Confirmed("z", 1, false)]), new ConfirmedOp("act_1", [new Confirmed("z", 3, false), new Confirmed("z", 0, true)]),
            new SpendOp(SpendKind.Offline, "act_1", "p", "renders", 1, 9, Told: true),
        };
        var folded = Journal.Compacted(ops);
        // Neighbours fold; nothing folds across another kind of change.
        Assert.Equal(4, folded.Count);
        Assert.Equal(3, ((SpendOp)folded[0]).N);
        Assert.Equal("o1", ((SpendOp)folded[0]).Id);
        Assert.Equal(7, ((FiguresOp)folded[1]).Figures["exports"].Left);
        Assert.Equal([new Confirmed("z", 3, false), new Confirmed("z", 0, true)], ((ConfirmedOp)folded[2]).Entries);
        var start = new StoredState { ActivationId = "act_1", Spends = [new("z", "exports", 2, 0, SpendKind.Unanswered)] };
        Assert.Equal(Journal.Land(start, ops, 100).State, Journal.Land(start, folded, 100).State);
    }

    private const string A = "act_1";

    private static SpendOp Held(long n, string meter = "exports", string about = A) =>
        new(SpendKind.Offline, about, n.ToString().PadLeft(22, 'q'), meter, n, 1, Told: true);

    private static FiguresOp Figures(long left, long at, string about = A) =>
        new(about, new Dictionary<string, MeterFigure> { ["exports"] = new(left, 100, 10, false, at) });

    private static ConfirmedOp Confirming(long n, string about = A) => new(about, [new Confirmed(new string('A', 22), n, false)]);

    [Fact]
    public void Folds_the_offline_spends_of_one_meter_adding_their_counts_and_keeping_the_first_s_id()
    {
        var folded = Journal.Compacted([Held(1), Held(2), Held(3)]);
        var only = Assert.IsType<SpendOp>(Assert.Single(folded));
        Assert.Equal((6L, "1".PadLeft(22, 'q')), (only.N, only.Id));
    }

    [Fact]
    public void Folds_nothing_across_another_meter()
    {
        Assert.Equal(3, Journal.Compacted([Held(1), Held(2, "renders"), Held(3)]).Count);
        // Nor an unanswered spend into an offline one: only the open entry of a meter grows.
        Assert.Equal(2, Journal.Compacted([Held(1), new SpendOp(SpendKind.Unanswered, A, "u", "exports", 2, 1, Told: true)]).Count);
    }

    [Fact]
    public void Folds_nothing_across_another_activation()
    {
        Assert.Equal(2, Journal.Compacted([Held(1), Held(2, about: "act_2")]).Count);
        Assert.Equal(2, Journal.Compacted([Figures(70, 1), Figures(50, 2, about: "act_2")]).Count);
        Assert.Equal(2, Journal.Compacted([Confirming(2), Confirming(3, about: "act_2")]).Count);
    }

    [Fact]
    public void Folds_nothing_across_a_deactivation_or_another_kind_of_change()
    {
        Assert.Equal(3, Journal.Compacted([Held(1), new DeactivatedOp(), Held(2)]).Count);
        Assert.Equal(3, Journal.Compacted([Figures(70, 1), new DeactivatedOp(), Figures(50, 2)]).Count);
        Assert.Equal(3, Journal.Compacted([Confirming(2), new ActivatedOp(A), Confirming(3)]).Count);
        Assert.Equal(3, Journal.Compacted([Held(1), Figures(70, 1), Held(2)]).Count);
    }

    [Fact]
    public void Folds_figures_to_the_newer_of_each_meter_and_confirmations_to_the_larger_count_each_id_once()
    {
        var figures = Assert.IsType<FiguresOp>(Assert.Single(Journal.Compacted([Figures(70, 1), Figures(50, 2)])));
        Assert.Equal(50, figures.Figures["exports"].Left);
        figures = Assert.IsType<FiguresOp>(Assert.Single(Journal.Compacted([Figures(70, 2), Figures(50, 1)])));
        Assert.Equal(70, figures.Figures["exports"].Left);
        var id = new string('A', 22);
        foreach (var ops in new[] { new JournalOp[] { Confirming(2), Confirming(3) }, [Confirming(3), Confirming(2)] })
        {
            var confirmed = Assert.IsType<ConfirmedOp>(Assert.Single(Journal.Compacted(ops)));
            Assert.Equal([new Confirmed(id, 3, false)], confirmed.Entries);
        }
        // A void and a count of one id are two reports, not one.
        var both = Assert.IsType<ConfirmedOp>(Assert.Single(Journal.Compacted([Confirming(2), new ConfirmedOp(A, [new Confirmed(id, 0, true)])])));
        Assert.Equal([new Confirmed(id, 2, false), new Confirmed(id, 0, true)], both.Entries);
    }

    [Fact]
    public void Lands_the_folded_changes_as_the_ones_they_were_folded_from_would()
    {
        var start = new StoredState { ActivationId = A };
        var ops = new List<JournalOp> { Held(1), Held(2), Held(4), Figures(70, 1), Figures(50, 2), Confirming(2), Confirming(3) };
        Assert.Equal(Journal.Land(start, ops, 100).State, Journal.Land(start, Journal.Compacted(ops), 100).State);
    }

    [Fact]
    public void Keeps_the_newest_voids_a_process_holds_and_drops_the_older_ones()
    {
        var ops = Enumerable.Range(0, 20).Select(i => (JournalOp)new SpendOp(SpendKind.Void, "act_1", $"v{i}", "exports", 1, i)).ToList();
        ops.Insert(5, new SpendOp(SpendKind.Offline, "act_1", "o", "exports", 1, 5));
        var kept = Journal.BoundedVoids(ops);
        Assert.Equal(Meters.MaxPendingVoids + 1, kept.Count);
        Assert.Contains(kept, op => op is SpendOp { Kind: SpendKind.Offline });
        Assert.Equal(Enumerable.Range(4, Meters.MaxPendingVoids).Select(i => $"v{i}"), kept.OfType<SpendOp>().Where(op => op.Kind == SpendKind.Void).Select(op => op.Id));
    }
}
