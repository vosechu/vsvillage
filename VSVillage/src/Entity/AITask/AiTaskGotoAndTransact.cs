using System.Collections.Generic;
using System.Linq;
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
    private readonly ContainerCooldownTracker cooldown = new ContainerCooldownTracker(60000); // 60s, per FillTrough
    private const int ReachSearchDepth = 3000; // modest cap: a chest needing a longer path is too far

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
        ReleaseClaim(); // drop any prior claim before re-choosing (single-threaded AI, so atomic)
        EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
        Village village = bh?.Village;
        if (village == null) return null;

        long now = entity.World.ElapsedMilliseconds;
        Vec3d from = entity.ServerPos.XYZ;
        // Placeholder predicate: any container holding at least one item. Profession specs override this.
        List<BlockPos> ranked = VillagerContainerFinder.RankContainers(
            entity.World, village, from, entity.EntityId, cooldown, now, HasAnyItem);

        foreach (BlockPos candidate in ranked.Take(5)) // probe only the nearest few
        {
            Vec3d approach = VillagerContainerFinder.ApproachPos(entity.World, candidate, from);
            if (approach == null) { cooldown.Mark(candidate, now); continue; }
            if (!CanReach(approach, ReachSearchDepth)) { cooldown.Mark(candidate, now); continue; }
            if (!VsVillage.ContainerClaims.TryClaim(candidate, entity.EntityId, now)) continue; // held by another
            claimedPos = candidate;
            entity.Api.Logger.Notification("[vsvillage] transact: villager {0} heading to container at {1}", entity.EntityId, candidate);
            return approach;
        }
        return null;
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
                return; // single-stack carry, done
            }
            // Reached only if the chest was empty on arrival (race): cool it so we don't re-pick it.
            cooldown.Mark(claimedPos, entity.World.ElapsedMilliseconds);
            entity.Api.Logger.Notification("[vsvillage] transact: villager {0} found container at {1} empty on arrival", entity.EntityId, claimedPos);
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
