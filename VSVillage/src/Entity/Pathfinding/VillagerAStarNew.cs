using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class VillagerAStarNew
{
	public List<string> doorCodes;

	public List<string> climbableCodes;

	public List<string> steppableCodes;

	public ICachingBlockAccessor blockAccessor;

	public const float stepHeight = 1.2f;

	public const int maxFallHeight = 5;

	protected CollisionTester collTester;

	protected Cuboidf entityCollBox = new Cuboidf(-0.4f, 0f, -0.4f, 0.4f, 1.8f, 0.4f);

	protected double centerOffsetX = 0.5;

	protected double centerOffsetZ = 0.5;

	protected Vec3d tmpVec;

	protected BlockPos tmpPos;

	public List<string> traversableCodes;

	public VillagerAStarNew(ICachingBlockAccessor blockAccessor)
	{
		this.blockAccessor = blockAccessor;
		collTester = new CollisionTester();
		traversableCodes = new List<string> { "door", "gate", "ladder", "multiblock" };
		doorCodes = new List<string> { "door", "gate", "multiblock" };
		climbableCodes = new List<string> { "ladder" };
		steppableCodes = new List<string> { "stair", "path", "bed-", "farmland", "furrowedland", "slab", "carpet" };
		tmpVec = new Vec3d();
		tmpPos = new BlockPos(0);
	}

	public List<VillagerPathNode> FindPath(BlockPos start, BlockPos end, int searchDepth = 8000)
	{
		List<VillagerPathNode> result;
		if (start == null || end == null)
		{
			result = null;
		}
		else
		{
			centerOffsetX = 0.3 + new Random().NextDouble() * 0.4;
			centerOffsetZ = 0.3 + new Random().NextDouble() * 0.4;
			SortedSet<VillagerPathNode> sortedSet = new SortedSet<VillagerPathNode>();
			HashSet<VillagerPathNode> hashSet = new HashSet<VillagerPathNode>();
			VillagerPathNode item = new VillagerPathNode(start, end, isDoor(blockAccessor.GetBlock(start)));
			sortedSet.Add(item);
			int num = 0;
			while (num < searchDepth && sortedSet.Count > 0)
			{
				num++;
				VillagerPathNode min = sortedSet.Min;
				sortedSet.Remove(min);
				hashSet.Add(min);
				if (min.BlockPos.Equals(end))
				{
					return min.RetracePath();
				}
				Cardinal[] aLL = Cardinal.ALL;
				foreach (Cardinal cardinal in aLL)
				{
					VillagerPathNode villagerPathNode = new VillagerPathNode(min, cardinal);
					if (hashSet.Contains(villagerPathNode))
					{
						continue;
					}
					float extraCost = 0f;
					if (!traversable(villagerPathNode, end, cardinal, ref extraCost))
					{
						continue;
					}
					villagerPathNode.SetGCostFromParent(min, extraCost);
					villagerPathNode.Init(end, isDoor(blockAccessor.GetBlock(villagerPathNode.BlockPos)));
					VillagerPathNode villagerPathNode2 = null;
					foreach (VillagerPathNode item2 in sortedSet)
					{
						if (item2.BlockPos.Equals(villagerPathNode.BlockPos))
						{
							villagerPathNode2 = item2;
							break;
						}
					}
					if (villagerPathNode2 != null)
					{
						if (villagerPathNode.gCost < villagerPathNode2.gCost - 0.0001f)
						{
							sortedSet.Remove(villagerPathNode2);
							sortedSet.Add(villagerPathNode);
						}
					}
					else
					{
						sortedSet.Add(villagerPathNode);
					}
				}
			}
			result = null;
		}
		return result;
	}

	private bool isDoor(Block block)
	{
		bool result;
		if (block == null || block.Code == null)
		{
			result = false;
		}
		else
		{
			foreach (string doorCode in doorCodes)
			{
				if (block.Code.Path.Contains(doorCode))
				{
					return true;
				}
			}
			result = false;
		}
		return result;
	}

	public BlockPos GetStartPos(Vec3d startPos)
	{
		BlockPos asBlockPos = startPos.AsBlockPos;
		tmpVec.Set(startPos.X, startPos.Y, startPos.Z);
		BlockPos result;
		if (!collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
		{
			result = asBlockPos;
		}
		else
		{
			double num = startPos.X - Math.Truncate(startPos.X);
			double num2 = startPos.Z - Math.Truncate(startPos.Z);
			BlockPos[] array = new BlockPos[4]
			{
				asBlockPos.NorthCopy(),
				asBlockPos.SouthCopy(),
				asBlockPos.WestCopy(),
				asBlockPos.EastCopy()
			};
			BlockPos[] array2 = array;
			foreach (BlockPos blockPos in array2)
			{
				tmpVec.Set((double)blockPos.X + 0.5, blockPos.Y, (double)blockPos.Z + 0.5);
				if (!collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
				{
					return blockPos;
				}
			}
			result = startPos.AsBlockPos;
		}
		return result;
	}

	protected virtual bool traversable(VillagerPathNode node, BlockPos target, Cardinal fromDir, ref float extraCost)
	{
		bool result;
		if (target.Equals(node.BlockPos))
		{
			result = true;
		}
		else
		{
			tmpVec.Set((double)node.BlockPos.X + centerOffsetX, node.BlockPos.Y, (double)node.BlockPos.Z + centerOffsetZ);
			tmpPos.Set(node.BlockPos.X, node.BlockPos.Y, node.BlockPos.Z);
			Block nodeBlock = blockAccessor.GetBlock(tmpPos);
			bool forceTraversable = nodeBlock != null && nodeBlock.Code != null &&
				traversableCodes.Exists(c => nodeBlock.Code.Path.Contains(c));
			if (forceTraversable || !collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
			{
				int num = 0;
				while (num <= 5)
				{
					tmpPos.Set(node.BlockPos.X, node.BlockPos.Y - 1, node.BlockPos.Z);
					Block block = blockAccessor.GetBlock(tmpPos);
					if (!block.CanStep)
					{
						return false;
					}
					float traversalCost = block.GetTraversalCost(tmpPos, EnumAICreatureType.Humanoid);
					if (traversalCost > 10000f)
					{
						return false;
					}
					Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, tmpPos);
					if (collisionBoxes != null && collisionBoxes.Length != 0)
					{
						extraCost += traversalCost;
						if (fromDir.IsDiagnoal)
						{
							tmpVec.Add((0f - (float)fromDir.Normali.X) / 2f, 0.0, (0f - (float)fromDir.Normali.Z) / 2f);
							if (collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
							{
								return false;
							}
						}
						tmpPos.Set(node.BlockPos.X, node.BlockPos.Y, node.BlockPos.Z);
						Block block2 = blockAccessor.GetBlock(tmpPos, 2);
						float traversalCost2 = block2.GetTraversalCost(tmpPos, EnumAICreatureType.Humanoid);
						if (traversalCost2 > 10000f)
						{
							return false;
						}
						extraCost += traversalCost2;
						// Penalize diagonal approach to gates/doors — forces straight-through traversal,
						// preventing entity width from clipping adjacent fence posts.
						if (forceTraversable && fromDir.IsDiagnoal)
						{
							extraCost += 8f;
						}
						return true;
					}
					tmpVec.Y -= 1.0;
					if (collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
					{
						return false;
					}
					num++;
					node.BlockPos.Y--;
				}
				result = false;
			}
			else
			{
				tmpPos.Set(node.BlockPos.X, node.BlockPos.Y, node.BlockPos.Z);
				Block block3 = blockAccessor.GetBlock(tmpPos);
				if (!block3.CanStep)
				{
					result = false;
				}
				else if (block3.Code != null &&
					block3.Code.Path.Contains("fence") &&
					!block3.Code.Path.Contains("gate") &&
					!block3.Code.Path.Contains("door"))
				{
					// Fence posts have narrow collision boxes — the step-up check clears their
					// tops at Y+1.7 even though the entity can't actually stand on them.
					// Block traversal explicitly so pathfinder routes around fence lines.
					result = false;
				}
				else
				{
					float traversalCost3 = block3.GetTraversalCost(tmpPos, EnumAICreatureType.Humanoid);
					if (traversalCost3 > 10000f)
					{
						result = false;
					}
					else
					{
						extraCost += traversalCost3;
						float num2 = 0f;
						Cuboidf[] collisionBoxes2 = block3.GetCollisionBoxes(blockAccessor, tmpPos);
						if (collisionBoxes2 != null && collisionBoxes2.Length != 0)
						{
							Cuboidf[] array = collisionBoxes2;
							foreach (Cuboidf cuboidf in array)
							{
								if (cuboidf.Y2 > num2)
								{
									num2 = cuboidf.Y2;
								}
							}
						}
						// Only allow stepping up onto blocks shorter than stepHeight (1.2).
						// Taller blocks (workstations at 2.0, walls, etc.) must be routed around,
						// not climbed — prevents pathfinder from routing up and over obstacles.
						if (num2 > stepHeight)
						{
							return false;
						}
						tmpVec.Set((double)node.BlockPos.X + centerOffsetX, (double)node.BlockPos.Y + 1.2000000476837158 + (double)num2 - 1.0, (double)node.BlockPos.Z + centerOffsetZ);
						if (!collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
						{
							if (fromDir.IsDiagnoal && collisionBoxes2 != null && collisionBoxes2.Length != 0)
							{
								tmpVec.Add((0f - (float)fromDir.Normali.X) / 2f, 0.0, (0f - (float)fromDir.Normali.Z) / 2f);
								if (collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
								{
									return false;
								}
							}
							node.BlockPos.Y += (int)(1f + num2 - 1f);
							result = true;
						}
						else
						{
							result = false;
						}
					}
				}
			}
		}
		return result;
	}

	public void SetEntityCollisionBox(Cuboidf collBox)
	{
		entityCollBox = collBox;
	}

	public void SetEntityCollisionBox(EntityAgent entity)
	{
		if (entity != null && entity.CollisionBox != null)
		{
			entityCollBox = entity.CollisionBox;
		}
		else
		{
			entityCollBox = new Cuboidf(-0.4f, 0f, -0.4f, 0.4f, 1.8f, 0.4f);
		}
	}
}
