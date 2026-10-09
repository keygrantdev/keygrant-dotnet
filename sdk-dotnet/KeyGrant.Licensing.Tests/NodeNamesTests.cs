using System.Runtime.InteropServices;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The platform and architecture as Node.js names them: they go into the host-name fingerprint, so a
/// name that differs from Node's (<c>x86</c> for <c>ia32</c>, <c>macos</c> for <c>darwin</c>) gives
/// the same machine another fingerprint under each SDK.
/// </summary>
public class NodeNamesTests
{
    public static TheoryData<string, string> Platforms => new()
    {
        { "Windows", "win32" },
        { "MacOS", "darwin" },
        { "MacCatalyst", "darwin" },
        { "Android", "android" },
        { "Linux", "linux" },
        { "FreeBsd", "freebsd" },
        { "Illumos", "sunos" },
        { "Solaris", "sunos" },
    };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Names_each_operating_system_as_process_platform_does(string os, string node) =>
        Assert.Equal(node, NodeNames.PlatformName(Enum.Parse<NodeNames.Os>(os), "ignored"));

    [Theory]
    [InlineData("OpenBSD 7.4 GENERIC.MP#1397 amd64", "openbsd")]
    [InlineData("NetBSD 10.0", "netbsd")]
    [InlineData("AIX 7.2", "aix")]
    public void Names_a_system_dotnet_does_not_know_by_the_first_word_of_its_description(string description, string node) =>
        Assert.Equal(node, NodeNames.PlatformName(NodeNames.Os.Other, description));

    [Fact]
    public void Names_every_operating_system_dotnet_tells_apart()
    {
        var named = Platforms.Select(row => (string)row[0]).ToHashSet();
        Assert.All(Enum.GetNames<NodeNames.Os>().Where(os => os != nameof(NodeNames.Os.Other)), os => Assert.Contains(os, named));
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.X86, "ia32")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.Arm, "arm")]
    [InlineData(Architecture.Armv6, "arm")]
    [InlineData(Architecture.S390x, "s390x")]
    [InlineData(Architecture.LoongArch64, "loong64")]
    [InlineData(Architecture.Ppc64le, "ppc64")]
    [InlineData(Architecture.Wasm, "wasm")]
    // RISC-V: Architecture.RiscV64 on the .NET 9 runtime and later, which a net8.0 library may run on.
    [InlineData((Architecture)9, "riscv64")]
    public void Names_each_architecture_as_process_arch_does(Architecture architecture, string node) =>
        Assert.Equal(node, NodeNames.ArchName(architecture));

    [Fact]
    public void Names_every_architecture_dotnet_knows_in_node_s_lower_case()
    {
        Assert.All(Enum.GetValues<Architecture>(), architecture => Assert.Matches("^[a-z0-9]+$", NodeNames.ArchName(architecture)));
    }

    [Fact]
    public void Names_this_process_s_own_platform_and_architecture()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : NodeNames.Platform;
        Assert.Equal((platform, NodeNames.ArchName(RuntimeInformation.ProcessArchitecture)), (NodeNames.Platform, NodeNames.Arch));
        Assert.Equal(DeviceFingerprints.Host(NodeNames.HostName(), NodeNames.Platform, NodeNames.Arch), DeviceFingerprints.Hostname());
        Assert.Equal(Hashes.Sha256Hex("keygrant|DESKTOP-1|win32|x64"), DeviceFingerprints.Host("DESKTOP-1", "win32", "x64"));
    }
}
