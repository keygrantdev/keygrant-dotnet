using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>The default adapters, and answers and files no SDK should ever throw on.</summary>
public class AdapterTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await answer(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact]
    public async Task Posts_the_body_as_JSON_and_returns_the_status_and_parsed_body_without_throwing_for_a_4xx()
    {
        var handler = new Handler((_, _) => Task.FromResult(Answer(HttpStatusCode.Forbidden, "{\"error\":\"revoked\"}")));
        var http = new HttpClientAdapter(new HttpClient(handler));
        var result = await http.PostAsync("https://api.test/v1/products/sluice/validate", new JsonObject { ["key"] = "K", ["major"] = 1 }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(403, result.Status);
        Assert.Equal("revoked", result.Json!["error"]!.GetValue<string>());
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        AssertJson.Equal(new JsonObject { ["key"] = "K", ["major"] = 1 }, JsonNode.Parse(body));
    }

    [Theory]
    [InlineData("<html>proxy says no</html>")]
    [InlineData("")]
    public async Task Yields_no_body_when_it_is_not_JSON(string text)
    {
        var http = new HttpClientAdapter(new HttpClient(new Handler((_, _) => Task.FromResult(Answer(HttpStatusCode.BadGateway, text, "text/html")))));
        var result = await http.PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal((502, (JsonNode?)null), (result.Status, result.Json));
    }

    /// <summary>A handler answering 200 with <paramref name="bytes"/> as they are, under <paramref name="type"/>.</summary>
    private static HttpClientAdapter Answering(byte[] bytes, string type) => new(new HttpClient(new Handler((_, _) =>
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", type);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    })));

    [Theory]
    [InlineData("application/json; charset=bogus")]
    [InlineData("application/json; charset=utf8")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("text/plain")]
    public async Task Reads_the_body_as_UTF_8_whatever_charset_the_response_names(string type)
    {
        // As fetch's `res.json()` reads it: always UTF-8.
        var activationId = "act_" + (char)0xE9;
        var bytes = Encoding.UTF8.GetBytes($"{{\"lease\":\"L\",\"activationId\":\"{activationId}\"}}");
        var result = await Answering(bytes, type).PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(("L", activationId), Internal.Wire.ParseLeaseBody(result.Json));
    }

    [Fact]
    public async Task Drops_a_byte_order_mark_and_replaces_a_malformed_sequence_as_fetch_does()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"note\":\"a"), 0xFF, .. Encoding.UTF8.GetBytes("\",\"lease\":\"L\"}")];
        var result = await Answering(bytes, "application/json; charset=bogus").PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("L", result.Json!["lease"]!.GetValue<string>());
        Assert.Equal("a" + (char)0xFFFD, result.Json["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task Abandons_a_call_that_outlives_its_bound_and_one_its_caller_cancels()
    {
        var hanging = new HttpClientAdapter(new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer(HttpStatusCode.OK, "{}");
        })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task The_system_clock_reads_milliseconds_and_the_default_stash_keeps_nothing()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var now = SystemClock.Instance.Now();
        Assert.InRange(now, before, before + 60_000);
        await NoopRegistryStash.Instance.WriteTrialStartAsync(123);
        Assert.Null(await NoopRegistryStash.Instance.ReadTrialStartAsync());
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task The_Windows_registry_stash_keeps_the_trial_start_for_the_current_user()
    {
        // Under a key of the test's own, removed whole after it: never under Software\KeyGrant, so the
        // machine is left exactly as it was found.
        var user = Microsoft.Win32.Registry.CurrentUser;
        var scratch = $@"Software\KeyGrant-test-{Guid.NewGuid():N}";
        try
        {
            var stash = new WindowsRegistryStash(user, scratch);
            Assert.Null(await stash.ReadTrialStartAsync());
            await stash.WriteTrialStartAsync(1_757_000_000_000);
            Assert.Equal(1_757_000_000_000, await new WindowsRegistryStash(user, scratch).ReadTrialStartAsync());
            await stash.WriteTrialStartAsync(1_756_000_000_000);
            Assert.Equal(1_756_000_000_000, await stash.ReadTrialStartAsync());
            using var key = user.OpenSubKey(scratch, writable: true)!;
            Assert.Equal(Microsoft.Win32.RegistryValueKind.QWord, key.GetValueKind("TrialStart"));
            // A start no SDK reads back is never written.
            await stash.WriteTrialStartAsync(TrialStash.PastEvidence);
            await stash.WriteTrialStartAsync(0);
            Assert.Equal(1_756_000_000_000, await stash.ReadTrialStartAsync());

            // A decimal REG_SZ, as a script or a hand writes it, reads; what cannot be a start is no
            // evidence, never a guess.
            async Task<long?> Holding(object value, Microsoft.Win32.RegistryValueKind kind)
            {
                key.SetValue("TrialStart", value, kind);
                return await stash.ReadTrialStartAsync();
            }
            var str = Microsoft.Win32.RegistryValueKind.String;
            var qword = Microsoft.Win32.RegistryValueKind.QWord;
            Assert.Equal(1_755_000_000_000, await Holding("1755000000000", str));
            Assert.Equal(TrialStash.PastEvidence - 1, await Holding(TrialStash.PastEvidence - 1, qword));
            Assert.Null(await Holding(TrialStash.PastEvidence, qword));
            Assert.Null(await Holding("9007199254740992", str));
            Assert.Null(await Holding(-1L, qword));
            Assert.Null(await Holding("0", str));
            Assert.Null(await Holding("not a time", str));
            Assert.Null(await Holding(" 1755000000000", str));
            Assert.Null(await Holding(42, Microsoft.Win32.RegistryValueKind.DWord));
            Assert.Null(await Holding("1755000000000", Microsoft.Win32.RegistryValueKind.ExpandString));
            Assert.Null(await Holding(new[] { "1755000000000" }, Microsoft.Win32.RegistryValueKind.MultiString));
            Assert.Null(await Holding(BitConverter.GetBytes(1_755_000_000_000L), Microsoft.Win32.RegistryValueKind.Binary));
        }
        finally
        {
            user.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        }
        using var gone = user.OpenSubKey(scratch);
        Assert.Null(gone);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Keeps_each_product_s_trial_start_under_its_own_KeyGrant_key()
    {
        // Computed only: nothing is read or written.
        Assert.Equal(@"Software\KeyGrant\sluice", new WindowsRegistryStash("sluice").SubKey);
        Assert.Equal(@"Software\KeyGrant\maindeck", new WindowsRegistryStash("maindeck").SubKey);
        Assert.Throws<ArgumentException>(() => new WindowsRegistryStash(""));
    }

    [Theory]
    [InlineData("1757000000000", 1_757_000_000_000L)]
    [InlineData("0001757000000000", 1_757_000_000_000L)]
    [InlineData("1", 1L)]
    [InlineData("9007199254740991", 9_007_199_254_740_991L)]
    [InlineData("9007199254740992", null)]
    [InlineData("99999999999999999999999999", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData("+1757000000000", null)]
    [InlineData("-1757000000000", null)]
    [InlineData(" 1757000000000", null)]
    [InlineData("1757000000000 ", null)]
    [InlineData("1.757e12", null)]
    [InlineData("0x199", null)]
    public void Reads_a_stashed_REG_SZ_as_a_start_only_when_it_is_decimal_digits_below_two_to_the_53(string text, long? start) =>
        Assert.Equal(start, TrialStash.FromDigits(text));

    [Fact]
    public void Reads_a_stashed_REG_QWORD_as_a_start_only_above_0_and_below_two_to_the_53()
    {
        Assert.Equal(1_757_000_000_000, TrialStash.FromQword(1_757_000_000_000));
        Assert.Equal(TrialStash.PastEvidence - 1, TrialStash.FromQword(TrialStash.PastEvidence - 1));
        Assert.Equal(9_007_199_254_740_992L, TrialStash.PastEvidence);
        foreach (var no in new[] { 0L, -1L, long.MinValue, TrialStash.PastEvidence, long.MaxValue }) Assert.Null(TrialStash.FromQword(no));
        // A REG_SZ's terminating NULs are not part of it.
        Assert.Equal(1_757_000_000_000, TrialStash.FromDigits("1757000000000" + (char)0 + (char)0));
        Assert.Null(TrialStash.FromDigits(((char)0).ToString()));
        // Other numerals than ASCII digits are no start either.
        Assert.Null(TrialStash.FromDigits(new string([(char)0x0661, (char)0x0662])));
        Assert.Null(TrialStash.FromDigits(new string([(char)0xFF11, (char)0xFF12])));
        Assert.True(TrialStash.Stashable(1) && !TrialStash.Stashable(0) && !TrialStash.Stashable(TrialStash.PastEvidence));
    }

    // --- nothing on the wire or on the disk throws ------------------------------------

    private static readonly string LoneSurrogateJson = "\"K\\ud800X\"";

    [Fact]
    public async Task Takes_an_error_word_dotnet_cannot_read_as_no_verdict_never_an_exception()
    {
        var h = Create();
        h.Stored = new StoredState { Key = "KEY", ActivationId = "act_1", Lease = h.Sign("KEY", "perpetual", 30 * 24 * 3600), LastSeen = Now };
        h.Http.Answer("validate", 403, JsonNode.Parse($"{{\"error\":{LoneSurrogateJson}}}")!.AsObject());
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = "KEY" }, await h.License.RefreshAsync());
        Assert.Null(h.Stored!.Refused);
    }

    [Fact]
    public void Reads_a_state_field_dotnet_cannot_hold_as_absent_keeping_the_rest_and_writes_it_back()
    {
        var text = $"{{\"key\":{LoneSurrogateJson},\"activationId\":\"act_1\",\"lastSeen\":5}}";
        var state = StoredState.FromJson(text)!;
        Assert.Null(state.Key);
        Assert.Equal(("act_1", 5L), (state.ActivationId, state.LastSeen));
        // Kept as it was written, for the SDK that wrote it.
        Assert.Contains($"\"key\":{LoneSurrogateJson}", state.ToJson());
        Assert.Equal(state, StoredState.FromJson(state.ToJson()));
    }

    [Fact]
    public void Verifies_a_lease_whose_claims_dotnet_cannot_hold_as_bad_claims_never_an_exception()
    {
        var signer = new TestSigner();
        var verifier = new Internal.LeaseVerifier(Internal.Base64Url.Decode(signer.PublicX)!);
        var token = signer.SignRaw($"{{\"iss\":\"keygrant\",\"sub\":{LoneSurrogateJson},\"product\":\"p\",\"aid\":\"a\",\"model\":\"trial\",\"lim\":1,\"iat\":1,\"exp\":9999999999}}");
        Assert.Equal(Internal.VerifyFailure.BadClaims, verifier.Verify(token, Now).Reason);
    }

    [Fact]
    public void Writes_a_lone_surrogate_as_the_replacement_character_rather_than_failing_the_save()
    {
        var text = new StoredState { Key = "K" + (char)0xD800 }.ToJson();
        Assert.Equal("K" + (char)0xFFFD, StoredState.FromJson(text)!.Key);
    }
}
