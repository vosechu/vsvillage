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
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        if (bh == null || bh.IsCarryEmpty) return null;
        BlockPos found = VillagerContainerFinder.FindNearestContainer(
            entity.World, entity.ServerPos.AsBlockPos, searchRadius, HasFreeSpaceForCarry(bh));
        if (found == null) return null;
        if (!VsVillage.ContainerClaims.TryClaim(found, entity.EntityId, entity.World.ElapsedMilliseconds))
            return null;
        claimedPos = found;
        return found.ToVec3d().Add(0.5, 0.0, 0.5);
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
        }
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        if (claimedPos != null) { VsVillage.ContainerClaims.Release(claimedPos, entity.EntityId); claimedPos = null; }
    }
}
