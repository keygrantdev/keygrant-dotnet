using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

/// <summary>Saves from more than one process over one state file: revisions, re-merges, and answers ordered by when they were asked for.</summary>
public class TwoProcessTests
{
    private static readonly string State = Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json");
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private const long Now = 1_757_000_000_000;
    private const long Day = 86_400_000;

    private static FileStorage StorageOver(MemoryFileSystem fs) => new(State, null, fs, _ => Task.CompletedTask, null);

    /// <summary>The engine's own save, as a License makes it: the patch merged onto the state as stored at the moment of saving.</summary>
    private static Task Save(FileStorage storage, StatePatch patch, Basis? basis = null) =>
        storage.UpdateAsync(latest => Offline.Merged(latest, patch, basis));

    private static JsonObject Saved(MemoryFileSystem fs) => JsonNode.Parse(fs.Files[State])!.AsObject();

    /// <summary>A's save held at its create until B has saved.</summary>
    private static async Task Interleave(MemoryFileSystem fs, Func<Task> first, Func<Task> between)
    {
        var held = fs.HoldNext(Op.Create);
        var running = first();
        await Eventually.Until(() => held.Reached, "A's held save");
        await between();
        held.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stamps_every_save_with_the_next_revision()
    {
        var fs = new MemoryFileSystem();
        var storage = StorageOver(fs);
        await Save(storage, new StatePatch { [StateField.Key] = "K" });
        Assert.Equal(("K", 1L), (Saved(fs)["key"]!.GetValue<string>(), Saved(fs)["rev"]!.GetValue<long>()));
        await Save(storage, new StatePatch { [StateField.CheckedMajor] = 2 });
        Assert.Equal(("K", 2, 2L), (Saved(fs)["key"]!.GetValue<string>(), Saved(fs)["checkedMajor"]!.GetValue<int>(), Saved(fs)["rev"]!.GetValue<long>()));
    }

    public static TheoryData<string, string?> Starts => new()
    {
        { "a stamped state", "{\"key\":\"K\",\"rev\":4}" },
        { "an older SDK's state, never stamped", "{\"key\":\"K\"}" },
        { "no state yet", null },
    };

    [Theory]
    [MemberData(nameof(Starts))]
    public async Task Loses_neither_of_two_saves_that_interleave(string _, string? start)
    {
        var fs = new MemoryFileSystem(start is null ? new() : new() { [State] = start });
        var (a, b) = (StorageOver(fs), StorageOver(fs));
        await Interleave(
            fs,
            () => Save(a, new StatePatch { [StateField.KeptMajor] = 2 }),
            () => Save(b, new StatePatch { [StateField.CheckedMajor] = 3 }));
        Assert.Equal((2, 3), (Saved(fs)["keptMajor"]!.GetValue<int>(), Saved(fs)["checkedMajor"]!.GetValue<int>()));
        Assert.Equal([State], fs.Files.Keys);
    }

    private static readonly (string Name, StatePatch BPatch, Asked? BAsked, StatePatch APatch, Asked? AAsked, string[] Gone)[] RemovalCases =
    [
        ("B clears the refusal", new StatePatch { [StateField.Refused] = null }, null, new StatePatch { [StateField.KeptMajor] = 2 }, null, ["refused"]),
        (
            "B drops the key",
            new StatePatch { [StateField.Key] = null, [StateField.ActivationId] = null }, null,
            new StatePatch { [StateField.KeptMajor] = 2 }, null,
            ["key", "activationId"]),
        (
            "B's answer, asked for later, clears the refusal A's older answer would set again",
            new StatePatch { [StateField.Lease] = "L2", [StateField.Refused] = null }, new Asked(2_000, "act_1", 100),
            new StatePatch { [StateField.KeptMajor] = 2, [StateField.Refused] = Refusal.Revoked, [StateField.Lease] = null }, new Asked(1_000, "act_1", 100),
            ["refused"]),
    ];

    public static TheoryData<int, string> Removals()
    {
        var data = new TheoryData<int, string>();
        for (var i = 0; i < RemovalCases.Length; i++) data.Add(i, RemovalCases[i].Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Removals))]
    public async Task Keeps_what_another_process_removed_in_between(int index, string _)
    {
        var (_, bPatch, bAsked, aPatch, aAsked, gone) = RemovalCases[index];
        var fs = new MemoryFileSystem(new() { [State] = "{\"key\":\"K\",\"activationId\":\"act_1\",\"refused\":\"revoked\",\"verdictAt\":100,\"rev\":4}" });
        var (a, b) = (StorageOver(fs), StorageOver(fs));
        await Interleave(
            fs,
            () => Save(a, aPatch, aAsked is null ? null : new Basis { Answer = aAsked }),
            () => Save(b, bPatch, bAsked is null ? null : new Basis { Answer = bAsked }));
        var saved = Saved(fs);
        Assert.Equal(2, saved["keptMajor"]!.GetValue<int>());
        foreach (var field in gone) Assert.False(saved.ContainsKey(field), field);
        if (bAsked is not null) Assert.Equal(("L2", 2_000L), (saved["lease"]!.GetValue<string>(), saved["verdictAt"]!.GetValue<long>()));
    }

