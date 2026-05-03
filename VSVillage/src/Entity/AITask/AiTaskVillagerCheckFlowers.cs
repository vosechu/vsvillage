using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Herbalist morning task: walks around her workstation area "checking on" flower
/// blocks (matches "flower-*-free" or "flower-*-snow" - the vanilla flower variant
/// pattern, which excludes flowerpot-* / planter-* / etc.), playing a hoe-till
/// animation on arrival. Pure cosmetic / immersion - no block mutation, no inventory
/// changes.
///
/// Anchored on workstation so she stays in her plot. The minDistance config skips
/// flowers too close to the workstation - she's meant to actually go for a walk
/// out to check distant flowers, not just hoe-till the one next to her station.
///
/// Replaces the old AiTaskVillagerPlantHorsetail which depended on a hard-coded
/// horsetail block lookup that wasn't resolving in 1.22.
/// </summary>
public class AiTaskVillagerCheckFlowers : AiTaskGotoAndInteract
{
    private float minDistance;

    public AiTaskVillagerCheckFlowers(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        // Hoe-till is the herbalist's "tending" animation - same one PlantHorsetail used.
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init();

        // Skip flowers closer than this to her workstation. Forces her to actually
        // walk out to range rather than hoe-tilling whatever's nearest. 0 = no floor.
        minDistance = taskConfig["minDistance"].AsFloat(0f);
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsHerbalist()) return null;

        // Anchor on workstation so she stays in her plot.
        EntityBehaviorVillager beh = entity.GetBehavior<EntityBehaviorVillager>();
        BlockPos ws = beh?.Workstation;
        if (ws == null) return null;

        // Search radius = min(task maxDistance, village radius). The village.Radius
        // clamp keeps small villages from making the herbalist roam past their
        // boundary just because the task config says 80.
        IBlockAccessor ba = entity.World.BlockAccessor;
        int r = (int)maxDistance;
        Village village = beh?.Village;
        if (village != null && village.Radius < r) r = village.Radius;
        int wsY = ws.Y;
        double minDistSq = (double)(minDistance * minDistance);
        BlockPos best = null;
        double bestDistSq = double.MaxValue;
        BlockPos tmp = new BlockPos(0);

        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    tmp.Set(ws.X + dx, wsY + dy, ws.Z + dz);
                    Block b = ba.GetBlock(tmp);
                    string path = b?.Code?.Path;
                    if (path == null) continue;
                    // Match only true flower variants. The vanilla flower blocktype
                    // generates codes like "flower-horsetail-free" / "flower-bluebell-snow",
                    // and the StartsWith("flower-") + EndsWith filter naturally excludes
                    // flowerpot-* / planter-* / wildflower-* / crop-sunflower-* etc.
                    if (!path.StartsWith("flower-")) continue;
                    if (!path.EndsWith("-free") && !path.EndsWith("-snow")) continue;

                    double dsq = (double)(dx * dx + dz * dz + dy * dy);
                    // Skip flowers inside the no-go floor around her workstation.
                    if (dsq < minDistSq) continue;
                    if (dsq < bestDistSq)
                    {
                        bestDistSq = dsq;
                        best = tmp.Copy();
                    }
                }
            }
        }

        if (best == null) return null;
        // Stand at the flower's XZ centre at ground level.
        return best.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override void ApplyInteractionEffect()
    {
        // Pure cosmetic - the hoe-till animation runs via interactAnim on arrival,
        // nothing to mutate.
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    private bool IsHerbalist()
        => entity?.Code?.Path?.EndsWith("-herbalist") == true;
}
