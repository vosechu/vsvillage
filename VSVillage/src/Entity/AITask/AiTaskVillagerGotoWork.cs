using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGotoWork : AiTaskGotoAndInteract
{
	private float offset;

	private BlockPos workstationPos;

	public AiTaskVillagerGotoWork(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-50), taskConfig["maxoffset"].AsInt(50)) / 100f;
	}

	protected override void ApplyInteractionEffect()
	{
	}

	protected override Vec3d GetTargetPos()
	{
		EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
		Village village = behavior?.Village;
		if (village == null)
		{
			return null;
		}
		BlockPos blockPos = behavior.Workstation;
		if (blockPos == null)
		{
			blockPos = (behavior.Workstation = village.FindFreeWorkstation(entity.EntityId, behavior.Profession));
		}
		else
		{
			village.Workstations.TryGetValue(blockPos, out var value);
			if (value == null || value.OwnerId != entity.EntityId)
			{
				blockPos = null;
				behavior.Workstation = null;
			}
		}
		if (blockPos != null)
		{
			workstationPos = blockPos.Copy();
		}
		return getWorkstationStandingPos((blockPos != null) ? blockPos.ToVec3d() : null);
	}

	public override bool ShouldExecute()
	{
		return base.ShouldExecute() && IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset);
	}

	private Vec3d getRandomPosNearby(Vec3d middle)
	{
		if (middle == null)
		{
			return null;
		}
		IBlockAccessor blockAccessor = entity.World.BlockAccessor;
		for (int i = 0; i < 5; i++)
		{
			int num = entity.World.Rand.Next(-1, 2);
			int num2 = entity.World.Rand.Next(-1, 2);
			Vec3d vec3d = middle.AddCopy(num2, 0f, num);
			if (blockAccessor.GetBlock(vec3d.AsBlockPos.Up()).Id == 0)
			{
				return vec3d;
			}
		}
		return middle;
	}

	public override bool ContinueExecute(float dt)
	{
		if (workstationPos != null && targetReached)
		{
			Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
			Vec3d vec3d = targetPos;
			if (vec3d != null)
			{
				double num = xYZ.DistanceTo(vec3d);
				if (num < 2.0)
				{
					FaceWorkstation();
				}
			}
		}
		return base.ContinueExecute(dt);
	}

	private Vec3d getWorkstationStandingPos(Vec3d workstationCenter)
	{
		if (workstationCenter == null)
		{
			return null;
		}
		IBlockAccessor blockAccessor = entity.World.BlockAccessor;
		BlockPos asBlockPos = workstationCenter.AsBlockPos;
		Block block = blockAccessor.GetBlock(asBlockPos);
		BlockFacing blockFacing = BlockFacing.NORTH;
		if (block != null && block.Variant != null)
		{
			if (block.Variant.TryGetValue("side", out var value))
			{
				blockFacing = BlockFacing.FromCode(value) ?? BlockFacing.NORTH;
			}
			else if (block.Variant.TryGetValue("facing", out value))
			{
				blockFacing = BlockFacing.FromCode(value) ?? BlockFacing.NORTH;
			}
			else if (block.Variant.TryGetValue("orientation", out value))
			{
				blockFacing = BlockFacing.FromCode(value) ?? BlockFacing.NORTH;
			}
		}
		BlockFacing opposite = blockFacing.Opposite;
		Vec3d vec3d = workstationCenter.AddCopy((double)opposite.Normalf.X * 1.2, 0.0, (double)opposite.Normalf.Z * 1.2);
		BlockPos asBlockPos2 = vec3d.AsBlockPos;
		if (blockAccessor.GetBlock(asBlockPos2.Up()).Id == 0 && blockAccessor.GetBlock(asBlockPos2).Id != 0)
		{
			return vec3d;
		}
		return getRandomPosNearby(workstationCenter);
	}

	private void FaceWorkstation()
	{
		if (workstationPos != null)
		{
			Vec3d xYZ = ((Entity)entity).ServerPos.XYZ;
			Vec3d vec3d = workstationPos.ToVec3d().Add(0.5, 0.5, 0.5);
			double y = vec3d.X - xYZ.X;
			double x = vec3d.Z - xYZ.Z;
			float yaw = (float)Math.Atan2(y, x);
			((Entity)entity).ServerPos.Yaw = yaw;
			((Entity)entity).Pos.Yaw = yaw;
		}
	}
}
