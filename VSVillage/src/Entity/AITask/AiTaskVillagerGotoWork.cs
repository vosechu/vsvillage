using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGotoWork : AiTaskGotoAndInteract
{
    private float offset;

    private BlockPos workstationPos;

    // Suffix-gating (onlyForEntitySuffix / excludeEntitySuffixes) is now handled
    // by the AiTaskGotoAndInteract base class: see base.SuffixGatePasses().

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
        if (!SuffixGatePasses()) return null;
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

                // Already at the stall: don't re-fire gotowork. Returning null
                // short-circuits ShouldExecute so the trader can sit at the stall
                // and run other tasks (trade dialog, idle, etc.) without gotowork preempting on every cooldown tick.
                if (entity.Pos.SquareDistanceTo(stallTarget) < 2.25) return null;

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

        // Already at the workstation: don't re-fire gotowork. Returning null
        // short-circuits ShouldExecute so the villager can sit at the
        // workstation and run their actual work tasks (cultivate, hammer,
        // tend-oven, etc.) without gotowork preempting on every cooldown tick.
        // gotowork will fire again only if the villager moves away (e.g. gets
        // pushed by another villager, returns from a flee, etc.).
        if (computedTarget != null && entity.Pos.SquareDistanceTo(computedTarget) < 2.25)
            return null;

        return computedTarget;
    }

    public override bool ShouldExecute()
    {
        if (!base.ShouldExecute()) return false;
        if (IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset)) return true;
        // 30s-idle fallback fires only during daytime hours (7-20). Lets a baker whose
        // oven task keeps failing walk back to their workstation, but never at night.
        double hour = entity.World.Calendar.HourOfDay;
        if (hour < 7.0 || hour >= 20.0) return false;
        var beh = entity.GetBehavior<EntityBehaviorVillager>();
        return beh != null && entity.World.ElapsedMilliseconds - beh.LastBusyAtMs > 30000;
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
