using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>Edge cases of the engine, each pinning one decision.</summary>
public class EngineEdgeTests
{
    private const string Key = "SLUICE-AAAA-BBBB-CCCC-DDDD";
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = Key };

    private static StoredState Holding(string lease) => new() { Key = Key, ActivationId = "act_1", Lease = lease, CheckedMajor = 1, LastSeen = Now };

    [Fact]
    public async Task Licenses_from_a_renewal_whose_clock_check_cannot_read_the_state()
    {
        // The renewal's second read (the server-time check) fails: the server's lease still stands.
        var h = Create();
        h.Stored = Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day));
        var fresh = h.Sign(Key, "perpetual", 30 * 24 * 3600);
        h.Http.Lease("validate", fresh);
        h.FailReadAt(2);
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.True(h.Reads >= 3, "the state was read for the status, the clock check and the save");
        Assert.Equal(fresh, h.Stored!.Lease);
    }

    [Fact]
    public async Task Answers_the_trial_the_device_holds_when_the_trial_endpoint_never_answers()
    {
        var time = new ManualTime();
        var h = Create(null, new Options { Time = time });
        h.Stored = new StoredState { TrialStart = Now - Day, TrialEndsAt = Now + 6 * Day, TrialLease = h.TrialLease(Now + 6 * Day), LastSeen = Now };
        h.Http.Hang = true;
        var starting = h.License.StartTrialAsync();
        await Eventually.Until(() => h.Http.Called("trial") && time.HasTimerDueIn(LicenseConfig.DefaultHttpTimeout), "the bounded trial ask");
        time.Advance(LicenseConfig.DefaultHttpTimeout - TimeSpan.FromMilliseconds(1));
        await Eventually.Quiet();
        Assert.False(starting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new LicenseStatus { State = LicenseState.Trial, TrialEndsAt = Now + 6 * Day, TrialDaysLeft = 6 }, await starting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Keys_the_install_by_a_machine_id_it_read_even_when_the_ask_got_no_answer()
    {
        var machine = new FuncFingerprinter(
            () => Task.FromResult("fp-machine"),
            () => Task.FromResult<DeviceIdentity?>(new DeviceIdentity("fp-machine", DeviceKind.Machine, [])));
        var h = Create(null, new Options { Fingerprint = machine });
        h.Stored = Holding(h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        var stored = h.Stored!;
        Assert.Equal((DeviceKind.Machine, Offline.RetryUnansweredMs), (stored.DeviceKind, stored.BackoffMs));
    }

    /// <summary>A state whose last ask came back without a lease a second AFTER the device clock now reads (it was put back), the guard ahead of both.</summary>
    private static StoredState PutBack(StoredState state) =>
        state with { BackoffSince = Now + 1_000, BackoffMs = Offline.AskAgainMs, LastSeen = Now + 5_000 };

    [Fact]
    public async Task Asks_for_an_expired_lease_at_once_when_the_device_clock_was_put_back_before_the_last_ask()
    {
        // By the device clock the ask is in the future: the wait cannot be measured, so ask. (By the
        // guarded clock it would be 4 s into an hour's wait.)
        var h = Create();
        h.Stored = PutBack(Holding(h.Sign(Key, "perpetual", 3600, Now - 2 * Day)));
        h.Http.Fail = true;
        await h.License.StatusAsync();
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Asks_a_stored_refusal_again_at_once_when_the_device_clock_was_put_back_before_the_last_ask()
    {
        var h = Create();
        h.Stored = PutBack(new StoredState { Key = Key, ActivationId = "act_1", Refused = Refusal.Revoked });
        h.Http.Fail = true;
        Assert.Equal(LicenseReason.Revoked, (await h.License.StatusAsync()).Reason);
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Renews_a_lease_near_its_end_at_once_when_the_device_clock_was_put_back_before_the_last_ask()
    {
        var h = Create();
        h.Stored = PutBack(Holding(h.Sign(Key, "subscription", 7 * 24 * 3600, Now - 5 * Day)));
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
        Assert.Equal(1, h.Http.Count("validate"));
    }

    [Fact]
    public async Task Keeps_its_state_at_the_default_path_when_the_app_gives_no_storage_never_the_working_directory()
    {
        var product = $"keygrant-test-{Guid.NewGuid():N}";
        var path = FileStorage.DefaultPath(product);
        var folder = Path.GetDirectoryName(path)!;
        var folderWasThere = Directory.Exists(folder);
        var signer = new TestSigner();
        var http = new FakeHttp().Lease("activate", signer.SignLease(new LeaseInput { Sub = Key, Product = product }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var license = new License(new LicenseConfig
        {
            Product = product,
            ApiBaseUrl = "https://api.test",
            PublicJwk = signer.Jwk,
            Adapters = new LicenseAdapters { Http = http, Fingerprint = new FixedFingerprinter("fp-test") },
        });
        try
        {
            Assert.Equal(ActivateResult.Success, await license.ActivateAsync(Key));
            var saved = StoredState.FromJson(await File.ReadAllTextAsync(path))!;
            Assert.Equal((Key, "act_1"), (saved.Key, saved.ActivationId));
            Assert.False(File.Exists(FileStorage.LegacyPath(product)));
            Assert.Equal(LicenseState.Licensed, (await license.StatusAsync()).State);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, $"{product}.json*")) File.Delete(file);
                if (!folderWasThere && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
    }

    [Fact]
    public async Task Treats_a_file_that_stopped_parsing_before_its_rename_as_another_process_s_save()
    {
        // The reference's revision of a file that does not parse is NaN: never the one read, so the
        // save starts again (and then sets the file aside), never renaming over it as if it were missing.
        var state = Path.Combine(Path.GetTempPath(), "keygrant-memory", "sluice.json");
        var fs = new MemoryFileSystem(new() { [state] = "{\"key\":\"K\"}" });
        var turned = false;
        fs.Before[Op.Create] = () =>
        {
            if (turned) return;
            turned = true;
            fs.Files[state] = "garbage";
        };
        var storage = new FileStorage(state, null, fs, _ => Task.CompletedTask, () => 1_757_000_000_000);
        Assert.True(await storage.UpdateAsync(s => s with { KeptMajor = 2 }));
        AssertJson.Equal(new JsonObject { ["keptMajor"] = 2, ["rev"] = 1 }, JsonNode.Parse(fs.Files[state]));
        Assert.Equal("garbage", fs.Files[$"{state}.1757000000000.corrupt"]);
    }
}
