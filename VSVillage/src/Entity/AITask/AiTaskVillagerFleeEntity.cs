using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Villager flee task that uses <see cref="VillagerAStarNew"/> so villagers can
/// navigate through gates, around fences, and across uneven terrain while panicking
/// — instead of getting wedged against the first fence post they touch.
///
/// Replaces the vanilla <c>fleeentity</c> task for villagers.  Movement pattern is
/// identical to <see cref="AiTaskVillagerShepherdWander"/>: direct A* path traversal
/// with a stuck-detection loop that re-paths at a different angle rather than giving up.
///
/// JSON config keys:
///   entityCodes           — list of entity code patterns to flee from (supports trailing *).
///   excludeEntitySuffixes — villager types that skip this task (e.g. ["-soldier","-archer"]).
///   seekingRange          — radius to notice a threat (default 20).
///   fleeingDistance       — distance at which we consider ourselves safe (default 30).
///   movespeed             — movement speed while fleeing (default 0.015).
///   fleeDurationMs        — hard cap on one flee episode in ms (default 12000).
/// </summary>
public class AiTaskVillagerFleeEntity : AiTaskBase
{
	// ── Config ────────────────────────────────────────────────────────────────
	private readonly List<string> threatCodes       = new List<string>();
	private readonly List<string> excludeSuffixes   = new List<string>();
	private float seekingRange    = 20f;
	private float fleeingDistance = 30f;
	private float moveSpeed       = 0.015f;
	private long  fleeDurationMs  = 12000L;

	// ── Runtime state ─────────────────────────────────────────────────────────
	private Entity                threat;
	private List<VillagerPathNode> currentPath;
	private int                   pathIndex;
	private VillagerAStarNew      pathfinder;
	private long                  fleeStartMs;
	private bool                  stuck;
	private Vec3d                 lastPos;
	private long                  stuckCheckMs;
	private int                   timesStuck;
	private long                  lastRepathMs;

	private const long RepathIntervalMs = 1500L;
	private const long StuckCheckMs     = 2000L;

	// ── Constructor ───────────────────────────────────────────────────────────

	public AiTaskVillagerFleeEntity(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		seekingRange    = taskConfig["seekingRange"].AsFloat(20f);
		fleeingDistance = taskConfig["fleeingDistance"].AsFloat(30f);
		moveSpeed       = taskConfig["movespeed"].AsFloat(0.015f);
		fleeDurationMs  = taskConfig["fleeDurationMs"].AsInt(12000);

		JsonObject[] codes = taskConfig["entityCodes"].AsArray();
		if (codes != null)
			foreach (JsonObject c in codes)
			{
				string s = c.AsString();
				if (!string.IsNullOrEmpty(s)) threatCodes.Add(s);
			}

		JsonObject exclNode = taskConfig["excludeEntitySuffixes"];
		if (exclNode != null && exclNode.Exists)
		{
			string[] excl = exclNode.AsArray(System.Array.Empty<string>());
			excludeSuffixes.AddRange(excl);
		}

		pathfinder = new VillagerAStarNew(
			entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
	}

	// ── ShouldExecute ─────────────────────────────────────────────────────────

	public override bool ShouldExecute()
	{
		if (threatCodes.Count == 0) return false;
		if (cooldownUntilMs > entity.World.ElapsedMilliseconds) return false;

		// Skip for entity types that never flee (soldiers, archers).
		string myPath = entity.Code?.Path ?? "";
		foreach (string sfx in excludeSuffixes)
			if (myPath.EndsWith(sfx)) return false;

		threat = entity.World.GetNearestEntity(
			entity.Pos.XYZ, seekingRange, seekingRange * 0.5f,
			e => e != entity && e.Alive && e.IsInteractable && MatchesThreat(e));

		return threat != null;
	}

	// ── StartExecute ──────────────────────────────────────────────────────────

	public override void StartExecute()
	{
		base.StartExecute();
		fleeStartMs  = entity.World.ElapsedMilliseconds;
		lastRepathMs = entity.World.ElapsedMilliseconds;
		stuckCheckMs = entity.World.ElapsedMilliseconds;
		stuck        = false;
		pathIndex    = 0;
		currentPath  = null;
		timesStuck   = 0;
		lastPos      = entity.Pos.XYZ.Clone();

		ComputeAndPath();
	}

	// ── ContinueExecute ───────────────────────────────────────────────────────

	public override bool ContinueExecute(float dt)
	{
		if (currentPath == null || stuck) return false;

		long now = entity.World.ElapsedMilliseconds;

		if (now - fleeStartMs > fleeDurationMs) return false;
		if (threat == null || !threat.Alive) return false;
		if (entity.Pos.SquareDistanceTo(threat.Pos) > fleeingDistance * fleeingDistance)
			return false;

		// Repath periodically so we keep running away as the threat moves.
		if (now - lastRepathMs > RepathIntervalMs)
		{
			lastRepathMs = now;
			ComputeAndPath();
			if (stuck) return false;
		}

		CheckIfStuck();
		if (stuck) return false;

		// Reached the end of the current path — immediately plan a new leg.
		if (pathIndex >= currentPath.Count)
		{
			ComputeAndPath();
			if (stuck) return false;
		}

		HandlePathTraversal();
		return true;
	}

	// ── FinishExecute ─────────────────────────────────────────────────────────

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		entity.Controls.StopAllMovement();
		if (animMeta != null)
			entity.AnimManager.StopAnimation(animMeta.Code);
		entity.Pos.Motion.X = 0.0;
		entity.Pos.Motion.Z = 0.0;
		threat      = null;
		currentPath = null;
		pathIndex   = 0;
		timesStuck  = 0;
		lastPos     = null;
	}

