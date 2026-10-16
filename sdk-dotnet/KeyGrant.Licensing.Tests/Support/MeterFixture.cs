using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>What the metered-uses suites share: a key's lease carrying meters, a harness holding it, and the server's answers to a <c>use</c>.</summary>
internal static class Metered
{
    public const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    public const long Minute = 60_000;
    public const long Hour = 3_600_000;
    public const long Month = 30L * 24 * 3600;

    /// <summary>Long enough ago that the journal's wait for an entry is over.</summary>
    public const long LongAgo = Now - 2 * Hour;

    /// <summary>A meter as a lease signs it.</summary>
    public static JsonObject Meter(long left, long total, long offline, bool resets = false)
    {
        var meter = new JsonObject { ["left"] = left, ["total"] = total, ["offline"] = offline };
        if (resets) meter["resets"] = true;
        return meter;
    }

    /// <summary>A key with 100 exports, 90 of them left, a device allowance of 10, and 5 renders a billing period.</summary>
    public static JsonObject TwoMeters() => new() { ["exports"] = Meter(90, 100, 10), ["renders"] = Meter(5, 5, 0, resets: true) };

    /// <summary>Only the exports of <see cref="TwoMeters"/>, with these figures.</summary>
    public static JsonObject Exports(long left, long total, long offline) => new() { ["exports"] = Meter(left, total, offline) };

    /// <summary>A licence lease for <see cref="Key"/> on act_1, covering majors 1 to 3, with <paramref name="met"/>.</summary>
    public static string Lease(Harness h, JsonNode? met, long ttlSeconds = Month, long now = Now, string aid = "act_1", string sub = Key) =>
        h.SignLease(new LeaseInput { Sub = sub, Aid = aid, Model = "subscription", Lim = 3, Major = 3, TtlSeconds = ttlSeconds, Met = met }, now);

    /// <summary>A harness whose storage holds <see cref="Key"/> activated on act_1 with a lease carrying <paramref name="met"/>, and what <paramref name="more"/> adds to the state.</summary>
    public static Harness Holding(JsonNode? met, Func<StoredState, StoredState>? more = null, Options? options = null)
    {
        var h = Create(null, options);
        var state = new StoredState { Key = Key, ActivationId = "act_1", Lease = Lease(h, met) };
        h.Stored = more is null ? state : more(state);
        return h;
    }

    /// <summary>A harness holding <see cref="TwoMeters"/>.</summary>
    public static Harness Holding(Func<StoredState, StoredState>? more = null, Options? options = null) => Holding(TwoMeters(), more, options);

    /// <summary>The server's answer to a spend that landed: what is left of <c>exports</c> now, at <paramref name="at"/>.</summary>
    public static HttpResult Spent(long left, long at = Now) =>
        new(200, new JsonObject { ["spent"] = true, ["meters"] = Exports(left, 100, 10), ["at"] = at });

    /// <summary>An answer.</summary>
    public static HttpResult Reply(int status, JsonNode? json) => new(status, json);

    /// <summary>An error word on a status.</summary>
    public static HttpResult Word(int status, string error, JsonObject? more = null)
    {
        var body = new JsonObject { ["error"] = error };
        if (more is not null) foreach (var (name, value) in more.ToList()) body[name] = value?.DeepClone();
        return new HttpResult(status, body);
    }

    /// <summary>The journal's entry for a spend, kind and all.</summary>
    public static SpendEntry Entry(string id = "AAAAAAAAAAAAAAAAAAAAAA", string meter = "exports", long n = 1, long at = Now, SpendKind kind = SpendKind.Offline) =>
        new(id, meter, n, at, kind);

    /// <summary>An id of 22 characters made of <paramref name="c"/>.</summary>
    public static string Id(char c) => new(c, 22);

    /// <summary>The body of the <paramref name="nth"/> (from 0) request to <paramref name="action"/>.</summary>
    public static JsonObject BodyOf(Harness h, string action, int nth = 0) =>
        h.Http.Calls.Where(c => c.Action == action).ElementAtOrDefault(nth).Body ?? throw new Xunit.Sdk.XunitException($"expected {action} request {nth}");

    public static JsonObject UseBody(Harness h, int nth = 0) => BodyOf(h, "use", nth);

