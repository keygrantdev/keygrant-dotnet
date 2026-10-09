using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// <c>conformance/vectors.json</c>: what every KeyGrant SDK must answer for the same inputs, taken from
/// the reference (the Electron SDK). Every group is answered here from the vector's own JSON, and every
/// <c>expect</c> must come out the same: a vector this SDK cannot reproduce is a bug in this SDK.
/// </summary>
public partial class ConformanceTests
{
    private static readonly LeaseVerifier Verifier = new(PublicJwk.Parse(All["publicJwk"]!.ToJsonString()).KeyBytes());

    /// <summary>Every group the harness answers. A group the file has and this list does not fails <see cref="Answers_every_group_the_vectors_hold"/>.</summary>
    private static readonly string[] Answered =
    [
        "leases", "trials", "fingerprintClaims", "fingerprints", "machineFingerprints", "hostFingerprints", "kept", "renew", "asks",
        "standing", "trialStatuses", "refusalPatches", "leasePatches", "keyedBy", "merges", "guardedNow", "clockCorrections",
        "mapServerError", "refusalOf", "enteredKey", "trimDeviceName", "noAnswer", "parseLeaseBody", "parseTrialBody", "mountinfo",
        "machineIds", "renewals", "validLeaseAsks", "devices", "identities", "savedLastSeen", "leaseStatuses", "trialLeaseStatuses",
        "entitlementNumbers", "majors", "purchaseUrls", "purchaseTokenKept", "claimDue", "parseClaimBody", "claimOutcomes", "trialReportDue",
        "purchaseReferences", "claimBodies", "keyIds", "keySets", "grants",
    ];

    private static readonly string[] NotGroups = ["$comment", "conventions", "publicJwk", "constants"];

    public static TheoryData<int, string> LeaseCases => Cases("leases");
    public static TheoryData<int, string> TrialCases => Cases("trials");
    public static TheoryData<int, string> FingerprintClaimCases => Cases("fingerprintClaims");
    public static TheoryData<int, string> FingerprintCases => Cases("fingerprints");
    public static TheoryData<int, string> MachineFingerprintCases => Cases("machineFingerprints");
    public static TheoryData<int, string> HostFingerprintCases => Cases("hostFingerprints");
    public static TheoryData<int, string> KeptCases => Cases("kept");
    public static TheoryData<int, string> RenewCases => Cases("renew");
    public static TheoryData<int, string> AskCases => Cases("asks");
    public static TheoryData<int, string> StandingCases => Cases("standing");
    public static TheoryData<int, string> TrialStatusCases => Cases("trialStatuses");
    public static TheoryData<int, string> RefusalPatchCases => Cases("refusalPatches");
    public static TheoryData<int, string> LeasePatchCases => Cases("leasePatches");
    public static TheoryData<int, string> KeyedByCases => Cases("keyedBy");
    public static TheoryData<int, string> MergeCases => Cases("merges");
    public static TheoryData<int, string> GuardedNowCases => Cases("guardedNow");
    public static TheoryData<int, string> ClockCorrectionCases => Cases("clockCorrections");
    public static TheoryData<int, string> MapServerErrorCases => Cases("mapServerError");
    public static TheoryData<int, string> RefusalOfCases => Cases("refusalOf");
    public static TheoryData<int, string> EnteredKeyCases => Cases("enteredKey");
    public static TheoryData<int, string> TrimDeviceNameCases => Cases("trimDeviceName");
    public static TheoryData<int, string> NoAnswerCases => Cases("noAnswer");
    public static TheoryData<int, string> ParseLeaseBodyCases => Cases("parseLeaseBody");
    public static TheoryData<int, string> ParseTrialBodyCases => Cases("parseTrialBody");
    public static TheoryData<int, string> MountinfoCases => Cases("mountinfo");
    public static TheoryData<int, string> RenewalCases => Cases("renewals");
    public static TheoryData<int, string> ValidLeaseAskCases => Cases("validLeaseAsks");
    public static TheoryData<int, string> DeviceCases => Cases("devices");
    public static TheoryData<int, string> IdentityCases => Cases("identities");
    public static TheoryData<int, string> SavedLastSeenCases => Cases("savedLastSeen");
    public static TheoryData<int, string> LeaseStatusCases => Cases("leaseStatuses");
    public static TheoryData<int, string> TrialLeaseStatusCases => Cases("trialLeaseStatuses");
    public static TheoryData<int, string> EntitlementNumberCases => Cases("entitlementNumbers");

