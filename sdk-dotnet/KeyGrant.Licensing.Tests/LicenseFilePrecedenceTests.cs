using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.LicenseFileSupport;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// A stored offline licence file beside a key and beside a trial (one that licenses this build answers first,
/// with no ask, no write and no launch report; one that does not answers only when nothing else does), a key
/// file activating its key, and the calls behind one that has not finished.
/// </summary>
public class LicenseFilePrecedenceTests
{
    private const long DayS = 86_400;

    private static readonly LicensedTo EmailOnly = new(null, AdaEmail);

    /// <summary><see cref="LicenseFileSupport.Key"/> held on act_1 with <paramref name="lease"/>, heard on major 2, seen at Now.</summary>
    private static StoredState Keyed(string lease, string? file = null) =>
        new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 2, LastSeen = Now, LicenseFile = file };

    [Fact]
    public async Task A_file_wins_over_a_held_key_which_keeps_working_once_the_file_is_removed()
    {
        var h = Create();
        var lease = LeaseTo3(h);
        h.Stored = Keyed(lease, h.Signer.OfflineFile());
        var app = h.LicenseFor(2);
        Assert.Equal(FileLicensed, await app.StatusAsync());
        await app.RemoveLicenseFileAsync();
        var stored = Assert.IsType<StoredState>(h.Stored);
        Assert.Equal((Key, lease, null), (stored.Key, stored.Lease, stored.LicenseFile));
        Assert.Equal(KeyLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task A_file_imported_over_a_held_key_leaves_its_seat_asking_nothing()
    {
        var h = Create();
        var lease = LeaseTo3(h);
        h.Stored = Keyed(lease);
        var app = h.LicenseFor(2);
        Assert.Equal(LicenseFileKind.Offline, (await app.ImportLicenseFileAsync(h.Signer.OfflineFile())).Kind);
        Assert.Empty(h.Http.Calls);
        var stored = Assert.IsType<StoredState>(h.Stored);
        Assert.Equal((Key, "act_1", lease), (stored.Key, stored.ActivationId, stored.Lease));
        await app.RemoveLicenseFileAsync();
        Assert.Equal(KeyLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task A_file_is_kept_by_an_activation_typed_or_from_a_key_file_and_still_answers()
    {
        var h = Create();
        var file = h.Signer.OfflineFile();
        h.Stored = StoredFile(file);
        h.Http.Lease("activate", LeaseTo3(h));
        var app = h.LicenseFor(2);
        Assert.Equal(ActivateResult.Success, await app.ActivateAsync(Key));
        Assert.Equal((Key, file), (h.Stored?.Key, h.Stored?.LicenseFile));
        Assert.Equal(LicenseFileKind.Key, (await app.ImportLicenseFileAsync(h.Signer.KeyFile())).Kind);
        Assert.Equal((Key, file), (h.Stored?.Key, h.Stored?.LicenseFile));
        Assert.Equal(FileLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task DeactivateAsync_leaves_a_file_alone_releasing_only_the_key()
    {
        var h = Create();
        var file = h.Signer.OfflineFile();
        h.Stored = Keyed(LeaseTo3(h), file);
        h.Http.Answer("deactivate", 200, new JsonObject());
        var app = h.LicenseFor(2);
        Assert.Equal(new DeactivateResult(true), await app.DeactivateAsync());
        Assert.Equal((null, file), (h.Stored?.Key, h.Stored?.LicenseFile));
        Assert.Equal(FileLicensed, await app.StatusAsync());
    }

    /// <summary>
    /// The key held with a second under a day left of its two-day lease (less than half its life, so due to
    /// renew early), inside an open upgrade window: an unforced status renews it and notes this major.
    /// </summary>
    private static void KeyDue(Harness h, string? file)
    {
        var lease = h.SignLease(new LeaseInput { Sub = Key, Major = 3, TtlSeconds = 2 * DayS, UpgradeUntil = Now / 1000 + 30 * DayS }, Now - Day - 1000);
        h.Stored = Keyed(lease, file);
    }

    [Fact]
    public async Task A_file_that_licenses_leaves_a_held_key_unrenewed_and_unnoted_forced_or_not()
    {
        var h = Create();
        KeyDue(h, h.Signer.OfflineFile());
        var app = h.LicenseFor(2);
        Assert.Equal(FileLicensed, await app.StatusAsync());
        Assert.Equal(FileLicensed, await app.RefreshAsync());
        Assert.Empty(h.Http.Calls);
        Assert.Null(h.Stored?.KeptMajor);
    }

    [Fact]
    public async Task With_no_file_the_same_key_is_asked_about_and_noted_unforced_so_the_test_above_can_see_it()
    {
        var h = Create();
        KeyDue(h, null);
        await h.LicenseFor(2).StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(2, h.Stored?.KeptMajor);
    }

    [Fact]
    public async Task A_file_that_licenses_answers_over_a_keys_stored_refusal_asking_nothing()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Refused = Refusal.Revoked, LicenseFile = h.Signer.OfflineFile() };
        Assert.Equal(FileLicensed, await h.LicenseFor(2).StatusAsync(forceOnline: true));
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task A_file_that_does_not_license_gives_way_to_the_key()
    {
        var h = Create();
        h.Stored = Keyed(LeaseTo3(h), h.Signer.OfflineFile());
        Assert.Equal(KeyLicensed, await h.LicenseFor(2, revoked: ["lf_1"]).StatusAsync());
    }

    [Fact]
    public async Task A_file_that_does_not_license_gives_way_to_the_keys_stored_refusal()
    {
        var h = Create();
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Refused = Refusal.Revoked, LicenseFile = h.Signer.OfflineFile() };
        h.Http.Fail = true;
        var revoked = new LicenseStatus { State = LicenseState.Invalid, Reason = LicenseReason.Revoked, Key = Key };
        Assert.Equal(revoked, await h.LicenseFor(2, revoked: ["lf_1"]).StatusAsync());
    }

    /// <summary>A trial this device started at <paramref name="start"/>, five days long, beside <paramref name="file"/>.</summary>
    private static StoredState Trial(Harness h, long start, string? file) =>
        new() { TrialStart = start, TrialEndsAt = start + 5 * Day, TrialLease = h.TrialLease(start + 5 * Day, start), LicenseFile = file };

    [Fact]
    public async Task A_file_that_licenses_answers_before_a_running_trial_whose_launch_is_then_not_reported()
    {
        var h = Create();
        // No stamp of a report or a trial answer: a running trial would report its launch now.
        h.Stored = Trial(h, Now, h.Signer.OfflineFile());
        var app = h.LicenseFor(2);
        Assert.Equal(FileLicensed, await app.StatusAsync());
        await app.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Http.Calls);
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task With_no_file_the_same_trial_reports_its_launch_so_the_test_above_can_see_it()
    {
        var h = Create();
        h.Stored = Trial(h, Now, null);
        var app = h.LicenseFor(2);
        Assert.Equal(LicenseState.Trial, (await app.StatusAsync()).State);
        await app.LaunchReportSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Http.Count("trial"));
        Assert.Equal(Now, h.Stored?.TrialReportedAt);
    }

    [Fact]
    public async Task A_file_that_does_not_license_gives_way_to_a_running_trial()
    {
        var h = Create();
        h.Stored = Trial(h, Now, h.Signer.OfflineFile(new JsonObject { ["major"] = 1 })) with { TrialAt = Now };
        var running = new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 5 * Day, TrialDaysLeft = 5 };
        Assert.Equal(running, await h.LicenseFor(2).StatusAsync());
    }

    [Fact]
    public async Task A_file_that_does_not_license_answers_in_place_of_an_ended_trial()
    {
        var h = Create();
        h.Stored = Trial(h, Now - 10 * Day, h.Signer.OfflineFile(new JsonObject { ["major"] = 1 })) with { TrialAt = Now };
        Assert.Equal(FileInvalid(LicenseReason.Upgrade), await h.LicenseFor(2).StatusAsync());
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task A_key_file_activates_its_key_online_as_a_typed_key_judging_no_version_and_stores_no_file()
    {
        var h = Create();
        var lease = LeaseTo3(h);
        h.Http.Lease("activate", lease);
        var app = h.LicenseFor(2);
        // Versions on a key file are not read: this build is outside them.
        var file = h.Signer.KeyFile(new JsonObject { ["major"] = 1, ["minMajor"] = 1 });
        Assert.Equal(ImportLicenseFileResult.Taken(LicenseFileKind.Key, EmailOnly), await app.ImportLicenseFileAsync(FileText(file)));
        // The key as a typed one is sent: normalised.
        var body = h.Http.BodyOf("activate");
        Assert.Equal((Key, 2), (body["key"]?.GetValue<string>(), body["major"]?.GetValue<int>()));
        var stored = Assert.IsType<StoredState>(h.Stored);
        Assert.Equal((Key, "act_1", lease, null), (stored.Key, stored.ActivationId, stored.Lease, stored.LicenseFile));
        Assert.Equal(KeyLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task A_key_file_answers_with_what_ActivateAsync_answers_storing_nothing_when_the_key_does_not_activate()
    {
        var h = Create();
        h.Http.Error("activate", 403, "limit");
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.Limit), await h.LicenseFor(2).ImportLicenseFileAsync(h.Signer.KeyFile()));
        h.Http.Fail = true;
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.Network), await h.LicenseFor(2).ImportLicenseFileAsync(h.Signer.KeyFile()));
        Assert.Equal((null, null), (h.Stored?.Key, h.Stored?.LicenseFile));
    }

    [Fact]
    public async Task A_key_file_throws_where_ActivateAsync_does_the_state_unreadable_asking_nothing()
    {
        var h = Create();
        var app = h.LicenseFor(2);
        h.FailReadAt(1);
        await Assert.ThrowsAsync<IOException>(() => app.ImportLicenseFileAsync(h.Signer.KeyFile()));
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task A_key_file_throws_where_ActivateAsync_does_the_activation_not_saved()
    {
        var h = Create();
        h.Http.Lease("activate", LeaseTo3(h));
        var app = h.LicenseFor(2);
        h.FailWrites();
        // Only a write throws this: the server answered, and the activation's save failed.
        await Assert.ThrowsAsync<DiskSaidNoException>(() => app.ImportLicenseFileAsync(h.Signer.KeyFile()));
        Assert.Equal(1, h.Http.Count("activate"));
    }

    [Fact]
    public async Task A_key_file_not_for_this_app_is_refused_before_any_request()
    {
        var h = Create();
        var app = h.LicenseFor(2, revoked: ["lf_2"]);
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.WrongProduct), await app.ImportLicenseFileAsync(h.Signer.KeyFile(new JsonObject { ["product"] = "maindeck" })));
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.RevokedFile), await app.ImportLicenseFileAsync(h.Signer.KeyFile()));
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task Behind_a_call_that_has_not_finished_import_and_remove_throw_and_a_status_answers_the_file_writing_nothing()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Major = 2, Time = time });
        var token = h.Signer.OfflineFile();
        h.Stored = StoredFile(token);
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        var imported = h.License.ImportLicenseFileAsync(token);
        var removed = h.License.RemoveLicenseFileAsync();
        var answered = h.License.StatusAsync();
        time.Advance(Grace);
        var importError = await Assert.ThrowsAsync<InvalidOperationException>(() => imported.WaitAsync(TimeSpan.FromSeconds(10)));
        var removeError = await Assert.ThrowsAsync<InvalidOperationException>(() => removed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("previous licence call has not finished", importError.Message, StringComparison.Ordinal);
        Assert.Contains("previous licence call has not finished", removeError.Message, StringComparison.Ordinal);
        Assert.Equal(FileLicensed with { Stalled = true }, await answered.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Writes);
    }
}