    /// <summary>The <c>spends</c> a request carried, or null for none.</summary>
    public static JsonArray? ReportOf(Harness h, string action, int nth = 0) => BodyOf(h, action, nth)["spends"]?.AsArray();

    /// <summary>The journal as stored now.</summary>
    public static IReadOnlyList<SpendEntry> Journal(Harness h) => h.Stored?.Spends ?? [];

    /// <summary>The journal as (kind, n) pairs, in order.</summary>
    public static (SpendKind Kind, long N)[] KindsAndCounts(Harness h) => Journal(h).Select(e => (e.Kind, e.N)).ToArray();

    /// <summary>
    /// A <see cref="License"/> over the harness's server, clock and state, with any adapter given in place of the harness's: another
    /// process of the app (or the same app with a storage that races, a clock that throws, a request that throws).
    /// </summary>
    public static License AppOver(
        Harness h,
        IStorageAdapter? storage = null,
        IFingerprinter? fingerprint = null,
        IHttpAdapter? http = null,
        IClock? clock = null,
        TimeSpan? httpTimeout = null,
        int major = 1) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test/",
        PublicJwk = h.Signer.Jwk,
        HttpTimeout = httpTimeout ?? LicenseConfig.DefaultHttpTimeout,
        Major = major,
        TimeProvider = h.Time,
        Adapters = new LicenseAdapters
        {
            Storage = storage ?? h.Adapters.Storage,
            Registry = h.Adapters.Registry,
            Clock = clock ?? h.Adapters.Clock,
            Fingerprint = fingerprint ?? h.Adapters.Fingerprint,
            Http = http ?? h.Adapters.Http,
        },
    });
}

/// <summary>
/// A storage over another whose every save first finds the file changed under it, as a second process of the app would
/// leave it: the first save finds <c>theirs</c> there (and the revision moved), merges onto it, and lands; later ones merge
/// onto what is stored.
/// </summary>
internal sealed class RacingStorage(IStorageAdapter inner, Func<StoredState, StoredState> theirs) : IStorageAdapter
{
    private int saves;

    public Task<StoredState?> ReadAsync(bool readOnly) => inner.ReadAsync(readOnly);

    public Task WriteAsync(StoredState state) => inner.WriteAsync(state);

    public async Task<bool> UpdateAsync(Func<StoredState, StoredState> merge)
    {
        var latest = await inner.ReadAsync(false).ConfigureAwait(false) ?? new StoredState();
        var other = Interlocked.Increment(ref saves) == 1 ? theirs(latest) : latest;
        await inner.WriteAsync(merge(other)).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// A storage over another whose saves a test decides: each save is told its number (from 1, since <see cref="Reset"/>) and
/// the state as stored, and may throw (the save fails) or hand back the state it is to merge onto (what another process
/// left meanwhile); reads can be made to fail.
/// </summary>
internal sealed class ScriptedStorage(IStorageAdapter inner) : IStorageAdapter
{
    private int saves;

    /// <summary>Called before each save lands, with its number and the state as stored; the state it returns is what the save merges onto.</summary>
    public Func<int, StoredState, StoredState>? OnSave { get; set; }

    /// <summary>Called after each save landed, with its number.</summary>
    public Action<int>? AfterSave { get; set; }

    /// <summary>Whether every read fails (the disk is gone).</summary>
    public bool FailReads { get; set; }

    /// <summary>What a read answers in place of the stored state (told whether it is read-only), or null to answer it: a state another process put there.</summary>
    public Func<bool, StoredState?>? ReadOverride { get; set; }

    public void Reset() => Interlocked.Exchange(ref saves, 0);

    public Task<StoredState?> ReadAsync(bool readOnly) =>
        FailReads ? throw new IOException("the disk is gone")
        : ReadOverride?.Invoke(readOnly) is { } over ? Task.FromResult<StoredState?>(over)
        : inner.ReadAsync(readOnly);

    public Task WriteAsync(StoredState state) => inner.WriteAsync(state);

    public async Task<bool> UpdateAsync(Func<StoredState, StoredState> merge)
    {
        var number = Interlocked.Increment(ref saves);
        var latest = await inner.ReadAsync(false).ConfigureAwait(false) ?? new StoredState();
        await inner.WriteAsync(merge(OnSave is null ? latest : OnSave(number, latest))).ConfigureAwait(false);
        AfterSave?.Invoke(number);
        return true;
    }
}
