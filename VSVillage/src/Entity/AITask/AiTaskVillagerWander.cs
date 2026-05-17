using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerWander : AiTaskBase
{
	private Vec3d targetPos;

	private float moveSpeed = 0.03f;

	private float wanderRange = 20f;

	private long lastWanderTime;

	private long wanderCooldownMs = 10000L;

	private string requiredEntitySuffix;

	private bool constrainToWorkstation;

	private float workstationRadius = 10f;

	private VillagerAStarNew pathfinder;

	private List<VillagerPathNode> currentPath;

	private int currentPathIndex;

	private bool stuck;

	private Vec3d lastPosition;

	private long stuckCheckTime;

	private int timesStuck;

	private long lastRepathTime;

	public AiTaskVillagerWander(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		// AsFloat/AsInt/AsBool already return the default when the key is absent.
		moveSpeed           = taskConfig["moveSpeed"].AsFloat(0.03f);
		wanderRange         = taskConfig["wanderRange"].AsFloat(20f);
		wanderCooldownMs    = (long)taskConfig["wanderCooldownSeconds"].AsInt(10) * 1000L;
		requiredEntitySuffix = taskConfig["entitySuffix"].AsString(null);
		constrainToWorkstation = taskConfig["constrainToWorkstation"].AsBool();
		workstationRadius   = taskConfig["workstationRadius"].AsFloat(10f);

		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
		lastPosition = null;
		stuckCheckTime = 0L;
		timesStuck = 0;
		lastRepathTime = 0L;
	}

	public override bool ShouldExecute()
	{
		if (!string.IsNullOrEmpty(requiredEntitySuffix)
			&& (entity?.Code?.Path == null || !entity.Code.Path.EndsWith(requiredEntitySuffix)))
			return false;

		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0
			&& !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
			return false;

		long now = entity.World.ElapsedMilliseconds;
		return now - lastWanderTime >= wanderCooldownMs && entity.World.Rand.NextDouble() <= 0.3;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		lastWanderTime = entity.World.ElapsedMilliseconds;
		stuck = false;
		currentPath = null;
		targetPos = null;
		BlockPos startPos = entity.Pos.AsBlockPos;

		BlockPos wanderCenter;
		if (constrainToWorkstation)
		{
			BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
			if (ws == null) { stuck = true; return; }
			wanderCenter = ws;
		}
		else
		{
			wanderCenter = startPos;
		}

		float effectiveRange = constrainToWorkstation ? Math.Min(wanderRange, workstationRadius) : wanderRange;
		const int attempts = 10;
		for (int i = 0; i < attempts; i++)
		{
			double angle = entity.World.Rand.NextDouble() * Math.PI * 2.0;
			double dist  = 2.0 + entity.World.Rand.NextDouble() * effectiveRange;
			BlockPos candidate = wanderCenter.AddCopy((int)(Math.Cos(angle) * dist), 0, (int)(Math.Sin(angle) * dist));

			// Find the surface at that XZ position.
			for (int dy = 5; dy >= -5; dy--)
			{
				BlockPos check = candidate.AddCopy(0, dy, 0);
				if (entity.World.BlockAccessor.GetBlock(check).SideSolid[BlockFacing.UP.Index]
					&& (entity.World.BlockAccessor.GetBlock(check.UpCopy()).CollisionBoxes == null
						|| entity.World.BlockAccessor.GetBlock(check.UpCopy()).CollisionBoxes.Length == 0))
				{
					candidate = check.UpCopy();
					break;
				}
			}

			pathfinder.blockAccessor.Begin();
			pathfinder.SetEntityCollisionBox(entity);
			currentPath = pathfinder.FindPath(startPos, candidate, 500);
			pathfinder.blockAccessor.Commit();

			if (currentPath != null && currentPath.Count > 5)
			{
				targetPos = candidate.ToVec3d().Add(0.5, 0.0, 0.5);
				currentPathIndex = 0;
				stuck = false;
				lastPosition = entity.Pos.XYZ.Clone();
				stuckCheckTime = entity.World.ElapsedMilliseconds;
				timesStuck = 0;
				return;
			}
		}
		stuck = true;
	}

	public override bool ContinueExecute(float dt)
	{
		CheckIfStuck();
		if (targetPos == null || stuck || currentPath == null) return false;

		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0
			&& !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
			return false;

		if (currentPathIndex >= currentPath.Count) return false;

		HandlePathTraversal();
		return entity.Pos.SquareDistanceTo(targetPos) > 2.0;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);
		entity.Pos.Motion.X = 0.0;
		entity.Pos.Motion.Z = 0.0;
		DoorPathHelper.CloseOpenDoorsAlongPath(entity, currentPath);
		targetPos = null;
		currentPath = null;
		currentPathIndex = 0;
		lastPosition = null;
		timesStuck = 0;
	}

	private void HandlePathTraversal()
	{
		if (currentPath == null || currentPathIndex >= currentPath.Count) { stuck = true; return; }

		VillagerPathNode node = currentPath[currentPathIndex];
		Vec3d nodePos = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d myPos = entity.Pos.XYZ;
		double dx = myPos.X - nodePos.X;
		double dz = myPos.Z - nodePos.Z;
		if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
		{
			currentPathIndex++;
			if (currentPathIndex < currentPath.Count && currentPath[currentPathIndex].IsDoor)
				DoorPathHelper.ToggleDoor(entity, currentPath[currentPathIndex].BlockPos, opened: true);

			if (node.IsDoor)
			{
				BlockPos doorPos = node.BlockPos.Copy();
				DoorPathHelper.ScheduleDoorClose(entity, doorPos, 5000);
			}
		}
		if (currentPathIndex < currentPath.Count)
		{
			Vec3d next = currentPath[currentPathIndex].BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
			Vec3d dir = next.Clone().Sub(myPos);
			dir.Y = 0.0;
			dir = dir.Normalize();
			entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
			entity.Controls.WalkVector.Set(dir.X * moveSpeed, 0.0, dir.Z * moveSpeed);
			if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
				entity.AnimManager.StartAnimation(animMeta);
		}
	}

	private void CheckIfStuck()
	{
		long now = entity.World.ElapsedMilliseconds;
		if (now - stuckCheckTime < 3000) return;

		Vec3d pos = entity.Pos.XYZ;
		if (lastPosition != null)
		{
			double moved = pos.DistanceTo(lastPosition);
			// Match GotoAndInteract's speed-scaled threshold.
			double threshold = Math.Max(0.25, moveSpeed * 60 * 0.4);
			if (moved < threshold)
			{
				timesStuck++;
				if (timesStuck <= 5)
				{
					if (now - lastRepathTime > 5000)
					{
						AttemptRepath();
						lastRepathTime = now;
					}
				}
				else if (timesStuck >= 6)
				{
					TeleportToRecoveryPosition();
					timesStuck = 0;
				}
			}
			else
			{
				timesStuck = 0;
			}
		}
		lastPosition = pos.Clone();
		stuckCheckTime = now;
	}

	private void AttemptRepath()
	{
		if (targetPos == null) return;

		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
		List<VillagerPathNode> newPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, 500);
		pathfinder.blockAccessor.Commit();
		if (newPath != null && newPath.Count > 0)
		{
			currentPath = newPath;
			currentPathIndex = 0;
			stuck = false;
		}
	}

	private void TeleportToRecoveryPosition()
	{
		Vec3d dest = null;
		if (currentPath != null && currentPathIndex < currentPath.Count)
		{
			int skip = Math.Min(2, currentPath.Count - currentPathIndex - 1);
			if (skip > 0)
			{
				dest = currentPath[currentPathIndex + skip].BlockPos.ToVec3d().Add(0.5, 0.1, 0.5);
				currentPathIndex += skip;
			}
		}
		if (dest == null && targetPos != null)
		{
			Vec3d myPos = entity.Pos.XYZ;
			Vec3d diff = targetPos.Clone().Sub(myPos);
			if (diff.LengthSq() < 0.0001) return;
			Vec3d dir = diff.Normalize();
			dest = myPos.Add(dir.X * 2.0, 0.5, dir.Z * 2.0);
		}
		if (dest != null)
		{
			IBlockAccessor ba = entity.World.BlockAccessor;
			BlockPos bp = dest.AsBlockPos;
			bool clear = ba.GetBlock(bp).CollisionBoxes == null || ba.GetBlock(bp).CollisionBoxes.Length == 0;
			bool head  = ba.GetBlock(bp.UpCopy()).CollisionBoxes == null || ba.GetBlock(bp.UpCopy()).CollisionBoxes.Length == 0;
			if (clear && head)
			{
				entity.TeleportTo(dest);
				entity.Pos.Motion.Y = 0.1;
			}
			else
			{
				stuck = true;
			}
		}
	}
}
