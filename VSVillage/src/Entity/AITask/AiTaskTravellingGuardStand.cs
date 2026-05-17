using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskTravellingGuardStand : AiTaskBase
{
	private const float MoveSpeed = 0.009f;

	private const double ArrivalSq = 2.25;

	private const int PostCooldownMs = 90000;

	private const int StuckIntervalMs = 3000;

	private const int MaxTimesStuck = 4;

	private VillagerAStarNew _pathfinder;

	private List<VillagerPathNode> _path;

	private int _pathIdx;

	private Vec3d _target;

	private bool _stuck;

	private bool _atPost;

	private Vec3d _lastPos;

	private long _stuckCheckTime;

	private int _timesStuck;

	private long _arrivedAt;

	public AiTaskTravellingGuardStand(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		_pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
	}

	public override bool ShouldExecute()
	{
		if (entity.Api.Side != EnumAppSide.Server)
		{
			return false;
		}
		EntityBehaviorTravellingGuard beh = entity.GetBehavior<EntityBehaviorTravellingGuard>();
		if (beh == null || !beh.IsTraderAtStall)
		{
			return false;
		}
		if (_atPost && entity.World.ElapsedMilliseconds < _arrivedAt + 90000)
		{
			return false;
		}
		return true;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		_stuck = false;
		_atPost = false;
		_path = null;
		_pathIdx = 0;
		_timesStuck = 0;
		_target = null;
		BlockPos stallPos = entity.GetBehavior<EntityBehaviorTravellingGuard>()?.MarketStallPos;
		if (stallPos == null)
		{
			_stuck = true;
			return;
		}
		_target = FindAdjacentPos(stallPos);
		if (_target == null)
		{
			entity.World.Logger.Debug($"[TG:{entity.EntityId}] GuardStand: no adjacent post found near {stallPos}.");
			_stuck = true;
			return;
		}
		if (entity.Pos.SquareDistanceTo(_target) < 2.25)
		{
			ArriveAtPost();
			return;
		}
		_pathfinder.blockAccessor.Begin();
		_pathfinder.SetEntityCollisionBox(entity);
		BlockPos start = _pathfinder.GetStartPos(entity.Pos.XYZ);
		_path = _pathfinder.FindPath(start, _target.AsBlockPos, 2000);
		_pathfinder.blockAccessor.Commit();
		if (_path == null || _path.Count == 0)
		{
			entity.World.Logger.Warning($"[TG:{entity.EntityId}] GuardStand: no path to post {_target}.");
			_stuck = true;
		}
		else
		{
			entity.World.Logger.Debug($"[TG:{entity.EntityId}] GuardStand: pathing to post ({_path.Count} nodes).");
			_pathIdx = 0;
			_lastPos = entity.Pos.XYZ.Clone();
			_stuckCheckTime = entity.World.ElapsedMilliseconds;
		}
	}

	public override bool ContinueExecute(float dt)
	{
		if (_atPost || _stuck || _path == null || _target == null)
		{
			return false;
		}
		CheckIfStuck();
		if (_stuck)
		{
			return false;
		}
		if (_pathIdx >= _path.Count || entity.Pos.SquareDistanceTo(_target) < 2.25)
		{
			ArriveAtPost();
			return false;
		}
		StepPath();
		return true;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		entity.Pos.Motion.Set(0.0, 0.0, 0.0);
		if (animMeta != null)
		{
			entity.AnimManager.StopAnimation(animMeta.Code);
		}
		DoorPathHelper.CloseOpenDoorsAlongPath(entity, _path);
		_path = null;
		_timesStuck = 0;
	}

	private void ArriveAtPost()
	{
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		if (animMeta != null)
		{
			entity.AnimManager.StopAnimation(animMeta.Code);
		}
		_atPost = true;
		_arrivedAt = entity.World.ElapsedMilliseconds;
		entity.World.Logger.Debug($"[TG:{entity.EntityId}] GuardStand: arrived at post.");
	}

	private Vec3d FindAdjacentPos(BlockPos stall)
	{
		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockFacing[] hORIZONTALS = BlockFacing.HORIZONTALS;
		foreach (BlockFacing facing in hORIZONTALS)
		{
			BlockPos nb = stall.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			Block atPos = ba.GetBlock(nb);
			Block above = ba.GetBlock(nb.UpCopy());
			Block below = ba.GetBlock(nb.DownCopy());
			if ((atPos.CollisionBoxes == null || atPos.CollisionBoxes.Length == 0) && (above.CollisionBoxes == null || above.CollisionBoxes.Length == 0) && below.CollisionBoxes != null && below.CollisionBoxes.Length != 0)
			{
				return nb.ToVec3d().Add(0.5, 0.0, 0.5);
			}
		}
		return stall.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	private void StepPath()
	{
		if (_path == null || _pathIdx >= _path.Count)
		{
			_stuck = true;
			return;
		}
		VillagerPathNode node = _path[_pathIdx];
		Vec3d centre = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d myPos = entity.Pos.XYZ;
		double dx = myPos.X - centre.X;
		double dz = myPos.Z - centre.Z;
		if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
		{
			_pathIdx++;
			if (_pathIdx < _path.Count && _path[_pathIdx].IsDoor)
			{
				DoorPathHelper.ToggleDoor(entity, _path[_pathIdx].BlockPos, opened: true);
			}
			if (node.IsDoor)
			{
				BlockPos dp = node.BlockPos.Copy();
				DoorPathHelper.ScheduleDoorClose(entity, dp, 5000);
			}
		}
		if (_pathIdx < _path.Count)
		{
			Vec3d next = _path[_pathIdx].BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
			Vec3d dir = next.Clone().Sub(myPos);
			dir.Y = 0.0;
			dir = dir.Normalize();
			entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
			entity.Controls.WalkVector.Set(dir.X * MoveSpeed, 0.0, dir.Z * MoveSpeed);
			if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
			{
				entity.AnimManager.StartAnimation(animMeta);
			}
		}
	}

	private void CheckIfStuck()
	{
		long now = entity.World.ElapsedMilliseconds;
		if (now - _stuckCheckTime < 3000)
		{
			return;
		}
		Vec3d myPos = entity.Pos.XYZ;
		if (_lastPos != null && (double)myPos.DistanceTo(_lastPos) < 0.4)
		{
			if (++_timesStuck >= 4)
			{
				_stuck = true;
				_timesStuck = 0;
			}
		}
		else
		{
			_timesStuck = 0;
		}
		_lastPos = myPos.Clone();
		_stuckCheckTime = now;
	}

}
