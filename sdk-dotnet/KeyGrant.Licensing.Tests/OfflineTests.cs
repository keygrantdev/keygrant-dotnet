using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

/// <summary>The offline decisions, beyond what the conformance vectors hold.</summary>
public class OfflineTests
{
    private const long Since = 1_757_000_000_000;
    private const long Hour = 60 * 60_000;
    private const long Day = 24 * Hour;
    private const long Issued = 1_757_000_000;

    public static TheoryData<string, string, int, string?> BuildRefusals => new()
    {
        { "no refusal", "{}", 3, null },
        { "revoked, any build", "{\"refused\":\"revoked\"}", 1, "revoked" },
        { "revoked, whatever major is stored", "{\"refused\":\"revoked\",\"refusedMajor\":4}", 2, "revoked" },
        { "upgrade, at the refused major", "{\"refused\":\"upgrade\",\"refusedMajor\":4}", 4, "upgrade" },
        { "upgrade, above it", "{\"refused\":\"upgrade\",\"refusedMajor\":4}", 5, "upgrade" },
        { "upgrade, below it", "{\"refused\":\"upgrade\",\"refusedMajor\":4}", 3, null },
        { "outdated, at the refused major", "{\"refused\":\"outdated\",\"refusedMajor\":2}", 2, "outdated" },
        { "outdated, below it", "{\"refused\":\"outdated\",\"refusedMajor\":2}", 1, "outdated" },
        { "outdated, above it", "{\"refused\":\"outdated\",\"refusedMajor\":2}", 3, null },
        { "upgrade stored with no major (legacy), any build", "{\"refused\":\"upgrade\"}", 1, "upgrade" },
        { "outdated stored with no major (legacy), any build", "{\"refused\":\"outdated\"}", 9, "outdated" },
        { "device with no refusedFor (the lease dropped)", "{\"refused\":\"device\"}", 1, "device" },
    };

    [Theory]
    [MemberData(nameof(BuildRefusals))]
    public void A_stored_refusal_stands_for_the_build_refused_and_further_the_same_way(string _, string state, int major, string? expected)
    {
        var standing = Offline.StandingRefusal(StoredState.FromJson(state)!, major, null, false);
        Assert.Equal(expected, standing is { } s ? Names.Of(s) : null);
    }

    [Theory]
    [InlineData("A", true, "device")]
    [InlineData("B", true, null)]
    [InlineData(null, false, null)]
    [InlineData("A", false, null)]
    public void A_device_refusal_stands_only_for_the_device_it_was_given_for_on_a_run_that_can_tell(string? ownClaim, bool canTell, string? expected)
    {
        var state = new StoredState { Refused = Refusal.Device, RefusedFor = ["A"] };
        var standing = Offline.StandingRefusal(state, 1, ownClaim, canTell);
        Assert.Equal(expected, standing is { } s ? Names.Of(s) : null);
    }

    private static LeaseClaims Claims(long lifeMs) => Vectors.Claims(Issued, Issued + lifeMs / 1000);

    [Theory]
    [InlineData(30 * Day, Offline.RenewWithinMs, false)]
    [InlineData(30 * Day, Offline.RenewWithinMs - 1, true)]
    [InlineData(7 * Day, (long)(3.5 * Day), false)]
    [InlineData(7 * Day, (long)(3.5 * Day) - 1, true)]
    [InlineData(Hour, 30 * 60_000, false)]
    [InlineData(Hour, 30 * 60_000 - 1, true)]
    public void Renews_early_within_a_week_or_half_the_lease_s_life(long life, long left, bool expected)
    {
        Assert.Equal(expected, Offline.RenewsEarly(Claims(life), Issued * 1000 + life - left));
    }

