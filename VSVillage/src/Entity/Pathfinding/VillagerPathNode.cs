using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace VsVillage;

public class VillagerPathNode : IEquatable<VillagerPathNode>, IComparable<VillagerPathNode>
{
	public VillagerPathNode Parent;

	public BlockPos BlockPos;

	public bool IsDoor;

	public float gCost;

	public float hCost;

	public int pathLength;

	public float fCost => gCost + hCost;

	public PathNode ToPathNode()
	{
		return new PathNode(BlockPos);
	}

	public Vec3d ToWaypoint()
	{
		return new Vec3d((double)BlockPos.X + 0.5, BlockPos.Y, (double)BlockPos.Z + 0.5);
	}

	public VillagerPathNode(BlockPos blockPos, BlockPos target, bool isDoor)
	{
		BlockPos = blockPos;
		IsDoor = isDoor;
		gCost = 0f;
		hCost = (float)Math.Sqrt(blockPos.DistanceSqTo(target.X, target.Y, target.Z));
		pathLength = 0;
		Parent = null;
	}

	public VillagerPathNode(VillagerPathNode parent, Cardinal cardinal)
	{
		Parent = parent;
		BlockPos = parent.BlockPos.AddCopy(cardinal.Normali.X, cardinal.Normali.Y, cardinal.Normali.Z);
		pathLength = parent.pathLength + 1;
		gCost = 0f;
		hCost = 0f;
		IsDoor = false;
	}

	public void Init(BlockPos target, bool isDoor)
	{
		hCost = (float)Math.Sqrt(BlockPos.DistanceSqTo(target.X, target.Y, target.Z));
		IsDoor = isDoor;
	}

	public bool Equals(VillagerPathNode other)
	{
		return BlockPos.Equals(other?.BlockPos);
	}

	public override bool Equals(object obj)
	{
		return obj is VillagerPathNode other && Equals(other);
	}

	public override int GetHashCode()
	{
		return BlockPos.GetHashCode();
	}

	public List<VillagerPathNode> RetracePath()
	{
		List<VillagerPathNode> list = new List<VillagerPathNode>(pathLength + 1);
		for (int i = 0; i <= pathLength; i++)
		{
			list.Add(null);
		}
		VillagerPathNode villagerPathNode = this;
		for (int num = pathLength; num >= 0; num--)
		{
			list[num] = villagerPathNode;
			villagerPathNode = villagerPathNode.Parent;
		}
		return list;
	}

	public int CompareTo(VillagerPathNode other)
	{
		int num = fCost.CompareTo(other.fCost);
		if (num == 0)
		{
			num = hCost.CompareTo(other.hCost);
		}
		if (num == 0)
		{
			num = BlockPos.GetHashCode().CompareTo(other.BlockPos.GetHashCode());
		}
		return num;
	}

	public float distanceTo(VillagerPathNode other)
	{
		int val = Math.Abs(BlockPos.X - other.BlockPos.X);
		int num = Math.Abs(BlockPos.Y - other.BlockPos.Y);
		int val2 = Math.Abs(BlockPos.Z - other.BlockPos.Z);
		int num2 = Math.Min(val, val2);
		int num3 = Math.Max(val, val2);
		return (float)(1.414 * (double)num2 + (double)(num3 - num2) + (double)num);
	}

	public void SetGCostFromParent(VillagerPathNode parent, float extraCost)
	{
		if (parent != null)
		{
			gCost = parent.gCost + parent.distanceTo(this) + extraCost;
		}
		else
		{
			gCost = extraCost;
		}
	}
}
