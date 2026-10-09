using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

public class FileStorageTests
{
    private static readonly string StateDir = Path.Combine(Path.GetTempPath(), "keygrant-memory");
    private static readonly string State = Path.Combine(StateDir, "sluice.json");
    private const string Saved = "{\"key\":\"SLUICE-AAAA-BBBB-CCCC-DDDD\",\"activationId\":\"act_1\",\"lastSeen\":1}";

    /// <summary>Waits that take no time, recorded: what a retry would have slept.</summary>
    private sealed class NoSleep
    {
        public List<int> Slept { get; } = new();

        public Task Sleep(int ms)
        {
            lock (Slept) Slept.Add(ms);
            return Task.CompletedTask;
        }
    }

    private static FileStorage Over(MemoryFileSystem fs, NoSleep? sleep = null, string? migrateFrom = null, Func<long>? now = null) =>
        new(State, migrateFrom, fs, (sleep ?? new NoSleep()).Sleep, now);

    /// <summary>A temp folder for one test, removed after it.</summary>
    private static async Task InTemp(Func<string, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"keygrant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await test(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string[] Names(string dir) => Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    private static void Aged(string path, TimeSpan age) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

    // --- defaultStoragePath -------------------------------------------------------

    public static TheoryData<string, string, string, Dictionary<string, string>, string> Systems => new()
    {
        { "Windows", "win32", @"C:\Users\jo", new() { ["LOCALAPPDATA"] = @"C:\Users\jo\AppData\Local" }, @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "Windows with %LOCALAPPDATA% elsewhere", "win32", @"C:\Users\jo", new() { ["LOCALAPPDATA"] = @"D:\Profiles\jo\Local" }, @"D:\Profiles\jo\Local\KeyGrant\sluice.json" },
        { "Windows with only the roaming %APPDATA%", "win32", @"C:\Users\jo", new() { ["APPDATA"] = @"C:\Users\jo\AppData\Roaming" }, @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "Windows with no %LOCALAPPDATA%", "win32", @"C:\Users\jo", new(), @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "Windows with an empty %LOCALAPPDATA%", "win32", @"C:\Users\jo", new() { ["LOCALAPPDATA"] = "" }, @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "Windows with a relative %LOCALAPPDATA%", "win32", @"C:\Users\jo", new() { ["LOCALAPPDATA"] = @"AppData\Local" }, @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "Windows with a drive-relative %LOCALAPPDATA%", "win32", @"C:\Users\jo", new() { ["LOCALAPPDATA"] = "C:Local" }, @"C:\Users\jo\AppData\Local\KeyGrant\sluice.json" },
        { "macOS", "darwin", "/Users/jo", new(), "/Users/jo/Library/Application Support/KeyGrant/sluice.json" },
        { "Linux with $XDG_CONFIG_HOME", "linux", "/home/jo", new() { ["XDG_CONFIG_HOME"] = "/home/jo/.cfg" }, "/home/jo/.cfg/keygrant/sluice.json" },
        { "Linux without it", "linux", "/home/jo", new(), "/home/jo/.config/keygrant/sluice.json" },
        { "Linux with it empty", "linux", "/home/jo", new() { ["XDG_CONFIG_HOME"] = "" }, "/home/jo/.config/keygrant/sluice.json" },
        { "Linux with it relative", "linux", "/home/jo", new() { ["XDG_CONFIG_HOME"] = ".cfg" }, "/home/jo/.config/keygrant/sluice.json" },
    };

    [Theory]
    [MemberData(nameof(Systems))]
    public void Keeps_state_per_user_on_this_machine_never_in_the_working_directory(string _, string platform, string home, Dictionary<string, string> env, string expected)
    {
        Assert.Equal(expected, FileStorage.DefaultPath("sluice", platform, home, name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void Defaults_to_a_folder_of_this_user_s()
    {
        var path = FileStorage.DefaultPath("sluice");
        Assert.EndsWith("sluice.json", path);
        Assert.Contains(OperatingSystem.IsWindows() ? @"\KeyGrant\" : OperatingSystem.IsMacOS() ? "/KeyGrant/" : "/keygrant/", path);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("Roaming", path);
    }

    // --- writes -----------------------------------------------------------------------

    [Fact]
    public Task Writes_by_renaming_a_temp_file_over_the_state_and_leaves_no_temp_behind() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        var storage = new FileStorage(path);
        await storage.WriteAsync(new StoredState { Key = "K", LastSeen = 1 });
        await storage.WriteAsync(new StoredState { Key = "K", LastSeen = 2 });
        AssertJson.Equal(new JsonObject { ["key"] = "K", ["lastSeen"] = 2 }, JsonNode.Parse(await File.ReadAllTextAsync(path)));
        Assert.Equal(["sluice.json"], Names(dir));
    });

    [Fact]
    public Task Clears_the_temps_crashes_left_once_a_write_lands_and_not_another_instance_s_write_in_flight() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        var stale = TimeSpan.FromMilliseconds(FileStorage.StaleTempMs);
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.1.1.crashed.tmp"), "{\"key\":\"CRASHED\"}");
        Aged(Path.Combine(dir, "sluice.json.1.1.crashed.tmp"), stale * 2);
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.2.2.inflight.tmp"), "{\"key\":\"IN");
        // Another product's temp in the same folder is not this file's to clear.
        await File.WriteAllTextAsync(Path.Combine(dir, "maindeck.json.3.3.other.tmp"), "{}");
        Aged(Path.Combine(dir, "maindeck.json.3.3.other.tmp"), stale * 2);
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.4.4.younger.tmp"), "{}");
        Aged(Path.Combine(dir, "sluice.json.4.4.younger.tmp"), stale - TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.5.5.older.tmp"), "{}");
        Aged(Path.Combine(dir, "sluice.json.5.5.older.tmp"), stale + TimeSpan.FromSeconds(5));

        await new FileStorage(path).WriteAsync(new StoredState { Key = "K" });
        Assert.Equal(["maindeck.json.3.3.other.tmp", "sluice.json", "sluice.json.2.2.inflight.tmp", "sluice.json.4.4.younger.tmp"], Names(dir));
        Assert.Equal(60_000, FileStorage.StaleTempMs);
    });

    [Fact]
    public async Task Clears_a_temp_aged_exactly_the_cutoff_and_keeps_one_a_millisecond_younger()
    {
        const long now = 1_757_000_000_000;
        var fs = new MemoryFileSystem(new() { [State] = Saved, [$"{State}.1.1.edge.tmp"] = "{}", [$"{State}.2.2.inside.tmp"] = "{}" });
        fs.Mtimes[$"{State}.1.1.edge.tmp"] = now - FileStorage.StaleTempMs;
        fs.Mtimes[$"{State}.2.2.inside.tmp"] = now - FileStorage.StaleTempMs + 1;
        fs.Now = () => now;
        await Over(fs, now: () => now).WriteAsync(new StoredState { Key = "K" });
        Assert.Equal([State, $"{State}.2.2.inside.tmp"], fs.Files.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("EPERM")]
    [InlineData("EBUSY")]
    [InlineData("EACCES")]
    public async Task Tries_a_rename_Windows_refuses_as_busy_again(string code)
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailNext(Op.Rename, code, code);
        var sleep = new NoSleep();
        await Over(fs, sleep).WriteAsync(new StoredState { Key = "K", LastSeen = 2 });
        AssertJson.Equal(new JsonObject { ["key"] = "K", ["lastSeen"] = 2 }, JsonNode.Parse(fs.Files[State]));
        Assert.Equal(2, sleep.Slept.Count);
        Assert.Equal([State], fs.Files.Keys);
    }

    [Fact]
    public async Task Gives_up_on_a_rename_that_stays_busy_after_a_second_or_two_keeping_the_state_and_removing_its_temp()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailAlways(Op.Rename, "EBUSY");
        var sleep = new NoSleep();
        var error = await Assert.ThrowsAsync<StorageCodeException>(() => Over(fs, sleep).WriteAsync(new StoredState { Key = "K" }));
        Assert.Equal("EBUSY", error.Code);
        Assert.InRange(sleep.Slept.Sum(), 1_000, 2_000);
        Assert.Equal(new Dictionary<string, string> { [State] = Saved }, fs.Files);
    }

    [Fact]
    public async Task Does_not_try_again_a_rename_that_failed_other_than_for_a_busy_file()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailNext(Op.Rename, "ENOSPC");
        var sleep = new NoSleep();
        var error = await Assert.ThrowsAsync<StorageCodeException>(() => Over(fs, sleep).WriteAsync(new StoredState { Key = "K" }));
        Assert.Equal("ENOSPC", error.Code);
        Assert.Empty(sleep.Slept);
        Assert.Equal(new Dictionary<string, string> { [State] = Saved }, fs.Files);
    }

