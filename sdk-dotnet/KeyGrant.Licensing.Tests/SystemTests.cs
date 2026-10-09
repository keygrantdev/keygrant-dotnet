using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The real readers and writers, on the real operating system: the files and folders the default
/// storage uses, symbolic and hard links, files another process holds, and the machine-id sources
/// (the registry on Windows, <c>ioreg</c> on macOS, <c>/etc</c> and <c>/proc</c> on Linux). A test for
/// another system, or a capability this one lacks, is reported skipped.
/// </summary>
public class SystemTests
{
    private static readonly IStorageFileSystem Fs = SystemStorageFileSystem.Instance;

    /// <summary>A temp folder for one test, removed after it.</summary>
    private static async Task InTemp(Func<string, Task> test)
    {
        var dir = Directory.CreateTempSubdirectory("keygrant-sys-").FullName;
        try
        {
            await test(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static FileStorage Storage(string path, List<int>? slept = null, Func<int, Task>? sleep = null) =>
        new(path, null, SystemStorageFileSystem.Instance, sleep ?? (ms => { slept?.Add(ms); return Task.CompletedTask; }), null);

    private static long Ms(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;

    // --- the default storage's file system --------------------------------------------------

    [Fact]
    public Task Creates_flushes_renames_lists_times_and_removes_real_files() => InTemp(async dir =>
    {
        var nested = Path.Combine(dir, "a", "b");
        await Fs.MkdirAsync(nested);
        var temp = Path.Combine(nested, "state.json.1.tmp");
        var file = await Fs.CreateAsync(temp);
        await file.WriteAsync("{\"key\":\"K\"}");
        await file.SyncAsync();
        await file.CloseAsync();
        Assert.Equal("{\"key\":\"K\"}", await Fs.ReadFileAsync(temp));
        // CreateNew: never over a file that is there.
        await Assert.ThrowsAnyAsync<IOException>(() => Fs.CreateAsync(temp));

        var state = Path.Combine(nested, "state.json");
        await File.WriteAllTextAsync(state, "old");
        await Fs.RenameAsync(temp, state);
        Assert.Equal("{\"key\":\"K\"}", await Fs.ReadFileAsync(state));
        Assert.Equal(["state.json"], await Fs.ReadDirAsync(nested));
        Assert.InRange(await Fs.MtimeMsAsync(state), Ms(DateTime.UtcNow) - 60_000, Ms(DateTime.UtcNow) + 60_000);
        await Fs.SyncDirAsync(nested);

        await Fs.RemoveAsync(state);
        await Fs.RemoveAsync(state);
        await Fs.RemoveAsync(Path.Combine(dir, "no-such-folder", "x"));
        Assert.Empty(await Fs.ReadDirAsync(nested));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Fs.MtimeMsAsync(state));
        Assert.True(StorageErrors.Missing(await Assert.ThrowsAnyAsync<IOException>(() => Fs.ReadFileAsync(state))));
    });

    [UnixFact]
    public Task Flushes_a_folder_on_POSIX_by_opening_and_syncing_it() => InTemp(async dir =>
    {
        await Fs.SyncDirAsync(dir);
        // Opened for real: a folder that is not there cannot be.
        await Assert.ThrowsAsync<IOException>(() => Fs.SyncDirAsync(Path.Combine(dir, "missing")));
    });

    [HardLinkFact]
    public Task Replaces_the_state_rather_than_writing_into_it_a_link_to_the_old_state_keeps_the_old_state() => InTemp(async dir =>
    {
        // A write in place changes the one file every name for it shares; a rename puts a new file
        // under the name and leaves the old one whole.
        var path = Path.Combine(dir, "sluice.json");
        var storage = new FileStorage(path);
        await storage.WriteAsync(new StoredState { Key = "K", LastSeen = 1 });
        Assert.True(HardLink.TryCreate(path, Path.Combine(dir, "old-state")));
        await storage.WriteAsync(new StoredState { Key = "K", LastSeen = 2 });
        AssertJson.Equal(new JsonObject { ["key"] = "K", ["lastSeen"] = 1 }, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "old-state"))));
        AssertJson.Equal(new JsonObject { ["key"] = "K", ["lastSeen"] = 2 }, JsonNode.Parse(await File.ReadAllTextAsync(path)));
    });

