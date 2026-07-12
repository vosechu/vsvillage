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
            entity.World, village, from, entity.EntityId, cooldown, now, WantsItem);

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

    // Which containers hold something worth fetching. Profession subclasses override.
    protected virtual bool WantsItem(BlockEntityContainer be)
    {
        if (be.Inventory == null) return false;
        foreach (ItemSlot slot in be.Inventory) if (!slot.Empty) return true;
        return false;
    }

    // How many of a matched stack to withdraw. Default: the whole source stack. Subclasses cap it.
    protected virtual int WithdrawNeed(ItemSlot src) => src.StackSize;

    // Movable quantity for a source slot under the single-stack carry model.
    protected int Movable(ItemSlot src) => VillagerInventoryMath.MovableQuantity(
        WithdrawNeed(src), src.StackSize, src.Itemstack.Collectible.MaxStackSize, src.StackSize);

    // Which source slot to withdraw. Default: the first non-empty slot with a movable quantity.
    // Subclasses (e.g. the shepherd feed-fetch) override to pick a specific/best slot — e.g. the
    // highest-priority feed the target pen's animal will actually eat, rather than first-come.
    protected virtual ItemSlot ChooseSourceSlot(BlockEntityContainer be)
    {
        foreach (ItemSlot src in be.Inventory)
        {
            if (src.Empty) continue;
            if (Movable(src) > 0) return src;
        }
        return null;
    }

    protected override void ApplyInteractionEffect()
    {
        if (claimedPos == null) return;
        if (entity.World.BlockAccessor.GetBlockEntity(claimedPos) is BlockEntityContainer be && be.Inventory != null)
        {
            EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
            if (bh == null) return;
            ItemSlot src = ChooseSourceSlot(be);
            int move = (src != null && !src.Empty) ? Movable(src) : 0;
            if (move > 0)
            {
                ItemStack carried = src.TakeOut(move);
                src.MarkDirty();
                bh.CarrySlot = carried;
                entity.Api.Logger.Notification("[vsvillage] transact: villager {0} withdrew {1}x {2} into carry from {3}",
                    entity.EntityId, carried.StackSize, carried.Collectible?.Code, claimedPos);
                return; // single-stack carry, done
            }
            // Nothing movable on arrival (empty, or every slot filtered out): cool it so we don't re-pick it.
            cooldown.Mark(claimedPos, entity.World.ElapsedMilliseconds);
            entity.Api.Logger.Notification("[vsvillage] transact: villager {0} found container at {1} with nothing to take", entity.EntityId, claimedPos);
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
