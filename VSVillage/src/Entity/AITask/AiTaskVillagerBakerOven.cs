using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Baker profession idle task.
/// Each visit to the workstation the baker tends the clay oven:
///   1. Removes (despawns) any finished bread from the oven (keeps only -partbaked).
///   2. Adds firewood and lights the oven when it is empty and cold.
///   3. Loads raw dough once the oven is hot but no longer burning.
/// Items are conjured directly (no inventory system required for the baker entity).
/// </summary>
public class AiTaskVillagerBakerOven : AiTaskGotoAndInteract
{
    // ---------- config -------------------------------------------------------

    /// <summary>Item code for the dough to bake (default: game:dough-spelt).</summary>
    private string doughCode;

    /// <summary>Minimum oven temperature before dough is loaded.</summary>
    private float minBakeTemp;

    /// <summary>Minimum real-time ms between oven checks (default 30 s).</summary>
    private long checkIntervalMs;

    // ---------- state --------------------------------------------------------

    private long lastCheckMs = -99999L;

    // -------------------------------------------------------------------------

    public AiTaskVillagerBakerOven(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        doughCode = taskConfig["doughCode"].AsString("game:dough-spelt");
        minBakeTemp = taskConfig["minBakeTemp"].AsFloat(180f);
        checkIntervalMs = (long)(taskConfig["checkIntervalSeconds"].AsFloat(15f) * 1000f);
    }

    // -------------------------------------------------------------------------
    // AiTaskGotoAndInteract overrides
    // -------------------------------------------------------------------------

    protected override Vec3d GetTargetPos()
    {
        if (!IsBaker()) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        // Only bother walking there if there is actually something to do.
        if (entity.World.ElapsedMilliseconds - lastCheckMs < checkIntervalMs) return null;

        BlockEntityOven oven = FindOven(ws);
        if (oven == null) return null;

        if (!OvenNeedsAttention(oven)) return null;

        // Navigate to the oven itself so the baker walks inside the room rather
        // than stopping at the workstation block near the entrance.
        return oven.Pos.ToVec3d().Add(0.5, 1.0, 0.5);
    }

