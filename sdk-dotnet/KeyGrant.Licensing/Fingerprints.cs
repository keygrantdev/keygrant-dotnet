using System.Net;
using System.Runtime.InteropServices;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

/// <summary>
/// The fingerprints the SDK computes, the same as the Electron SDK's for the same machine.
/// </summary>
public static class DeviceFingerprints
{
    /// <summary>
    /// A machine's fingerprint from what identifies it, hashed so the raw identifier never leaves it:
    /// lower-case hex SHA-256 of <c>"keygrant|" + raw</c>.
    /// </summary>
    /// <param name="raw">What identifies the machine.</param>
    /// <returns>The fingerprint.</returns>
    public static string Hash(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Hashes.Sha256Hex($"keygrant|{raw}");
    }

    /// <summary>
    /// The host-name fingerprint: the host name, platform and architecture as Node.js names them
    /// (<c>DESKTOP-1|win32|x64</c>), hashed. The fingerprint when the machine has no id to read, and
    /// offered as an alternate beside the machine id's, so a device activated under it is still itself.
    /// </summary>
    /// <returns>The fingerprint.</returns>
    public static string Hostname() => HostNamesFingerprint(ReadHostNames());

    /// <summary>What is hashed when not even the host name can be read.</summary>
    public const string UnknownHost = "unknown-host";

    /// <summary>The host-name fingerprint of a given host, platform and architecture (Node's names): every SDK composes it the same way.</summary>
    internal static string Host(string host, string platform, string arch) => Hash($"{host}|{platform}|{arch}");

    /// <summary>The fingerprint of a machine id as the machine-id reader gives it.</summary>
    internal static string Machine(string id) => Hash($"machine-id|{id}");

    /// <summary>This machine's host names, or null when they cannot be read.</summary>
    internal static HostNames? ReadHostNames()
    {
        try
        {
            return new HostNames(NodeNames.HostName(), NodeNames.Platform, NodeNames.Arch);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The host-name fingerprint of these names; when they could not be read (null), <see cref="UnknownHost"/>'s.</summary>
    internal static string HostNamesFingerprint(HostNames? names) =>
        names is null ? Hash(UnknownHost) : Host(names.Host, names.Platform, names.Arch);

    /// <summary>
    /// Who this device is, as the default fingerprinter answers (<see cref="MachineFingerprinter.IdentifyAsync"/>),
    /// from a reading of its machine id and its host names (null when they cannot be read): an id, the
    /// machine id's fingerprint, kind machine, with the host name's as an alternate (none when the two
    /// are the same); no id to read, the host name's, kind hostname, no alternate; a read that did not
    /// finish, no fingerprint this time, and the host name's as an alternate.
    /// </summary>
    internal static DeviceIdentity IdentityOf(MachineIdReading reading, HostNames? host)
    {
        var legacy = HostNamesFingerprint(host);
        return WithHostAlternate(SettledOn(reading, () => legacy), legacy);
    }

    /// <summary>
    /// What a reading of the machine id settles on: its fingerprint; the host name's (<paramref name="hostFingerprint"/>,
    /// read only then) when there is no id to read; null when the read did not finish, which settles nothing.
    /// </summary>
    internal static MachineFingerprinter.Settled? SettledOn(MachineIdReading reading, Func<string> hostFingerprint) => reading.Kind switch
    {
        MachineIdKind.Id => new MachineFingerprinter.Settled(Machine(reading.Id!), DeviceKind.Machine),
        MachineIdKind.Unavailable => new MachineFingerprinter.Settled(hostFingerprint(), DeviceKind.Hostname),
        _ => null,
    };

    /// <summary>Who a settled fingerprint is (null: none this time), with the host name's (<paramref name="legacy"/>) as an alternate when it is another.</summary>
    internal static DeviceIdentity WithHostAlternate(MachineFingerprinter.Settled? known, string legacy)
    {
        if (known is null) return new DeviceIdentity(null, null, [legacy]);
        IReadOnlyList<string> alternates = known.Fingerprint == legacy ? [] : [legacy];
        return new DeviceIdentity(known.Fingerprint, known.Kind, alternates);
    }
}

/// <summary>What a host-name fingerprint hashes, as Node names them.</summary>
internal sealed record HostNames(string Host, string Platform, string Arch);

/// <summary>
/// The default fingerprinter: the operating system's machine id, which survives renaming the
/// computer, hashed (<c>Hash("machine-id|" + id)</c>); the host name's
/// (<see cref="DeviceFingerprints.Hostname"/>) when the machine has no id to read. A read that did
/// not finish gives NO fingerprint and is not kept, so the next call reads again: a device's
/// fingerprint never changes with how fast a read was. A settled fingerprint is kept for the process.
/// </summary>
public sealed class MachineFingerprinter : IFingerprinter
{
    /// <summary>What every fingerprinter reading THIS machine's id settled: one read for the whole process.</summary>
    internal static readonly Keeper ProcessKeeper = new();

    private readonly IMachineIdSource? source;
    private readonly TimeProvider time;
    private readonly Keeper keeper;

    /// <summary>The default fingerprinter, reading this machine's id.</summary>
    public MachineFingerprinter()
        : this(null, null)
    {
    }

    /// <summary>
    /// A fingerprinter over <paramref name="source"/> (null: this machine's own id, kept for the whole
    /// process), keeping what it settles in <paramref name="keeper"/> (null: its own, for a stand-in source).
    /// </summary>
    internal MachineFingerprinter(IMachineIdSource? source, TimeProvider? time, Keeper? keeper = null)
    {
        this.source = source;
        this.time = time ?? TimeProvider.System;
        this.keeper = keeper ?? (source is null ? ProcessKeeper : new Keeper());
    }

    /// <summary>Where this fingerprinter keeps what it settled (tests).</summary>
    internal Keeper KeptIn => keeper;

    /// <summary>A settled fingerprint and what it hashes.</summary>
    internal sealed record Settled(string Fingerprint, DeviceKind Kind);

    /// <summary>A settled fingerprint, shared by the fingerprinters given the same keeper.</summary>
    internal sealed class Keeper
    {
        private readonly object gate = new();
        private Settled? settled;

        public Settled? Settled
        {
            get
            {
                lock (gate) return settled;
            }
        }

        public void Keep(Settled found)
        {
            lock (gate) settled = found;
        }

        /// <summary>Forget what was settled (tests).</summary>
        public void Forget()
        {
            lock (gate) settled = null;
        }
    }

    /// <inheritdoc />
    public async Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // As DeviceFingerprints.IdentityOf, with the settled fingerprint kept: the host name's alternate
        // is this call's, the fingerprint the one first settled on.
        var legacy = DeviceFingerprints.Hostname();
        var known = keeper.Settled ?? await ReadFingerprintAsync(timeout).ConfigureAwait(false);
        if (known is not null) keeper.Keep(known);
        return DeviceFingerprints.WithHostAlternate(known, legacy);
    }

    /// <summary>
    /// The fingerprint, waiting as long as an activation does. THROWS when the machine id could not be
    /// read in time, rather than answer a stand-in; the SDK itself uses <see cref="IdentifyAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="InvalidOperationException">The machine id could not be read in time.</exception>
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var identity = await IdentifyAsync(TimeSpan.FromMilliseconds(MachineId.ActivateTimeoutMs), cancellationToken).ConfigureAwait(false);
        return identity?.Fingerprint ?? throw new InvalidOperationException("this machine's id could not be read in time");
    }

