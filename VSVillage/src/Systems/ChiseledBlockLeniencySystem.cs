using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace VsVillage;

// Gates the cbr-*.json sidesolid patches on the ChiseledBlocksCountAsWalls config
// flag. When the flag is off we revert chiseled/microblock sidesolid to vanilla.
public class ChiseledBlockLeniencySystem : ModSystem
{
	public override double ExecuteOrder()
	{
		return 0.5;
	}

	public override bool ShouldLoad(EnumAppSide side)
	{
		return side == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		VillageConfig config;
		try
		{
			config = api.LoadModConfig<VillageConfig>("villageconfig.json") ?? new VillageConfig();
		}
		catch
		{
			config = new VillageConfig();
		}

		if (config.ChiseledBlocksCountAsWalls) return;

		// ChiseledBlockRetention owns this behaviour when installed and our patches are
		// skipped via inverted dependsOn, so reverting here would clobber ITS changes.
		if (api.ModLoader.IsModEnabled("cbr")) return;

		foreach (Block block in api.World.Blocks)
		{
			if (block == null) continue;
			string cls = block.Class;
			if (cls == "BlockChisel" || cls == "BlockMicroBlock")
			{
				block.SideSolid = new SmallBoolArray(0);
			}
		}
	}
}
