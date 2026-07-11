using Xunit;

namespace VsVillage.Tests;

public class VillagerInventoryMath_When_deciding_move_quantity
{
    [Theory]
    [InlineData(5, 10, 64, 64, 5)]    // need binds
    [InlineData(10, 5, 64, 64, 5)]    // available binds
    [InlineData(5, 5, 64, 64, 5)]     // exact match
    [InlineData(0, 10, 64, 64, 0)]    // zero need
    [InlineData(10, 0, 64, 64, 0)]    // zero available
    [InlineData(100, 100, 64, 100, 64)] // single-stack limit binds
    [InlineData(100, 100, 64, 10, 10)]  // capacity binds
    [InlineData(64, 64, 64, 0, 0)]      // zero capacity
    [InlineData(64, 64, 64, 64, 64)]    // all equal, no off-by-one
    [InlineData(8, 7, 6, 5, 5)]         // all distinct, smallest wins
    [InlineData(-3, 10, 64, 64, 0)]     // negative need clamps to 0
    [InlineData(10, -3, 64, 64, 0)]     // negative available clamps to 0
    public void It_returns_the_smallest_binding_limit_floored_at_zero(
        int need, int available, int stackMax, int capacity, int expected)
    {
        Assert.Equal(expected, VillagerInventoryMath.MovableQuantity(need, available, stackMax, capacity));
    }
}

public class VillagerInventoryMath_When_checking_orphaned_carry
{
    [Fact] public void It_is_not_orphaned_below_threshold()
    { Assert.False(VillagerInventoryMath.IsCarryOrphaned(1000, 1000 + 29_000, 30_000)); }

    [Fact] public void It_is_orphaned_at_the_threshold()
    { Assert.True(VillagerInventoryMath.IsCarryOrphaned(1000, 1000 + 30_000, 30_000)); }

    [Fact] public void It_is_orphaned_past_the_threshold()
    { Assert.True(VillagerInventoryMath.IsCarryOrphaned(1000, 1000 + 31_000, 30_000)); }

    [Fact] public void It_is_not_orphaned_on_a_backward_clock()
    { Assert.False(VillagerInventoryMath.IsCarryOrphaned(1000, 500, 30_000)); }
}

public class VillagerInventoryMath_When_computing_trough_capacity
{
    [Theory]
    [InlineData(0, 8, 2, 16)]    // empty grain large trough: 8 levels * 2 = 16 free
    [InlineData(16, 8, 2, 0)]    // full: no room
    [InlineData(2, 8, 2, 14)]    // one fill level in: 14 free
    [InlineData(20, 8, 2, 0)]    // over capacity (shouldn't happen): clamp to 0, never negative
    [InlineData(0, 4, 4, 16)]    // a different config: 4 levels * 4
    public void TroughFreeCapacity_When_partiallyFilled_It_returns_room_clamped_nonnegative(
        int current, int maxLevels, int perLevel, int expected)
    {
        Assert.Equal(expected, VillagerInventoryMath.TroughFreeCapacity(current, maxLevels, perLevel));
    }
}
