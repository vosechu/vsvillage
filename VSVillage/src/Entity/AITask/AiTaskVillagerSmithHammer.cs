using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Primary work activity for smiths: approach the anvil in their workstation
/// room and hammer on it for a long duration. Much more visible than forge
/// tending (which happens once per day) — this is what the smith should be
/// doing most of his waking, on-shift hours.
///
/// Like the forge task, the smith's workstation is a custom VsVillage block;
/// the anvil is placed nearby, so we scan a ±2-block radius around the
/// workstation to locate it.
///
/// JSON config keys:
///   hammerDurationSeconds  — how long one hammering bout lasts (default 30 s).
///   duringDayTimeFrames    — handled by AiTaskBase; restrict to work hours.
/// </summary>
public class AiTaskVillagerSmithHammer : AiTaskGotoAndInteract
{
    private const string HammerAnimCode = "hammer-forge";

    // Duration of one hammering bout, milliseconds.
    private long hammerDurationMs;

    // When the current bout began (elapsed-ms wall clock).
    private long hammerStartedAtMs;

    // Cached target position (anvil stand point) used by InteractionPossible.
    private Vec3d anvilStandPos;

    // Cached look position (anvil center) used while hammering.
    private Vec3d anvilLookPos;

    public AiTaskVillagerSmithHammer(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        hammerDurationMs = (long)(taskConfig["hammerDurationSeconds"].AsFloat(30f) * 1000f);
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsSmith()) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        BlockPos anvil = FindAnvil(ws);
        if (anvil == null) return null;

        // Where to stand (pathing target).
        anvilStandPos = anvil.ToVec3d().Add(0.25, 1.0, 0.25);

        // Where to look while hammering (visual target).
        anvilLookPos = anvil.ToVec3d().Add(0.5, 0.8, 0.5);

        return anvilStandPos;
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 49.0;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        hammerStartedAtMs = 0L;
    }

    public override bool ContinueExecute(float dt)
    {
        // While we haven't reached the anvil yet, let the base walker run.
        if (!targetReached) return base.ContinueExecute(dt);

        // First tick after arrival: record when hammering began.
        if (hammerStartedAtMs == 0L)
        {
            hammerStartedAtMs = entity.World.ElapsedMilliseconds;
        }

        // Hold position, keep the hammer animation looping.
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();

        // Keep facing the anvil while hammering.
        if (anvilLookPos != null)
        {
            Vec3d from = entity.Pos.XYZ.Add(0.0, entity.SelectionBox.Y2 * 0.5, 0.0);
            double dx = anvilLookPos.X - from.X;
            double dz = anvilLookPos.Z - from.Z;

            float targetYaw = (float)Math.Atan2(dx, dz);

            entity.Pos.Yaw = targetYaw;
            entity.Pos.Yaw = targetYaw;
        }

        if (!entity.AnimManager.IsAnimationActive(HammerAnimCode))
        {
            entity.AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = HammerAnimCode,
                Code = HammerAnimCode,
                AnimationSpeed = 1.0f,
                BlendMode = EnumAnimationBlendMode.Add,
                EaseInSpeed = 3f,
                EaseOutSpeed = 3f
            }.Init());
        }

        return entity.World.ElapsedMilliseconds - hammerStartedAtMs < hammerDurationMs;
    }

    protected override void ApplyInteractionEffect()
    {
        // The bout itself is handled in ContinueExecute; nothing to apply here.
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation(HammerAnimCode);

        anvilStandPos = null;
        anvilLookPos = null;

        base.FinishExecute(cancelled);

        // Ensure cooldown applies even if targetReached was never latched.
        lastExecution = entity.World.ElapsedMilliseconds;
    }

    // -----------------------------------------------------------------------
    // Anvil discovery
    // -----------------------------------------------------------------------

    /// <summary>
    /// Finds an anvil block within ±2 horizontal / ±1 vertical of the workstation.
    /// Matches any block whose code path contains "anvil" (covers vanilla metal
    /// variants: anvil-copper, anvil-bronze, anvil-iron, anvil-meteoriciron, etc.).
    /// </summary>
    private BlockPos FindAnvil(BlockPos ws)
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
                    if (b?.Code?.Path?.Contains("anvil") == true) return tmp.Copy();
                }
            }
        }
        return null;
    }

    private bool IsSmith()
    {
        return entity?.Code?.Path?.EndsWith("-smith") == true;
    }
}