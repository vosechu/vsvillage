using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillage;

// Block class for guard posts. Right-clicking sends a VillageAssignmentContext to the client so the
// player can assign a soldier or archer to the day watch or the night watch.
public class BlockVsGuardPost : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		if (world.Api is ICoreServerAPI sapi && byPlayer is IServerPlayer serverPlayer)
		{
			BlockEntityVillagerGuardPost be = sapi.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerGuardPost>(blockSel.Position);
			if (be == null || string.IsNullOrEmpty(be.VillageId))
				return false; // not assigned to a village - no interaction

			Village village = sapi.ModLoader.GetModSystem<VillageManager>()?.GetVillage(be.VillageId);
			if (village == null)
				return false;

			VillageAssignmentContext ctx = new VillageAssignmentContext
			{
				Village      = village,
				StructurePos = blockSel.Position.Copy(),
				IsGuardPost  = true
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
				ActionLangCode = "vsvillage:interact-assign-guard",
				MouseButton    = EnumMouseButton.Right
			}
		};
	}
}