	// ── Pathfinding ───────────────────────────────────────────────────────────

	/// <summary>
	/// Computes a flee destination in the direction away from the threat, then runs
	/// A* to it.  Tries five angles (0°, ±45°, ±90°) so that a blocked primary
	/// direction doesn't leave the villager frozen — they'll curve around fences
	/// rather than slamming into them.
	/// </summary>
	private void ComputeAndPath()
	{
		if (threat == null) { stuck = true; return; }

		Vec3d myPos     = entity.Pos.XYZ;
		Vec3d threatPos = threat.Pos.XYZ;

		// Unit vector pointing away from the threat.
		double dx   = myPos.X - threatPos.X;
		double dz   = myPos.Z - threatPos.Z;
		double dist = Math.Sqrt(dx * dx + dz * dz);
		if (dist < 0.01) { dx = 1; dz = 0; } else { dx /= dist; dz /= dist; }

		// How far each A* leg should reach — run 75% of fleeingDistance per leg so
		// the villager keeps making progress without needing enormous search depths.
		float legDist = fleeingDistance * 0.75f;

		// Try angles in order: straight back, then slight curves, then wide curves.
		float[] angles = { 0f, -0.785f, 0.785f, -1.571f, 1.571f };

		pathfinder.blockAccessor.Begin();
		pathfinder.SetEntityCollisionBox(entity);
		BlockPos startPos = pathfinder.GetStartPos(myPos);

		foreach (float angle in angles)
		{
			double cos = Math.Cos(angle);
			double sin = Math.Sin(angle);
			double fdx = dx * cos - dz * sin;
			double fdz = dx * sin + dz * cos;

			BlockPos dest = new BlockPos(
				(int)(myPos.X + fdx * legDist),
				(int)myPos.Y,
				(int)(myPos.Z + fdz * legDist));

			// 1200 iterations per attempt — fast enough for real-time flee, handles
			// typical pen/fence layouts without excessive CPU cost.
			List<VillagerPathNode> path = pathfinder.FindPath(startPos, dest, 1200);
			if (path != null && path.Count > 1)
			{
				pathfinder.blockAccessor.Commit();
				currentPath = path;
				pathIndex   = 0;
				stuck       = false;
				return;
			}
		}

		pathfinder.blockAccessor.Commit();
		// No walkable angle found — mark stuck so ContinueExecute exits gracefully.
		stuck = true;
	}

	// ── Movement (same pattern as AiTaskVillagerShepherdWander) ──────────────

	private void HandlePathTraversal()
	{
		if (currentPath == null || pathIndex >= currentPath.Count) { stuck = true; return; }

		VillagerPathNode node    = currentPath[pathIndex];
		Vec3d            nodePos = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
		Vec3d            myPos   = entity.Pos.XYZ;

		double dx = myPos.X - nodePos.X;
		double dz = myPos.Z - nodePos.Z;
		if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
			pathIndex++;

		if (pathIndex < currentPath.Count)
		{
			VillagerPathNode next    = currentPath[pathIndex];
			Vec3d            nextPos = next.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
			Vec3d            dir     = nextPos.Clone().Sub(myPos);
			dir.Y = 0.0;
			dir   = dir.Normalize();

			entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
			entity.Controls.WalkVector.Set(dir.X * moveSpeed, 0.0, dir.Z * moveSpeed);

			if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
				entity.AnimManager.StartAnimation(animMeta);
		}
	}

	private void CheckIfStuck()
	{
		long now = entity.World.ElapsedMilliseconds;
		if (now - stuckCheckMs < StuckCheckMs) return;

		Vec3d myPos = entity.Pos.XYZ;
		if (lastPos != null && myPos.DistanceTo(lastPos) < 0.3f)
		{
			timesStuck++;
			if (timesStuck >= 3)
			{
				timesStuck = 0;
				// Try a fresh path at a different angle before giving up.
				ComputeAndPath();
			}
		}
		else
		{
			timesStuck = 0;
		}

		lastPos      = myPos.Clone();
		stuckCheckMs = now;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private bool MatchesThreat(Entity e)
	{
		string path = e.Code?.Path ?? "";
		foreach (string pattern in threatCodes)
		{
			if (pattern.EndsWith("*"))
			{
				if (path.StartsWith(pattern.Substring(0, pattern.Length - 1),
				    StringComparison.OrdinalIgnoreCase)) return true;
			}
			else if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}
}
