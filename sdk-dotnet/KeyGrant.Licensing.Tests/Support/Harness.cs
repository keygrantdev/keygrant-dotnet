using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>A storage write the disk refused (an antivirus scan holding the file, say).</summary>
internal sealed class DiskSaidNoException() : IOException("EBUSY: the disk said no");

/// <summary>
/// A <see cref="License"/> over stand-in adapters: the state in memory (with a read that hangs, a write
/// held inside the adapter, or every write failing), a clock the test sets, a registry stash, a scripted
/// server and a fixed fingerprint (<c>fp-test</c>, kind unknown).
/// </summary>
internal sealed class Harness
{
    public const long Now = 1_757_000_000_000;
    public const long Day = 86_400_000;
    public const string Product = "sluice";

    private readonly object gate = new();
    private StoredState? stored;
    private long clockNow = Now;
    private long? registryStart;
    private int writes;
    private bool failWrites;
    private bool hangNextRead;
    private bool failReads;
    private int reads;
    private int failReadAt;
    private bool holdNextWrite;
    private Action releaseHeld = () => { };
    private int replaceAtRead;
    private StoredState? replacement;

    private Harness(StoredState? initial, Options options)
    {
        stored = initial;
        Time = options.Time ?? TimeProvider.System;
        Adapters = new LicenseAdapters
        {
            Storage = new MemoryStorage(this),
            Registry = new MemoryRegistry(this),
            Clock = new ClockAt(this),
            Fingerprint = options.Fingerprint ?? new FixedFingerprinter("fp-test"),
            Http = Http,
        };
        License = new License(new LicenseConfig
        {
            Product = Product,
            ApiBaseUrl = "https://api.test/",
            PublicJwk = Signer.Jwk,
            Major = options.Major ?? 1,
            DeviceName = options.DeviceName,
            HttpTimeout = options.HttpTimeout ?? LicenseConfig.DefaultHttpTimeout,
            DeviceHold = options.DeviceHold ?? true,
            Adapters = Adapters,
            TimeProvider = Time,
        });
    }

    public sealed record Options
    {
        public int? Major { get; init; }
        public string? DeviceName { get; init; }
        public TimeSpan? HttpTimeout { get; init; }
        public IFingerprinter? Fingerprint { get; init; }
        public bool? DeviceHold { get; init; }
        public TimeProvider? Time { get; init; }
    }

    public static Harness Create(StoredState? initial = null, Options? options = null) => new(initial, options ?? new Options());

    public static Harness Create(int major) => Create(null, new Options { Major = major });

    public License License { get; }

    public LicenseAdapters Adapters { get; }

    public FakeHttp Http { get; } = new();

    public TestSigner Signer { get; } = new();

    public TimeProvider Time { get; }