    [Theory]
    [InlineData(Since, Offline.AskAgainMs, Since, true, true)]
    [InlineData(null, null, Since, false, true)]
    [InlineData(Since, Offline.AskAgainMs, Since - 1, false, true)]
    [InlineData(Since, Offline.RetryUnansweredMs, Since + Offline.RetryUnansweredMs - 1, false, false)]
    [InlineData(Since, Offline.RetryUnansweredMs, Since + Offline.RetryUnansweredMs, false, true)]
    [InlineData(Since, Offline.AskAgainMs, Since + Offline.AskAgainMs - 1, false, false)]
    [InlineData(Since, Offline.AskAgainMs, Since + Offline.AskAgainMs, false, true)]
    [InlineData(Since, null, Since + Offline.AskAgainMs - 1, false, false)]
    [InlineData(Since, null, Since + Offline.AskAgainMs, false, true)]
    public void Asks_when_forced_never_waited_set_back_or_once_the_wait_is_over(long? since, long? backoffMs, long clockNow, bool force, bool expected)
    {
        Assert.Equal(expected, Offline.AskDue(new StoredState { BackoffSince = since, BackoffMs = backoffMs }, clockNow, force));
    }

    private static readonly Asked AskedAt1000 = new(1_000, "act_1", 500);

    [Theory]
    [InlineData("act_1", 500L, null)]
    [InlineData("act_1", 2_000L, "Answer")]
    [InlineData("act_1", 800L, null)]
    [InlineData("act_1", 1_000L, null)]
    [InlineData("act_2", 500L, "Activation")]
    [InlineData(null, 500L, "Activation")]
    public void An_answer_is_superseded_by_another_activation_or_a_newer_answer(string? activationId, long? verdictAt, string? expected)
    {
        var superseded = Offline.SupersededBy(new StoredState { ActivationId = activationId, VerdictAt = verdictAt }, AskedAt1000);
        Assert.Equal(expected, superseded?.ToString());
    }

    [Fact]
    public void Saves_one_process_s_own_answer_over_the_one_it_saw_even_stamped_later_and_compares_no_times_when_none_was_seen()
    {
        Assert.Null(Offline.SupersededBy(new StoredState { ActivationId = "act_1", VerdictAt = 2_000 }, new Asked(1_000, "act_1", 2_000)));
        Assert.Null(Offline.SupersededBy(new StoredState { ActivationId = "act_1" }, new Asked(1_000, "act_1", null)));
        Assert.Equal(Superseded.Answer, Offline.SupersededBy(new StoredState { ActivationId = "act_1", VerdictAt = 2_000 }, new Asked(1_000, "act_1", null)));
    }

    [Fact]
    public void Leaves_out_what_the_server_said_and_the_wait_and_keeps_what_the_device_knows()
    {
        var patch = new StatePatch
        {
            [StateField.Lease] = "L",
            [StateField.Refused] = Refusal.Revoked,
            [StateField.RefusedMajor] = 2,
            [StateField.RefusedFor] = new[] { "c" },
            [StateField.BackoffSince] = 1L,
            [StateField.BackoffMs] = 2L,
            [StateField.CheckedMajor] = 3,
            [StateField.VerdictAt] = 4L,
            [StateField.KeptMajor] = 5,
            [StateField.DeviceKind] = DeviceKind.Machine,
        };
        Assert.Equal([StateField.KeptMajor, StateField.DeviceKind], Offline.WithoutVerdict(patch).Entries.Select(e => e.Key));
    }

    private static StatePatch Answer() => new()
    {
        [StateField.Lease] = "LA",
        [StateField.CheckedMajor] = 3,
        [StateField.KeptMajor] = 3,
        [StateField.DeviceKind] = DeviceKind.Machine,
        [StateField.TrialStart] = 100L,
        [StateField.TrialEndsAt] = 900L,
        [StateField.TrialLease] = "T",
    };

    [Fact]
    public void Keeps_only_the_trial_evidence_of_an_answer_about_an_activation_deactivated_or_replaced_meanwhile()
    {
        foreach (var latest in new[]
        {
            new StoredState { TrialStart = 200 },
            new StoredState { Key = "B", ActivationId = "act_2", Lease = "LB", DeviceKind = DeviceKind.Hostname, TrialStart = 200 },
        })
        {
            var saved = Offline.Merged(latest, Answer(), new Basis { Answer = AskedAt1000 });
            Assert.Equal(latest with { TrialStart = 100, TrialEndsAt = 900, TrialLease = "T" }, saved);
        }
    }

