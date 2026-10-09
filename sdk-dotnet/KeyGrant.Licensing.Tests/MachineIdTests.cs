using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.FakeMachineIdSource;

namespace KeyGrant.Licensing.Tests;

public class MachineIdTests
{
    private static readonly TimeSpan Status = TimeSpan.FromMilliseconds(MachineId.TimeoutMs);
    private static readonly string Fallback = DeviceFingerprints.Hostname();
    private static readonly MachineIdReading Unavailable = MachineIdReading.Unavailable;
    private static readonly MachineIdReading Transient = MachineIdReading.Transient;

    private static Task<MachineIdReading> Read(IMachineIdSource source) => MachineId.ReadAsync(source, Status, TimeProvider.System);

    private static FakeMachineIdSource Windows(Func<Task<string?>> guid) => new() { Platform = "win32", MachineGuid = guid };

    private static Func<Task<string?>> Value(string? value) => () => Task.FromResult(value);

    private static Func<Task<string?>> Throws(Exception error) => () => Task.FromException<string?>(error);

    private static FakeMachineIdSource Linux(Dictionary<string, Func<Task<string>>> files, Func<string, Task<double?>>? mtime = null) =>
        new() { Platform = "linux", Files = files, Mtime = mtime ?? (_ => Task.FromResult<double?>(null)) };

    private const string MacOutput =
        "+-o J314sAP  <class IOPlatformExpertDevice, id 0x100000110, registered>\n" +
        "    {\n" +
        "      \"IOPlatformSerialNumber\" = \"C02XXXXXXX\"\n" +
        "      \"IOPlatformUUID\" = \"564D8A1C-3F6E-4E3B-9F2A-1B2C3D4E5F60\"\n" +
        "    }";

    // --- Windows: MachineGuid from the 64-bit registry view ----------------------------

    [Fact]
    public async Task Reads_the_Windows_MachineGuid_and_hashes_it_never_sending_it()
    {
        Assert.Equal(MachineIdReading.Of(TestGuid), await Read(Windows(Value(TestGuid))));
        var fingerprint = await new MachineFingerprinter(Windows(Value(TestGuid)), null).GetAsync(CancellationToken.None);
        Assert.Equal(DeviceFingerprints.Hash($"machine-id|{TestGuid}"), fingerprint);
        Assert.DoesNotContain("3f2504e0", fingerprint);
    }

