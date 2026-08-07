using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Stands a soldier or archer at their assigned guard post for a whole shift. The shift window is the
// JSON duringDayTimeFrames; guardShift picks which assignment this entry serves.
public class AiTaskVillagerGuardPost : AiTaskGotoAndInteract
{
	private readonly EnumGuardShift configuredShift;

	protected override bool HoldAtTarget => true;

	public AiTaskVillagerGuardPost(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		configuredShift = GuardDuty.ParseShift(taskConfig["guardShift"].AsString(null));
	}

	// Gate here rather than only in GetTargetPos: targetPos is sticky between the base class's
	// targetSearchIntervalMs refreshes, so a null target alone would leave the task firing for ~2s.
	public override bool ShouldExecute()
	{
		if (configuredShift == EnumGuardShift.none) return false;
		if (GuardDuty.ShiftOf(entity) != configuredShift) return false;
		return base.ShouldExecute();
	}

	// The base never re-checks duringDayTimeFrames while running, so without this the shift would
	// overrun by up to maxtaskduration.
	public override bool ContinueExecute(float dt)
	{
		if (!IsInValidDayTimeHours(initialRandomness: false)) return false;
		if (GuardDuty.ShiftOf(entity) != configuredShift) return false;
		return base.ContinueExecute(dt);
	}

	protected override Vec3d GetTargetPos()
	{
		BlockPos post = GuardDuty.ValidateAssignment(entity, configuredShift);
		if (post == null) return null;

		// Never returns null when already in place; that would stop the task holding the post.
		return GuardDuty.FindStandingPosNear(entity.World.BlockAccessor, post, entity.Pos.XYZ);
	}
}
