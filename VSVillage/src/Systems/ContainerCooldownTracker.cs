using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Per-villager "don't immediately re-pick this container" memory. A position marked at time t is on
/// cooldown until t + cooldownMs (strict); a query purges anything past its window. Pure logic
/// (BlockPos value keys + longs), unit-tested off-engine. Mirrors the recentlyFilledTroughs pattern
/// in AiTaskVillagerFillTrough.
/// </summary>
public class ContainerCooldownTracker
{
    private readonly long cooldownMs;
    private readonly Dictionary<BlockPos, long> markedAtMs = new();

    public ContainerCooldownTracker(long cooldownMs) { this.cooldownMs = cooldownMs; }

    public void Mark(BlockPos pos, long nowMs) => markedAtMs[pos.Copy()] = nowMs; // defensive copy of the mutable key

    public bool IsOnCooldown(BlockPos pos, long nowMs)
    {
        Purge(nowMs);
        return markedAtMs.TryGetValue(pos, out long at) && nowMs - at < cooldownMs;
    }

    private void Purge(long nowMs)
    {
        List<BlockPos> expired = null;
        foreach (KeyValuePair<BlockPos, long> kv in markedAtMs)
            if (nowMs - kv.Value >= cooldownMs) (expired ??= new List<BlockPos>()).Add(kv.Key);
        if (expired != null) foreach (BlockPos p in expired) markedAtMs.Remove(p);
    }
}