    [Fact]
    public async Task Flushes_the_temp_to_the_disk_and_closes_it_before_the_rename_and_the_folder_after_it()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        await Over(fs).WriteAsync(new StoredState { Key = "K" });
        var steps = fs.Ops.Select(op => op.Split(' ')[0]).Where(op => op is not ("ReadDir" or "Mtime" or "Mkdir"));
        Assert.Equal(["Create", "Write", "Sync", "Close", "Rename", "SyncDir"], steps);
    }

    [Fact]
    public async Task Fails_a_write_whose_temp_cannot_be_flushed_the_state_as_it_was_and_no_temp()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailNext(Op.Sync, "EIO");
        var error = await Assert.ThrowsAsync<StorageCodeException>(() => Over(fs).WriteAsync(new StoredState { Key = "K" }));
        Assert.Equal("EIO", error.Code);
        Assert.Equal(new Dictionary<string, string> { [State] = Saved }, fs.Files);
    }

    [Fact]
    public async Task Fails_nothing_when_the_folder_cannot_be_flushed_the_rename_has_landed()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailNext(Op.SyncDir, "EINVAL");
        await Over(fs).WriteAsync(new StoredState { Key = "K" });
        Assert.Equal(new Dictionary<string, string> { [State] = "{\"key\":\"K\"}" }, fs.Files);
    }

    [Fact]
    public Task Leaves_the_state_as_it_was_and_no_temp_when_a_write_fails() => InTemp(async dir =>
    {
        // The state path is a folder: the rename over it fails (tried again as busy on Windows).
        var path = Path.Combine(dir, "sluice.json");
        Directory.CreateDirectory(path);
        var storage = new FileStorage(path, null, null, new NoSleep().Sleep, null);
        await Assert.ThrowsAnyAsync<Exception>(() => storage.WriteAsync(new StoredState { Key = "K" }));
        Assert.Equal(["sluice.json"], Names(dir));
    });

    [Fact]
    public Task Writes_the_reference_s_file_format() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        var state = new StoredState
        {
            Key = "K",
            ActivationId = "act_1",
            Refused = Refusal.Device,
            RefusedFor = ["c1"],
            DeviceKind = DeviceKind.Hostname,
            KeptMajor = 3,
            TrialStart = 1_757_000_000_000,
        };
        await new FileStorage(path).WriteAsync(state);
        var text = await File.ReadAllTextAsync(path);
        AssertJson.Equal(
            JsonNode.Parse("{\"key\":\"K\",\"activationId\":\"act_1\",\"trialStart\":1757000000000,\"keptMajor\":3,\"refused\":\"device\",\"refusedFor\":[\"c1\"],\"deviceKind\":\"hostname\"}"),
            JsonNode.Parse(text));
        Assert.DoesNotContain("null", text);
        Assert.False(text.StartsWith((char)0xFEFF), "no byte-order mark");
    });

    // --- reads ------------------------------------------------------------------------

    [Fact]
    public Task Returns_null_when_the_file_does_not_exist_then_round_trips_state() => InTemp(async dir =>
    {
        var storage = new FileStorage(Path.Combine(dir, "nested", "license.json"));
        Assert.Null(await storage.ReadAsync(false));
        await storage.WriteAsync(new StoredState { Key = "K", ActivationId = "a", LastSeen = 42 });
        Assert.Equal(new StoredState { Key = "K", ActivationId = "a", LastSeen = 42 }, await storage.ReadAsync(false));
    });

    [Fact]
    public Task Reads_the_state_it_had_when_a_write_crashed_before_its_rename() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await new FileStorage(path).WriteAsync(new StoredState { Key = "OLD", LastSeen = 1 });
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.123.456.abc.tmp"), "{\"key\":\"NE");
        Assert.Equal(new StoredState { Key = "OLD", LastSeen = 1 }, await new FileStorage(path).ReadAsync(false));
    });

    [Fact]
    public Task Reads_the_newest_temp_that_parses_when_the_state_itself_is_missing_by_its_time_not_its_name() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        var newer = Path.Combine(dir, "sluice.json.1.1.a.tmp");
        var older = Path.Combine(dir, "sluice.json.9.9.z.tmp");
        var torn = Path.Combine(dir, "sluice.json.5.5.m.tmp");
        await File.WriteAllTextAsync(newer, "{\"key\":\"NEW\"}");
        await File.WriteAllTextAsync(older, "{\"key\":\"OLD\"}");
        await File.WriteAllTextAsync(torn, "{\"key\":\"TOR");
        Aged(older, TimeSpan.FromSeconds(30));
        Aged(newer, TimeSpan.FromSeconds(20));
        Aged(torn, TimeSpan.FromSeconds(10));
        Assert.Equal(new StoredState { Key = "NEW" }, await new FileStorage(path).ReadAsync(false));
        File.Delete(newer);
        Assert.Equal(new StoredState { Key = "OLD" }, await new FileStorage(path).ReadAsync(false));
        File.Delete(older);
        Assert.Null(await new FileStorage(path).ReadAsync(false));
        // Read, never written in: the state file is still missing.
        Assert.False(File.Exists(path));
    });

    [Fact]
    public async Task Recovers_a_temp_on_a_read_only_read_too_writing_nothing()
    {
        // The reference reads the newest whole temp for a missing state whatever the read; a read-only
        // read writes nothing (neither puts the temp in place nor copies anything).
        var fs = new MemoryFileSystem(new() { [$"{State}.1.1.a.tmp"] = "{\"key\":\"FROM-A-TEMP\"}" });
        Assert.Equal(new StoredState { Key = "FROM-A-TEMP" }, await Over(fs).ReadAsync(readOnly: true));
        Assert.Empty(fs.Changes);
    }

    [Fact]
    public Task Sets_a_file_that_does_not_parse_aside_and_starts_afresh_never_from_a_temp() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllTextAsync(path, "{\"key\":\"SLUICE-AA");
        await File.WriteAllTextAsync(Path.Combine(dir, "sluice.json.1.1.a.tmp"), "{\"key\":\"FROM-A-TEMP\"}");
        var storage = new FileStorage(path);
        Assert.Null(await storage.ReadAsync(false));

        var aside = Names(dir).Single(name => System.Text.RegularExpressions.Regex.IsMatch(name, "^sluice\\.json\\.[0-9]+\\.corrupt$"));
        Assert.Equal("{\"key\":\"SLUICE-AA", await File.ReadAllTextAsync(Path.Combine(dir, aside)));
        // An empty state in its place, so no later read takes the temp instead.
        Assert.Equal(new StoredState(), await storage.ReadAsync(false));
    });

    [Fact]
    public async Task Keeps_only_the_newest_set_aside_files_of_a_state_file_by_the_time_in_their_names()
    {
        static string Aside(long at) => $"{State}.{at}.corrupt";
        var others = new Dictionary<string, string> { [Path.Combine(StateDir, "tunnel.json.100.corrupt")] = "x", [$"{State}.notes.corrupt"] = "x" };
        var files = new Dictionary<string, string> { [State] = "{\"bad", [Aside(900)] = "x", [Aside(1000)] = "x", [Aside(2000)] = "x", [Aside(3000)] = "x" };
        foreach (var (k, v) in others) files[k] = v;
        var fs = new MemoryFileSystem(files);
        Assert.Null(await Over(fs).ReadAsync(false));
        var kept = fs.Files.Keys.Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, "sluice\\.json\\.[0-9]+\\.corrupt$")).ToList();
        Assert.Equal(FileStorage.KeepCorrupt, kept.Count);
        Assert.Contains(Aside(3000), kept);
        Assert.Contains(Aside(2000), kept);
        Assert.DoesNotContain(Aside(1000), kept);
        Assert.DoesNotContain(Aside(900), kept);
        foreach (var name in others.Keys) Assert.True(fs.Files.ContainsKey(name), name);
    }

    [Fact]
    public async Task Reads_read_only_without_repairing_a_file_that_does_not_parse()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"bad" });
        Assert.Null(await Over(fs).ReadAsync(readOnly: true));
        Assert.Equal(new Dictionary<string, string> { [State] = "{\"bad" }, fs.Files);
        Assert.Empty(fs.Changes);
    }

    [Fact]
    public async Task Tries_a_read_that_failed_again_busy_once_then_the_state()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailNext(Op.ReadFile, "EBUSY");
        var sleep = new NoSleep();
        Assert.Equal(StoredState.FromJson(Saved), await Over(fs, sleep).ReadAsync(false));
        Assert.Single(sleep.Slept);
    }

    [Theory]
    [InlineData("EBUSY")]
    [InlineData("EACCES")]
    [InlineData("EIO")]
    public async Task Throws_on_a_read_that_still_fails_after_about_a_second_rather_than_reading_no_state_or_a_temp(string code)
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved, [$"{State}.1.1.a.tmp"] = "{\"key\":\"FROM-A-TEMP\"}" });
        fs.FailAlways(Op.ReadFile, code);
        var sleep = new NoSleep();
        var error = await Assert.ThrowsAsync<StorageCodeException>(() => Over(fs, sleep).ReadAsync(false));
        Assert.Equal(code, error.Code);
        Assert.InRange(sleep.Slept.Sum(), 500, 1_500);
        Assert.Empty(fs.Changes);
    }

    [Fact]
    public async Task Does_not_wait_on_a_missing_file()
    {
        var sleep = new NoSleep();
        Assert.Null(await Over(new MemoryFileSystem(), sleep).ReadAsync(false));
        Assert.Empty(sleep.Slept);
    }

    [Fact]
    public async Task Throws_writing_nothing_when_a_file_that_does_not_parse_cannot_be_set_aside()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"key\":\"SLUICE-AA" });
        fs.FailAlways(Op.Rename, "EBUSY");
        var error = await Assert.ThrowsAsync<StorageCodeException>(() => Over(fs).ReadAsync(false));
        Assert.Equal("EBUSY", error.Code);
        Assert.Equal(new Dictionary<string, string> { [State] = "{\"key\":\"SLUICE-AA" }, fs.Files);
    }

    [Fact]
    public async Task On_a_read_that_stays_busy_status_is_unknown_activate_and_deactivate_throw_nothing_asked_or_written()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailAlways(Op.ReadFile, "EBUSY");
        var (license, asked) = LicenseOver(fs);
        var unknown = new LicenseStatus { State = LicenseState.Unknown, Reason = LicenseReason.Storage };
        Assert.Equal(unknown, await license.StatusAsync());
        Assert.Equal(unknown, await license.RefreshAsync());
        Assert.Equal(unknown, await license.StartTrialAsync());
        Assert.Equal("EBUSY", (await Assert.ThrowsAsync<StorageCodeException>(() => license.ActivateAsync("SLUICE-EEEE-FFFF-GGGG-HHHH"))).Code);
        Assert.Equal("EBUSY", (await Assert.ThrowsAsync<StorageCodeException>(() => license.DeactivateAsync())).Code);
        Assert.Empty(asked);
        Assert.Equal(new Dictionary<string, string> { [State] = Saved }, fs.Files);
        Assert.Empty(fs.Changes);
    }

    // --- the state kept at an older path ----------------------------------------------

    [Fact]
    public Task Copies_an_older_path_s_state_once_and_reads_from_the_new_one_after() => InTemp(async dir =>
    {
        var legacy = Path.Combine(dir, "app", ".keygrant", "sluice.json");
        var path = Path.Combine(dir, "user", "sluice.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, Saved);
        var storage = new FileStorage(path, new FileStorageOptions { MigrateFrom = legacy });
        Assert.Equal(StoredState.FromJson(Saved), await storage.ReadAsync(false));
        AssertJson.Equal(JsonNode.Parse(Saved), JsonNode.Parse(await File.ReadAllTextAsync(path)));
        Assert.Equal(Saved, await File.ReadAllTextAsync(legacy));
        await File.WriteAllTextAsync(legacy, "{\"key\":\"CHANGED\"}");
        Assert.Equal(StoredState.FromJson(Saved), await storage.ReadAsync(false));
    });

    [Fact]
    public Task Ignores_an_older_path_when_the_new_one_has_a_state() => InTemp(async dir =>
    {
        var legacy = Path.Combine(dir, ".keygrant", "sluice.json");
        var path = Path.Combine(dir, "user", "sluice.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, Saved);
        await new FileStorage(path).WriteAsync(new StoredState { Key = "NEWER" });
        Assert.Equal(new StoredState { Key = "NEWER" }, await new FileStorage(path, new FileStorageOptions { MigrateFrom = legacy }).ReadAsync(false));
    });

    [Fact]
    public Task Is_no_state_and_nothing_written_when_the_older_path_is_missing_or_does_not_parse() => InTemp(async dir =>
    {
        var legacy = Path.Combine(dir, ".keygrant", "sluice.json");
        var path = Path.Combine(dir, "user", "sluice.json");
        Assert.Null(await new FileStorage(path, new FileStorageOptions { MigrateFrom = legacy }).ReadAsync(false));
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, "{\"key\":\"SLU");
        Assert.Null(await new FileStorage(path, new FileStorageOptions { MigrateFrom = legacy }).ReadAsync(false));
        Assert.Equal([".keygrant"], Names(dir));
    });

    [Fact]
    public async Task Still_reads_an_older_path_it_cannot_copy_and_copies_it_at_a_later_read()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "keygrant-legacy", "sluice.json");
        var fs = new MemoryFileSystem(new() { [legacy] = Saved });
        fs.FailNext(Op.Write, "ENOSPC");
        var storage = Over(fs, migrateFrom: legacy);
        Assert.Equal(StoredState.FromJson(Saved), await storage.ReadAsync(false));
        Assert.False(fs.Files.ContainsKey(State));
        Assert.Equal(StoredState.FromJson(Saved), await storage.ReadAsync(false));
        AssertJson.Equal(JsonNode.Parse(Saved), JsonNode.Parse(fs.Files[State]));
    }

    [Fact]
    public async Task Reads_not_copies_an_older_path_on_a_read_only_read()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "keygrant-legacy", "sluice.json");
        var fs = new MemoryFileSystem(new() { [legacy] = Saved });
        Assert.Equal(StoredState.FromJson(Saved), await Over(fs, migrateFrom: legacy).ReadAsync(readOnly: true));
        Assert.Empty(fs.Changes);
    }

    /// <summary>A License over a file storage on <paramref name="fs"/>, recording what it asks the server (which answers 503).</summary>
    private static (License License, List<string> Asked) LicenseOver(MemoryFileSystem fs, string? migrateFrom = null, TimeProvider? time = null)
    {
        var asked = new List<string>();
        var license = new License(new LicenseConfig
        {
            Product = "sluice",
            ApiBaseUrl = "https://api.test",
            PublicJwk = new TestSigner().Jwk,
            TimeProvider = time,
            Adapters = new LicenseAdapters
            {
                Storage = Over(fs, migrateFrom: migrateFrom),
                Http = new AnsweringHttp(asked),
                Fingerprint = new FixedFingerprinter("fp-test"),
            },
        });
        return (license, asked);
    }

    private sealed class AnsweringHttp(List<string> asked) : IHttpAdapter
    {
        public Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (asked) asked.Add(url);
            return Task.FromResult(new HttpResult(503, new JsonObject()));
        }
    }

    // --- a status() behind a call that has not finished ---------------------------------

    /// <summary>A status stuck on its read, and time past its grace: the next call runs read-only.</summary>
    private static Task<(LicenseStatus Answer, MemoryFileSystem.Hold Stuck)> BehindAStuckRead(MemoryFileSystem fs, string? migrateFrom = null) =>
        BehindAStuckRead(fs, license => license.StatusAsync(), migrateFrom);

    /// <summary>A status stuck on its read, and time past its grace: <paramref name="call"/> runs read-only.</summary>
    private static async Task<(T Answer, MemoryFileSystem.Hold Stuck)> BehindAStuckRead<T>(MemoryFileSystem fs, Func<License, Task<T>> call, string? migrateFrom = null)
    {
        var time = new ManualTime();
        var (license, _) = LicenseOver(fs, migrateFrom, time);
        var stuck = fs.HoldNext(Op.ReadFile);
        _ = license.StatusAsync();
        await Eventually.Until(() => stuck.Reached, "the stuck read");
        var later = call(license);
        time.Advance(LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15));
        return (await later.WaitAsync(TimeSpan.FromSeconds(10)), stuck);
    }

    [Fact]
    public async Task Behind_a_stuck_call_a_purchase_link_sets_no_file_that_does_not_parse_aside_nor_writes_in_its_place()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"bad" });
        var (error, stuck) = await BehindAStuckRead(fs, async license =>
        {
            try
            {
                await license.PurchaseUrlAsync("https://buy.test/x");
                return (Exception?)null;
            }
            catch (Exception thrown)
            {
                return thrown;
            }
        });
        // Read as no state (no secret held), and refused, as a stalled call with none held is.
        Assert.Contains("has not finished", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }

    [Fact]
    public async Task Behind_a_stuck_call_a_purchase_link_is_made_from_an_older_path_s_secret_without_copying_it()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "keygrant-legacy", "sluice.json");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fs = new MemoryFileSystem(new() { [legacy] = $"{{\"purchaseToken\":\"{PurchaseUrlTests.Secret}\",\"purchaseTokenAt\":{now}}}" });
        var (answer, stuck) = await BehindAStuckRead(fs, license => license.PurchaseUrlAsync("https://buy.test/x"), legacy);
        Assert.Equal($"https://buy.test/x?client_reference_id={PurchaseUrlTests.Reference}", answer);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }

    [Fact]
    public async Task Behind_a_stuck_call_does_not_set_a_file_that_does_not_parse_aside_nor_write_in_its_place()
    {
        var fs = new MemoryFileSystem(new() { [State] = "{\"bad" });
        var (answer, stuck) = await BehindAStuckRead(fs);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed, Stalled = true }, answer);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }

    [Fact]
    public async Task Behind_a_stuck_call_reads_an_older_path_s_state_without_copying_it()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "keygrant-legacy", "sluice.json");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fs = new MemoryFileSystem(new() { [legacy] = $"{{\"trialStart\":{now - 86_400_000},\"trialEndsAt\":{now + 13 * 86_400_000L}}}" });
        var (answer, stuck) = await BehindAStuckRead(fs, legacy);
        // Read, and answered from: a stored end with no trial lease runs no trial (nothing read would be unlicensed).
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial, Stalled = true }, answer);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }

    [Fact]
    public async Task Behind_a_stuck_call_recovers_a_temp_for_a_missing_state_writing_nothing()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fs = new MemoryFileSystem(new() { [$"{State}.1.1.a.tmp"] = $"{{\"trialStart\":{now - 86_400_000},\"trialEndsAt\":{now + 13 * 86_400_000L}}}" });
        var (answer, stuck) = await BehindAStuckRead(fs);
        // Read, and answered from: a stored end with no trial lease runs no trial (nothing read would be unlicensed).
        Assert.Equal(new LicenseStatus { State = LicenseState.Expired, Reason = LicenseReason.Trial, Stalled = true }, answer);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }

    [Fact]
    public async Task Makes_the_upgrade_link_from_an_older_path_s_state_without_copying_it()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "keygrant-legacy", "sluice.json");
        var fs = new MemoryFileSystem(new() { [legacy] = Saved });
        var (license, _) = LicenseOver(fs, legacy);
        Assert.Equal("https://buy.test/upgrade?client_reference_id=act_1", await license.UpgradeUrlAsync("https://buy.test/upgrade"));
        Assert.Empty(fs.Changes);
    }

    [Fact]
    public async Task Behind_a_stuck_call_is_unknown_still_writing_nothing_when_the_state_cannot_be_read()
    {
        var fs = new MemoryFileSystem(new() { [State] = Saved });
        fs.FailAlways(Op.ReadFile, "EBUSY");
        var (answer, stuck) = await BehindAStuckRead(fs);
        Assert.Equal(new LicenseStatus { State = LicenseState.Unknown, Reason = LicenseReason.Storage, Stalled = true }, answer);
        Assert.Empty(fs.Changes);
        stuck.Release();
    }
}
