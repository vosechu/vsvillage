using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public abstract class AiTaskGotoAndInteract : AiTaskBase
{
	protected float moveSpeed;

	protected long lastSearch;

	protected long lastExecution;

	protected bool stuck;

	protected Vec3d targetPos;

	protected AnimationMetaData interactAnim;

	protected bool targetReached;

	protected List<VillagerPathNode> currentPath;

	protected int currentPathIndex;

	protected VillagerAStarNew pathfinder;

	protected Vec3d lastPosition;

	protected long stuckCheckTime;

	protected int timesStuck;

	protected long lastRepathTime;

	protected BlockPos currentlyOpeningDoor;

	protected long doorOpenedTime;

	private int separationCheckTick = 0;

	protected float maxDistance { get; set; }

	public AiTaskGotoAndInteract(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		maxDistance = taskConfig["maxdistance"].AsFloat(5f);
		moveSpeed = taskConfig["movespeed"].AsFloat(0.08f);
		interactAnim = new AnimationMetaData
		{
			Code = "interact",
			Animation = taskConfig["interact"].AsString("interact")
		}.Init();
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
		lastPosition = null;
		stuckCheckTime = 0L;
		timesStuck = 0;
		lastRepathTime = 0L;
		currentlyOpeningDoor = null;
		doorOpenedTime = 0L;
	}

	public override bool ShouldExecute()
	{
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		if (2000 + lastSearch < elapsedMilliseconds && cooldownUntilMs + lastExecution < elapsedMilliseconds)
		{
			lastSearch = elapsedMilliseconds;
			targetPos = GetTargetPos();
		}
		return targetPos != null && cooldownUntilMs + lastExecution < elapsedMilliseconds;
	}

	protected abstract Vec3d GetTargetPos();

	public override void StartExecute()
	{
		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
		BlockPos asBlockPos = targetPos.AsBlockPos;
		currentPath = pathfinder.FindPath(startPos, asBlockPos);
		pathfinder.blockAccessor.Commit();
		if (currentPath != null && currentPath.Count > 0)
		{
			entity.World.Logger.Debug("Found path with " + currentPath.Count + " nodes");
			currentPathIndex = 0;
			stuck = false;
			targetReached = false;
			lastPosition = ((Entity)entity).ServerPos.XYZ.Clone();
			stuckCheckTime = entity.World.ElapsedMilliseconds;
			timesStuck = 0;
		}
		else
		{
			entity.World.Logger.Debug("No path found to target");
			stuck = true;
		}
		base.StartExecute();
	}

	public override bool ContinueExecute(float dt)
	{
		CheckIfStuck();
		if (targetReached)
		{
			return entity.AnimManager.IsAnimationActive(interactAnim.Code);
		}
		if (!targetReached && targetPos != null && currentPath != null)
		{
			HandlePathTraversal();
			// Throttled: check personal space every 15 ticks to push apart overlapping villagers.
			if (++separationCheckTick >= 15)
			{
				separationCheckTick = 0;
				ApplySeparationForce();
			}
		}
		if (InteractionPossible())
		{
			entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
			entity.Controls.StopAllMovement();
			entity.AnimManager.StopAnimation(animMeta.Code);
			entity.AnimManager.StartAnimation(interactAnim);
			targetReached = true;
			return true;
		}
		return !stuck && currentPath != null;
	}

	protected virtual bool InteractionPossible()
	{
		return targetPos != null && ((Entity)entity).ServerPos.SquareDistanceTo(targetPos) < 2.25;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		entity.AnimManager.StopAnimation(animMeta.Code);
		CloseAllOpenDoors();
		if (targetReached)
		{
			ApplyInteractionEffect();
			lastExecution = entity.World.ElapsedMilliseconds;
		}
		entity.AnimManager.StopAnimation("interact");
		targetPos = null;
		targetReached = false;
		currentPath = null;
		lastPosition = null;
		timesStuck = 0;
		currentlyOpeningDoor = null;
	}

	protected abstract void ApplyInteractionEffect();

	protected void ToggleDoor(bool opened, BlockPos target)
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
				entity.World.Logger.Debug("Toggled door at " + target.ToString() + " to " + (opened ? "open" : "closed"));
			}
			catch (Exception ex)
			{
				entity.World.Logger.Error("Failed to toggle door at " + target.ToString() + ": " + ex.Message);
			}
		}
	}

	private void HandlePathTraversal()
	{
		if (currentlyOpeningDoor != null)
		{
			long num = entity.World.ElapsedMilliseconds - doorOpenedTime;
			if (num < 500)
			{
				entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
				return;
			}
			currentlyOpeningDoor = null;
		}
		if (currentPath == null || currentPathIndex >= currentPath.Count)
		{
			stuck = true;
			return;
		}
		VillagerPathNode villagerPathNode = currentPath[currentPathIndex];
		Vec3d vec3d = villagerPathNode.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
		double num2 = xYZ.X - vec3d.X;
		double num3 = xYZ.Z - vec3d.Z;
		double num4 = Math.Sqrt(num2 * num2 + num3 * num3);
		if (num4 < 0.5)
		{
			currentPathIndex++;
			if (currentPathIndex < currentPath.Count)
			{
				VillagerPathNode villagerPathNode2 = currentPath[currentPathIndex];
				if (villagerPathNode2.IsDoor)
				{
					entity.World.Logger.Debug("Opening door at " + villagerPathNode2.BlockPos.ToString());
					ToggleDoor(opened: true, villagerPathNode2.BlockPos);
					currentlyOpeningDoor = villagerPathNode2.BlockPos.Copy();
					doorOpenedTime = entity.World.ElapsedMilliseconds;
				}
			}
			if (villagerPathNode.IsDoor)
			{
				entity.World.Logger.Debug("Closing door behind at " + villagerPathNode.BlockPos.ToString());
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
			double num5 = moveSpeed;
			entity.Controls.WalkVector.Set(vec3d3.X * num5, 0.0, vec3d3.Z * num5);
			if (!entity.AnimManager.IsAnimationActive(animMeta.Code))
			{
				entity.AnimManager.StartAnimation(animMeta);
			}
		}
	}

	/// <summary>
	/// Applies a gentle repulsion force away from any villager within 1 block.
	/// Prevents villagers from occupying the exact same position (visual clipping)
	/// and from interfering with each other's collision-based path start detection.
	/// </summary>
	private void ApplySeparationForce()
	{
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		Entity[] nearby = entity.World.GetEntitiesAround(myPos, 1.5f, 1.5f, e =>
			e != entity && e.Code != null && e.Code.Domain == "vsvillage"
		);
		if (nearby == null || nearby.Length == 0) return;
		foreach (Entity neighbor in nearby)
		{
			Vec3d theirPos = ((Entity)neighbor).ServerPos.XYZ;
			double dx = myPos.X - theirPos.X;
			double dz = myPos.Z - theirPos.Z;
			double distSq = dx * dx + dz * dz;
			if (distSq < 1.0 && distSq > 0.0001)
			{
				double dist = Math.Sqrt(distSq);
				double pushStrength = (1.0 - dist) * 0.05;
				entity.Controls.WalkVector.X += (dx / dist) * pushStrength;
				entity.Controls.WalkVector.Z += (dz / dist) * pushStrength;
			}
		}
	}

	protected void CloseAllOpenDoors()
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
				entity.World.Logger.Warning("Villager appears stuck! Distance moved: " + num.ToString("F2") + " (stuck count: " + timesStuck + ")");
				if (timesStuck <= 5)
				{
					long num2 = elapsedMilliseconds - lastRepathTime;
					if (num2 > 5000)
					{
						entity.World.Logger.Notification("Recovery attempt " + timesStuck + ": Re-pathing");
						AttemptRepath();
						lastRepathTime = elapsedMilliseconds;
					}
				}
				else if (timesStuck >= 6)
				{
					entity.World.Logger.Notification("Recovery attempt FINAL: Teleporting (last resort)");
					TeleportToRecoveryPosition();
					timesStuck = 0;
				}
			}
			else
			{
				if (timesStuck > 0)
				{
					entity.World.Logger.Debug("Villager is moving again! Distance: " + num.ToString("F2"));
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
			entity.World.Logger.Debug("Attempting to find new path to target");
			pathfinder.blockAccessor.Begin();
			pathfinder.SetEntityCollisionBox(entity);
			BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
			BlockPos asBlockPos = targetPos.AsBlockPos;
			List<VillagerPathNode> list = pathfinder.FindPath(startPos, asBlockPos);
			pathfinder.blockAccessor.Commit();
			if (list != null && list.Count > 0)
			{
				entity.World.Logger.Notification("Found new path with " + list.Count + " nodes");
				currentPath = list;
				currentPathIndex = 0;
				stuck = false;
			}
			else
			{
				entity.World.Logger.Warning("Could not find alternative path");
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
				entity.World.Logger.Notification("Teleporting " + num + " nodes ahead to " + vec3d.ToString());
			}
		}
		if (vec3d == null && targetPos != null)
		{
			Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
			Vec3d vec3d2 = targetPos.Clone().Sub(xYZ).Normalize();
			vec3d = xYZ.Add(vec3d2.X * 2.0, 0.5, vec3d2.Z * 2.0);
			entity.World.Logger.Notification("Teleporting toward target to " + vec3d.ToString());
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
				entity.World.Logger.Notification("Teleported villager to " + vec3d.ToString());
				((Entity)entity).ServerPos.Motion.Y = 0.1;
			}
			else
			{
				entity.World.Logger.Warning("Teleport destination " + vec3d.ToString() + " is not safe, skipping");
				stuck = true;
			}
		}
	}
}
