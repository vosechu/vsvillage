using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Replacement for the vanilla "seekentity" task on soldiers and guards.
/// Extends AiTaskGotoAndInteract so it uses VillagerAStarNew — giving it
/// fence avoidance, gate opening, stuck detection, and teleport recovery.
/// The task chases a hostile entity until the melee-attack task (higher
/// priority) takes over once within striking range.
///
/// JSON config keys:
///   entityCodes           — flat list of entity code patterns (supports trailing *)
///   seekingRange          — aggro radius (blocks)
///   maxFollowTime         — abort chase after this many seconds
///   requiredEntitySuffixes — if present, only entities whose Code.Path ends
///                            with one of these suffixes will run this task.
///                            Leave empty/absent to run for all entity types.
/// </summary>
public class AiTaskVillagerChaseEntity : AiTaskGotoAndInteract
{
    // The entity we are currently chasing.
    public Entity targetEntity;

    // Entity code patterns read from JSON "entityCodes".
    private readonly List<string> entityCodes = new List<string>();

    // Optional entity-suffix allow-list (e.g. ["-soldier", "-archer"]).
    private readonly List<string> requiredSuffixes = new List<string>();

    // Chase configuration.
    private float seekingRange;
    private float maxFollowTimeSec;
    private long  chaseStartedAtMs;

    // Repath as target moves.
    private long lastTargetUpdateMs;
    private const long TargetUpdateIntervalMs = 800;

    // Call-for-help throttle.
    private long lastCallForHelp;

    public AiTaskVillagerChaseEntity(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        seekingRange     = taskConfig["seekingRange"].AsFloat(20f);
        maxFollowTimeSec = taskConfig["maxFollowTime"].AsFloat(120f);

        JsonObject[] codes = taskConfig["entityCodes"].AsArray();
        if (codes != null)
            foreach (JsonObject c in codes)
            {
                string s = c.AsString();
                if (!string.IsNullOrEmpty(s)) entityCodes.Add(s);
            }

        JsonObject[] suffixes = taskConfig["requiredEntitySuffixes"].AsArray();
        if (suffixes != null)
            foreach (JsonObject s in suffixes)
            {
                string sfx = s.AsString();
                if (!string.IsNullOrEmpty(sfx)) requiredSuffixes.Add(sfx);
            }
    }

    // -----------------------------------------------------------------------
    // Target acquisition
    // -----------------------------------------------------------------------

    protected override Vec3d GetTargetPos()
    {
        // Honour entity-suffix allow-list (e.g. soldier-only tasks).
        if (!IsAllowedEntityType()) return null;

        // Keep existing live target.
        if (targetEntity != null && targetEntity.Alive && InRange(targetEntity))
            return targetEntity.Pos.XYZ;

        targetEntity = null;

        // Scan for the nearest matching hostile.
        targetEntity = entity.World.GetNearestEntity(
            entity.Pos.XYZ, seekingRange, seekingRange * 0.5f,
            e => e != entity && e.Alive && e.IsInteractable && MatchesCode(e));

        if (targetEntity != null)
        {
            chaseStartedAtMs = entity.World.ElapsedMilliseconds;
            BroadcastContactReport(targetEntity);
            return targetEntity.Pos.XYZ;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    public override void StartExecute()
    {
        chaseStartedAtMs   = entity.World.ElapsedMilliseconds;
        lastTargetUpdateMs = entity.World.ElapsedMilliseconds;
        base.StartExecute();
    }

    // Stop chasing when within this many blocks — lets the melee/ranged attack
    // task (higher priority) take over cleanly without us overshoot-walking into walls.
    private const double MeleeHandoffDistSq = 6.25; // 2.5 blocks

    public override bool ContinueExecute(float dt)
    {
        // Drop target if dead or fled out of range.
        if (targetEntity == null || !targetEntity.Alive || !InRange(targetEntity))
            return false;

        // Enforce max follow time.
        if (entity.World.ElapsedMilliseconds - chaseStartedAtMs > maxFollowTimeSec * 1000f)
            return false;

        // Within melee/ranged strike range — stop chasing and let the attack task take over.
        if (entity.Pos.SquareDistanceTo(targetEntity.Pos) <= MeleeHandoffDistSq)
            return false;

        // Periodically repath toward the moving target.
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastTargetUpdateMs > TargetUpdateIntervalMs)
        {
            lastTargetUpdateMs = now;
            // Path to a point 2 blocks toward us from the target's position, not the
            // target's exact tile — avoids pathfinding into an occupied or wall-adjacent block.
            Vec3d approachPos = GetApproachPos(targetEntity.Pos.XYZ, 2.0);
            if (targetPos == null || approachPos.SquareDistanceTo(targetPos) > 4.0)
            {
                targetPos = approachPos;
                AttemptRepath();
            }
        }

        return base.ContinueExecute(dt);
    }

    /// <summary>
    /// Returns a point <paramref name="stopDist"/> blocks away from <paramref name="rawTarget"/>
    /// in the direction of this entity — a safe approach destination that's never ON the target tile.
    /// </summary>
    private Vec3d GetApproachPos(Vec3d rawTarget, double stopDist)
    {
        Vec3d myPos = entity.Pos.XYZ;
        double dx = myPos.X - rawTarget.X;
        double dz = myPos.Z - rawTarget.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist < stopDist + 0.5) return rawTarget.Clone();
        double scale = stopDist / dist;
        return new Vec3d(rawTarget.X + dx * scale, rawTarget.Y, rawTarget.Z + dz * scale);
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        // Apply cooldown even when targetReached was never set (it never is).
        lastExecution = entity.World.ElapsedMilliseconds;
        targetEntity  = null;
    }

