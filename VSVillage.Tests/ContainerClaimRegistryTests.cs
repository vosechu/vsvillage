using Vintagestory.API.MathTools;
using Xunit;

namespace VsVillage.Tests;

public class ContainerClaimRegistry_When_claiming
{
    static BlockPos P(int x, int y, int z) => new BlockPos(x, y, z);
    const long Exp = ContainerClaimRegistry.ClaimExpiryMs;

    [Fact] public void It_grants_an_unclaimed_pos()
    { var r = new ContainerClaimRegistry(); Assert.True(r.TryClaim(P(1,1,1), 10, 0)); }

    [Fact] public void It_refuses_a_pos_held_by_another_within_expiry()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.False(r.TryClaim(P(1,1,1), 20, 0)); }

    [Fact] public void It_grants_idempotent_reclaim_to_the_same_owner()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.True(r.TryClaim(P(1,1,1), 10, 100)); }

    [Fact] public void It_refreshes_the_clock_on_owner_reclaim()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      r.TryClaim(P(1,1,1), 10, Exp);                       // reclaim at t=Exp resets baseline
      Assert.False(r.TryClaim(P(1,1,1), 20, Exp + 1));     // still held: only 1ms since refresh
    }

    [Fact] public void It_grants_a_pos_whose_claim_has_expired()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.True(r.TryClaim(P(1,1,1), 20, Exp + 1)); }

    [Fact] public void It_does_not_refresh_expiry_on_a_failed_claim()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.False(r.TryClaim(P(1,1,1), 20, Exp - 1));     // fails, must NOT extend
      Assert.True(r.TryClaim(P(1,1,1), 20, Exp + 1)); }    // original t=0 governs -> expired

    [Fact] public void It_transfers_ownership_after_expiry_and_resets_clock()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      r.TryClaim(P(1,1,1), 20, Exp + 1);                    // B takes it, fresh
      Assert.False(r.TryClaim(P(1,1,1), 10, Exp + 1)); }    // A can't steal back immediately

    [Fact] public void It_keeps_distinct_positions_independent()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.True(r.TryClaim(P(2,2,2), 20, 0)); }

    [Fact] public void It_lets_one_owner_hold_multiple_positions()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(1,1,1), 10, 0);
      Assert.True(r.TryClaim(P(2,2,2), 10, 0)); }

    [Fact] public void It_treats_value_equal_blockpos_as_the_same_key()
    { var r = new ContainerClaimRegistry(); r.TryClaim(new BlockPos(1,1,1), 10, 0);
      Assert.False(r.TryClaim(new BlockPos(1,1,1), 20, 0)); } // different instance, equal coords
}

public class ContainerClaimRegistry_When_at_expiry_boundary
{
    static BlockPos P() => new BlockPos(1,1,1);
    const long Exp = ContainerClaimRegistry.ClaimExpiryMs;

    [Fact] public void It_still_holds_exactly_at_the_threshold()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      Assert.False(r.TryClaim(P(), 20, Exp)); }             // strict > : ==Exp not yet stale

    [Fact] public void It_frees_one_ms_past_the_threshold()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      Assert.True(r.TryClaim(P(), 20, Exp + 1)); }

    [Fact] public void It_still_holds_one_ms_before_the_threshold()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      Assert.False(r.TryClaim(P(), 20, Exp - 1)); }

    [Fact] public void It_does_not_spuriously_expire_on_a_backward_clock()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 1000);
      Assert.False(r.TryClaim(P(), 20, 500)); }             // now < claimedAt -> not expired
}

public class ContainerClaimRegistry_When_releasing
{
    static BlockPos P() => new BlockPos(1,1,1);

    [Fact] public void It_lets_the_owner_free_a_held_pos()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      r.Release(P(), 10); Assert.True(r.TryClaim(P(), 20, 0)); }

    [Fact] public void It_ignores_a_release_from_a_non_owner()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      r.Release(P(), 20);                                   // B tries to free A's claim
      Assert.False(r.TryClaim(P(), 30, 0)); }               // A's claim survives

    [Fact] public void It_is_a_noop_to_release_an_unheld_pos()
    { var r = new ContainerClaimRegistry(); r.Release(P(), 10);
      Assert.True(r.TryClaim(P(), 10, 0)); }

    [Fact] public void It_is_safe_to_double_release()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      r.Release(P(), 10); r.Release(P(), 10);
      Assert.True(r.TryClaim(P(), 20, 0)); }

    [Fact] public void It_allows_release_then_reclaim_by_same_owner()
    { var r = new ContainerClaimRegistry(); r.TryClaim(P(), 10, 0);
      r.Release(P(), 10); Assert.True(r.TryClaim(P(), 10, 0)); }
}