    /// <summary>This machine's fingerprint and what it hashes, or null when its id could not be read this time.</summary>
    private async Task<Settled?> ReadFingerprintAsync(TimeSpan timeout) =>
        DeviceFingerprints.SettledOn(
            await MachineId.ReadAsync(source ?? SystemMachineIdSource.Instance, timeout, time).ConfigureAwait(false),
            DeviceFingerprints.Hostname);
}

/// <summary>
/// The platform, architecture and host name as Node.js reports them (<c>os.platform()</c>,
/// <c>os.arch()</c>, <c>os.hostname()</c>), so a machine fingerprints the same under either SDK.
/// </summary>
internal static class NodeNames
{
    /// <summary>The operating systems .NET tells apart, as Node names them (<see cref="PlatformName"/>).</summary>
    internal enum Os
    {
        Windows,
        MacOS,
        MacCatalyst,
        Android,
        Linux,
        FreeBsd,
        Illumos,
        Solaris,
        Other,
    }

    /// <summary><c>process.platform</c> on this machine: <c>win32</c>, <c>darwin</c>, <c>linux</c>, <c>freebsd</c>, ...</summary>
    public static string Platform { get; } = PlatformName(CurrentOs(), RuntimeInformation.OSDescription);

    /// <summary><c>process.arch</c> of this PROCESS: <c>x64</c>, <c>arm64</c>, <c>ia32</c>, <c>arm</c>, ...</summary>
    public static string Arch { get; } = ArchName(RuntimeInformation.ProcessArchitecture);

    /// <summary>Node's <c>process.platform</c> for an operating system (<paramref name="description"/> names one .NET does not know).</summary>
    internal static string PlatformName(Os os, string description) => os switch
    {
        Os.Windows => "win32",
        Os.MacOS or Os.MacCatalyst => "darwin",
        Os.Android => "android",
        Os.Linux => "linux",
        Os.FreeBsd => "freebsd",
        Os.Illumos or Os.Solaris => "sunos",
        _ => description.Split(' ')[0].ToLowerInvariant(),
    };

    /// <summary>Node's <c>process.arch</c> for an architecture.</summary>
    internal static string ArchName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "ia32",
        Architecture.Arm64 => "arm64",
        Architecture.Arm or Architecture.Armv6 => "arm",
        Architecture.S390x => "s390x",
        Architecture.LoongArch64 => "loong64",
        Architecture.Ppc64le => "ppc64",
        // Architecture.RiscV64, from the .NET 9 runtime on.
        (Architecture)9 => "riscv64",
        var other => other.ToString().ToLowerInvariant(),
    };

    private static Os CurrentOs()
    {
        if (OperatingSystem.IsWindows()) return Os.Windows;
        if (OperatingSystem.IsMacCatalyst()) return Os.MacCatalyst;
        if (OperatingSystem.IsMacOS()) return Os.MacOS;
        if (OperatingSystem.IsAndroid()) return Os.Android;
        if (OperatingSystem.IsLinux()) return Os.Linux;
        if (OperatingSystem.IsFreeBSD()) return Os.FreeBsd;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ILLUMOS"))) return Os.Illumos;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("SOLARIS"))) return Os.Solaris;
        return Os.Other;
    }

    /// <summary>
    /// <c>os.hostname()</c>: <c>gethostname()</c>; on Windows the Unicode <c>GetHostNameW</c> libuv
    /// calls, the DNS host name (never the 15-character NetBIOS name <c>Environment.MachineName</c> gives).
    /// </summary>
    public static string HostName()
    {
        // Also starts Winsock for this process, which GetHostNameW needs.
        var name = Dns.GetHostName();
        if (!OperatingSystem.IsWindows()) return name;
        try
        {
            var buffer = new char[256];
            return GetHostNameW(buffer, buffer.Length) == 0 ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length) : name;
        }
        catch (Exception)
        {
            return name;
        }
    }

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetHostNameW([Out] char[] name, int nameLength);
}
