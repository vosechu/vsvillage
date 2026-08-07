using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Woodworker work bout: walk to the sawhorse near the workstation and play the sawing
// animation for sawDurationSeconds (default 30s). Mirrors AiTaskVillagerSmithHammer.
public class AiTaskVillagerWoodworkerSaw : AiTaskGotoAndInteract
{
    private string sawAnimCode;
    private long sawDurationMs;
    private long sawStartedAtMs;

    private Vec3d sawhorseStandPos;
    private Vec3d sawhorseLookPos;

    public AiTaskVillagerWoodworkerSaw(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        sawDurationMs = (long)(taskConfig["sawDurationSeconds"].AsFloat(30f) * 1000f);
        sawAnimCode = taskConfig["sawAnimation"].AsString("hoe-till");
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsWoodworker()) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        BlockPos sawhorse = FindSawhorse(ws);
        if (sawhorse == null) return null;

        // Stand NEXT TO the sawhorse (it's a tall block, not something the entity stands on).
        BlockPos standBlock = FindAdjacentStandSpot(sawhorse, entity.World.BlockAccessor);
        if (standBlock == null) return null;

        sawhorseStandPos = standBlock.ToVec3d().Add(0.5, 0.0, 0.5);
        sawhorseLookPos = sawhorse.ToVec3d().Add(0.5, 0.5, 0.5);
        return sawhorseStandPos;
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        // Tight: must be at the stand spot, otherwise she plays hoe-till from across the room.
        return dx * dx + dz * dz < 2.25;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        sawStartedAtMs = 0L;
    }

    public override bool ContinueExecute(float dt)
    {
        if (!targetReached) return base.ContinueExecute(dt);

        if (sawStartedAtMs == 0L)
        {
            sawStartedAtMs = entity.World.ElapsedMilliseconds;
        }

        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();

        if (sawhorseLookPos != null)
        {
            Vec3d from = entity.Pos.XYZ.Add(0.0, entity.SelectionBox.Y2 * 0.5, 0.0);
            double dx = sawhorseLookPos.X - from.X;
            double dz = sawhorseLookPos.Z - from.Z;
            float targetYaw = (float)Math.Atan2(dx, dz);
            entity.Pos.Yaw = targetYaw;
        }

        if (!entity.AnimManager.IsAnimationActive(sawAnimCode))
        {
            entity.AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = sawAnimCode,
                Code = sawAnimCode,
                AnimationSpeed = 1.0f,
                BlendMode = EnumAnimationBlendMode.Add,
                EaseInSpeed = 3f,
                EaseOutSpeed = 3f
            }.Init());
        }

        return entity.World.ElapsedMilliseconds - sawStartedAtMs < sawDurationMs;
    }

    protected override void ApplyInteractionEffect()
    {
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation(sawAnimCode);

        sawhorseStandPos = null;
        sawhorseLookPos = null;

        base.FinishExecute(cancelled);

        lastExecution = entity.World.ElapsedMilliseconds;
    }

    private BlockPos FindAdjacentStandSpot(BlockPos sawhorse, IBlockAccessor ba)
    {
        BlockPos best = null;
        double bestSq = double.MaxValue;
        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            BlockPos candidate = new BlockPos(
                sawhorse.X + facing.Normali.X,
                sawhorse.Y,
                sawhorse.Z + facing.Normali.Z,
                sawhorse.dimension);
            if (!IsStandable(candidate, ba)) continue;

            double dx = candidate.X + 0.5 - entity.Pos.X;
            double dz = candidate.Z + 0.5 - entity.Pos.Z;
            double dsq = dx * dx + dz * dz;
            if (dsq < bestSq)
            {
                bestSq = dsq;
                best = candidate;
            }
        }
        return best;
    }

    private static bool IsStandable(BlockPos pos, IBlockAccessor ba)
    {
        Block foot = ba.GetBlock(pos);
        Block head = ba.GetBlock(pos.UpCopy());
        Block below = ba.GetBlock(pos.DownCopy());
        bool footClear = foot.CollisionBoxes == null || foot.CollisionBoxes.Length == 0;
        bool headClear = head.CollisionBoxes == null || head.CollisionBoxes.Length == 0;
        bool grounded = below.CollisionBoxes != null && below.CollisionBoxes.Length != 0;
        return footClear && headClear && grounded;
    }

    private BlockPos FindSawhorse(BlockPos ws)
    {
        IBlockAccessor ba = entity.World.BlockAccessor;
        BlockPos tmp = new BlockPos(ws.dimension);
        for (int dx = -4; dx <= 4; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -4; dz <= 4; dz++)
                {
                    tmp.Set(ws.X + dx, ws.Y + dy, ws.Z + dz);
                    Block b = ba.GetBlock(tmp);
                    if (b?.Code?.Path?.Contains("sawhorse") == true) return tmp.Copy();
                }
            }
        }
        return null;
    }

    private bool IsWoodworker()
    {
        return entity?.Code?.Path?.EndsWith("-woodworker") == true;
    }
}
