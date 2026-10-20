using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The key-pickup groups, which close the file: what a pickup call sends for what a person typed, the error each refusal of
// the server is, the keys a confirm hands back as read, and the key a restore activates.
public partial class ConformanceTests
{
    /// <summary>The key-pickup groups.</summary>
    private static string[] PickupGroups => ["pickupAsks", "pickupRefusals", "pickedKeys", "pickupChoices"];

    public static TheoryData<int, string> PickupAskCases => Cases("pickupAsks");
    public static TheoryData<int, string> PickupRefusalCases => Cases("pickupRefusals");
    public static TheoryData<int, string> PickedKeyCases => Cases("pickedKeys");
    public static TheoryData<int, string> PickupChoiceCases => Cases("pickupChoices");

    [Fact]
    public void Holds_every_pickup_group_to_the_vectors_it_has_and_they_close_the_file_in_their_order()
    {
        // 25 + 24 + 33 + 18 vectors in 4 groups.
        Assert.Equal(100, PickupGroups.Sum(g => Group(g).Count));
        var groups = All.Select(p => p.Key).Where(k => !NotGroups.Contains(k)).ToList();
        Assert.Equal(PickupGroups, groups.TakeLast(PickupGroups.Length));
    }

    [Theory]
    [MemberData(nameof(PickupAskCases))]
    public void What_a_person_typed_for_a_pickup_is_sent_trimmed_or_refused_before_anything_is_asked(int index, string name)
    {
        var v = Item("pickupAsks", index);
        // The code is null for the ask for a code.
        var ask = Pickup.AskFor(Str(v["email"])!, Str(v["code"]));
        var expect = new JsonObject();
        if (ask.Refused is { } refused)
        {
            expect["refused"] = Pickup.Of(refused);
        }
        else
        {
            var send = new JsonObject { ["email"] = ask.Email };
            if (ask.Code is not null) send["code"] = ask.Code;
            expect["send"] = send;
        }
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(PickupRefusalCases))]
    public void A_refused_call_is_the_error_of_the_server_s_word_with_a_wait_only_where_one_helps(int index, string name)
    {
        var v = Item("pickupRefusals", index);
        var (error, retryAfter) = Pickup.Refusal(Json.Lenient(v["json"]));
        var expect = new JsonObject { ["error"] = Pickup.Of(error) };
        if (retryAfter is { } wait) expect["retryAfter"] = wait;
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(PickedKeyCases))]
    public void A_confirm_s_keys_are_read_leniently_and_a_body_that_is_no_answer_is_none(int index, string name)
    {
        var v = Item("pickedKeys", index);
        var keys = Pickup.KeysOf(Json.Lenient(v["json"]));
        AssertJson.Equal(v["expect"], keys is null ? null : KeysJson(keys), name);
    }

    [Theory]
    [MemberData(nameof(PickupChoiceCases))]
    public void The_key_a_restore_activates_is_the_one_held_then_one_with_a_seat_then_the_first(int index, string name)
    {
        var v = Item("pickupChoices", index);
        // The vectors give the keys as read: reading them again changes none.
        var keys = Pickup.KeysOf(new JsonObject { ["keys"] = v["keys"]!.DeepClone() })!;
        AssertJson.Equal(v["keys"], KeysJson(keys), $"{name}: keys");
        var choice = Pickup.Choose(keys, Long(v["major"])!.Value, Long(v["now"])!.Value);
        var expect = choice.Refused is { } refused
            ? new JsonObject { ["refused"] = Pickup.Of(refused) }
            : new JsonObject { ["index"] = choice.Index };
        AssertJson.Equal(v["expect"], expect, name);
    }

    /// <summary>Keys as the vectors write them: a member with no value is left out (a sale, an activation code).</summary>
    private static JsonArray KeysJson(IReadOnlyList<PickedKey> keys)
    {
        var json = new JsonArray();
        foreach (var k in keys)
        {
            var key = new JsonObject
            {
                ["key"] = k.Key,
                ["model"] = k.Model,
                ["modelName"] = k.ModelName,
                ["state"] = k.State,
                ["entitlements"] = EntitlementsJson(k.Entitlements),
                ["expiresAt"] = k.ExpiresAt,
                ["versions"] = k.Versions is { } v ? new JsonObject { ["minMajor"] = v.MinMajor, ["maxMajor"] = v.MaxMajor, ["upgradeUntil"] = v.UpgradeUntil } : null,
                ["deviceLimit"] = k.DeviceLimit,
                ["activeDevices"] = k.ActiveDevices,
                ["held"] = k.Held,
            };
            if (k.Sale is { } sale) key["sale"] = new JsonObject { ["id"] = sale.Id, ["index"] = sale.Index, ["size"] = sale.Size };
            if (k.ActivationCode is not null) key["activationCode"] = k.ActivationCode;
            json.Add(key);
        }
        return json;
    }
}
