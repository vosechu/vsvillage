using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsVillage;

public class VillagerPathfind
{
	private VillagerAStarNew villagerAStar;

	private WaypointAStar waypointAStar;

	public VillagerPathfind(ICoreServerAPI sapi)
	{
		// relight:false because pathfinding only reads blocks; the lighting machinery was dead overhead per villager.
		ICachingBlockAccessor cachingBlockAccessor = sapi.World.GetCachingBlockAccessor(synchronize: true, relight: false);
		villagerAStar = new VillagerAStarNew(cachingBlockAccessor, sapi.World);
		waypointAStar = new WaypointAStar(cachingBlockAccessor, sapi.World);
	}

	public BlockPos GetStartPos(Vec3d startPos)
	{
		return villagerAStar.GetStartPos(startPos);
	}

	public List<VillagerPathNode> FindPath(BlockPos start, BlockPos end, Village village)
	{
		// Defers to VillagerAStarNew.FindPath default (12000). The previous 5000 cap silently failed long routes.
		return villagerAStar.FindPath(start, end);
	}

	public List<Vec3d> FindPathAsWaypoints(BlockPos start, BlockPos end, Village village)
	{
		List<VillagerPathNode> list = FindPath(start, end, village);
		if (list != null)
		{
			return ToWaypoints(list);
		}
		return null;
	}

	public List<Vec3d> ToWaypoints(List<VillagerPathNode> path)
	{
		// path.Count == 1 means start equals end: nothing to walk to. Return null so callers branch on "already there".
		if (path == null || path.Count <= 1) return null;
		List<Vec3d> list = new List<Vec3d>(path.Count);
		for (int i = 1; i < path.Count; i++)
		{
			list.Add(path[i].ToWaypoint());
		}
		return list;
	}
}
