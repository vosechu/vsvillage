namespace VsVillage;

/// <summary>Pure geometry for container-finding. Unit-tested off-engine.</summary>
public static class VillagerContainerMath
{
    /// <summary>True when a point at offset (dx,dy,dz) from a village center is within its radius,
    /// inclusive, by 3D squared distance. Gates which scanned containers belong to a village.</summary>
    public static bool IsWithinRadius(long dx, long dy, long dz, int radius)
        => dx * dx + dy * dy + dz * dz <= (long)radius * radius;
}