    [Fact]
    public void Keeps_only_the_trial_evidence_of_a_note_about_an_activation_no_longer_stored()
    {
        var about = new Basis { About = new About("act_1") };
        var noted = new StatePatch { [StateField.KeptMajor] = 3 };
        Assert.Equal(new StoredState { ActivationId = "act_2" }, Offline.Merged(new StoredState { ActivationId = "act_2" }, noted, about));
        Assert.Equal(new StoredState { ActivationId = "act_1", KeptMajor = 3 }, Offline.Merged(new StoredState { ActivationId = "act_1" }, noted, about));
        Assert.Equal(new StoredState(), Offline.Merged(new StoredState(), noted, about));
    }

    [Fact]
    public void Keeps_the_verdict_of_a_newer_answer_about_the_same_activation_and_saves_the_rest_of_an_older_one()
    {
        var latest = new StoredState { ActivationId = "act_1", Refused = Refusal.Revoked, VerdictAt = 2_000 };
        Assert.Equal(
            new StoredState { ActivationId = "act_1", Refused = Refusal.Revoked, VerdictAt = 2_000, KeptMajor = 3, DeviceKind = DeviceKind.Machine, TrialStart = 100, TrialEndsAt = 900, TrialLease = "T" },
            Offline.Merged(latest, Answer(), new Basis { Answer = AskedAt1000 }));
    }

    private static StatePatch Activated(string activationId = "act_1") => new StatePatch
    {
        [StateField.Key] = "K",
        [StateField.ActivationId] = activationId,
        [StateField.Lease] = "LA",
        [StateField.CheckedMajor] = 2,
        // As the activate names it: a fresh activation keeps no kept major of another seat.
        [StateField.KeptMajor] = null,
    }.With(Offline.Leased).With(new StatePatch { [StateField.DeviceKind] = DeviceKind.Machine });

    private static readonly StoredState RefusedLater = new() { Key = "K", ActivationId = "act_1", Refused = Refusal.Revoked, VerdictAt = 2_000 };

    [Fact]
    public void An_activation_of_the_same_seat_under_a_refusal_asked_for_later_lands_its_key_and_kind_not_its_lease()
    {
        var saved = Offline.Merged(RefusedLater, Activated(), new Basis { Activation = new Asked(1_000, "act_1", 500) });
        Assert.Equal(new StoredState { Key = "K", ActivationId = "act_1", Refused = Refusal.Revoked, VerdictAt = 2_000, DeviceKind = DeviceKind.Machine }, saved);
    }

    [Fact]
    public void An_activation_of_the_same_seat_over_the_verdict_it_saw_lands_whole()
    {
        var saved = Offline.Merged(RefusedLater, Activated(), new Basis { Activation = new Asked(1_000, "act_1", 2_000) });
        Assert.Equal(("LA", 2, 1_000L), (saved.Lease, saved.CheckedMajor, saved.VerdictAt));
        Assert.Null(saved.Refused);
    }

    [Fact]
    public void An_activation_of_another_seat_lands_whole_whatever_was_stored()
    {
        var saved = Offline.Merged(RefusedLater with { KeptMajor = 7, BackoffSince = 5, BackoffMs = 60_000 }, Activated("act_2"), new Basis { Activation = new Asked(1_000, "act_2", null) });
        Assert.Equal(new StoredState { Key = "K", ActivationId = "act_2", Lease = "LA", CheckedMajor = 2, VerdictAt = 1_000, DeviceKind = DeviceKind.Machine }, saved);
    }

