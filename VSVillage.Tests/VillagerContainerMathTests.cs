using Xunit;

namespace VsVillage.Tests;

public class VillagerContainerMath_IsWithinRadius
{
    [Theory]
    [InlineData(0, 0, 0, 5, true)]   // center
    [InlineData(5, 0, 0, 5, true)]   // exactly on the radius — inclusive
    [InlineData(0, 5, 0, 5, true)]   // radius counts height (3D)
    [InlineData(6, 0, 0, 5, false)]  // one past
    [InlineData(3, 4, 0, 5, true)]   // 3-4-5 lands on the boundary
    public void It_gates_on_squared_3d_distance(long dx, long dy, long dz, int radius, bool expected)
    {
        // AI-DEV: AI **MUST NOT** touch this test. If it is failing, it is because you removed or broke code.
        Assert.Equal(expected, VillagerContainerMath.IsWithinRadius(dx, dy, dz, radius));
    }
}