    [Fact]
    public async Task Keeps_the_higher_kept_major_another_process_noted_in_between()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"key\":\"K\",\"activationId\":\"act_1\",\"keptMajor\":2,\"rev\":1}" });
        var (a, b) = (StorageOver(fs), StorageOver(fs));
        var about = new Basis { About = new About("act_1") };
        await Interleave(fs, () => Save(a, new StatePatch { [StateField.KeptMajor] = 3 }, about), () => Save(b, new StatePatch { [StateField.KeptMajor] = 5 }, about));
        Assert.Equal(5, Saved(fs)["keptMajor"]!.GetValue<int>());
    }

    [Fact]
    public async Task Fails_with_a_conflict_after_a_few_tries_when_the_state_keeps_changing_keeping_the_other_s()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"key\":\"K\",\"rev\":1}" });
        var other = 1;
        // Another process saves every time this one has read and is about to write.
        fs.Before[Op.Create] = () =>
        {
            other += 1;
            fs.Files[State] = $"{{\"key\":\"OTHER\",\"rev\":{other}}}";
        };
        await Assert.ThrowsAsync<StateConflictException>(() => Save(StorageOver(fs), new StatePatch { [StateField.KeptMajor] = 2 }));
        Assert.Equal(FileStorage.UpdateAttempts, fs.Ops.Count(op => op.StartsWith("Create", StringComparison.Ordinal)));
        Assert.Equal(5, FileStorage.UpdateAttempts);
        AssertJson.Equal(new JsonObject { ["key"] = "OTHER", ["rev"] = other }, Saved(fs));
        Assert.Equal([State], fs.Files.Keys);
    }

    // --- answers saved by two instances of the app ---------------------------------------

    private sealed class OneFile
    {
        public TestSigner Signer { get; } = new();

        public MemoryFileSystem Fs { get; } = new();

        public string Sign(long at, LeaseInput? input = null) => Signer.SignLease(input ?? new LeaseInput { Sub = Key }, at);

        /// <summary>An instance whose clock reads <paramref name="now"/> and whose server answers each action as given.</summary>
        public License Instance(long now, Dictionary<string, HttpResult> answers, IFingerprinter? fingerprint = null) => new(new LicenseConfig
        {
            Product = "sluice",
            ApiBaseUrl = "https://api.test",
            PublicJwk = Signer.Jwk,
            Adapters = new LicenseAdapters
            {
                Storage = StorageOver(Fs),
                Http = new Answers(answers),
                Clock = new FixedClock(now),
                Fingerprint = fingerprint ?? new FixedFingerprinter("fp-test"),
            },
        });

        public StoredState Saved() => StoredState.FromJson(Fs.Files[State])!;

        public async Task<T> Interleave<T>(Func<Task<T>> first, Func<Task> between)
        {
            var saving = Fs.HoldNext(Op.Create);
            var running = first();
            await Eventually.Until(() => saving.Reached, "the first save");
            await between();
            saving.Release();
            return await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class Answers(Dictionary<string, HttpResult> answers) : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(answers.TryGetValue(url.Split('/')[^1], out var answer) ? answer : new HttpResult(404, new JsonObject()));
    }

    private sealed class FixedClock(long now) : IClock
    {
        public long Now() => now;
    }

    private static HttpResult LeaseAnswer(string lease, string aid = "act_1") => new(200, new JsonObject { ["lease"] = lease, ["activationId"] = aid });

    private static HttpResult Refusal403(string error) => new(403, new JsonObject { ["error"] = error });

    private static OneFile Licensed(Func<OneFile, StoredState>? stored = null)
    {
        var file = new OneFile();
        var state = stored?.Invoke(file) ?? new StoredState { Key = Key, ActivationId = "act_1", Lease = file.Sign(Now - Day), CheckedMajor = 1, LastSeen = Now - Day, Rev = 1 };
        file.Fs.Files[State] = state.ToJson();
        return file;
    }

    [Fact]
    public async Task Keeps_the_newer_answer_a_refusal_asked_for_later_survives_the_re_merge_of_an_older_ask_s_lease()
    {
        var file = Licensed();
        var a = file.Instance(Now, new() { ["validate"] = LeaseAnswer(file.Sign(Now)) });
        var b = file.Instance(Now + 1_000, new() { ["validate"] = Refusal403("revoked") });
        var answered = await file.Interleave(
            () => a.RefreshAsync(),
            async () => Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key }, await b.RefreshAsync()));
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, answered);
        Assert.Equal((Refusal.Revoked, Now + 1_000), (file.Saved().Refused, file.Saved().VerdictAt));
        Assert.Null(file.Saved().Lease);
        Assert.Equal(3, file.Fs.Ops.Count(op => op.StartsWith("Create", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Keeps_the_later_ask_s_answer_when_the_clock_was_put_back_between_the_two_asks()
    {
        var file = Licensed();
        var a = file.Instance(Now, new() { ["validate"] = LeaseAnswer(file.Sign(Now)) });
        var b = file.Instance(Now - 60_000, new() { ["validate"] = Refusal403("revoked") });
        await file.Interleave(
            () => a.RefreshAsync(),
            async () =>
            {
                // A save made at NOW + 2 s, before the clock went back, raised the guard.
                file.Fs.Files[State] = (file.Saved() with { LastSeen = Now + 2_000, Rev = file.Saved().Rev + 1 }).ToJson();
                await b.RefreshAsync();
            });
        Assert.Equal((Refusal.Revoked, Now + 2_000), (file.Saved().Refused, file.Saved().VerdictAt));
        Assert.Null(file.Saved().Lease);
    }

    [Fact]
    public async Task Keeps_a_refusal_asked_for_later_over_a_re_activation_of_the_same_seat_that_saves_after_it()
    {
        var file = Licensed();
        var w1 = file.Instance(Now, new() { ["activate"] = LeaseAnswer(file.Sign(Now)) });
        var w2 = file.Instance(Now + 1_000, new() { ["validate"] = Refusal403("revoked") });
        var activated = await file.Interleave(
            () => w1.ActivateAsync(Key),
            async () => Assert.Equal(LicenseReason.Revoked, (await w2.RefreshAsync()).Reason));
        Assert.Equal(ActivateResult.Success, activated);
        var saved = file.Saved();
        Assert.Equal((Key, "act_1", Refusal.Revoked, Now + 1_000), (saved.Key, saved.ActivationId, saved.Refused, saved.VerdictAt));
        Assert.Null(saved.Lease);
        // So the revoked device does not license offline.
        Assert.Equal((LicenseState.Invalid, LicenseReason.Revoked), await StateOf(w1.StatusAsync()));
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("activate")]
    public async Task Writes_nothing_of_a_renewal_that_lands_after_the_activation_it_renewed_is_gone(string action)
    {
        // W2's licence: an open lease inside its upgrade window, so a check-in notes the major it ran.
        var open = new LeaseInput { Sub = Key, Major = 1_000_000, UpgradeUntil = (Now + 10 * Day) / 1000 };
        var file = Licensed(f => new StoredState { Key = Key, ActivationId = "act_1", Lease = f.Sign(Now - Day, open), CheckedMajor = 1, LastSeen = Now - Day, Rev = 1 });
        var leaseB = file.Sign(Now, new LeaseInput { Sub = "SLUICE-BBBB-BBBB-BBBB-BBBB", Aid = "act_2" });
        var w1 = file.Instance(Now, new()
        {
            ["deactivate"] = new HttpResult(200, new JsonObject { ["ok"] = true }),
            ["activate"] = LeaseAnswer(leaseB, "act_2"),
        });
        var machine = new FuncFingerprinter(
            () => Task.FromResult("fp-machine"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, [])));
        var w2 = file.Instance(Now, new() { ["validate"] = LeaseAnswer(file.Sign(Now, open)) }, machine);

        await file.Interleave(
            () => w2.RefreshAsync(),
            async () =>
            {
                if (action == "deactivate") await w1.DeactivateAsync();
                else Assert.Equal(ActivateResult.Success, await w1.ActivateAsync("SLUICE-BBBB-BBBB-BBBB-BBBB"));
            });
        var saved = file.Saved();
        Assert.Null(saved.KeptMajor);
        Assert.Null(saved.DeviceKind);
        if (action == "deactivate") Assert.Null(saved.Key);
        else Assert.Equal(("SLUICE-BBBB-BBBB-BBBB-BBBB", "act_2", leaseB), (saved.Key, saved.ActivationId, saved.Lease));
    }

    private static async Task<(LicenseState, LicenseReason?)> StateOf(Task<LicenseStatus> status)
    {
        var s = await status;
        return (s.State, s.Reason);
    }
}