    /// <summary>
    /// The machine-id reads this SDK makes as the reference does: Linux (and any other platform's) files,
    /// and macOS's <c>ioreg</c> output. The Windows cases script <c>reg.exe</c>'s output, which this SDK
    /// never parses (it reads <c>MachineGuid</c> through the registry API, as the file's conventions
    /// allow); its own tests cover that reader's decisions.
    /// </summary>
    public static TheoryData<int, string> MachineIdCases => Cases("machineIds", v => Str(v["source"]!["platform"]) != "win32");

    [Fact]
    public void Are_found_at_the_repo_root_and_signed_with_the_test_key()
    {
        Assert.EndsWith("vectors.json", PathOnDisk);
        Assert.Equal("OKP", Str(All["publicJwk"]!["kty"]));
        Assert.Equal("Ed25519", Str(All["publicJwk"]!["crv"]));
    }

    [Fact]
    public void Answers_every_group_the_vectors_hold()
    {
        var groups = All.Select(p => p.Key).Where(k => !NotGroups.Contains(k)).Order(StringComparer.Ordinal);
        Assert.Equal(Answered.Order(StringComparer.Ordinal), groups);
    }

    [Fact]
    public void Replays_every_machine_id_case_but_the_Windows_reg_exe_ones()
    {
        var all = Group("machineIds").Count;
        var windows = Group("machineIds").Count(v => Str(v!["source"]!["platform"]) == "win32");
        Assert.Equal(all - windows, MachineIdCases.Count());
        Assert.True(all - windows > 0);
    }

    [Fact]
    public void Cover_every_kind_of_verdict_the_offline_check_can_give()
    {
        var reasons = Group("leases")
            .Select(l => Bool(l!["expect"]!["ok"]) == true ? "ok" : Str(l!["expect"]!["reason"])!)
            .Distinct()
            .Order(StringComparer.Ordinal);
        Assert.Equal(["device", "expired", "ok", "outdated", "tampered", "unknown-key", "upgrade"], reasons);
    }

    [Fact]
    public void Constants_are_the_reference_s()
    {
        var c = All["constants"]!;
        Assert.Equal(Offline.OpenMajor, Long(c["OPEN_MAJOR"]));
        Assert.Equal(Offline.AskAgainMs, Long(c["ASK_AGAIN_MS"]));
        Assert.Equal(Offline.RetryUnansweredMs, Long(c["RETRY_UNANSWERED_MS"]));
        Assert.Equal(Offline.RenewWithinMs, Long(c["RENEW_WITHIN_MS"]));
        Assert.Equal(Offline.ClockToleranceMs, Long(c["CLOCK_TOLERANCE_MS"]));
        Assert.Equal(Wire.StatusCheckTimeoutMs, Long(c["STATUS_CHECK_TIMEOUT_MS"]));
        Assert.Equal((long)LicenseConfig.DefaultHttpTimeout.TotalMilliseconds, Long(c["DEFAULT_HTTP_TIMEOUT_MS"]));
        Assert.Equal((long)License.LockGrace.TotalMilliseconds, Long(c["LOCK_GRACE_MS"]));
        Assert.Equal(MachineId.TimeoutMs, Long(c["MACHINE_ID_TIMEOUT_MS"]));
        Assert.Equal(MachineId.ActivateTimeoutMs, Long(c["ACTIVATE_MACHINE_ID_TIMEOUT_MS"]));
        Assert.Equal(MachineId.BootSlackMs, Long(c["BOOT_SLACK_MS"]));
        Assert.Equal(Wire.MaxDeviceName, Long(c["MAX_DEVICE_NAME"]));
        Assert.Equal(Wire.MaxKey, Long(c["MAX_KEY"]));
        Assert.Equal(FileStorage.KeepCorrupt, Long(c["KEEP_CORRUPT"]));
        Assert.Equal(FileStorage.StaleTempMs, Long(c["STALE_TEMP_MS"]));
        Assert.Equal(FileStorage.UpdateAttempts, Long(c["UPDATE_ATTEMPTS"]));
        Assert.Equal(DeviceFingerprints.UnknownHost, Str(c["UNKNOWN_HOST"]));
        Assert.Equal(Offline.ClaimWaitMs, Long(c["CLAIM_WAIT_MS"]));
        Assert.Equal(Offline.ClaimEagerMs, Long(c["CLAIM_EAGER_MS"]));
        Assert.Equal(Offline.PurchaseTokenMs, Long(c["PURCHASE_TOKEN_MS"]));
        Assert.Equal(Offline.TrialReportMs, Long(c["TRIAL_REPORT_MS"]));
        Assert.Equal(Pickup.PurchaseReferencePrefix, Str(c["PURCHASE_REFERENCE_PREFIX"]));
        Assert.Equal(Pickup.MaxClaimFingerprints, Long(c["MAX_CLAIM_FINGERPRINTS"]));
        Assert.Equal(Pickup.MaxClaimFingerprintLength, Long(c["MAX_CLAIM_FINGERPRINT_LENGTH"]));
    }

