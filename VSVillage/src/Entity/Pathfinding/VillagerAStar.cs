using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace VsVillage;

public class VillagerAStar
{
	protected ICoreAPI api;

	protected ICachingBlockAccessor blockAccess;

	public int NodesChecked;

	public const double centerOffsetX = 0.5;

	public const double centerOffsetZ = 0.5;

	public PathNodeSet openSet = new PathNodeSet();

	public HashSet<PathNode> closedSet = new HashSet<PathNode>();

	public List<string> traversableCodes { get; set; } = new List<string> { "door", "gate", "ladder", "multiblock" };

	public List<string> climbableCodes { get; set; } = new List<string> { "ladder" };

	public List<string> steppableCodes { get; set; } = new List<string> { "stair", "path", "bed-", "farmland", "slab" };

	public VillagerAStar(ICoreAPI api)
	{
		this.api = api;
		blockAccess = api.World.GetCachingBlockAccessor(synchronize: true, relight: true);
	}

	public List<PathNode> FindPath(BlockPos start, BlockPos end, int maxFallHeight, float stepHeight, int searchDepth = 999, bool allowReachAlmost = true)
	{
		blockAccess.Begin();
		NodesChecked = 0;
		PathNode pathNode = new PathNode(start);
		PathNode pathNode2 = new PathNode(end);
		openSet.Clear();
		closedSet.Clear();
		openSet.Add(pathNode);
		while (openSet.Count > 0)
		{
			if (NodesChecked++ > searchDepth)
			{
				return null;
			}
			PathNode pathNode3 = openSet.RemoveNearest();
			closedSet.Add(pathNode3);
			if (pathNode3 == pathNode2 || (allowReachAlmost && Math.Abs(pathNode3.X - pathNode2.X) <= 1 && Math.Abs(pathNode3.Z - pathNode2.Z) <= 1 && Math.Abs(pathNode3.Y - pathNode2.Y) <= 2))
			{
				return retracePath(pathNode, pathNode3);
			}
			foreach (PathNode item in findValidNeighbourNodes(pathNode3, pathNode2, stepHeight, maxFallHeight))
			{
				float num = 0f;
				PathNode pathNode4 = openSet.TryFindValue(item);
				if ((object)pathNode4 != null)
				{
					float num2 = pathNode3.gCost + pathNode3.distanceTo(item);
					if (pathNode4.gCost > num2 + 0.0001f && traversable(item, pathNode2, stepHeight, maxFallHeight) && pathNode4.gCost > num2 + num + 0.0001f)
					{
						UpdateNode(pathNode3, pathNode4, num);
					}
				}
				else if (!closedSet.Contains(item) && traversable(item, pathNode2, stepHeight, maxFallHeight))
				{
					UpdateNode(pathNode3, item, num);
					item.hCost = item.distanceTo(pathNode2);
					openSet.Add(item);
				}
			}
		}
		return null;
	}

	protected virtual IEnumerable<PathNode> findValidNeighbourNodes(PathNode nearestNode, PathNode targetNode, float stepHeight, int maxFallHeight)
	{
		Block current = blockAccess.GetBlock(new BlockPos(nearestNode.X, nearestNode.Y, nearestNode.Z, 0));
		if (climbableCodes.Exists((string code) => current.Code.Path.Contains(code)))
		{
			Cardinal card;
			List<PathNode> list;
			switch (current.Variant["side"])
			{
			case "east":
				card = Cardinal.East;
				list = new List<PathNode>(new PathNode[3]
				{
					new PathNode(nearestNode, Cardinal.North),
					new PathNode(nearestNode, Cardinal.South),
					new PathNode(nearestNode, Cardinal.West)
				});
				break;
			case "west":
				card = Cardinal.West;
				list = new List<PathNode>(new PathNode[3]
				{
					new PathNode(nearestNode, Cardinal.North),
					new PathNode(nearestNode, Cardinal.East),
					new PathNode(nearestNode, Cardinal.South)
				});
				break;
			case "south":
				card = Cardinal.South;
				list = new List<PathNode>(new PathNode[3]
				{
					new PathNode(nearestNode, Cardinal.North),
					new PathNode(nearestNode, Cardinal.East),
					new PathNode(nearestNode, Cardinal.West)
				});
				break;
			default:
				card = Cardinal.North;
				list = new List<PathNode>(new PathNode[3]
				{
					new PathNode(nearestNode, Cardinal.East),
					new PathNode(nearestNode, Cardinal.South),
					new PathNode(nearestNode, Cardinal.West)
				});
				break;
			}
			int i;
			for (i = 1; traversableCodes.Exists((string code) => blockAccess.GetBlock(new BlockPos(nearestNode.X, nearestNode.Y + i, nearestNode.Z, 0)).Code.Path.Contains(code)); i++)
			{
			}
			PathNode pathNode = new PathNode(nearestNode, card);
			pathNode.Y += i;
			list.Add(pathNode);
			return list;
		}
		return new PathNode[4]
		{
			new PathNode(nearestNode, Cardinal.North),
			new PathNode(nearestNode, Cardinal.East),
			new PathNode(nearestNode, Cardinal.South),
			new PathNode(nearestNode, Cardinal.West)
		};
	}

