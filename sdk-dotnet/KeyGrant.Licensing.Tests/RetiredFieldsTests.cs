using System.Text.Json;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// What an earlier version stored to buy from the app with no key typed: a purchase secret and the two stamps
/// beside it. This version never reads them, and never writes them back, so the next save clears them;
/// a field it does not know otherwise (a later version's) is still kept.
/// </summary>
public class RetiredFieldsTests
{
    private const string Retired = "\"purchaseToken\":\"q1w2e3r4t5y6u7i8o9p0a1s2d3f4g5h6j7k8l9z0x1c\",\"purchaseTokenAt\":1756913600000,\"claimAskedAt\":1756999940000";

    [Fact]
    public void Are_not_read_and_not_written_back_while_a_later_version_s_field_is()
    {
        var state = StoredState.FromJson($"{{\"trialStart\":1756740800000,{Retired},\"later\":{{\"n\":1}}}}")!;
        AssertJson.Equal(JsonNode.Parse("{\"trialStart\":1756740800000,\"later\":{\"n\":1}}"), JsonNode.Parse(state.ToJson()));
    }

    [Fact]
    public async Task Are_cleared_from_the_file_by_the_next_save()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"keygrant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sluice.json");
            await File.WriteAllTextAsync(path, $"{{\"trialStart\":1756740800000,{Retired},\"rev\":3}}");
            var storage = new FileStorage(path);
            Assert.True(await storage.UpdateAsync(s => s with { TrialReportedAt = 1757000000000 }));
            var text = await File.ReadAllTextAsync(path);
            foreach (var name in new[] { "purchaseToken", "purchaseTokenAt", "claimAskedAt" }) Assert.DoesNotContain(name, text);
            AssertJson.Equal(JsonNode.Parse("{\"trialStart\":1756740800000,\"rev\":4,\"trialReportedAt\":1757000000000}"), JsonNode.Parse(text));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Set_among_the_additional_fields_by_hand_are_never_written()
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["purchaseToken"] = JsonSerializer.SerializeToElement(Token),
            ["purchaseTokenAt"] = JsonSerializer.SerializeToElement(1756913600000L),
            ["claimAskedAt"] = JsonSerializer.SerializeToElement(1756999940000L),
            ["later"] = JsonSerializer.SerializeToElement(new { n = 1 }),
        };
        var state = new StoredState { TrialStart = 1756740800000, AdditionalFields = extra };
        AssertJson.Equal(JsonNode.Parse("{\"trialStart\":1756740800000,\"later\":{\"n\":1}}"), JsonNode.Parse(state.ToJson()));
    }

    // Through the whole engine over the default file storage: never sent, and gone from the file once the SDK saves.

    private const string Token = "q1w2e3r4t5y6u7i8o9p0a1s2d3f4g5h6j7k8l9z0x1c";

    private static readonly string[] Names = ["purchaseToken", "purchaseTokenAt", "claimAskedAt"];

    /// <summary>A licence over the default file storage at <paramref name="path"/>, every other adapter the harness's.</summary>
    private static License OverFile(Harness h, string path) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test/",
        PublicJwk = h.Signer.Jwk,
        Adapters = new LicenseAdapters
        {
            Storage = new FileStorage(path),
            Registry = h.Adapters.Registry,
            Clock = h.Adapters.Clock,
            Http = h.Adapters.Http,
            Fingerprint = h.Adapters.Fingerprint,
        },
        TimeProvider = h.Time,
    });

    /// <summary>Writes <paramref name="state"/> as an earlier version did: with the three fields beside it.</summary>
    private static void Write(string path, JsonObject state)
    {
        state["purchaseToken"] = Token;
        state["purchaseTokenAt"] = Now - Day;
        state["claimAskedAt"] = Now - 60_000;
        File.WriteAllText(path, state.ToJsonString());
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void AssertLeftOut(string path)
    {
        var saved = Read(path);
        foreach (var name in Names) Assert.False(saved.ContainsKey(name), $"{name} is still in the file");
    }

    private static void AssertNeverSent(Harness h)
    {
        Assert.False(h.Http.Called("claim"));
        foreach (var call in h.Http.Calls) Assert.DoesNotContain(Token, call.Body.ToJsonString());
    }

    private static async Task InFolder(Func<string, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"keygrant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await test(Path.Combine(dir, "sluice.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public Task A_running_trial_never_sends_them_and_its_first_save_leaves_them_out() => InFolder(async path =>
    {
        var h = Create();
        var lease = h.TrialLease(Now + 5 * Day);
        Write(path, new JsonObject { ["trialStart"] = Now - 2 * Day, ["trialEndsAt"] = Now + 5 * Day, ["trialLease"] = lease, ["lastSeen"] = Now });
        h.Http.Answer("trial", 200, new JsonObject { ["trialStart"] = Now - 2 * Day, ["trialEndsAt"] = Now + 5 * Day, ["lease"] = lease });
        var license = OverFile(h, path);
        Assert.Equal(LicenseState.Trial, (await license.StatusAsync()).State);
        await license.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        // The launch report's stamp is the first save, and it carries none of them.
        Assert.Equal(Now, Read(path)["trialReportedAt"]!.GetValue<long>());
        AssertLeftOut(path);
        Assert.Equal(LicenseState.Trial, (await license.RefreshAsync()).State);
        Assert.Equal(LicenseState.Trial, (await license.StartTrialAsync()).State);
        AssertLeftOut(path);
        AssertNeverSent(h);
    });

    [Fact]
    public Task An_activation_leaves_them_out_and_a_later_deactivation_activates_nothing() => InFolder(async path =>
    {
        var h = Create();
        Write(path, new JsonObject { ["lastSeen"] = Now });
        h.Http.Lease("activate", h.Sign("KEY-1", "perpetual", 30 * 86_400));
        h.Http.Answer("deactivate", 200, new JsonObject());
        var license = OverFile(h, path);
        Assert.True((await license.ActivateAsync("KEY-1")).Ok);
        AssertLeftOut(path);
        Assert.True((await license.DeactivateAsync()).Released);
        AssertLeftOut(path);
        Assert.Equal(LicenseState.Unlicensed, (await license.StatusAsync()).State);
        AssertNeverSent(h);
    });

    [Fact]
    public Task A_deactivation_alone_leaves_them_out() => InFolder(async path =>
    {
        var h = Create();
        Write(path, new JsonObject
        {
            ["key"] = "KEY-1",
            ["activationId"] = "act_1",
            ["lease"] = h.Sign("KEY-1", "perpetual", 30 * 86_400),
            ["checkedMajor"] = 1,
            ["lastSeen"] = Now,
        });
        h.Http.Answer("deactivate", 200, new JsonObject());
        var license = OverFile(h, path);
        Assert.Equal(LicenseState.Licensed, (await license.StatusAsync()).State);
        Assert.True((await license.DeactivateAsync()).Released);
        AssertLeftOut(path);
        AssertNeverSent(h);
    });
}
