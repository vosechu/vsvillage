using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillage;


// Block class for non-mayor villager workstations.  Right-clicking sends an
// VillageAssignmentContext to the client so the player can open the
// assignment GUI and manually assign a compatible villager to this station.

public class BlockVsWorkstation : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		if (world.Api is ICoreServerAPI sapi && byPlayer is IServerPlayer serverPlayer)
		{
			BlockEntityVillagerWorkstation be = sapi.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(blockSel.Position);
			if (be == null || string.IsNullOrEmpty(be.VillageId))
				return false; // not assigned to a village - no interaction

			Village village = sapi.ModLoader.GetModSystem<VillageManager>()?.GetVillage(be.VillageId);
			if (village == null)
				return false;

			VillageAssignmentContext ctx = new VillageAssignmentContext
			{
				Village      = village,
				StructurePos = blockSel.Position.Copy(),
				IsBed        = false
			};

			// Attach profession-specific resource stats for the assignment GUI.
			if (village.Workstations.TryGetValue(blockSel.Position, out VillagerWorkstation ws))
			{
				if (ws.Profession == EnumVillagerProfession.farmer)
				{
					var (total, keys, values) = VillagerHireRequirementChecker.GetFarmlandStats(village, sapi);
					ctx.WorkstationResourceCount  = total;
					ctx.ResourceBreakdownKeys     = keys;
					ctx.ResourceBreakdownValues   = values;
				}
				else if (ws.Profession == EnumVillagerProfession.shepherd)
				{
					var (total, keys, values) = VillagerHireRequirementChecker.GetLivestockStats(village, sapi);
					ctx.WorkstationResourceCount  = total;
					ctx.ResourceBreakdownKeys     = keys;
					ctx.ResourceBreakdownValues   = values;
				}
			}

			sapi.Network.GetChannel("villagemanagementnetwork").SendPacket(ctx, serverPlayer);
			return true;
		}

		// Client side: return true to suppress default interaction (server handles it).
		return world.Api is ICoreClientAPI;
	}

	public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
	{
		return new WorldInteraction[]
		{
			new WorldInteraction
			{
				ActionLangCode = "vsvillage:interact-assign-villager",
				MouseButton    = EnumMouseButton.Right
			}
		};
	}
}
