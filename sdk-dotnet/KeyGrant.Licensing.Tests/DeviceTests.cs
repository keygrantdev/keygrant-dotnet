using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>A lease held to its key, its activation and its device; and who this device is, when it cannot be told.</summary>
public class DeviceTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private static readonly string Machine = DeviceFingerprints.Hash($"machine-id|{FakeMachineIdSource.TestGuid}");
    private static readonly string HostnameFp = DeviceFingerprints.Hostname();
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };
    private static readonly LicenseStatus DeviceRefused = new() { State = LicenseState.Invalid, Reason = LicenseReason.Device, Key = Key };

    /// <summary>A 30-day licence lease for this key and activation, its seat keyed (<paramref name="kind"/>) by <paramref name="device"/>; none when null.</summary>
    private static string LeaseFor(Harness h, string? device = "fp-test", string kind = "machine", string sub = Key, string aid = "act_1") =>
        h.SignLease(new LeaseInput
        {
            Sub = sub,
            Aid = aid,
            Fp = device is null ? null : Hashes.FingerprintClaim(device),
            Fpk = device is null ? null : kind,
        });

    private static StoredState Holding(string lease) => new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 1, LastSeen = Now };

    private static (Harness H, ManualTime Time) OnMachine(FakeMachineIdSource source, bool? deviceHold = null)
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Fingerprint = new MachineFingerprinter(source, time), Time = time, DeviceHold = deviceHold });
        return (h, time);
    }

    private static FuncFingerprinter Failing() => new(() => throw new InvalidOperationException("fingerprint adapter failed"));

    // --- a lease held to its key, its activation and its device -------------------

    [Fact]
    public async Task Licenses_this_device_on_its_own_lease_offline()
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(0, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Takes_a_lease_with_no_device_claim()
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h, device: null));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Matches_the_key_without_regard_to_case()
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h, sub: "Sluice-aaaa-BBBB-CCCC-DDDD"));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    public static TheoryData<string, string, string, string, bool> Strangers => new()
    {
        { "another key", "SLUICE-OTHR-OTHR-OTHR-OTHR", "act_1", "fp-test", false },
        { "another activation", Key, "act_2", "fp-test", false },
        { "another device", Key, "act_1", "fp-another-machine", true },
    };

    [Theory]
    [MemberData(nameof(Strangers))]
    public async Task Refuses_offline_a_lease_for_another_licence_or_device_and_asks_under_the_usual_waits(string _, string sub, string aid, string device, bool keyed)
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h, device, sub: sub, aid: aid));
        h.Http.Fail = true;
        var refused = keyed ? DeviceRefused : new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Tampered };
        Assert.Equal(refused, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(refused, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Stays_refused_for_another_device_even_online()
    {
        var h = Create();
        var theirs = LeaseFor(h, "fp-another-machine");
        h.Stored = Holding(theirs);
        h.Http.Lease("validate", theirs);
        Assert.Equal(DeviceRefused, await h.License.StatusAsync());
        Assert.Equal(DeviceRefused, await h.License.RefreshAsync());
    }

    [Fact]
    public async Task Says_device_offline_too_once_a_lease_with_no_device_claim_is_renewed_with_another_device_s()
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h, device: null));
        h.Http.Lease("validate", LeaseFor(h, "fp-another-machine"));
        Assert.Equal(DeviceRefused, await h.License.RefreshAsync());
        h.Http.Fail = true;
        Assert.Equal(DeviceRefused, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Accepts_the_lease_an_activation_of_this_device_is_given_and_refuses_one_for_another_device()
    {
        var h = Create();
        h.Http.Lease("activate", LeaseFor(h));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());

        var other = Create();
        other.Http.Lease("activate", LeaseFor(other, "fp-another-machine"));
        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidLease), await other.License.ActivateAsync(Key));
    }

    // --- who this device is, when it cannot be told -------------------------------

    [Fact]
    public async Task Does_not_call_a_slow_first_read_another_device_no_flip_on_a_normal_run_then_the_id()
    {
        var source = FakeMachineIdSource.SlowRegistry(1);
        var (first, firstTime) = OnMachine(source);
        first.Stored = Holding(LeaseFor(first, Machine));
        first.Http.Fail = true;
        // The read outlives its time box: this run cannot tell, and does not judge.
        Assert.Equal(Licensed, await Eventually.Settle(first.License.StatusAsync(), firstTime));

        var (second, secondTime) = OnMachine(source);
        second.Stored = Holding(LeaseFor(second, Machine));
        second.Http.Fail = true;
        Assert.Equal(Licensed, await Eventually.Settle(second.License.StatusAsync(), secondTime));
    }

    [Fact]
    public async Task Waits_the_full_activation_bound_on_a_read_that_does_not_finish_not_the_2_s_a_status_gets()
    {
        var (h, time) = OnMachine(FakeMachineIdSource.SlowRegistry(1));
        var activating = h.License.ActivateAsync(Key);
        // The registry read is bounded at the activation's 10 s, plus the half second the reference allows.
        await Eventually.Until(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs + 500)), "the bounded read");
        time.Advance(TimeSpan.FromMilliseconds(MachineId.TimeoutMs + 1_500));
        await Eventually.Quiet();
        Assert.False(activating.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs - 1_000 - (MachineId.TimeoutMs + 1_500)));
        await Eventually.Quiet();
        Assert.False(activating.IsCompleted);
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Activates_under_the_host_name_when_the_machine_id_never_reads_in_time_and_moves_to_the_id_once_it_does()
    {
        var (h, time) = OnMachine(FakeMachineIdSource.SlowRegistry(1));
        h.Http.Lease("activate", LeaseFor(h, HostnameFp, "hostname"));
        Assert.Equal(ActivateResult.Success, await Eventually.Settle(h.License.ActivateAsync(Key), time));
        Assert.Equal(HostnameFp, h.Http.BodyOf("activate")["fingerprint"]!.GetValue<string>());
        Assert.Equal("hostname", h.Http.BodyOf("activate")["fingerprintKind"]!.GetValue<string>());
        Assert.Equal(DeviceKind.Hostname, h.Stored!.DeviceKind);

        h.Http.Lease("validate", LeaseFor(h, Machine));
        Assert.Equal(Licensed, await Eventually.Settle(h.License.RefreshAsync(), time));
        var body = h.Http.BodyOf("validate");
        Assert.Equal([Machine, HostnameFp], body["fingerprints"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("machine", body["fingerprintKind"]!.GetValue<string>());
        Assert.Equal(DeviceKind.Machine, h.Stored!.DeviceKind);
    }

    [Fact]
    public async Task Never_re_activates_the_seat_a_machine_id_keyed_under_the_host_name()
    {
        var (h, time) = OnMachine(FakeMachineIdSource.SlowRegistry(1));
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", DeviceKind = DeviceKind.Machine, LastSeen = Now };
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await Eventually.Settle(h.License.ActivateAsync(Key), time));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Keeps_to_the_seat_whatever_case_its_key_was_stored_in()
    {
        var (h, time) = OnMachine(FakeMachineIdSource.SlowRegistry(1));
        h.Stored = new StoredState { Key = Key.ToLowerInvariant(), ActivationId = "act_1", DeviceKind = DeviceKind.Machine, LastSeen = Now };
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await Eventually.Settle(h.License.ActivateAsync(Key), time));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Takes_a_lease_from_an_activation_under_the_host_name_fingerprint_and_renews_it_telling_both()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.SlowRegistry(0));
        var legacy = LeaseFor(h, HostnameFp, "hostname");
        h.Stored = Holding(legacy);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        h.Http.Fail = false;
        h.Http.Lease("validate", legacy);
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.Equal([Machine, HostnameFp], h.Http.BodyOf("validate")["fingerprints"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Does_not_judge_the_device_nor_tell_the_server_who_it_is_on_a_run_that_cannot_tell()
    {
        var failing = Failing();
        var h = Create(null, new Options { Fingerprint = failing });
        var elsewhere = LeaseFor(h, "fp-another-machine");
        h.Stored = Holding(elsewhere);
        Assert.Equal(Licensed, await h.License.StatusAsync());
        h.Http.Lease("validate", elsewhere);
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.False(h.Http.BodyOf("validate").ContainsKey("fingerprints"));
        // Not kept: every call asks the adapter again.
        Assert.True(failing.Calls > 1);
    }

    [Fact]
    public async Task Reads_again_after_an_adapter_that_failed_and_judges_the_device_once_it_can_tell()
    {
        var failing = true;
        var h = Create(null, new Options { Fingerprint = new FuncFingerprinter(() => failing ? throw new InvalidOperationException("not yet") : Task.FromResult("fp-test")) });
        h.Stored = Holding(LeaseFor(h, "fp-another-machine"));
        h.Http.Fail = true;
        Assert.Equal(LicenseState.Licensed, (await h.License.StatusAsync()).State);
        failing = false;
        Assert.Equal(DeviceRefused, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Bounds_an_adapter_that_never_answers_and_does_not_keep_that()
    {
        var time = new ManualTime();
        var hanging = new FuncFingerprinter(() => new TaskCompletionSource<string>().Task);
        var h = Create(null, new Options { Fingerprint = hanging, Time = time });
        h.Stored = Holding(LeaseFor(h));
        h.Http.Fail = true;
        var pending = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(MachineId.TimeoutMs)), "the bounded fingerprint");
        time.Advance(TimeSpan.FromMilliseconds(MachineId.TimeoutMs - 1));
        await Eventually.Quiet();
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Licensed, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await Eventually.Settle(h.License.StatusAsync(), time);
        Assert.Equal(2, hanging.Calls);
    }

    [Fact]
    public async Task Takes_the_server_s_device_refusal_keeping_the_lease_refused_for_this_device_only()
    {
        var h = Create();
        h.Stored = Holding(LeaseFor(h));
        h.Http.Error("validate", 403, "device");
        Assert.Equal(DeviceRefused, await h.License.RefreshAsync());
        var body = h.Http.BodyOf("validate");
        Assert.Equal(["fp-test"], body["fingerprints"]!.AsArray().Select(n => n!.GetValue<string>()));
        // A GetAsync-only fingerprinter says nothing of what it hashes: no kind is claimed for it.
        Assert.False(body.ContainsKey("fingerprintKind"));
        Assert.Equal(Refusal.Device, h.Stored!.Refused);
        Assert.Equal([Hashes.FingerprintClaim("fp-test")], h.Stored.RefusedFor!);
        Assert.NotNull(h.Stored.Lease);
        h.Http.Fail = true;
        Assert.Equal(DeviceRefused, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Licenses_again_at_once_when_the_fingerprint_flips_back_after_a_device_refusal()
    {
        var fingerprint = "fp-flipped";
        var flipping = new FuncFingerprinter(() => Task.FromResult(fingerprint));
        var h = Create(null, new Options { Fingerprint = flipping });
        h.Stored = Holding(LeaseFor(h));
        h.Http.Error("validate", 403, "device");
        Assert.Equal(LicenseReason.Device, (await h.License.RefreshAsync()).Reason);
        fingerprint = "fp-test";
        var next = h.LicenseFor(1, flipping);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await next.StatusAsync());
    }

    [Fact]
    public async Task Does_not_refuse_a_machine_keyed_install_whose_id_stops_reading_and_keeps_its_lease()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.BlockedRegistry());
        var lease = LeaseFor(h, Machine);
        h.Stored = Holding(lease) with { DeviceKind = DeviceKind.Machine };
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        h.Http.Fail = false;
        h.Http.Lease("validate", lease);
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.False(h.Http.BodyOf("validate").ContainsKey("fingerprints"));
        Assert.Equal((lease, DeviceKind.Machine), (h.Stored!.Lease, h.Stored.DeviceKind));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Does_not_refuse_a_host_name_keyed_seat_renamed(bool idReadsNow)
    {
        var (h, _) = OnMachine(idReadsNow ? FakeMachineIdSource.SlowRegistry(0) : FakeMachineIdSource.BlockedRegistry());
        h.Stored = Holding(LeaseFor(h, DeviceFingerprints.Hash("old-name|darwin|x64"), "hostname"));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Does_not_judge_a_lease_whose_seat_s_kind_is_unknown()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.SlowRegistry(0));
        h.Stored = Holding(h.SignLease(new LeaseInput { Sub = Key, Fp = Hashes.FingerprintClaim(DeviceFingerprints.Hash("old-name|darwin|x64")) }));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Holds_nothing_to_its_device_with_deviceHold_false()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.SlowRegistry(0), deviceHold: false);
        h.Http.Lease("activate", LeaseFor(h, Machine));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal(Machine, h.Http.BodyOf("activate")["fingerprint"]!.GetValue<string>());
        Assert.False(h.Http.BodyOf("activate").ContainsKey("fingerprintKind"));
        Assert.Null(h.Stored!.DeviceKind);

        var elsewhere = LeaseFor(h, "fp-another-machine");
        h.Stored = Holding(elsewhere);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        h.Http.Fail = false;
        h.Http.Lease("validate", elsewhere);
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.False(h.Http.BodyOf("validate").ContainsKey("fingerprints"));
        Assert.False(h.Http.BodyOf("validate").ContainsKey("fingerprintKind"));
    }

    [Fact]
    public async Task Activates_under_the_host_name_with_deviceHold_false_on_an_install_a_machine_id_once_keyed()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.BlockedRegistry(), deviceHold: false);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", DeviceKind = DeviceKind.Machine, LastSeen = Now };
        h.Http.Lease("activate", LeaseFor(h, Machine));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal(HostnameFp, h.Http.BodyOf("activate")["fingerprint"]!.GetValue<string>());
        Assert.False(h.Http.BodyOf("activate").ContainsKey("fingerprintKind"));
        Assert.Null(h.Stored!.DeviceKind);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task Clears_a_stored_machine_kind_when_a_fingerprint_of_unknown_kind_activates_the_seat()
    {
        var h = Create(new StoredState { Key = Key, ActivationId = "act_1", DeviceKind = DeviceKind.Machine, LastSeen = Now });
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Null(h.Stored!.DeviceKind);
    }

    [Fact]
    public async Task Activates_under_the_host_name_after_a_deactivate_frees_a_machine_install_whose_id_no_longer_reads()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.BlockedRegistry());
        h.Stored = Holding(LeaseFor(h, Machine)) with { DeviceKind = DeviceKind.Machine };
        h.Http.Answer("deactivate", 200, new JsonObject { ["ok"] = true });
        await h.License.DeactivateAsync();
        Assert.Null(h.Stored!.DeviceKind);
        h.Http.Lease("activate", LeaseFor(h, HostnameFp, "hostname"));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal(HostnameFp, h.Http.BodyOf("activate")["fingerprint"]!.GetValue<string>());
        Assert.Equal("hostname", h.Http.BodyOf("activate")["fingerprintKind"]!.GetValue<string>());
        Assert.Equal(DeviceKind.Hostname, h.Stored!.DeviceKind);
    }

    [Fact]
    public async Task Activates_another_key_under_the_host_name_on_a_machine_install_whose_id_no_longer_reads()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.BlockedRegistry());
        h.Stored = new StoredState { Key = "SLUICE-OLDK-OLDK-OLDK-OLDK", ActivationId = "act_1", DeviceKind = DeviceKind.Machine, LastSeen = Now };
        h.Http.Lease("activate", LeaseFor(h, HostnameFp, "hostname"));
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        Assert.Equal(HostnameFp, h.Http.BodyOf("activate")["fingerprint"]!.GetValue<string>());
        Assert.Equal("hostname", h.Http.BodyOf("activate")["fingerprintKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Does_not_stand_a_device_refusal_on_a_later_run_that_only_shares_its_host_name_alternate()
    {
        var own = "fp-boot-1";
        var booting = new FuncFingerprinter(
            () => Task.FromResult(own),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity(own, DeviceKind.Machine, ["fp-same-host"])));
        var h = Create(null, new Options { Fingerprint = booting });
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Error("validate", 403, "device");
        Assert.Equal(LicenseReason.Device, (await h.License.RefreshAsync()).Reason);
        Assert.Equal([Hashes.FingerprintClaim("fp-boot-1")], h.Stored!.RefusedFor!);
        own = "fp-boot-2";
        var next = h.LicenseFor(1, booting);
        h.Http.Fail = true;
        Assert.Equal(Licensed, await next.StatusAsync());
    }

    [Fact]
    public async Task Takes_a_trial_lease_started_under_the_host_name_it_answers_to_besides_its_machine_id()
    {
        var (h, _) = OnMachine(FakeMachineIdSource.SlowRegistry(0));
        var claim = Hashes.FingerprintClaim(HostnameFp);
        var trialLease = h.SignLease(new LeaseInput { Sub = $"trial:{claim}", Aid = claim, Fpk = "machine", Model = "trial", Lim = 1, Major = 1_000_000, TtlSeconds = 7 * 24 * 3600 });
        h.Stored = new StoredState { TrialStart = Now - 2 * Day, TrialLease = trialLease, LastSeen = Now };
        Assert.Equal(LicenseState.Trial, (await h.License.StatusAsync()).State);
    }
}
