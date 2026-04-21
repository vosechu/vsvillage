using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillage;

public class BlockMayorWorkstation : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		string villageId = world.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(blockSel.Position).VillageId;
		bool flag = !string.IsNullOrEmpty(villageId);
		if (!flag && world.Api is ICoreClientAPI capi)
		{
			new ManagementGui(capi, blockSel.Position).TryOpen();
			return false;
		}
		if (flag && world.Api is ICoreServerAPI coreServerAPI && byPlayer is IServerPlayer serverPlayer)
		{
			coreServerAPI.Network.GetChannel("villagemanagementnetwork").SendPacket(coreServerAPI.ModLoader.GetModSystem<VillageManager>().GetVillage(villageId), serverPlayer);
		}
		return true;
	}

	public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
	{
		return new WorldInteraction[1]
		{
			new WorldInteraction
			{
				ActionLangCode = "vsvillage:manage-village",
				MouseButton = EnumMouseButton.Right
			}
		};
	}
}
