using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>A build's major read as the server reads a version: a 0.x build (major 0) runs as major 1.</summary>
public class MajorTests
{
    private static readonly LicenseStatus Licensed = new() { State = LicenseState.Licensed, Key = "KEY" };

    /// <summary>A lease for KEY / act_1 covering majors up to <paramref name="major"/>.</summary>
    private static string LeaseUpTo(Harness h, long major) => h.SignLease(new LeaseInput { Sub = "KEY", Major = major });

    // The majors vectors replay null, 0, 1, 2, 7 and -3 (ConformanceTests.Purchase.cs); these are an int's ends.
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Reads_an_int_major_s_ends_with_no_overflow(int? configured, int expected)
    {
        Assert.Equal(expected, Offline.MajorOf(configured));
    }

    [Fact]
    public async Task A_0_x_build_is_licensed_offline_by_a_lease_for_major_1_not_refused_as_outdated()
    {
        var h = Create(0);
        h.Stored = new StoredState { Key = "KEY", ActivationId = "act_1", Lease = LeaseUpTo(h, 1), CheckedMajor = 1, LastSeen = Now };
        h.Http.Fail = true;
        Assert.Equal(Licensed, await h.License.StatusAsync());
    }

    [Fact]
    public async Task A_0_x_build_sends_major_1_with_its_activation_its_validate_and_its_claim()
    {
        var h = Create(0);
        var lease = LeaseUpTo(h, 1);
        h.Http.Lease("activate", lease);
        h.Http.Lease("validate", lease);
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync("KEY"));
        Assert.Equal(Licensed, await h.License.RefreshAsync());
        Assert.Equal(1, h.Http.BodyOf("activate")["major"]!.GetValue<int>());
        Assert.Equal(1, h.Http.BodyOf("validate")["major"]!.GetValue<int>());
        Assert.Equal(1, h.Stored!.CheckedMajor);

        var buyer = Create(PurchaseUrlTests.Held(), new Options { Major = 0 });
        buyer.Http.Answer("claim", 200, new JsonObject { ["pending"] = true });
        await buyer.License.StatusAsync();
        Assert.Equal(1, buyer.Http.BodyOf("claim")["major"]!.GetValue<int>());
    }
}
