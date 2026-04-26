using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGoShopping : AiTaskGotoAndInteract
{
    private static readonly Dictionary<string, HashSet<long>> ActiveShoppersByVillage
        = new Dictionary<string, HashSet<long>>();

    private const int MaxShoppersPerVillage = 1;

    private static readonly EnumVillagerProfession[] ShoppableProfessions =
    {
        EnumVillagerProfession.smith,
        EnumVillagerProfession.baker,
        EnumVillagerProfession.herbalist
    };

    private string registeredVillageId;

    public AiTaskVillagerGoShopping(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
    }

    public override bool ShouldExecute()
    {
        string path = entity?.Code?.Path;
        if (path != null && (path.EndsWith("-soldier") || path.EndsWith("-archer") || path.EndsWith("-trader")))
            return false;

        // AiTaskGotoAndInteract.ShouldExecute doesn't call AiTaskBase.ShouldExecute,
        // so duringDayTimeFrames is never checked unless we do it explicitly here.
        if (!IsInValidDayTimeHours(true))
            return false;

        Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
        if (village == null) return false;

        string villageId = village.Id;
        if (!ActiveShoppersByVillage.TryGetValue(villageId, out HashSet<long> shoppers))
        {
            shoppers = new HashSet<long>();
            ActiveShoppersByVillage[villageId] = shoppers;
        }

        if (shoppers.Contains(entity.EntityId))
            return base.ShouldExecute();

        if (shoppers.Count >= MaxShoppersPerVillage)
            return false;

        return base.ShouldExecute();
    }

    public override void StartExecute()
    {
        Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
        if (village != null)
        {
            registeredVillageId = village.Id;
            if (!ActiveShoppersByVillage.TryGetValue(registeredVillageId, out HashSet<long> shoppers))
            {
                shoppers = new HashSet<long>();
                ActiveShoppersByVillage[registeredVillageId] = shoppers;
            }
            shoppers.Add(entity.EntityId);
        }
        base.StartExecute();
    }

    public override void FinishExecute(bool cancelled)
    {
        if (registeredVillageId != null && ActiveShoppersByVillage.TryGetValue(registeredVillageId, out HashSet<long> shoppers))
            shoppers.Remove(entity.EntityId);

        registeredVillageId = null;
        base.FinishExecute(cancelled);
    }

    protected override Vec3d GetTargetPos()
    {
        EntityBehaviorVillager villager = entity.GetBehavior<EntityBehaviorVillager>();
        Village village = villager?.Village;
        if (village == null) return null;

        BlockPos myWorkstation = villager.Workstation;

        List<BlockPos> candidates = village.Workstations.Values
            .Where(ws => ws.Pos != null
                      && ShoppableProfessions.Contains(ws.Profession)
                      && ws.Pos != myWorkstation)
            .Select(ws => ws.Pos)
            .ToList();

        if (candidates.Count == 0) return null;

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = entity.World.Rand.Next(i + 1);
            BlockPos tmp = candidates[i]; candidates[i] = candidates[j]; candidates[j] = tmp;
        }

        IBlockAccessor ba = entity.World.BlockAccessor;
        foreach (BlockPos candidate in candidates)
        {
            foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
            {
                BlockPos neighbor = candidate.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
                Block foot = ba.GetBlock(neighbor);
                Block head = ba.GetBlock(neighbor.UpCopy());
                Block below = ba.GetBlock(neighbor.DownCopy());

                string footPath = foot?.Code?.Path ?? "";
                if (footPath.Contains("fence") && !footPath.Contains("gate")) continue;
                if (foot.Id != 0) continue;
                if (head.Id != 0) continue;
                if (below.Id == 0) continue;

                return neighbor.ToVec3d().Add(0.5, 0.0, 0.5);
            }
        }
        return null;
    }

    public override bool ContinueExecute(float dt)
    {
        bool result = base.ContinueExecute(dt);
        if (targetReached) return false;
        return result;
    }

    protected override void ApplyInteractionEffect()
    {
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = "greet",
            Code = "greet",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());
        entity.World.RegisterCallback(delegate
        {
            entity.AnimManager.StopAnimation("greet");
        }, 2000);
    }
}