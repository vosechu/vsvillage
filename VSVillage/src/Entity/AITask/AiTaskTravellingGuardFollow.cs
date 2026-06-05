using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskTravellingGuardFollow : AiTaskBase
{
	private const float MoveSpeed = 0.01f;

	private const float FollowDist = 2.5f;

	private const int RepathMs = 3000;

	private const int StuckIntervalMs = 3000;

	private const int MaxTimesStuck = 3;

	private VillagerAStarNew _pathfinder;

	private List<VillagerPathNode> _path;

	private int _pathIdx;

	private Vec3d _target;

	private bool _stuck;

	private Vec3d _lastPos;

	private long _stuckCheckTime;

	private int _timesStuck;

	private long _lastRepathTime;

	public AiTaskTravellingGuardFollow(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		_pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false), entity.World, entity);
	}

	public override bool ShouldExecute()
	{
		if (entity.Api.Side != EnumAppSide.Server)
		{
			return false;
		}
		EntityBehaviorTravellingGuard beh = entity.GetBehavior<EntityBehaviorTravellingGuard>();
		if (beh == null || beh.IsTraderAtStall)
		{
			return false;
		}
		Entity trader = beh.TraderEntity;
		if (trader == null || !trader.Alive)
		{
			return false;
		}
		return cooldownUntilMs <= entity.World.ElapsedMilliseconds;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		_stuck = false;
		_path = null;
		_pathIdx = 0;
		_timesStuck = 0;
		_target = null;
		_lastRepathTime = 0L;
		BuildPath();
	}

	public override bool ContinueExecute(float dt)
	{
		if (_stuck)
		{
			return false;
		}
		EntityBehaviorTravellingGuard beh = entity.GetBehavior<EntityBehaviorTravellingGuard>();
		if (beh == null || beh.IsTraderAtStall)
		{
			return false;
		}
		Entity trader = beh.TraderEntity;
		if (trader == null || !trader.Alive)
		{
			return false;
		}
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d traderPos = trader.Pos.XYZ;
		if (myPos.DistanceTo(traderPos) < 2.5f)
		{
			entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
			entity.Controls.StopAllMovement();
			if (animMeta != null)
			{
				entity.AnimManager.StopAnimation(animMeta.Code);
			}
			return true;
		}
		long now = entity.World.ElapsedMilliseconds;
		// Repath every 3s only when the trader has actually moved more than a block from the path target. Idle traders no longer trigger fresh A* calls.
		if (now - _lastRepathTime > 3000 && (_target == null || traderPos.SquareDistanceTo(_target) > 1.0))
		{
			_lastRepathTime = now;
			BuildPath();
			if (_stuck)
			{
				return false;
			}
		}
		CheckIfStuck();
		if (_stuck)
		{
			return false;
		}
		if (_path == null || _pathIdx >= _path.Count)
		{
			BuildPath();
			return !_stuck;
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
		_target = null;
		_timesStuck = 0;
	}

	private void BuildPath()
	{
		Entity trader = entity.GetBehavior<EntityBehaviorTravellingGuard>()?.TraderEntity;
		if (trader == null)
		{
			_stuck = true;
			return;
		}
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d traderPos = trader.Pos.XYZ;
		Vec3d diff = traderPos.Clone().Sub(myPos);
		diff.Y = 0.0;
		double len = diff.Length();
		Vec3d behindPos = ((len > 0.1) ? traderPos.Clone().Sub(diff.Normalize().Mul(2.5)) : traderPos.Clone().Add(2.0, 0.0, 0.0));
		_pathfinder.blockAccessor.Begin();
		_pathfinder.SetEntityCollisionBox(entity);
		BlockPos start = _pathfinder.GetStartPos(myPos);
		// 5000 (was 1500): the lower budget couldn't reach a bridge route past
		// the wading "easy" path, so the pathfinder gave up on the dry detour
		// and committed to wading across streams even when a bridge existed.
		// Matches VillagerPathfind.FindPath's default and other long-haul
		// villager tasks.
		_path = _pathfinder.FindPath(start, behindPos.AsBlockPos, 5000);
		_pathfinder.blockAccessor.Commit();
		if (_path == null || _path.Count == 0)
		{
			entity.World.Logger.Debug($"[TG:{entity.EntityId}] GuardFollow: no path to trader.");
			_stuck = true;
		}
		else
		{
			_target = behindPos;
			_pathIdx = 0;
			_stuck = false;
			_lastPos = myPos.Clone();
			_stuckCheckTime = entity.World.ElapsedMilliseconds;
		}
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
			if (++_timesStuck >= 3)
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
