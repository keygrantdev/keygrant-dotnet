using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

public class StatusTests
{
    private static LicenseStatus Licensed(string key) => new() { State = LicenseState.Licensed, Key = key };

    private static LicenseStatus Invalid(LicenseReason reason, string? key) => new() { State = LicenseState.Invalid, Reason = reason, Key = key };

    private static readonly LicenseStatus ExpiredOffline = new() { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = "KEY" };

    /// <summary>A device holding <paramref name="lease"/> for KEY / act_1.</summary>
    private static StoredState Holding(string lease) => new() { Key = "KEY", ActivationId = "act_1", Lease = lease, LastSeen = Now };

    [Fact]
    public async Task Is_unlicensed_with_no_state()
    {
        var h = Create();
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Is_licensed_from_a_valid_lease_without_contacting_the_server()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        Assert.Equal(Licensed("KEY"), await h.License.StatusAsync());
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Renews_online_when_the_lease_has_expired()
    {
        var h = Create();
        var fresh = h.Sign("KEY", "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Lease("validate", fresh);
        Assert.Equal(Licensed("KEY"), await h.License.StatusAsync());
        Assert.True(h.Http.Called("validate"));
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Fact]
    public async Task Stays_expired_offline_when_an_expired_lease_cannot_be_renewed()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Fail = true;
        Assert.Equal(ExpiredOffline, await h.License.StatusAsync());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("deactivated")]
    public async Task Locks_out_when_renewal_returns_a_definitive_key_refusal(string error)
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Error("validate", 403, error);
        Assert.Equal(Invalid(LicenseReason.Revoked, "KEY"), await h.License.StatusAsync());
    }