	private void UpdateNode(PathNode nearestNode, PathNode neighbourNode, float extraCost)
	{
		neighbourNode.gCost = nearestNode.gCost + nearestNode.distanceTo(neighbourNode) + extraCost;
		neighbourNode.Parent = nearestNode;
		neighbourNode.pathLength = nearestNode.pathLength + 1;
	}

	protected virtual bool traversable(PathNode node, PathNode target, float stepHeight, int maxFallHeight)
	{
		if (target.X == node.X && target.Z == node.Z && target.Y == node.Y)
		{
			return true;
		}
		if (traversable(blockAccess.GetBlock(new BlockPos(node.X, node.Y, node.Z, 0))) && traversable(blockAccess.GetBlock(new BlockPos(node.X, node.Y + 1, node.Z, 0))))
		{
			while (0 <= maxFallHeight)
			{
				Block block = blockAccess.GetBlock(new BlockPos(node.X, node.Y - 1, node.Z, 0));
				if (canStep(block))
				{
					return true;
				}
				if (!traversable(block))
				{
					return false;
				}
				node.Y--;
				maxFallHeight--;
			}
			while (climbableCodes.Exists((string code) => blockAccess.GetBlock(new BlockPos(node.X, node.Y, node.Z, 0)).Code.Path.Contains(code)))
			{
				Block block2 = blockAccess.GetBlock(new BlockPos(node.X, node.Y - 1, node.Z, 0));
				if (canStep(block2))
				{
					return true;
				}
				node.Y--;
			}
		}
		else
		{
			while (1f < stepHeight)
			{
				node.Y++;
				if (canStep(blockAccess.GetBlock(new BlockPos(node.X, node.Y - 1, node.Z, 0))) && traversable(blockAccess.GetBlock(new BlockPos(node.X, node.Y, node.Z, 0))) && traversable(blockAccess.GetBlock(new BlockPos(node.X, node.Y + 1, node.Z, 0))))
				{
					return true;
				}
				stepHeight -= 1f;
			}
		}
		return false;
	}

	public BlockPos GetStartPos(Vec3d startPos)
	{
		BlockPos asBlockPos = startPos.AsBlockPos;
		Block block = blockAccess.GetBlock(asBlockPos);
		if (traversable(block))
		{
			return asBlockPos;
		}
		if (getDecimalPart(startPos.Z) < 0.5 && traversable(blockAccess.GetBlock(asBlockPos.NorthCopy())))
		{
			return asBlockPos.NorthCopy();
		}
		if (getDecimalPart(startPos.Z) > 0.5 && traversable(blockAccess.GetBlock(asBlockPos.SouthCopy())))
		{
			return asBlockPos.SouthCopy();
		}
		if (getDecimalPart(startPos.X) < 0.5 && traversable(blockAccess.GetBlock(asBlockPos.West())))
		{
			return asBlockPos;
		}
		if (getDecimalPart(startPos.X) > 0.5 && traversable(blockAccess.GetBlock(asBlockPos.East())))
		{
			return asBlockPos;
		}
		if (getDecimalPart(startPos.Z) < 0.5 && traversable(blockAccess.GetBlock(asBlockPos.NorthCopy())))
		{
			return asBlockPos.NorthCopy();
		}
		if (getDecimalPart(startPos.Z) > 0.5 && traversable(blockAccess.GetBlock(asBlockPos.SouthCopy())))
		{
			return asBlockPos.SouthCopy();
		}
		return startPos.AsBlockPos;
	}

	private double getDecimalPart(double number)
	{
		return number - Math.Truncate(number);
	}

	protected virtual bool canStep(Block belowBlock)
	{
		if (!belowBlock.SideSolid[BlockFacing.UP.Index])
		{
			return steppableCodes.Exists((string code) => belowBlock.Code.Path.Contains(code));
		}
		return true;
	}

	protected virtual bool traversable(Block block)
	{
		if (block.CollisionBoxes != null && block.CollisionBoxes.Length != 0)
		{
			return traversableCodes.Exists((string code) => block.Code.Path.Contains(code));
		}
		return true;
	}

	private List<PathNode> retracePath(PathNode startNode, PathNode endNode)
	{
		int pathLength = endNode.pathLength;
		List<PathNode> list = new List<PathNode>(pathLength + 1);
		for (int i = 0; i < pathLength + 1; i++)
		{
			list.Add(null);
		}
		PathNode pathNode = endNode;
		for (int num = pathLength; num >= 0; num--)
		{
			list[num] = pathNode;
			pathNode = pathNode.Parent;
		}
		return list;
	}
}
