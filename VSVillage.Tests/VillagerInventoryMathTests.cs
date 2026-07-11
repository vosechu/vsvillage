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
    [InlineData(0, 4, 16, 64)]    // empty large trough: 4*16 free
    [InlineData(64, 4, 16, 0)]    // full: no room
    [InlineData(16, 4, 16, 48)]   // one level in: 48 free
    [InlineData(100, 4, 16, 0)]   // over capacity (shouldn't happen): clamp to 0, never negative
    [InlineData(0, 1, 16, 16)]    // small trough: 1 level
    public void TroughFreeCapacity_When_partiallyFilled_It_returns_room_clamped_nonnegative(
        int current, int maxLevels, int perLevel, int expected)
    {
        Assert.Equal(expected, VillagerInventoryMath.TroughFreeCapacity(current, maxLevels, perLevel));
    }
}