    // Chase just navigates — the melee-attack task handles the attack.
    protected override bool InteractionPossible() => false;
    protected override void ApplyInteractionEffect() { }

    // -----------------------------------------------------------------------
    // Ally coordination (mirrors AiTaskVillagerSeekEntity)
    // -----------------------------------------------------------------------

    public override void OnEntityHurt(DamageSource source, float damage)
    {
        Entity cause = source.CauseEntity;
        if (cause != null && cause.HasBehavior<EntityBehaviorVillager>()) return;
        Entity src = source.SourceEntity;
        if (src   != null && src.HasBehavior<EntityBehaviorVillager>())   return;

        if (source.Type == EnumDamageType.Heal) return;
        if (cause == null && src == null) return;
        if (entity.World.ElapsedMilliseconds - lastCallForHelp < 5000) return;

        lastCallForHelp = entity.World.ElapsedMilliseconds;
        Entity attacker = src ?? cause;

        Entity[] allies = entity.World.GetEntitiesAround(
            entity.Pos.XYZ, 15f, 4f,
            e =>
            {
                EntityBehaviorVillager bv = e.GetBehavior<EntityBehaviorVillager>();
                return bv != null && bv.Profession == EnumVillagerProfession.soldier;
            });

        foreach (Entity ally in allies)
        {
            AiTaskManager tm = ally.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
            if (tm == null) continue;
            tm.GetTask<AiTaskVillagerChaseEntity>()?.OnAllyAttacked(attacker);
            tm.GetTask<AiTaskVillagerMeleeAttack>()?.OnAllyAttacked(attacker);
            tm.GetTask<AiTaskVillagerRangedAttack>()?.OnAllyAttacked(attacker);
        }
    }

    public void OnAllyAttacked(Entity byEntity)
    {
        if (byEntity != null && byEntity.Alive && (targetEntity == null || !targetEntity.Alive))
            targetEntity = byEntity;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns true if this entity is in the requiredSuffixes allow-list (or the list is empty).</summary>
    private bool IsAllowedEntityType()
    {
        if (requiredSuffixes.Count == 0) return true;
        string path = entity.Code?.Path ?? "";
        foreach (string sfx in requiredSuffixes)
            if (path.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool InRange(Entity e)
    {
        return entity.Pos.SquareDistanceTo(e.Pos) <= seekingRange * seekingRange * 2f;
    }

    private void BroadcastContactReport(Entity target)
    {
        if (entity.World.ElapsedMilliseconds - lastCallForHelp < 10000) return;
        lastCallForHelp = entity.World.ElapsedMilliseconds;

        ICoreServerAPI sapi = entity.World.Api as ICoreServerAPI;
        if (sapi == null) return;

        EntityBehaviorVillager bv = entity.GetBehavior<EntityBehaviorVillager>();
        string villageName = bv?.Village?.Name ?? Lang.Get("vsvillage:trader-village-unknown");

        string langKey = (entity.Code?.Path?.EndsWith("-archer") == true)
            ? "vsvillage:archer-spotted-enemy"
            : "vsvillage:soldier-spotted-enemy";

        sapi.BroadcastMessageToAllGroups(Lang.Get(langKey, villageName, target.GetName()), EnumChatType.Notification);
    }

    private bool MatchesCode(Entity e)
    {
        string path = e.Code?.Path ?? "";
        foreach (string pattern in entityCodes)
        {
            if (pattern.EndsWith("*"))
            {
                string prefix = pattern.Substring(0, pattern.Length - 1);
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
