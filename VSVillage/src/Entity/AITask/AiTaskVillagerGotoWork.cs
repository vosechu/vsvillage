using System;
using Vintagestory.API.Common;
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
        if (behavior.Profession == EnumVillagerProfession.trader)
        {
            BlockPos stallPos = entity.World.Api.ModLoader.GetModSystem<TravellingTraderManager>()?.GetActiveStallPos(village.Id);
            if (stallPos != null)
            {
                workstationPos = stallPos.Copy();
                Vec3d stallTarget = stallPos.ToVec3d().Add(0.25, 0.0, 0.25);

                // If we're already standing at this target, don't recompute — reuse
                // our current position so we don't micro-walk to a slightly different spot.
                if (targetPos != null && entity.Pos.SquareDistanceTo(targetPos) < 2.25)
                    return targetPos;

                return stallTarget;
            }
        }

        BlockPos blockPos = behavior.Workstation;
        if (blockPos == null)
        {
            BlockPos blockPos2 = (behavior.Workstation = village.FindFreeWorkstation(entity.EntityId, behavior.Profession));
            blockPos = blockPos2;
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

        Vec3d computedTarget = getWorkstationStandingPos((blockPos != null) ? blockPos.ToVec3d().Add(0.25, 0.0, 0.25) : null);

        // If we already have a valid target and are already close enough to it,
        // return the existing targetPos instead of a freshly computed one.
        // This prevents micro-sliding when the task re-evaluates while the villager
        // is already standing at their workstation.
        if (computedTarget != null && targetPos != null && entity.Pos.SquareDistanceTo(targetPos) < 2.25)
            return targetPos;

        return computedTarget;
    }

    public override bool ShouldExecute()
    {
        return base.ShouldExecute() && IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset);
    }

    private Vec3d getRandomPosNearby(Vec3d middle)
    {
        if (middle == null) return null;
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            Vec3d candidate = middle.AddCopy((double)facing.Normalf.X, 0.0, (double)facing.Normalf.Z);
            BlockPos cPos = candidate.AsBlockPos;

            Block foot = blockAccessor.GetBlock(cPos);
            string footPath = foot?.Code?.Path ?? "";
            if (footPath.Contains("fence") && !footPath.Contains("gate")) continue;
            if (foot.Id != 0) continue;
            if (blockAccessor.GetBlock(cPos.DownCopy()).Id == 0) continue;
            if (blockAccessor.GetBlock(cPos.UpCopy()).Id != 0) continue;

            return candidate;
        }

        return middle;
    }

    public override bool ContinueExecute(float dt)
    {
        if (workstationPos != null && targetReached)
        {
            Vec3d xYZ = entity.Pos.XYZ;
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

        BlockFacing[] candidateFacings = new BlockFacing[4];
        candidateFacings[0] = opposite;
        int ci = 1;
        foreach (BlockFacing hf in BlockFacing.HORIZONTALS)
        {
            if (hf != opposite) candidateFacings[ci++] = hf;
        }

        foreach (BlockFacing facing in candidateFacings)
        {
            Vec3d candidate = workstationCenter.AddCopy((double)facing.Normalf.X * 0.75, 0.0, (double)facing.Normalf.Z * 0.75);
            BlockPos cPos = candidate.AsBlockPos;
            Block foot = blockAccessor.GetBlock(cPos);
            string footPath = foot?.Code?.Path ?? "";

            if (footPath.Contains("fence") && !footPath.Contains("gate")) continue;
            if (foot.Id != 0) continue;
            if (blockAccessor.GetBlock(cPos.DownCopy()).Id == 0) continue;
            if (blockAccessor.GetBlock(cPos.UpCopy()).Id != 0) continue;

            return candidate;
        }

        return getRandomPosNearby(workstationCenter);
    }

    private void FaceWorkstation()
    {
        if (workstationPos != null)
        {
            Vec3d xYZ = entity.Pos.XYZ;
            Vec3d vec3d = workstationPos.ToVec3d().Add(0.25, 0.5, 0.25);
            double y = vec3d.X - xYZ.X;
            double x = vec3d.Z - xYZ.Z;
            float yaw = (float)Math.Atan2(y, x);
            entity.Pos.Yaw = yaw;
        }
    }
}
