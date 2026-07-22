using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest;

// Shared building blocks for behavioral scenarios: spawn the cast (villagers, penned consumer animals),
// place seeded blocks, read live world state, and narrate for watch mode. Companion to TestScene, which
// owns terrain (BuildFlatArea) and animal spawning (SpawnStationaryAnimal). Every method is static and
// takes the ICoreServerAPI first, matching TestScene's convention, so a scenario stays a thin arena plus
// its assertions instead of each re-deriving this plumbing.
public static class ScenarioKit
{
    // Interactive-only narration: broadcasts to any connected players (watch mode); a no-op headless
    // (zero players), so it never affects suite output.
    public static void Note(ICoreServerAPI api, string msg)
    {
        if (api.World.AllOnlinePlayers.Length == 0) return;
        api.BroadcastMessageToAllGroups("[golden] " + msg, EnumChatType.Notification);
    }

    // Spawn a villager, optionally assigned to a village. Returns the entity id. Its AI ticks because the
    // movement-suite client parked on the arena keeps it inside the 128-block simulation range — the SAME
    // range that runs its physics (AI-active and physics-tracked are one threshold, GlobalConstants
    // .DefaultSimulationRange). No AlwaysActive: real villagers never set it, so the harness matches them.
    public static long SpawnVillager(ICoreServerAPI api, EntityProperties etype, BlockPos vp, Village village)
    {
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.ServerPos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        api.World.SpawnEntity(e);
        if (village != null) e.GetBehavior<EntityBehaviorVillager>().Village = village;
        return e.EntityId;
    }

    public static long SpawnVillager(ICoreServerAPI api, string code, BlockPos vp, Village village)
        => SpawnVillager(api, api.World.GetEntityType(new AssetLocation(code)), vp, village);

    // Spawn a consumer animal penned in place beside its trough. A non-AlwaysActive animal is frozen only
    // on a PLAYERLESS server (its AI never ticks); with a client connected it comes alive — real physics +
    // AI — and would wander off, breaking its role as a fixed diet source. So we ring it with a CONNECTED
    // fence (see PenAnimals) whose rails close the gaps a small animal would otherwise slip through.
    public static long PenAnimal(ICoreServerAPI api, string code, BlockPos pos)
    {
        List<long> ids = PenAnimals(api, code, new List<BlockPos> { pos });
        return ids.Count > 0 ? ids[0] : -1;
    }

    // Pen a GROUP of consumer animals (e.g. two stacked against one trough) as one enclosure: spawn each,
    // then fence every cell bordering the group that is air — skipping cells another penned animal occupies
    // and any solid block (the trough/chest), so it never clobbers a functional block or traps a penmate.
    // The freestanding fence variant is only a POST (~0.4 wide), leaving ~0.6-block gaps between posts a
    // 0.5-wide chicken slips through. So after placing the ring we trigger a neighbour update on each fence,
    // which runs BlockFence.OnNeighbourBlockChange — it swaps each post to the connected `type` variant and
    // grows rails that close the gaps. Place all first, THEN connect, so every fence sees its finished ring.
    public static List<long> PenAnimals(ICoreServerAPI api, string code, List<BlockPos> positions)
    {
        List<long> ids = new List<long>();
        foreach (BlockPos p in positions)
        {
            long id = TestScene.SpawnStationaryAnimal(api, code, p);
            if (id >= 0) ids.Add(id);
        }
        int fence = BlockId(api, "game:woodenfence-aged-ns-free");
        IBlockAccessor ba = api.World.BlockAccessor;
        List<BlockPos> fences = new List<BlockPos>();
        foreach (BlockPos p in positions)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    BlockPos c = new BlockPos(p.X + dx, p.Y, p.Z + dz);
                    // Compare by coordinate, not BlockPos identity: a penmate standing here must never be fenced in.
                    if (positions.Any(q => q.X == c.X && q.Y == c.Y && q.Z == c.Z)) continue;
                    if (ba.GetBlock(c).Id == 0) { ba.SetBlock(fence, c); fences.Add(c); }   // fence only air; skip the trough/chest
                }
        foreach (BlockPos c in fences) ba.TriggerNeighbourBlockUpdate(c);   // let each fence connect to its ring
        return ids;
    }

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

    // Release any container/trough claims these villagers hold on these positions. The claim registries
    // are process-STATIC with a 120s expiry, and running several scenarios in one boot ("all" suite) has
    // no server restart between them — so a claim left by one scenario would bleed into the next (they
    // reuse overlapping spawn-relative positions) and block it. Call in teardown before despawning.
    // Over-releasing is safe: Release() only removes a claim the caller actually owns, and a cross-registry
    // call (a chest position on TroughClaims, say) is a harmless no-op.
    public static void ReleaseClaims(IEnumerable<long> villagerIds, IEnumerable<BlockPos> positions)
    {
        foreach (long id in villagerIds)
            foreach (BlockPos p in positions)
            {
                if (p == null) continue;
                VsVillage.VsVillage.ContainerClaims.Release(p, id);
                VsVillage.VsVillage.TroughClaims.Release(p, id);
            }
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
