using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class VillagerAStarNew
{
	public List<string> doorCodes;

	public List<string> climbableCodes;

	public List<string> steppableCodes;

	public ICachingBlockAccessor blockAccessor;

	// Held so traversable() can query the reinforcement system for door-lock state.
	public IWorldAccessor world;

	// Cached at construction so the per-door lock check skips the ModLoader.GetModSystem lookup.
	private ModSystemBlockReinforcement reinforcement;

	// Pathfinding entity, used to self-exclude from the villager/player occupancy scan. Null = no avoidance.
	public Vintagestory.API.Common.Entities.Entity owner;

	// Set of nearby villager + player foot positions, populated at the start of each FindPath when owner is set.
	// These cells are treated as impassable (same shape as locked doors) so the villager routes around them.
	private HashSet<BlockPos> occupiedCells;

	// One-shot flag: when set, the next FindPath skips crowd avoidance entirely. The flag clears
	// itself after that call. Used by AiTaskGotoAndInteract as the "walk through villagers" fallback.
	public bool IgnoreCrowdOnce;

	public const float stepHeight = 1.2f;

	// Capped at 2 so villagers don't ragdoll off 3-5 block ledges. The traversable()
	// loop reads this constant directly; bump it here to widen drops in the future.
	public const int maxFallHeight = 2;

	// Extra A* cost per node at foot-level liquid. Picks dry detours over wading when one exists.
	public const float LiquidPathPenalty = 150f;

	// Extra A* cost per node at head-level liquid. Stacks with foot-level so fully-submerged swimming costs ~2x wading.
	public const float SubmergedPathPenalty = 100f;

	protected CollisionTester collTester;

	protected Cuboidf entityCollBox = new Cuboidf(-0.4f, 0f, -0.4f, 0.4f, 1.8f, 0.4f);

	protected double centerOffsetX = 0.5;

	protected double centerOffsetZ = 0.5;

	protected Vec3d tmpVec;

	protected BlockPos tmpPos;

	public List<string> traversableCodes;

	// Single shared Random per pathfinder instance - avoids two new Random() calls per
	// FindPath invocation which could produce correlated seeds when called in the same ms.
	private readonly Random _rng = new Random();

	public VillagerAStarNew(ICachingBlockAccessor blockAccessor, IWorldAccessor world, Vintagestory.API.Common.Entities.Entity owner = null)
	{
		this.blockAccessor = blockAccessor;
		this.world = world;
		this.owner = owner;
		reinforcement = world?.Api?.ModLoader?.GetModSystem<ModSystemBlockReinforcement>();
		collTester = new CollisionTester();
		traversableCodes = new List<string> { "door", "gate", "ladder", "multiblock" };
		doorCodes = new List<string> { "door", "gate", "multiblock" };
		climbableCodes = new List<string> { "ladder" };
		steppableCodes = new List<string> { "stair", "path", "bed-", "farmland", "furrowedland", "slab", "carpet" };
		tmpVec = new Vec3d();
		tmpPos = new BlockPos(0);
	}

	public List<VillagerPathNode> FindPath(BlockPos start, BlockPos end, int searchDepth = 12000)
	{
		if (start == null || end == null) return null;

		// Refresh nearby villager positions for soft crowd-avoidance. Bounded radius around start.
		RefreshOccupiedCells(start);

		// Probe near block centre so narrow collision (fences, posts) is reliably detected, no diagonal slip-through.
		centerOffsetX = 0.45 + _rng.NextDouble() * 0.10;
		centerOffsetZ = 0.45 + _rng.NextDouble() * 0.10;

		// SortedSet (O log n min) + Dictionary (O 1 lookup) for the open set.
		SortedSet<VillagerPathNode> openSet    = new SortedSet<VillagerPathNode>();
		Dictionary<BlockPos, VillagerPathNode> openLookup = new Dictionary<BlockPos, VillagerPathNode>();
		HashSet<VillagerPathNode> closedSet    = new HashSet<VillagerPathNode>();

		VillagerPathNode startNode = new VillagerPathNode(start, end, isDoor(blockAccessor.GetBlock(start)));
		openSet.Add(startNode);
		openLookup[start] = startNode;

		int iterations = 0;
		while (iterations++ < searchDepth && openSet.Count > 0)
		{
			VillagerPathNode current = openSet.Min;
			openSet.Remove(current);
			openLookup.Remove(current.BlockPos);
			closedSet.Add(current);

			if (current.BlockPos.Equals(end))
				return current.RetracePath();

			foreach (Cardinal cardinal in Cardinal.ALL)
			{
				VillagerPathNode neighbour = new VillagerPathNode(current, cardinal);
				if (closedSet.Contains(neighbour)) continue;

				float extraCost = 0f;
				if (!traversable(neighbour, end, cardinal, ref extraCost)) continue;

				neighbour.SetGCostFromParent(current, extraCost);
				neighbour.Init(end, isDoor(blockAccessor.GetBlock(neighbour.BlockPos)));

				if (openLookup.TryGetValue(neighbour.BlockPos, out VillagerPathNode existing))
				{
					if (neighbour.gCost < existing.gCost - 0.0001f)
					{
						openSet.Remove(existing);
						openSet.Add(neighbour);
						openLookup[neighbour.BlockPos] = neighbour;
					}
				}
				else
				{
					openSet.Add(neighbour);
					openLookup[neighbour.BlockPos] = neighbour;
				}
			}
		}
		return null;
	}

	// Builds the occupiedCells set from nearby villager-bearing entities AND players so
	// traversable() can mark those cells impassable. Skipped when owner is null (no avoidance)
	// or during village gather events (villagers need to cluster at the gather point).
	private void RefreshOccupiedCells(BlockPos start)
	{
		if (IgnoreCrowdOnce) { IgnoreCrowdOnce = false; occupiedCells = null; return; }
		if (owner == null || world == null) { occupiedCells = null; return; }
		if (owner.GetBehavior<EntityBehaviorVillager>()?.Village?.IsGatherActive == true)
		{
			occupiedCells = null;
			return;
		}
		occupiedCells = new HashSet<BlockPos>();
		Vec3d center = start.ToVec3d();
		Vintagestory.API.Common.Entities.Entity[] nearby = world.GetEntitiesAround(center, 50f, 10f,
			e => e != owner
				&& (e is EntityPlayer || e.HasBehavior<EntityBehaviorVillager>()));
		if (nearby == null) return;
		for (int i = 0; i < nearby.Length; i++)
		{
			occupiedCells.Add(nearby[i].Pos.AsBlockPos.Copy());
		}
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
			BlockPos[] array3 = array2;
			foreach (BlockPos blockPos in array3)
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

	// True if the block is a narrow barrier (fence/post/handrail/glasspane) that slips past the AABB probe. Gates and doors are allowed.
	protected bool IsNarrowBarrier(BlockPos pos)
	{
		Block b = blockAccessor.GetBlock(pos);
		string path = b?.Code?.Path;
		if (path == null) return false;

		// Allow gates/doors/trapdoors through the substring filter below.
		if (path.Contains("gate") || path.Contains("door")) return false;

		// isFence is vanilla's canonical fence attribute. Wattle fence sets it but
		// its code path is "wattle-..." with no "fence" substring; without this
		// check villagers walked through wattle fences and wandered out of town.
		if (b.Attributes?["isFence"].AsBool(false) == true) return true;

		// Substring fallback for blocks that don't set the attribute (modded content).
		return path.Contains("fence")
			|| path.Contains("glasspane")
			|| path.Contains("handrail");
	}

	// Diagonal NE move (x,z) -> (x+1,z+1) cuts between (x+1,z) and (x,z+1). True if either corner is a narrow barrier.
	private bool DiagonalCornerBlocked(BlockPos toPos, Cardinal fromDir)
	{
		BlockPos cornerA = new BlockPos(toPos.X - fromDir.Normali.X, toPos.Y, toPos.Z);
		BlockPos cornerB = new BlockPos(toPos.X, toPos.Y, toPos.Z - fromDir.Normali.Z);
		return IsNarrowBarrier(cornerA) || IsNarrowBarrier(cornerB);
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

			// HARD BLOCK 1: the target block itself is a narrow barrier.
			// Covers fences, fence posts, handrails, glass panes - regardless of
			// centerOffset or which branch below would have run.  (The block
			// above is already covered by the entity's 1.8-tall AABB in
			// CollisionTester.IsColliding, so no need to check it explicitly.)
			if (IsNarrowBarrier(tmpPos)) return false;

			// HARD BLOCK 2: diagonal moves must not cut across a fence corner.
			// Without this, two fences meeting at a post can be slipped through
			// diagonally because the CollisionTester probe lands just outside
			// the fence post's narrow collision box.
			if (fromDir.IsDiagonal && DiagonalCornerBlocked(node.BlockPos, fromDir))
			{
				return false;
			}

			bool forceTraversable = nodeBlock != null && nodeBlock.Code != null && traversableCodes.Exists((string c) => nodeBlock.Code.Path.Contains(c));

			// Locked doors/gates are impassable; villagers route around player padlocks.
			// reinforcement?. handles "no reinforcement mod loaded"; GetReinforcment?. handles "no entry for this pos".
			if (forceTraversable && isDoor(nodeBlock) && reinforcement?.GetReinforcment(node.BlockPos)?.Locked == true) return false;

			// Villager- or player-occupied cells are impassable so paths route around them.
			if (occupiedCells != null && occupiedCells.Contains(node.BlockPos)) return false;

			if (forceTraversable || !collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
			{
				int num = 0;
				while (num <= maxFallHeight)
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
						if (fromDir.IsDiagonal)
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

						// Foot-level liquid surcharge. Not impassable; still crossable if no alternative exists in the search depth.
						if (block2.IsLiquid())
						{
							extraCost += LiquidPathPenalty;
						}

						// Head-level liquid surcharge stacks with foot, so submerged swimming costs ~2x wading (prevents preferring "swim across" over a bridge).
						tmpPos.Set(node.BlockPos.X, node.BlockPos.Y + 1, node.BlockPos.Z);
						Block headFluid = blockAccessor.GetBlock(tmpPos, 2);
						if (headFluid != null && headFluid.IsLiquid())
						{
							extraCost += SubmergedPathPenalty;
						}
						if (forceTraversable && fromDir.IsDiagonal)
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
				else if (IsNarrowBarrier(tmpPos))
				{
					// Never step up onto a narrow barrier.
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
							Cuboidf[] array2 = array;
							foreach (Cuboidf cuboidf in array2)
							{
								if (cuboidf.Y2 > num2)
								{
									num2 = cuboidf.Y2;
								}
							}
						}
						if (num2 > 1.2f)
						{
							return false;
						}
						tmpVec.Set((double)node.BlockPos.X + centerOffsetX, (double)node.BlockPos.Y + 1.2000000476837158 + (double)num2 - 1.0, (double)node.BlockPos.Z + centerOffsetZ);
						if (!collTester.IsColliding(blockAccessor, entityCollBox, tmpVec, alsoCheckTouch: false))
						{
							if (fromDir.IsDiagonal && collisionBoxes2 != null && collisionBoxes2.Length != 0)
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
