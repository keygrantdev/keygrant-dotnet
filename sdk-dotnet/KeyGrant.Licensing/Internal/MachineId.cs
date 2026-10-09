using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KeyGrant.Licensing.Internal;

/// <summary>What reading the machine id gave.</summary>
internal enum MachineIdKind
{
    /// <summary>The machine's id.</summary>
    Id,

    /// <summary>There is none to read, and there will not be on the next try either.</summary>
    Unavailable,

    /// <summary>The read did not finish: there is NO fingerprint this time, and the next call reads again.</summary>
    Transient,
}

/// <summary>What reading the machine id gave, and the id when there is one.</summary>
internal readonly record struct MachineIdReading(MachineIdKind Kind, string? Id = null)
{
    public static readonly MachineIdReading Unavailable = new(MachineIdKind.Unavailable);
    public static readonly MachineIdReading Transient = new(MachineIdKind.Transient);

    public static MachineIdReading Of(string id) => new(MachineIdKind.Id, id);
}

/// <summary>A command that ran and exited non-zero: it said no, for good.</summary>
internal sealed class CommandExitException(int exitCode) : Exception($"the command exited {exitCode}")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>A command killed at its time box: it did not finish, this time only.</summary>
internal sealed class CommandKilledException() : Exception("the command was killed at its time box");

/// <summary>Where the machine id is read from: the real system, or a stand-in in tests.</summary>
internal interface IMachineIdSource
{
    /// <summary>The platform, as Node names it (<c>win32</c>, <c>darwin</c>, <c>linux</c>, ...).</summary>
    string Platform { get; }

    /// <summary>
    /// <c>MachineGuid</c> under <c>HKLM\SOFTWARE\Microsoft\Cryptography</c>, from the 64-bit registry
    /// view; null when it is not there as a string (REG_SZ). Throws when it cannot be read.
    /// </summary>
    Task<string?> ReadMachineGuidAsync();

    /// <summary>
    /// A command's standard output. Throws <see cref="CommandExitException"/> when it ran and failed,
    /// <see cref="CommandKilledException"/> when it outlived <paramref name="timeout"/>, and what
    /// starting it threw when it could not be run.
    /// </summary>
    Task<string> RunAsync(string command, IReadOnlyList<string> args, TimeSpan timeout);

    /// <summary>A file's text; throws when it cannot be read.</summary>
    Task<string> ReadFileAsync(string path);

    /// <summary>When a file's content was last written (ms), or null when this source cannot say (its age is then not judged).</summary>
    Task<double?> MtimeMsAsync(string path);
}

/// <summary>
/// The operating system's own id for this machine, which the default fingerprinter hashes. It
/// survives renaming the computer, which the host name does not.
///  - Windows: <c>MachineGuid</c> under <c>HKLM\SOFTWARE\Microsoft\Cryptography</c>, from the 64-bit
///    registry view, which a 32-bit process under WOW64 otherwise never sees.
///  - macOS: <c>IOPlatformUUID</c> of the platform expert device (<c>/usr/sbin/ioreg</c>).
///  - Linux (and anything else): <c>/etc/machine-id</c>, else <c>/var/lib/dbus/machine-id</c>, unless
///    it lives on a filesystem in memory or was written at this boot.
/// A read gives an id, "unavailable" (none to read, for good: the host name stands in, consistently)
/// or "transient" (the read did not finish: no fingerprint this time).
/// </summary>
internal static class MachineId
{
    /// <summary>How long <c>status()</c> may spend reading the machine id.</summary>
    public const int TimeoutMs = 2_000;

    /// <summary>How long <c>activate()</c> may spend reading it: a person is waiting, and a seat is at stake.</summary>
    public const int ActivateTimeoutMs = 10_000;

    /// <summary>How far before the boot time an id may have been written and still count as written at this boot.</summary>
    public const int BootSlackMs = 5_000;

    public const string Ioreg = "/usr/sbin/ioreg";
    public static readonly IReadOnlyList<string> IoregArgs = ["-rd1", "-c", "IOPlatformExpertDevice"];
    public static readonly IReadOnlyList<string> LinuxMachineIdFiles = ["/etc/machine-id", "/var/lib/dbus/machine-id"];
    private const string MachineIdPath = "/etc/machine-id";

    /// <summary>JavaScript's <c>\s</c>, for patterns ported from the reference.</summary>
    private static readonly string Ws = $"[{Wire.JsWhiteSpaceChars}]";

