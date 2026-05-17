using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillage;


// Block class for villager beds.  Right-clicking sends a
// VillageAssignmentContext to the client so the player can open the
// assignment GUI and manually assign a villager to sleep here.

public class BlockVsBed : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		if (world.Api is ICoreServerAPI sapi && byPlayer is IServerPlayer serverPlayer)
		{
			BlockEntityVillagerBed be = sapi.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(blockSel.Position);
			if (be == null || string.IsNullOrEmpty(be.VillageId))
				return false; // not assigned to a village - no interaction

			Village village = sapi.ModLoader.GetModSystem<VillageManager>()?.GetVillage(be.VillageId);
			if (village == null)
				return false;

			VillageAssignmentContext ctx = new VillageAssignmentContext
			{
				Village      = village,
				StructurePos = blockSel.Position.Copy(),
				IsBed        = true
			};
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
