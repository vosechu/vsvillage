using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Farmer-only task: locate dead/wilted crop blocks above farmland POIs and
/// remove them so the plot can be re-planted.  The farmer walks to the block,
/// plays the hoe-till animation on arrival, then removes it in
/// ApplyInteractionEffect.
/// </summary>
public class AiTaskVillagerRemoveDeadCrops : AiTaskGotoAndInteract
{
    // Position of the dead-crop block we are currently targeting.
    private BlockPos deadCropPos;

    // Per-position cooldown so the same plot is not targeted over and over.
    private readonly Dictionary<BlockPos, long> recentlyClearedPos;
    private readonly long clearedCooldownMs;

    public AiTaskVillagerRemoveDeadCrops(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        recentlyClearedPos = new Dictionary<BlockPos, long>();
        clearedCooldownMs = (taskConfig?["clearedCooldownSeconds"]?.AsInt(120) ?? 120) * 1000L;

        // Replace the default nod with hoe-till so the base class plays the
        // correct animation on arrival instead of nodding.
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init();
    }

    // -----------------------------------------------------------------------
    // AiTaskGotoAndInteract overrides
    // -----------------------------------------------------------------------

    protected override Vec3d GetTargetPos()
    {
        if (!IsFarmer()) return null;

        // Anchor on the WORKSTATION so we clear dead crops near the farmer's fields,
        // not whichever stray dead crop happens to be closest to where she's standing.
        BlockPos wsPos = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (wsPos == null) return null;
        Vec3d searchAnchor = wsPos.ToVec3d().Add(0.5, 0.0, 0.5);

        // Walk all farmland POIs in range; track the nearest dead crop to the workstation.
        BlockPos best = null;
        double bestDistSq = double.MaxValue;

        entity.Api.ModLoader.GetModSystem<POIRegistry>().WalkPois(
            searchAnchor,
            maxDistance,
            poi =>
            {
                if (!(poi is BlockEntityFarmland farmland)) return false;

                BlockPos above = farmland.Pos.UpCopy();
                Block block = entity.World.BlockAccessor.GetBlock(above);
                if (!IsDeadCrop(block)) return false;
                if (IsPosOnCooldown(above)) return false;

                double distSq = searchAnchor.SquareDistanceTo(above.ToVec3d());
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = above.Copy();
                }
                return false; // continue walking — don't stop at first match
            });

        deadCropPos = best;
        if (deadCropPos == null) return null;

        // Target the centre of the dead-crop block.  Crops have no solid
        // collision box so the farmer can stand at the same XZ position.
        return deadCropPos.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (deadCropPos == null) return false;

        // Compare against block centre, not corner, so the distance check
        // matches the target position the villager actually walked to.
        if (entity.Pos.XYZ.SquareDistanceTo(deadCropPos.ToVec3d().Add(0.5, 0.0, 0.5)) > 9.0) return false;

        // Dead crop must still be there (another entity may have cleared it).
        return IsDeadCrop(entity.World.BlockAccessor.GetBlock(deadCropPos));
    }

    protected override void ApplyInteractionEffect()
    {
        if (deadCropPos == null) return;
        if (!IsDeadCrop(entity.World.BlockAccessor.GetBlock(deadCropPos))) return;

        entity.World.BlockAccessor.SetBlock(0, deadCropPos);
        entity.World.BlockAccessor.TriggerNeighbourBlockUpdate(deadCropPos);
        MarkPosCleared(deadCropPos);
        deadCropPos = null;
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private static bool IsDeadCrop(Block block)
    {
        return block?.Code?.Path != null && block.Code.Path.StartsWith("deadcrop");
    }

    private bool IsPosOnCooldown(BlockPos pos)
    {
        long now = entity.World.ElapsedMilliseconds;

        // Expire stale entries opportunistically.
        var toRemove = new List<BlockPos>();
        foreach (var kv in recentlyClearedPos)
        {
            if (now - kv.Value > clearedCooldownMs) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) recentlyClearedPos.Remove(k);

        return recentlyClearedPos.TryGetValue(pos, out long clearedAt)
            && now - clearedAt < clearedCooldownMs;
    }

    private void MarkPosCleared(BlockPos pos)
    {
        recentlyClearedPos[pos.Copy()] = entity.World.ElapsedMilliseconds;
    }

    private bool IsFarmer()
    {
        return entity?.Code?.Path?.EndsWith("-farmer") == true;
    }
}