using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace VsVillage;

public class BlockMayorWorkstation : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		string villageId = world.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(blockSel.Position)?.VillageId;
		bool flag = !string.IsNullOrEmpty(villageId);
		if (!flag && world.Api is ICoreClientAPI capi)
		{
			new ManagementGui(capi, blockSel.Position).TryOpen();
			return false;
		}
		if (flag && world.Api is ICoreServerAPI coreServerAPI && byPlayer is IServerPlayer serverPlayer)
		{
			VillageManager mgr = coreServerAPI.ModLoader.GetModSystem<VillageManager>();
			Village village = mgr?.GetVillage(villageId);
			if (village == null)
			{
				serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("vsvillage:village-data-unavailable"), EnumChatType.Notification);
				return true;
			}
			coreServerAPI.Network.GetChannel("villagemanagementnetwork").SendPacket(village, serverPlayer);
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
