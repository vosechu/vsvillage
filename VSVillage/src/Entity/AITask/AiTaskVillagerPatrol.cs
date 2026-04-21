using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Soldier/archer patrol task: walks between points near village workstations
/// and villagers, maintaining a protective presence around the village.
/// </summary>
public class AiTaskVillagerPatrol : AiTaskBase
{
	private Vec3d targetPos;

	private float moveSpeed = 0.008f;

	private float patrolOffset = 5f;

	private VillagerAStarNew pathfinder;

	private List<VillagerPathNode> currentPath;

	private int currentPathIndex;

	private bool stuck;

	private Vec3d lastPosition;

	private long stuckCheckTime;

	private int timesStuck;

	public AiTaskVillagerPatrol(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		if (taskConfig["moveSpeed"] != null)
		{
			moveSpeed = taskConfig["moveSpeed"].AsFloat(0.008f);
		}
		if (taskConfig["patrolOffset"] != null)
		{
			patrolOffset = taskConfig["patrolOffset"].AsFloat(5f);
		}
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
	}

	public override bool ShouldExecute()
	{
		if (!IsSoldierOrArcher())
		{
			return false;
		}
		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0 && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
		{
			return false;
		}
		if (cooldownUntilMs > entity.World.ElapsedMilliseconds)
		{
			return false;
		}
		return entity.World.Rand.NextDouble() < 0.7;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		targetPos = null;
		stuck = false;
		currentPath = null;
		currentPathIndex = 0;
		timesStuck = 0;

		EntityBehaviorVillager villagerBehavior = entity.GetBehavior<EntityBehaviorVillager>();
		Village village = villagerBehavior?.Village;
		if (village == null)
		{
			return;
		}

		// 70% patrol near a workstation, 30% patrol near a villager
		bool foundTarget = false;
		if (entity.World.Rand.NextDouble() < 0.7 && village.Workstations.Count > 0)
		{
			List<BlockPos> workstationPositions = new List<BlockPos>(village.Workstations.Keys);
			BlockPos ws = workstationPositions[entity.World.Rand.Next(workstationPositions.Count)];
			targetPos = FindPatrolPoint(ws.ToVec3d().Add(0.5, 0.0, 0.5));
			foundTarget = targetPos != null;
		}

		if (!foundTarget)
		{
			Entity[] villagerEntities = entity.World.GetEntitiesAround(
				((Entity)entity).ServerPos.XYZ, village.Radius + 10f, 6f,
				e => e != entity && e.Alive && e.HasBehavior<EntityBehaviorVillager>()
			);
			if (villagerEntities.Length > 0)
			{
				Entity randomVillager = villagerEntities[entity.World.Rand.Next(villagerEntities.Length)];
				targetPos = FindPatrolPoint(randomVillager.ServerPos.XYZ);
			}
		}

		if (targetPos == null)
		{
			return;
		}

		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
		currentPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, 1000);
		pathfinder.blockAccessor.Commit();

		if (currentPath == null || currentPath.Count == 0)
		{
			targetPos = null;
			stuck = true;
			return;
		}

