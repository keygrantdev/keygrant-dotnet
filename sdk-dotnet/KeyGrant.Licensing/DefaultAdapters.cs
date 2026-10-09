using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using Microsoft.Win32;

namespace KeyGrant.Licensing;

/// <summary>
/// HTTP over <see cref="HttpClient"/>, every call bounded by its timeout: a network that accepts a
/// connection and never answers (a captive portal, a dead proxy) cannot hold a check-in. Abandoned,
/// the call throws, which the SDK treats as no network. Never throws for an HTTP status.
/// </summary>
public sealed class HttpClientAdapter : IHttpAdapter
{
    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new SocketsHttpHandler
    {
        // DNS changes are picked up by long-running apps.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    private readonly HttpClient client;

    /// <summary>An adapter over a shared client of its own.</summary>
    public HttpClientAdapter()
        : this(null)
    {
    }

    /// <summary>An adapter over <paramref name="client"/> (a proxy, a handler of the app's own); null for the shared one.</summary>
    /// <param name="client">The client, or null. Its own <see cref="HttpClient.Timeout"/> still applies.</param>
    public HttpClientAdapter(HttpClient? client) => this.client = client ?? Shared.Value;

    /// <inheritdoc />
    public async Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        using var content = new StringContent(Json.Text(body), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(url, content, bound.Token).ConfigureAwait(false);
        JsonNode? json = null;
        try
        {
            // As `res.json()` reads it: UTF-8 whatever charset the response names (one .NET does not know
            // must not cost the answer), a leading byte-order mark dropped, a malformed sequence replaced;
            // and a repeated name keeps its last value, nothing to throw later.
            var bytes = await response.Content.ReadAsByteArrayAsync(bound.Token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(bytes.AsSpan(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0));
            if (!string.IsNullOrWhiteSpace(text)) json = Json.ParseBody(text);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A body that is not JSON (a proxy's page, say) or cannot be read: as `res.json()` failing,
            // no body. The status still says what it says.
        }
        return new HttpResult((int)response.StatusCode, json);
    }
}

/// <summary>The system clock.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The one instance.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>A trial-start stash that keeps nothing: the default.</summary>
public sealed class NoopRegistryStash : IRegistryStash
{
    /// <summary>The one instance.</summary>
    public static readonly NoopRegistryStash Instance = new();

    /// <inheritdoc />
    public Task<long?> ReadTrialStartAsync() => Task.FromResult<long?>(null);

    /// <inheritdoc />
    public Task WriteTrialStartAsync(long ms) => Task.CompletedTask;
}

/// <summary>
/// The trial start a stash holds, in the one format every KeyGrant SDK keeps it in, so a trial one SDK
/// stashed is seen by another for the same product: milliseconds since the epoch, written as a
/// <c>REG_QWORD</c>; read from a <c>REG_QWORD</c>, or a <c>REG_SZ</c> of decimal digits (what a script or a
/// hand writes). Anything else is no evidence: another type (a <c>REG_DWORD</c> read as a time is 1970), 0
/// or less, or 2^53 or more (past what every SDK holds exactly).
/// </summary>
internal static class TrialStash
{
    /// <summary>The first value that is no start: 2^53 ms, past what a JavaScript number holds exactly.</summary>
    public const long PastEvidence = 1L << 53;

    /// <summary>A <c>REG_QWORD</c>'s value as a start, when it is one.</summary>
    public static long? FromQword(long value) => value is > 0 and < PastEvidence ? value : null;

    /// <summary>
    /// A <c>REG_SZ</c>'s text as a start, when it is decimal digits and nothing else (its terminating
    /// NULs dropped): no sign, no white space, no other numerals.
    /// </summary>
    public static long? FromDigits(string text)
    {
        var digits = text.TrimEnd('\0');
        if (digits.Length == 0) return null;
        long value = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return null;
            value = (value * 10) + (c - '0');
            // Stops before any overflow: past 2^53 it is no start whatever follows.
            if (value >= PastEvidence) return null;
        }
        return FromQword(value);
    }

    /// <summary>Whether <paramref name="ms"/> is a start a reader takes: nothing else is written.</summary>
    public static bool Stashable(long ms) => FromQword(ms) is not null;
}

/// <summary>
/// Opt-in: keeps the earliest trial start in the current user's registry
/// (<c>HKCU\Software\KeyGrant\&lt;product&gt;</c>, value <c>TrialStart</c>, milliseconds as a
/// <c>REG_QWORD</c>; a <c>REG_SZ</c> of decimal digits is read too), which an uninstall leaves behind, so
/// reinstalling the app does not reset its trial. It is the format every KeyGrant SDK keeps, so a
/// trial another SDK stashed for the product counts. Never throws: a registry that cannot be read,
/// or a value that cannot be a start, is no evidence, and one that cannot be written loses nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryStash : IRegistryStash
{
    private const string ValueName = "TrialStart";

    /// <summary>The hive the key is in; null for the current user's, opened when it is used.</summary>
    private readonly RegistryKey? hive;

    /// <summary>A stash for <paramref name="product"/>, under <c>HKCU\Software\KeyGrant</c>.</summary>
    /// <param name="product">The product slug.</param>
    public WindowsRegistryStash(string product)
        : this(null, ProductSubKey(product))
    {
    }

    /// <summary>
    /// A stash under <paramref name="subKey"/> of <paramref name="hive"/> (null: <c>HKCU</c>): tests
    /// keep theirs under a key of their own, apart from every product's.
    /// </summary>
    internal WindowsRegistryStash(RegistryKey? hive, string subKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKey);
        this.hive = hive;
        SubKey = subKey;
    }

    /// <summary>The key the trial start is kept under.</summary>
    internal string SubKey { get; }

    /// <summary>A product's stash key: <c>Software\KeyGrant\&lt;product&gt;</c>.</summary>
    private static string ProductSubKey(string product)
    {
        ArgumentException.ThrowIfNullOrEmpty(product);
        return $@"Software\KeyGrant\{product}";
    }

    private RegistryKey Hive => hive ?? Registry.CurrentUser;

    /// <inheritdoc />
    public Task<long?> ReadTrialStartAsync()
    {
        try
        {
            using var key = Hive.OpenSubKey(SubKey);
            if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) return Task.FromResult<long?>(null);
            // What cannot be a time is no evidence, never a guess: the server would take it as a trial
            // that started in 1970, or refuse the trial outright.
            long? start = (key.GetValueKind(ValueName), value) switch
            {
                (RegistryValueKind.QWord, long ms) => TrialStash.FromQword(ms),
                (RegistryValueKind.String, string text) => TrialStash.FromDigits(text),
                _ => null,
            };
            return Task.FromResult(start);
        }
        catch (Exception)
        {
            return Task.FromResult<long?>(null);
        }
    }

    /// <inheritdoc />
    public Task WriteTrialStartAsync(long ms)
    {
        // Only what every SDK reads back as a start.
        if (!TrialStash.Stashable(ms)) return Task.CompletedTask;
        try
        {
            using var key = Hive.CreateSubKey(SubKey, writable: true);
            key.SetValue(ValueName, ms, RegistryValueKind.QWord);
        }
        catch (Exception)
        {
            // Best-effort: the server's trial and the state file still stand.
        }
        return Task.CompletedTask;
    }
}
