using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

public class ActivateTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";

    private static Harness Activating(Options? options = null)
    {
        var h = Create(null, options);
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        return h;
    }

    [Fact]
    public async Task Cuts_a_device_name_the_server_would_refuse()
    {
        var h = Activating(new Options { DeviceName = new string('D', 500) });
        await h.License.ActivateAsync(Key);
        Assert.Equal(120, h.Http.BodyOf("activate")["name"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Does_not_cut_a_character_in_half()
    {
        var h = Activating(new Options { DeviceName = new string('D', 119) + char.ConvertFromUtf32(0x1F600) });
        await h.License.ActivateAsync(Key);
        Assert.Equal(new string('D', 119), h.Http.BodyOf("activate")["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Leaves_a_name_of_exactly_the_limit_alone()
    {
        var h = Activating(new Options { DeviceName = new string('D', 120) });
        await h.License.ActivateAsync(Key);
        Assert.Equal(120, h.Http.BodyOf("activate")["name"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Sends_a_device_name_that_fits_unchanged_and_none_when_there_is_none()
    {
        var name = "Rae" + (char)0x2019 + "s MacBook";
        var h = Activating(new Options { DeviceName = name });
        await h.License.ActivateAsync(Key);
        Assert.Equal(name, h.Http.BodyOf("activate")["name"]!.GetValue<string>());

        var unnamed = Activating();
        await unnamed.License.ActivateAsync(Key);
        Assert.False(unnamed.Http.BodyOf("activate").ContainsKey("name"));
    }

    [Fact]
    public async Task Refuses_a_key_longer_than_one_could_be_by_name()
    {
        var h = Create();
        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidKey), await h.License.ActivateAsync(new string('S', 200)));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Sends_a_key_of_exactly_the_limit_rather_than_refusing_it()
    {
        var key = "SLUICE-" + new string('A', 113);
        Assert.Equal(120, key.Length);
        var h = Create();
        h.Http.Lease("activate", h.Sign(key, "perpetual", 30 * 24 * 3600));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(key));
        Assert.Equal(key, h.Http.BodyOf("activate")["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task Activates_on_a_key_pasted_with_the_whitespace_around_it_and_stores_the_trimmed_one()
    {
        var h = Activating();
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync("  " + Key + "\n"));
        Assert.Equal(Key, h.Http.BodyOf("activate")["key"]!.GetValue<string>());
        Assert.Equal(Key, h.Stored?.Key);
    }

    [Fact]
    public async Task Does_not_count_the_whitespace_around_a_key_towards_its_length()
    {
        var key = "SLUICE-" + new string('A', 113);
        var h = Create();
        h.Http.Lease("activate", h.Sign(key, "perpetual", 30 * 24 * 3600));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(key + "\n"));
    }

    [Fact]
    public async Task Activates_on_a_key_typed_in_lower_case_and_stores_the_normalised_one()
    {
        var h = Activating();
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync("sluice-aaaa-bbbb-cccc-dddd"));
        Assert.Equal(Key, h.Http.BodyOf("activate")["key"]!.GetValue<string>());
        Assert.Equal(Key, h.Stored?.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_an_empty_key_without_asking_anybody(string key)
    {
        var h = Create();
        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidKey), await h.License.ActivateAsync(key));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Stores_a_verified_lease_and_reports_licensed()
    {
        var h = Activating();
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal(Key, h.Stored?.Key);
        Assert.Equal("act_1", h.Stored?.ActivationId);
        Assert.Equal(1, h.Stored?.CheckedMajor);

        var status = await h.License.StatusAsync();
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, status);
    }

    [Fact]
    public async Task Sends_exactly_the_reference_s_body()
    {
        var h = Create(null, new Options { Major = 3, DeviceName = "Box" });
        h.Http.Lease("activate", h.SignLease(new LeaseInput { Sub = Key, Major = 3 }));
        await h.License.ActivateAsync(Key);
        // A fingerprinter with only GetAsync reports no kind.
        AssertJson.Equal(
            new JsonObject { ["key"] = Key, ["fingerprint"] = "fp-test", ["name"] = "Box", ["major"] = 3 },
            h.Http.BodyOf("activate"));
    }

    [Theory]
    [InlineData(404, "invalid-key", LicenseError.InvalidKey)]
    [InlineData(409, "limit", LicenseError.Limit)]
    [InlineData(403, "revoked", LicenseError.Revoked)]
    [InlineData(403, "expired", LicenseError.Revoked)]
    [InlineData(403, "upgrade-required", LicenseError.Upgrade)]
    [InlineData(403, "update-required", LicenseError.Outdated)]
    [InlineData(400, "weird", LicenseError.Network)]
    [InlineData(500, "boom", LicenseError.Network)]
    public async Task Maps_a_server_refusal_to_its_error(int status, string error, LicenseError expected)
    {
        var h = Create();
        h.Http.Error("activate", status, error);
        Assert.Equal(ActivateResult.Failure(expected), await h.License.ActivateAsync("KEY"));
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Returns_network_on_a_transport_failure()
    {
        var h = Create();
        h.Http.Fail = true;
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await h.License.ActivateAsync("KEY"));
    }

    [Fact]
    public async Task Returns_network_when_the_server_answers_200_without_a_lease_or_activation()
    {
        var h = Create();
        h.Http.Answer("activate", 200, new JsonObject { ["activationId"] = "act_1" });
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await h.License.ActivateAsync("KEY"));
        h.Http.Answer("activate", 200, new JsonObject { ["lease"] = h.Sign("KEY", "perpetual", 3600), ["activationId"] = 7 });
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await h.License.ActivateAsync("KEY"));
    }

    [Fact]
    public async Task Returns_invalid_lease_when_the_server_lease_fails_offline_verification()
    {
        var h = Create();
        h.Http.Lease("activate", "not.a.jwt");
        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidLease), await h.License.ActivateAsync("KEY"));
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Posts_to_the_product_s_endpoint_with_one_trailing_slash_taken_off_the_base()
    {
        var seen = new List<string>();
        var h = Create();
        var http = new RecordingHttp(seen);
        var license = new License(new LicenseConfig
        {
            Product = Product,
            ApiBaseUrl = "https://api.test/",
            PublicJwk = h.Signer.Jwk,
            Adapters = new LicenseAdapters { Storage = h.Adapters.Storage, Http = http, Fingerprint = new FixedFingerprinter("fp"), Clock = h.Adapters.Clock },
        });
        await license.ActivateAsync("KEY");
        Assert.Equal(["https://api.test/v1/products/sluice/activate"], seen);
    }

    [Fact]
    public async Task Starts_a_key_s_notes_afresh_and_clears_any_refusal_and_wait()
    {
        var h = Create(
            new StoredState
            {
                Key = "OLD-KEY",
                ActivationId = "act_old",
                KeptMajor = 7,
                CheckedMajor = 7,
                Refused = Refusal.Revoked,
                BackoffSince = Now,
                BackoffMs = 3_600_000,
            },
            new Options { Major = 2 });
        h.Http.Lease("activate", h.SignLease(new LeaseInput { Sub = Key, Major = 1_000_000, UpgradeUntil = (Now + 10 * Day) / 1000 }));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        var stored = h.Stored!;
        Assert.Equal((Key, "act_1", 2, 2), (stored.Key, stored.ActivationId, stored.CheckedMajor, stored.KeptMajor));
        Assert.Null(stored.Refused);
        Assert.Null(stored.BackoffSince);
        Assert.Null(stored.BackoffMs);
        Assert.Equal(Now, stored.VerdictAt);
        Assert.Equal(Now, stored.LastSeen);
    }

    private sealed class RecordingHttp(List<string> seen) : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            seen.Add(url);
            return Task.FromResult(new HttpResult(404, null));
        }
    }
}
