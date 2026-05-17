using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerSleep : AiTaskBase
{
	private float moveSpeed = 0.06f;

	private float offset;

	private VillagerAStarNew pathfinder;

	private List<VillagerPathNode> currentPath;

	private int currentPathIndex;

	private bool stuck;

	private Vec3d targetPos;

	private BlockPos targetBedPos;

	private long lastSearchTime;

	private bool reachedBed;

	private Vec3d lastPosition;

	private long stuckCheckTime;

	private int timesStuck;

	private long lastRepathTime;

	private bool isExecuting;

	// Pre-computed in the constructor: false when this task entry doesn't apply to this
	// entity type (based on onlyForEntitySuffix / excludeEntitySuffix in the task config).
	// Avoids repeated string allocations and EndsWith calls on every AI tick.
	private readonly bool _isApplicableToThisEntity;

	public AiTaskVillagerSleep(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		moveSpeed = taskConfig["movespeed"].AsFloat(0.06f);
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-50), taskConfig["maxoffset"].AsInt(50)) / 100f;
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));

		string codePath = entity.Code?.Path ?? "";
		string onlyFor  = taskConfig["onlyForEntitySuffix"].AsString(null);
		string exclude  = taskConfig["excludeEntitySuffix"].AsString(null);
		_isApplicableToThisEntity =
			(onlyFor  == null || codePath.EndsWith(onlyFor))  &&
			(exclude  == null || !codePath.EndsWith(exclude));
	}

	public override bool ShouldExecute()
	{
		if (!_isApplicableToThisEntity) return false;

		// Respect the duringDayTimeFrames configured in JSON (handles midnight wrap-around).
		if (duringDayTimeFrames == null || duringDayTimeFrames.Length == 0
			|| !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset))
			return false;

		// Soldiers don't go back to sleep while their village is under attack.
		if (IsVillageUnderAlarm()) return false;

		if (isExecuting) return true;

		long now = entity.World.ElapsedMilliseconds;
		if (now - lastSearchTime <= 5000) return false;
		lastSearchTime = now;

		EntityBehaviorVillager villagerBehavior = entity.GetBehavior<EntityBehaviorVillager>();
		Village village = villagerBehavior?.Village;
		if (village == null) return false;

		BlockPos blockPos = villagerBehavior.Bed;
		if (blockPos == null)
		{
			blockPos = village.FindFreeBed(entity.EntityId);
			if (blockPos != null)
				villagerBehavior.Bed = blockPos; // persist so we don't search every 5 s
		}
		if (blockPos != null)
		{
			// Only validate destruction if the chunk is actually loaded - GetBlock
			// returns air (id 0) for unloaded chunks, which we'd otherwise misread as
			// "bed destroyed" and falsely free the bed in village.Beds. That mis-free
			// caused TryHireVillager to hand newly-hired villagers beds still in use
			// by villagers whose home chunks were unloaded (large villages).
			if (entity.World.BlockAccessor.GetChunkAtBlockPos(blockPos) != null)
			{
				Block bedBlock = entity.World.BlockAccessor.GetBlock(blockPos);
				if (bedBlock == null || bedBlock.Id == 0 || !bedBlock.Code.Path.Contains("villagebed"))
				{
					// Assigned bed was destroyed - release it and search again.
					villagerBehavior.Bed = null;
					village.ClearBedOwner(entity.EntityId);
					blockPos = village.FindFreeBed(entity.EntityId);
					if (blockPos != null)
						villagerBehavior.Bed = blockPos; // persist the replacement
				}
			}
		}
		if (blockPos == null) return false;

		targetBedPos = blockPos.Copy();
		Vec3d bedStandingPos = GetBedStandingPos(targetBedPos.ToVec3d());
		if (bedStandingPos == null)
		{
			entity.World.Logger.Warning("[VsVillage] Sleep: no standing position near bed at " + targetBedPos);
			targetBedPos = null;
			return false;
		}
		targetPos = bedStandingPos;
		return true;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		stuck = false;
		isExecuting = true;

		if (targetPos == null)
		{
			stuck = true;
			return;
		}
		if (targetBedPos != null && entity.Pos.SquareDistanceTo(targetBedPos.ToVec3d().Add(0.5, 0.5, 0.5)) < 4.0)
		{
			currentPath = new List<VillagerPathNode>();
			currentPathIndex = 0;
			stuck = false;
			reachedBed = false;
			StartSleeping();
			return;
		}
		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
		currentPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, 10000);
		pathfinder.blockAccessor.Commit();
		if (currentPath != null && currentPath.Count > 0)
		{
			currentPathIndex = 0;
			stuck = false;
			reachedBed = false;
			lastPosition = entity.Pos.XYZ.Clone();
			stuckCheckTime = entity.World.ElapsedMilliseconds;
			timesStuck = 0;
		}
		else
		{
			entity.World.Logger.Warning("[VsVillage] Sleep: no path to bed at " + targetPos + " (from " + startPos + ")");
			stuck = true;
		}
	}

	public override bool ContinueExecute(float dt)
	{
		if (targetPos == null || stuck || currentPath == null) return false;

		if (duringDayTimeFrames == null || duringDayTimeFrames.Length == 0
			|| !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset))
			return false;

		// Wake sleeping soldiers when their village raises an alarm.
		if (IsVillageUnderAlarm()) return false;

		if (reachedBed) return entity.AnimManager.IsAnimationActive("Lie");

		CheckIfStuck();
		if (currentPathIndex >= currentPath.Count)
		{
			StartSleeping();
			return true;
		}
		HandlePathTraversal();
		if (IsAtBed())
		{
			StartSleeping();
			return true;
		}
		return !stuck;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		isExecuting = false;
		stuck = false;
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		entity.Pos.Motion.Set(0.0, 0.0, 0.0);
		DoorPathHelper.CloseOpenDoorsAlongPath(entity, currentPath);
		if (animMeta != null)
			entity.AnimManager.StopAnimation(animMeta.Code);
		entity.AnimManager.StopAnimation("Lie");
		if (reachedBed && targetBedPos != null)
		{
			Vec3d wakePos = GetBedStandingPos(targetBedPos.ToVec3d());
			if (wakePos != null && IsPositionSafe(wakePos))
				entity.TeleportTo(wakePos);
		}
		targetPos = null;
		targetBedPos = null;
		currentPath = null;
		currentPathIndex = 0;
		reachedBed = false;
		lastPosition = null;
		timesStuck = 0;
	}

	private void HandlePathTraversal()
	{
		if (currentPath == null || currentPathIndex >= currentPath.Count) return;

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


	private bool IsAtBed()
	{
		return targetPos != null && entity.Pos.SquareDistanceTo(targetPos) < 2.25;
	}

	private void StartSleeping()
	{
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		entity.Pos.Motion.Set(0.0, 0.0, 0.0);
		if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);
		entity.AnimManager.StopAnimation("walk");
		entity.AnimManager.StopAnimation("Walk");
		entity.AnimManager.StopAnimation("interact");
		if (targetBedPos != null)
		{
			BlockEntityVillagerBed blockEntity = entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(targetBedPos);
			if (blockEntity != null)
			{
				entity.Pos.SetPos(GetBedSleepPosition(blockEntity));
				entity.Pos.Yaw = blockEntity.Yaw;
			}
			else
			{
				FaceBed();
			}
		}
		entity.AnimManager.StartAnimation(new AnimationMetaData { Code = "Lie", Animation = "Lie", AnimationSpeed = 1f }.Init());
		reachedBed = true;
	}

	private void FaceBed()
	{
		if (targetBedPos == null) return;
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d bedPos = targetBedPos.ToVec3d().Add(0.5, 0.5, 0.5);
		entity.Pos.Yaw = (float)Math.Atan2(bedPos.X - myPos.X, bedPos.Z - myPos.Z);
	}

	private Vec3d GetBedStandingPos(Vec3d bedCenter)
	{
		if (bedCenter == null) return null;

		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos bedBlockPos = bedCenter.AsBlockPos;
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d bestPos = null;
		double bestDist = double.MaxValue;

		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos nb = bedBlockPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			bool posClear  = ba.GetBlock(nb).CollisionBoxes == null || ba.GetBlock(nb).CollisionBoxes.Length == 0;
			bool headClear = ba.GetBlock(nb.UpCopy()).CollisionBoxes == null || ba.GetBlock(nb.UpCopy()).CollisionBoxes.Length == 0;
			bool grounded  = ba.GetBlock(nb.DownCopy()).CollisionBoxes != null && ba.GetBlock(nb.DownCopy()).CollisionBoxes.Length != 0;
			if (posClear && headClear && grounded)
			{
				Vec3d candidate = nb.ToVec3d().Add(0.5, 0.0, 0.5);
				double dist = candidate.SquareDistanceTo(myPos);
				if (dist < bestDist) { bestDist = dist; bestPos = candidate; }
			}
		}
		if (bestPos != null) return bestPos;

		entity.World.Logger.Warning("[VsVillage] Sleep: no clear neighbour around bed " + bedBlockPos + " - using bed centre");
		return bedCenter;
	}

	private void CheckIfStuck()
	{
		long now = entity.World.ElapsedMilliseconds;
		if (now - stuckCheckTime < 3000) return;

		Vec3d pos = entity.Pos.XYZ;
		if (lastPosition != null)
		{
			double moved = pos.DistanceTo(lastPosition);
			double threshold = Math.Max(0.25, moveSpeed * 60 * 0.4);
			if (moved < threshold)
			{
				timesStuck++;
				if (timesStuck <= 3)
				{
					if (now - lastRepathTime > 5000)
					{
						AttemptRepath();
						lastRepathTime = now;
					}
				}
				else if (timesStuck >= 4)
				{
					TeleportToSafeLocation();
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
		List<VillagerPathNode> newPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos);
		pathfinder.blockAccessor.Commit();
		if (newPath != null && newPath.Count > 0)
		{
			currentPath = newPath;
			currentPathIndex = 0;
		}
		else
		{
			entity.World.Logger.Warning("[VsVillage] Sleep: no alternative path to bed for entity " + entity.EntityId);
		}
	}

	// Returns true when this soldier's village has an active alarm.
	// Only soldiers respond to alarms - archers and civilians sleep through them.
	private bool IsVillageUnderAlarm()
	{
		string codePath = entity.Code?.Path ?? "";
		if (!codePath.EndsWith("-soldier") && !codePath.EndsWith("-archer")) return false;

		Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
		if (village == null) return false;

		if (!AiTaskVillagerMeleeAttack.VillageAlarms.TryGetValue(village.Id, out long alarmRaisedAt))
			return false;

		long elapsed = entity.World.ElapsedMilliseconds - alarmRaisedAt;
		return elapsed >= 0 && elapsed < AiTaskVillagerMeleeAttack.AlarmDurationMs;
	}

	private void TeleportToSafeLocation()
	{
		Vec3d dest = null;
		string label = "";

		if (targetPos != null && IsPositionSafe(targetPos))
		{
			dest = targetPos;
			label = "bed";
		}
		if (dest == null)
		{
			Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
			if (village != null)
			{
				BlockPos gp = village.FindRandomGatherplace();
				if (gp != null)
				{
					Vec3d candidate = gp.ToVec3d().Add(0.5, 1.0, 0.5);
					if (IsPositionSafe(candidate)) { dest = candidate; label = "gatherplace"; }
				}
			}
		}
		if (dest == null)
		{
			EntityBehaviorVillager bv = entity.GetBehavior<EntityBehaviorVillager>();
			Village v = bv?.Village;
			if (v != null && bv != null)
			{
				BlockPos ws = v.FindFreeWorkstation(entity.EntityId, bv.Profession);
				if (ws != null)
				{
					Vec3d candidate = ws.ToVec3d().Add(0.5, 1.0, 0.5);
					if (IsPositionSafe(candidate)) { dest = candidate; label = "workstation"; }
				}
			}
		}

		if (dest != null)
		{
			entity.TeleportTo(dest);
			if (label == "bed") StartSleeping();
			else AttemptRepath();
		}
		else
		{
			entity.World.Logger.Warning("[VsVillage] Sleep: entity " + entity.EntityId + " has no safe teleport destination - giving up");
			stuck = true;
		}
	}

	private bool IsPositionSafe(Vec3d pos)
	{
		if (pos == null) return false;
		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos bp = pos.AsBlockPos;
		bool atClear   = ba.GetBlock(bp).CollisionBoxes == null || ba.GetBlock(bp).CollisionBoxes.Length == 0;
		bool headClear = ba.GetBlock(bp.UpCopy()).CollisionBoxes == null || ba.GetBlock(bp.UpCopy()).CollisionBoxes.Length == 0;
		bool grounded  = ba.GetBlock(bp.DownCopy()).CollisionBoxes != null && ba.GetBlock(bp.DownCopy()).CollisionBoxes.Length != 0;
		return atClear && headClear && grounded;
	}

	private Vec3d GetBedSleepPosition(BlockEntityVillagerBed bed)
	{
		string side = null;
		bed.Block?.Variant?.TryGetValue("side", out side);
		Cardinal dir = side switch
		{
			"north" => Cardinal.North,
			"east"  => Cardinal.East,
			"south" => Cardinal.South,
			_       => Cardinal.West,
		};
		return bed.Pos.ToVec3d().Add(0.5, 0.0, 0.5).Add(dir.Normalf.Clone().Mul(0.7f));
	}
}
