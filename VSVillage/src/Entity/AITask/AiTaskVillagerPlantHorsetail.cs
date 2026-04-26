using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Herbalist morning task: walk to a nearby soil block and plant a horsetail
/// flower, preferring spots close to beehives/skeps/bee blocks.
/// </summary>
public class AiTaskVillagerPlantHorsetail : AiTaskGotoAndInteract
{
    private float plantRadius;
    private BlockPos plantTarget;
    private Block horsetailBlock;

    // Block-code fragments that indicate a bee-adjacent location is desirable
    private static readonly string[] BeeKeywords = { "bee", "hive", "beehive", "skep" };

    // Blocks considered valid ground to plant on
    private static readonly string[] SoilSurfaces = { "soil", "grass", "loam", "peat", "mud" };

    public AiTaskVillagerPlantHorsetail(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        plantRadius = taskConfig["plantRadius"].AsFloat(12f);
    }

    public override bool ShouldExecute()
    {
        if (!IsHerbalist()) return false;
        return base.ShouldExecute();
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsHerbalist()) return null;

        // Resolve the flower block (once)
        horsetailBlock ??= FindBlock("game:flower-horsetail-free")
                        ?? FindBlock("game:flower-horsetail")
                        ?? FindBlock("game:wildflower-horsetail-free");
        if (horsetailBlock == null) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        IBlockAccessor ba = entity.World.BlockAccessor;
        int r = (int)plantRadius;
        int wsY = ws.Y;

        // Pre-compute bee positions once — scanning per candidate is far too expensive.
        // Scan a slightly-expanded area (radius + bee detection radius) just once.
        int beeCheckR = r + 8;
        var beePositions = new HashSet<(int x, int z)>();
        for (int dx = -beeCheckR; dx <= beeCheckR; dx++)
        for (int dz = -beeCheckR; dz <= beeCheckR; dz++)
        for (int dy = -2; dy <= 4; dy++)
        {
            Block b = ba.GetBlock(new BlockPos(ws.X + dx, wsY + dy, ws.Z + dz));
            if (b?.Code?.Path == null) continue;
            string p = b.Code.Path;
            foreach (string kw in BeeKeywords)
            {
                if (p.Contains(kw))
                {
                    // Mark cells within 8 blocks of this bee block as bee-adjacent
                    for (int bx = -8; bx <= 8; bx++)
                    for (int bz = -8; bz <= 8; bz++)
                        beePositions.Add((ws.X + dx + bx, ws.Z + dz + bz));
                    break;
                }
            }
        }

        // Collect candidate positions: empty-air on valid soil, within planting radius
        var beeCandidates = new List<BlockPos>();
        var anyCandidates = new List<BlockPos>();
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    BlockPos groundPos = new BlockPos(ws.X + dx, wsY + dy, ws.Z + dz);
                    Block groundBlock = ba.GetBlock(groundPos);
                    if (!IsValidSoil(groundBlock)) continue;

                    BlockPos airPos = groundPos.UpCopy();
                    Block airBlock = ba.GetBlock(airPos);
                    if (airBlock == null) continue;
                    // Must be air (or at least passable — no collision boxes)
                    bool passable = airBlock.BlockMaterial == EnumBlockMaterial.Air ||
                                   airBlock.CollisionBoxes == null || airBlock.CollisionBoxes.Length == 0;
                    if (!passable) continue;
                    // Don't plant on top of an existing horsetail
                    if (airBlock.Code?.Path?.Contains("horsetail") == true) continue;

                    anyCandidates.Add(groundPos);
                    if (beePositions.Contains((groundPos.X, groundPos.Z)))
                        beeCandidates.Add(groundPos);
                }
            }
        }

        if (anyCandidates.Count == 0) return null;

        // Prefer bee-adjacent spots; fall back to any valid spot
        var pool = beeCandidates.Count > 0 ? beeCandidates : anyCandidates;
        BlockPos chosenGround = pool[entity.World.Rand.Next(pool.Count)];
        plantTarget = chosenGround.UpCopy(); // the air block we will place the flower in
        return plantTarget.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (plantTarget == null) return false;
        return entity.Pos.SquareDistanceTo(plantTarget.ToVec3d().Add(0.5, 0.5, 0.5)) < 9.0; // 3 block radius
    }

    protected override void ApplyInteractionEffect()
    {
        if (!IsHerbalist() || plantTarget == null || horsetailBlock == null) return;

        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = "hoe-till",
            Code = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());

        entity.World.RegisterCallback(delegate
        {
            PlantHorsetail();
            entity.AnimManager.StopAnimation("hoe-till");
        }, 1500);
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        plantTarget = null;
        base.FinishExecute(cancelled);
    }

    private void PlantHorsetail()
    {
        if (plantTarget == null || horsetailBlock == null) return;
        IBlockAccessor ba = entity.World.BlockAccessor;

        // Re-validate the spot is still empty
        Block current = ba.GetBlock(plantTarget);
        if (current == null || current.Id != 0) return; // occupied

        Block ground = ba.GetBlock(plantTarget.DownCopy());
        if (!IsValidSoil(ground)) return;

        ba.SetBlock(horsetailBlock.Id, plantTarget);
        ba.TriggerNeighbourBlockUpdate(plantTarget);

        SimpleParticleProperties particles = new SimpleParticleProperties(
            5f, 8f, ColorUtil.ToRgba(180, 100, 200, 100),
            plantTarget.ToVec3d().AddCopy(-0.3, 0.0, -0.3),
            plantTarget.ToVec3d().AddCopy(0.3, 0.5, 0.3),
            new Vec3f(-0.1f, 0.2f, -0.1f), new Vec3f(0.1f, 0.4f, 0.1f),
            1.5f, 0.3f, 0.08f);
        entity.World.SpawnParticles(particles);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsValidSoil(Block block)
    {
        if (block?.Code?.Path == null) return false;
        string path = block.Code.Path;
        foreach (string s in SoilSurfaces)
            if (path.Contains(s)) return true;
        return false;
    }

    private Block FindBlock(string code)
    {
        Block b = entity.World.GetBlock(new AssetLocation(code));
        return (b != null && b.Id != 0) ? b : null;
    }

    private bool IsHerbalist()
        => entity?.Code?.Path?.EndsWith("-herbalist") == true;
}
