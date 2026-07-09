using Vintagestory.API.MathTools;
using Xunit;

namespace VsVillage.Tests;

public class ContainerClaimRegistry_IsClaimedByOther
{
    [Fact]
    public void It_is_false_when_unclaimed()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        Assert.False(new ContainerClaimRegistry().IsClaimedByOther(new BlockPos(1, 1, 1), 7, 0));
    }

    [Fact]
    public void It_is_true_when_a_different_owner_holds_a_live_claim()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var reg = new ContainerClaimRegistry();
        reg.TryClaim(new BlockPos(1, 1, 1), 7, 0);
        Assert.True(reg.IsClaimedByOther(new BlockPos(1, 1, 1), 8, 0));
    }

    [Fact]
    public void It_is_false_for_the_owners_own_claim()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var reg = new ContainerClaimRegistry();
        reg.TryClaim(new BlockPos(1, 1, 1), 7, 0);
        Assert.False(reg.IsClaimedByOther(new BlockPos(1, 1, 1), 7, 0));
    }

    [Fact]
    public void It_is_false_once_the_claim_has_expired()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var reg = new ContainerClaimRegistry();
        reg.TryClaim(new BlockPos(1, 1, 1), 7, 0);
        Assert.False(reg.IsClaimedByOther(new BlockPos(1, 1, 1), 8, ContainerClaimRegistry.ClaimExpiryMs + 1));
    }
}
