using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// One leg of a real-resource job: walk to the nearest container holding what's wanted, claim it,
/// and pull one stack into the villager's carry slot.
/// </summary>
// FIXME: withdraw-only. Profession specs still need to supply (a) a real "what to fetch" predicate
// in place of the any-item placeholder in GetTargetPos, and (b) the deliver/use leg that consumes
// the carried stack into actual work.
public class AiTaskGotoAndTransact : AiTaskGotoAndInteract
{
    private int searchRadius = 12;
    private BlockPos claimedPos;

    public AiTaskGotoAndTransact(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        if (taskConfig["searchRadius"] != null) searchRadius = taskConfig["searchRadius"].AsInt(12);
    }

    public override bool ShouldExecute()
    {
        // Only fetch when the carry slot is free.
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        if (bh == null || !bh.IsCarryEmpty) return false;
        return base.ShouldExecute();
    }

    protected override Vec3d GetTargetPos()
    {
        // Drop any prior target's claim before choosing a new one, so re-evaluating the target
        // never leaks a claim on a container we're no longer heading to. Single-threaded AI, so
        // this release + reclaim is atomic relative to other villagers.
        ReleaseClaim();
        // Placeholder predicate: any container holding at least one item. Profession specs override this.
        BlockPos found = VillagerContainerFinder.FindNearestContainer(
            entity.World, entity.ServerPos.AsBlockPos, searchRadius, HasAnyItem);
        if (found == null) return null;
        if (!VsVillage.ContainerClaims.TryClaim(found, entity.EntityId, entity.World.ElapsedMilliseconds))
            return null; // someone else holds it
        claimedPos = found;
        entity.Api.Logger.Notification("[vsvillage] transact: villager {0} heading to container at {1}", entity.EntityId, found);
        return found.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    private static bool HasAnyItem(BlockEntityContainer be)
    {
        if (be.Inventory == null) return false;
        foreach (ItemSlot slot in be.Inventory) if (!slot.Empty) return true;
        return false;
    }

    protected override void ApplyInteractionEffect()
    {
        if (claimedPos == null) return;
        if (entity.World.BlockAccessor.GetBlockEntity(claimedPos) is BlockEntityContainer be && be.Inventory != null)
        {
            EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
            if (bh == null) return;
            foreach (ItemSlot src in be.Inventory)
            {
                if (src.Empty) continue;
                int move = VillagerInventoryMath.MovableQuantity(
                    src.StackSize, src.StackSize, src.Itemstack.Collectible.MaxStackSize, src.StackSize);
                if (move <= 0) continue;
                ItemStack carried = src.TakeOut(move);
                src.MarkDirty();
                bh.CarrySlot = carried;
                entity.Api.Logger.Notification("[vsvillage] transact: villager {0} withdrew {1}x {2} into carry from {3}",
                    entity.EntityId, carried.StackSize, carried.Collectible?.Code, claimedPos);
                break; // single-stack carry
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
