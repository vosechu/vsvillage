using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest;

// Shared building blocks for behavioral scenarios: anchor an arena inside spawn's chunk, spawn the
// cast, place seeded blocks, read live world state (guarded against the headless read-unreliability
// documented in .claude/rules/testing.md), and narrate for watch mode. Companion to TestScene, which
// owns terrain (BuildFlatArea) and diet-source spawning (SpawnStationaryAnimal). Every method is static
// and takes the ICoreServerAPI first, matching TestScene's convention, so a scenario stays a thin arena
// plus its assertions instead of each re-deriving this plumbing.
public static class ScenarioKit
{
    // Which way an arena should extend so every read-critical block entity stays inside spawn's own
    // 32^3 chunk. Chunks NEIGHBOURING spawn decay to permanently-unreadable a minute or two into a
    // headless run, and a spawn can sit on a chunk corner, so even a small negative offset can cross
    // out. Returns +1 on an axis when spawn sits in the low half of its chunk (room toward +), else -1.
    public static (int dirX, int dirZ) AnchorDirections(BlockPos spawn)
        => ((spawn.X - ((spawn.X >> 5) << 5) <= 15) ? 1 : -1,
            (spawn.Z - ((spawn.Z >> 5) << 5) <= 15) ? 1 : -1);

    // Interactive-only narration: broadcasts to any connected players (watch mode); a no-op headless
    // (zero players), so it never affects suite output.
    public static void Note(ICoreServerAPI api, string msg)
    {
        if (api.World.AllOnlinePlayers.Length == 0) return;
        api.BroadcastMessageToAllGroups("[golden] " + msg, EnumChatType.Notification);
    }

    // Spawn a villager that ticks its AI on a playerless server (AlwaysActive set BEFORE SpawnEntity),
    // optionally assigned to a village. Returns the entity id.
    public static long SpawnVillager(ICoreServerAPI api, EntityProperties etype, BlockPos vp, Village village)
    {
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.ServerPos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.AlwaysActive = true;                            // MUST precede SpawnEntity (ticks AI with no player)
        api.World.SpawnEntity(e);
        if (village != null) e.GetBehavior<EntityBehaviorVillager>().Village = village;
        return e.EntityId;
    }

    public static long SpawnVillager(ICoreServerAPI api, string code, BlockPos vp, Village village)
        => SpawnVillager(api, api.World.GetEntityType(new AssetLocation(code)), vp, village);

    // Place a container block coplanar on the flat floor and seed slot 0 with `stack` (null = leave it
    // empty). Works for chests and troughs alike — BlockEntityTrough is a BlockEntityContainer.
    public static BlockPos PlaceContainer(ICoreServerAPI api, BlockPos pos, int blockId, ItemStack stack)
    {
        api.World.BlockAccessor.SetBlock(blockId, pos);
        if (stack != null
            && api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be
            && be.Inventory != null && be.Inventory.Count > 0)
        {
            be.Inventory[0].Itemstack = stack;
            be.Inventory[0].MarkDirty();
            be.MarkDirty(true);
        }
        return pos;
    }

    // A `height`-tall solid ring on the four horizontal neighbours of `around` (the block itself stays
    // open). Used to wrap a decoy: only ever impedes a villager that WRONGLY tries for it, so it can
    // never make a correct one fail. Pass blockId 0 to clear a previously built ring.
    public static void WallRing(ICoreServerAPI api, BlockPos around, int blockId, int height)
    {
        foreach (BlockFacing f in BlockFacing.HORIZONTALS)
            for (int dy = 0; dy < height; dy++)
                api.World.BlockAccessor.SetBlock(blockId,
                    new BlockPos(around.X + f.Normali.X, around.Y + dy, around.Z + f.Normali.Z));
    }

    // Resolve a block code to its id; warns and returns 0 if it doesn't resolve.
    public static int BlockId(ICoreServerAPI api, string code)
    {
        Block b = api.World.GetBlock(new AssetLocation(code));
        if (b == null) { api.Logger.Warning("[harness] block '{0}' did not resolve!", code); return 0; }
        return b.BlockId;
    }

    // True only when the block entity at pos is currently loaded and queryable. Guards window-sampled
    // reads against transient no-BE reads (a BE reads absent for seconds at a time headless).
    public static bool IsReadable(ICoreServerAPI api, BlockPos pos)
        => api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null;

    // Count of items whose collectible code path == `code` across the block entity's inventory (chest
    // or trough — both are containers). 0 when the block entity is unreadable.
    public static int ItemCountIn(ICoreServerAPI api, BlockPos pos, string code)
    {
        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null)
            return be.Inventory.Where(s => !s.Empty && s.Itemstack.Collectible.Code.Path == code).Sum(s => s.StackSize);
        return 0;
    }

    // Total item count across the inventory (any code) — used to detect ANY drain. 0 when unreadable.
    public static int TotalItemsIn(ICoreServerAPI api, BlockPos pos)
    {
        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null)
            return be.Inventory.Where(s => !s.Empty).Sum(s => s.StackSize);
        return 0;
    }

    // The collectible code path a villager is currently carrying, or null.
    public static string CarryPath(ICoreServerAPI api, long id)
        => api.World.GetEntityById(id)?.GetBehavior<EntityBehaviorVillager>()?.CarrySlot?.Collectible?.Code?.Path;
}