    [Fact]
    public Task Reads_a_state_file_that_begins_with_a_byte_order_mark_whole_and_never_sets_it_aside() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"key\":\"K\",\"activationId\":\"act_1\"}")]);
        var storage = Storage(path);
        Assert.Equal(new StoredState { Key = "K", ActivationId = "act_1" }, await storage.ReadAsync(false));
        Assert.True(await storage.UpdateAsync(s => s with { LastSeen = 5 }));
        Assert.Equal(["sluice.json"], Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!));
        Assert.Equal(new StoredState { Key = "K", ActivationId = "act_1", LastSeen = 5, Rev = 1 }, await storage.ReadAsync(false));
    });

    [Fact]
    public Task Keeps_what_an_Electron_build_stored_that_this_SDK_cannot_type_through_its_own_saves() => InTemp(async dir =>
    {
        // A later SDK's refusal word, a number written as text, a field this SDK has never heard of.
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllTextAsync(path, "{\"key\":\"K\",\"refused\":\"suspended\",\"checkedMajor\":\"4\",\"newer\":{\"a\":1},\"rev\":3}");
        var storage = Storage(path);
        Assert.True(await storage.UpdateAsync(s => s with { LastSeen = 9 }));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(("suspended", "4", 9L, 4L), (saved["refused"]!.GetValue<string>(), saved["checkedMajor"]!.GetValue<string>(), saved["lastSeen"]!.GetValue<long>(), saved["rev"]!.GetValue<long>()));
        AssertJson.Equal(new JsonObject { ["a"] = 1 }, saved["newer"]);
    });

    [Fact]
    public Task Shares_its_reads_with_another_process_writing_the_file() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllTextAsync(path, "{\"key\":\"K\"}");
        // Another instance holds the file open for writing, sharing it as libuv does.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, SystemStorageFileSystem.ShareAll);
        Assert.Equal(new StoredState { Key = "K" }, await Storage(path).ReadAsync(false));
    });

    [WindowsFact]
    public Task Reads_a_file_another_process_holds_exclusively_as_busy_tried_again_and_then_failed() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllTextAsync(path, "{\"key\":\"K\"}");
        var slept = new List<int>();
        IOException failure;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = await Assert.ThrowsAnyAsync<IOException>(() => Storage(path, slept).ReadAsync(false));
        }
        // ERROR_SHARING_VIOLATION: what Windows says while an antivirus scan holds the file.
        Assert.Equal(unchecked((int)0x80070020), failure.HResult);
        Assert.Equal("EBUSY", StorageErrors.CodeOf(failure));
        Assert.Equal(FileStorage.ReadBackoffMs, slept);
        Assert.Equal(new StoredState { Key = "K" }, await Storage(path).ReadAsync(false));
    });

    [WindowsFact]
    public Task Tries_a_rename_over_a_file_another_process_reads_again_as_busy_until_it_lets_go() => InTemp(async dir =>
    {
        var path = Path.Combine(dir, "sluice.json");
        await File.WriteAllTextAsync(path, "{\"key\":\"OLD\"}");
        // Held as our own reads hold it: sharing everything, yet Windows refuses a rename over it,
        // ERROR_ACCESS_DENIED, which is tried again as busy (as the reference tries EPERM again).
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, SystemStorageFileSystem.ShareAll);
        var temp = Path.Combine(dir, "sluice.json.1.tmp");
        await File.WriteAllTextAsync(temp, "{\"key\":\"NEWER\"}");
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Fs.RenameAsync(temp, path));
        Assert.Equal((unchecked((int)0x80070005), "EACCES", true), (refused.HResult, StorageErrors.CodeOf(refused), StorageErrors.Busy(refused)));
        File.Delete(temp);
        var sleeps = 0;
        var storage = Storage(path, sleep: _ =>
        {
            sleeps++;
            held.Dispose();
            return Task.CompletedTask;
        });
        await storage.WriteAsync(new StoredState { Key = "NEW" });
        Assert.True(sleeps >= 1, "the rename was refused while the file was held, and tried again");
        Assert.Equal(new StoredState { Key = "NEW" }, await Storage(path).ReadAsync(false));
        Assert.Equal(["sluice.json"], Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!));
    });

    public static TheoryData<string, Exception, string?, bool> Errors => new()
    {
        { "a sharing violation", new IOException("busy", unchecked((int)0x80070020)), "EBUSY", true },
        { "a lock violation", new IOException("locked", unchecked((int)0x80070021)), "EBUSY", true },
        { "access denied (a rename over an open file)", new UnauthorizedAccessException("denied"), "EACCES", true },
        { "a file not there", new FileNotFoundException("gone"), "ENOENT", false },
        { "a folder not there", new DirectoryNotFoundException("gone"), "ENOENT", false },
        { "a file already there", new IOException("exists", unchecked((int)0x80070050)), "EEXIST", false },
        { "a file already there (ERROR_ALREADY_EXISTS)", new IOException("exists", unchecked((int)0x800700B7)), "EEXIST", false },
        { "a full disk", new IOException("full", unchecked((int)0x80070070)), "ENOSPC", false },
        { "an I/O error", new IOException("EIO"), null, false },
        { "a revision conflict", new StateConflictException(), null, false },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Names_a_file_error_as_the_reference_does_and_tries_only_a_busy_one_again(string _, Exception error, string? code, bool busy)
    {
        Assert.Equal(code, StorageErrors.CodeOf(error));
        Assert.Equal(busy, StorageErrors.Busy(error));
        Assert.Equal(code == "ENOENT", StorageErrors.Missing(error));
    }

    // --- fs.stat follows symbolic links -----------------------------------------------------------

    [SymlinkFact]
    public Task Times_a_file_through_a_symbolic_link_by_the_file_it_names_not_the_link() => InTemp(async dir =>
    {
        var target = Path.Combine(dir, "machine-id");
        await File.WriteAllTextAsync(target, "a1b2c3d4e5f6\n");
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, written);
        // The link itself is made now: its own time is today.
        var link = Path.Combine(dir, "link");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(Ms(written), (long)FileTimes.LastWriteMs(link));
        Assert.Equal(Ms(written), (long)(await SystemMachineIdSource.Instance.MtimeMsAsync(link))!.Value);
        Assert.Equal(Ms(written), (long)await Fs.MtimeMsAsync(link));
        // A link to nothing is nothing there, as stat says.
        File.Delete(target);
        Assert.Throws<FileNotFoundException>(() => FileTimes.LastWriteMs(link));
    });

    [SymlinkFact]
    public Task Judges_a_machine_id_behind_a_link_by_when_the_id_itself_was_written() => InTemp(async dir =>
    {
        // /etc/machine-id a link to a file: an id written long before this boot is an id, whatever
        // the link's own time says.
        var id = Path.Combine(dir, "machine-id-file");
        await File.WriteAllTextAsync(id, "a1b2c3d4e5f6\n");
        File.SetLastWriteTimeUtc(id, DateTime.UtcNow.AddDays(-90));
        var link = Path.Combine(dir, "machine-id-link");
        File.CreateSymbolicLink(link, id);
        var stat = Path.Combine(dir, "stat");
        await File.WriteAllTextAsync(stat, $"cpu 1 2 3\nbtime {DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60}\n");
        var mountinfo = Path.Combine(dir, "mountinfo");
        await File.WriteAllTextAsync(mountinfo, "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n");
        var source = new MappedLinuxSource(new()
        {
            ["/etc/machine-id"] = link,
            ["/var/lib/dbus/machine-id"] = Path.Combine(dir, "missing"),
            ["/proc/stat"] = stat,
            ["/proc/self/mountinfo"] = mountinfo,
        });
        Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), await MachineId.ReadAsync(source, TimeSpan.FromSeconds(5), TimeProvider.System));

        // And one written at this boot, behind a link made long before, is no id.
        File.SetLastWriteTimeUtc(id, DateTime.UtcNow);
        Assert.Equal(MachineIdReading.Unavailable, await MachineId.ReadAsync(source, TimeSpan.FromSeconds(5), TimeProvider.System));
    });

    /// <summary>The real file reads and times, with Linux's paths pointed at a test's own files.</summary>
    private sealed class MappedLinuxSource(Dictionary<string, string> paths) : IMachineIdSource
    {
        public string Platform => "linux";

        public Task<string?> ReadMachineGuidAsync() => throw new NotSupportedException();

        public Task<string> RunAsync(string command, IReadOnlyList<string> args, TimeSpan timeout) => throw new NotSupportedException();

        public Task<string> ReadFileAsync(string path) => SystemMachineIdSource.Instance.ReadFileAsync(paths.GetValueOrDefault(path, path));

        public Task<double?> MtimeMsAsync(string path) => SystemMachineIdSource.Instance.MtimeMsAsync(paths.GetValueOrDefault(path, path));
    }

    // --- the machine-id sources on each system ----------------------------------------------------

    [LinuxFact]
    public async Task Reads_this_Linux_machine_s_id_from_its_files_judging_mountinfo_and_the_boot_time()
    {
        var source = SystemMachineIdSource.Instance;
        Assert.Equal("linux", source.Platform);
        Assert.NotNull(MachineId.MachineIdFilesystem(await source.ReadFileAsync("/proc/self/mountinfo")));
        Assert.Matches(new Regex("^btime [0-9]+$", RegexOptions.Multiline), await source.ReadFileAsync("/proc/stat"));
        var reading = await MachineId.ReadAsync(source, TimeSpan.FromSeconds(5), TimeProvider.System);
        Assert.NotEqual(MachineIdKind.Transient, reading.Kind);
        if (reading.Kind == MachineIdKind.Id)
        {
            var files = new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" }.Where(File.Exists).Select(f => Wire.JsTrim(File.ReadAllText(f)));
            Assert.Contains(reading.Id, files);
            Assert.NotNull(await source.MtimeMsAsync(File.Exists("/etc/machine-id") ? "/etc/machine-id" : "/var/lib/dbus/machine-id"));
        }
    }

    [MacFact]
    public async Task Reads_this_Mac_s_IOPlatformUUID_by_running_ioreg()
    {
        var source = SystemMachineIdSource.Instance;
        Assert.Equal("darwin", source.Platform);
        var output = await source.RunAsync(MachineId.Ioreg, MachineId.IoregArgs, TimeSpan.FromSeconds(10));
        var uuid = Regex.Match(output, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
        Assert.True(uuid.Success, output);
        Assert.Equal(MachineIdReading.Of(uuid.Groups[1].Value), await MachineId.ReadAsync(source, TimeSpan.FromSeconds(10), TimeProvider.System));
    }

    [WindowsFact]
    public async Task Reads_this_Windows_machine_s_MachineGuid_from_the_registry()
    {
        var source = SystemMachineIdSource.Instance;
        Assert.Equal("win32", source.Platform);
        var guid = await source.ReadMachineGuidAsync();
        Assert.Matches("^[0-9a-fA-F-]{36}$", guid);
        Assert.Equal(MachineIdReading.Of(guid!), await MachineId.ReadAsync(source, TimeSpan.FromSeconds(10), TimeProvider.System));
    }

    [UnixFact]
    public async Task Tells_a_command_that_said_no_from_one_that_did_not_finish_on_POSIX()
    {
        var source = SystemMachineIdSource.Instance;
        var said = await Assert.ThrowsAsync<CommandExitException>(() => source.RunAsync("/bin/sh", ["-c", "exit 3"], TimeSpan.FromSeconds(10)));
        Assert.Equal((3, MachineIdKind.Unavailable), (said.ExitCode, MachineId.FailureKind(said)));
        // Killed by a signal: .NET reports exit code 137, read as did-not-finish.
        var killed = await Assert.ThrowsAsync<CommandKilledException>(() => source.RunAsync("/bin/sh", ["-c", "kill -9 $$"], TimeSpan.FromSeconds(10)));
        Assert.Equal(MachineIdKind.Transient, MachineId.FailureKind(killed));
        await Assert.ThrowsAsync<CommandKilledException>(() => source.RunAsync("/bin/sh", ["-c", "sleep 5"], TimeSpan.FromMilliseconds(200)));
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => source.RunAsync("/no/such/command", [], TimeSpan.FromSeconds(10)));
        Assert.Equal(MachineIdKind.Unavailable, MachineId.FailureKind(missing));
    }

    [WindowsFact]
    public async Task Tells_a_command_that_said_no_from_one_that_did_not_finish_on_Windows()
    {
        var source = SystemMachineIdSource.Instance;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var said = await Assert.ThrowsAsync<CommandExitException>(() => source.RunAsync(cmd, ["/c", "exit 3"], TimeSpan.FromSeconds(10)));
        Assert.Equal((3, MachineIdKind.Unavailable), (said.ExitCode, MachineId.FailureKind(said)));
        await Assert.ThrowsAsync<CommandKilledException>(() => source.RunAsync(cmd, ["/c", "ping -n 6 127.0.0.1 >NUL"], TimeSpan.FromMilliseconds(300)));
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => source.RunAsync(@"C:\no\such\command.exe", [], TimeSpan.FromSeconds(10)));
        Assert.IsType<Win32Exception>(missing);
        Assert.Equal(MachineIdKind.Unavailable, MachineId.FailureKind(missing));
    }
}
