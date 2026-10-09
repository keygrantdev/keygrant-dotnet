using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What the reference reads without a second thought and System.Text.Json does not: a repeated name in
/// a body (JSON.parse keeps the last), a byte-order mark before a payload, names .NET cannot hold, deep
/// nesting; and a process ended by a signal. None of it may throw out of a status, or settle a device.
/// </summary>
public class RobustnessTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";

    /// <summary>An adapter answering with a body as a custom adapter would build it: <c>JsonNode.Parse</c> of the text.</summary>
    private sealed class TextHttp(int status, string text) : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResult(status, JsonNode.Parse(text)));
    }

    private static License Over(Harness h, IHttpAdapter http) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test",
        PublicJwk = h.Signer.Jwk,
        Adapters = new LicenseAdapters { Storage = h.Adapters.Storage, Clock = h.Adapters.Clock, Fingerprint = h.Adapters.Fingerprint, Http = http },
    });

    [Fact]
    public async Task A_refusal_body_with_a_repeated_name_is_read_as_JSON_parse_reads_it_never_thrown()
    {
        // A lease in its last week, renewed early; a proxy answers with a name twice.
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "subscription", 7 * 24 * 3600), CheckedMajor = 1, LastSeen = Now };
        h.SetClock(Now + 5 * Day);
        var license = Over(h, new TextHttp(403, "{\"error\":\"forbidden\",\"error\":\"policy\"}"));
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, await license.StatusAsync());
        Assert.Equal((1, Now + 5 * Day, Offline.AskAgainMs), (h.Stored!.CheckedMajor, h.Stored.BackoffSince, h.Stored.BackoffMs));
        Assert.Null(h.Stored.Refused);

        // The last of the repeated name is the one that counts, as in JSON.parse.
        var revoked = Over(h, new TextHttp(403, "{\"error\":\"policy\",\"error\":\"revoked\"}"));
        Assert.Equal(LicenseReason.Revoked, (await revoked.RefreshAsync()).Reason);
    }

    [Fact]
    public async Task An_activation_and_a_trial_answered_with_a_repeated_name_take_its_last_value()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        var activate = Over(h, new TextHttp(200, $"{{\"lease\":\"x\",\"activationId\":\"act_1\",\"lease\":\"{lease}\"}}"));
        Assert.Equal(ActivateResult.Success, await activate.ActivateAsync(Key));
        Assert.Equal(lease, h.Stored!.Lease);

        var trial = Create();
        var claim = Hashes.FingerprintClaim("fp-test");
        var trialLease = trial.SignLease(new LeaseInput { Sub = $"trial:{claim}", Aid = claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = 7 * 24 * 3600 });
        var started = Over(trial, new TextHttp(200, $"{{\"trialStart\":1,\"trialEndsAt\":{Now - 1},\"trialStart\":{Now},\"trialEndsAt\":{Now + 7 * Day},\"lease\":\"{trialLease}\"}}"));
        var status = await started.StartTrialAsync();
        Assert.Equal((LicenseState.Trial, 7), (status.State, status.TrialDaysLeft));
        Assert.Equal((Now, Now + 7 * Day), (trial.Stored!.TrialStart, trial.Stored.TrialEndsAt));
    }

    [Fact]
    public async Task The_default_HTTP_adapter_reads_a_repeated_name_as_JSON_parse_does()
    {
        var handler = new Handler(HttpStatusCode.Forbidden, "{\"error\":\"policy\",\"error\":\"revoked\"}");
        var result = await new HttpClientAdapter(new HttpClient(handler)).PostAsync("https://api.test/x", new JsonObject(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("revoked", Wire.ErrorOf(result.Json));
    }

    private sealed class Handler(HttpStatusCode status, string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void Reads_nothing_rather_than_throwing_from_any_node_an_adapter_hands_over()
    {
        var repeated = JsonNode.Parse("{\"lease\":\"a\",\"lease\":\"b\",\"trialStart\":1,\"trialStart\":2,\"trialEndsAt\":3}");
        Assert.Equal(("b", (string?)null), Wire.ParseLeaseBody(Json.Lenient(repeated)));
        Assert.Equal(new TrialBody(2, 3, "b"), Wire.ParseTrialBody(Json.Lenient(repeated)));
        // And straight from the node, without the engine's re-read: nothing, never an exception.
        Assert.Equal(((string?)null, (string?)null), Wire.ParseLeaseBody(JsonNode.Parse("{\"lease\":\"a\",\"lease\":\"b\"}")));
        Assert.Null(Wire.ErrorOf(JsonNode.Parse("{\"error\":\"a\",\"error\":\"b\"}")));
    }

    // --- a lone surrogate nested in an answer costs nothing else in it ---------------------

    private const string NestedLoneSurrogate = "\"meta\":{\"note\":\"\\ud800\"}";

    [Fact]
    public void Reads_every_member_beside_one_dotnet_cannot_write()
    {
        var body = Json.Lenient(JsonNode.Parse($"{{\"lease\":\"L\",\"activationId\":\"act_1\",{NestedLoneSurrogate},\"x\":[\"\\udc00\"],\"n\":[1,{{\"m\":2}}]}}"));
        Assert.Equal(("L", "act_1"), Wire.ParseLeaseBody(body));
        Assert.Equal("[1,{\"m\":2}]", body!["n"]!.ToJsonString());
        Assert.False(body.AsObject().ContainsKey("meta"));
    }

    [Fact]
    public async Task Licenses_from_a_renewal_whose_answer_holds_a_nested_lone_surrogate()
    {
        // As a custom adapter hands it over (JsonNode.Parse), and as the default one reads it.
        var custom = Create();
        custom.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = custom.Sign(Key, "perpetual", 3600, Now - 2 * Day), CheckedMajor = 1, LastSeen = Now };
        var fresh = custom.Sign(Key, "perpetual", 30 * 24 * 3600);
        var answer = $"{{\"lease\":\"{fresh}\",\"activationId\":\"act_1\",{NestedLoneSurrogate}}}";
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, await Over(custom, new TextHttp(200, answer)).StatusAsync());
        Assert.Equal(fresh, custom.Stored!.Lease);

        var byDefault = Create();
        byDefault.Stored = custom.Stored with { Lease = byDefault.Sign(Key, "perpetual", 3600, Now - 2 * Day) };
        var renewed = byDefault.Sign(Key, "perpetual", 30 * 24 * 3600);
        var http = new HttpClientAdapter(new HttpClient(new Handler(HttpStatusCode.OK, $"{{\"lease\":\"{renewed}\",\"activationId\":\"act_1\",{NestedLoneSurrogate}}}")));
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, await Over(byDefault, http).StatusAsync());
        Assert.Equal(renewed, byDefault.Stored!.Lease);
    }

    [Fact]
    public async Task Activates_from_an_answer_that_holds_a_nested_lone_surrogate()
    {
        var h = Create();
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        var license = Over(h, new TextHttp(200, $"{{\"lease\":\"{lease}\",\"activationId\":\"act_1\",\"x\":[\"\\udc00\"]}}"));
        Assert.Equal(ActivateResult.Success, await license.ActivateAsync(Key));
        Assert.Equal(lease, h.Stored!.Lease);
    }

    // --- leases --------------------------------------------------------------------------

    private static readonly TestSigner Signer = new();
    private static readonly LeaseVerifier Verifier = new(Base64Url.Decode(Signer.PublicX)!);
    private const long At = 1_790_000_000_000;

    private static string Claims(string extra = "") =>
        $"{{\"iss\":\"keygrant\",\"sub\":\"{Key}\",\"product\":\"sluice\",\"aid\":\"act_1\",\"model\":\"perpetual\",\"lim\":3{extra},\"iat\":{At / 1000},\"exp\":{At / 1000 + 86_400}}}";

    private static string Header => Base64Url.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}"));

    [Fact]
    public void Takes_a_payload_with_a_byte_order_mark_as_TextDecoder_does()
    {
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Claims())).ToArray();
        Assert.True(Verifier.Verify(Signer.SignInput($"{Header}.{Base64Url.Encode(payload)}"), At).Valid);
    }

    [Fact]
    public void Ignores_unknown_claims_however_nested_or_named()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 300)) + string.Concat(Enumerable.Repeat("]", 300));
        Assert.True(Verifier.Verify(Signer.SignRaw(Claims($",\"deep\":{deep}")), At).Valid);
        Assert.True(Verifier.Verify(Signer.SignRaw(Claims(",\"\\ud800\":1")), At).Valid);
        // An entitlement whose name or text .NET cannot hold is dropped as a malformed one is; the rest stay.
        var ent = Verifier.Verify(Signer.SignRaw(Claims(",\"ent\":{\"note\":\"\\ud800\",\"\\udc00\":true,\"edition\":\"pro\"}")), At);
        Assert.True(ent.Valid);
        Assert.Equal(["edition"], ent.Claims!.Ent.Keys);
    }

    // --- the state file ---------------------------------------------------------------------

    [Fact]
    public void Reads_a_state_file_with_a_name_dotnet_cannot_hold_keeping_the_rest()
    {
        var state = StoredState.FromJson("{\"\\ud800\":1,\"key\":\"K\",\"activationId\":\"act_1\"}");
        Assert.Equal(new StoredState { Key = "K", ActivationId = "act_1" }, state);
    }

    [Fact]
    public async Task Keeps_a_deeply_nested_unknown_field_and_writes_it_back()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 500)) + string.Concat(Enumerable.Repeat("]", 500));
        var fs = new MemoryFileSystem(new() { [Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json")] = $"{{\"key\":\"K\",\"deep\":{deep}}}" });
        var storage = new FileStorage(Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json"), null, fs, _ => Task.CompletedTask, null);
        Assert.Equal("K", (await storage.ReadAsync(false))!.Key);
        Assert.True(await storage.UpdateAsync(s => s with { LastSeen = 5 }));
        var written = fs.Files[Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json")];
        Assert.Contains(deep, written);
        Assert.DoesNotContain(fs.Files.Keys, name => name.EndsWith(".corrupt", StringComparison.Ordinal));
    }

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth));

    /// <summary>
    /// Deeper than 2^20 levels. Read once over, it takes milliseconds; held as a JsonDocument, it took
    /// time growing with the square of its depth (seconds at 100 000 levels, a quarter of an hour here),
    /// and a reader or writer that recursed would end the process rather than fail a test.
    /// </summary>
    private static readonly string Deep = Nested(1_100_000);

    /// <summary>Fails the test, rather than hang the suite, when <paramref name="work"/> is not done in a minute.</summary>
    private static Task InOnePass(Func<Task> work) => Task.Run(work).WaitAsync(TimeSpan.FromMinutes(1));

    [Fact]
    public Task Keeps_a_state_field_nested_deeper_than_any_bound_and_writes_it_back_in_one_pass() => InOnePass(async () =>
    {
        // A field this SDK does not know, and a known one holding what it cannot type, both that deep.
        var path = Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json");
        var fs = new MemoryFileSystem(new() { [path] = $"{{\"key\":\"K\",\"deep\":{Deep},\"refused\":{Deep}}}" });
        var storage = new FileStorage(path, null, fs, _ => Task.CompletedTask, null);
        var read = (await storage.ReadAsync(false))!;
        Assert.Equal(("K", (Refusal?)null, (IReadOnlyDictionary<string, System.Text.Json.JsonElement>?)null), (read.Key, read.Refused, read.AdditionalFields));
        Assert.True(await storage.UpdateAsync(s => s with { LastSeen = 5 }));
        Assert.Contains($"\"deep\":{Deep}", fs.Files[path]);
        Assert.Contains($"\"refused\":{Deep}", fs.Files[path]);
        Assert.DoesNotContain(fs.Files.Keys, name => name.EndsWith(".corrupt", StringComparison.Ordinal));
        // A save that sets the known field replaces what it held; the unknown one stays.
        Assert.True(await storage.UpdateAsync(s => StateFields.Set(s, StateField.Refused, Refusal.Revoked)));
        Assert.Contains("\"refused\":\"revoked\"", fs.Files[path]);
        Assert.DoesNotContain($"\"refused\":{Deep}", fs.Files[path]);
        Assert.Contains($"\"deep\":{Deep}", fs.Files[path]);
        Assert.Equal(StoredState.FromJson(fs.Files[path]), StoredState.FromJson(StoredState.FromJson(fs.Files[path])!.ToJson()));
    });

    [Fact]
    public void Drops_what_a_deep_field_held_when_a_save_clears_it()
    {
        // Cleared, the field is gone: what the file held for it is not written back in its place.
        var held = StoredState.FromJson($"{{\"key\":\"K\",\"refused\":{Nested(70)},\"deep\":{Nested(70)}}}")!;
        Assert.Contains("\"refused\":[[", held.ToJson());
        var cleared = StateFields.Set(held, StateField.Refused, null);
        Assert.DoesNotContain("\"refused\"", cleared.ToJson());
        Assert.Contains($"\"deep\":{Nested(70)}", cleared.ToJson());
        Assert.NotEqual(held, cleared);
        Assert.Equal(StoredState.FromJson($"{{\"key\":\"K\",\"deep\":{Nested(70)}}}"), cleared);
    }

    [Fact]
    public Task Reads_a_lease_and_an_answer_with_a_member_nested_deeper_than_any_bound_in_one_pass() => InOnePass(async () =>
    {
        // An unknown claim is never read; `ent` is read leniently however it nests (a list is none, a
        // member that is not a flag, a number or a text dropped unread); any other claim the schema checks
        // cannot nest, and fails as it fails there.
        Assert.True(Verifier.Verify(Signer.SignRaw(Claims($",\"deep\":{Deep}")), At).Valid);
        var list = Verifier.Verify(Signer.SignRaw(Claims($",\"ent\":{Deep}")), At);
        Assert.True(list.Valid);
        Assert.Empty(list.Claims!.Ent);
        var member = Verifier.Verify(Signer.SignRaw(Claims($",\"ent\":{{\"deep\":{Deep},\"edition\":\"pro\"}}")), At);
        Assert.True(member.Valid);
        Assert.Equal(["edition"], member.Claims!.Ent.Keys);
        Assert.Equal(VerifyFailure.BadClaims, Verifier.Verify(Signer.SignRaw(Claims($",\"model\":{Deep}")), At).Reason);

        // An answer's member that deep is left out; the rest of the answer is read.
        var body = Json.ParseBody($"{{\"deep\":{Deep},\"error\":\"revoked\"}}");
        Assert.Equal("revoked", Wire.ErrorOf(body));
        Assert.False(body!.AsObject().ContainsKey("deep"));
        Assert.Null(Json.ParseBody(Deep));

        // Through the default HTTP adapter and the engine: the lease it carries licenses.
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", 3600, Now - 2 * Day), CheckedMajor = 1, LastSeen = Now };
        var lease = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        var http = new HttpClientAdapter(new HttpClient(new Handler(HttpStatusCode.OK, $"{{\"deep\":{Deep},\"lease\":\"{lease}\",\"activationId\":\"act_1\"}}")));
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, await Over(h, http).StatusAsync());
        Assert.Equal(lease, h.Stored!.Lease);
    });

    [Fact]
    public void Keeps_each_top_level_member_s_exact_text_and_how_deep_it_nests()
    {
        var top = TopLevelJson.Read(" {\"a\" : \"x\\\"y\\u00e9\", \"b\":12.5e3,\"c\":[1,{\"d\":[]}],\"\\ud800\":1,\"e\":true,\"f\":null,\"g\":{},\"a\":\"last\"} ");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, top.Kind);
        Assert.Equal(
            [("a", "\"last\"", 0), ("b", "12.5e3", 0), ("c", "[1,{\"d\":[]}]", 3), ("e", "true", 0), ("f", "null", 0), ("g", "{}", 1)],
            top.Members.Select(m => (m.Key, m.Value.Text, m.Value.Depth)));
        Assert.Equal((4, true), (top.Value.Depth, top.Value.Holdable));
        // A string's text is as written, escapes and all.
        Assert.Equal("\"x\\\"y\\u00e9\"", TopLevelJson.Read("[\"x\\\"y\\u00e9\"]").Value.Text[1..^1]);
        Assert.Equal((System.Text.Json.JsonValueKind.Number, "-0.5", 0), (TopLevelJson.Read("-0.5").Kind, TopLevelJson.Read("-0.5").Value.Text, TopLevelJson.Read("-0.5").Value.Depth));
        Assert.Equal((65, false), (TopLevelJson.Read(Nested(65)).Value.Depth, TopLevelJson.Read(Nested(65)).Value.Holdable));
        Assert.True(TopLevelJson.Read(Nested(64)).Value.Holdable);
        Assert.Equal("[]", TopLevelJson.Read(Nested(64)).Value.Element().GetRawText()[63..^63]);
    }

    [Fact]
    public void Reads_what_JSON_parse_reads_and_refuses_what_it_refuses_at_the_top_level()
    {
        Assert.Null(StoredState.FromJson("null"));
        Assert.Equal(new StoredState(), StoredState.FromJson("[1,2]"));
        Assert.Equal(new StoredState(), StoredState.FromJson(" \"text\" \n"));
        foreach (var refused in new[] { "", " ", "{", "{\"key\":\"K\"", "{\"key\":\"K\"} {}", "{\"key\":\"K\"}x", "[[]", "{\"key\":}", "{'key':1}", "{\"key\":1,}" })
        {
            Assert.False(StoredStateJson.TryRead(refused, out _), refused);
        }
        // One byte-order mark is forgiven (the reference sets such a file aside); a second is not JSON.
        var bom = (char)0xFEFF;
        Assert.True(StoredStateJson.TryRead($"{bom}{{\"key\":\"K\"}}", out var marked));
        Assert.Equal("K", marked!.Key);
        Assert.False(StoredStateJson.TryRead($"{bom}{bom}{{}}", out _));
        // The last of a repeated name, in the place of its first, as JSON.parse keeps it.
        var state = StoredState.FromJson("{\"x\":1,\"key\":\"A\",\"x\":[2],\"key\":\"B\"}")!;
        Assert.Equal("B", state.Key);
        Assert.Equal("[2]", state.AdditionalFields!["x"].GetRawText());
    }

    // --- the machine id ----------------------------------------------------------------------

    [Theory]
    [InlineData(1, false, "Exit")]
    [InlineData(2, false, "Exit")]
    [InlineData(128, false, "Exit")]
    [InlineData(137, false, "Killed")]
    [InlineData(143, false, "Killed")]
    [InlineData(137, true, "Exit")]
    public void Reads_a_command_ended_by_a_signal_as_one_that_did_not_finish(int exitCode, bool windows, string expected)
    {
        var failure = SystemMachineIdSource.ExitFailure(exitCode, windows);
        Assert.Equal(expected == "Killed" ? typeof(CommandKilledException) : typeof(CommandExitException), failure.GetType());
        Assert.Equal(expected == "Killed" ? MachineIdKind.Transient : MachineIdKind.Unavailable, MachineId.FailureKind(failure));
    }

    [Fact]
    public async Task Gives_up_on_the_boot_check_as_soon_as_one_of_its_reads_fails()
    {
        // Promise.all: the file's time cannot be read, so /proc/stat (which never answers) is not waited on.
        var time = new ManualTime();
        var source = new FakeMachineIdSource
        {
            Platform = "linux",
            Files =
            {
                ["/etc/machine-id"] = FakeMachineIdSource.Text("a1b2c3d4e5f6\n"),
                ["/proc/stat"] = FakeMachineIdSource.Never(),
            },
            Mtime = _ => Task.FromException<double?>(new UnauthorizedAccessException()),
        };
        var reading = await MachineId.ReadAsync(source, TimeSpan.FromSeconds(2), time).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), reading);
    }
}
