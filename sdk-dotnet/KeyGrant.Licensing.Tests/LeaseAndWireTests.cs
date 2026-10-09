using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

/// <summary>The lease verifier as the reference's, the wire helpers, and the state file's JSON.</summary>
public class LeaseAndWireTests
{
    private const long Now = 1_790_000_000_000;
    private static readonly TestSigner Signer = new();
    private static readonly LeaseVerifier Verifier = new(Base64Url.Decode(Signer.PublicX)!);

    private static JsonObject Claims() => new()
    {
        ["iss"] = "keygrant",
        ["sub"] = "SLUICE-AAAA-BBBB-CCCC-DDDD",
        ["product"] = "sluice",
        ["aid"] = "act_1",
        ["model"] = "perpetual",
        ["lim"] = 3,
        ["major"] = 3,
        ["ent"] = new JsonObject(),
        ["iat"] = Now / 1000 - 86_400,
        ["exp"] = Now / 1000 + 86_400,
    };

    private static JsonObject With(Action<JsonObject> change)
    {
        var claims = Claims();
        change(claims);
        return claims;
    }

    private static string? Reason(VerifyResult v) => v.Valid ? null : VerifyResult.NameOf(v.Reason!.Value);

    public static TheoryData<string, string> BadClaims => new()
    {
        { "major: null (only an absent major defaults)", "{\"major\":null}" },
        { "fp: null", "{\"fp\":null}" },
        { "an empty fp", "{\"fp\":\"\"}" },
        { "an fp of 129 characters", $"{{\"fp\":\"{new string('f', 129)}\"}}" },
        { "an unknown fpk", "{\"fpk\":\"bios\"}" },
        { "a lim of 0", "{\"lim\":0}" },
        { "a negative major", "{\"major\":-1}" },
        { "a minMajor of 0", "{\"minMajor\":0}" },
        { "a fractional iat", "{\"iat\":1.5}" },
        { "a negative exp", "{\"exp\":-1}" },
        { "an empty product", "{\"product\":\"\"}" },
        { "an empty aid", "{\"aid\":\"\"}" },
        { "test as a string", "{\"test\":\"true\"}" },
        { "grace as a number", "{\"grace\":1}" },
        { "a string lim", "{\"lim\":\"3\"}" },
    };

    [Theory]
    [MemberData(nameof(BadClaims))]
    public void Refuses_claims_zod_refuses(string _, string overrides)
    {
        var claims = Claims();
        foreach (var (name, value) in JsonNode.Parse(overrides)!.AsObject()) claims[name] = value?.DeepClone();
        Assert.Equal("bad-claims", Reason(Verifier.Verify(Signer.Sign(claims), Now)));
    }

    [Fact]
    public void Defaults_an_absent_major_to_0_and_an_absent_ent_to_empty_and_ignores_unknown_claims()
    {
        var verified = Verifier.Verify(Signer.Sign(With(c =>
        {
            c.Remove("major");
            c.Remove("ent");
            c["somethingNew"] = new JsonArray(1, 2);
        })), Now);
        Assert.True(verified.Valid);
        Assert.Equal(0, verified.Claims!.Major);
        Assert.Empty(verified.Claims.Ent);
    }

    // --- the entitlements claim, read leniently ---------------------

    /// <summary>The entitlements of a valid lease whose <c>ent</c> is the JSON text given, signed as written (null: no <c>ent</c> at all).</summary>
    private static IReadOnlyDictionary<string, Entitlement> VerifiedEnt(string? ent)
    {
        var text = ent is null ? With(c => c.Remove("ent")).ToJsonString() : Claims().ToJsonString().Replace("\"ent\":{}", $"\"ent\":{ent}");
        var verified = Verifier.Verify(Signer.SignRaw(text), Now);
        Assert.True(verified.Valid, $"expected a valid lease, got {Reason(verified)}");
        return verified.Claims!.Ent;
    }

    [Fact]
    public void Reads_every_kind_of_entitlement_as_signed()
    {
        AssertEntitlements.Equal(
            new Dictionary<string, Entitlement>
            {
                ["edition"] = new("pro"),
                ["seats"] = new(5),
                ["ratio"] = new(0.5),
                ["discount"] = new(-2),
                ["beta"] = new(false),
                ["cloud"] = new(true),
                ["label"] = new(""),
                ["users"] = new(5),
                ["big"] = new(3_000_000_000),
                ["huge"] = new(1e21),
                ["tiny"] = new(1e-7),
            },
            VerifiedEnt("{\"edition\":\"pro\",\"seats\":5,\"ratio\":0.5,\"discount\":-2,\"beta\":false,\"cloud\":true,\"label\":\"\",\"users\":5.0,\"big\":3000000000,\"huge\":1e21,\"tiny\":1E-7}"));
    }

