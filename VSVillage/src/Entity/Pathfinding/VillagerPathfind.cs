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
		ICachingBlockAccessor cachingBlockAccessor = sapi.World.GetCachingBlockAccessor(synchronize: true, relight: true);
		villagerAStar = new VillagerAStarNew(cachingBlockAccessor);
		waypointAStar = new WaypointAStar(cachingBlockAccessor);
	}

	public BlockPos GetStartPos(Vec3d startPos)
	{
		return villagerAStar.GetStartPos(startPos);
	}

	public List<VillagerPathNode> FindPath(BlockPos start, BlockPos end, Village village)
	{
		return villagerAStar.FindPath(start, end, 5000);
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
		List<Vec3d> list = new List<Vec3d>(path.Count + 1);
		for (int i = 1; i < path.Count; i++)
		{
			list.Add(path[i].ToWaypoint());
		}
		return list;
	}
}
