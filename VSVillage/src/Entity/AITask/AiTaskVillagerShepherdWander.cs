using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Shepherd-specific wander that paths toward nearby livestock and troughs
/// instead of random destinations, creating the illusion of active herding.
/// </summary>
public class AiTaskVillagerShepherdWander : AiTaskBase
{
	private Vec3d targetPos;

	private float moveSpeed = 0.008f;

	private float wanderRange = 25f;

	private VillagerAStarNew pathfinder;

	private List<VillagerPathNode> currentPath;

	private int currentPathIndex;

	private bool stuck;

	private Vec3d lastPosition;

	private long stuckCheckTime;

	private int timesStuck;

	private static readonly string[] livestockPrefixes = { "pig", "sheep", "cow", "goat", "chicken", "turkey", "hen", "rooster", "ram", "ewe", "lamb" };

	public AiTaskVillagerShepherdWander(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		if (taskConfig["moveSpeed"] != null)
		{
			moveSpeed = taskConfig["moveSpeed"].AsFloat(0.008f);
		}
		if (taskConfig["wanderRange"] != null)
		{
			wanderRange = taskConfig["wanderRange"].AsFloat(25f);
		}
		pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
	}

	public override bool ShouldExecute()
	{
		if (!IsShepherd())
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
		return entity.World.Rand.NextDouble() < 0.65;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		targetPos = null;
		stuck = false;
		currentPath = null;
		currentPathIndex = 0;
		timesStuck = 0;

		// 60% chance to head toward livestock, 40% toward a trough
		if (entity.World.Rand.NextDouble() < 0.6)
		{
			targetPos = FindNearbyLivestock();
		}
		if (targetPos == null)
		{
			targetPos = FindNearbyTrough();
		}
		if (targetPos == null)
		{
			return;
		}

		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(((Entity)entity).ServerPos.XYZ);
		currentPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, 800);
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
		if (((Entity)entity).ServerPos.SquareDistanceTo(targetPos) < 3.0)
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
		targetPos = null;
		currentPath = null;
		currentPathIndex = 0;
		lastPosition = null;
		timesStuck = 0;
	}

	private Vec3d FindNearbyLivestock()
	{
		Entity[] nearby = entity.World.GetEntitiesAround(
			((Entity)entity).ServerPos.XYZ, wanderRange, 6f,
			e => e != entity && e.Alive && IsLivestock(e)
		);
		if (nearby.Length == 0)
		{
			return null;
		}

		Entity target = nearby[entity.World.Rand.Next(nearby.Length)];
		Vec3d animalPos = target.ServerPos.XYZ;

		// Aim near the animal, not exactly on it
		return new Vec3d(
			animalPos.X + (entity.World.Rand.NextDouble() - 0.5) * 4.0,
			animalPos.Y,
			animalPos.Z + (entity.World.Rand.NextDouble() - 0.5) * 4.0
		);
	}

	private Vec3d FindNearbyTrough()
	{
		IPointOfInterest poi = entity.Api.ModLoader.GetModSystem<POIRegistry>()
			.GetNearestPoi(((Entity)entity).ServerPos.XYZ, wanderRange, p => p is BlockEntityTrough);
		return poi?.Position;
	}

	private bool IsLivestock(Entity e)
	{
		if (e?.Code?.Path == null)
		{
			return false;
		}
		string path = e.Code.Path;
		foreach (string prefix in livestockPrefixes)
		{
			if (path.StartsWith(prefix))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsShepherd()
	{
		return entity?.Code?.Path?.EndsWith("-shepherd") == true;
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
		}

		if (currentPathIndex < currentPath.Count)
		{
			VillagerPathNode nextNode = currentPath[currentPathIndex];
			Vec3d nextPos = nextNode.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
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
			timesStuck++;
			if (timesStuck >= 4)
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
