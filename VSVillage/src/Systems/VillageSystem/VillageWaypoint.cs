using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillageWaypoint
{
	[ProtoMember(1)]
	public BlockPos Pos;

	[ProtoMember(2)]
	public Dictionary<BlockPos, int> _Neighbours = new Dictionary<BlockPos, int>();

	[ProtoMember(3)]
	public Dictionary<BlockPos, VillageWaypointPath> _ReachableNodes = new Dictionary<BlockPos, VillageWaypointPath>();

	public Dictionary<VillageWaypoint, int> Neighbours = new Dictionary<VillageWaypoint, int>();

	public Dictionary<VillageWaypoint, VillageWaypointPath> ReachableNodes = new Dictionary<VillageWaypoint, VillageWaypointPath>();

	public void SetNeighbour(VillageWaypoint newNeighbour, int distance)
	{
		Neighbours[newNeighbour] = distance;
		_Neighbours[newNeighbour.Pos] = distance;
	}

	public void RemoveNeighbour(VillageWaypoint waypoint)
	{
		Neighbours.Remove(waypoint);
		_Neighbours.Remove(waypoint.Pos);
	}

	public void SetReachableNode(VillageWaypoint waypoint, VillageWaypointPath path)
	{
		ReachableNodes[waypoint] = path;
		_ReachableNodes[waypoint.Pos] = path;
	}

	public void RemoveReachableNode(VillageWaypoint waypoint)
	{
		ReachableNodes.Remove(waypoint);
		_ReachableNodes.Remove(waypoint.Pos);
	}

	public List<VillageWaypoint> FindPath(VillageWaypoint target, int maxSearchDepth)
	{
		VillageWaypoint villageWaypoint = this;
		List<VillageWaypoint> list = new List<VillageWaypoint> { villageWaypoint };
		int num = 0;
		while (!villageWaypoint.Equals(target) && num++ < maxSearchDepth)
		{
			VillageWaypoint nextWaypoint = villageWaypoint.GetNextWaypoint(target);
			if (nextWaypoint == null)
			{
				return list;
			}
			list.Add(nextWaypoint);
			villageWaypoint = nextWaypoint;
		}
		return list;
	}

	private VillageWaypoint GetNextWaypoint(VillageWaypoint target)
	{
		if (Neighbours.ContainsKey(target))
		{
			return target;
		}
		ReachableNodes.TryGetValue(target, out var value);
		return value?.NextWaypoint;
	}

	public void UpdateReachableNodes()
	{
		foreach (KeyValuePair<VillageWaypoint, int> neighbour in Neighbours)
		{
			foreach (KeyValuePair<VillageWaypoint, int> neighbour2 in neighbour.Key.Neighbours)
			{
				ReachableNodes.TryGetValue(neighbour2.Key, out var value);
				if (neighbour2.Key != this && (value == null || value.Distance > neighbour2.Value + neighbour.Value))
				{
					SetReachableNode(neighbour2.Key, new VillageWaypointPath
					{
						NextWaypoint = neighbour.Key,
						_NextWaypoint = neighbour.Key.Pos,
						Distance = neighbour.Value + neighbour2.Value
					});
				}
			}
			foreach (KeyValuePair<VillageWaypoint, VillageWaypointPath> reachableNode in neighbour.Key.ReachableNodes)
			{
				ReachableNodes.TryGetValue(reachableNode.Key, out var value2);
				if (reachableNode.Key != this && (value2 == null || value2.Distance > reachableNode.Value.Distance + neighbour.Value))
				{
					SetReachableNode(reachableNode.Key, new VillageWaypointPath
					{
						NextWaypoint = neighbour.Key,
						_NextWaypoint = neighbour.Key.Pos,
						Distance = neighbour.Value + reachableNode.Value.Distance
					});
				}
			}
		}
	}

	public VillageWaypoint FindNearestReachableWaypoint(BlockPos pos)
	{
		if (pos == null)
		{
			return null;
		}
		VillageWaypoint villageWaypoint = null;
		foreach (VillageWaypoint key in Neighbours.Keys)
		{
			if (villageWaypoint == null || key.Pos.ManhattenDistance(pos) < villageWaypoint.Pos.ManhattenDistance(pos))
			{
				villageWaypoint = key;
			}
		}
		foreach (VillageWaypoint key2 in ReachableNodes.Keys)
		{
			if (villageWaypoint == null || key2.Pos.ManhattenDistance(pos) < villageWaypoint.Pos.ManhattenDistance(pos))
			{
				villageWaypoint = key2;
			}
		}
		return villageWaypoint;
	}

	public override bool Equals(object obj)
	{
		return object.Equals(Pos, (obj as VillageWaypoint)?.Pos);
	}

	public override int GetHashCode()
	{
		return Pos?.GetHashCode() ?? (-1);
	}
}