    // Relaxed to 2.5 blocks so the baker doesn't need to be flush against the
    // oven face — one block away is close enough to tend it.
    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 49.0;
    }

    protected override void ApplyInteractionEffect()
    {
        if (!IsBaker()) return;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return;

        BlockEntityOven oven = FindOven(ws);
        if (oven == null) return;

        lastCheckMs = entity.World.ElapsedMilliseconds;

        // Play a quick animation during the interaction.
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = "hoe-till",
            Code = "hoe-till",
            AnimationSpeed = 1.2f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());

        // Run oven logic immediately to avoid callback timing edge-cases.
        TendOven(oven);
        FillBarrelsInRoom(ws);

        // Keep animation briefly for visual feedback.
        entity.World.RegisterCallback(_ => entity.AnimManager.StopAnimation("hoe-till"), 1200);
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    // -------------------------------------------------------------------------
    // Oven logic
    // -------------------------------------------------------------------------

    private void TendOven(BlockEntityOven oven)
    {
        // Step 1 — collect finished bread (keep only -partbaked).
        CollectFinishedBread(oven);

        // Step 2 — fuel up and ignite if the oven is cold and empty.
        if (!oven.IsBurning && oven.FuelSlot.Empty && !oven.HasBakeables && oven.ovenTemperature < minBakeTemp)
        {
            TryFuelAndIgnite(oven);
            return; // don't load dough in the same visit; wait for oven to heat
        }

        // Step 3 — load dough once hot and done burning.
        if (!oven.IsBurning && !oven.HasFuel && oven.ovenTemperature >= minBakeTemp)
        {
            TryLoadDough(oven);
        }
    }

    /// <summary>
    /// Removes all bread-* stacks except -partbaked from the oven slots (despawn).
    /// </summary>
    private void CollectFinishedBread(BlockEntityOven oven)
    {
        bool changed = false;
        int capacity = oven.bakeableCapacity;
        for (int i = 0; i < capacity; i++)
        {
            ItemSlot slot = oven.Inventory[i];
            if (slot.Empty) continue;

            string path = slot.Itemstack?.Collectible?.Code?.Path;
            if (path == null) continue;

            // Keep only partbaked bread in oven; remove finished/charred/etc.
            if (path.StartsWith("bread-") && !path.EndsWith("-partbaked"))
            {
                slot.Itemstack = null;
                slot.MarkDirty();
                changed = true;
            }
        }
        if (changed) oven.MarkDirty(true);
    }

    /// <summary>
    /// Adds up to fuelitemCapacity firewood to the fuel slot and lights the oven.
    /// </summary>
    private void TryFuelAndIgnite(BlockEntityOven oven)
    {
        Item firewood = entity.World.GetItem(new AssetLocation("game:firewood"));
        if (firewood == null) return;

        int fuelCapacity = oven.fuelitemCapacity;
        if (!oven.FuelSlot.Empty && oven.ovenTemperature < minBakeTemp)
        {
            // Top up existing stack.
            oven.FuelSlot.Itemstack.StackSize = Math.Max(oven.FuelSlot.Itemstack.StackSize, fuelCapacity);
        }
        else if (oven.ovenTemperature < minBakeTemp)
        {
            oven.FuelSlot.Itemstack = new ItemStack(firewood, fuelCapacity);
        }
        oven.FuelSlot.MarkDirty();

        if (oven.CanIgnite())
        {
            oven.TryIgnite();
        }

        oven.MarkDirty(true);
    }

    /// <summary>
    /// Fills empty oven slots with raw dough (conjured).
    /// </summary>
    private void TryLoadDough(BlockEntityOven oven)
    {
        Item doughItem = entity.World.GetItem(new AssetLocation(doughCode));
        if (doughItem == null) return;

        int capacity = oven.bakeableCapacity;
        bool changed = false;
        for (int i = 0; i < capacity; i++)
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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool OvenNeedsAttention(BlockEntityOven oven)
    {
        // Has collectable bread to clear? (anything bread-* that isn't -partbaked)
        int capacity = oven.bakeableCapacity;
        for (int i = 0; i < capacity; i++)
        {
            ItemSlot slot = oven.Inventory[i];
            if (slot.Empty) continue;
            string path = slot.Itemstack?.Collectible?.Code?.Path;
            if (path != null && path.StartsWith("bread-") && !path.EndsWith("-partbaked"))
                return true;
        }

        // Needs fuel + ignition?
        if (!oven.IsBurning && oven.FuelSlot.Empty && !oven.HasBakeables)
            return true;

        // Needs dough loaded?
        if (!oven.IsBurning && !oven.HasFuel && oven.ovenTemperature >= minBakeTemp)
        {
            for (int i = 0; i < capacity; i++)
                if (oven.Inventory[i].Empty) return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a BlockEntityOven at or near (±4 blocks) the workstation position.
    /// </summary>
    private BlockEntityOven FindOven(BlockPos ws)
    {
        // Try the workstation block itself first.
        BlockEntityOven oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(ws);
        if (oven != null) return oven;

        // Scan adjacent blocks (±5 horizontal, ±2 vertical).
        BlockPos tmp = new BlockPos(ws.dimension);
        for (int dx = -4; dx <= 4; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -4; dz <= 4; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    tmp.Set(ws.X + dx, ws.Y + dy, ws.Z + dz);
                    oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(tmp);
                    if (oven != null) return oven;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Finds every barrel in the baker's room and fills any empty ones with water.
    /// Called each time the baker tends the oven so the barrel is always topped up.
    /// </summary>
    private void FillBarrelsInRoom(BlockPos ws)
    {
        Room room = null;
        try { room = entity.World.Api.ModLoader.GetModSystem<RoomRegistry>()?.GetRoomForPosition(ws.UpCopy()); }
        catch { return; }
        if (room == null) return;

        IBlockAccessor ba = entity.World.BlockAccessor;
        Cuboidi loc = room.Location;
        int x1 = loc.X1, x2 = Math.Min(loc.X2, x1 + 35);
        int y1 = loc.Y1, y2 = Math.Min(loc.Y2, y1 + 15);
        int z1 = loc.Z1, z2 = Math.Min(loc.Z2, z1 + 35);
        BlockPos tmp = new BlockPos(0);

        Item water = entity.World.GetItem(new AssetLocation("game:waterportion"));
        if (water == null) return;

        for (int x = x1; x <= x2; x++)
            for (int y = y1; y <= y2; y++)
                for (int z = z1; z <= z2; z++)
                {
                    tmp.Set(x, y, z);
                    if (ba.GetBlock(tmp)?.Code?.Path?.Contains("barrel") != true) continue;

                    BlockEntityBarrel barrel = ba.GetBlockEntity<BlockEntityBarrel>(tmp);
                    if (barrel?.Inventory == null) continue;
                    if (!barrel.Inventory[0].Empty) continue; // already has contents

                    barrel.Inventory[0].Itemstack = new ItemStack(water, 10);
                    barrel.Inventory[0].MarkDirty();
                    barrel.MarkDirty(true);
                }
    }

    private bool IsBaker()
    {
        EntityAgent entityAgent = entity;
        return entityAgent != null && entityAgent.Code?.Path?.EndsWith("-baker") == true;
    }
}