using Vintagestory.API.MathTools;
using Xunit;

namespace VsVillage.Tests;

public class ContainerCooldownTracker_When_a_position_is_marked
{
    [Fact]
    public void It_is_on_cooldown_before_the_window_elapses()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var tracker = new ContainerCooldownTracker(1000);
        tracker.Mark(new BlockPos(1, 2, 3), 0);
        Assert.True(tracker.IsOnCooldown(new BlockPos(1, 2, 3), 999));
    }

    [Fact]
    public void It_is_off_cooldown_exactly_at_the_window_edge()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var tracker = new ContainerCooldownTracker(1000);
        tracker.Mark(new BlockPos(1, 2, 3), 0);
        Assert.False(tracker.IsOnCooldown(new BlockPos(1, 2, 3), 1000)); // strict: now - marked >= cooldown => off
    }

    [Fact]
    public void It_reports_off_cooldown_for_a_never_marked_position()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var tracker = new ContainerCooldownTracker(1000);
        Assert.False(tracker.IsOnCooldown(new BlockPos(9, 9, 9), 0));
    }

    [Fact]
    public void It_treats_equal_coordinates_as_the_same_key()
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        var tracker = new ContainerCooldownTracker(1000);
        tracker.Mark(new BlockPos(4, 5, 6), 0);
        Assert.True(tracker.IsOnCooldown(new BlockPos(4, 5, 6), 500)); // BlockPos has value equality
    }
}
