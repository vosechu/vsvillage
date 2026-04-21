using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Smith idle task: walks to their assigned workstation and performs a
/// hammering animation. On first visit, places an anvil block adjacent
/// to the workstation if one isn't already present.
/// </summary>
public class AiTaskVillagerSmithIdle : AiTaskGotoAndInteract
{
	public AiTaskVillagerSmithIdle(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsSmith())
		{
			return null;
		}
		EntityBehaviorVillager villager = entity.GetBehavior<EntityBehaviorVillager>();
		BlockPos ws = villager?.Workstation;
		if (ws == null)
		{
			return null;
		}
		return ws.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	protected override void ApplyInteractionEffect()
	{
		if (!IsSmith())
		{
			return;
		}

		EntityBehaviorVillager villager = entity.GetBehavior<EntityBehaviorVillager>();
		if (villager?.Workstation != null)
		{
			EnsureAnvilPresent(villager.Workstation);
		}

		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = "hoe-till",
			Code = "hoe-till",
			AnimationSpeed = 1.5f,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());

		entity.World.RegisterCallback(delegate
		{
			entity.AnimManager.StopAnimation("hoe-till");
		}, 2500);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	/// <summary>
	/// Checks whether an anvil block exists adjacent to the workstation.
	/// If not, places one in the first valid free adjacent block.
	/// </summary>
	private void EnsureAnvilPresent(BlockPos workstationPos)
	{
		foreach (BlockFacing face in BlockFacing.HORIZONTALS)
		{
			BlockPos adjacent = workstationPos.AddCopy(face);
			Block block = entity.World.BlockAccessor.GetBlock(adjacent);
			if (block?.Code?.Path?.Contains("anvil") == true)
			{
				return; // Already has an anvil nearby
			}
		}

		foreach (BlockFacing face in BlockFacing.HORIZONTALS)
		{
			BlockPos adjacent = workstationPos.AddCopy(face);
			Block existing = entity.World.BlockAccessor.GetBlock(adjacent);
			Block below = entity.World.BlockAccessor.GetBlock(adjacent.DownCopy());

			if (existing.Id == 0 && below.SideSolid[BlockFacing.UP.Index])
			{
				// Face the anvil toward the workstation (opposite of placement face)
				string facingCode = face.Opposite.Code;
				Block anvilBlock = entity.World.GetBlock(new AssetLocation("game:anvil-copper-" + facingCode));
				if (anvilBlock == null || anvilBlock.Id == 0)
				{
					// Try without directional suffix
					anvilBlock = entity.World.GetBlock(new AssetLocation("game:anvil-copper"));
				}
				if (anvilBlock == null || anvilBlock.Id == 0)
				{
					entity.World.Logger.Warning("Smith: Could not find anvil-copper block to place");
					return;
				}
				entity.World.BlockAccessor.SetBlock(anvilBlock.Id, adjacent);
				entity.World.Logger.Debug("Smith: Placed anvil-copper-" + facingCode + " at " + adjacent.ToString());
				return;
			}
		}

		entity.World.Logger.Debug("Smith: No free adjacent block found to place anvil at " + workstationPos.ToString());
	}

	private bool IsSmith()
	{
		return entity?.Code?.Path?.EndsWith("-smith") == true;
	}
}
