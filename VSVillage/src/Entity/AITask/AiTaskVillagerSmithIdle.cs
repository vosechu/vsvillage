using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerSmithIdle : AiTaskGotoAndInteract
{
	// How many in-game hours must pass before the smith tends the forge again.
	// One VS day = 24 hours; default keeps it to roughly once per day.
	private double forgeTendIntervalHours;

	// Last in-game hour at which coal was added (Calendar.TotalHours).
	private double lastCoalAddedTotalHours = -9999.0;

	public AiTaskVillagerSmithIdle(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		forgeTendIntervalHours = taskConfig["forgeTendIntervalHours"].AsDouble(2.0);
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsSmith())
		{
			return null;
		}
		BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
		if (ws == null)
		{
			return null;
		}
		return ws.ToVec3d().Add(0.5, 1.0, 0.5);
	}

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 49.0;
    }
    protected override void ApplyInteractionEffect()
	{
		if (!IsSmith()) return;

		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = "hoe-till",
			Code = "hoe-till",
			AnimationSpeed = 1.5f,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());

		entity.World.RegisterCallback(delegate
		{
			TryTendForge();
			entity.AnimManager.StopAnimation("hoe-till");
		}, 2500);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	// -------------------------------------------------------------------------
	// Forge-tending logic
	// -------------------------------------------------------------------------

	private void TryTendForge()
	{
		double now = entity.World.Calendar.TotalHours;
		if (now - lastCoalAddedTotalHours < forgeTendIntervalHours)
		{
			return;
		}

		BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
		if (ws == null) return;

		BlockEntityForge forge = FindForge(ws);
		if (forge == null) return;

		// Only skip tending if the forge is actively burning — ignore residual fuel
		// level, because a cold forge with leftover fuel still needs relighting.
		if (forge.IsBurning) return;

		// Try to add one piece of coal (bituminous first, then anthracite, then coke).
		Item coalItem = entity.World.GetItem(new AssetLocation("game:ore-lignite"));
		if (coalItem == null) return;

		ItemStack coal = new ItemStack(coalItem, 1);

		if (!forge.FuelSlot.Empty)
		{
			// Fuel slot already has something — only add if it's the same item and there's room.
			if (!forge.FuelSlot.Itemstack.Equals(entity.World, coal, GlobalConstants.IgnoredStackAttributes))
			{
				return;
			}
			forge.FuelSlot.Itemstack.StackSize++;
		}
		else
		{
			forge.FuelSlot.Itemstack = coal;
		}

		forge.FuelSlot.MarkDirty();
		forge.MarkDirty();

		// Always attempt ignition after adding fuel — don't gate on CanIgnite,
		// which can return false for a cold forge even when there is plenty of fuel.
		forge.TryIgnite();

		lastCoalAddedTotalHours = now;
	}

	/// <summary>
	/// Returns the first usable forge-fuel Item that exists in this world's item registry.
	/// Tries lignite first (as requested), then falls back to higher-grade coals.
	/// </summary>
	private Item ResolveCoalItem()
	{
		string[] candidates = { "game:ore-ungraded-lignite", "game:coal-bituminous", "game:coal-anthracite", "game:coalcoke" };
		foreach (string code in candidates)
		{
			Item item = entity.World.GetItem(new AssetLocation(code));
			if (item != null) return item;
		}
		return null;
	}

	/// <summary>
	/// Finds a BlockEntityForge at or near (±2 blocks) the workstation position.
	/// The smith's workstation is a custom VSVillage block, not the forge itself,
	/// so we search the surrounding area rather than the exact position.
	/// </summary>
	private BlockEntityForge FindForge(BlockPos ws)
	{
		// Try the workstation block itself first (future-proofing).
		BlockEntityForge forge = entity.World.BlockAccessor.GetBlockEntity<BlockEntityForge>(ws);
		if (forge != null) return forge;

		// Scan ±4 horizontal, ±1 vertical around the workstation.
		BlockPos tmp = new BlockPos(ws.dimension);
		for (int dx = -4; dx <= 4; dx++)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dz = -4; dz <= 4; dz++)
				{
					if (dx == 0 && dy == 0 && dz == 0) continue;
					tmp.Set(ws.X + dx, ws.Y + dy, ws.Z + dz);
					forge = entity.World.BlockAccessor.GetBlockEntity<BlockEntityForge>(tmp);
					if (forge != null) return forge;
				}
			}
		}
		return null;
	}

	private bool IsSmith()
	{
		EntityAgent entityAgent = entity;
		return entityAgent != null && entityAgent.Code?.Path?.EndsWith("-smith") == true;
	}
}