    [Theory]
    [MemberData(nameof(LeaseCases))]
    public void Leases_verify_and_check_as_the_reference(int index, string name)
    {
        var v = Item("leases", index);
        var verified = Verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value);

        var verify = new JsonObject { ["valid"] = verified.Valid };
        if (!verified.Valid) verify["reason"] = VerifyResult.NameOf(verified.Reason!.Value);
        if (verified.Claims is { } claims) verify["claims"] = ClaimsJson(claims);
        AssertJson.Equal(v["verify"], verify, $"{name}: verify");

        var check = Offline.CheckLease(verified, Str(v["product"])!, Long(v["major"])!.Value, HolderOf(v["holder"]!));
        var expect = check.Ok
            ? new JsonObject { ["ok"] = true, ["test"] = check.Test }
            : new JsonObject { ["ok"] = false, ["reason"] = ReasonName(check.Reason!.Value) };
        AssertJson.Equal(v["expect"], expect, $"{name}: expect");
    }

    /// <summary>A check's reason as the vectors write it: <c>unknown-key</c> for <see cref="LeaseCheckFailure.UnknownKey"/>.</summary>
    private static string ReasonName(LeaseCheckFailure reason) =>
        reason == LeaseCheckFailure.UnknownKey ? "unknown-key" : reason.ToString().ToLowerInvariant();

    /// <summary>Whose a lease must be, as a vector gives it: the key and activation stored, and the device's claims.</summary>
    private static LeaseHolder HolderOf(JsonNode h) => new(Str(h["key"])!, Str(h["activationId"])!, Strings(h["claims"]), Bool(h["canTell"])!.Value);

    [Theory]
    [MemberData(nameof(TrialCases))]
    public void Trial_leases_as_the_reference(int index, string name)
    {
        var v = Item("trials", index);
        var verified = Verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value);
        var device = v["device"]!;
        var valid = Offline.TrialLeaseValid(verified, Str(v["product"])!, Strings(device["claims"]), Bool(device["canTell"])!.Value);
        Assert.True(Bool(v["expect"]) == valid, name);
    }

    [Theory]
    [MemberData(nameof(FingerprintClaimCases))]
    public void Fingerprint_claims_as_the_reference(int index, string _)
    {
        var v = Item("fingerprintClaims", index);
        Assert.Equal(Str(v["claim"]), Hashes.FingerprintClaim(Str(v["input"])!));
    }

    [Theory]
    [MemberData(nameof(FingerprintCases))]
    public void Fingerprints_as_the_reference(int index, string _)
    {
        var v = Item("fingerprints", index);
        Assert.Equal(Str(v["fingerprint"]), DeviceFingerprints.Hash(Str(v["raw"])!));
    }

    [Theory]
    [MemberData(nameof(MachineFingerprintCases))]
    public void Machine_fingerprints_as_the_reference(int index, string _)
    {
        var v = Item("machineFingerprints", index);
        Assert.Equal(Str(v["fingerprint"]), DeviceFingerprints.Machine(Str(v["id"])!));
    }

    [Theory]
    [MemberData(nameof(HostFingerprintCases))]
    public void Host_fingerprints_as_the_reference(int index, string _)
    {
        var v = Item("hostFingerprints", index);
        Assert.Equal(Str(v["fingerprint"]), DeviceFingerprints.Host(Str(v["host"])!, Str(v["platform"])!, Str(v["arch"])!));
    }

    [Theory]
    [MemberData(nameof(KeptCases))]
    public void Kept_majors_as_the_reference(int index, string name)
    {
        var v = Item("kept", index);
        var kept = Offline.KeptWith(Int(v["kept"]), Claims(upgradeUntil: Long(v["upgradeUntil"])), Long(v["now"])!.Value, Int(v["major"])!.Value);
        Assert.True(Int(v["expect"]) == kept, $"{name}: expected {Int(v["expect"])}, got {kept}");
    }

    [Theory]
    [MemberData(nameof(RenewCases))]
    public void Early_renewal_as_the_reference(int index, string name)
    {
        var v = Item("renew", index);
        Assert.True(Bool(v["expect"]) == Offline.RenewsEarly(Claims(Long(v["iat"]), Long(v["exp"])), Long(v["now"])!.Value), name);
    }

    [Theory]
    [MemberData(nameof(AskCases))]
    public void Asks_as_the_reference(int index, string name)
    {
        var v = Item("asks", index);
        Assert.True(Bool(v["expect"]) == Offline.AskDue(State(v["state"]), Long(v["clockNow"])!.Value, Bool(v["force"])!.Value), name);
    }

    [Theory]
    [MemberData(nameof(StandingCases))]
    public void Standing_refusals_as_the_reference(int index, string name)
    {
        var v = Item("standing", index);
        // A device read is { ownClaim?, canTell } (canTell true unless said); none read at all is null.
        var device = v["device"];
        var standing = Offline.StandingRefusal(State(v["state"]), Int(v["major"])!.Value, Str(device?["ownClaim"]), device is not null && (Bool(device["canTell"]) ?? true));
        Assert.True(Str(v["expect"]) == (standing is { } s ? Names.Of(s) : null), name);
    }

    [Theory]
    [MemberData(nameof(TrialStatusCases))]
    public void Trial_statuses_as_the_reference(int index, string name)
    {
        var v = Item("trialStatuses", index);
        // leaseEndsAt: the end the trial lease was signed to (ms), null when it is not known.
        var status = Offline.TrialStatusOf(State(v["state"]), Long(v["now"])!.Value, Bool(v["leaseValid"])!.Value, Long(v["leaseEndsAt"]));
        AssertJson.Equal(v["expect"], StatusJson(status), name);
    }

    [Theory]
    [MemberData(nameof(RefusalPatchCases))]
    public void Refusal_patches_set_and_clear_as_the_reference(int index, string name)
    {
        var v = Item("refusalPatches", index);
        var patch = Offline.RefusalPatch(Names.RefusalOf(Str(v["refused"]))!.Value, Int(v["major"])!.Value, Str(v["device"]!["ownClaim"]));
        AssertJson.Equal(v["expect"], PatchJson(patch), name);
    }

    [Theory]
    [MemberData(nameof(LeasePatchCases))]
    public void Lease_patches_clear_as_the_reference(int index, string name)
    {
        var v = Item("leasePatches", index);
        var patch = Str(v["patch"]) switch
        {
            "LEASED" => Offline.Leased,
            "UNKEYED" => Offline.Unkeyed,
            "NO_PURCHASE" => Offline.NoPurchase,
            var other => throw new InvalidOperationException($"no patch {other}"),
        };
        AssertJson.Equal(v["expect"], PatchJson(patch), name);
    }

    [Theory]
    [MemberData(nameof(KeyedByCases))]
    public void Install_keys_as_the_reference(int index, string name)
    {
        var v = Item("keyedBy", index);
        var device = new Device { Kind = Names.DeviceKindOf(Str(v["kind"])), Own = "fingerprint-own", CanTell = true };
        AssertJson.Equal(v["expect"], PatchJson(Offline.KeyedBy(device)), name);
    }

    [Theory]
    [MemberData(nameof(MergeCases))]
    public void Merges_land_as_the_reference(int index, string name)
    {
        var v = Item("merges", index);
        var merged = Offline.Merged(State(v["latest"]), Patch(v["patch"]), BasisOf(v["basis"]));
        AssertJson.Equal(v["expect"], JsonNode.Parse(merged.ToJson()), name);
    }

    [Theory]
    [MemberData(nameof(GuardedNowCases))]
    public void The_clock_guard_as_the_reference(int index, string name)
    {
        var v = Item("guardedNow", index);
        Assert.True(Long(v["expect"]) == Offline.GuardedNow(Long(v["clockNow"])!.Value, Long(v["lastSeen"])), name);
    }

    [Theory]
    [MemberData(nameof(ClockCorrectionCases))]
    public void Clock_corrections_as_the_reference(int index, string name)
    {
        var v = Item("clockCorrections", index);
        var claims = v["claims"] is { } c ? new SignedAt(Str(c["sub"])!, Str(c["aid"])!, Long(c["iat"])!.Value) : null;
        var corrected = Offline.ClockCorrection(Long(v["clockNow"])!.Value, Long(v["lastSeen"]), claims, Long(v["heldIat"]), Str(v["key"])!, Str(v["activationId"])!);
        Assert.True(Long(v["expect"]) == corrected, $"{name}: expected {Long(v["expect"])}, got {corrected}");
    }

    [Theory]
    [MemberData(nameof(MapServerErrorCases))]
    public void Server_errors_map_as_the_reference(int index, string _)
    {
        var v = Item("mapServerError", index);
        Assert.Equal(Str(v["expect"]), Names.Of(Wire.MapServerError(Str(v["error"]))));
    }

    [Theory]
    [MemberData(nameof(RefusalOfCases))]
    public void Refusals_are_read_as_the_reference(int index, string _)
    {
        var v = Item("refusalOf", index);
        Assert.Equal(Str(v["expect"]), Wire.RefusalOf(Str(v["error"])) is { } r ? Names.Of(r) : null);
    }

    [Theory]
    [MemberData(nameof(EnteredKeyCases))]
    public void Entered_keys_normalise_as_the_reference(int index, string _)
    {
        var v = Item("enteredKey", index);
        Assert.Equal(Str(v["expect"]), Wire.EnteredKey(Str(v["input"])!));
    }

    [Theory]
    [MemberData(nameof(TrimDeviceNameCases))]
    public void Device_names_cut_as_the_reference(int index, string _)
    {
        var v = Item("trimDeviceName", index);
        Assert.Equal(Str(v["expect"]), Wire.TrimToLength(Str(v["input"]), Int(v["max"])!.Value));
    }

    [Theory]
    [MemberData(nameof(NoAnswerCases))]
    public void No_answers_as_the_reference(int index, string _)
    {
        var v = Item("noAnswer", index);
        Assert.Equal(Bool(v["expect"]), Wire.NoAnswer(Int(v["status"])!.Value));
    }

    [Theory]
    [MemberData(nameof(ParseLeaseBodyCases))]
    public void Lease_bodies_read_as_the_reference(int index, string _)
    {
        var v = Item("parseLeaseBody", index);
        var (lease, activationId) = Wire.ParseLeaseBody(v["body"]);
        var read = new JsonObject();
        if (lease is not null) read["lease"] = lease;
        if (activationId is not null) read["activationId"] = activationId;
        AssertJson.Equal(v["expect"], read);
    }

    [Theory]
    [MemberData(nameof(ParseTrialBodyCases))]
    public void Trial_bodies_read_as_the_reference(int index, string _)
    {
        var v = Item("parseTrialBody", index);
        JsonObject? read = null;
        if (Wire.ParseTrialBody(v["body"]) is { } body)
        {
            read = new JsonObject { ["trialStart"] = body.TrialStart, ["trialEndsAt"] = body.TrialEndsAt };
            if (body.Lease is not null) read["lease"] = body.Lease;
        }
        AssertJson.Equal(v["expect"], read);
    }

    [Theory]
    [MemberData(nameof(MountinfoCases))]
    public void Mountinfo_is_read_as_the_reference(int index, string name)
    {
        var v = Item("mountinfo", index);
        Assert.True(Str(v["expect"]) == MachineId.MachineIdFilesystem(Str(v["mountinfo"])!), name);
    }

    [Theory]
    [MemberData(nameof(MachineIdCases))]
    public async Task Machine_ids_are_read_as_the_reference(int index, string name)
    {
        var v = Item("machineIds", index);
        var reading = await MachineId.ReadAsync(new ScriptedSource(v["source"]!.AsObject()), TimeSpan.FromMilliseconds(MachineId.TimeoutMs), TimeProvider.System);
        var read = new JsonObject { ["kind"] = reading.Kind.ToString().ToLowerInvariant() };
        if (reading.Id is not null) read["id"] = reading.Id;
        AssertJson.Equal(v["expect"], read, name);
    }

    [Theory]
    [MemberData(nameof(RenewalCases))]
    public void Renewals_save_and_say_as_the_reference(int index, string name)
    {
        var v = Item("renewals", index);
        // The answer: null when there was none at all; its JSON body as the adapter hands it over.
        var response = v["response"] is { } r ? new HttpResult(Int(r["status"])!.Value, r["json"]?.DeepClone()) : null;
        // The offline check of the lease a 200 carried, as given: the lease itself is not verified.
        var check = CheckOf(v["check"]);
        var device = new Device { Kind = Names.DeviceKindOf(Str(v["device"]!["kind"])), OwnClaim = Str(v["device"]!["ownClaim"]) };
        var outcome = Renewal.Outcome(new RenewalInput(
            response,
            check,
            Long(v["askedAt"])!.Value,
            Long(v["now"])!.Value,
            Int(v["major"])!.Value,
            Str(v["key"]),
            Int(v["keptMajor"]),
            device));
        var expect = new JsonObject
        {
            ["answered"] = outcome.Answered,
            ["status"] = outcome.Status is { } status ? StatusJson(status) : null,
            ["patch"] = PatchJson(outcome.Patch),
        };
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(ValidLeaseAskCases))]
    public void Asks_on_a_valid_lease_as_the_reference(int index, string name)
    {
        var v = Item("validLeaseAsks", index);
        var c = v["claims"]!;
        var claims = Claims(iat: Long(c["iat"]), exp: Long(c["exp"]), major: Long(c["major"]));
        var asks = Renewal.AsksOnValidLease(claims, State(v["state"]), Int(v["major"])!.Value, Long(v["now"])!.Value, Long(v["clockNow"])!.Value);
        Assert.True(Bool(v["expect"]) == asks, name);
    }

    [Theory]
    [MemberData(nameof(DeviceCases))]
    public void Devices_are_told_as_the_reference(int index, string name)
    {
        var v = Item("devices", index);
        var i = v["identity"]!;
        var identity = new DeviceIdentity(Str(i["fingerprint"]), Names.DeviceKindOf(Str(i["kind"])), Strings(i["alternates"]));
        var device = Devices.From(identity, Names.DeviceKindOf(Str(v["installKind"])), Bool(v["deviceHold"])!.Value, Hashes.FingerprintClaim);
        var told = new JsonObject
        {
            ["own"] = device.Own,
            ["canTell"] = device.CanTell,
            ["fingerprints"] = new JsonArray(device.Fingerprints.Select(f => (JsonNode?)f).ToArray()),
            ["claims"] = new JsonArray(device.Claims.Select(f => (JsonNode?)f).ToArray()),
            ["hostname"] = device.Hostname,
        };
        if (device.OwnClaim is { } ownClaim) told["ownClaim"] = ownClaim;
        if (device.Kind is { } kind) told["kind"] = Names.Of(kind);
        AssertJson.Equal(v["expect"], told, name);
    }

    [Theory]
    [MemberData(nameof(IdentityCases))]
    public void Identities_are_answered_as_the_reference(int index, string name)
    {
        var v = Item("identities", index);
        var r = v["reading"]!;
        var reading = Str(r["kind"]) switch
        {
            "id" => MachineIdReading.Of(Str(r["id"])!),
            "unavailable" => MachineIdReading.Unavailable,
            "transient" => MachineIdReading.Transient,
            var other => throw new InvalidOperationException($"no reading {other}"),
        };
        var host = v["host"] is { } h ? new HostNames(Str(h["host"])!, Str(h["platform"])!, Str(h["arch"])!) : null;
        var identity = DeviceFingerprints.IdentityOf(reading, host);
        var answered = new JsonObject { ["fingerprint"] = identity.Fingerprint };
        if (identity.Kind is { } kind) answered["kind"] = Names.Of(kind);
        answered["alternates"] = new JsonArray(identity.Alternates.Select(f => (JsonNode?)f).ToArray());
        AssertJson.Equal(v["expect"], answered, name);
    }

    [Theory]
    [MemberData(nameof(SavedLastSeenCases))]
    public void The_time_a_save_records_as_seen_as_the_reference(int index, string name)
    {
        var v = Item("savedLastSeen", index);
        var saved = Offline.SavedLastSeen(Long(v["stored"]), Long(v["clockNow"])!.Value, Long(v["guard"]));
        Assert.True(Long(v["expect"]) == saved, $"{name}: expected {Long(v["expect"])}, got {saved}");
    }

    [Theory]
    [MemberData(nameof(LeaseStatusCases))]
    public void Stored_leases_give_the_status_the_reference_gives_with_no_verdict(int index, string name)
    {
        var v = Item("leaseStatuses", index);
        var holder = HolderOf(v["holder"]!);
        var check = Offline.CheckLease(Verifier.Verify(Str(v["token"])!, Long(v["now"])!.Value), Str(v["product"])!, Long(v["major"])!.Value, holder);
        AssertJson.Equal(v["expect"], StatusJson(Offline.LeaseStatus(check, holder.Key)), name);
    }

    [Theory]
    [MemberData(nameof(TrialLeaseStatusCases))]
    public void Trial_leases_give_the_trial_status_the_reference_gives(int index, string name)
    {
        var v = Item("trialLeaseStatuses", index);
        var token = Str(v["token"])!;
        var now = Long(v["now"])!.Value;
        var device = v["device"]!;
        // The stored trial is its start and its lease: its end and its entitlements are the lease's.
        var lease = Offline.TrialLeaseOf(Verifier.Verify(token, now), Str(v["product"])!, Strings(device["claims"]), Bool(device["canTell"])!.Value);
        var state = new StoredState { TrialStart = Long(v["trialStart"])!.Value, TrialLease = token };
        var status = Offline.TrialStatusOf(state, now, lease.Valid, lease.EndsAt, lease.Entitlements);
        AssertJson.Equal(v["expect"], StatusJson(status), name);
    }

    [Theory]
    [MemberData(nameof(EntitlementNumberCases))]
    public void Entitlement_numbers_read_to_the_bit_as_the_reference_reads_them(int index, string name)
    {
        var v = Item("entitlementNumbers", index);
        // The number as the server writes it, read as a lease's ent is ({"n": json}, through the payload's
        // own reader): the expected bits are text, which no reader of the file can round.
        var ent = TopLevelJson.Read($"{{\"n\":{Str(v["json"])}}}").Value;
        var read = ClaimsParser.EntitlementsOf(ent)["n"].Number ?? throw new Xunit.Sdk.XunitException($"{name}: not a number");
        Assert.True(Str(v["expect"]) == Bits(read), $"{name}: expected {Str(v["expect"])}, got {Bits(read)}");
    }

    /// <summary>A double's IEEE-754 bits, sign first, as 16 lowercase hex digits.</summary>
    private static string Bits(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>JSON compared as values: object members in any order, numbers by value.</summary>
internal static class AssertJson
{
    public static void Equal(JsonNode? expected, JsonNode? actual, string because = "")
    {
        var e = Canonical(expected);
        var a = Canonical(actual);
        if (e != a) throw new Xunit.Sdk.XunitException($"{because}\nexpected: {e}\nactual:   {a}");
    }

    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Canonical(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when Json.TryNumber(v, out var d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}
