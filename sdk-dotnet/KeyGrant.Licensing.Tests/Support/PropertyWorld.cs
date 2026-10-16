using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests.Support;

// The world a property test of UseAsync runs the real License in (UsePropertyTests): two processes of the app over one state
// file and one device clock, a KeyGrant server (PropServer) whose every answer a step may lose, delay or refuse, a disk that
// may refuse its writes, and a device clock that jumps forward and back. A case is plain data (Case), so a failing one is
// shrunk and kept as a named test.
//
// The oracle is written from the contract (section 12) and takes nothing from the code it judges but the License itself and
// the shape of a stored state: its constants, its figures and its waits are written again below.
//
// Concurrency is deterministic: a request of a process is parked at the world, which decides when and how it is answered, and a
// call is started only once every call running before it is parked at a request or done, so one thread runs at a time and a
// case replays the same way on any machine. The races that matter (a process's whole call between another's request and its
// answer) are the order the world answers its parked requests in.

/// <summary>A property that did not hold: which, and where.</summary>
internal sealed class PropertyFailure(string invariant, string message) : Exception($"{invariant}: {message}")
{
    public string Invariant { get; } = invariant;
}

internal sealed class PropertyWorld
{
    private const long Minute = 60_000;
    private const long Hour = 3_600_000;
    private const long Day = 24 * Hour;

    /// <summary>The contract's numbers (12.4, 12.5): the journal's entries and voids.</summary>
    private const int Entries = 64;
    private const int Voids = 16;

    private static readonly long Now = Harness.Now;

    private enum Net { Good, Down, Flaky }

    private enum Fate { Ok, Lost, LostAfter, Hang, HangAfter, Fail, FailAfter, Limited }

    /// <summary>A request a process made for one of its <c>UseAsync</c> calls.</summary>
    private sealed record Req(string Meter, long N, string Id, bool Applied, bool Answered);

    /// <summary>A request parked at the world: it is answered, lost or left to time out when the world says.</summary>
    private sealed class Parked(int index, string action, JsonObject body, Fate fate, CancellationToken token)
    {
        public int Index { get; } = index;
        public string Action { get; } = action;
        public JsonObject Body { get; } = body;
        public Fate Fate { get; } = fate;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<HttpResult> Delivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hung { get; set; }
    }

    /// <summary>A call under way: the process it is made on, and whether it is done.</summary>
    private sealed class Running(int p)
    {
        public int P { get; } = p;
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile bool Done;
        public object? Value;
        public Exception? Error;
    }

    /// <summary>A process of the app: the real <see cref="License"/>, with what the property test reads of it.</summary>
    private sealed class Proc(License license)
    {
        public License License { get; } = license;
        public List<Req> UseReqs { get; } = [];
    }

    /// <summary>What the state said of a spend before the call.</summary>
    private sealed record Pre(bool Licensed, bool Covered, long LeftAfter, int EntryCount, bool OpenOffline, bool Waiting, bool VoidWaiting);

    private readonly Case c;
    private readonly PropServer server;
    private readonly TestSigner signer;
    private readonly ManualTime time = new();
    private readonly object gate = new();
    private readonly Proc[] procs = new Proc[2];
    private readonly Parked?[] parkedOf = new Parked?[2];
    private readonly List<Running> active = [];
    private StoredState state = new();
    private long trueNow = Now;
    private long offset;
    private Net net = Net.Good;
    private bool diskFailing;
    private double[] rs = [0.5];
    private int rk;
    private int stepNo = -1;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What happened, for reading a failing case.</summary>
    public List<string> Trace { get; } = [];

    /// <summary>Whether the trace is written (a case being read, not run).</summary>
    public bool Tracing { get; set; }