    [Theory]
    [InlineData("a lower one noted", 5, 3, "about", 5)]
    [InlineData("a higher one noted", 3, 5, "about", 5)]
    [InlineData("an answer that noted none", 5, null, "answer", 5)]
    [InlineData("a deactivation, based on nothing", 5, null, "none", null)]
    [InlineData("a fresh activation", 5, null, "activation-2", null)]
    [InlineData("an activation of the same seat", 5, 3, "activation-1", 5)]
    public void The_kept_major_only_goes_up_for_the_stored_activation(string _, int stored, int? noted, string basis, int? expected)
    {
        var on = basis switch
        {
            "about" => new Basis { About = new About("act_1") },
            "answer" => new Basis { Answer = new Asked(1, "act_1", null) },
            "activation-2" => new Basis { Activation = new Asked(1, "act_2", null) },
            "activation-1" => new Basis { Activation = new Asked(1, "act_1", null) },
            _ => Basis.None,
        };
        var saved = Offline.Merged(new StoredState { ActivationId = "act_1", KeptMajor = stored }, new StatePatch { [StateField.KeptMajor] = noted }, on);
        Assert.Equal(expected, saved.KeptMajor);
    }

    [Fact]
    public void Stamps_an_answer_and_a_trial_answer_with_when_they_were_asked_for()
    {
        Assert.Equal(1_000, Offline.Merged(new StoredState { ActivationId = "act_1" }, new StatePatch { [StateField.Lease] = "L" }, new Basis { Answer = AskedAt1000 }).VerdictAt);
        Assert.Equal(
            new StoredState { TrialEndsAt = 9, TrialAt = 7 },
            Offline.Merged(new StoredState(), new StatePatch { [StateField.TrialEndsAt] = 9L }, new Basis { Trial = new Stamp(7, null) }));
    }

    private static readonly StoredState StoredTrial = new() { TrialStart = 50, TrialEndsAt = 500, TrialLease = "NEWER", TrialAt = 2_000 };

    [Theory]
    [InlineData(400)]
    [InlineData(600)]
    public void An_older_trial_answer_loses_its_end_and_lease_whichever_ends_first(long end)
    {
        var older = new StatePatch { [StateField.TrialStart] = 60L, [StateField.TrialEndsAt] = end, [StateField.TrialLease] = "OLDER" };
        Assert.Equal(StoredTrial, Offline.Merged(StoredTrial, older, new Basis { Trial = new Stamp(1_000, null) }));
    }

    [Fact]
    public void A_newer_trial_answer_wins_a_later_end_included()
    {
        var newer = new StatePatch { [StateField.TrialStart] = 60L, [StateField.TrialEndsAt] = 900L, [StateField.TrialLease] = "EXT" };
        Assert.Equal(new StoredState { TrialStart = 50, TrialEndsAt = 900, TrialLease = "EXT", TrialAt = 3_000 }, Offline.Merged(StoredTrial, newer, new Basis { Trial = new Stamp(3_000, null) }));
    }

    [Fact]
    public void A_trial_answer_wins_over_one_with_no_stamp_and_over_the_one_it_saw()
    {
        var mine = new StatePatch { [StateField.TrialEndsAt] = 400L, [StateField.TrialLease] = "NEW" };
        Assert.Equal(
            new StoredState { TrialEndsAt = 400, TrialLease = "NEW", TrialAt = 1 },
            Offline.Merged(new StoredState { TrialEndsAt = 500, TrialLease = "OLD" }, mine, new Basis { Trial = new Stamp(1, null) }));
        var saw = Offline.Merged(StoredTrial, new StatePatch { [StateField.TrialEndsAt] = 300L, [StateField.TrialLease] = "MINE" }, new Basis { Trial = new Stamp(1_000, 2_000) });
        Assert.Equal((300L, "MINE", 1_000L), (saw.TrialEndsAt, saw.TrialLease, saw.TrialAt));
    }

    [Fact]
    public void The_trial_start_is_never_cleared_nor_moved_later()
    {
        var latest = new StoredState { TrialStart = 100 };
        Assert.Equal(100, Offline.Merged(latest, new StatePatch { [StateField.TrialStart] = null }).TrialStart);
        Assert.Equal(100, Offline.Merged(latest, new StatePatch { [StateField.TrialStart] = 200L }).TrialStart);
        Assert.Equal(50, Offline.Merged(latest, new StatePatch { [StateField.TrialStart] = 50L }).TrialStart);
    }

