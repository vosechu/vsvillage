using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Herbalist morning hoe-till on flower-*-free/snow blocks near her workstation. Cosmetic, no mutation.
// minDistance skips flowers too close so she actually walks out to range.
public class AiTaskVillagerCheckFlowers : AiTaskGotoAndInteract
{
    private float minDistance;

    public AiTaskVillagerCheckFlowers(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average,
            SupressDefaultAnimation = true,
            Weight = 5f
        }.Init();

        // No-go floor around the workstation. 0 = no floor.
        minDistance = taskConfig["minDistance"].AsFloat(0f);
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsHerbalist()) return null;

        // Anchor on workstation so she stays in her plot.
        EntityBehaviorVillager beh = entity.GetBehavior<EntityBehaviorVillager>();
        BlockPos ws = beh?.Workstation;
        if (ws == null) return null;

        // Search radius clamped by village.Radius so small villages don't let the herbalist roam past their boundary.
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
                    // Match only the real flower-*-{free,snow} vanilla blocktype, not flowerpot/planter/wildflower/crop.
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
        // Cosmetic only; the interactAnim runs on arrival.
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    private bool IsHerbalist()
        => entity?.Code?.Path?.EndsWith("-herbalist") == true;
}
