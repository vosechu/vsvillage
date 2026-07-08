using System;

namespace VsVillage;

/// <summary>Pure decision helpers for villager item movement. No world/entity state → unit-tested.</summary>
public static class VillagerInventoryMath
{
    public const long DefaultOrphanThresholdMs = 30_000L;

    /// <summary>
    /// How many items to move given the four binding limits: how many are needed, how many
    /// are available at the source, the per-move stack cap (v1 single-stack carry), and the
    /// free room at the destination. The smallest limit binds; negatives clamp to 0.
    /// One symmetric function for both withdraw and deposit — only the meaning of the args differs.
    /// </summary>
    public static int MovableQuantity(int need, int available, int stackMax, int capacity)
        => Math.Max(0, Math.Min(Math.Min(need, available), Math.Min(stackMax, capacity)));

    /// <summary>
    /// True when a carried stack has sat unchanged for at least <paramref name="thresholdMs"/>
    /// (so the return-carry task should deposit it). Backward clocks read as not-orphaned.
    /// </summary>
    public static bool IsCarryOrphaned(long lastChangeMs, long nowMs, long thresholdMs)
        => nowMs - lastChangeMs >= thresholdMs;
}