    /// <summary>Another build of the app on this device: the same storage and server, another major.</summary>
    public License LicenseFor(int major, IFingerprinter? fingerprint = null) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test/",
        PublicJwk = Signer.Jwk,
        Major = major,
        Adapters = fingerprint is null ? Adapters : new LicenseAdapters
        {
            Storage = Adapters.Storage,
            Registry = Adapters.Registry,
            Clock = Adapters.Clock,
            Http = Adapters.Http,
            Fingerprint = fingerprint,
        },
        TimeProvider = Time,
    });

    /// <summary>A lease as the server signs one: activation <c>act_1</c>, major 1.</summary>
    public string Sign(string sub, string model, long ttlSeconds, long now = Now) =>
        Signer.SignLease(new LeaseInput { Sub = sub, Model = model, TtlSeconds = ttlSeconds }, now);

    public string SignLease(LeaseInput input, long now = Now) => Signer.SignLease(input, now);

    /// <summary>A trial lease for the harness's device (<c>fp-test</c>), as the server signs one: to the trial's own end (<paramref name="endsAt"/>, ms).</summary>
    public string TrialLease(long endsAt, long now = Now)
    {
        var claim = Hashes.FingerprintClaim("fp-test");
        return Signer.SignLease(new LeaseInput { Sub = $"trial:{claim}", Aid = claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = (endsAt - now) / 1000 }, now);
    }

    public StoredState? Stored
    {
        get
        {
            lock (gate) return stored;
        }
        set
        {
            lock (gate) stored = value;
        }
    }

    public void SetClock(long now)
    {
        lock (gate) clockNow = now;
    }

    public long ClockNow
    {
        get
        {
            lock (gate) return clockNow;
        }
    }

    public long? Registry
    {
        get
        {
            lock (gate) return registryStart;
        }
        set
        {
            lock (gate) registryStart = value;
        }
    }

    public int Writes
    {
        get
        {
            lock (gate) return writes;
        }
    }

    /// <summary>Every storage write fails from now on (false to stop).</summary>
    public void FailWrites(bool on = true)
    {
        lock (gate) failWrites = on;
    }

    /// <summary>The next storage read never settles: an adapter stuck on a dead disk.</summary>
    public void HangNextRead()
    {
        lock (gate) hangNextRead = true;
    }

    /// <summary>Every storage read throws from now on (false to stop): a file an antivirus scan holds.</summary>
    public void FailReads(bool on = true)
    {
        lock (gate) failReads = on;
    }

    /// <summary>The <paramref name="n"/>th storage read from now (1 for the next) throws; the others answer.</summary>
    public void FailReadAt(int n)
    {
        lock (gate) failReadAt = reads + n;
    }

    /// <summary>
    /// Another process saves <paramref name="state"/> just before the <paramref name="n"/>th storage read
    /// from now (1 for the next): that read, and every later one, finds it.
    /// </summary>
    public void SavedElsewhereBeforeRead(int n, StoredState state)
    {
        lock (gate)
        {
            replaceAtRead = reads + n;
            replacement = state;
        }
    }

    /// <summary>How many storage reads have been made.</summary>
    public int Reads
    {
        get
        {
            lock (gate) return reads;
        }
    }

    /// <summary>The next storage write is held inside the adapter with the whole state it was handed; returns what lets it land.</summary>
    public Action HoldNextWrite()
    {
        lock (gate) holdNextWrite = true;
        return () =>
        {
            Action release;
            lock (gate) release = releaseHeld;
            release();
        };
    }

    private sealed class MemoryStorage(Harness h) : IStorageAdapter
    {
        public Task<StoredState?> ReadAsync(bool readOnly)
        {
            lock (h.gate)
            {
                h.reads += 1;
                if (h.reads == h.replaceAtRead) h.stored = h.replacement;
                if (h.hangNextRead)
                {
                    h.hangNextRead = false;
                    return new TaskCompletionSource<StoredState?>().Task;
                }
                if (h.failReads || h.reads == h.failReadAt) return Task.FromException<StoredState?>(new IOException("EBUSY: the disk said no"));
                return Task.FromResult(h.stored);
            }
        }

        public Task WriteAsync(StoredState state)
        {
            lock (h.gate)
            {
                h.writes += 1;
                if (h.failWrites) throw new DiskSaidNoException();
                if (h.holdNextWrite)
                {
                    h.holdNextWrite = false;
                    var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    h.releaseHeld = () =>
                    {
                        lock (h.gate) h.stored = state;
                        landed.TrySetResult();
                    };
                    return landed.Task;
                }
                h.stored = state;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class MemoryRegistry(Harness h) : IRegistryStash
    {
        public Task<long?> ReadTrialStartAsync() => Task.FromResult(h.Registry);

        public Task WriteTrialStartAsync(long ms)
        {
            h.Registry = ms;
            return Task.CompletedTask;
        }
    }

    private sealed class ClockAt(Harness h) : IClock
    {
        public long Now() => h.ClockNow;
    }
}

/// <summary>A fingerprinter with only <c>GetAsync</c>: says nothing of what it hashes.</summary>
internal sealed class FixedFingerprinter(string fingerprint) : IFingerprinter
{
    public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(fingerprint);
}

/// <summary>A fingerprinter built from functions, for the tests that flip, fail or hang it.</summary>
internal sealed class FuncFingerprinter(Func<Task<string>> get, Func<Task<DeviceIdentity?>>? identify = null) : IFingerprinter
{
    public int Calls;

    public Task<string> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return get();
    }

    public Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        identify is null ? Task.FromResult<DeviceIdentity?>(null) : identify();
}

/// <summary>A scripted server: a handler per action, every call recorded, or no network at all.</summary>
internal sealed class FakeHttp : IHttpAdapter
{
    private readonly object gate = new();
    private readonly Dictionary<string, Func<JsonObject, Task<HttpResult>>> handlers = new();
    private readonly List<(string Action, JsonObject Body)> calls = new();

    /// <summary>Every call throws, as with no network.</summary>
    public bool Fail { get => Volatile.Read(ref fail); set => Volatile.Write(ref fail, value); }

    private bool fail;

    /// <summary>Never answer: a network that takes the connection and says nothing.</summary>
    public bool Hang { get => Volatile.Read(ref hang); set => Volatile.Write(ref hang, value); }

    private bool hang;

    public FakeHttp On(string action, Func<JsonObject, HttpResult> handler) => On(action, body => Task.FromResult(handler(body)));

    public FakeHttp On(string action, Func<JsonObject, Task<HttpResult>> handler)
    {
        lock (gate) handlers[action] = handler;
        return this;
    }

    /// <summary>Answer <paramref name="action"/> with <paramref name="status"/> and a body.</summary>
    public FakeHttp Answer(string action, int status, JsonObject json) => On(action, _ => new HttpResult(status, json));

    /// <summary>Answer <paramref name="action"/> with a lease (and an activation id).</summary>
    public FakeHttp Lease(string action, string lease, string activationId = "act_1") =>
        Answer(action, 200, new JsonObject { ["lease"] = lease, ["activationId"] = activationId });

    /// <summary>Refuse <paramref name="action"/> with an error word.</summary>
    public FakeHttp Error(string action, int status, string error) => Answer(action, status, new JsonObject { ["error"] = error });

    public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var action = url.Split('/')[^1];
        Func<JsonObject, Task<HttpResult>>? handler;
        lock (gate)
        {
            calls.Add((action, JsonNode.Parse(body.ToJsonString())!.AsObject()));
            handlers.TryGetValue(action, out handler);
        }
        if (Fail) throw new HttpRequestException("network down");
        if (Hang) return new TaskCompletionSource<HttpResult>().Task;
        return handler is null ? Task.FromResult(new HttpResult(404, new JsonObject { ["error"] = "unknown" })) : handler(body);
    }

    public IReadOnlyList<(string Action, JsonObject Body)> Calls
    {
        get
        {
            lock (gate) return calls.ToList();
        }
    }

    public bool Called(string action) => Calls.Any(c => c.Action == action);

    public int Count(string action) => Calls.Count(c => c.Action == action);

    /// <summary>The body of the first <paramref name="action"/> call, failing the test when none was made.</summary>
    public JsonObject BodyOf(string action) =>
        Calls.FirstOrDefault(c => c.Action == action).Body ?? throw new Xunit.Sdk.XunitException($"expected a {action} call");

    /// <summary>The body of the last <paramref name="action"/> call.</summary>
    public JsonObject LastBodyOf(string action) =>
        Calls.LastOrDefault(c => c.Action == action).Body ?? throw new Xunit.Sdk.XunitException($"expected a {action} call");
}

/// <summary>Waiting on work that runs on other threads.</summary>
internal static class Eventually
{
    /// <summary>Wait (real time) until <paramref name="condition"/> holds, failing after a few seconds.</summary>
    public static async Task Until(Func<bool> condition, string what = "the condition", int timeoutMs = 10_000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs) throw new Xunit.Sdk.XunitException($"timed out waiting for {what}");
            await Task.Delay(2);
        }
    }

    /// <summary>A short real pause, for work that should NOT happen to have the chance to.</summary>
    public static Task Quiet() => Task.Delay(60);

    /// <summary>
    /// Runs <paramref name="work"/> on manual time, letting time pass in steps until it settles.
    /// </summary>
    public static async Task<T> Settle<T>(Task<T> work, ManualTime time, int stepMs = 500, int steps = 60)
    {
        for (var step = 0; step < steps && !work.IsCompleted; step++)
        {
            await Task.WhenAny(work, Task.Delay(20));
            if (!work.IsCompleted) time.Advance(TimeSpan.FromMilliseconds(stepMs));
        }
        return await work.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