    private static readonly Regex IoregUuid = new($"\"IOPlatformUUID\"{Ws}*={Ws}*\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Btime = new("^btime ([0-9]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Octal = new(@"\\([0-7]{3})", RegexOptions.CultureInvariant);

    /// <summary>
    /// This machine's id, or why there is none. Never throws, and never takes much longer than
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task<MachineIdReading> ReadAsync(IMachineIdSource source, TimeSpan timeout, TimeProvider time)
    {
        try
        {
            if (source.Platform == "win32")
            {
                var guid = await Tasks.Bounded(Tasks.Start(source.ReadMachineGuidAsync), timeout + TimeSpan.FromMilliseconds(500), time).ConfigureAwait(false);
                return IdIn(FirstToken(guid));
            }
            if (source.Platform == "darwin")
            {
                var output = await Tasks.Bounded(Tasks.Start(() => source.RunAsync(Ioreg, IoregArgs, timeout)), timeout + TimeSpan.FromMilliseconds(500), time)
                    .ConfigureAwait(false);
                var match = IoregUuid.Match(output);
                return IdIn(match.Success ? match.Groups[1].Value : null);
            }
            // An id on a filesystem in memory is new every boot: no id to hold a device to.
            if (await TransientMachineIdAsync(source, timeout, time).ConfigureAwait(false)) return MachineIdReading.Unavailable;
            foreach (var path in LinuxMachineIdFiles)
            {
                var reading = await FromFileAsync(source, path, timeout, time).ConfigureAwait(false);
                if (reading.Kind == MachineIdKind.Unavailable) continue;
                // Written at this boot: a volatile root, whatever its filesystem.
                if (reading.Kind == MachineIdKind.Id && await WrittenThisBootAsync(source, path, timeout, time).ConfigureAwait(false))
                {
                    return MachineIdReading.Unavailable;
                }
                return reading;
            }
            return MachineIdReading.Unavailable;
        }
        catch (Exception error)
        {
            return new MachineIdReading(FailureKind(error));
        }
    }

    /// <summary>
    /// The type of the filesystem /etc/machine-id is on, from <c>/proc/self/mountinfo</c>: the mount
    /// whose mount point is the longest prefix of the path on a path boundary; of several at that
    /// point, the one on top. Null when no line can be read so.
    /// </summary>
    public static string? MachineIdFilesystem(string mountinfo)
    {
        var mounts = new List<Mount>();
        foreach (var line in mountinfo.Split('\n'))
        {
            var fields = line.Split(' ');
            var separator = IndexOfFrom(fields, "-", 6);
            var fstype = separator > 0 && separator + 1 < fields.Length ? fields[separator + 1] : null;
            if (fields.Length < 5 || fstype is null) continue;
            var (id, parent, raw) = (fields[0], fields[1], fields[4]);
            var point = Octal.Replace(raw, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
            if (point == MachineIdPath || point == "/" || MachineIdPath.StartsWith($"{point}/", StringComparison.Ordinal))
            {
                mounts.Add(new Mount(id, parent, point, fstype));
            }
        }
        if (mounts.Count == 0) return null;
        var deepest = mounts.Max(m => m.Point.Length);
        return TopMount(mounts.Where(m => m.Point.Length == deepest).ToList())?.Fstype;
    }

    private sealed record Mount(string Id, string Parent, string Point, string Fstype);

    /// <summary>Of mounts at one point, the one no other there names as its parent; when that does not settle it, the last listed.</summary>
    private static Mount? TopMount(List<Mount> atPoint)
    {
        var tops = atPoint.Where(m => !atPoint.Any(other => !ReferenceEquals(other, m) && other.Parent == m.Id)).ToList();
        return tops.Count == 1 ? tops[0] : atPoint.LastOrDefault();
    }

    private static int IndexOfFrom(string[] fields, string value, int from)
    {
        for (var i = from; i < fields.Length; i++)
        {
            if (fields[i] == value) return i;
        }
        return -1;
    }

    /// <summary>Whether /etc/machine-id lives on a filesystem in memory. False when mountinfo cannot be read.</summary>
    private static async Task<bool> TransientMachineIdAsync(IMachineIdSource source, TimeSpan timeout, TimeProvider time)
    {
        try
        {
            var info = await Tasks.Bounded(Tasks.Start(() => source.ReadFileAsync("/proc/self/mountinfo")), timeout, time).ConfigureAwait(false);
            return MachineIdFilesystem(info) is "tmpfs" or "ramfs";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the machine-id file was written at this boot: its mtime no older than <c>btime</c> in
    /// <c>/proc/stat</c>, less <see cref="BootSlackMs"/>. mtime only, never ctime. False when either
    /// cannot be read. Never throws.
    /// </summary>
    private static async Task<bool> WrittenThisBootAsync(IMachineIdSource source, string path, TimeSpan timeout, TimeProvider time)
    {
        try
        {
            var written = Tasks.Bounded(Tasks.Start(() => source.MtimeMsAsync(path)), timeout, time);
            var procStat = Tasks.Bounded(Tasks.Start(() => source.ReadFileAsync("/proc/stat")), timeout, time);
            // As Promise.all: the first that fails decides, without waiting on the other.
            var pending = new List<Task> { written, procStat };
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                if (!done.IsCompletedSuccessfully)
                {
                    Tasks.Abandon(done);
                    foreach (var other in pending) Tasks.Abandon(other);
                    return false;
                }
            }
            if (await written.ConfigureAwait(false) is not { } mtimeMs) return false;
            var btime = Btime.Match(await procStat.ConfigureAwait(false));
            if (!btime.Success) return false;
            return mtimeMs >= double.Parse(btime.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 1000 - BootSlackMs;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<MachineIdReading> FromFileAsync(IMachineIdSource source, string path, TimeSpan timeout, TimeProvider time)
    {
        try
        {
            var id = Wire.JsTrim(await Tasks.Bounded(Tasks.Start(() => source.ReadFileAsync(path)), timeout, time).ConfigureAwait(false));
            return id.Length > 0 ? MachineIdReading.Of(id) : MachineIdReading.Unavailable;
        }
        catch (Exception error)
        {
            return new MachineIdReading(FailureKind(error));
        }
    }

    /// <summary>
    /// The registry value as <c>reg query</c>'s output gives it to the reference: the first run of
    /// characters that are not white space after <c>REG_SZ</c>.
    /// </summary>
    private static string? FirstToken(string? value)
    {
        if (value is null) return null;
        var trimmed = Wire.JsTrim(value);
        var end = 0;
        while (end < trimmed.Length && !Wire.IsJsWhiteSpace(trimmed[end])) end++;
        return trimmed[..end];
    }

    private static MachineIdReading IdIn(string? match)
    {
        var id = match is null ? null : Wire.JsTrim(match);
        return string.IsNullOrEmpty(id) ? MachineIdReading.Unavailable : MachineIdReading.Of(id);
    }

    /// <summary>
    /// Whether a failed read is for good (<see cref="MachineIdKind.Unavailable"/>) or this time only. A
    /// command that ran and said no, or one or a file that is not there or not ours to read, is for
    /// good. A process killed at its time box, our own time box, and anything unexpected are this time
    /// only: guessing "for good" there is what flipped a device's fingerprint.
    /// </summary>
    public static MachineIdKind FailureKind(Exception error) => error switch
    {
        CommandKilledException or TimeoutException or OperationCanceledException => MachineIdKind.Transient,
        CommandExitException => MachineIdKind.Unavailable,
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or SecurityException => MachineIdKind.Unavailable,
        Win32Exception win32 when UnavailableErrno(win32.NativeErrorCode) => MachineIdKind.Unavailable,
        _ => MachineIdKind.Transient,
    };

    /// <summary>Errors starting a process that mean there is nothing to run, now or on the next try.</summary>
    private static bool UnavailableErrno(int code) => OperatingSystem.IsWindows()
        // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, ERROR_ACCESS_DENIED
        ? code is 2 or 3 or 5
        // EPERM, ENOENT, EACCES, ENOTDIR, EISDIR
        : code is 1 or 2 or 13 or 20 or 21;
}

/// <summary>The real system: the registry API, <c>ioreg</c> killed at its time box, and the file system.</summary>
internal sealed class SystemMachineIdSource : IMachineIdSource
{
    /// <summary>This machine's source: its registry read is shared by the whole process.</summary>
    public static readonly SystemMachineIdSource Instance = new(SystemMachineGuidRegistry.Instance);

    private readonly IMachineGuidRegistry registry;

    /// <summary>
    /// The registry read in flight, shared: a read that never comes back (a hung hive) blocks the pool
    /// thread it runs on, so every later call joins it rather than block another.
    /// </summary>
    private readonly SharedRead<string?> machineGuidRead = new();

    /// <summary>A source reading <c>MachineGuid</c> from <paramref name="registry"/> (tests give a stand-in).</summary>
    internal SystemMachineIdSource(IMachineGuidRegistry registry) => this.registry = registry;

    /// <summary>Where Windows keeps the machine id.</summary>
    internal const string CryptographyKey = @"SOFTWARE\Microsoft\Cryptography";

    /// <summary>
    /// The view it is read from: the 64-bit one whatever this process is, as <c>reg query /reg:64</c>
    /// reads it. A 32-bit process under WOW64 never sees the 64-bit hive otherwise.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static RegistryView MachineGuidView => RegistryView.Registry64;

    public string Platform => NodeNames.Platform;

    public Task<string?> ReadMachineGuidAsync() => machineGuidRead.Run(() => Task.Run(() => MachineGuidOf(registry.Read())));

    /// <summary>
    /// The machine id <paramref name="value"/> holds: its text when it is a <c>REG_SZ</c>, all
    /// <c>reg query</c> matches; null for any other kind (a <c>REG_EXPAND_SZ</c> included) or none.
    /// </summary>
    internal static string? MachineGuidOf(RegistryValue? value) => value is { Type: RegistryType.String, Data: string text } ? text : null;

    /// <summary><c>MachineGuid</c> from <paramref name="view"/>, when it is there as a <c>REG_SZ</c>.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ReadMachineGuid(RegistryView view) => MachineGuidOf(SystemMachineGuidRegistry.Read(view));

    public async Task<string> RunAsync(string command, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new CommandKilledException();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Gone already, or not ours to kill: either way it did not finish in time.
            }
            Tasks.Abandon(output);
            Tasks.Abandon(errors);
            throw new CommandKilledException();
        }
        var text = await output.ConfigureAwait(false);
        Tasks.Abandon(errors);
        if (process.ExitCode != 0) throw ExitFailure(process.ExitCode, OperatingSystem.IsWindows());
        return text;
    }

    /// <summary>
    /// What a command that did not exit 0 failed with. On macOS and Linux .NET reports a process ended
    /// by a SIGNAL as exit code 128 + the signal, and has no way to tell it from one that exited with
    /// that code: above 128 is read as a signal (killed, crashed: did not finish, this time only),
    /// where <c>execFile</c> would tell the two apart. Any other non-zero exit is the command saying no,
    /// for good.
    /// </summary>
    internal static Exception ExitFailure(int exitCode, bool windows) =>
        !windows && exitCode > 128 ? new CommandKilledException() : new CommandExitException(exitCode);

    public Task<string> ReadFileAsync(string path) => File.ReadAllTextAsync(path);

    public Task<double?> MtimeMsAsync(string path) => Task.FromResult<double?>(FileTimes.LastWriteMs(path));
}

/// <summary>A registry value's type, by its Win32 number (<c>REG_SZ</c> is 1, <c>REG_EXPAND_SZ</c> 2, ...).</summary>
internal enum RegistryType
{
    None = -1,
    Unknown = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    MultiString = 7,
    QWord = 11,
}

/// <summary>A registry value as it was read: its data, and its type.</summary>
internal readonly record struct RegistryValue(object? Data, RegistryType Type);

/// <summary>Where <c>MachineGuid</c> is read from: the registry, or a stand-in in tests.</summary>
internal interface IMachineGuidRegistry
{
    /// <summary>
    /// <c>MachineGuid</c> under <c>HKLM\SOFTWARE\Microsoft\Cryptography</c>, from the 64-bit view, as it
    /// is there (environment names not expanded); null when it is not there. Throws when it cannot be read.
    /// </summary>
    RegistryValue? Read();
}

/// <summary>The registry itself.</summary>
internal sealed class SystemMachineGuidRegistry : IMachineGuidRegistry
{
    public static readonly SystemMachineGuidRegistry Instance = new();

    public RegistryValue? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the registry is Windows'");
        return Read(SystemMachineIdSource.MachineGuidView);
    }

    /// <summary><c>MachineGuid</c> from <paramref name="view"/>, as it is there.</summary>
    [SupportedOSPlatform("windows")]
    internal static RegistryValue? Read(RegistryView view)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hklm.OpenSubKey(SystemMachineIdSource.CryptographyKey);
        if (key?.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data) return null;
        return new RegistryValue(data, (RegistryType)(int)key.GetValueKind("MachineGuid"));
    }
}

/// <summary>A file's times as <c>fs.stat</c> gives them.</summary>
internal static class FileTimes
{
    /// <summary>
    /// <c>fs.stat(path).mtimeMs</c>: when the content of the file the path names was last written,
    /// following symbolic links to the end (<c>FileInfo</c> alone gives a link's own time). Throws
    /// <see cref="FileNotFoundException"/> when there is nothing there.
    /// </summary>
    public static double LastWriteMs(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not null) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        if (!info.Exists)
        {
            var folder = new DirectoryInfo(info.FullName);
            if (!folder.Exists) throw new FileNotFoundException("no such file", path);
            info = folder;
        }
        return (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds;
    }
}

/// <summary>One read at a time: a call while one is in flight joins it; a call after it settled starts afresh.</summary>
internal sealed class SharedRead<T>
{
    private readonly object gate = new();
    private Task<T>? inFlight;

    public Task<T> Run(Func<Task<T>> start)
    {
        lock (gate)
        {
            if (inFlight is { IsCompleted: false } running) return running;
            inFlight = Tasks.Start(start);
            return inFlight;
        }
    }
}
