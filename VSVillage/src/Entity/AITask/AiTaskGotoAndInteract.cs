using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public abstract class AiTaskGotoAndInteract : AiTaskBase
{
    protected float moveSpeed;

    protected long lastSearch;

    protected long lastExecution;

    protected bool stuck;

    protected Vec3d targetPos;

    protected AnimationMetaData interactAnim;

    protected bool targetReached;

    protected List<VillagerPathNode> currentPath;

    protected int currentPathIndex;

    protected VillagerAStarNew pathfinder;

    protected Vec3d lastPosition;

    protected long stuckCheckTime;

    protected int timesStuck;

    protected long lastRepathTime;

    protected BlockPos currentlyOpeningDoor;

    protected long doorOpenedTime;

    private int separationCheckTick = 0;

    private long maxTaskDurationMs;

    protected float maxDistance { get; set; }

    protected long taskStartedAtMs;

    public AiTaskGotoAndInteract(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        maxDistance = taskConfig["maxdistance"].AsFloat(5f);
        moveSpeed = taskConfig["movespeed"].AsFloat(0.08f);
        maxTaskDurationMs = (long)(taskConfig["maxtaskduration"].AsFloat(90f) * 1000f);
        interactAnim = new AnimationMetaData
        {
            Code = "nod",
            Animation = taskConfig["interact"].AsString("nod")
        }.Init();
        pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
        lastPosition = null;
        stuckCheckTime = 0L;
        timesStuck = 0;
        lastRepathTime = 0L;
        currentlyOpeningDoor = null;
        doorOpenedTime = 0L;
    }

    public override bool ShouldExecute()
    {
        if (!PreconditionsSatisfied()) return false;

        long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
        if (2000 + lastSearch < elapsedMilliseconds && cooldownUntilMs + lastExecution < elapsedMilliseconds)
        {
            lastSearch = elapsedMilliseconds;
            targetPos = GetTargetPos();
        }
        return targetPos != null && cooldownUntilMs + lastExecution < elapsedMilliseconds;
    }

    protected abstract Vec3d GetTargetPos();

    public override void StartExecute()
    {
        taskStartedAtMs = entity.World.ElapsedMilliseconds;
        pathfinder.blockAccessor.Begin();
        pathfinder.SetEntityCollisionBox(entity);
        BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
        BlockPos asBlockPos = targetPos.AsBlockPos;
        currentPath = pathfinder.FindPath(startPos, asBlockPos);
        pathfinder.blockAccessor.Commit();
        if (currentPath != null && currentPath.Count > 0)
        {
            currentPathIndex = 0;
            stuck = false;
            targetReached = false;
            lastPosition = entity.Pos.XYZ.Clone();
            stuckCheckTime = entity.World.ElapsedMilliseconds;
            timesStuck = 0;
        }
        else
        {
            stuck = true;
        }
        base.StartExecute();
    }

    public override bool ContinueExecute(float dt)
    {
        if (entity.World.ElapsedMilliseconds - taskStartedAtMs > maxTaskDurationMs)
            return false;
        CheckIfStuck();
        if (targetReached)
        {
            // Zero walk vector first so no residual movement bleeds in,
            // then apply a clamped separation nudge so villagers don't pile up.
            if (++separationCheckTick >= 15)
            {
                separationCheckTick = 0;
                entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
                ApplySeparationForce();
            }
            return entity.AnimManager.IsAnimationActive(interactAnim.Code);
        }
        if (!targetReached && targetPos != null && currentPath != null)
        {
            HandlePathTraversal();
            if (++separationCheckTick >= 15)
            {
                separationCheckTick = 0;
                ApplySeparationForce();
            }
        }
        if (InteractionPossible())
        {
            entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
            entity.Controls.StopAllMovement();
            entity.AnimManager.StopAnimation(animMeta.Code);
            entity.AnimManager.StartAnimation(interactAnim);
            targetReached = true;
            return true;
        }
        return !stuck && currentPath != null;
    }

    protected virtual bool InteractionPossible()
    {
        return targetPos != null && entity.Pos.SquareDistanceTo(targetPos) < 2.25;
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();
        entity.AnimManager.StopAnimation(animMeta.Code);
        CloseAllOpenDoors();
        if (targetReached)
        {
            ApplyInteractionEffect();
            lastExecution = entity.World.ElapsedMilliseconds;
        }
        entity.AnimManager.StopAnimation(interactAnim.Code);
        targetPos = null;
        targetReached = false;
        currentPath = null;
        lastPosition = null;
        timesStuck = 0;
        currentlyOpeningDoor = null;
    }

    protected abstract void ApplyInteractionEffect();

    protected void ToggleDoor(bool opened, BlockPos target)
    {
        Block block = entity.World.BlockAccessor.GetBlock(target);
        if (block != null && block.Code != null && (block.Code.Path.Contains("door") || block.Code.Path.Contains("gate")))
        {
            BlockSelection blockSel = new BlockSelection
            {
                Block = block,
                Position = target,
                HitPosition = new Vec3d(0.5, 0.5, 0.5),
                Face = BlockFacing.NORTH
            };
            TreeAttribute treeAttribute = new TreeAttribute();
            treeAttribute.SetBool("opened", opened);
            try
            {
                block.Activate(entity.World, new Caller
                {
                    Entity = entity,
                    Type = EnumCallerType.Entity,
                    Pos = entity.Pos.XYZ
                }, blockSel, treeAttribute);
            }
            catch (Exception ex)
            {
                entity.World.Logger.Error("[VsVillage] Failed to toggle door at " + target + ": " + ex.Message);
            }
        }
    }

    private void HandlePathTraversal()
    {
        if (currentlyOpeningDoor != null)
        {
            if (entity.World.ElapsedMilliseconds - doorOpenedTime < 500)
            {
                entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
                return;
            }
            currentlyOpeningDoor = null;
        }
        if (currentPath == null || currentPathIndex >= currentPath.Count)
        {
            stuck = true;
            return;
        }
        VillagerPathNode villagerPathNode = currentPath[currentPathIndex];
        Vec3d vec3d = villagerPathNode.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
        Vec3d xYZ = entity.Pos.XYZ;
        double dx = xYZ.X - vec3d.X;
        double dz = xYZ.Z - vec3d.Z;
        if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
        {
            currentPathIndex++;
            if (currentPathIndex < currentPath.Count)
            {
                VillagerPathNode next = currentPath[currentPathIndex];
                if (next.IsDoor)
                {
                    ToggleDoor(opened: true, next.BlockPos);
                    currentlyOpeningDoor = next.BlockPos.Copy();
                    doorOpenedTime = entity.World.ElapsedMilliseconds;
                }
            }
            if (villagerPathNode.IsDoor)
            {
                BlockPos doorPos = villagerPathNode.BlockPos.Copy();
                entity.World.RegisterCallback(delegate
                {
                    if (entity.Alive) ToggleDoor(opened: false, doorPos);
                }, 3000);
            }
        }
        if (currentPathIndex < currentPath.Count)
        {
            VillagerPathNode current = currentPath[currentPathIndex];
            Vec3d nextPos = current.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
            Vec3d dir = nextPos.Clone().Sub(xYZ);
            dir.Y = 0.0;
            dir = dir.Normalize();
            entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
            entity.Controls.WalkVector.Set(dir.X * moveSpeed, 0.0, dir.Z * moveSpeed);
            if (!entity.AnimManager.IsAnimationActive(animMeta.Code))
            {
                entity.AnimManager.StartAnimation(animMeta);
            }
        }
    }

    private void ApplySeparationForce()
    {
        Vec3d myPos = entity.Pos.XYZ;
        Entity[] nearby = entity.World.GetEntitiesAround(myPos, 1.5f, 1.5f, (Entity e) => e != entity && e.Code != null && e.Code.Domain == "vsvillagejadelands");
        if (nearby == null || nearby.Length == 0) return;

        double fx = 0.0, fz = 0.0;
        foreach (Entity neighbor in nearby)
        {
            Vec3d theirPos = neighbor.Pos.XYZ;
            double ndx = myPos.X - theirPos.X;
            double ndz = myPos.Z - theirPos.Z;
            double distSq = ndx * ndx + ndz * ndz;
            if (distSq < 1.0 && distSq > 0.0001)
            {
                double dist = Math.Sqrt(distSq);
                double pushStrength = (1.0 - dist) * 0.05;
                fx += ndx / dist * pushStrength;
                fz += ndz / dist * pushStrength;
            }
        }

        // Clamp total force — tighter cap when standing at target so stacked
        // villagers can't push each other away from their workstations.
        double forceMag = Math.Sqrt(fx * fx + fz * fz);
        double maxForce = targetReached ? 0.02 : 0.08;
        if (forceMag > maxForce)
        {
            fx = fx / forceMag * maxForce;
            fz = fz / forceMag * maxForce;
        }

        entity.Controls.WalkVector.X += fx;
        entity.Controls.WalkVector.Z += fz;
    }

    protected void CloseAllOpenDoors()
    {
        if (currentPath == null) return;

        foreach (VillagerPathNode node in currentPath)
        {
            if (node.IsDoor)
            {
                Block block = entity.World.BlockAccessor.GetBlock(node.BlockPos);
                if (block != null && block.Code != null && (block.Code.Path.Contains("opened") || block.Code.Path.Contains("open")))
                    ToggleDoor(opened: false, node.BlockPos);
            }
        }
    }

    private void CheckIfStuck()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - stuckCheckTime < 3000) return;

        Vec3d pos = entity.Pos.XYZ;
        if (lastPosition != null)
        {
            double moved = pos.DistanceTo(lastPosition);
            // Scale threshold by moveSpeed so slow tasks don't false-trigger.
            // At 20 ticks/s over 3 s expected = moveSpeed*60; flag below 40% of that.
            double stuckThreshold = Math.Max(0.25, moveSpeed * 60 * 0.4);
            if (moved < stuckThreshold)
            {
                timesStuck++;

                // If we're stuck in front of a gate/door node (e.g. another villager
                // closed it while we were walking through), re-open it immediately
                // before attempting a repath — this is cheaper and usually sufficient.
                TryReopenBlockingGate();

                if (timesStuck <= 5)
                {
                    if (now - lastRepathTime > 3000)
                    {
                        AttemptRepath();
                        lastRepathTime = now;
                    }
                }
                else if (timesStuck >= 6)
                {
                    TeleportToRecoveryPosition();
                    timesStuck = 0;
                }
            }
            else
            {
                timesStuck = 0;
            }
        }
        lastPosition = pos.Clone();
        stuckCheckTime = now;
    }

    /// <summary>
    /// If the current or next path node is a gate/door and it appears to be closed,
    /// re-open it.  Handles the case where another villager shuts a gate while this
    /// one is mid-traverse, leaving them pressed against it unable to move.
    /// </summary>
    private void TryReopenBlockingGate()
    {
        if (currentPath == null) return;

        // Check the node we're currently targeting and the one just behind us.
        int[] candidates = { currentPathIndex, currentPathIndex - 1 };
        foreach (int idx in candidates)
        {
            if (idx < 0 || idx >= currentPath.Count) continue;
            VillagerPathNode node = currentPath[idx];
            if (!node.IsDoor) continue;

            Block b = entity.World.BlockAccessor.GetBlock(node.BlockPos);
            if (b?.Code == null) continue;

            // A gate/door is "closed" when its code path does NOT contain "opened".
            bool isClosed = !b.Code.Path.Contains("opened") && !b.Code.Path.Contains("-open");
            if (isClosed)
            {
                ToggleDoor(opened: true, node.BlockPos);
                currentlyOpeningDoor = node.BlockPos.Copy();
                doorOpenedTime = entity.World.ElapsedMilliseconds;
                // Schedule it to close again after we've passed through.
                BlockPos doorPos = node.BlockPos.Copy();
                entity.World.RegisterCallback(delegate
                {
                    if (entity.Alive) ToggleDoor(opened: false, doorPos);
                }, 3000);
                return;
            }
        }
    }

    protected void AttemptRepath()
    {
        if (targetPos == null) return;

        pathfinder.blockAccessor.Begin();
        pathfinder.SetEntityCollisionBox(entity);
        BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
        List<VillagerPathNode> newPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos);
        pathfinder.blockAccessor.Commit();
        if (newPath != null && newPath.Count > 0)
        {
            currentPath = newPath;
            currentPathIndex = 0;
            stuck = false;
        }
        else
        {
            entity.World.Logger.Warning("[VsVillage] Pathfinder: no alternative path found for entity " + entity.EntityId);
        }
    }

    private void TeleportToRecoveryPosition()
    {
        Vec3d dest = null;

        // Try skipping a couple of nodes along the existing path first.
        if (currentPath != null && currentPathIndex < currentPath.Count)
        {
            int skip = Math.Min(2, currentPath.Count - currentPathIndex - 1);
            if (skip > 0)
            {
                dest = currentPath[currentPathIndex + skip].BlockPos.ToVec3d().Add(0.5, 0.1, 0.5);
                currentPathIndex += skip;
            }
        }

        // Fall back to nudging toward the target.
        if (dest == null && targetPos != null)
        {
            Vec3d myPos = entity.Pos.XYZ;
            Vec3d diff = targetPos.Clone().Sub(myPos);
            // Avoid NaN from normalising a near-zero vector.
            if (diff.LengthSq() < 0.0001) return;
            Vec3d dir = diff.Normalize();
            dest = myPos.Add(dir.X * 2.0, 0.5, dir.Z * 2.0);
        }

        if (dest != null)
        {
            IBlockAccessor ba = entity.World.BlockAccessor;
            BlockPos bp = dest.AsBlockPos;
            bool floorClear = ba.GetBlock(bp).CollisionBoxes == null || ba.GetBlock(bp).CollisionBoxes.Length == 0;
            bool headClear = ba.GetBlock(bp.UpCopy()).CollisionBoxes == null || ba.GetBlock(bp.UpCopy()).CollisionBoxes.Length == 0;
            if (floorClear && headClear)
            {
                entity.TeleportTo(dest);
                entity.Pos.Motion.Y = 0.1;
            }
            else
            {
                entity.World.Logger.Warning("[VsVillage] Teleport recovery: unsafe destination at " + dest + " — skipping");
                stuck = true;
            }
        }
    }
}