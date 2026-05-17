using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Baker task: walk to the oven and fuel + ignite it when cold.
// Dough loading is now handled by AiTaskVillagerBakerCollectBread (one round-trip
// pulls finished bread AND loads fresh dough), so this task is purely the cold-oven
// branch. Cooldown is short because the work is mostly checking temperature.
// Also fills any empty barrels in the baker's room with water as a side effect.
public class AiTaskVillagerBakerTendOven : AiTaskVillagerBakerBase
{
    // ---------- config -------------------------------------------------------

    // Internal re-check guard: don't run GetTargetPos more than once
    // per checkIntervalMs even if the AI scheduler asks. Defaults to 5s, matching
    // the new short JSON cooldown.
    private long checkIntervalMs;

    // ---------- state --------------------------------------------------------

    private long lastCheckMs = -99999L;
    private BlockPos ovenPos;


    public AiTaskVillagerBakerTendOven(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        checkIntervalMs = (long)(taskConfig["checkIntervalSeconds"].AsFloat(5f) * 1000f);
    }

    // AiTaskGotoAndInteract overrides

    protected override Vec3d GetTargetPos()
    {
        if (!IsBaker()) return null;
        if (entity.World.ElapsedMilliseconds - lastCheckMs < checkIntervalMs) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        BlockEntityOven oven = FindOven(ws);
        if (oven == null) return null;
        if (!OvenNeedsFueling(oven)) return null;

        ovenPos = oven.Pos.Copy();
        return oven.Pos.ToVec3d().Add(0.5, 1.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 49.0;
    }

    protected override void ApplyInteractionEffect()
    {
        if (!IsBaker() || ovenPos == null) return;

        BlockEntityOven oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(ovenPos);
        if (oven == null) return;

        lastCheckMs = entity.World.ElapsedMilliseconds;

        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = "hoe-till",
            Code = "hoe-till",
            AnimationSpeed = 1.2f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());

        TryFuelAndIgnite(oven);

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws != null) FillBarrelsInRoom(ws);

        entity.World.RegisterCallback(_ => entity.AnimManager.StopAnimation("hoe-till"), 1200);
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
        ovenPos = null;
    }

    // Oven tending logic - fuel + ignite branch only

    // True if the oven is cold + empty + has no fuel - ready to be fueled
    // and ignited. Dough-loading conditions are handled by CollectBread.
    private bool OvenNeedsFueling(BlockEntityOven oven)
    {
        return !oven.IsBurning
            && oven.FuelSlot.Empty
            && !oven.HasBakeables
            && oven.ovenTemperature < minBakeTemp;
    }

    private void TryFuelAndIgnite(BlockEntityOven oven)
    {
        Item firewood = entity.World.GetItem(new AssetLocation("game:firewood"));
        if (firewood == null) return;

        int fuelCapacity = oven.fuelitemCapacity;
        if (!oven.FuelSlot.Empty && oven.ovenTemperature < minBakeTemp)
        {
            oven.FuelSlot.Itemstack.StackSize = Math.Max(oven.FuelSlot.Itemstack.StackSize, fuelCapacity);
        }
        else if (oven.ovenTemperature < minBakeTemp)
        {
            oven.FuelSlot.Itemstack = new ItemStack(firewood, fuelCapacity);
        }
        oven.FuelSlot.MarkDirty();

        if (oven.CanIgnite()) oven.TryIgnite();

        oven.MarkDirty(true);
    }

    // Barrel filling

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
            if (!barrel.Inventory[0].Empty) continue;

            barrel.Inventory[0].Itemstack = new ItemStack(water, 10);
            barrel.Inventory[0].MarkDirty();
            barrel.MarkDirty(true);
        }
    }
}