    private readonly Dictionary<string, string> labels = new(StringComparer.Ordinal);
    private readonly HashSet<string> preIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> acked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> told = new(StringComparer.Ordinal);
    private int toldOps;
    private readonly Dictionary<string, long> lossAllowed = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Meter, long N, bool Full)> phantoms = [];
    private readonly HashSet<string> lostVoids = new(StringComparer.Ordinal);
    private readonly HashSet<string>[] heldVoids = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly HashSet<string> refusedVoids = new(StringComparer.Ordinal);
    /// <summary>The ids of spends a request reported as plain spends (not as voids): spends the customer was told were made.</summary>
    private readonly HashSet<string> reportedPlain = new(StringComparer.Ordinal);
    private string? violation;
    private List<(bool Applied, HashSet<string> Ids)> releases = [];

    public PropertyWorld(Case c)
    {
        this.c = c;
        signer = new TestSigner();
        server = new PropServer(c.Meters, signer, Now);
        foreach (var m in c.Meters.Keys)
        {
            acked[m] = 0;
            told[m] = 0;
            lossAllowed[m] = 0;
        }
        static string PreId(int i) => $"pre{i:D3}".PadRight(22, 'p');
        var spends = c.Preload.Select((e, i) => new SpendEntry(PreId(i), e.Meter, e.N, Now - e.AgoMs, e.Kind)).ToList();
        for (var i = 0; i < c.Preload.Count; i++)
        {
            var e = c.Preload[i];
            preIds.Add(PreId(i));
            if (e.Recorded) server.Recorded[PreId(i)] = new PropServer.Held(e.Meter, e.N);
            if (e.Kind == SpendKind.Void) phantoms.Add((PreId(i), e.Meter, e.N, false));
            else Credit(acked, e.Meter, e.N);
        }
        state = new StoredState { Key = Metered.Key, ActivationId = "act_1", LastSeen = Now, Spends = spends.Count > 0 ? spends : null };
        // A journal that begins past the cap is one that spends told to the customer once landed past it.
        toldOps = Math.Max(0, spends.Count - Entries);
        foreach (var e in spends)
        {
            if (e.Kind != SpendKind.Void) Credit(told, e.Meter, e.N);
        }
        for (var i = 0; i < procs.Length; i++) procs[i] = Process(i);
        state = state with { Lease = server.Lease("act_1") };
    }

    private static void Credit(Dictionary<string, long> into, string meter, long n)
    {
        if (into.ContainsKey(meter)) into[meter] += n;
    }

    /// <summary>An id as the trace shows it: the order it was first seen in, so a case reads the same every time.</summary>
    private string Label(string? id)
    {
        if (id is null) return "";
        if (!labels.ContainsKey(id)) labels[id] = preIds.Contains(id) ? id[..6] : $"s{labels.Count}";
        return labels[id];
    }

    // --- the machine ------------------------------------------------------------------------------

    private static StoredState Copy(StoredState s) => s with { };

    private sealed class WorldStorage(PropertyWorld w) : IStorageAdapter
    {
        public Task<StoredState?> ReadAsync(bool readOnly)
        {
            lock (w.gate) return Task.FromResult<StoredState?>(Copy(w.state));
        }

        public Task WriteAsync(StoredState next)
        {
            lock (w.gate)
            {
                if (w.diskFailing) throw new DiskSaidNoException();
                w.Landed(w.state, next);
                w.state = Copy(next);
            }
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(Func<StoredState, StoredState> merge)
        {
            lock (w.gate)
            {
                var next = merge(Copy(w.state));
                if (w.diskFailing) throw new DiskSaidNoException();
                w.Landed(w.state, next);
                w.state = Copy(next);
            }
            return Task.FromResult(true);
        }
    }

    private sealed class WorldHttp(PropertyWorld w, int index) : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
            w.Park(index, url.Split('/')[^1], body.DeepClone().AsObject(), cancellationToken);
    }

    private sealed class WorldClock(PropertyWorld w) : IClock
    {
        public long Now() => w.trueNow + w.offset;
    }

    private Proc Process(int index) => new(new License(new LicenseConfig
    {
        Product = Harness.Product,
        ApiBaseUrl = "https://api.test/",
        PublicJwk = signer.Jwk,
        TimeProvider = time,
        Adapters = new LicenseAdapters
        {
            Storage = new WorldStorage(this),
            Clock = new WorldClock(this),
            Fingerprint = new FixedFingerprinter("fp-test"),
            Http = new WorldHttp(this, index),
        },
    }));

    /// <summary>
    /// A save that landed. A void that left the journal and that the server did not confirm was replaced (12.4), and a
    /// replacement happens only when a void lands with sixteen standing: for each void that lands (one now in the journal
    /// that was not, or one held that is in neither, whether refused or confirmed in the same save) the oldest may go. A
    /// void dropped otherwise is a fault, kept for the next check to throw. A seat taken or dropped is the release's and the
    /// restart's to account for.
    /// </summary>
    private void Landed(StoredState before, StoredState after)
    {
        if (before.ActivationId != after.ActivationId || after.Key is null) return;
        var was = EntriesOf(before);
        var now = EntriesOf(after);
        var kept = now.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var had = was.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var dropped = was.Where(e => e.Kind == SpendKind.Void && !kept.Contains(e.Id) && !server.Voided.Contains(e.Id)).ToList();
        var landing = now.Where(e => e.Kind == SpendKind.Void && !had.Contains(e.Id)).Select(e => e.Id)
            .Concat(procs.Where(p => p is not null).SelectMany(p => p.License.HeldChanges.OfType<SpendOp>().Where(op => op.Kind == SpendKind.Void && !had.Contains(op.Id) && !kept.Contains(op.Id)).Select(op => op.Id)))
            .ToHashSet(StringComparer.Ordinal).Count;
        var standing = was.Count(e => e.Kind == SpendKind.Void);
        if (dropped.Count > 0)
        {
            if (dropped.Count <= landing && standing + landing > Voids) foreach (var e in dropped) lostVoids.Add(e.Id);
            else violation ??= $"{dropped.Count} void(s) left the journal of {standing} voids with {landing} landing, and nothing was to replace them";
        }
        // Left at its size, with sixteen voids' oldest not yet replaced, the journal took no more: a void that was held and did not land found no room.
        if (now.Count - (now.Count(e => e.Kind == SpendKind.Void) >= Voids ? 1 : 0) >= Entries)
        {
            foreach (var p in procs.Where(p => p is not null))
            {
                foreach (var op in p.License.HeldChanges.OfType<SpendOp>())
                {
                    if (op.Kind == SpendKind.Void && !kept.Contains(op.Id)) refusedVoids.Add(op.Id);
                }
            }
        }
    }

    // --- the network: requests parked at the world ------------------------------------------------------------

    private static Fate FateOf(Net net, double r)
    {
        if (net == Net.Good) return Fate.Ok;
        (double Below, Fate Fate)[] thresholds = net == Net.Down
            ? [(0.45, Fate.Lost), (0.75, Fate.LostAfter), (0.88, Fate.Hang)]
            : [(0.35, Fate.Ok), (0.45, Fate.Lost), (0.55, Fate.LostAfter), (0.65, Fate.Fail), (0.75, Fate.FailAfter), (0.82, Fate.Limited), (0.9, Fate.Hang)];
        foreach (var (below, fate) in thresholds)
        {
            if (r < below) return fate;
        }
        return Fate.HangAfter;
    }

    private Task<HttpResult> Park(int index, string action, JsonObject body, CancellationToken token)
    {
        Parked parked;
        lock (gate)
        {
            var r = rs[rk % rs.Length];
            rk++;
            parked = new Parked(index, action, body, FateOf(net, r), token);
            parkedOf[index] = parked;
        }
        // A request the SDK gave up on (its bound passed): nothing more to deliver.
        token.Register(() => parked.Delivery.TrySetCanceled());
        Signal();
        return parked.Delivery.Task;
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (gate)
        {
            old = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private async Task Until(Func<bool> condition)
    {
        var began = Environment.TickCount64;
        while (true)
        {
            Task waiting;
            lock (gate) waiting = changed.Task;
            if (condition()) return;
            if (Environment.TickCount64 - began > 30_000) throw new PropertyFailure("stuck", $"a call never settled (step {stepNo}): {Whereabouts()}");
            await Task.WhenAny(waiting, Task.Delay(1_000)).ConfigureAwait(false);
        }
    }

    /// <summary>Where every call is, for a failure that says a call never settled.</summary>
    private string Whereabouts()
    {
        lock (gate)
        {
            var calls = active.Select((a, i) => $"call {i} on p{a.P}: {(a.Done ? "done" : "running")}");
            var parked = parkedOf.Select((p, i) => p is null ? $"p{i}: nothing parked" : $"p{i}: {p.Action} {p.Fate}{(p.Hung ? " hung" : "")}");
            return $"{string.Join("; ", calls)}; {string.Join("; ", parked)}; timers {time.LiveTimers}";
        }
    }

    /// <summary>Every call running is parked at a request or done: nothing moves until the world moves it.</summary>
    private bool Quiescent()
    {
        lock (gate)
        {
            for (var p = 0; p < procs.Length; p++)
            {
                // The first call of a process not done runs (the others wait their turn); it is moving unless it is parked.
                var running = active.FirstOrDefault(a => a.P == p && !a.Done);
                if (running is not null && parkedOf[p] is null) return false;
            }
            return true;
        }
    }

    private Running Launch<T>(int p, Func<Task<T>> work)
    {
        var running = new Running(p);
        lock (gate) active.Add(running);
        running.Task = Task.Run(async () =>
        {
            try
            {
                running.Value = await work().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                running.Error = e;
            }
            finally
            {
                running.Done = true;
                Signal();
            }
        });
        return running;
    }

    /// <summary>
    /// Wait for a call just started to park at a request or be done. Behind another call of its process it waits its turn, and
    /// the call before it is parked or done, so nothing else moves either.
    /// </summary>
    private Task SettleAsync() => Until(Quiescent);

    /// <summary>
    /// Answer the requests parked, one at a time in an order the case fixes, until every call started is done. Requests left
    /// to time out (hung) are timed out last.
    /// </summary>
    private async Task DriveAsync()
    {
        for (var rounds = 0; ; rounds++)
        {
            await Until(Quiescent).ConfigureAwait(false);
            List<Running> pending;
            lock (gate) pending = active.Where(a => !a.Done).ToList();
            if (pending.Count == 0) break;
            if (rounds > 400) throw new PropertyFailure("stuck", $"a call never settled (step {stepNo})");
            Parked? next;
            lock (gate)
            {
                var waiting = parkedOf.Where(p => p is not null && !p.Hung).Select(p => p!).ToList();
                // Two requests parked: the case says whose is answered first.
                var last = rs[^1];
                next = waiting.Count == 0 ? null : waiting.Count == 1 || last < 0.5 ? waiting[0] : waiting[^1];
            }
            if (next is not null)
            {
                Release(next);
                continue;
            }
            Parked? silent;
            lock (gate)
            {
                // Two left that nobody answers: one at a time, in the order the case fixes, so a case replays the same way.
                var hung = parkedOf.Where(p => p is not null).Select(p => p!).ToList();
                silent = hung.Count == 0 ? null : hung.Count == 1 || rs[^1] < 0.5 ? hung[0] : hung[^1];
                if (silent is not null) parkedOf[silent.Index] = null;
            }
            if (silent is null) throw new PropertyFailure("stuck", $"a call never settled (step {stepNo}): {Whereabouts()}");
            // A request nobody answers times out, as an adapter's does at its bound (no answer, to the SDK).
            silent.Delivery.TrySetException(new TimeoutException("the request timed out"));
        }
        lock (gate) active.Clear();
    }

    /// <summary>What a request comes to over a network that may lose it, or its answer, or both.</summary>
    private void Release(Parked req)
    {
        Served Run(bool answered)
        {
            var s = server.Handle(req.Action, req.Body);
            Noted(req.Index, req.Action, req.Body, s, answered);
            return s;
        }
        void Deliver(HttpResult result)
        {
            lock (gate) parkedOf[req.Index] = null;
            req.Delivery.TrySetResult(result);
        }
        void Throw()
        {
            lock (gate) parkedOf[req.Index] = null;
            req.Delivery.TrySetException(new HttpRequestException("network down"));
        }
        switch (req.Fate)
        {
            case Fate.Ok:
                var ok = Run(true);
                Deliver(new HttpResult(ok.Status, ok.Json));
                break;
            case Fate.LostAfter:
                Run(false);
                Throw();
                break;
            case Fate.HangAfter:
                Run(false);
                req.Hung = true;
                break;
            case Fate.FailAfter:
                Deliver(new HttpResult(500, Run(false).Json));
                break;
            case Fate.Lost:
                Noted(req.Index, req.Action, req.Body, null, false);
                Throw();
                break;
            case Fate.Hang:
                Noted(req.Index, req.Action, req.Body, null, false);
                req.Hung = true;
                break;
            case Fate.Fail:
                Noted(req.Index, req.Action, req.Body, null, false);
                Deliver(new HttpResult(503, new JsonObject { ["error"] = "unavailable" }));
                break;
            default:
                Noted(req.Index, req.Action, req.Body, null, false);
                Deliver(new HttpResult(429, new JsonObject { ["error"] = "rate-limited" }));
                break;
        }
    }

    private void Noted(int index, string action, JsonObject body, Served? served, bool answered)
    {
        if (Tracing)
        {
            var reports = body["spends"]?.AsArray().Select(e => $"{Label(Str(e!["id"]))}:{Str(e["meter"])}:{Long(e["n"])}{(e["void"] is not null ? "v" : "")}");
            Trace.Add($"  p{index} {action} {{meter:{Str(body["meter"])},n:{Long(body["n"])},id:{Label(Str(body["id"]))},spends:[{string.Join(",", reports ?? [])}]}} -> {(served is null ? "lost" : served.Status.ToString())} {(answered ? "answered" : "unanswered")}");
        }
        foreach (var e in body["spends"]?.AsArray() ?? []) if (e!["void"] is null && Str(e["id"]) is { } plain) reportedPlain.Add(plain);
        if (action == "deactivate") releases.Add((served is not null, (body["spends"]?.AsArray().Select(e => Str(e!["id"])!) ?? []).ToHashSet(StringComparer.Ordinal)));
        if (action != "use") return;
        procs[index].UseReqs.Add(new Req(Str(body["meter"]) ?? "", Long(body["n"]) ?? 0, Str(body["id"]) ?? "", served?.Spent is not null, answered));
    }

    private PropertyFailure Fail(string invariant, string message) =>
        new(invariant, $"{message} (step {stepNo}: {(stepNo >= 0 && stepNo < c.Steps.Count ? c.Steps[stepNo].ToJson().ToJsonString() : "end")})");

    // --- the oracle's own reading of the contract ----------------------------------------------------------

    private static IReadOnlyList<SpendEntry> EntriesOf(StoredState s) => s.Spends ?? [];

    /// <summary>12.4: the unsynced spends of a meter, its offline and unanswered entries, never a void.</summary>
    private static long UnsyncedIn(IEnumerable<SpendEntry> spends, string meter) => spends.Where(e => e.Meter == meter && e.Kind != SpendKind.Void).Sum(e => e.N);

    /// <summary>12.4: a meter's base figure: the stored one when its time is at or after the lease's second, else the lease's own.</summary>
    private static (long Left, long Offline)? BaseFigure(StoredState s, string meter)
    {
        if (s.Key is null || s.Lease is null) return null;
        var payload = s.Lease.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var claims = JsonNode.Parse(Convert.FromBase64String(payload + new string('=', (4 - payload.Length % 4) % 4)))!;
        var iat = Long(claims["iat"])!.Value;
        if (s.MeterFigures is { } figures && figures.TryGetValue(meter, out var stored) && stored.At >= iat * 1000) return (stored.Left, stored.Offline);
        return claims["met"]?[meter] is { } met ? (Long(met["left"])!.Value, Long(met["offline"])!.Value) : null;
    }

    /// <summary>12.5: the journal's wait: k unanswered or void entries (none ahead of now), a minute doubling to an hour after the newest.</summary>
    private static bool JournalWait(IEnumerable<SpendEntry> spends, long now)
    {
        var opened = spends.Where(e => e.Kind != SpendKind.Offline && e.At <= now).ToList();
        if (opened.Count == 0) return false;
        return now < opened.Max(e => e.At) + Math.Min(Hour, Minute * (1L << Math.Min(opened.Count - 1, 6)));
    }

    /// <summary>12.5: the wait of a spend the allowance refuses: a void under a minute old, by the guarded clock and the device clock.</summary>
    private static bool VoidWait(IEnumerable<SpendEntry> spends, long now, long clockNow) =>
        spends.Any(e => e.Kind == SpendKind.Void && e.At <= now && e.At <= clockNow && now < e.At + Minute);

    /// <summary>12.5: the wait an unanswered ask leaves, by the device clock.</summary>
    private static bool AskWait(StoredState s, long clockNow) =>
        s.BackoffSince is { } since && s.BackoffMs == Minute && clockNow >= since && clockNow < since + Minute;

    private async Task<Pre> PreOf(Call call)
    {
        var s = await procs[call.P].License.ViewAsync().ConfigureAwait(false);
        var spends = EntriesOf(s);
        var figure = BaseFigure(s, call.Meter);
        var unsynced = UnsyncedIn(spends, call.Meter);
        var clockNow = trueNow + offset;
        var guarded = Math.Max(clockNow, s.LastSeen ?? 0);
        var left = figure is null ? 0 : Math.Max(0, figure.Value.Left - unsynced);
        return new Pre(
            s.Key is not null && s.Lease is not null,
            figure is not null && unsynced + call.N <= figure.Value.Offline && call.N <= left,
            left - call.N,
            spends.Count,
            spends.Any(e => e.Kind == SpendKind.Offline && e.Meter == call.Meter),
            AskWait(s, clockNow) || JournalWait(spends, guarded),
            VoidWait(spends, guarded, clockNow));
    }

    // --- the steps ------------------------------------------------------------------------------------------

    public async Task RunAsync()
    {
        for (var i = 0; i < c.Steps.Count; i++)
        {
            stepNo = i;
            var step = c.Steps[i];
            if (Tracing) Trace.Add($"step {i}: {step.ToJson().ToJsonString()} net={net} disk={(diskFailing ? "failing" : "ok")} clock={trueNow + offset - Now}");
            await StepAsync(step).ConfigureAwait(false);
            if (Tracing) Describe();
            await CheckAsync().ConfigureAwait(false);
        }
        stepNo = c.Steps.Count;
        await ConvergeAsync().ConfigureAwait(false);
    }

    private async Task StepAsync(Step s)
    {
        if (s.R.Count > 0 || s.T is "use" or "status" or "repeat" or "deactivate")
        {
            rs = s.R.Count > 0 ? s.R.ToArray() : [0.5];
            rk = 0;
        }
        switch (s.T)
        {
            case "wait":
                Pass(s.Ms);
                return;
            case "repeat":
                for (var i = 0; i < s.Times; i++)
                {
                    Pass(s.GapMs);
                    await EnsureActiveAsync().ConfigureAwait(false);
                    await UseAsync(new Step { T = "use", Calls = [s.Call!], R = s.R }).ConfigureAwait(false);
                    await CheckAsync().ConfigureAwait(false);
                }
                return;
            case "jump":
                offset = Math.Max(-3 * Day, Math.Min(3 * Day, offset + s.Ms));
                return;
            case "net":
                net = s.Mode switch { "good" => Net.Good, "down" => Net.Down, _ => Net.Flaky };
                return;
            case "disk":
                diskFailing = s.Failing;
                return;
            case "status":
                await EnsureActiveAsync().ConfigureAwait(false);
                var status = Launch(s.P, async () => await procs[s.P].License.StatusAsync(s.Force).ConfigureAwait(false));
                await SettleAsync().ConfigureAwait(false);
                await DriveAsync().ConfigureAwait(false);
                if (status.Error is not null) throw Fail("status-rejected", status.Error.Message);
                return;
            case "deactivate":
                await EnsureActiveAsync().ConfigureAwait(false);
                await DeactivateAsync(s.P).ConfigureAwait(false);
                return;
            case "restart":
                Restart(s.P);
                return;
            case "use":
                await EnsureActiveAsync().ConfigureAwait(false);
                await UseAsync(s).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Time passes, for the server and the device alike.</summary>
    private void Pass(long ms)
    {
        trueNow += ms;
        server.Now = Math.Max(server.Now, trueNow);
    }

    /// <summary>
    /// A release drops the journal of the seat. The releasing process lands what it holds first, so the release carries its
    /// journal: the contract lets it drop an entry only when the server got none of it (no 200) or it is past the 64 a report
    /// holds; a void it was meant to carry and did not, in any other case, is a fault. What another process held only in
    /// memory is lost with the seat.
    /// </summary>
    private async Task DeactivateAsync(int p)
    {
        var proc = procs[p];
        var journal = EntriesOf(await proc.License.ViewAsync().ConfigureAwait(false));
        var others = procs.Where((_, i) => i != p).SelectMany(q => q.License.HeldChanges).ToList();
        releases = [];
        var release = Launch(p, async () => await proc.License.DeactivateAsync().ConfigureAwait(false));
        await SettleAsync().ConfigureAwait(false);
        await DriveAsync().ConfigureAwait(false);
        if (release.Error is not null)
        {
            // It throws, the key kept, when the state cannot be saved; any other reason is a bug.
            if (!diskFailing) throw Fail("deactivate-rejected", release.Error.Message);
            return;
        }
        var carried = releases.Where(r => r.Applied).SelectMany(r => r.Ids).ToHashSet(StringComparer.Ordinal);
        var applied = releases.Any(r => r.Applied);
        for (var index = 0; index < journal.Count; index++)
        {
            var e = journal[index];
            // The report holds the first 64 of the journal in the order the SDK keeps it: only what the server got none of (no 200) or what is past them may be dropped.
            var mayDrop = !applied || index >= Entries;
            if (e.Kind == SpendKind.Void)
            {
                if (carried.Contains(e.Id) || server.Voided.Contains(e.Id)) continue;
                if (!mayDrop) throw Fail("release-dropped-void", $"a release left a void of the journal behind: {Label(e.Id)} is entry {index} of {journal.Count} ({carried.Count} carried)");
                lostVoids.Add(e.Id);
            }
            else if (mayDrop)
            {
                // What the server holds of it already is not lost; the rest is, and the contract allows it.
                Credit(lossAllowed, e.Meter, Math.Max(0, e.N - (server.Recorded.TryGetValue(e.Id, out var held) ? held.N : 0)));
            }
        }
        foreach (var op in others)
        {
            if (op is SpendOp { Kind: SpendKind.Offline or SpendKind.Unanswered } spend) Credit(lossAllowed, spend.Meter, spend.N);
            else if (op is SpendOp { Kind: SpendKind.Void } voided) lostVoids.Add(voided.Id);
        }
    }

    /// <summary>The app ends and starts again: what its process held only in memory is gone, and the customer was told it was made.</summary>
    private void Restart(int p)
    {
        foreach (var op in procs[p].License.HeldChanges.OfType<SpendOp>())
        {
            if (op.Kind is SpendKind.Offline or SpendKind.Unanswered) Credit(lossAllowed, op.Meter, op.N);
            else lostVoids.Add(op.Id);
        }
        heldVoids[p] = new HashSet<string>(StringComparer.Ordinal);
        procs[p] = Process(p);
    }

    /// <summary>The seat is taken (on a good network and disk) when a release dropped it: the app activates again.</summary>
    private async Task EnsureActiveAsync()
    {
        StoredState current;
        lock (gate) current = state;
        if (current.Key is not null) return;
        var (savedNet, savedDisk) = (net, diskFailing);
        net = Net.Good;
        diskFailing = false;
        var activating = Launch(0, async () => await procs[0].License.ActivateAsync(Metered.Key).ConfigureAwait(false));
        await SettleAsync().ConfigureAwait(false);
        await DriveAsync().ConfigureAwait(false);
        if (activating.Error is not null || activating.Value is not ActivateResult { Ok: true }) throw Fail("reactivation", activating.Error?.Message ?? activating.Value?.ToString() ?? "no answer");
        (net, diskFailing) = (savedNet, savedDisk);
    }

    private async Task UseAsync(Step s)
    {
        var concurrent = s.Calls.Count > 1 || s.Status is not null;
        // Behind a call that has not finished a process saves nothing: what it spends offline is kept in memory, and told.
        var behind = s.Calls.Select((a, i) => s.Calls.Select((b, j) => i < j && a.P == b.P).Any(x => x)).Any(x => x)
            || (s.Status is { } side && s.Calls.Any(call => call.P == side.P));
        var risky = diskFailing || behind;
        var pres = new List<Pre>();
        foreach (var call in s.Calls) pres.Add(await PreOf(call).ConfigureAwait(false));
        foreach (var p in procs) p.UseReqs.Clear();
        var calls = new List<Running>();
        foreach (var call in s.Calls)
        {
            var proc = procs[call.P];
            var running = Launch(call.P, async () => await proc.License.UseAsync(call.Meter, call.N).ConfigureAwait(false));
            calls.Add(running);
            await SettleAsync().ConfigureAwait(false);
        }
        Running? sideStatus = null;
        if (s.Status is { } statusCall)
        {
            sideStatus = Launch(statusCall.P, async () => await procs[statusCall.P].License.StatusAsync(statusCall.Force).ConfigureAwait(false));
            await SettleAsync().ConfigureAwait(false);
        }
        await DriveAsync().ConfigureAwait(false);
        // What each process sees now: a spend after the first of two may meet a journal the first filled.
        var views = new List<IReadOnlyList<SpendEntry>>();
        foreach (var p in procs) views.Add(EntriesOf(await p.License.ViewAsync().ConfigureAwait(false)));
        for (var i = 0; i < s.Calls.Count; i++)
        {
            var call = s.Calls[i];
            var done = calls[i];
            if (done.Error is not null || done.Value is not UseResult result) throw Fail("use-rejected", done.Error?.ToString() ?? "no result");
            var reqs = procs[call.P].UseReqs.Where(q => q.Meter == call.Meter).ToList();
            if (Tracing) Trace.Add($"  p{call.P} use {call.Meter} {call.N} => {result}");
            Judge(call, pres[i], result, reqs, concurrent, risky, views[call.P]);
        }
        if (sideStatus?.Error is not null) throw Fail("status-rejected", sideStatus.Error.Message);
    }

    /// <summary>What the contract says of an answer, and the ledger of what the customer was told.</summary>
    private void Judge(Call call, Pre pre, UseResult res, List<Req> reqs, bool concurrent, bool risky, IReadOnlyList<SpendEntry> view)
    {
        var asked = reqs.Count > 0;
        var alone = !concurrent && pre.Licensed;
        if (alone && !pre.Covered && pre.VoidWaiting && asked) throw Fail("uncovered-asked-in-wait", $"{call.N} of {call.Meter} asked the server while a void under a minute old stands");
        if (alone && pre.Covered && asked == pre.Waiting) throw Fail("covered-ask-rule", $"{call.N} of {call.Meter} {(asked ? "asked the server while a wait runs" : "did not ask the server though no wait runs")}");
        if (res.Ok)
        {
            // 12.4: what is left is never told high. Made offline it is the figure less the unsynced, less the spend; online the server's own is at most what it has.
            if (alone && res.Offline && res.Left != pre.LeftAfter) throw Fail("left-wrong", $"{call.N} of {call.Meter} made offline answered {res.Left} left, the figure and the unsynced say {pre.LeftAfter}");
            if (alone && !res.Offline && res.Left > server.Left(call.Meter)) throw Fail("left-high", $"{call.N} of {call.Meter} answered {res.Left} left, the server has {server.Left(call.Meter)}");
            Credit(acked, call.Meter, call.N);
            if (res.Offline && risky)
            {
                Credit(told, call.Meter, call.N);
                toldOps++;
            }
            return;
        }
        // A void finds no room when the journal, less the oldest void if sixteen stand and the new one takes its place, still holds the cap; one that landed, or is held, found its room.
        var crowded = Math.Max(pre.EntryCount, view.Count) - (view.Count(e => e.Kind == SpendKind.Void) >= Voids ? 1 : 0) >= Entries;
        bool Placed(string id) => view.Any(e => e.Id == id) || procs.Any(p => p.License.HeldChanges.Any(op => op is SpendOp { Kind: SpendKind.Void } v && v.Id == id));
        // A process's requests of one meter are not told apart by call, so a spend the customer was told was made (another call of the step, asked and
        // unanswered, then kept as a plain entry or reported as one) is not a refused spend whose void is owed.
        bool Told(string id) => reportedPlain.Contains(id)
            || view.Any(e => e.Id == id && e.Kind != SpendKind.Void)
            || procs.Any(p => p.License.HeldChanges.Any(op => op is SpendOp { Kind: not SpendKind.Void } v && v.Id == id));
        foreach (var q in reqs)
        {
            if (q.Applied && !q.Answered && !Told(q.Id)) phantoms.Add((q.Id, q.Meter, q.N, crowded && !Placed(q.Id)));
        }
        if (concurrent || !pre.Licensed || (res.Error != UseError.OfflineLimit && res.Error != UseError.NoMeter)) return;
        if (pre.Covered && res.Error == UseError.OfflineLimit)
        {
            // A covered spend is refused only for the journal's size: it would open a 65th entry.
            var capped = pre.EntryCount >= Entries && (asked || !pre.OpenOffline);
            if (!capped) throw Fail("covered-refused", $"{call.N} of {call.Meter} was covered by the allowance and refused (journal of {pre.EntryCount})");
        }
        if (!pre.Covered && !asked && !pre.VoidWaiting) throw Fail("uncovered-unasked", $"{call.N} of {call.Meter} was refused without asking, and no void wait runs");
    }

    private void Describe()
    {
        string Show(SpendEntry e) => $"{Names.Of(e.Kind)}:{Label(e.Id)}:{e.Meter}:{e.N}";
        string ShowOp(JournalOp op) => op switch
        {
            ConfirmedOp confirmed => $"confirmed:{confirmed.Entries.Count}",
            FiguresOp => "figures",
            ActivatedOp => "activated",
            DeactivatedOp => "deactivated",
            SpendOp spend => $"{Names.Of(spend.Kind)}{(spend.Told ? "!" : "")}:{Label(spend.Id)}:{spend.Meter}:{spend.N}",
            _ => "?",
        };
        StoredState stored;
        lock (gate) stored = state;
        Trace.Add($"  stored [{string.Join(" ", EntriesOf(stored).Select(Show))}]");
        for (var i = 0; i < procs.Length; i++) Trace.Add($"  p{i} holds [{string.Join(" ", procs[i].License.HeldChanges.Select(ShowOp))}]");
    }

    // --- what must hold between steps, and at the end -------------------------------------------------------------

    /// <summary>A void held in memory that is gone, though it neither landed nor reached the server: sixteen were held and the oldest went, or the journal found no room for it.</summary>
    private void AccountVoids()
    {
        var stored = EntriesOf(CurrentState());
        for (var i = 0; i < procs.Length; i++)
        {
            var now = procs[i].License.HeldChanges.OfType<SpendOp>().Where(op => op.Kind == SpendKind.Void).Select(op => op.Id).ToHashSet(StringComparer.Ordinal);
            var before = heldVoids[i];
            foreach (var id in before)
            {
                if (now.Contains(id) || stored.Any(e => e.Id == id) || server.Voided.Contains(id) || lostVoids.Contains(id)) continue;
                if (before.Count >= Voids || refusedVoids.Contains(id)) lostVoids.Add(id);
                else throw Fail("void-dropped", $"a void held in memory is gone: {Label(id)} ({before.Count} held, {stored.Count} stored)");
            }
            heldVoids[i] = now;
        }
    }

    private StoredState CurrentState()
    {
        lock (gate) return state;
    }

    /// <summary>The phantoms the server recorded and no void has taken back.</summary>
    private List<(string Id, string Meter, long N, bool Full)> Outstanding() =>
        phantoms.Where(x => server.Recorded.ContainsKey(x.Id) && !server.Voided.Contains(x.Id)).ToList();

    private Task CheckAsync()
    {
        if (violation is not null) throw Fail("void-replaced", violation);
        var stored = EntriesOf(CurrentState());
        if (stored.Select(e => e.Id).Distinct().Count() != stored.Count) throw Fail("duplicate-id", "the journal holds one id twice");
        if (stored.Count > Entries + toldOps) throw Fail("journal-size", $"{stored.Count} entries stored, past the cap and what was told");
        if (stored.Count(e => e.Kind == SpendKind.Void) > Voids) throw Fail("void-cap", "more voids stored than the cap");
        foreach (var (m, spec) in c.Meters)
        {
            var unsynced = UnsyncedIn(stored, m);
            if (unsynced > spec.Offline + told[m]) throw Fail("allowance", $"{unsynced} of {m} unsynced past its allowance {spec.Offline} and the {told[m]} told");
        }
        var holding = false;
        foreach (var p in procs)
        {
            var held = p.License.HeldChanges;
            if (held.Count(op => op is SpendOp { Kind: SpendKind.Void }) > Voids) throw Fail("unsaved-voids", "a process holds more voids in memory than the cap");
            if (held.Any(op => op is SpendOp { Kind: SpendKind.Void, Told: true })) throw Fail("void-told", "a void was marked told");
            if (held.Count > Entries + Voids + 16) throw Fail("unsaved-size", $"{held.Count} changes held in memory");
            if (held.Count > 0) holding = true;
        }
        AccountVoids();
        // The server never holds more than the customer was told and the refusals it recorded: a spend counted twice shows here at once.
        foreach (var m in c.Meters.Keys)
        {
            var phantom = Outstanding().Where(x => x.Meter == m).Sum(x => x.N);
            if (server.Used(m) > acked[m] + phantom) throw Fail("counted-twice", $"{m}: the server holds {server.Used(m)}, the customer was told {acked[m]} (and {phantom} refused but recorded)");
        }
        // What was told while only memory held it excuses the allowance only while it is unreported: once a meter's journal drains, the loosening ends.
        foreach (var m in c.Meters.Keys)
        {
            if (UnsyncedIn(stored, m) == 0 && !procs.Any(p => p.License.HeldChanges.Any(op => op is SpendOp { Kind: not SpendKind.Void } spend && spend.Meter == m))) told[m] = 0;
        }
        if (stored.Count == 0 && !holding) toldOps = 0;
        return Task.CompletedTask;
    }

    /// <summary>The network and the disk come back, time passes, and every journal is reported: then the books are balanced.</summary>
    private async Task ConvergeAsync()
    {
        net = Net.Good;
        diskFailing = false;
        rs = [0];
        rk = 0;
        trueNow += 3 * Hour;
        server.Now = Math.Max(server.Now, trueNow);
        await EnsureActiveAsync().ConfigureAwait(false);
        // A journal past 64 entries is reported 64 at a time, so more than one check-in each may be needed.
        for (var round = 0; round < 4 && (round == 0 || EntriesOf(CurrentState()).Count > 0 || procs.Any(p => p.License.HeldChanges.Count > 0)); round++)
        {
            foreach (var p in procs)
            {
                var status = Launch(Array.IndexOf(procs, p), async () => await p.License.StatusAsync(true).ConfigureAwait(false));
                await SettleAsync().ConfigureAwait(false);
                await DriveAsync().ConfigureAwait(false);
                if (status.Error is not null) throw Fail("status-rejected", status.Error.Message);
                AccountVoids();
            }
        }
        if (violation is not null) throw Fail("void-replaced", violation);
        if (EntriesOf(CurrentState()).Count > 0) throw Fail("journal-left", $"the journal still holds {string.Join(" ", EntriesOf(CurrentState()).Select(e => $"{Names.Of(e.Kind)}:{Label(e.Id)}"))} after every process reported");
        foreach (var p in procs)
        {
            if (p.License.HeldChanges.Count > 0) throw Fail("unsaved-left", "a process still holds changes in memory");
        }
        // A refusal the server recorded is taken back, unless that very void was lost as the contract allows: replaced by a newer one, dropped with a release, held by a process that ended, or never given room.
        foreach (var x in Outstanding())
        {
            if (!x.Full && !lostVoids.Contains(x.Id)) throw Fail("void-lost", $"{x.Meter}: {x.N} refused after the server recorded it ({Label(x.Id)}), and its void did not reach the server though nothing lost it");
        }
        var shown = new List<LicenseStatus>();
        foreach (var p in procs) shown.Add(await p.License.StatusAsync().ConfigureAwait(false));
        foreach (var (m, spec) in c.Meters)
        {
            var used = server.Used(m);
            var outstanding = Outstanding().Where(x => x.Meter == m).Sum(x => x.N);
            if (used < acked[m] - lossAllowed[m] + outstanding) throw Fail("lost", $"{m}: the server holds {used}, the customer was told {acked[m]} (and {outstanding} refused but recorded)");
            if (used > acked[m] + outstanding) throw Fail("counted-twice", $"{m}: the server holds {used}, the customer was told {acked[m]} (and {outstanding} refused but recorded)");
            var left = Math.Max(0, spec.Total - used);
            foreach (var status in shown)
            {
                if (!status.Meters.TryGetValue(m, out var figure) || figure.Left != left || figure.Unsynced != 0) throw Fail("figures", $"{m}: status shows {(status.Meters.TryGetValue(m, out var f) ? f.ToString() : "none")}, the server has {left} left");
            }
        }
    }

    // --- running a case ------------------------------------------------------------------------------------------

    /// <summary>Run a case. Null when every property held.</summary>
    public static async Task<PropertyFailure?> RunCaseAsync(Case c, List<string>? trace = null)
    {
        var world = new PropertyWorld(c) { Tracing = trace is not null };
        try
        {
            await world.RunAsync().ConfigureAwait(false);
            return null;
        }
        catch (PropertyFailure failure)
        {
            trace?.AddRange(world.Trace);
            return failure;
        }
        catch (Exception e)
        {
            trace?.AddRange(world.Trace);
            return new PropertyFailure("exception", $"{e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>Run a case and give its whole trace, failing or not: what a replay is compared by.</summary>
    public static async Task<string> TraceOfAsync(Case c)
    {
        var world = new PropertyWorld(c) { Tracing = true };
        var ended = new StringBuilder();
        try
        {
            await world.RunAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ended.Append($"\nfailed: {e.Message}");
        }
        return string.Join("\n", world.Trace) + ended;
    }
}