    [Theory]
    [InlineData("  3f2504e0-4f89-11d3-9a0c-0305e82c3301\r\n")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301 trailing words")]
    public async Task Reads_the_value_as_reg_query_s_output_gave_it_the_first_word_trimmed(string value)
    {
        Assert.Equal(MachineIdReading.Of(TestGuid), await Read(Windows(Value(value))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Has_no_id_for_good_when_the_value_is_not_there_or_empty(string? value)
    {
        Assert.Equal(Unavailable, await Read(Windows(Value(value))));
    }

    [Fact]
    public async Task Has_no_id_for_good_when_the_registry_refuses_this_process_and_cannot_tell_on_anything_unexpected()
    {
        Assert.Equal(Unavailable, await Read(Windows(Throws(new UnauthorizedAccessException()))));
        Assert.Equal(Unavailable, await Read(Windows(Throws(new System.Security.SecurityException()))));
        Assert.Equal(Transient, await Read(Windows(Throws(new IOException("the registry hiccupped")))));
        Assert.Equal(Transient, await Read(Windows(() => throw new InvalidOperationException("thrown at once"))));
    }

    // --- macOS: IOPlatformUUID from ioreg ------------------------------------------------

    [Fact]
    public async Task Reads_the_macOS_IOPlatformUUID_with_usr_sbin_ioreg_time_boxed()
    {
        var source = new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Text(MacOutput) } };
        Assert.Equal(MachineIdReading.Of("564D8A1C-3F6E-4E3B-9F2A-1B2C3D4E5F60"), await Read(source));
        var run = Assert.Single(source.Runs);
        Assert.Equal(("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice", Status), (run.Command, string.Join(' ', run.Args), run.Timeout));
    }

    [Fact]
    public async Task Reads_no_id_from_ioreg_output_that_holds_none_and_classifies_how_ioreg_failed()
    {
        Assert.Equal(Unavailable, await Read(new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Text("no uuid here") } }));
        Assert.Equal(Unavailable, await Read(new FakeMachineIdSource { Platform = "darwin" }));
        Assert.Equal(Unavailable, await Read(new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Failing(new CommandExitException(1)) } }));
        Assert.Equal(Transient, await Read(new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Failing(new CommandKilledException()) } }));
        Assert.Equal(Transient, await Read(new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Failing(new IOException("EAGAIN")) } }));
    }

    [Fact]
    public async Task Gives_no_fingerprint_once_the_time_box_is_out_when_the_command_never_finishes()
    {
        var time = new ManualTime();
        var hanging = new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Never() } };
        var pending = MachineId.ReadAsync(hanging, Status, time);
        await Eventually.Until(() => time.HasTimerDueIn(Status + TimeSpan.FromMilliseconds(500)), "the time box");
        time.Advance(Status);
        await Eventually.Quiet();
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(Transient, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // --- Linux: /etc/machine-id, else the D-Bus one ---------------------------------------

    [Fact]
    public async Task Reads_etc_machine_id_on_Linux_else_the_D_Bus_one_trimmed()
    {
        Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), await Read(Linux(new() { ["/etc/machine-id"] = Text("a1b2c3d4e5f6\n") })));
        Assert.Equal(MachineIdReading.Of("dbus0001"), await Read(Linux(new() { ["/var/lib/dbus/machine-id"] = Text("dbus0001\n") })));
        // An empty systemd file, or one not ours to read, is not an id: the D-Bus one is read.
        Assert.Equal(MachineIdReading.Of("dbus0002"), await Read(Linux(new() { ["/etc/machine-id"] = Text("\n"), ["/var/lib/dbus/machine-id"] = Text("dbus0002") })));
        Assert.Equal(MachineIdReading.Of("dbus0003"), await Read(Linux(new()
        {
            ["/etc/machine-id"] = Failing(new UnauthorizedAccessException()),
            ["/var/lib/dbus/machine-id"] = Text("dbus0003"),
        })));
    }

    private const string Root = "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw";
    private const string OverMachineId = "29 23 0:25 /machine-id /etc/machine-id ro,relatime shared:5 - tmpfs tmpfs rw";
    private const string RootTmpfs = "1 0 0:1 / / rw,relatime shared:1 - tmpfs tmpfs rw";

    public static TheoryData<string, string[], bool> Mounts => new()
    {
        { "nothing mounted at /etc/machine-id", [Root], true },
        { "the path as a mount's root, not its mount point", [Root, "30 23 0:25 /etc/machine-id /run/host-id ro,relatime - tmpfs tmpfs rw"], true },
        { "a mount point it is only a prefix of", [Root, "31 23 0:26 / /etc/machine-id.d rw,relatime - tmpfs tmpfs rw"], true },
        { "an impermanent root: / itself a tmpfs", [RootTmpfs], false },
        { "an impermanent /etc: a tmpfs", [Root, "2 1 0:2 / /etc rw,relatime shared:2 - tmpfs none rw"], false },
        { "a tmpfs /etc with no optional field", [Root, "2 1 0:2 / /etc rw - tmpfs none rw"], false },
        { "a tmpfs /etc with two optional fields", [Root, "2 1 0:2 / /etc rw shared:2 master:1 - tmpfs tmpfs rw"], false },
        { "an overmount listed BEFORE the mount it covers", ["5 29 8:1 /etc/machine-id /etc/machine-id ro - ext4 /dev/sda1 rw", Root, OverMachineId], true },
        { "a mount point that is a prefix of the path but not a parent of it", [Root, "2 1 0:2 / /et rw - tmpfs tmpfs rw"], true },
        { "systemd's transient id: a /run tmpfs file bound over it", [Root, OverMachineId], false },
        { "an escaped mount point that unescapes to /etc, a tmpfs", [Root, "2 1 0:2 / /et\\143 rw - tmpfs tmpfs rw"], false },
        { "a ramfs /etc", [Root, "2 1 0:2 / /etc rw master:4 - ramfs ramfs rw"], false },
        { "a stable id bound in from a disk", [Root, "3 1 8:1 /etc/machine-id /etc/machine-id ro,relatime - ext4 /dev/sda1 rw"], true },
        { "a persisted id bound over an impermanent root", [RootTmpfs, "3 1 8:2 /persist/etc/machine-id /etc/machine-id rw - btrfs /dev/sda2 rw"], true },
        { "a disk bind mounted over a tmpfs one at the same point", [Root, OverMachineId, "5 29 8:1 /etc/machine-id /etc/machine-id ro - ext4 /dev/sda1 rw"], true },
        {
            "two mounts at one point, neither over the other, a tmpfs listed last",
            [Root, "3 1 8:1 /etc/machine-id /etc/machine-id ro - ext4 /dev/sda1 rw", "4 1 0:25 /machine-id /etc/machine-id ro - tmpfs tmpfs rw"],
            false
        },
        {
            "two mounts at one point, neither over the other, a disk listed last",
            [Root, "4 1 0:25 /machine-id /etc/machine-id ro - tmpfs tmpfs rw", "3 1 8:1 /etc/machine-id /etc/machine-id ro - ext4 /dev/sda1 rw"],
            true
        },
    };

    [Theory]
    [MemberData(nameof(Mounts))]
    public async Task Classifies_by_the_filesystem_holding_the_id(string _, string[] lines, bool isId)
    {
        var source = Linux(new() { ["/proc/self/mountinfo"] = Text(string.Join('\n', lines) + "\n"), ["/etc/machine-id"] = Text("a1b2c3d4e5f6\n") });
        Assert.Equal(isId ? MachineIdReading.Of("a1b2c3d4e5f6") : Unavailable, await Read(source));
    }

    [Fact]
    public void Reads_no_filesystem_from_mountinfo_it_cannot_parse()
    {
        Assert.Null(MachineId.MachineIdFilesystem(""));
        Assert.Null(MachineId.MachineIdFilesystem("garbage\n1 2 3\n"));
        Assert.Equal("tmpfs", MachineId.MachineIdFilesystem(OverMachineId));
    }

    [Fact]
    public async Task Has_no_id_to_hold_a_device_to_when_systemd_s_transient_id_is_mounted_so_the_host_name()
    {
        var files = new Dictionary<string, Func<Task<string>>> { ["/proc/self/mountinfo"] = Text($"{Root}\n{OverMachineId}\n"), ["/etc/machine-id"] = Text("transient-id\n") };
        var identity = await new MachineFingerprinter(Linux(files), null).IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.Equal((Fallback, (DeviceKind?)DeviceKind.Hostname), (identity!.Fingerprint, identity.Kind));
    }

    [Fact]
    public async Task Reads_the_id_as_always_when_mountinfo_cannot_be_read()
    {
        foreach (var unreadable in new Exception?[] { null, new IOException("EIO"), new UnauthorizedAccessException() })
        {
            var files = new Dictionary<string, Func<Task<string>>> { ["/etc/machine-id"] = Text("a1b2c3d4e5f6\n") };
            if (unreadable is not null) files["/proc/self/mountinfo"] = Failing(unreadable);
            Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), await Read(Linux(files)));
        }
    }

    // --- an id written at this boot ----------------------------------------------------------

    private const long Btime = 1_757_000_000;
    private const long Booted = Btime * 1000;

    /// <summary>A Linux machine with a persistent root, booted at BTIME, whose id file was written at <paramref name="mtimeMs"/>.</summary>
    private static FakeMachineIdSource BootedAt(double? mtimeMs, Exception? statFails = null, string? procStat = null, Exception? procStatFails = null) => Linux(
        new()
        {
            ["/proc/self/mountinfo"] = Text($"{Root}\n"),
            ["/etc/machine-id"] = Text("a1b2c3d4e5f6\n"),
            ["/proc/stat"] = procStatFails is not null ? Failing(procStatFails) : Text(procStat ?? $"cpu 1 2 3\nbtime {Btime}\n"),
        },
        _ => statFails is not null ? Task.FromException<double?>(statFails) : Task.FromResult(mtimeMs));

    [Fact]
    public async Task Is_no_id_when_the_file_is_newer_than_the_boot()
    {
        Assert.Equal(Unavailable, await Read(BootedAt(Booted + 3_000)));
    }

    [Fact]
    public async Task Is_the_id_when_the_file_dates_from_before_the_boot()
    {
        Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), await Read(BootedAt(Booted - 90 * 86_400_000d)));
    }

    [Fact]
    public async Task Counts_the_slack_before_btime_as_this_boot_and_not_a_millisecond_more()
    {
        Assert.Equal(Unavailable, await Read(BootedAt(Booted - MachineId.BootSlackMs)));
        Assert.Equal(MachineIdReading.Of("a1b2c3d4e5f6"), await Read(BootedAt(Booted - MachineId.BootSlackMs - 1)));
    }

    [Fact]
    public async Task Reads_the_id_as_always_when_proc_stat_or_the_file_s_time_cannot_be_read()
    {
        var id = MachineIdReading.Of("a1b2c3d4e5f6");
        Assert.Equal(id, await Read(BootedAt(Booted + 3_000, procStatFails: new FileNotFoundException())));
        Assert.Equal(id, await Read(BootedAt(Booted + 3_000, procStat: "cpu 1 2 3\n")));
        Assert.Equal(id, await Read(BootedAt(null, statFails: new UnauthorizedAccessException())));
        // A source that cannot say when the file was written: its age is not judged.
        Assert.Equal(id, await Read(BootedAt(null)));
    }

    // --- what a failed read means --------------------------------------------------------------

    public static TheoryData<string, Exception, string> Failures => new()
    {
        { "a missing file", new FileNotFoundException(), "Unavailable" },
        { "a missing folder", new DirectoryNotFoundException(), "Unavailable" },
        { "access denied", new UnauthorizedAccessException(), "Unavailable" },
        { "a command not there", new Win32Exception(2), "Unavailable" },
        { "a command that said no", new CommandExitException(1), "Unavailable" },
        { "a command killed at its time box", new CommandKilledException(), "Transient" },
        { "our own time box", new TimeoutException(), "Transient" },
        { "an I/O error (EIO)", new IOException("EIO"), "Transient" },
        { "anything unexpected", new InvalidOperationException("boom"), "Transient" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Reads_a_failure_as_for_good_or_this_time_only(string _, Exception error, string kind)
    {
        Assert.Equal(kind, MachineId.FailureKind(error).ToString());
    }

    [Fact]
    public async Task Never_throws_a_reader_that_throws_at_once_gives_no_fingerprint_and_get_says_it_could_not_read()
    {
        var throwing = new FakeMachineIdSource
        {
            Platform = "win32",
            MachineGuid = () => throw new InvalidOperationException("spawn failed"),
            Files = { ["/etc/machine-id"] = () => throw new InvalidOperationException("no fs") },
        };
        Assert.Equal(Transient, await Read(throwing));
        Assert.Equal(Transient, await Read(new FakeMachineIdSource { Platform = "linux", Files = { ["/etc/machine-id"] = () => throw new IOException("no fs") } }));
        Assert.Null((await new MachineFingerprinter(throwing, null).IdentifyAsync(Status, CancellationToken.None))!.Fingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MachineFingerprinter(throwing, null).GetAsync(CancellationToken.None));
    }

    // --- the default fingerprinter ---------------------------------------------------------

    [Fact]
    public async Task Survives_a_rename_the_fingerprint_follows_the_machine_id_not_the_host_name()
    {
        var before = await new MachineFingerprinter(Linux(new() { ["/etc/machine-id"] = Text("same-machine") }), null).GetAsync(CancellationToken.None);
        var after = await new MachineFingerprinter(Linux(new() { ["/etc/machine-id"] = Text("same-machine") }), null).GetAsync(CancellationToken.None);
        Assert.Equal(before, after);
        Assert.NotEqual(Fallback, before);
    }

    [Fact]
    public async Task Falls_back_to_the_host_name_for_good_when_the_machine_has_no_id_to_read()
    {
        var sources = new[]
        {
            Windows(Value(null)),
            Windows(Throws(new UnauthorizedAccessException())),
            new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Text("no uuid here") } },
            new FakeMachineIdSource { Platform = "darwin" },
            Linux(new()),
            Linux(new() { ["/etc/machine-id"] = Failing(new UnauthorizedAccessException()), ["/var/lib/dbus/machine-id"] = Failing(new UnauthorizedAccessException()) }),
            new FakeMachineIdSource { Platform = "freebsd" },
        };
        foreach (var source in sources)
        {
            Assert.Equal(Unavailable, await Read(source));
            var fingerprinter = new MachineFingerprinter(source, null);
            Assert.Equal(Fallback, await fingerprinter.GetAsync(CancellationToken.None));
            // Settled: kept, with no host-name alternate besides itself.
            Assert.Equal(Describe(new DeviceIdentity(Fallback, DeviceKind.Hostname, [])), Describe(await fingerprinter.IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None)));
        }
    }

    [Fact]
    public async Task Gives_NO_fingerprint_for_a_read_that_did_not_finish_and_does_not_keep_that()
    {
        var sources = new[]
        {
            Windows(Throws(new IOException("hiccup"))),
            new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Failing(new CommandKilledException()) } },
            new FakeMachineIdSource { Platform = "darwin", Commands = { [MachineId.Ioreg] = Failing(new IOException("EAGAIN")) } },
            Linux(new() { ["/etc/machine-id"] = Failing(new IOException("EIO")) }),
        };
        foreach (var source in sources)
        {
            Assert.Equal(Transient, await Read(source));
            var identity = await new MachineFingerprinter(source, null).IdentifyAsync(Status, CancellationToken.None);
            // Not a stand-in: no fingerprint, and the host-name one only as what it may have been activated under.
            Assert.Equal(Describe(new DeviceIdentity(null, null, [Fallback])), Describe(identity));
        }
    }

    [Fact]
    public async Task Reads_again_after_a_read_that_did_not_finish_and_keeps_the_id_once_it_has_it()
    {
        var first = true;
        var flaky = Windows(() =>
        {
            if (!first) return Task.FromResult<string?>(TestGuid);
            first = false;
            return Task.FromException<string?>(new IOException("hiccup"));
        });
        var fingerprinter = new MachineFingerprinter(flaky, null);
        Assert.Null((await fingerprinter.IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None))!.Fingerprint);
        var machine = DeviceFingerprints.Hash($"machine-id|{TestGuid}");
        Assert.Equal(machine, (await fingerprinter.IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None))!.Fingerprint);
        Assert.Equal(machine, (await fingerprinter.IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None))!.Fingerprint);
        Assert.Equal(2, flaky.GuidReads);
    }

    [Fact]
    public async Task Offers_the_host_name_fingerprint_as_an_alternate_beside_the_machine_id_s()
    {
        var identity = await new MachineFingerprinter(Windows(Value(TestGuid)), null).IdentifyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.Equal(Describe(new DeviceIdentity(DeviceFingerprints.Hash($"machine-id|{TestGuid}"), DeviceKind.Machine, [Fallback])), Describe(identity));
    }

    [Fact]
    public async Task Reads_the_machine_id_once_for_every_fingerprinter_that_shares_what_was_settled()
    {
        var keeper = new MachineFingerprinter.Keeper();
        var source = Windows(Value(TestGuid));
        var a = await new MachineFingerprinter(source, null, keeper).IdentifyAsync(Status, CancellationToken.None);
        var b = await new MachineFingerprinter(source, null, keeper).IdentifyAsync(Status, CancellationToken.None);
        Assert.Equal(DeviceFingerprints.Machine(TestGuid), a!.Fingerprint);
        Assert.Equal((a.Fingerprint, a.Kind), (b!.Fingerprint, b.Kind));
        Assert.Equal(1, source.GuidReads);

        // A read that did not finish is not kept: the next fingerprinter reads again.
        var unsettled = new MachineFingerprinter.Keeper();
        var hiccup = Windows(Throws(new IOException("hiccup")));
        Assert.Null((await new MachineFingerprinter(hiccup, null, unsettled).IdentifyAsync(Status, CancellationToken.None))!.Fingerprint);
        Assert.Null((await new MachineFingerprinter(hiccup, null, unsettled).IdentifyAsync(Status, CancellationToken.None))!.Fingerprint);
        Assert.Equal(2, hiccup.GuidReads);
        Assert.Null(unsettled.Settled);
    }

    [Fact]
    public void Keeps_this_machine_s_fingerprint_for_the_whole_process_and_a_stand_in_s_for_itself()
    {
        // The default, and the one a License builds when the app gives none, share the process's.
        Assert.Same(MachineFingerprinter.ProcessKeeper, new MachineFingerprinter().KeptIn);
        Assert.Same(MachineFingerprinter.ProcessKeeper, new MachineFingerprinter(null, new ManualTime()).KeptIn);
        var source = Windows(Value(TestGuid));
        Assert.NotSame(MachineFingerprinter.ProcessKeeper, new MachineFingerprinter(source, null).KeptIn);
        Assert.NotSame(new MachineFingerprinter(source, null).KeptIn, new MachineFingerprinter(source, null).KeptIn);
    }

    [Fact]
    public async Task Fingerprints_this_machine_the_same_from_every_instance()
    {
        MachineFingerprinter.ProcessKeeper.Forget();
        var a = await new MachineFingerprinter().IdentifyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var b = await new MachineFingerprinter().IdentifyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Matches("^[0-9a-f]{64}$", a!.Fingerprint!);
        Assert.Equal(a.Fingerprint, b!.Fingerprint);
        if (OperatingSystem.IsWindows()) Assert.Equal(DeviceKind.Machine, a.Kind);
    }

    [Fact]
    public async Task Joins_a_read_in_flight_rather_than_start_another_and_reads_afresh_once_it_settled()
    {
        // A hung registry read blocks the pool thread it runs on: every later call joins it.
        var shared = new SharedRead<string?>();
        var started = 0;
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Start()
        {
            Interlocked.Increment(ref started);
            return answer.Task;
        }
        var first = shared.Run(Start);
        var second = shared.Run(Start);
        Assert.Same(first, second);
        Assert.Equal(1, started);
        answer.SetResult(TestGuid);
        Assert.Equal(TestGuid, await second);
        // Settled: the next call reads afresh.
        var again = await shared.Run(() =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<string?>("again");
        });
        Assert.Equal(("again", 2), (again, started));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Reads_the_machine_id_from_the_64_bit_registry_view_whatever_this_process_is()
    {
        // `reg query ... /reg:64`: a 32-bit process under WOW64 sees the 32-bit hive by default.
        Assert.Equal(Microsoft.Win32.RegistryView.Registry64, SystemMachineIdSource.MachineGuidView);
        Assert.Equal(@"SOFTWARE\Microsoft\Cryptography", SystemMachineIdSource.CryptographyKey);
        Assert.Equal(SystemMachineIdSource.ReadMachineGuid(Microsoft.Win32.RegistryView.Registry64), SystemMachineIdSource.ReadMachineGuid(SystemMachineIdSource.MachineGuidView));
    }

    /// <summary>A registry that holds <c>MachineGuid</c> as given, counting its reads, each held until released.</summary>
    private sealed class StandInRegistry(RegistryValue? value, bool held = false) : IMachineGuidRegistry
    {
        private readonly ManualResetEventSlim released = new(!held);
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public void Release() => released.Set();

        public RegistryValue? Read()
        {
            Interlocked.Increment(ref reads);
            released.Wait(TimeSpan.FromSeconds(10));
            return value;
        }
    }

    [Fact]
    public async Task Shares_the_system_source_s_registry_read_in_flight_and_reads_afresh_once_it_settled()
    {
        // A hung hive blocks the pool thread its read runs on: every later call joins that read.
        var registry = new StandInRegistry(new RegistryValue(TestGuid, RegistryType.String), held: true);
        var source = new SystemMachineIdSource(registry);
        var first = source.ReadMachineGuidAsync();
        var second = source.ReadMachineGuidAsync();
        Assert.Same(first, second);
        await Eventually.Until(() => registry.Reads == 1, "the registry read");
        Assert.False(second.IsCompleted);
        registry.Release();
        Assert.Equal(TestGuid, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, registry.Reads);
        Assert.Equal(TestGuid, await source.ReadMachineGuidAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, registry.Reads);
    }

    public static TheoryData<string, int, string?> RegistryValues => new()
    {
        { "a REG_SZ", (int)RegistryType.String, TestGuid },
        { "a REG_EXPAND_SZ", (int)RegistryType.ExpandString, null },
        { "a REG_MULTI_SZ", (int)RegistryType.MultiString, null },
        { "a REG_DWORD", (int)RegistryType.DWord, null },
        { "a REG_QWORD", (int)RegistryType.QWord, null },
        { "a REG_BINARY", (int)RegistryType.Binary, null },
    };

    [Theory]
    [MemberData(nameof(RegistryValues))]
    public async Task Takes_MachineGuid_only_as_a_REG_SZ_as_reg_query_matches_it(string _, int type, string? id)
    {
        // The data as the registry API hands each type over; a string under any type but REG_SZ is no id.
        object data = (RegistryType)type switch
        {
            RegistryType.MultiString => new[] { TestGuid },
            RegistryType.DWord => 42,
            RegistryType.QWord => 42L,
            RegistryType.Binary => new byte[] { 1, 2 },
            _ => TestGuid,
        };
        var source = new SystemMachineIdSource(new StandInRegistry(new RegistryValue(data, (RegistryType)type)));
        Assert.Equal(id, await source.ReadMachineGuidAsync());
        Assert.Null(await new SystemMachineIdSource(new StandInRegistry(null)).ReadMachineGuidAsync());
    }

    [WindowsFact]
    public async Task Reads_the_same_MachineGuid_through_the_registry_API_as_reg_exe_prints_on_this_machine()
    {
        var reading = await MachineId.ReadAsync(SystemMachineIdSource.Instance, TimeSpan.FromSeconds(10), TimeProvider.System);
        var info = new ProcessStartInfo(@"C:\Windows\System32\reg.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "query", @"HKLM\SOFTWARE\Microsoft\Cryptography", "/v", "MachineGuid", "/reg:64" }) info.ArgumentList.Add(arg);
        using var reg = Process.Start(info)!;
        var output = await reg.StandardOutput.ReadToEndAsync();
        await reg.WaitForExitAsync();
        var match = Regex.Match(output, @"MachineGuid\s+REG_SZ\s+(\S+)");
        Assert.True(match.Success, output);
        Assert.Equal(MachineIdReading.Of(match.Groups[1].Value.Trim()), reading);
    }

    private static string Describe(DeviceIdentity? identity) =>
        identity is null ? "none" : $"{identity.Fingerprint ?? "null"} {identity.Kind?.ToString() ?? "unknown"} [{string.Join(",", identity.Alternates)}]";
}
