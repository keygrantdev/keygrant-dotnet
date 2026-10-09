using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// A key set (<see cref="LicenseConfig.PublicJwks"/>): a lease verified by the key it names, the ids of the
/// keys reported at each check-in, a lease signed by a key the build does not carry; and
/// <see cref="License.HasAsync"/> and <see cref="License.RequireAsync"/>, read offline from the stored lease.
/// </summary>
public class KeySetTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";

    /// <summary>The harness's app, built again with <paramref name="keys"/> (its storage, clock, device and server kept).</summary>
    private static License Build(Harness h, IReadOnlyList<PublicJwk>? keys = null, PublicJwk? one = null) => new(new LicenseConfig
    {
        Product = Product,
        ApiBaseUrl = "https://api.test/",
        PublicJwks = keys,
        PublicJwk = one,
        Adapters = h.Adapters,
        TimeProvider = h.Time,
    });

    private static string[] Kids(JsonObject body) => body["kids"]!.AsArray().Select(k => (string)k!).ToArray();

    [Fact]
    public async Task Activates_on_a_lease_the_staged_key_signed_when_the_build_carries_it_and_reports_its_key_ids()
    {
        var h = Create();
        var next = new TestSigner();
        var lease = next.Named(next.SignLease(new LeaseInput { Sub = Key }, Now), next.Kid);
        h.Http.Lease("activate", lease);
        var app = Build(h, [h.Signer.Jwk, next.Jwk]);

        Assert.Equal(ActivateResult.Success, await app.ActivateAsync(Key));
        Assert.Equal([h.Signer.Kid, next.Kid], Kids(h.Http.BodyOf("activate")));
        Assert.Equal(lease, h.Stored?.Lease);
    }

    [Fact]
    public async Task Refuses_an_activation_whose_lease_names_a_key_the_build_does_not_carry()
    {
        var h = Create();
        var next = new TestSigner();
        h.Http.Lease("activate", next.Named(next.SignLease(new LeaseInput { Sub = Key }, Now), next.Kid));

        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidLease), await h.License.ActivateAsync(Key));
        Assert.Null(h.Stored?.Lease);
    }

    [Fact]
    public async Task Answers_a_stored_lease_signed_by_a_key_the_build_does_not_carry_as_unknown_key_with_the_key()
    {
        var next = new TestSigner();
        var h = Create(new StoredState { Key = Key, ActivationId = "act_1", Lease = next.Named(next.SignLease(new LeaseInput { Sub = Key }, Now), next.Kid) });
        h.Http.Fail = true;

        var status = await h.License.StatusAsync();
        Assert.Equal(new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.UnknownKey, Key = Key }, status);
    }

    [Fact]
    public async Task Sends_the_ids_of_the_keys_it_carries_with_each_validate()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Sign(Key, "perpetual", 2 * 86_400) };
        h.Http.Answer("validate", 503, new JsonObject());
        await h.License.RefreshAsync();
        Assert.Equal([h.Signer.Kid], Kids(h.Http.BodyOf("validate")));
    }

    [Fact]
    public async Task Takes_PublicJwk_alone_as_a_set_of_one_a_lease_naming_its_key_verifies_and_one_naming_none()
    {
        var h = Create();
        var app = Build(h, one: h.Signer.Jwk);
        var plain = h.Sign(Key, "perpetual", 30 * 86_400);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.Signer.Named(plain, h.Signer.Kid) };
        Assert.Equal(LicenseState.Licensed, (await app.StatusAsync()).State);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = plain };
        Assert.Equal(LicenseState.Licensed, (await app.StatusAsync()).State);
    }

    [Fact]
    public void Refuses_a_configuration_with_no_key_an_empty_set_or_both()
    {
        var h = Create();
        Assert.Throws<ArgumentException>(() => Build(h));
        Assert.Throws<ArgumentException>(() => Build(h, []));
        Assert.Throws<ArgumentException>(() => Build(h, [h.Signer.Jwk], h.Signer.Jwk));
        // A second key that is missing, or not a key, is refused when the app is made.
        Assert.Throws<ArgumentException>(() => Build(h, [h.Signer.Jwk, null!]));
        Assert.Throws<ArgumentException>(() => Build(h, [h.Signer.Jwk, new PublicJwk { X = "AAAA" }]));
    }

    [Fact]
    public void Parses_a_JWK_set_or_a_list_of_JWKs_in_order()
    {
        var a = new TestSigner().Jwk;
        var b = new TestSigner().Jwk;
        string Jwk(PublicJwk k) => $"{{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"x\":\"{k.X}\",\"kid\":\"ignored\"}}";
        Assert.Equal([a.X, b.X], PublicJwk.ParseSet($"{{\"keys\":[{Jwk(a)},{Jwk(b)}]}}").Select(k => k.X));
        Assert.Equal([b.X], PublicJwk.ParseSet($"[{Jwk(b)}]").Select(k => k.X));
        Assert.Throws<ArgumentException>(() => PublicJwk.ParseSet("{\"keys\":[]}"));
        Assert.Throws<ArgumentException>(() => PublicJwk.ParseSet("{\"publicJwk\":{}}"));
        Assert.Throws<ArgumentException>(() => PublicJwk.ParseSet("not json"));
        // A member Parse refuses refuses the set: here one holding a private key.
        Assert.Throws<ArgumentException>(() => PublicJwk.ParseSet($"{{\"keys\":[{Jwk(a)},{Jwk(b)[..^1]},\"d\":\"AAAA\"}}]}}"));
        // A JWK whose `keys` member is not a list is refused, never read as the one JWK it also is.
        Assert.Throws<ArgumentException>(() => PublicJwk.ParseSet($"{Jwk(a)[..^1]},\"keys\":\"x\"}}"));
    }

    private static Harness LicensedWith(JsonNode ent)
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = h.SignLease(new LeaseInput { Sub = Key, Ent = ent }) };
        return h;
    }

    private static JsonObject Ent() => new() { ["cloud"] = true, ["beta"] = false, ["seats"] = 0, ["devices"] = 5, ["edition"] = "pro", ["blank"] = "" };

    [Fact]
    public async Task HasAsync_answers_from_the_stored_lease_a_flag_on_a_number_not_zero_a_text_not_empty()
    {
        var h = LicensedWith(Ent());
        Assert.True(await h.License.HasAsync("cloud"));
        Assert.False(await h.License.HasAsync("beta"));
        Assert.False(await h.License.HasAsync("seats"));
        Assert.True(await h.License.HasAsync("devices"));
        Assert.True(await h.License.HasAsync("edition"));
        Assert.False(await h.License.HasAsync("blank"));
        Assert.False(await h.License.HasAsync("export"));
        Assert.False(await h.License.HasAsync("Cloud"));
    }

    [Fact]
    public async Task HasAsync_never_goes_online_and_never_writes()
    {
        var h = LicensedWith(Ent());
        // A lease near its end, which StatusAsync would renew.
        h.SetClock(Now + 29 * Day);
        var writes = h.Writes;
        Assert.True(await h.License.HasAsync("cloud"));
        Assert.Empty(h.Http.Calls);
        Assert.Equal(writes, h.Writes);
    }

    [Fact]
    public async Task HasAsync_grants_nothing_on_a_lease_that_does_not_license_this_build_nor_with_no_licence_nor_unreadable_storage()
    {
        var h = LicensedWith(Ent());
        h.SetClock(Now + 31 * Day);
        Assert.False(await h.License.HasAsync("cloud"));
        h.Stored = null;
        Assert.False(await h.License.HasAsync("cloud"));
        var unreadable = LicensedWith(Ent());
        unreadable.FailReads();
        Assert.False(await unreadable.License.HasAsync("cloud"));
    }

    [Fact]
    public async Task HasAsync_grants_nothing_over_a_stored_refusal_though_the_lease_it_kept_still_verifies()
    {
        var h = Create();
        var kept = h.SignLease(new LeaseInput { Sub = Key, Ent = Ent() });
        // The server refused this build's major: the lease is kept for builds it covers.
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = kept, CheckedMajor = 1, Refused = Refusal.Upgrade, RefusedMajor = 1 };
        Assert.False(await h.License.HasAsync("cloud"));
        // Revoked: the refusal stands over any lease still held.
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = kept, CheckedMajor = 1, Refused = Refusal.Revoked };
        Assert.False(await h.License.HasAsync("cloud"));
    }

    [Fact]
    public async Task HasAsync_grants_during_a_running_trial_what_its_trial_lease_signs_and_asks_nothing()
    {
        var h = Create();
        var claim = Hashes.FingerprintClaim("fp-test");
        var lease = h.SignLease(new LeaseInput
        {
            Sub = $"trial:{claim}", Aid = claim, Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = 5 * 24 * 3600,
            Ent = new JsonObject { ["cloud"] = true },
        });
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialEndsAt = Now + 5 * Day, TrialLease = lease, LastSeen = Now };
        Assert.True(await h.License.HasAsync("cloud"));
        Assert.False(await h.License.HasAsync("export"));
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task RequireAsync_completes_for_a_granted_code_and_throws_naming_the_code_otherwise()
    {
        var h = LicensedWith(Ent());
        await h.License.RequireAsync("cloud");
        var refused = await Assert.ThrowsAsync<MissingEntitlementException>(() => h.License.RequireAsync("seats"));
        Assert.Equal("seats", refused.Entitlement);
    }
}
