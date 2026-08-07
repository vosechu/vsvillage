using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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

    [ProtoMember(9)]
    public List<BlockPos> ConstructionQueue = new List<BlockPos>();

    [ProtoMember(10)]
    public Dictionary<BlockPos, VillagerGuardPost> GuardPosts = new Dictionary<BlockPos, VillagerGuardPost>();

    // Generated footprint, max-exclusive like the Cuboidi VillageGrid.getEnd describes. Non-null
    // marks a worldgen village and is the exact land-claim box; player-founded villages leave it null.
    [ProtoMember(11)]
    public BlockPos ClaimStart;

    [ProtoMember(12)]
    public BlockPos ClaimEnd;

    public ICoreAPI Api;

    // Runtime-only flag - not persisted. True while a Gather is active.
    public bool IsGatherActive;

    // Callback ID for the gather auto-clear timer. -1 when no timer is running.
    public long GatherCallbackId = -1;

    public string Id => "village-" + Pos.ToString();

    private long _centerYCacheTime = -1;
    private int _centerYCache;

    // Worldgen villages stored Pos.Y near 0, ~90 below real terrain. Pos can't be fixed in place
    // since Id derives from it, so derive the true centre Y from POIs (fallback: terrain height).
    public int EffectiveCenterY()
    {
        if (Pos == null) return 0;
        long now = Api?.World?.ElapsedMilliseconds ?? 0;
        if (_centerYCacheTime >= 0 && now - _centerYCacheTime < 5000) return _centerYCache;

        int sum = 0, count = 0;
        foreach (BlockPos p in Workstations.Keys) { if (p != null) { sum += p.Y; count++; } }
        foreach (BlockPos p in Beds.Keys) { if (p != null) { sum += p.Y; count++; } }

        if (count > 0)
        {
            _centerYCache = sum / count;
            _centerYCacheTime = now;
            return _centerYCache;
        }
        if (Api?.World != null)
        {
            _centerYCache = Api.World.BlockAccessor.GetTerrainMapheightAt(Pos);
            _centerYCacheTime = now;
            return _centerYCache;
        }
        return Pos.Y;
    }

    public Vec3d EffectiveCenter() => new Vec3d(Pos.X, EffectiveCenterY(), Pos.Z);

    // Hot path. One list alloc instead of the prior chain (ToList -> ConvertAll -> Where -> ToList = 3 lists + enumerator).
    public List<EntityBehaviorVillager> Villagers
    {
        get
        {
            var list = new List<EntityBehaviorVillager>(VillagerSaveData.Count);
            foreach (VillagerData data in VillagerSaveData.Values)
            {
                var beh = Api.World.GetEntityById(data.Id)?.GetBehavior<EntityBehaviorVillager>();
                if (beh != null) list.Add(beh);
            }
            return list;
        }
    }

    public void Init(ICoreAPI api)
    {
        Api = api;
        ScrubNullKeys();
        if (api.Side == EnumAppSide.Server)
        {
            // Delay ghost sweep until initial chunks load. Scales with MaxChunkRadius, 15s default.
            int delayMs = 15000;
            try
            {
                var cfg = (api as Vintagestory.API.Server.ICoreServerAPI)?.Server?.Config;
                if (cfg != null) delayMs = Math.Max(8000, cfg.MaxChunkRadius * 1500);
            }
            catch { }
            api.World.RegisterCallback(delegate { ScrubGhostStructures(); BuildWaypointGraph(); }, delayMs);
        }
    }

    // Removes workstation/bed entries whose block entity no longer exists in the
    // world. Only acts on chunks that are currently loaded - safe to call any time.
    internal void ScrubGhostStructures()
    {
        if (Api == null) return;
        IBlockAccessor ba = Api.World.BlockAccessor;

        List<BlockPos> deadWorkstations = new List<BlockPos>();
        foreach (BlockPos pos in Workstations.Keys)
        {
            if (pos == null) continue;
            // Skip positions in unloaded chunks - null BE is ambiguous there.
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

        List<BlockPos> deadGuardPosts = new List<BlockPos>();
        foreach (BlockPos pos in GuardPosts.Keys)
        {
            if (pos == null) continue;
            if (!ba.IsValidPos(pos)) continue;
            if (ba.GetChunkAtBlockPos(pos) == null) continue;
            if (ba.GetBlockEntity<BlockEntityVillagerGuardPost>(pos) == null)
            {
                deadGuardPosts.Add(pos);
                Api.Logger.Warning("[VsVillage] Village " + Id + ": removing ghost guard post at " + pos);
            }
        }
        foreach (BlockPos pos in deadGuardPosts) GuardPosts.Remove(pos);
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

        foreach (BlockPos key in GuardPosts.Keys.Where(k => k == null).ToList())
        {
            GuardPosts.Remove(key);
            Api.Logger.Warning("[VsVillage] Village " + Id + ": removed null-keyed GuardPost entry during Init.");
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

    // Axis-aligned box, matching GetVillage and the POI registration checks rather than a
    // radial test, so "inside the village" means the same thing everywhere.
    public bool IsInside(double x, double z)
    {
        if (Pos == null) return false;
        return Math.Abs(Pos.X - x) <= Radius && Math.Abs(Pos.Z - z) <= Radius;
    }

    // Picks the NEAREST free station, not the first in dictionary order. First-hit could hand a
    // villager a station on the far side of the village, beyond what one A* budget can reach,
    // which left them grinding an impossible path instead of working.
    public BlockPos FindFreeWorkstation(long villagerId, EnumVillagerProfession profession)
    {
        Entity villager = Api.World.GetEntityById(villagerId);
        Vec3d from = villager?.Pos?.XYZ;

        VillagerWorkstation best = null;
        double bestSq = double.MaxValue;
        foreach (VillagerWorkstation value in Workstations.Values)
        {
            if (value.Profession != profession || value.Pos == null) continue;
            if (value.OwnerId != -1 && value.OwnerId != villagerId) continue;

            if (from == null) { best = value; break; }
            double sq = value.Pos.ToVec3d().SquareDistanceTo(from);
            if (sq < bestSq) { bestSq = sq; best = value; }
        }
        if (best == null) return null;

        best.OwnerId = villagerId;
        string text = villager?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
        BlockEntityVillagerWorkstation blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(best.Pos);
        if (blockEntity != null && !string.IsNullOrEmpty(text))
        {
            blockEntity.OwnerName = text;
            blockEntity.MarkDirty();
        }
        return best.Pos;
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

    // Village-side only. The owning entity's own GuardPost/GuardShift attributes are cleared by the
    // caller when it is loaded, and self-heal via GuardDuty.ValidateAssignment when it is not.
    public void ClearGuardOwner(long villagerId)
    {
        foreach (VillagerGuardPost post in GuardPosts.Values)
        {
            if (post.DayOwnerId == villagerId) post.DayOwnerId = -1L;
            if (post.NightOwnerId == villagerId) post.NightOwnerId = -1L;
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
        ClearGuardOwner(villagerId);
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
        if (Waypoints.Remove(pos)) BuildWaypointGraph();
    }

    public void EnqueueConstruction(BlockPos markerPos)
    {
        if (markerPos == null) return;
        if (ConstructionQueue.Contains(markerPos)) return;
        ConstructionQueue.Add(markerPos);
    }

    public void DequeueConstruction(BlockPos markerPos)
    {
        if (markerPos == null) return;
        ConstructionQueue.Remove(markerPos);
    }

    public BlockPos GetActiveConstruction()
    {
        if (ConstructionQueue.Count == 0) return null;
        return ConstructionQueue[0];
    }

    // Transient - not persisted. Rebuilt from Waypoints on load and on any add/remove.
    public Dictionary<BlockPos, VillageWaypoint> WaypointGraph = new Dictionary<BlockPos, VillageWaypoint>();

    // Rebuilds the waypoint graph from the current Waypoints set.
    // Runs short A* legs between nearby pairs to establish edges, then propagates
    // transitive reachability. Called on load (delayed) and whenever a waypoint is placed or removed.
    public void BuildWaypointGraph()
    {
        WaypointGraph.Clear();
        if (Api == null || Waypoints.Count < 2) return;

        int connectionRange = Math.Max(50, Radius / 4);

        foreach (BlockPos pos in Waypoints)
            WaypointGraph[pos] = new VillageWaypoint { Pos = pos.Copy() };

        ICachingBlockAccessor bac = Api.World.GetCachingBlockAccessor(synchronize: true, relight: false);
        var edgeTester = new WaypointAStar(bac, Api.World);

        var nodes = new List<BlockPos>(WaypointGraph.Keys);
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                BlockPos a = nodes[i];
                BlockPos b = nodes[j];
                if (a.ManhattanDistance(b) > connectionRange) continue;

                bac.Begin();
                var path = edgeTester.FindPath(a, b, searchDepth: 3000);
                bac.Commit();

                if (path != null && path.Count > 1)
                {
                    int dist = path.Count;
                    WaypointGraph[a].SetNeighbour(WaypointGraph[b], dist);
                    WaypointGraph[b].SetNeighbour(WaypointGraph[a], dist);
                }
            }
        }

        // Bellman-Ford style propagation - repeat until reachability is fully transitive.
        int passes = WaypointGraph.Count;
        for (int pass = 0; pass < passes; pass++)
            foreach (VillageWaypoint node in WaypointGraph.Values)
                node.UpdateReachableNodes();
    }
}