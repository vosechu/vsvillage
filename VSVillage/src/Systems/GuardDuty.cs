using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VsVillage;

public enum EnumGuardShift
{
	none,
	day,
	night
}

public static class GuardDuty
{
	public static EnumGuardShift ParseShift(string value)
	{
		return value switch
		{
			"day" => EnumGuardShift.day,
			"night" => EnumGuardShift.night,
			_ => EnumGuardShift.none,
		};
	}

	// JSON "guardShift" gate. Null means the entry has no gate at all, which is what keeps every
	// pre-existing task entry (combat, gotowork, professions) firing for assigned guards untouched.
	public static EnumGuardShift? ParseGate(string value)
	{
		if (string.IsNullOrEmpty(value)) return null;
		if (value == "unassigned") return EnumGuardShift.none;
		if (value == "day") return EnumGuardShift.day;
		if (value == "night") return EnumGuardShift.night;
		return null;
	}

	public static bool GatePasses(EntityAgent entity, EnumGuardShift? gate)
	{
		if (gate == null) return true;
		return ShiftOf(entity) == gate.Value;
	}

	// Archers resolve to the soldier profession here via villager.json professionByType, so one value
	// covers both. Sakura keeps archer as its own profession and tests for both.
	public static bool CanStandWatch(EnumVillagerProfession profession)
	{
		return profession == EnumVillagerProfession.soldier;
	}

	// Both post and shift must be set, otherwise a half-written assignment would read as on-duty.
	public static EnumGuardShift ShiftOf(EntityAgent entity)
	{
		EntityBehaviorVillager beh = entity?.GetBehavior<EntityBehaviorVillager>();
		if (beh?.GuardPost == null) return EnumGuardShift.none;
		return beh.GuardShift;
	}

	public static BlockPos PostOf(EntityAgent entity)
	{
		return entity?.GetBehavior<EntityBehaviorVillager>()?.GuardPost;
	}

	// Self-heal for a post that vanished while its guard was unloaded (broken, ghost-scrubbed, reassigned).
	// Returns the live post, or null after clearing the stale assignment.
	public static BlockPos ValidateAssignment(EntityAgent entity, EnumGuardShift shift)
	{
		EntityBehaviorVillager beh = entity?.GetBehavior<EntityBehaviorVillager>();
		BlockPos post = beh?.GuardPost;
		if (post == null) return null;

		// Village unresolved is a load-order race, not a dead post. Clearing here would wipe live state.
		Village village = beh.Village;
		if (village == null) return post;

		if (village.GuardPosts.TryGetValue(post, out VillagerGuardPost gp) && gp.OwnerFor(shift) == entity.EntityId)
			return post;

		beh.GuardPost = null;
		beh.GuardShift = EnumGuardShift.none;
		return null;
	}

	// Nearest clear grounded cell horizontally adjacent to the post. The post block's own collision
	// is not assumed walkable, so guards stand beside it rather than on it.
	public static Vec3d FindStandingPosNear(IBlockAccessor ba, BlockPos post, Vec3d from)
	{
		if (ba == null || post == null) return null;

		Vec3d bestPos = null;
		double bestDist = double.MaxValue;

		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos nb = post.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			Vec3d candidate = FindGroundedStandingPos(ba, nb, scanDepth: 1);
			if (candidate == null) continue;

			double dist = from == null ? 0.0 : candidate.SquareDistanceTo(from);
			if (dist < bestDist) { bestDist = dist; bestPos = candidate; }
		}

		return bestPos ?? post.ToVec3d().Add(0.5, 0.0, 0.5);
	}

	// Scan down from origin up to scanDepth blocks; first clear+headclear+grounded wins.
	// Mirrors AiTaskVillagerSleep.FindGroundedStandingPos (villager step height 1.2 = 1 block).
	private static Vec3d FindGroundedStandingPos(IBlockAccessor ba, BlockPos origin, int scanDepth)
	{
		for (int dy = 0; dy >= -scanDepth; dy--)
		{
			BlockPos check = origin.AddCopy(0, dy, 0);

			bool posClear = ba.GetBlock(check).CollisionBoxes == null
			              || ba.GetBlock(check).CollisionBoxes.Length == 0;
			bool headClear = ba.GetBlock(check.UpCopy()).CollisionBoxes == null
			              || ba.GetBlock(check.UpCopy()).CollisionBoxes.Length == 0;
			bool grounded = ba.GetBlock(check.DownCopy()).CollisionBoxes != null
			              && ba.GetBlock(check.DownCopy()).CollisionBoxes.Length != 0;

			if (posClear && headClear && grounded)
				return check.ToVec3d().Add(0.5, 0.0, 0.5);
		}
		return null;
	}
}