    [Fact]
    public void Keeps_the_flag_number_and_text_members_of_a_malformed_ent_and_drops_the_rest_the_lease_still_valid()
    {
        // A signed lease is the server's word: one bad member must not take the licence away, nor the good members beside it.
        AssertEntitlements.Equal(
            new Dictionary<string, Entitlement> { ["edition"] = new("pro"), ["seats"] = new(2) },
            VerifiedEnt("{\"nested\":{\"tier\":\"gold\"},\"edition\":\"pro\",\"none\":null,\"list\":[\"a\"],\"seats\":2,\"empty\":{}}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"pro\",true]")]
    [InlineData("\"pro\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Reads_anything_but_an_object_as_none_never_as_a_refusal(string? ent)
    {
        Assert.Empty(VerifiedEnt(ent));
    }

    [Fact]
    public void Keeps_names_as_they_are()
    {
        AssertEntitlements.Equal(
            new Dictionary<string, Entitlement> { ["__proto__"] = new("pro"), ["édition"] = new("studio"), [""] = new(1), ["constructor"] = new(true) },
            VerifiedEnt("{\"__proto__\":\"pro\",\"édition\":\"studio\",\"\":1,\"constructor\":true}"));
    }

    [Fact]
    public void Keeps_names_and_texts_past_U_FFFF_as_signed_raw_or_escaped()
    {
        // Two UTF-16 units each, whole: U+1F3B5 as a name, U+1D11E in a text.
        var expected = new Dictionary<string, Entitlement> { [char.ConvertFromUtf32(0x1F3B5)] = new("Pro " + char.ConvertFromUtf32(0x1D11E)) };
        AssertEntitlements.Equal(expected, VerifiedEnt(Vectors.EntitlementsJson(expected).ToJsonString()));
        AssertEntitlements.Equal(expected, VerifiedEnt("{\"\\ud83c\\udfb5\":\"Pro \\ud834\\udd1e\"}"));
    }

    [Fact]
    public void Reads_entitlement_numbers_correctly_rounded_as_JavaScript_does()
    {
        // The nearest double to each, its bits as JSON.parse gives them (from node). The conformance
        // harness parses both sides with .NET, so only the bits themselves show a reader that rounds otherwise.
        var ent = VerifiedEnt("{\"ratio\":221.62324973788606,\"share\":975.1952520418383,\"total\":123456789012345680000}");
        Assert.Equal(
            [0x406bb3f1a96f2ec2L, 0x408e798fe04d7162L, 0x441ac53a7e04bcdaL],
            new[] { "ratio", "share", "total" }.Select(name => BitConverter.DoubleToInt64Bits(ent[name].Number!.Value)));
    }

    [Fact]
    public void Keeps_the_last_of_a_repeated_name_as_JSON_parse_does()
    {
        // The last value is the one read, malformed or not.
        AssertEntitlements.Equal(
            new Dictionary<string, Entitlement> { ["edition"] = new("pro"), ["seats"] = new(3) },
            VerifiedEnt("{\"edition\":\"basic\",\"edition\":\"pro\",\"beta\":true,\"beta\":null,\"seats\":{},\"seats\":3}"));
    }

    [Fact]
    public void Enforces_none_of_the_caps_the_server_holds_entitlements_to()
    {
        // The signature is what is trusted: a later server may raise its caps without locking out this build.
        var many = Enumerable.Range(0, 20).ToDictionary(i => $"n{i}", i => new Entitlement(i));
        many[new string('k', 40)] = new Entitlement(true);
        many["long"] = new Entitlement(new string('v', 100));
        AssertEntitlements.Equal(many, VerifiedEnt(Vectors.EntitlementsJson(many).ToJsonString()));
    }

    [Fact]
    public void Takes_integers_written_as_JSON_reals_as_JavaScript_does()
    {
        var verified = Verifier.Verify(Signer.SignRaw(Claims().ToJsonString().Replace("\"lim\":3", "\"lim\":3.0").Replace("\"major\":3", "\"major\":3e0")), Now);
        Assert.True(verified.Valid);
        Assert.Equal((3L, 3L), (verified.Claims!.Lim, verified.Claims.Major));
    }

    [Fact]
    public void Keeps_the_last_of_a_repeated_claim_as_JSON_parse_does()
    {
        var text = Claims().ToJsonString().Replace("\"product\":\"sluice\"", "\"product\":\"maindeck\",\"product\":\"sluice\"");
        Assert.Equal("sluice", Verifier.Verify(Signer.SignRaw(text), Now).Claims!.Product);
    }

    [Fact]
    public void Checks_the_signature_before_the_claims_and_the_claims_before_the_expiry()
    {
        var other = new TestSigner();
        Assert.Equal("bad-signature", Reason(Verifier.Verify(other.SignRaw("not json"), Now)));
        Assert.Equal("bad-claims", Reason(Verifier.Verify(Signer.SignRaw("{\"exp\":1}"), Now)));
        var expired = Verifier.Verify(Signer.Sign(With(c => c["exp"] = Now / 1000 - 1)), Now);
        Assert.Equal("expired", Reason(expired));
        Assert.NotNull(expired.Claims);
        Assert.True(Verifier.Verify(Signer.Sign(With(c => c["exp"] = Now / 1000)), Now).Valid);
    }

    [Fact]
    public void Reads_no_header_and_takes_only_canonical_base64url()
    {
        var token = Signer.Sign(Claims());
        var parts = token.Split('.');
        // The signature covers the header as written, but the header itself is never read.
        Assert.True(Verifier.Verify($"bm90IGpzb24.{parts[1]}.{parts[2]}", Now) is { Valid: false, Reason: VerifyFailure.BadSignature });
        Assert.True(Verifier.Verify(Signer.SignInput($"%%%.{parts[1]}"), Now).Valid);
        // A padded signature is not one the server signed (the standard alphabet and stray bits: the
        // vectors).
        Assert.Equal("malformed", Reason(Verifier.Verify($"{token}==", Now)));
        Assert.Equal("malformed", Reason(Verifier.Verify($"{parts[0]}.{parts[1]}.a", Now)));
        Assert.Equal("malformed", Reason(Verifier.Verify($"{parts[0]}.{parts[1]}", Now)));
        Assert.Equal("malformed", Reason(Verifier.Verify($"{token}.extra", Now)));
        Assert.Equal("bad-signature", Reason(Verifier.Verify($"{parts[0]}.{parts[1]}.AAAA", Now)));
        // A payload that is not canonical, signed as it is: its claims cannot be read.
        Assert.Equal("bad-claims", Reason(Verifier.Verify(Signer.SignInput($"{parts[0]}.{parts[1]}="), Now)));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("AQ", "01")]
    [InlineData("AQI", "0102")]
    [InlineData("AQID", "010203")]
    [InlineData("_-8", "FFEF")]
    public void Decodes_canonical_base64url(string input, string hex)
    {
        Assert.Equal(hex, Convert.ToHexString(Base64Url.Decode(input)!));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("AQ==")]
    [InlineData("AQ=A")]
    [InlineData("A!==")]
    [InlineData(" AQ")]
    [InlineData("AQ ")]
    [InlineData("/+8")]
    [InlineData("AR")]
    [InlineData("AQJ")]
    public void Refuses_what_is_not_canonical_base64url(string input)
    {
        Assert.Null(Base64Url.Decode(input));
    }

    // --- the wire ---------------------------------------------------------------------------------

    [Fact]
    public void Trims_a_key_as_JavaScript_does()
    {
        var nbsp = (char)0xA0;
        var bom = (char)0xFEFF;
        var nel = (char)0x85;
        Assert.Equal("SLUICE-A", Wire.EnteredKey($"{bom}{nbsp} sluice-a\r\n\t"));
        // NEL is no white space to JavaScript.
        Assert.Equal($"SLUICE-A{nel}", Wire.EnteredKey($"sluice-a{nel}"));
    }

    [Fact]
    public void Reads_only_string_errors_leases_and_activation_ids()
    {
        Assert.Null(Wire.ErrorOf(new JsonObject { ["error"] = 403 }));
        Assert.Null(Wire.ErrorOf(new JsonArray("revoked")));
        Assert.Null(Wire.ErrorOf(null));
        Assert.Equal("revoked", Wire.ErrorOf(JsonNode.Parse("{\"error\":\"revoked\"}")));
        Assert.Equal((null, "act"), Wire.ParseLeaseBody(JsonNode.Parse("{\"lease\":5,\"activationId\":\"act\"}")));
        Assert.Equal((null, null), Wire.ParseLeaseBody(JsonValue.Create("lease")));
    }

    [Fact]
    public void Reads_a_trial_body_only_with_numeric_start_and_end()
    {
        Assert.Equal(new TrialBody(1, 2, "L"), Wire.ParseTrialBody(JsonNode.Parse("{\"trialStart\":1,\"trialEndsAt\":2,\"lease\":\"L\"}")));
        Assert.Equal(new TrialBody(1, 2, null), Wire.ParseTrialBody(JsonNode.Parse("{\"trialStart\":1,\"trialEndsAt\":2,\"lease\":7}")));
        Assert.Null(Wire.ParseTrialBody(JsonNode.Parse("{\"trialStart\":\"1\",\"trialEndsAt\":2}")));
        Assert.Null(Wire.ParseTrialBody(JsonNode.Parse("{\"trialStart\":1}")));
        Assert.Null(Wire.ParseTrialBody(null));
    }

    [Fact]
    public void Takes_the_earliest_defined_time()
    {
        Assert.Null(Wire.Earliest());
        Assert.Null(Wire.Earliest(null, null));
        Assert.Equal(3, Wire.Earliest(null, 5, 3, null, 4));
    }

    [Theory]
    [InlineData("https://buy.stripe.com/test_abc", "https://buy.stripe.com/test_abc?client_reference_id=act_1")]
    [InlineData("https://buy.stripe.com", "https://buy.stripe.com/?client_reference_id=act_1")]
    [InlineData("HTTPS://Buy.Stripe.COM/x", "https://buy.stripe.com/x?client_reference_id=act_1")]
    [InlineData("https://buy.stripe.com/x?a=b+c&d", "https://buy.stripe.com/x?a=b+c&d&client_reference_id=act_1")]
    [InlineData("https://buy.stripe.com/x?a=%20%E2%9C%93", "https://buy.stripe.com/x?a=%20%E2%9C%93&client_reference_id=act_1")]
    [InlineData("https://buy.stripe.com/x?CLIENT_REFERENCE_ID=a", "https://buy.stripe.com/x?CLIENT_REFERENCE_ID=a&client_reference_id=act_1")]
    public void Puts_the_reference_on_a_link_editing_its_query_as_text(string link, string expected)
    {
        // One link rule for upgradeUrl and purchaseUrl: the query kept as written, not re-encoded as
        // URLSearchParams would write it.
        Assert.Equal(expected, Pickup.ReferencedLink(link, "act_1"));
    }

    [Theory]
    [InlineData("a b&c=d", "a%20b%26c%3Dd")]
    [InlineData("a!*'()~-_.", "a!*'()~-_.")]
    [InlineData("café", "caf%C3%A9")]
    public void Percent_encodes_the_reference_as_encodeURIComponent_does(string reference, string encoded)
    {
        var value = System.Text.RegularExpressions.Regex.Unescape(reference);
        Assert.Equal($"https://buy.test/?client_reference_id={encoded}", Pickup.ReferencedLink("https://buy.test/", value));
    }

    [Theory]
    [InlineData("buy.stripe.com/test_abc")]
    [InlineData("/buy")]
    [InlineData("")]
    public void Refuses_a_link_that_is_not_an_absolute_URL(string link)
    {
        Assert.Throws<ArgumentException>(() => Pickup.ReferencedLink(link, "act_1"));
    }

    [Fact]
    public void Cuts_a_name_without_splitting_a_character()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.Equal(new string('x', 119), Wire.TrimToLength(new string('x', 119) + emoji, 120));
        Assert.Equal(new string('x', 118) + emoji, Wire.TrimToLength(new string('x', 118) + emoji + "y", 120));
        Assert.Null(Wire.TrimToLength(null, 120));
    }

    // --- the state file's JSON ----------------------------------------------------------------

    [Fact]
    public void Round_trips_every_field_under_the_reference_s_names()
    {
        var state = new StoredState
        {
            Key = "K",
            ActivationId = "A",
            Lease = "L",
            TrialStart = 1,
            TrialEndsAt = 2,
            TrialLease = "T",
            TrialAt = 3,
            LastSeen = 4,
            CheckedMajor = 5,
            KeptMajor = 6,
            BackoffSince = 7,
            BackoffMs = 8,
            Refused = Refusal.Outdated,
            RefusedMajor = 9,
            RefusedFor = ["c"],
            DeviceKind = DeviceKind.Machine,
            VerdictAt = 10,
            Rev = 11,
        };
        var json = JsonNode.Parse(state.ToJson())!.AsObject();
        Assert.Equal(
            ["key", "activationId", "lease", "trialStart", "trialEndsAt", "trialLease", "trialAt", "lastSeen", "checkedMajor", "keptMajor",
             "backoffSince", "backoffMs", "refused", "refusedMajor", "refusedFor", "deviceKind", "verdictAt", "rev"],
            json.Select(p => p.Key));
        Assert.Equal("outdated", json["refused"]!.GetValue<string>());
        Assert.Equal("machine", json["deviceKind"]!.GetValue<string>());
        Assert.Equal(state, StoredState.FromJson(state.ToJson()));
        Assert.Equal("{}", new StoredState().ToJson());
    }

    [Fact]
    public void Keeps_fields_it_does_not_know_and_writes_them_back()
    {
        var state = StoredState.FromJson("{\"key\":\"K\",\"futureField\":{\"a\":[1,2]},\"note\":\"x\"}")!;
        Assert.Equal("K", state.Key);
        var written = JsonNode.Parse(state.ToJson())!;
        AssertJson.Equal(JsonNode.Parse("{\"key\":\"K\",\"futureField\":{\"a\":[1,2]},\"note\":\"x\"}"), written);
        Assert.Equal(state, StoredState.FromJson(state.ToJson()));
    }

    [Fact]
    public void Reads_a_field_of_the_wrong_type_as_absent_rather_than_losing_the_licence_and_keeps_it_as_written()
    {
        var text = "{\"key\":\"K\",\"lastSeen\":\"soon\",\"refused\":\"suspended\",\"keptMajor\":2.5,\"refusedFor\":[\"a\",1],\"deviceKind\":7,\"trialStart\":1.9}";
        var state = StoredState.FromJson(text)!;
        // Absent to every decision here (the lease still licenses; nothing is refused)...
        Assert.Equal(("K", (Refusal?)null, (int?)null, (long?)null, (DeviceKind?)null), (state.Key, state.Refused, state.KeptMajor, state.LastSeen, state.DeviceKind));
        Assert.Equal(["a"], state.RefusedFor!);
        Assert.Equal(1, state.TrialStart);
        // ...and written back as it was, for the SDK that wrote it (a later word, a number as text).
        var written = JsonNode.Parse(state.ToJson())!;
        AssertJson.Equal(JsonNode.Parse("\"soon\""), written["lastSeen"]);
        AssertJson.Equal(JsonNode.Parse("\"suspended\""), written["refused"]);
        AssertJson.Equal(JsonNode.Parse("2.5"), written["keptMajor"]);
        AssertJson.Equal(JsonNode.Parse("7"), written["deviceKind"]);
    }

    [Fact]
    public void Drops_what_the_file_held_for_a_field_once_a_save_sets_or_clears_it()
    {
        var state = StoredState.FromJson("{\"key\":\"K\",\"activationId\":\"act_1\",\"refused\":\"suspended\",\"lastSeen\":\"soon\"}")!;
        // A new lease clears any refusal: the word this SDK could not read goes with it, as in the reference.
        var leased = Offline.Merged(state, Offline.Leased.With(new StatePatch { [StateField.Lease] = "L" }));
        var written = JsonNode.Parse(leased.ToJson())!.AsObject();
        Assert.False(written.ContainsKey("refused"));
        Assert.Equal("L", written["lease"]!.GetValue<string>());
        // And a field set is the value set.
        var seen = StateFields.Set(state, StateField.LastSeen, 5L);
        Assert.Equal(5, JsonNode.Parse(seen.ToJson())!["lastSeen"]!.GetValue<long>());
        Assert.False(seen.AdditionalFields?.ContainsKey("lastSeen") ?? false);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("5", true)]
    [InlineData("[1]", true)]
    [InlineData("\"text\"", true)]
    public void Reads_JSON_that_is_not_an_object_as_no_state_or_an_empty_one(string json, bool empty)
    {
        Assert.Equal(empty ? new StoredState() : null, StoredState.FromJson(json));
    }

    [Fact]
    public void Serialises_through_System_Text_Json_in_the_same_format()
    {
        var state = new StoredState { Key = "K", Refused = Refusal.Device, RefusedFor = ["c"] };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        AssertJson.Equal(JsonNode.Parse(state.ToJson()), JsonNode.Parse(json));
        Assert.Equal(state, System.Text.Json.JsonSerializer.Deserialize<StoredState>(json));
    }

    [Fact]
    public void Writes_text_as_JSON_stringify_does_without_escaping_what_JSON_does_not_need()
    {
        var name = "Rae" + (char)0x2019 + "s <Mac> & co";
        var text = new StoredState { Key = name }.ToJson();
        Assert.Contains(name, text);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), Encoding.UTF8.GetBytes(text).Length);
    }

    [NodeFact]
    public void Matches_node_s_host_name_fingerprint_on_this_machine()
    {
        // The host-name fingerprint must be the Electron SDK's on the same machine (os.hostname(),
        // process.platform, process.arch), as node itself computes it.
        Assert.Equal(Conditions.NodeHostnameFingerprint, DeviceFingerprints.Hostname());
    }
}
