using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Self-healing cleanup: if a villager has carried a stack unchanged for ~30s (the target that
/// wanted it got satisfied elsewhere), walk to the nearest container and deposit it so the
/// single carry slot is freed for other work. Also acts as end-of-day tidy. Verified in-game.
/// </summary>
public class AiTaskVillagerReturnCarry : AiTaskGotoAndInteract
{
    private int searchRadius = 12;
    private long thresholdMs = VillagerInventoryMath.DefaultOrphanThresholdMs;
    private BlockPos claimedPos;
    private readonly ContainerCooldownTracker cooldown = new ContainerCooldownTracker(60000);
    private const int ReachSearchDepth = 3000;

    public AiTaskVillagerReturnCarry(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        if (taskConfig["searchRadius"] != null) searchRadius = taskConfig["searchRadius"].AsInt(12);
        if (taskConfig["orphanSeconds"] != null) thresholdMs = taskConfig["orphanSeconds"].AsInt(30) * 1000L;
    }

    public override bool ShouldExecute()
    {
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        if (bh == null || bh.IsCarryEmpty) return false;
        if (!VillagerInventoryMath.IsCarryOrphaned(bh.CarryChangedMs, entity.World.ElapsedMilliseconds, thresholdMs)) return false;
        return base.ShouldExecute();
    }

    protected override Vec3d GetTargetPos()
    {
        ReleaseClaim();
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        Village village = bh?.Village;
        if (village == null || bh.IsCarryEmpty) return null;

        long now = entity.World.ElapsedMilliseconds;
        Vec3d from = entity.ServerPos.XYZ;
        List<BlockPos> ranked = VillagerContainerFinder.RankContainers(
            entity.World, village, from, entity.EntityId, cooldown, now, HasFreeSpaceForCarry(bh));

        foreach (BlockPos candidate in ranked.Take(5))
        {
            Vec3d approach = VillagerContainerFinder.ApproachPos(entity.World, candidate, from);
            if (approach == null) { cooldown.Mark(candidate, now); continue; }
            if (!CanReach(approach, ReachSearchDepth)) { cooldown.Mark(candidate, now); continue; }
            if (!VsVillage.ContainerClaims.TryClaim(candidate, entity.EntityId, now)) continue;
            claimedPos = candidate;
            return approach;
        }
        return null;
    }

    private System.Func<BlockEntityContainer, bool> HasFreeSpaceForCarry(EntityBehaviorVillager bh)
    {
        ItemStack carried = bh.CarrySlot;
        return be =>
        {
            if (be.Inventory == null || carried == null) return false;
            WeightedSlot sink = be.Inventory.GetBestSuitedSlot(new DummySlot(carried));
            return sink?.slot != null;
        };
    }

    protected override void ApplyInteractionEffect()
    {
        if (claimedPos == null) return;
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        if (bh == null || bh.IsCarryEmpty) return;
        if (entity.World.BlockAccessor.GetBlockEntity(claimedPos) is BlockEntityContainer be && be.Inventory != null)
        {
            DummySlot source = new DummySlot(bh.CarrySlot);
            WeightedSlot sink = be.Inventory.GetBestSuitedSlot(source);
            if (sink?.slot != null)
            {
                int moved = source.TryPutInto(entity.World, sink.slot, source.StackSize);
                if (moved > 0)
                {
                    sink.slot.MarkDirty();
                    entity.Api.Logger.Notification("[vsvillage] returncarry: villager {0} deposited {1}x into container at {2}",
                        entity.EntityId, moved, claimedPos);
                    bh.CarrySlot = source.Empty ? null : source.Itemstack;
                }
            }
            else
            {
                cooldown.Mark(claimedPos, entity.World.ElapsedMilliseconds); // full on arrival — skip it next search
            }
        }
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        ReleaseClaim();
    }

    private void ReleaseClaim()
    {
        if (claimedPos != null)
        {
            VsVillage.ContainerClaims.Release(claimedPos, entity.EntityId);
            claimedPos = null;
        }
    }
}