    [Fact]
    public void Leased_clears_any_refusal_and_wait_and_Unkeyed_everything_but_trial_evidence_and_the_clock()
    {
        Assert.All(Offline.Leased.Entries, e => Assert.Null(e.Value));
        Assert.Equal(
            [StateField.Refused, StateField.RefusedMajor, StateField.RefusedFor, StateField.BackoffSince, StateField.BackoffMs],
            Offline.Leased.Entries.Select(e => e.Key));
        Assert.Equal(
            [StateField.Key, StateField.ActivationId, StateField.Lease, StateField.CheckedMajor, StateField.KeptMajor, StateField.DeviceKind,
             StateField.Refused, StateField.RefusedMajor, StateField.RefusedFor, StateField.BackoffSince, StateField.BackoffMs],
            Offline.Unkeyed.Entries.Select(e => e.Key));
    }

    // --- entitlements in a status -------------------------------------------------------------
    //
    // The status a lease gives offline, and the entitlements it carries (LeaseStatus, TrialLeaseOf,
    // TrialStatusOf): only a lease that licenses, or a trial lease that runs a trial, carries them.

    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";

    private static Dictionary<string, Entitlement> Pro() => new() { ["edition"] = new("pro"), ["seats"] = new(5) };

    private static LeaseClaims Granting() => Claims(30 * Day) with { Ent = Pro() };

    [Fact]
    public void A_licensing_lease_gives_licensed_with_its_entitlements_and_the_check_s_test_flag()
    {
        var licensed = new LicenseStatus { State = LicenseState.Licensed, Key = Key, Entitlements = Pro() };
        Assert.Equal(licensed, Offline.LeaseStatus(LeaseCheck.Licensed(false, Granting()), Key));
        Assert.Equal(licensed with { Test = true }, Offline.LeaseStatus(LeaseCheck.Licensed(true, Granting() with { Test = true }), Key));
        // The check is the flag's one source (CheckLease takes it from the claims): a status never reads it again.
        Assert.Equal(licensed with { Test = true }, Offline.LeaseStatus(LeaseCheck.Licensed(true, Granting()), Key));
        Assert.Equal(licensed, Offline.LeaseStatus(LeaseCheck.Licensed(false, Granting() with { Test = true }), Key));
    }

    [Fact]
    public void A_licensing_lease_that_grants_none_gives_the_status_it_always_gave()
    {
        var status = Offline.LeaseStatus(LeaseCheck.Licensed(false, Claims(30 * Day)), Key);
        Assert.Equal(new LicenseStatus { State = LicenseState.Licensed, Key = Key }, status);
        Assert.Empty(status.Entitlements);
    }

    [Fact]
    public void A_lease_that_does_not_license_gives_its_reason_and_no_entitlements()
    {
        var cases = new (LeaseCheckFailure Reason, LicenseStatus Expected)[]
        {
            (LeaseCheckFailure.Expired, new() { State = LicenseState.Expired, Reason = LicenseReason.Offline, Key = Key }),
            (LeaseCheckFailure.Upgrade, new() { State = LicenseState.Invalid, Reason = LicenseReason.Upgrade, Key = Key }),
            (LeaseCheckFailure.Outdated, new() { State = LicenseState.Invalid, Reason = LicenseReason.Outdated, Key = Key }),
            (LeaseCheckFailure.Device, new() { State = LicenseState.Invalid, Reason = LicenseReason.Device, Key = Key }),
            // Not this licence's at all: no key either.
            (LeaseCheckFailure.Tampered, new() { State = LicenseState.Invalid, Reason = LicenseReason.Tampered }),
        };
        foreach (var (reason, expected) in cases)
        {
            var status = Offline.LeaseStatus(LeaseCheck.Refused(reason), Key);
            Assert.True(expected == status, $"{reason}: {status}");
        }
    }

