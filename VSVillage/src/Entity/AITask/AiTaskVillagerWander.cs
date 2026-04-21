using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
		if (taskConfig["moveSpeed"] != null)
		{
			moveSpeed = taskConfig["moveSpeed"].AsFloat(0.03f);
		}
		if (taskConfig["wanderRange"] != null)
		{
			wanderRange = taskConfig["wanderRange"].AsFloat(20f);
		}
		if (taskConfig["wanderCooldownSeconds"] != null)
		{
			wanderCooldownMs = (long)taskConfig["wanderCooldownSeconds"].AsInt(10) * 1000L;
		}
		if (taskConfig["entitySuffix"] != null)
		{
			requiredEntitySuffix = taskConfig["entitySuffix"].AsString();
		}
		if (taskConfig["constrainToWorkstation"] != null)
		{
			constrainToWorkstation = taskConfig["constrainToWorkstation"].AsBool(false);
		}
		if (taskConfig["workstationRadius"] != null)
		{
			workstationRadius = taskConfig["workstationRadius"].AsFloat(10f);
		}
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
		lastPosition = null;
		stuckCheckTime = 0L;
		timesStuck = 0;
		lastRepathTime = 0L;
	}

	public override bool ShouldExecute()
	{
		if (!string.IsNullOrEmpty(requiredEntitySuffix) && (entity == null || entity.Code == null || entity.Code.Path == null || !entity.Code.Path.EndsWith(requiredEntitySuffix)))
		{
			return false;
		}
		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0 && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
		{
			return false;
		}
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		return elapsedMilliseconds - lastWanderTime >= wanderCooldownMs && entity.World.Rand.NextDouble() <= 0.3;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		lastWanderTime = entity.World.ElapsedMilliseconds;
		stuck = false;
		currentPath = null;
		targetPos = null;

		BlockPos startPos = ((Entity)entity).ServerPos.AsBlockPos;
		BlockPos wanderCenter;

		if (constrainToWorkstation)
		{
			EntityBehaviorVillager villagerBehavior = entity.GetBehavior<EntityBehaviorVillager>();
			BlockPos ws = villagerBehavior?.Workstation;
			if (ws == null)
			{
				entity.World.Logger.Debug("Villager constrained wander: no workstation found, skipping");
				stuck = true;
				return;
			}
			wanderCenter = ws;
		}
		else
		{
			wanderCenter = startPos;
		}

		float effectiveRange = constrainToWorkstation ? Math.Min(wanderRange, workstationRadius) : wanderRange;
		int num = 10;
		for (int i = 0; i < num; i++)
		{
			double num2 = entity.World.Rand.NextDouble() * Math.PI * 2.0;
			double num3 = 2.0 + entity.World.Rand.NextDouble() * (double)effectiveRange;
			double num4 = Math.Cos(num2) * num3;
			double num5 = Math.Sin(num2) * num3;
			BlockPos blockPos = wanderCenter.AddCopy((int)num4, 0, (int)num5);
			for (int num6 = 5; num6 >= -5; num6--)
			{
				BlockPos blockPos2 = blockPos.AddCopy(0, num6, 0);
				Block block = entity.World.BlockAccessor.GetBlock(blockPos2);
				Block block2 = entity.World.BlockAccessor.GetBlock(blockPos2.UpCopy());
				if (block.SideSolid[BlockFacing.UP.Index] && (block2.CollisionBoxes == null || block2.CollisionBoxes.Length == 0))
				{
					blockPos = blockPos2.UpCopy();
					break;
				}
			}
			pathfinder.blockAccessor.Begin();
			pathfinder.SetEntityCollisionBox(entity);
			currentPath = pathfinder.FindPath(startPos, blockPos, 500);
			pathfinder.blockAccessor.Commit();
			if (currentPath != null && currentPath.Count > 5)
			{
				targetPos = blockPos.ToVec3d().Add(0.5, 0.0, 0.5);
				currentPathIndex = 0;
				stuck = false;
				lastPosition = ((Entity)entity).ServerPos.XYZ.Clone();
				stuckCheckTime = entity.World.ElapsedMilliseconds;
				timesStuck = 0;
				entity.World.Logger.Debug("Villager found wander path with " + currentPath.Count + " nodes to " + blockPos.ToString());
				break;
			}
		}
		if (currentPath == null || currentPath.Count <= 5)
		{
			entity.World.Logger.Debug("Villager could not find valid wander destination after " + num + " attempts");
			stuck = true;
		}
	}

	public override bool ContinueExecute(float dt)
	{
		CheckIfStuck();
		if (targetPos == null || stuck || currentPath == null)
		{
			return false;
		}
		if (duringDayTimeFrames != null && duringDayTimeFrames.Length != 0 && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
		{
			entity.World.Logger.Debug("Wander: Time window ended, stopping");
			return false;
		}
		if (currentPathIndex >= currentPath.Count)
		{
			entity.World.Logger.Debug("Villager reached wander destination");
			return false;
		}
		HandlePathTraversal();
		double num = ((Entity)entity).ServerPos.SquareDistanceTo(targetPos);
		return num > 2.0;
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
				entity.World.Logger.Error("Villager failed to toggle door: " + ex.Message);
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

	private void HandlePathTraversal()
	{
		if (currentPath == null || currentPathIndex >= currentPath.Count)
		{
			stuck = true;
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
					entity.World.Logger.Debug("Villager opening door at " + villagerPathNode2.BlockPos.ToString());
					ToggleDoor(opened: true, villagerPathNode2.BlockPos);
				}
			}
			if (villagerPathNode.IsDoor)
			{
				entity.World.Logger.Debug("Villager closing door at " + villagerPathNode.BlockPos.ToString());
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
				entity.World.Logger.Warning("Wander: Villager stuck! Distance: " + num.ToString("F2") + " (count: " + timesStuck + ")");
				if (timesStuck <= 5)
				{
					long num2 = elapsedMilliseconds - lastRepathTime;
					if (num2 > 5000)
					{
						entity.World.Logger.Notification("Wander recovery " + timesStuck + ": Re-pathing");
						AttemptRepath();
						lastRepathTime = elapsedMilliseconds;
					}
				}
				else if (timesStuck >= 6)
				{
					entity.World.Logger.Notification("Wander recovery FINAL: Teleporting");
					TeleportToRecoveryPosition();
					timesStuck = 0;
				}
			}
			else
			{
				if (timesStuck > 0)
				{
					entity.World.Logger.Debug("Wander: Moving again! Distance: " + num.ToString("F2"));
				}
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
			entity.World.Logger.Debug("Wander: Finding new path");
			pathfinder.blockAccessor.Begin();
			pathfinder.SetEntityCollisionBox(entity);
			BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
			BlockPos asBlockPos = targetPos.AsBlockPos;
			List<VillagerPathNode> list = pathfinder.FindPath(startPos, asBlockPos, 500);
			pathfinder.blockAccessor.Commit();
			if (list != null && list.Count > 0)
			{
				entity.World.Logger.Notification("Wander: New path with " + list.Count + " nodes");
				currentPath = list;
				currentPathIndex = 0;
				stuck = false;
			}
			else
			{
				entity.World.Logger.Warning("Wander: No alternative path found");
			}
		}
	}

	private void TeleportToRecoveryPosition()
	{
		Vec3d vec3d = null;
		if (currentPath != null && currentPathIndex < currentPath.Count)
		{
			int num = Math.Min(2, currentPath.Count - currentPathIndex - 1);
			if (num > 0)
			{
				VillagerPathNode villagerPathNode = currentPath[currentPathIndex + num];
				vec3d = villagerPathNode.BlockPos.ToVec3d().Add(0.5, 0.1, 0.5);
				currentPathIndex += num;
				entity.World.Logger.Notification("Wander: Teleporting " + num + " nodes ahead");
			}
		}
		if (vec3d == null && targetPos != null)
		{
			Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
			Vec3d vec3d2 = targetPos.Clone().Sub(xYZ).Normalize();
			vec3d = xYZ.Add(vec3d2.X * 2.0, 0.5, vec3d2.Z * 2.0);
			entity.World.Logger.Notification("Wander: Teleporting toward target");
		}
		if (vec3d != null)
		{
			IBlockAccessor blockAccessor = entity.World.BlockAccessor;
			BlockPos asBlockPos = vec3d.AsBlockPos;
			Block block = blockAccessor.GetBlock(asBlockPos);
			Block block2 = blockAccessor.GetBlock(asBlockPos.UpCopy());
			bool flag = block.CollisionBoxes == null || block.CollisionBoxes.Length == 0;
			bool flag2 = block2.CollisionBoxes == null || block2.CollisionBoxes.Length == 0;
			if (flag && flag2)
			{
				entity.TeleportTo(vec3d);
				entity.World.Logger.Notification("Wander: Teleported to " + vec3d.ToString());
				((Entity)entity).ServerPos.Motion.Y = 0.1;
			}
			else
			{
				entity.World.Logger.Warning("Wander: Teleport target unsafe");
				stuck = true;
			}
		}
	}
}
