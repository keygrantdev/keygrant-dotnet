using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

public class WireBodiesTests
{
    [Fact]
    public void Skips_only_outside_the_repository_that_holds_the_reference()
    {
        // Present: never skipped. Missing: skipped in the public export, never where the bodies are made.
        Assert.False(WireBodies.Skips(present: true, inRepository: true));
        Assert.False(WireBodies.Skips(present: true, inRepository: false));
        Assert.False(WireBodies.Skips(present: false, inRepository: true));
        Assert.True(WireBodies.Skips(present: false, inRepository: false));
    }

    [WireBodiesFact]
    public void Finds_the_captured_bodies_in_the_repository_that_holds_the_reference()
    {
        // Skipped, with the reason, outside that repository; a missing file is a failure inside it, never a skip.
        Assert.NotNull(WireBodies.PathOnDisk);
    }
}