    [Fact]
    public async Task Reports_upgrade_when_renewal_says_upgrade_required()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Error("validate", 403, "upgrade-required");
        Assert.Equal(Invalid(LicenseReason.Upgrade, "KEY"), await h.License.StatusAsync());
    }

    [Theory]
    [InlineData("garbage.lease.value")]
    [InlineData(null)]
    public async Task Falls_back_to_expired_offline_when_the_renewal_lease_is_unusable(string? lease)
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Answer("validate", 200, lease is null ? new JsonObject() : new JsonObject { ["lease"] = lease });
        Assert.Equal(ExpiredOffline, await h.License.StatusAsync());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(401)]
    public async Task Treats_an_outage_a_rate_limit_or_an_ambiguous_4xx_on_an_expired_lease_as_no_verdict(int status)
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Error("validate", status, "whatever");
        Assert.Equal(ExpiredOffline, await h.License.StatusAsync());
        Assert.Null(h.Stored!.Refused);
    }

    [Fact]
    public async Task Reports_upgrade_when_the_renewal_lease_covers_a_lower_major_and_keeps_it()
    {
        var h = Create(2);
        var freshMajor1 = h.Sign("KEY", "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Lease("validate", freshMajor1);
        Assert.Equal(Invalid(LicenseReason.Upgrade, "KEY"), await h.License.StatusAsync());
        Assert.Equal(freshMajor1, h.Stored!.Lease);
    }

    [Fact]
    public async Task Reports_tampered_for_a_lease_not_signed_by_the_product_key_when_the_server_is_unreachable()
    {
        var h = Create();
        h.Stored = Holding(new TestSigner().SignLease(new LeaseInput { Sub = "KEY" }, Now));
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Tampered, null), await h.License.StatusAsync());
    }

    [Fact]
    public async Task Lets_the_server_heal_a_locally_invalid_lease()
    {
        var h = Create();
        var fresh = h.Sign("KEY", "perpetual", 30 * 24 * 3600);
        h.Stored = Holding(new TestSigner().SignLease(new LeaseInput { Sub = "KEY" }, Now));
        h.Http.Lease("validate", fresh);
        Assert.Equal(Licensed("KEY"), await h.License.StatusAsync());
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Fact]
    public async Task Rejects_a_validly_signed_lease_issued_for_a_different_product()
    {
        var h = Create();
        h.Stored = Holding(h.SignLease(new LeaseInput { Sub = "KEY", Product = "other-app" }));
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Tampered, null), await h.License.StatusAsync());
    }

    [Fact]
    public async Task Reports_upgrade_offline_when_the_app_major_exceeds_the_lease()
    {
        var h = Create(2);
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Upgrade, "KEY"), await h.License.StatusAsync());
    }

    [Fact]
    public async Task Ignores_a_rolled_back_clock_which_cannot_resurrect_an_expired_lease()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day)) with { LastSeen = Now + 10 * Day };
        h.SetClock(Now - 5 * Day);
        h.Http.Fail = true;
        Assert.Equal(ExpiredOffline, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Advances_the_clock_guard_on_every_save()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Fail = true;
        h.SetClock(Now + 5_000);
        await h.License.StatusAsync();
        Assert.Equal(Now + 5_000, h.Stored!.LastSeen);
    }

    // --- refresh (forced online) ------------------------------------------------

    [Fact]
    public async Task Refresh_renews_a_still_valid_lease()
    {
        var h = Create();
        var fresh = h.Sign("KEY", "perpetual", 30 * 24 * 3600, Now + 1000);
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        h.Http.Lease("validate", fresh);
        Assert.Equal(Licensed("KEY"), await h.License.RefreshAsync());
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(429)]
    [InlineData(404)]
    public async Task Refresh_keeps_a_valid_lease_licensed_when_it_gets_no_verdict(int status)
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        if (status == 0) h.Http.Fail = true;
        else h.Http.Error("validate", status, "unknown-product");
        Assert.Equal(Licensed("KEY"), await h.License.RefreshAsync());
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Sends_exactly_the_reference_s_validate_body()
    {
        var h = Create(new StoredState { Key = "KEY", ActivationId = "act_1", KeptMajor = 4, LastSeen = Now }, new Options { Major = 3 });
        h.Stored = h.Stored! with { Lease = h.SignLease(new LeaseInput { Sub = "KEY", Major = 1_000_000 }) };
        h.Http.Fail = true;
        await h.License.RefreshAsync();
        AssertJson.Equal(
            new JsonObject { ["key"] = "KEY", ["activationId"] = "act_1", ["major"] = 3, ["keptMajor"] = 4, ["fingerprints"] = new JsonArray("fp-test"), ["kids"] = new JsonArray(h.Signer.Kid) },
            h.Http.BodyOf("validate"));
    }

    // --- deactivate -------------------------------------------------------------

    [Fact]
    public async Task Deactivate_releases_the_activation_and_clears_local_state_but_trial_evidence()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600)) with
        {
            TrialStart = Now - Day,
            TrialEndsAt = Now - 1,
            TrialLease = "T",
            TrialAt = Now - Day,
            CheckedMajor = 1,
            KeptMajor = 2,
            DeviceKind = DeviceKind.Machine,
            Refused = Refusal.Upgrade,
            RefusedMajor = 2,
            BackoffSince = Now,
            BackoffMs = 60_000,
        };
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        await h.License.DeactivateAsync();
        AssertJson.Equal(new JsonObject { ["key"] = "KEY", ["activationId"] = "act_1" }, h.Http.BodyOf("deactivate"));
        Assert.Equal(
            new StoredState { TrialStart = Now - Day, TrialEndsAt = Now - 1, TrialLease = "T", TrialAt = Now - Day, LastSeen = Now, VerdictAt = Now },
            h.Stored);
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Deactivate_says_the_seat_is_released_when_KeyGrant_answers_200()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.Null(h.Stored!.Key);
    }

    [Fact]
    public async Task Deactivate_says_the_seat_is_not_released_when_KeyGrant_could_not_be_asked_and_still_clears()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        h.Http.Fail = true;
        Assert.Equal(new DeactivateResult(Released: false), await h.License.DeactivateAsync());
        Assert.Equal(1, h.Http.Count("deactivate"));
        Assert.Null(h.Stored!.Key);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
    }

    [Theory]
    [InlineData(500, "{\"error\":\"internal\"}")]
    [InlineData(503, null)]
    [InlineData(429, "{\"error\":\"rate-limited\"}")]
    [InlineData(404, "{\"error\":\"unknown-product\"}")]
    [InlineData(403, null)]
    [InlineData(201, "{\"ok\":true}")]
    public async Task Deactivate_says_the_seat_is_not_released_on_any_answer_but_a_200_and_still_clears(int status, string? body)
    {
        // A 5xx, a 429, a proxy's page: an answer, but not KeyGrant saying the seat is free.
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        h.Http.On("deactivate", _ => new HttpResult(status, body is null ? null : JsonNode.Parse(body)));
        Assert.Equal(new DeactivateResult(Released: false), await h.License.DeactivateAsync());
        Assert.Null(h.Stored!.Key);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Deactivate_says_the_seat_is_released_when_none_was_held_asking_nothing()
    {
        var h = Create();
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.False(h.Http.Called("deactivate"));
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());

        // A key with no activation is no seat either.
        h.Stored = new StoredState { Key = "KEY", LastSeen = Now };
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.False(h.Http.Called("deactivate"));
    }

    // --- deactivate, when the activation stored is another device's -------------------
    // A profile copied or restored from another PC carries that PC's activation: releasing it would
    // lock that PC out at its next check-in.

    /// <summary>A 30-day licence lease held to <paramref name="device"/>'s machine id (none: held to no machine), signed at <paramref name="at"/>.</summary>
    private static string LeaseFor(Harness h, string? device = "fp-test", string kind = "machine", long at = Now) =>
        h.SignLease(new LeaseInput
        {
            Sub = "KEY",
            Fp = device is null ? null : Internal.Hashes.FingerprintClaim(device),
            Fpk = device is null ? null : kind,
            TtlSeconds = 30 * 24 * 3600,
        }, at);

    private static Harness Deactivating(Func<Harness, StoredState> stored, Options? options = null)
    {
        var h = Create(null, options);
        h.Stored = stored(h) with { CheckedMajor = 1 };
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        return h;
    }

    [Fact]
    public async Task Deactivate_drops_another_device_s_activation_here_without_releasing_its_seat()
    {
        var h = Deactivating(x => Holding(LeaseFor(x, "fp-other-pc")));
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.False(h.Http.Called("deactivate"));
        Assert.Null(h.Stored!.Key);
        Assert.Equal(Now, h.Stored.VerdictAt);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Deactivate_reads_whose_seat_it_is_from_a_lease_that_has_run_out_too()
    {
        var h = Deactivating(x => Holding(LeaseFor(x, "fp-other-pc", at: Now - 40 * Day)));
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.False(h.Http.Called("deactivate"));
        Assert.Null(h.Stored!.Key);
    }

    [Fact]
    public async Task Deactivate_takes_a_device_refusal_that_stands_for_this_device_as_the_same()
    {
        var h = Deactivating(x => Holding(LeaseFor(x)) with { Refused = Refusal.Device, RefusedFor = [Internal.Hashes.FingerprintClaim("fp-test")] });
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.False(h.Http.Called("deactivate"));
        Assert.Null(h.Stored!.Key);
    }

    public static TheoryData<string> OwnSeats => new() { "this device's own lease", "a lease held to no machine", "another device's lease, its seat keyed by the host name", "another device's lease under another key's signature", "another device's lease, DeviceHold off", "another device's lease, on a run that cannot tell" };

    [Theory]
    [MemberData(nameof(OwnSeats))]
    public async Task Deactivate_still_releases_a_seat_it_cannot_tell_is_another_device_s(string seat)
    {
        Options? options = seat switch
        {
            "another device's lease, DeviceHold off" => new Options { DeviceHold = false },
            "another device's lease, on a run that cannot tell" => new Options { Fingerprint = new FuncFingerprinter(() => throw new InvalidOperationException("cannot read it")) },
            _ => null,
        };
        var h = Deactivating(
            x => Holding(seat switch
            {
                "this device's own lease" => LeaseFor(x),
                "a lease held to no machine" => LeaseFor(x, null),
                "another device's lease, its seat keyed by the host name" => LeaseFor(x, "fp-other-pc", "hostname"),
                "another device's lease under another key's signature" => LeaseFor(Create(), "fp-other-pc"),
                _ => LeaseFor(x, "fp-other-pc"),
            }),
            options);
        Assert.Equal(new DeactivateResult(Released: true), await h.License.DeactivateAsync());
        Assert.True(h.Http.Called("deactivate"));
        Assert.Null(h.Stored!.Key);
    }

    [Fact]
    public async Task Deactivate_drops_nothing_when_the_caller_stops_waiting_while_the_device_is_read()
    {
        using var cancel = new CancellationTokenSource();
        var reading = new FuncFingerprinter(
            () => Task.FromResult("fp-test"),
            async () =>
            {
                cancel.Cancel();
                await Task.Yield();
                return new DeviceIdentity("fp-test", null, []);
            });
        var h = Deactivating(x => Holding(LeaseFor(x, "fp-other-pc")), new Options { Fingerprint = reading });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.DeactivateAsync(cancel.Token));
        Assert.Equal("KEY", h.Stored!.Key);
        Assert.False(h.Http.Called("deactivate"));
    }

    // --- test licences ----------------------------------------------------------

    private const string TestKey = "SLUICE-TEST-TEST-TEST-TEST";

    private static string TestLease(Harness h, long ttlSeconds = 30 * 24 * 3600, long now = Now) =>
        h.SignLease(new LeaseInput { Sub = TestKey, Test = true, TtlSeconds = ttlSeconds }, now);

    [Fact]
    public async Task A_test_licence_licenses_the_app_and_says_so()
    {
        var h = Create();
        h.Http.Lease("activate", TestLease(h));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(TestKey));
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = TestKey, Test = true }, await h.License.StatusAsync());
    }

    [Fact]
    public async Task A_real_licence_says_nothing_of_it()
    {
        var h = Create();
        h.Http.Lease("activate", h.Sign(TestKey, "perpetual", 30 * 24 * 3600));
        await h.License.ActivateAsync(TestKey);
        Assert.False((await h.License.StatusAsync()).Test);
    }

    [Fact]
    public async Task A_test_licence_keeps_saying_so_after_a_renewal_and_when_a_refresh_cannot_reach_the_server()
    {
        var h = Create();
        h.Http.Lease("activate", TestLease(h, 60));
        await h.License.ActivateAsync(TestKey);
        var later = Now + 2 * 3_600_000;
        h.SetClock(later);
        h.Http.Answer("validate", 200, new JsonObject { ["lease"] = TestLease(h, 30 * 24 * 3600, later) });
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = TestKey, Test = true }, await h.License.StatusAsync());
        Assert.True(h.Http.Called("validate"));

        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = TestKey, Test = true }, await h.License.RefreshAsync());
        Assert.Equal(["activate", "validate", "validate"], h.Http.Calls.Select(c => c.Action));
    }

    // --- a key for a range of versions ------------------------------------------

    private static string RangeLease(Harness h, long major, long? minMajor = null) =>
        h.SignLease(new LeaseInput { Sub = "KEY", Major = major, MinMajor = minMajor });

    [Fact]
    public async Task Reports_outdated_offline_for_a_build_older_than_the_key_covers()
    {
        var h = Create(1);
        h.Stored = Holding(RangeLease(h, 3, 2));
        h.Http.Fail = true;
        Assert.Equal(Invalid(LicenseReason.Outdated, "KEY"), await h.License.StatusAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Licenses_every_build_inside_the_range(int major)
    {
        var h = Create(major);
        h.Stored = Holding(RangeLease(h, 3, 2));
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Reads_a_lease_with_no_lowest_major_as_starting_at_1_and_runs_any_later_build_on_an_open_ended_key()
    {
        var h = Create(1);
        h.Stored = Holding(RangeLease(h, 1));
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);

        var open = Create(41);
        open.Stored = Holding(RangeLease(open, 1_000_000, 2)) with { CheckedMajor = 41 };
        Assert.Equal(LicenseState.Licensed, (await open.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Reports_outdated_when_renewal_says_update_required()
    {
        var h = Create();
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Error("validate", 403, "update-required");
        Assert.Equal(Invalid(LicenseReason.Outdated, "KEY"), await h.License.StatusAsync());
    }

    [Fact]
    public async Task Keeps_a_renewed_lease_that_starts_above_this_build_and_reports_outdated()
    {
        var h = Create(1);
        var fresh = RangeLease(h, 2, 2);
        h.Stored = Holding(h.Sign("KEY", "perpetual", 3600, Now - 2 * Day));
        h.Http.Lease("validate", fresh);
        Assert.Equal(Invalid(LicenseReason.Outdated, "KEY"), await h.License.StatusAsync());
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    // --- upgradeUrl -------------------------------------------------------------

    [Fact]
    public async Task UpgradeUrl_names_this_device_s_activation_and_never_the_key()
    {
        var h = Create(new StoredState { Key = "SLUICE-AAAA-BBBB-CCCC-DDDD", ActivationId = "act_device1" });
        Assert.Equal("https://buy.stripe.com/test_abc?client_reference_id=act_device1", await h.License.UpgradeUrlAsync("https://buy.stripe.com/test_abc"));
        var replaced = await h.License.UpgradeUrlAsync("https://buy.stripe.com/test_abc?client_reference_id=x");
        Assert.DoesNotContain("SLUICE", replaced);
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task UpgradeUrl_keeps_the_link_s_own_parameters_and_replaces_a_client_reference_id_on_it()
    {
        var h = Create(new StoredState { Key = "KEY-1", ActivationId = "act_1" });
        var url = await h.License.UpgradeUrlAsync("https://buy.stripe.com/x?prefilled_email=jo%40example.com&client_reference_id=old&client_reference_id=older#top");
        Assert.Equal("https://buy.stripe.com/x?prefilled_email=jo%40example.com&client_reference_id=act_1#top", url);
    }

    [Fact]
    public async Task UpgradeUrl_is_null_on_a_device_holding_no_activated_licence()
    {
        foreach (var state in new[] { new StoredState { TrialStart = Now }, new StoredState { Key = "KEY-1" }, new StoredState { Key = "", ActivationId = "act_1" } })
        {
            var h = Create(state);
            Assert.Null(await h.License.UpgradeUrlAsync("https://buy.stripe.com/test_abc"));
        }
    }

    [Fact]
    public async Task UpgradeUrl_refuses_what_is_not_an_absolute_url()
    {
        var h = Create(new StoredState { Key = "KEY-1", ActivationId = "act_1" });
        await Assert.ThrowsAsync<ArgumentException>(() => h.License.UpgradeUrlAsync("buy/test_abc"));
    }

    // --- configuration ----------------------------------------------------------

    [Fact]
    public void Refuses_a_private_key_a_wrong_curve_or_a_short_key_at_construction()
    {
        var x = new TestSigner().PublicX;
        Assert.Throws<ArgumentException>(() => PublicJwk.Parse($"{{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"x\":\"{x}\",\"d\":\"{x}\"}}"));
        Assert.Throws<ArgumentException>(() => PublicJwk.Parse($"{{\"kty\":\"OKP\",\"crv\":\"X25519\",\"x\":\"{x}\"}}"));
        Assert.Throws<ArgumentException>(() => PublicJwk.Parse("{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"x\":\"AAAA\"}"));
        Assert.Throws<ArgumentException>(() => PublicJwk.Parse("not json"));
        Assert.Throws<ArgumentException>(() => new License(new LicenseConfig
        {
            Product = "p",
            ApiBaseUrl = "https://api.test",
            PublicJwk = new PublicJwk { X = "short" },
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2 * 24 * 3600 * 1000.0)]
    public void Refuses_a_request_bound_that_is_none_or_more_than_a_day(double ms)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new License(new LicenseConfig
        {
            Product = "p",
            ApiBaseUrl = "https://api.test",
            PublicJwk = new TestSigner().Jwk,
            HttpTimeout = TimeSpan.FromMilliseconds(ms),
        }));
    }

    [Fact]
    public void Refuses_a_configuration_with_no_product_or_api()
    {
        var jwk = new TestSigner().Jwk;
        Assert.Throws<ArgumentException>(() => new License(new LicenseConfig { Product = "", ApiBaseUrl = "https://api.test", PublicJwk = jwk }));
        Assert.Throws<ArgumentException>(() => new License(new LicenseConfig { Product = "p", ApiBaseUrl = "", PublicJwk = jwk }));
    }

    [Fact]
    public async Task Takes_the_public_key_as_its_JWK_JSON()
    {
        var h = Create();
        var license = new License(new LicenseConfig
        {
            Product = Product,
            ApiBaseUrl = "https://api.test",
            PublicJwk = $"{{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"x\":\"{h.Signer.PublicX}\"}}",
            Adapters = h.Adapters,
        });
        h.Stored = Holding(h.Sign("KEY", "perpetual", 30 * 24 * 3600));
        Assert.Equal(Licensed("KEY"), await license.StatusAsync());
    }

    [Fact]
    public async Task A_typed_key_whose_lease_model_is_trial_is_licensed_offline_and_never_reports_a_trial()
    {
        // A key issued under a trial model is a licence lease: judged as any other, never as the device's trial.
        var h = Create();
        h.Stored = new StoredState { Key = "KEY-1", ActivationId = "act_1", Lease = h.Sign("KEY-1", "trial", 30 * 24 * 3600), CheckedMajor = 1, LastSeen = Now };
        h.Http.Fail = true;
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = "KEY-1" }, await h.License.StatusAsync());
        await h.License.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Http.Calls);
        Assert.Null(h.Stored!.TrialLease);
    }
}