		currentPathIndex = 0;
		lastPosition = ((Entity)entity).ServerPos.XYZ.Clone();
		stuckCheckTime = entity.World.ElapsedMilliseconds;
	}

	public override bool ContinueExecute(float dt)
	{
		if (targetPos == null || stuck || currentPath == null)
		{
			return false;
		}
		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0 && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
		{
			return false;
		}

		CheckIfStuck();

		if (currentPathIndex >= currentPath.Count)
		{
			return false;
		}
		if (((Entity)entity).ServerPos.SquareDistanceTo(targetPos) < 2.0)
		{
			return false;
		}

		HandlePathTraversal();
		return true;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		if (animMeta != null)
		{
			entity.AnimManager.StopAnimation(animMeta.Code);
		}
		((Entity)entity).ServerPos.Motion.X = 0.0;
		((Entity)entity).ServerPos.Motion.Z = 0.0;
		CloseAllOpenDoors();
		targetPos = null;
		currentPath = null;
		currentPathIndex = 0;
		lastPosition = null;
		timesStuck = 0;
	}

	private void ToggleDoor(bool opened, BlockPos target)
	{
		Block block = entity.World.BlockAccessor.GetBlock(target);
		if (block != null && block.Code != null && (block.Code.Path.Contains("door") || block.Code.Path.Contains("gate")))
		{
			BlockSelection blockSel = new BlockSelection
			{
				Block = block,
				Position = target,
				HitPosition = new Vec3d(0.5, 0.5, 0.5),
				Face = BlockFacing.NORTH
			};
			TreeAttribute treeAttribute = new TreeAttribute();
			treeAttribute.SetBool("opened", opened);
			try
			{
				block.Activate(entity.World, new Caller
				{
					Entity = entity,
					Type = EnumCallerType.Entity,
					Pos = ((Entity)entity).Pos.XYZ
				}, blockSel, treeAttribute);
			}
			catch (Exception ex)
			{
				entity.World.Logger.Error("Patrol: Failed to toggle door: " + ex.Message);
			}
		}
	}

	private void CloseAllOpenDoors()
	{
		if (currentPath == null) return;
		foreach (VillagerPathNode node in currentPath)
		{
			if (node.IsDoor)
			{
				Block block = entity.World.BlockAccessor.GetBlock(node.BlockPos);
				if (block != null && block.Code != null && (block.Code.Path.Contains("opened") || block.Code.Path.Contains("open")))
				{
					ToggleDoor(opened: false, node.BlockPos);
				}
			}
		}
	}

	private Vec3d FindPatrolPoint(Vec3d center)
	{
		for (int attempt = 0; attempt < 10; attempt++)
		{
			double angle = entity.World.Rand.NextDouble() * Math.PI * 2.0;
			double dist = 2.0 + entity.World.Rand.NextDouble() * patrolOffset;
			int tx = (int)(center.X + Math.Cos(angle) * dist);
			int tz = (int)(center.Z + Math.Sin(angle) * dist);
			int ty = (int)center.Y;

			BlockPos candidate = new BlockPos(tx, ty, tz, 0);

			for (int dy = 3; dy >= -3; dy--)
			{
				BlockPos check = candidate.AddCopy(0, dy, 0);
				Block floor = entity.World.BlockAccessor.GetBlock(check);
				Block space = entity.World.BlockAccessor.GetBlock(check.UpCopy());
				if (floor.SideSolid[BlockFacing.UP.Index] && (space.CollisionBoxes == null || space.CollisionBoxes.Length == 0))
				{
					return check.UpCopy().ToVec3d().Add(0.5, 0.0, 0.5);
				}
			}
		}
		return null;
	}

	private bool IsSoldierOrArcher()
	{
		string path = entity?.Code?.Path;
		return path != null && (path.EndsWith("-soldier") || path.EndsWith("-archer"));
	}

	private void HandlePathTraversal()
	{
		if (currentPath == null || currentPathIndex >= currentPath.Count)
		{
			stuck = true;
			return;
		}

		VillagerPathNode node = currentPath[currentPathIndex];
		Vec3d nodePos = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;

		double dx = myPos.X - nodePos.X;
		double dz = myPos.Z - nodePos.Z;
		if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
		{
			currentPathIndex++;
			if (currentPathIndex < currentPath.Count)
			{
				VillagerPathNode nextNode = currentPath[currentPathIndex];
				if (nextNode.IsDoor)
				{
					ToggleDoor(opened: true, nextNode.BlockPos);
				}
			}
			if (node.IsDoor)
			{
				BlockPos doorPos = node.BlockPos.Copy();
				entity.World.RegisterCallback(delegate
				{
					ToggleDoor(opened: false, doorPos);
				}, 5000);
			}
		}

		if (currentPathIndex < currentPath.Count)
		{
			VillagerPathNode next = currentPath[currentPathIndex];
			Vec3d nextPos = next.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
			Vec3d dir = nextPos.Clone().Sub(myPos);
			dir.Y = 0.0;
			dir = dir.Normalize();

			((Entity)entity).ServerPos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
			entity.Controls.WalkVector.Set(dir.X * moveSpeed, 0.0, dir.Z * moveSpeed);

			if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
			{
				entity.AnimManager.StartAnimation(animMeta);
			}
		}
	}

	private void CheckIfStuck()
	{
		long now = entity.World.ElapsedMilliseconds;
		if (now - stuckCheckTime < 3000)
		{
			return;
		}

		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		if (lastPosition != null && myPos.DistanceTo(lastPosition) < 0.5)
		{
			if (++timesStuck >= 4)
			{
				stuck = true;
				timesStuck = 0;
			}
		}
		else
		{
			timesStuck = 0;
		}

		lastPosition = myPos.Clone();
		stuckCheckTime = now;
	}
}
