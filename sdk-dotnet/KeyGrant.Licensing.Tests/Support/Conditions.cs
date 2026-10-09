using System.Diagnostics;
using System.Runtime.InteropServices;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// What this machine can do for the tests that need it, found once. A test whose condition does not
/// hold is reported SKIPPED, with the reason, never passed with nothing run.
/// </summary>
internal static class Conditions
{
    private static readonly Lazy<bool> Symlinks = new(CanCreateSymlinks);
    private static readonly Lazy<bool> HardLinks = new(CanCreateHardLinks);
    private static readonly Lazy<string?> NodeAnswer = new(NodeHostFingerprint);

    public static bool SymlinksWork => Symlinks.Value;

    public static bool HardLinksWork => HardLinks.Value;

    /// <summary>The host-name fingerprint as <c>node</c> computes it on this machine, or null without a node of this process's architecture.</summary>
    public static string? NodeHostnameFingerprint => NodeAnswer.Value;

    private static bool CanCreateSymlinks()
    {
        var dir = Directory.CreateTempSubdirectory("keygrant-link-");
        try
        {
            var target = Path.Combine(dir.FullName, "target");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(dir.FullName, "link"), target);
            return true;
        }
        catch (Exception)
        {
            // Windows without the privilege (Developer Mode off, not elevated), or a file system without links.
            return false;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static bool CanCreateHardLinks()
    {
        var dir = Directory.CreateTempSubdirectory("keygrant-hardlink-");
        try
        {
            var target = Path.Combine(dir.FullName, "target");
            File.WriteAllText(target, "x");
            return HardLink.TryCreate(target, Path.Combine(dir.FullName, "link"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string? NodeHostFingerprint()
    {
        try
        {
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("const os=require('os');process.stdout.write(os.arch()+' '+require('crypto').createHash('sha256').update('keygrant|'+os.hostname()+'|'+os.platform()+'|'+os.arch()).digest('hex'))");
            using var process = Process.Start(info)!;
            var words = process.StandardOutput.ReadToEnd().Split(' ');
            process.WaitForExit();
            // Another architecture's node (an x64 one beside an arm64 .NET, say) hashes another arch.
            return process.ExitCode == 0 && words.Length == 2 && words[0] == NodeNames.Arch ? words[1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A test for Windows only.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A test for Linux only.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
    }
}

/// <summary>A test for macOS only.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only";
    }
}

/// <summary>A test for macOS and Linux (POSIX processes and files).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "macOS and Linux only";
    }
}

/// <summary>A test that needs to create symbolic links (on Windows: Developer Mode or elevation).</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!Conditions.SymlinksWork) Skip = "this machine cannot create symbolic links here";
    }
}

/// <summary>A test that needs to create hard links.</summary>
public sealed class HardLinkFactAttribute : FactAttribute
{
    public HardLinkFactAttribute()
    {
        if (!Conditions.HardLinksWork) Skip = "this machine cannot create hard links here";
    }
}

/// <summary>A test that compares with <c>node</c> itself (installed, and of this process's architecture).</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (Conditions.NodeHostnameFingerprint is null) Skip = "no node of this architecture on this machine";
    }
}

/// <summary>Hard links, which .NET has no API for: <c>CreateHardLinkW</c> on Windows, <c>link</c> elsewhere.</summary>
internal static class HardLink
{
    public static bool TryCreate(string existing, string link)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : Link(existing, link) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}
