using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Shared base for baker AI tasks. Provides oven lookup, bread collection,
// dough loading, and shared config (doughCode + minBakeTemp).
public abstract class AiTaskVillagerBakerBase : AiTaskGotoAndInteract
{
    // Item code for the dough to bake (default: game:dough-spelt). Read from
    // task JSON config and shared by all baker subtasks via this base class.
    protected string doughCode;

    // Minimum oven temperature (degC) before dough is loaded. Read from
    // task JSON config and shared.
    protected float minBakeTemp;

    protected AiTaskVillagerBakerBase(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        doughCode   = taskConfig["doughCode"].AsString("game:dough-spelt");
        minBakeTemp = taskConfig["minBakeTemp"].AsFloat(180f);
    }

    // Shared helpers

    protected bool IsBaker()
        => entity?.Code?.Path?.EndsWith("-baker") == true;

    // Finds a BlockEntityOven at or within +/-4 blocks of the given position.
    protected BlockEntityOven FindOven(BlockPos ws)
    {
        BlockEntityOven oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(ws);
        if (oven != null) return oven;

        BlockPos tmp = new BlockPos(ws.dimension);
        for (int dx = -4; dx <= 4; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -4; dz <= 4; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            tmp.Set(ws.X + dx, ws.Y + dy, ws.Z + dz);
            oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(tmp);
            if (oven != null) return oven;
        }
        return null;
    }

    // Returns true if any oven slot contains finished bread
    // (bread-* that is not -partbaked).
    protected static bool HasFinishedBread(BlockEntityOven oven)
    {
        for (int i = 0; i < oven.bakeableCapacity; i++)
        {
            ItemSlot slot = oven.Inventory[i];
            if (slot.Empty) continue;
            string path = slot.Itemstack?.Collectible?.Code?.Path;
            if (path != null && path.StartsWith("bread-") && !path.EndsWith("-partbaked"))
                return true;
        }
        return false;
    }

    // Returns true if the oven has at least one empty bakeable slot.
    protected static bool HasEmptyBakeableSlot(BlockEntityOven oven)
    {
        for (int i = 0; i < oven.bakeableCapacity; i++)
            if (oven.Inventory[i].Empty) return true;
        return false;
    }

    // Removes all finished bread from the oven (anything bread-* except -partbaked).
    protected static void CollectFinishedBread(BlockEntityOven oven)
    {
        bool changed = false;
        for (int i = 0; i < oven.bakeableCapacity; i++)
        {
            ItemSlot slot = oven.Inventory[i];
            if (slot.Empty) continue;
            string path = slot.Itemstack?.Collectible?.Code?.Path;
            if (path != null && path.StartsWith("bread-") && !path.EndsWith("-partbaked"))
            {
                slot.Itemstack = null;
                slot.MarkDirty();
                changed = true;
            }
        }
        if (changed) oven.MarkDirty(true);
    }

    // Fills any empty bakeable slots with raw dough (one stack per slot). Vanilla
    // requires the oven to actually be hot (>= minBakeTemp) before dough is accepted -
    // callers should temperature-gate this themselves.
    protected void TryLoadDough(BlockEntityOven oven)
    {
        Item doughItem = entity.World.GetItem(new AssetLocation(doughCode));
        if (doughItem == null) return;

        bool changed = false;
        for (int i = 0; i < oven.bakeableCapacity; i++)
        {
            ItemSlot slot = oven.Inventory[i];
            if (slot.Empty)
            {
                slot.Itemstack = new ItemStack(doughItem, 1);
                slot.MarkDirty();
                changed = true;
            }
        }
        if (changed) oven.MarkDirty(true);
    }
}
