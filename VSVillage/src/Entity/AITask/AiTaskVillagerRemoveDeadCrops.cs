using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Farmer clears deadcrop-* blocks plus vanilla tallgrass above farmland POIs (narrow match avoids picking mod-added grass-named plants).
public class AiTaskVillagerRemoveDeadCrops : AiTaskGotoAndInteract
{
    // Position of the dead-crop / tallgrass block we are currently targeting.
    private BlockPos deadCropPos;

    // Per-position cooldown so the same plot is not targeted over and over.
    private readonly Dictionary<BlockPos, long> recentlyClearedPos;
    private readonly long clearedCooldownMs;

    public AiTaskVillagerRemoveDeadCrops(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        recentlyClearedPos = new Dictionary<BlockPos, long>();
        clearedCooldownMs = (taskConfig?["clearedCooldownSeconds"]?.AsInt(120) ?? 120) * 1000L;

        // Replace base "nod" with hoe-till so arrival plays the farming anim.
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init();
    }

    // AiTaskGotoAndInteract overrides

    protected override Vec3d GetTargetPos()
    {
        if (!IsFarmer()) return null;

        // Search around the farmer's current position so she can wander between
        // her workstation and fields without the task constantly anchoring her
        // back home. The villagergotowork task brings her back periodically.
        Vec3d searchAnchor = entity.Pos.XYZ;

        // Walk all farmland POIs in range; track the nearest dead crop.
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
                if (!IsRemovableGrowth(block)) return false;
                if (IsPosOnCooldown(above)) return false;

                double distSq = searchAnchor.SquareDistanceTo(above.ToVec3d());
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = above.Copy();
                }
                return false; // continue walking - don't stop at first match
            });

        deadCropPos = best;
        if (deadCropPos == null) return null;

        // Target the centre of the dead-crop / tallgrass block.  Neither has a
        // solid collision box so the farmer can stand at the same XZ position.
        return deadCropPos.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (deadCropPos == null) return false;

        // Compare against block centre, not corner, so the distance check
        // matches the target position the villager actually walked to.
        if (entity.Pos.XYZ.SquareDistanceTo(deadCropPos.ToVec3d().Add(0.5, 0.0, 0.5)) > 9.0) return false;

        // Block must still be removable (another entity may have cleared it,
        // or the grass tile may have been cut/eaten in the meantime).
        return IsRemovableGrowth(entity.World.BlockAccessor.GetBlock(deadCropPos));
    }

    protected override void ApplyInteractionEffect()
    {
        if (deadCropPos == null) return;
        if (!IsRemovableGrowth(entity.World.BlockAccessor.GetBlock(deadCropPos))) return;

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

    // Internals

    // True for anything the farmer should pluck off her farmland: dead crops
    // (any domain) plus engine-spawned vanilla tallgrass. The tallgrass match
    // is domain-locked to `game` so we never clear mod-added plants that
    // happen to use "grass" or "tallgrass" in their code path.
    private static bool IsRemovableGrowth(Block block)
    {
        AssetLocation code = block?.Code;
        if (code?.Path == null) return false;

        // Dead crops - any domain. Pre-existing match, kept as-is.
        if (code.Path.StartsWith("deadcrop")) return true;

        // Vanilla tallgrass family (tallgrass-{stage}-{cover}). The engine
        // spawns these on cultivated tiles via BlockSoil.GetTallGrassBlock.
        if (code.Domain == "game" && block.FirstCodePart() == "tallgrass") return true;

        return false;
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