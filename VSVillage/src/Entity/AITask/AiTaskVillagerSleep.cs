using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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

	public AiTaskVillagerSleep(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		moveSpeed = taskConfig["movespeed"].AsFloat(0.06f);
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-50), taskConfig["maxoffset"].AsInt(50)) / 100f;
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
	}

	public override bool ShouldExecute()
	{
		float hourOfDay = entity.World.Calendar.HourOfDay;
		if (!ManualTimeCheck(hourOfDay))
		{
			return false;
		}
		if (isExecuting)
		{
			return true;
		}
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		if (elapsedMilliseconds - lastSearchTime <= 5000)
		{
			return false;
		}
		lastSearchTime = elapsedMilliseconds;
		entity.World.Logger.Debug("Sleep: Entity " + entity.EntityId + " searching for bed...");
		EntityBehaviorVillager villagerBehavior = entity.GetBehavior<EntityBehaviorVillager>();
		Village village = villagerBehavior?.Village;
		if (village == null)
		{
			entity.World.Logger.Warning("Sleep: No village found for entity " + entity.EntityId);
			return false;
		}
		// Prefer previously assigned bed to prevent bed-shifting on chunk reload
		BlockPos blockPos = villagerBehavior.Bed ?? village.FindFreeBed(entity.EntityId);
		// Verify the bed block still physically exists (it may have been destroyed)
		if (blockPos != null)
		{
			Block bedBlock = entity.World.BlockAccessor.GetBlock(blockPos);
			if (bedBlock == null || bedBlock.Id == 0 || !bedBlock.Code.Path.Contains("villagebed"))
			{
				entity.World.Logger.Warning("Sleep: Assigned bed at " + blockPos + " was destroyed, seeking new bed");
				villagerBehavior.Bed = null;
				village.ClearBedOwner(entity.EntityId);
				blockPos = village.FindFreeBed(entity.EntityId);
			}
		}
		if (blockPos == null)
		{
			entity.World.Logger.Warning("Sleep: No free bed found for entity " + entity.EntityId);
			return false;
		}
		targetBedPos = blockPos.Copy();
		entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " found bed at " + targetBedPos.ToString());
		Vec3d bedStandingPos = GetBedStandingPos(targetBedPos.ToVec3d());
		if (bedStandingPos == null)
		{
			entity.World.Logger.Warning("Sleep: Could not find standing position near bed");
			targetBedPos = null;
			return false;
		}
		targetPos = bedStandingPos;
		return true;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		stuck = false; // reset from any previous failed attempt so we start clean
		isExecuting = true;
		entity.World.Logger.Notification("Sleep: StartExecute for entity " + entity.EntityId);
		if (targetPos == null)
		{
			entity.World.Logger.Warning("Sleep: StartExecute but targetPos is null!");
			stuck = true;
			return;
		}
		// Already at bed (e.g. after server restart while sleeping) — skip pathing.
		if (targetBedPos != null && ((Entity)entity).ServerPos.SquareDistanceTo(targetBedPos.ToVec3d().Add(0.5, 0.5, 0.5)) < 4.0)
		{
			entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " already at bed, sleeping immediately");
			currentPath = new List<VillagerPathNode>();
			currentPathIndex = 0;
			stuck = false;
			reachedBed = false;
			StartSleeping();
			return;
		}

		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
		BlockPos asBlockPos = targetPos.AsBlockPos;
		entity.World.Logger.Notification("Sleep: Pathing from " + startPos + " to target " + asBlockPos + " (bed " + targetBedPos + ")");
		currentPath = pathfinder.FindPath(startPos, asBlockPos, 3000);
		pathfinder.blockAccessor.Commit();
		if (currentPath != null && currentPath.Count > 0)
		{
			entity.World.Logger.Notification("Sleep: Found path with " + currentPath.Count + " nodes to bed");
			currentPathIndex = 0;
			stuck = false;
			reachedBed = false;
			lastPosition = ((Entity)entity).ServerPos.XYZ.Clone();
			stuckCheckTime = entity.World.ElapsedMilliseconds;
			timesStuck = 0;
		}
		else
		{
			entity.World.Logger.Warning("Sleep: No path found to bed at " + targetPos.ToString() + " (from " + startPos + ")");
			stuck = true;
		}
	}

	public override bool ContinueExecute(float dt)
	{
		if (targetPos == null || stuck || currentPath == null)
		{
			entity.World.Logger.Debug("Sleep: ContinueExecute ending - targetPos:" + (targetPos == null) + " stuck:" + stuck + " path:" + (currentPath == null));
			return false;
		}
		float hourOfDay = entity.World.Calendar.HourOfDay;
		if (!ManualTimeCheck(hourOfDay))
		{
			entity.World.Logger.Notification("Sleep: Time to wake up! Entity " + entity.EntityId);
			return false;
		}
		if (reachedBed)
		{
			return entity.AnimManager.IsAnimationActive("Lie");
		}
		CheckIfStuck();
		if (currentPathIndex >= currentPath.Count)
		{
			entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " reached end of path, starting sleep");
			StartSleeping();
			return true;
		}
		HandlePathTraversal();
		if (IsAtBed())
		{
			entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " at bed, starting sleep");
			StartSleeping();
			return true;
		}
		return !stuck;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		isExecuting = false;
		stuck = false; // always reset so the next ShouldExecute→StartExecute cycle starts clean
		entity.World.Logger.Debug("Sleep: FinishExecute for entity " + entity.EntityId + ", cancelled: " + cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		((Entity)entity).ServerPos.Motion.Set(0.0, 0.0, 0.0);
		CloseAllOpenDoors();
		if (animMeta != null)
		{
			entity.AnimManager.StopAnimation(animMeta.Code);
		}
		entity.AnimManager.StopAnimation("Lie");
		// Wake-up relocation: if the entity was sleeping (snapped inside the bed block),
		// move it to a clear standing position next to the bed BEFORE we clear state.
		// This ensures other AI tasks (gotomayor, gotowork) can immediately find a valid
		// start position without colliding with the bed's collision box.
		if (reachedBed && targetBedPos != null)
		{
			Vec3d wakePos = GetBedStandingPos(targetBedPos.ToVec3d());
			if (wakePos != null && IsPositionSafe(wakePos))
			{
				entity.TeleportTo(wakePos);
				entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " woke up at " + wakePos);
			}
		}
		targetPos = null;
		targetBedPos = null;
		currentPath = null;
		currentPathIndex = 0;
		reachedBed = false;
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
				entity.World.Logger.Error("Sleep: Failed to toggle door: " + ex.Message);
			}
		}
	}

	private void HandlePathTraversal()
	{
		if (currentPath == null || currentPathIndex >= currentPath.Count)
		{
			return;
		}
		VillagerPathNode villagerPathNode = currentPath[currentPathIndex];
		Vec3d vec3d = villagerPathNode.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
		double num = xYZ.X - vec3d.X;
		double num2 = xYZ.Z - vec3d.Z;
		double num3 = Math.Sqrt(num * num + num2 * num2);
		if (num3 < 0.5)
		{
			currentPathIndex++;
			if (currentPathIndex < currentPath.Count)
			{
				VillagerPathNode villagerPathNode2 = currentPath[currentPathIndex];
				if (villagerPathNode2.IsDoor)
				{
					ToggleDoor(opened: true, villagerPathNode2.BlockPos);
				}
			}
			if (villagerPathNode.IsDoor)
			{
				BlockPos doorPos = villagerPathNode.BlockPos.Copy();
				entity.World.RegisterCallback(delegate
				{
					ToggleDoor(opened: false, doorPos);
				}, 5000);
			}
		}
		if (currentPathIndex < currentPath.Count)
		{
			VillagerPathNode villagerPathNode3 = currentPath[currentPathIndex];
			Vec3d vec3d2 = villagerPathNode3.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
			Vec3d vec3d3 = vec3d2.Clone().Sub(xYZ);
			vec3d3.Y = 0.0;
			vec3d3 = vec3d3.Normalize();
			float yaw = (float)Math.Atan2(vec3d3.X, vec3d3.Z);
			((Entity)entity).ServerPos.Yaw = yaw;
			double num4 = moveSpeed;
			entity.Controls.WalkVector.Set(vec3d3.X * num4, 0.0, vec3d3.Z * num4);
			if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
			{
				entity.AnimManager.StartAnimation(animMeta);
			}
		}
	}

	private void CloseAllOpenDoors()
	{
		if (currentPath == null)
		{
			return;
		}
		for (int i = 0; i < currentPath.Count; i++)
		{
			VillagerPathNode villagerPathNode = currentPath[i];
			if (villagerPathNode.IsDoor)
			{
				Block block = entity.World.BlockAccessor.GetBlock(villagerPathNode.BlockPos);
				if (block != null && block.Code != null && (block.Code.Path.Contains("opened") || block.Code.Path.Contains("open")))
				{
					ToggleDoor(opened: false, villagerPathNode.BlockPos);
				}
			}
		}
	}

	private bool IsAtBed()
	{
		return targetPos != null && ((Entity)entity).ServerPos.SquareDistanceTo(targetPos) < 2.25;
	}

	private void StartSleeping()
	{
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		((Entity)entity).ServerPos.Motion.Set(0.0, 0.0, 0.0);
		if (animMeta != null)
		{
			entity.AnimManager.StopAnimation(animMeta.Code);
		}
		entity.AnimManager.StopAnimation("walk");
		entity.AnimManager.StopAnimation("Walk");
		entity.AnimManager.StopAnimation("balanced-walk");
		entity.AnimManager.StopAnimation("interact");
		if (targetBedPos != null)
		{
			BlockEntityVillagerBed blockEntity = entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(targetBedPos);
			if (blockEntity != null)
			{
				Vec3d bedSleepPosition = GetBedSleepPosition(blockEntity);
				((Entity)entity).ServerPos.SetPos(bedSleepPosition);
				((Entity)entity).ServerPos.Yaw = blockEntity.Yaw;
			}
			else
			{
				FaceBed();
			}
		}
		AnimationMetaData animdata = new AnimationMetaData
		{
			Code = "Lie",
			Animation = "Lie",
			AnimationSpeed = 1f
		}.Init();
		entity.AnimManager.StartAnimation(animdata);
		reachedBed = true;
		entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " now sleeping at " + ((targetBedPos != null) ? targetBedPos.ToString() : "unknown"));
	}

	private void FaceBed()
	{
		if (targetBedPos != null)
		{
			Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
			Vec3d vec3d = targetBedPos.ToVec3d().Add(0.5, 0.5, 0.5);
			double y = vec3d.X - xYZ.X;
			double x = vec3d.Z - xYZ.Z;
			float yaw = (float)Math.Atan2(y, x);
			((Entity)entity).ServerPos.Yaw = yaw;
			((Entity)entity).Pos.Yaw = yaw;
		}
	}

	private Vec3d GetBedStandingPos(Vec3d bedCenter)
	{
		if (bedCenter == null) return null;
		IBlockAccessor blockAccessor = entity.World.BlockAccessor;
		BlockPos bedBlockPos = bedCenter.AsBlockPos;
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		Vec3d bestPos = null;
		double bestDist = double.MaxValue;

		// Scan all 4 cardinal sides of the bed for the closest walkable standing position.
		// The original code only checked the side opposite the bed's facing AND had a broken
		// flag2 check that required the standing block to be non-air (always false for open air),
		// so it always fell back to bedCenter (the raw bed-block corner). This caused FindPath to
		// target the bed block itself, which had no clear accessible neighbors in some layouts.
		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos neighborPos = bedBlockPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			Block atPos  = blockAccessor.GetBlock(neighborPos);             // entity body space — must be air
			Block above  = blockAccessor.GetBlock(neighborPos.UpCopy());    // head space — must be air
			Block below  = blockAccessor.GetBlock(neighborPos.DownCopy()); // floor — must be solid

			bool posClear    = atPos.CollisionBoxes == null || atPos.CollisionBoxes.Length == 0;
			bool headClear   = above.CollisionBoxes == null || above.CollisionBoxes.Length == 0;
			bool groundSolid = below.CollisionBoxes != null && below.CollisionBoxes.Length > 0;

			if (!posClear || !headClear || !groundSolid) continue;

			Vec3d candidate = neighborPos.ToVec3d().Add(0.5, 0.0, 0.5);
			double dist = candidate.SquareDistanceTo(myPos);
			if (dist < bestDist)
			{
				bestDist = dist;
				bestPos = candidate;
			}
		}

		if (bestPos != null)
		{
			entity.World.Logger.Debug("Sleep: Found accessible bed standing position at " + bestPos);
			return bestPos;
		}
		// Last resort — path to the bed block itself and let the pathfinder route adjacent.
		entity.World.Logger.Warning("Sleep: No accessible neighbor found around bed " + bedBlockPos + ", falling back to bed center");
		return bedCenter;
	}

	private void CheckIfStuck()
	{
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		if (elapsedMilliseconds - stuckCheckTime < 3000)
		{
			return;
		}
		Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
		if (lastPosition != null)
		{
			double num = xYZ.DistanceTo(lastPosition);
			if (num < 0.5)
			{
				timesStuck++;
				entity.World.Logger.Warning("Sleep: Entity " + entity.EntityId + " stuck! (count: " + timesStuck + ")");
				if (timesStuck <= 3)
				{
					long num2 = elapsedMilliseconds - lastRepathTime;
					if (num2 > 5000)
					{
						AttemptRepath();
						lastRepathTime = elapsedMilliseconds;
					}
				}
				else if (timesStuck >= 4)
				{
					entity.World.Logger.Notification("Sleep: Entity " + entity.EntityId + " teleporting to safe location");
					TeleportToSafeLocation();
					timesStuck = 0;
				}
			}
			else
			{
				timesStuck = 0;
			}
		}
		lastPosition = xYZ.Clone();
		stuckCheckTime = elapsedMilliseconds;
	}

	private void AttemptRepath()
	{
		if (!(targetPos == null))
		{
			entity.World.Logger.Debug("Sleep: Entity " + entity.EntityId + " attempting repath");
			pathfinder.blockAccessor.Begin();
			pathfinder.SetEntityCollisionBox(entity);
			BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
			BlockPos asBlockPos = targetPos.AsBlockPos;
			List<VillagerPathNode> list = pathfinder.FindPath(startPos, asBlockPos);
			pathfinder.blockAccessor.Commit();
			if (list != null && list.Count > 0)
			{
				entity.World.Logger.Debug("Sleep: Found new path with " + list.Count + " nodes");
				currentPath = list;
				currentPathIndex = 0;
			}
			else
			{
				entity.World.Logger.Warning("Sleep: Could not find alternative path");
			}
		}
	}

	private bool ManualTimeCheck(float currentHour)
	{
		float num = 21.5f + offset;
		float num2 = 6f + offset;
		if (num > num2)
		{
			return currentHour >= num || currentHour <= num2;
		}
		return currentHour >= num && currentHour <= num2;
	}

	private void TeleportToSafeLocation()
	{
		Vec3d vec3d = null;
		string text = "";
		if (targetPos != null && IsPositionSafe(targetPos))
		{
			vec3d = targetPos;
			text = "bed";
		}
		if (vec3d == null)
		{
			Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
			if (village != null)
			{
				BlockPos blockPos = village.FindRandomGatherplace();
				if (blockPos != null)
				{
					Vec3d vec3d2 = blockPos.ToVec3d().Add(0.5, 1.0, 0.5);
					if (IsPositionSafe(vec3d2))
					{
						vec3d = vec3d2;
						text = "gatherplace";
					}
				}
			}
		}
		if (vec3d == null)
		{
			EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
			Village village2 = behavior?.Village;
			if (village2 != null && behavior != null)
			{
				BlockPos blockPos2 = village2.FindFreeWorkstation(entity.EntityId, behavior.Profession);
				if (blockPos2 != null)
				{
					Vec3d vec3d3 = blockPos2.ToVec3d().Add(0.5, 1.0, 0.5);
					if (IsPositionSafe(vec3d3))
					{
						vec3d = vec3d3;
						text = "workstation";
					}
				}
			}
		}
		if (vec3d != null)
		{
			entity.TeleportTo(vec3d);
			entity.World.Logger.Notification("Sleep: Teleported entity " + entity.EntityId + " to " + text + " at " + vec3d.ToString());
			if (text == "bed")
			{
				StartSleeping();
			}
			else
			{
				AttemptRepath();
			}
		}
		else
		{
			entity.World.Logger.Warning("Sleep: Entity " + entity.EntityId + " could not find safe teleport location, giving up");
			stuck = true;
		}
	}

	private bool IsPositionSafe(Vec3d pos)
	{
		if (pos == null)
		{
			return false;
		}
		IBlockAccessor blockAccessor = entity.World.BlockAccessor;
		BlockPos asBlockPos = pos.AsBlockPos;
		Block block = blockAccessor.GetBlock(asBlockPos);
		Block block2 = blockAccessor.GetBlock(asBlockPos.UpCopy());
		bool flag = block.CollisionBoxes == null || block.CollisionBoxes.Length == 0;
		bool flag2 = block2.CollisionBoxes == null || block2.CollisionBoxes.Length == 0;
		Block block3 = blockAccessor.GetBlock(asBlockPos.DownCopy());
		bool flag3 = block3.CollisionBoxes != null && block3.CollisionBoxes.Length != 0;
		return flag && flag2 && flag3;
	}

	private Vec3d GetBedSleepPosition(BlockEntityVillagerBed bed)
	{
		Cardinal cardinal = bed.Block.Variant["side"] switch
		{
			"north" => Cardinal.North, 
			"east" => Cardinal.East, 
			"south" => Cardinal.South, 
			_ => Cardinal.West, 
		};
		return bed.Pos.ToVec3d().Add(0.5, 0.0, 0.5).Add(cardinal.Normalf.Clone().Mul(0.7f));
	}
}
