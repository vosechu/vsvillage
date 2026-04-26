using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class Village
{
    [ProtoMember(1)]
    public BlockPos Pos;

    [ProtoMember(2)]
    public int Radius;

    [ProtoMember(3)]
    public string Name;

    [ProtoMember(4)]
    public Dictionary<BlockPos, VillagerBed> Beds = new Dictionary<BlockPos, VillagerBed>();

    [ProtoMember(5)]
    public Dictionary<BlockPos, VillagerWorkstation> Workstations = new Dictionary<BlockPos, VillagerWorkstation>();

    [ProtoMember(6)]
    public HashSet<BlockPos> Gatherplaces = new HashSet<BlockPos>();

    [ProtoMember(7)]
    public Dictionary<long, VillagerData> VillagerSaveData = new Dictionary<long, VillagerData>();

    [ProtoMember(8)]
    public HashSet<BlockPos> Waypoints = new HashSet<BlockPos>();

    public ICoreAPI Api;

    /// <summary>Runtime-only flag — not persisted. True while a Gather is active.</summary>
    public bool IsGatherActive;

    /// <summary>Callback ID for the gather auto-clear timer. -1 when no timer is running.</summary>
    public long GatherCallbackId = -1;

    public string Id => "village-" + Pos.ToString();

    public List<EntityBehaviorVillager> Villagers => VillagerSaveData.Values
        .ToList()
        .ConvertAll((VillagerData data) => Api.World.GetEntityById(data.Id)?.GetBehavior<EntityBehaviorVillager>())
        .Where(v => v != null)
        .ToList();

    public void Init(ICoreAPI api)
    {
        Api = api;
        ScrubNullKeys();
        if (api.Side == EnumAppSide.Server)
        {
            // Delay the ghost sweep until the server has had time to load its initial
            // chunk radius. "MaxChunkRadius" / "SpawnChunksWidth" in server config
            // controls how many chunks load on startup; we scale the wait accordingly.
            // Default 15 s covers a typical radius of 8–12 chunks.
            int delayMs = 15000;
            try
            {
                // Try the two known server-config property names (VS version differences).
                var cfg = (api as Vintagestory.API.Server.ICoreServerAPI)?.Server?.Config;
                if (cfg != null)
                {
                    // MaxChunkRadius is the standard name in most VS versions.
                    int r = cfg.MaxChunkRadius;
                    delayMs = Math.Max(8000, r * 1500);
                }
            }
            catch
            {
                // Property unavailable — fall back to 15 s.
            }
            api.World.RegisterCallback(delegate { ScrubGhostStructures(); }, delayMs);
        }
    }

    /// <summary>
    /// Removes workstation/bed entries whose block entity no longer exists in the
    /// world. Only acts on chunks that are currently loaded — safe to call on startup.
    /// </summary>
    private void ScrubGhostStructures()
    {
        if (Api == null) return;
        IBlockAccessor ba = Api.World.BlockAccessor;

        List<BlockPos> deadWorkstations = new List<BlockPos>();
        foreach (BlockPos pos in Workstations.Keys)
        {
            if (pos == null) continue;
            // Skip positions in unloaded chunks — null BE is ambiguous there.
            if (!ba.IsValidPos(pos)) continue;
            if (ba.GetChunkAtBlockPos(pos) == null) continue;
            if (ba.GetBlockEntity<BlockEntityVillagerWorkstation>(pos) == null)
            {
                deadWorkstations.Add(pos);
                Api.Logger.Warning("[VsVillage] Village " + Id + ": removing ghost workstation at " + pos);
            }
        }
        foreach (BlockPos pos in deadWorkstations) Workstations.Remove(pos);

        List<BlockPos> deadBeds = new List<BlockPos>();
        foreach (BlockPos pos in Beds.Keys)
        {
            if (pos == null) continue;
            if (!ba.IsValidPos(pos)) continue;
            if (ba.GetChunkAtBlockPos(pos) == null) continue;
            if (ba.GetBlockEntity<BlockEntityVillagerBed>(pos) == null)
            {
                deadBeds.Add(pos);
                Api.Logger.Warning("[VsVillage] Village " + Id + ": removing ghost bed at " + pos);
            }
        }
        foreach (BlockPos pos in deadBeds) Beds.Remove(pos);
    }

    private void ScrubNullKeys()
    {
        foreach (BlockPos key in Workstations.Keys.Where(k => k == null).ToList())
        {
            Workstations.Remove(key);
            Api.Logger.Warning("[VsVillage] Village " + Id + ": removed null-keyed Workstation entry during Init.");
        }

        foreach (BlockPos key in Beds.Keys.Where(k => k == null).ToList())
        {
            Beds.Remove(key);
            Api.Logger.Warning("[VsVillage] Village " + Id + ": removed null-keyed Bed entry during Init.");
        }

        Gatherplaces.RemoveWhere(k =>
        {
            if (k != null) return false;
            Api.Logger.Warning("[VsVillage] Village " + Id + ": removed null Gatherplace entry during Init.");
            return true;
        });

        Waypoints.RemoveWhere(k =>
        {
            if (k != null) return false;
            Api.Logger.Warning("[VsVillage] Village " + Id + ": removed null Waypoint entry during Init.");
            return true;
        });
    }

    public BlockPos FindFreeBed(long villagerId)
    {
        foreach (VillagerBed value in Beds.Values)
        {
            if (value.OwnerId == -1 || value.OwnerId == villagerId)
            {
                value.OwnerId = villagerId;
                string text = Api.World.GetEntityById(villagerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
                BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
                if (blockEntity != null && !string.IsNullOrEmpty(text))
                {
                    blockEntity.OwnerName = text;
                    blockEntity.MarkDirty();
                }
                return value.Pos;
            }
        }
        return null;
    }

    public BlockPos FindFreeWorkstation(long villagerId, EnumVillagerProfession profession)
    {
        foreach (VillagerWorkstation value in Workstations.Values)
        {
            if (value.Profession == profession && (value.OwnerId == -1 || value.OwnerId == villagerId))
            {
                value.OwnerId = villagerId;
                string text = Api.World.GetEntityById(villagerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
                BlockEntityVillagerWorkstation blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(value.Pos);
                if (blockEntity != null && !string.IsNullOrEmpty(text))
                {
                    blockEntity.OwnerName = text;
                    blockEntity.MarkDirty();
                }
                return value.Pos;
            }
        }
        return null;
    }

    public void ClearBedOwner(long villagerId)
    {
        foreach (VillagerBed value in Beds.Values)
        {
            if (value.OwnerId == villagerId)
            {
                value.OwnerId = -1L;
                BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
                if (blockEntity != null)
                {
                    blockEntity.OwnerName = null;
                    blockEntity.MarkDirty();
                }
            }
        }
    }

    public BlockPos FindRandomGatherplace()
    {
        if (Gatherplaces.Count == 0)
        {
            return null;
        }
        return Gatherplaces.ElementAt(Api.World.Rand.Next(Gatherplaces.Count));
    }

    public void RemoveVillager(long villagerId)
    {
        VillagerSaveData.Remove(villagerId);
        foreach (VillagerBed value in Beds.Values)
        {
            if (value.OwnerId == villagerId)
            {
                value.OwnerId = -1L;
                BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
                if (blockEntity != null)
                {
                    blockEntity.OwnerName = null;
                    blockEntity.MarkDirty();
                }
            }
        }
        foreach (VillagerWorkstation value2 in Workstations.Values)
        {
            if (value2.OwnerId == villagerId)
            {
                value2.OwnerId = -1L;
                // Only clear the workstation sign for the departing villager's own station.
                BlockEntityVillagerWorkstation blockEntity2 = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(value2.Pos);
                if (blockEntity2 != null)
                {
                    blockEntity2.OwnerName = null;
                    blockEntity2.MarkDirty();
                }
            }
        }
    }

    public BlockPos FindNearesWaypoint(BlockPos pos)
    {
        BlockPos blockPos = null;
        foreach (BlockPos waypoint in Waypoints)
        {
            if (blockPos == null || waypoint.ManhattanDistance(pos) < blockPos.ManhattanDistance(pos))
            {
                blockPos = waypoint;
            }
        }
        return blockPos;
    }

    public void RemoveWaypoint(BlockPos pos)
    {
        Waypoints.Remove(pos);
    }
}