    [Fact]
    public void A_status_s_entitlements_are_a_read_only_copy_not_the_claims_own_map()
    {
        var claims = Granting();
        var status = Offline.LeaseStatus(LeaseCheck.Licensed(false, claims), Key);
        Assert.NotSame(claims.Ent, status.Entitlements);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Entitlement>)status.Entitlements).Add("x", new Entitlement(true)));
        ((Dictionary<string, Entitlement>)claims.Ent)["edition"] = new Entitlement("studio");
        Assert.Equal("pro", status.Entitlements["edition"].Text);
    }

    private static readonly string[] Own = ["claim-own"];

    private static LeaseClaims TrialClaims() => Claims(7 * Day) with { Model = "trial", Aid = "claim-own", Fpk = DeviceKind.Machine, Ent = Pro() };

    [Fact]
    public void A_valid_trial_lease_gives_its_end_and_its_entitlements()
    {
        var trial = TrialClaims();
        var lease = Offline.TrialLeaseOf(VerifyResult.Ok(trial), "sluice", Own, true);
        Assert.Equal((true, (long?)(trial.Exp * 1000)), (lease.Valid, lease.EndsAt));
        AssertEntitlements.Equal(Pro(), lease.Entitlements);
    }

    public static TheoryData<string> InvalidTrialLeases => new() { "expired", "another product's", "another device's", "a licence's" };

    [Theory]
    [MemberData(nameof(InvalidTrialLeases))]
    public void A_trial_lease_not_valid_here_says_nothing_of_its_end_or_entitlements(string which)
    {
        var trial = TrialClaims();
        var lease = which switch
        {
            "expired" => Offline.TrialLeaseOf(VerifyResult.Failed(VerifyFailure.Expired, trial), "sluice", Own, true),
            "another product's" => Offline.TrialLeaseOf(VerifyResult.Ok(trial), "maindeck", Own, true),
            "another device's" => Offline.TrialLeaseOf(VerifyResult.Ok(trial), "sluice", ["claim-other"], true),
            _ => Offline.TrialLeaseOf(VerifyResult.Ok(trial with { Model = "perpetual" }), "sluice", Own, true),
        };
        Assert.Same(TrialLease.NotValid, lease);
        Assert.Equal((false, (long?)null), (lease.Valid, lease.EndsAt));
        Assert.Empty(lease.Entitlements);
    }

    [Fact]
    public void A_running_trial_s_entitlements_are_a_read_only_copy_not_the_map_it_was_given()
    {
        var state = new StoredState { TrialStart = Issued * 1000, TrialLease = "stored" };
        var given = Pro();
        var entitlements = Offline.TrialStatusOf(state, Issued * 1000, true, Issued * 1000 + Day, given).Entitlements;
        AssertEntitlements.Equal(Pro(), entitlements);
        Assert.NotSame(given, entitlements);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Entitlement>)entitlements).Add("x", new Entitlement(true)));
        given["edition"] = new Entitlement("studio");
        Assert.Equal("pro", entitlements["edition"].Text);
    }

    [Fact]
    public void A_running_trial_carries_the_entitlements_it_is_given_and_every_other_answer_none()
    {
        var state = new StoredState { TrialStart = Issued * 1000, TrialLease = "stored" };
        var now = Issued * 1000;
        var running = new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = now + 3 * Day, TrialDaysLeft = 3 };
        Assert.Equal(running with { Entitlements = Pro() }, Offline.TrialStatusOf(state, now, true, now + 3 * Day, Pro()));
        Assert.Equal(running, Offline.TrialStatusOf(state, now, true, now + 3 * Day, new Dictionary<string, Entitlement>()));
        Assert.Equal(running, Offline.TrialStatusOf(state, now, true, now + 3 * Day));
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial }, Offline.TrialStatusOf(state, now, false, null, Pro()));
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, Offline.TrialStatusOf(new StoredState(), now, true, now + 3 * Day, Pro()));
    }
}
