using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// Key pickup: what <c>PickUpAsync</c> sends and answers, and the key <c>RestoreByEmailAsync</c> activates. The rules each
/// piece follows are held to the conformance vectors (<c>pickupAsks</c>, <c>pickupRefusals</c>, <c>pickedKeys</c>,
/// <c>pickupChoices</c>); these are the calls around them.
/// </summary>
public class PickupTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private const long Month = 30 * 24 * 3600;
    private const string Address = "Mara@Studio.example";

    private static readonly IReadOnlyDictionary<string, Entitlement> NoEntitlements = ClaimsParser.EntitlementsOf(null);

    /// <summary>A key as a confirm's answer gives it, with what a test leaves out filled in.</summary>
    private static JsonObject Picked(string key = Key, long minMajor = 1, long? maxMajor = 1, long activeDevices = 0, bool held = false, string? activationCode = null)
    {
        var picked = new JsonObject
        {
            ["key"] = key,
            ["model"] = "perpetual",
            ["modelName"] = "Single",
            ["state"] = "active",
            ["versions"] = new JsonObject { ["minMajor"] = minMajor, ["maxMajor"] = maxMajor, ["upgradeUntil"] = null },
            ["deviceLimit"] = 1,
            ["activeDevices"] = activeDevices,
            ["held"] = held,
        };
        if (activationCode is not null) picked["activationCode"] = activationCode;
        return picked;
    }

    private static JsonObject Keys(params JsonObject[] keys) => new() { ["keys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) };

    private static Harness Confirming(params JsonObject[] keys)
    {
        var h = Create();
        h.Http.Answer("confirm", 200, Keys(keys));
        return h;
    }

    private static void Activating(Harness h) => h.Http.Lease("activate", h.Sign(Key, "perpetual", Month));

    // --- PickUpAsync(email) -------------------------------------------------------------------

    [Fact]
    public async Task Asks_for_a_code_with_the_address_trimmed_and_answers_sent()
    {
        var h = Create();
        h.Http.Answer("pickup", 200, new JsonObject { ["ok"] = true });
        Assert.Equal(PickUpSentResult.Sent, await h.License.PickUpAsync("  Mara@Studio.example\n"));
        Assert.Equal(Address, h.Http.BodyOf("pickup")["email"]!.GetValue<string>());
        Assert.Single(h.Http.BodyOf("pickup"));
    }

    [Fact]
    public async Task Refuses_a_blank_address_before_it_asks_and_answers_each_refusal_as_its_error()
    {
        var h = Create();
        Assert.Equal(PickUpSentResult.Failure(PickupError.InvalidEmail), await h.License.PickUpAsync("   "));
        Assert.Equal(PickUpSentResult.Failure(PickupError.InvalidEmail), await h.License.PickUpAsync(new string('a', 254) + "@b.example"));
        Assert.Equal(0, h.Http.Count("pickup"));

        h.Http.Error("pickup", 403, "pickup-disabled");
        Assert.Equal(PickUpSentResult.Failure(PickupError.PickupDisabled), await h.License.PickUpAsync(Address));
        h.Http.Answer("pickup", 429, new JsonObject { ["error"] = "rate-limited", ["retryAfter"] = 120 });
        Assert.Equal(PickUpSentResult.Failure(PickupError.TooManyAttempts, 120), await h.License.PickUpAsync(Address));
        h.Http.Fail = true;
        Assert.Equal(PickUpSentResult.Failure(PickupError.Network), await h.License.PickUpAsync(Address));
    }

    [Fact]
    public async Task Reads_the_wait_from_the_body_and_the_word_decides_never_the_status()
    {
        var h = Create();
        h.Http.Answer("pickup", 503, new JsonObject { ["error"] = "unavailable", ["retryAfter"] = 60 });
        Assert.Equal(PickUpSentResult.Failure(PickupError.TryAgain, 60), await h.License.PickUpAsync(Address));
        h.Http.Error("pickup", 502, "wrong-code");
        Assert.Equal(PickUpSentResult.Failure(PickupError.WrongCode), await h.License.PickUpAsync(Address));
        h.Http.Error("pickup", 404, "unknown-product");
        Assert.Equal(PickUpSentResult.Failure(PickupError.Network), await h.License.PickUpAsync(Address));
    }

    [Fact]
    public async Task Writes_nothing_when_it_asks_for_a_code()
    {
        var h = Create();
        h.Http.Answer("pickup", 200, new JsonObject { ["ok"] = true });
        await h.License.PickUpAsync(Address);
        Assert.Equal(0, h.Writes);
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Throws_for_a_null_address_or_code_and_for_a_call_cancelled_before_it_is_answered()
    {
        var h = Create();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.License.PickUpAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.License.PickUpAsync(Address, null!));
        h.Http.Hang = true;
        using var cts = new CancellationTokenSource();
        var pending = h.License.PickUpAsync(Address, cts.Token);
        await Eventually.Until(() => h.Http.Called("pickup"), "the request");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // --- PickUpAsync(email, code) -------------------------------------------------------------

    [Fact]
    public async Task Sends_the_address_the_code_and_this_device_s_fingerprints_and_answers_the_keys_as_read()
    {
        var h = Confirming(new JsonObject { ["key"] = Key, ["model"] = "perpetual", ["modelName"] = "Single", ["state"] = "active", ["held"] = true });
        var res = await h.License.PickUpAsync(Address, " 123 456 ");
        Assert.True(res.Ok);
        var picked = Assert.Single(res.Keys!);
        Assert.Equal(new PickedKey(Key, "perpetual", "Single", "active", NoEntitlements, null, null, 0, 0, true, null, null), picked with { Entitlements = NoEntitlements });
        var body = h.Http.BodyOf("confirm");
        Assert.Equal(Address, body["email"]!.GetValue<string>());
        Assert.Equal("123456", body["code"]!.GetValue<string>());
        Assert.Equal(["fp-test"], body["fingerprints"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task Sends_no_fingerprints_for_a_product_whose_devices_are_not_held()
    {
        var h = Create(null, new Options { DeviceHold = false });
        h.Http.Answer("confirm", 200, Keys());
        Assert.True((await h.License.PickUpAsync(Address, "123456")).Ok);
        Assert.False(h.Http.BodyOf("confirm").ContainsKey("fingerprints"));
    }

    [Fact]
    public async Task Refuses_a_code_that_is_not_six_digits_before_it_asks_costing_the_address_no_try()
    {
        var h = Create();
        foreach (var code in new[] { "12345", "", "   ", "12345a", "１２３４５６" })
        {
            Assert.Equal(PickUpKeysResult.Failure(PickupError.WrongCode), await h.License.PickUpAsync(Address, code));
        }
        // The address is judged first.
        Assert.Equal(PickUpKeysResult.Failure(PickupError.InvalidEmail), await h.License.PickUpAsync("", "12"));
        Assert.Equal(0, h.Http.Count("confirm"));
    }

    [Fact]
    public async Task Answers_the_server_s_words_and_a_200_that_is_no_pickup_answer_as_network()
    {
        var h = Create();
        foreach (var (status, word, error) in new[]
        {
            (403, "wrong-code", PickupError.WrongCode),
            (403, "code-expired", PickupError.CodeExpired),
            (403, "pickup-disabled", PickupError.PickupDisabled),
            (503, "try-again", PickupError.TryAgain),
            (400, "invalid-request", PickupError.InvalidEmail),
        })
        {
            h.Http.Error("confirm", status, word);
            Assert.Equal(PickUpKeysResult.Failure(error), await h.License.PickUpAsync(Address, "123456"));
        }
        h.Http.Answer("confirm", 429, new JsonObject { ["error"] = "too-many-attempts", ["retryAfter"] = 3000 });
        Assert.Equal(PickUpKeysResult.Failure(PickupError.TooManyAttempts, 3000), await h.License.PickUpAsync(Address, "123456"));
        foreach (var body in new JsonObject[] { new() { ["ok"] = true }, new() { ["keys"] = "no" } })
        {
            h.Http.Answer("confirm", 200, body);
            Assert.Equal(PickUpKeysResult.Failure(PickupError.Network), await h.License.PickUpAsync(Address, "123456"));
        }
        h.Http.Fail = true;
        Assert.Equal(PickUpKeysResult.Failure(PickupError.Network), await h.License.PickUpAsync(Address, "123456"));
    }

    [Fact]
    public async Task Writes_nothing_when_it_confirms()
    {
        var h = Confirming(Picked());
        Assert.True((await h.License.PickUpAsync(Address, "123456")).Ok);
        Assert.Equal(0, h.Writes);
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Asks_again_with_another_code_and_once_the_keys_are_ten_minutes_old()
    {
        var h = Confirming();
        await h.License.PickUpAsync(Address, "123456");
        await h.License.PickUpAsync(Address, "654321");
        Assert.Equal(2, h.Http.Count("confirm"));
        await h.License.PickUpAsync(Address, "654321");
        Assert.Equal(2, h.Http.Count("confirm"));
        h.SetClock(Now + Pickup.KeysKeepMs);
        await h.License.PickUpAsync(Address, "654321");
        Assert.Equal(3, h.Http.Count("confirm"));
        // Another address asks again too.
        await h.License.PickUpAsync("other@studio.example", "654321");
        Assert.Equal(4, h.Http.Count("confirm"));
    }

    // --- RestoreByEmailAsync ------------------------------------------------------------------

    [Fact]
    public async Task Confirms_then_activates_the_key_that_fits_with_the_address_and_stores_the_activation_never_the_code()
    {
        var h = Confirming(Picked());
        Activating(h);
        Assert.Equal(RestoreResult.Success, await h.License.RestoreByEmailAsync(Address, "123456"));
        var body = h.Http.BodyOf("activate");
        Assert.Equal(Key, body["key"]!.GetValue<string>());
        Assert.Equal(Address, body["email"]!.GetValue<string>());
        Assert.False(body.ContainsKey("code"));
        var stored = h.Stored!.ToJson();
        Assert.Contains(Key, stored);
        Assert.DoesNotContain("studio", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", stored);
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        Assert.Equal(["confirm", "activate"], h.Http.Calls.Select(c => c.Action));
    }

    [Fact]
    public async Task Activates_the_address_as_the_pickup_sent_it_trimmed()
    {
        var h = Confirming(Picked());
        Activating(h);
        Assert.Equal(RestoreResult.Success, await h.License.RestoreByEmailAsync("  Mara@Studio.example\n", " 123 456 "));
        Assert.Equal(Address, h.Http.BodyOf("confirm")["email"]!.GetValue<string>());
        Assert.Equal("123456", h.Http.BodyOf("confirm")["code"]!.GetValue<string>());
        Assert.Equal(Address, h.Http.BodyOf("activate")["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task Activates_a_code_locked_key_with_the_code_the_pickup_handed_back()
    {
        var h = Confirming(Picked(activationCode: "654321"));
        Activating(h);
        Assert.Equal(RestoreResult.Success, await h.License.RestoreByEmailAsync(Address, "123456"));
        var body = h.Http.BodyOf("activate");
        Assert.Equal(Key, body["key"]!.GetValue<string>());
        Assert.Equal(Address, body["email"]!.GetValue<string>());
        Assert.Equal("654321", body["code"]!.GetValue<string>());
        // Neither the pickup's code nor the lock's is stored.
        var stored = h.Stored!.ToJson();
        Assert.DoesNotContain("654321", stored);
        Assert.DoesNotContain("123456", stored);
    }

    [Fact]
    public async Task Chooses_the_key_this_device_holds_then_one_with_a_seat()
    {
        const string a = "SLUICE-AAAA-AAAA-AAAA-AAAA";
        const string b = "SLUICE-BBBB-BBBB-BBBB-BBBB";
        const string c = "SLUICE-CCCC-CCCC-CCCC-CCCC";
        var h = Create();
        h.Http.Answer("confirm", 200, Keys(Picked(a, activeDevices: 1), Picked(b), Picked(c, activeDevices: 1, held: true)));
        h.Http.Error("activate", 409, "limit");
        Assert.Equal(RestoreResult.Failure(LicenseError.Limit), await h.License.RestoreByEmailAsync(Address, "123456"));
        Assert.Equal(c, h.Http.BodyOf("activate")["key"]!.GetValue<string>());
        h.Http.Answer("confirm", 200, Keys(Picked(a, activeDevices: 1), Picked(b)));
        await h.License.RestoreByEmailAsync(Address, "654321");
        Assert.Equal(b, h.Http.LastBodyOf("activate")["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task Answers_no_key_for_an_address_with_none_and_upgrade_or_outdated_activating_nothing()
    {
        var h = Create(2);
        h.Http.Answer("confirm", 200, Keys());
        Assert.Equal(RestoreResult.NoKeyFound, await h.License.RestoreByEmailAsync(Address, "123456"));
        // Keys for an older major: buy an upgrade.
        h.Http.Answer("confirm", 200, Keys(Picked()));
        Assert.Equal(RestoreResult.Failure(LicenseError.Upgrade), await h.License.RestoreByEmailAsync(Address, "222222"));
        // Keys that start at a major after this build: update the app.
        h.Http.Answer("confirm", 200, Keys(Picked(minMajor: 3, maxMajor: null)));
        Assert.Equal(RestoreResult.Failure(LicenseError.Outdated), await h.License.RestoreByEmailAsync(Address, "333333"));
        Assert.Equal(0, h.Http.Count("activate"));
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Answers_a_pickup_refusal_and_an_activation_s_as_they_come()
    {
        var h = Create();
        h.Http.Error("confirm", 403, "code-expired");
        Assert.Equal(RestoreResult.Failure(PickupError.CodeExpired), await h.License.RestoreByEmailAsync(Address, "123456"));
        h.Http.Answer("confirm", 429, new JsonObject { ["error"] = "too-many-attempts", ["retryAfter"] = 3600 });
        var waiting = await h.License.RestoreByEmailAsync(Address, "123456");
        Assert.Equal(PickupError.TooManyAttempts, waiting.PickupError);
        Assert.Equal(3600, waiting.RetryAfter);
        // The typed refusals ask nothing.
        Assert.Equal(RestoreResult.Failure(PickupError.InvalidEmail), await h.License.RestoreByEmailAsync("", "123456"));
        Assert.Equal(RestoreResult.Failure(PickupError.WrongCode), await h.License.RestoreByEmailAsync(Address, "12"));
        Assert.Equal(2, h.Http.Count("confirm"));

        h.Http.Answer("confirm", 200, Keys(Picked()));
        h.Http.Error("activate", 409, "limit");
        Assert.Equal(RestoreResult.Failure(LicenseError.Limit), await h.License.RestoreByEmailAsync(Address, "123456"));
        h.Http.Error("activate", 403, "user-code-invalid");
        Assert.Equal(RestoreResult.Failure(LicenseError.UserCodeInvalid), await h.License.RestoreByEmailAsync(Address, "123456"));
        h.Http.Fail = true;
        Assert.Equal(RestoreResult.Failure(LicenseError.Network), await h.License.RestoreByEmailAsync(Address, "123456"));
    }

    [Fact]
    public async Task Keeps_the_confirmed_keys_in_memory_so_a_retry_or_a_restore_after_PickUpAsync_asks_once()
    {
        var h = Confirming(Picked());
        h.Http.Error("activate", 503, "unavailable");
        Assert.Equal(RestoreResult.Failure(LicenseError.Network), await h.License.RestoreByEmailAsync(Address, "123456"));
        Activating(h);
        Assert.Equal(RestoreResult.Success, await h.License.RestoreByEmailAsync(Address, " 123 456 "));
        Assert.Equal(1, h.Http.Count("confirm"));
        // Done with once activated: the same pair asks again.
        Assert.True((await h.License.PickUpAsync(Address, "123456")).Ok);
        Assert.Equal(2, h.Http.Count("confirm"));
        // Kept in memory only: nothing of them is stored.
        Assert.DoesNotContain("123456", h.Stored!.ToJson());
    }

    [Fact]
    public async Task Leaves_the_licence_this_device_holds_as_it_was_when_a_restore_fails()
    {
        var h = Create();
        Activating(h);
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        var before = h.Stored!.ToJson();
        h.Http.Answer("confirm", 200, Keys(Picked("SLUICE-OTHR-BBBB-CCCC-DDDD")));
        h.Http.Error("activate", 409, "limit");
        Assert.Equal(RestoreResult.Failure(LicenseError.Limit), await h.License.RestoreByEmailAsync(Address, "123456"));
        h.Http.Fail = true;
        Assert.Equal(RestoreResult.Failure(PickupError.Network), await h.License.RestoreByEmailAsync(Address, "654321"));
        Assert.Equal(before, h.Stored!.ToJson());
    }

    [Fact]
    public async Task Answers_pickups_beside_a_stuck_call_and_a_restore_network_asking_nothing()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", Month), CheckedMajor = 1, LastSeen = Now };
        h.Http.Error("validate", 403, "revoked");
        h.Http.Answer("pickup", 200, new JsonObject { ["ok"] = true });
        h.Http.Answer("confirm", 200, Keys());
        h.HoldNextWrite();
        _ = h.License.RefreshAsync();
        await Eventually.Until(() => h.Writes == 1, "the held write");
        time.Advance(Grace);
        Assert.Equal(PickUpSentResult.Sent, await h.License.PickUpAsync(Address).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await h.License.PickUpAsync(Address, "123456").WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(RestoreResult.Failure(LicenseError.Network), await h.License.RestoreByEmailAsync(Address, "654321").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Http.Count("confirm"));
        Assert.Equal(1, h.Writes);
    }

    private static readonly TimeSpan Grace = LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15);

    // --- the pieces ---------------------------------------------------------------------------

    [Fact]
    public void Asks_for_a_code_with_an_address_alone_and_confirms_with_a_code()
    {
        Assert.Equal(new Pickup.Ask(Address, null, null), Pickup.AskFor(Address, null));
        Assert.Equal(new Pickup.Ask(Address, "123456", null), Pickup.AskFor(Address, "123456"));
        Assert.Equal(PickupError.InvalidEmail, Pickup.AskFor("", "123456").Refused);
    }

    [Fact]
    public void Reads_a_refusal_leniently()
    {
        Assert.Equal((PickupError.Network, (long?)null), Pickup.Refusal(null));
        Assert.Equal((PickupError.TryAgain, (long?)60), Pickup.Refusal(new JsonObject { ["error"] = "unavailable", ["retryAfter"] = 60 }));
        Assert.Equal((PickupError.WrongCode, (long?)null), Pickup.Refusal(new JsonObject { ["error"] = "wrong-code", ["retryAfter"] = 60 }));
        Assert.Equal((PickupError.TooManyAttempts, (long?)null), Pickup.Refusal(new JsonObject { ["error"] = "too-many-attempts", ["retryAfter"] = 1.5 }));
    }

    [Fact]
    public void Reads_a_confirm_s_keys_leniently_and_none_from_what_is_no_answer()
    {
        Assert.Null(Pickup.KeysOf(new JsonObject { ["keys"] = "no" }));
        Assert.Null(Pickup.KeysOf(new JsonArray()));
        Assert.Null(Pickup.KeysOf(null));
        var read = Pickup.KeysOf(new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject { ["key"] = "" }, 7, new JsonObject { ["key"] = Key, ["held"] = "yes", ["activationCode"] = "12345" }),
        })!;
        var only = Assert.Single(read);
        Assert.Equal(new PickedKey(Key, "", null, "", NoEntitlements, null, null, 0, 0, false, null, null), only);
    }

    [Fact]
    public void Judges_a_key_s_versions_as_the_server_does()
    {
        Assert.True(Pickup.Covers(null, 9, Now));
        Assert.False(Pickup.Covers(new PickedVersions(2, 3, null), 1, Now));
        Assert.False(Pickup.Covers(new PickedVersions(2, 3, null), 4, Now));
        Assert.True(Pickup.Covers(new PickedVersions(2, 3, Now + 1), 4, Now));
        Assert.False(Pickup.Covers(new PickedVersions(2, 3, Now), 4, Now));
        Assert.True(Pickup.Covers(new PickedVersions(2, null, null), 40, Now));
    }

    [Fact]
    public void Chooses_the_key_held_then_one_with_a_free_seat_then_the_first()
    {
        PickedKey Read(JsonObject key) => Pickup.KeysOf(Keys(key))!.Single();
        var full = Read(Picked("A", activeDevices: 1));
        var free = Read(Picked("B"));
        var held = Read(Picked("C", activeDevices: 1, held: true));
        Assert.Equal(2, Pickup.Choose([full, free, held], 1, Now).Index);
        Assert.Equal(1, Pickup.Choose([full, free], 1, Now).Index);
        Assert.Equal(0, Pickup.Choose([full, Read(Picked("D", activeDevices: 1))], 1, Now).Index);
        Assert.Equal(Pickup.Unchosen.Outdated, Pickup.Choose([Read(Picked(minMajor: 3, maxMajor: null))], 1, Now).Refused);
    }
}
