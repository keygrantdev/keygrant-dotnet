using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The groups after the first ones (the major a build runs as, upgradeUrl's link, the launch report),
// answered from each vector's own JSON.
public partial class ConformanceTests
{
    /// <summary>
    /// The <c>majors</c> cases this SDK's config can hold: every one but a fraction (the <c>jsOnly</c>
    /// ones). A configured major is a signed <see cref="int"/> here, so a negative is held, and replayed.
    /// </summary>
    public static TheoryData<int, string> MajorCases => Cases("majors", v => v["configured"] is null || IsWhole(v["configured"]!));

    public static TheoryData<int, string> ReferencedLinkCases => Cases("referencedLinks");
    public static TheoryData<int, string> TrialReportDueCases => Cases("trialReportDue");

    private static bool IsWhole(JsonNode node) => Json.TryNumber(node, out var d) && Math.Truncate(d) == d;

    [Fact]
    public void Skips_only_the_majors_this_SDK_s_config_cannot_hold_the_fractions()
    {
        var skipped = Group("majors").Where(v => v!["configured"] is { } c && !IsWhole(c)).ToList();
        Assert.Equal(4, skipped.Count);
        // The skipped are exactly the jsOnly ones: a negative is replayed.
        Assert.Equal(skipped, Group("majors").Where(v => Bool(v!["jsOnly"]) == true));
        Assert.Contains(Group("majors"), v => Json.TryNumber(v!["configured"], out var d) && d < 0 && IsWhole(v["configured"]!));
        Assert.Equal(Group("majors").Count - skipped.Count, MajorCases.Count());
    }

    [Theory]
    [MemberData(nameof(MajorCases))]
    public void Majors_run_as_the_reference_reads_them(int index, string name)
    {
        var v = Item("majors", index);
        Assert.True(Int(v["expect"]) == Offline.MajorOf(Int(v["configured"])), name);
    }

    [Theory]
    [MemberData(nameof(ReferencedLinkCases))]
    public void Links_carry_the_reference_as_the_reference_builds_them(int index, string name)
    {
        var v = Item("referencedLinks", index);
        Assert.True(Str(v["expect"]) == Links.ReferencedLink(Str(v["link"])!, Str(v["reference"])!), name);
    }

    [Theory]
    [MemberData(nameof(TrialReportDueCases))]
    public void Launch_reports_fall_due_as_the_reference_s(int index, string name)
    {
        var v = Item("trialReportDue", index);
        Assert.True(Bool(v["expect"]) == Offline.TrialReportDue(State(v["state"]), Long(v["now"])!.Value), name);
    }
}
