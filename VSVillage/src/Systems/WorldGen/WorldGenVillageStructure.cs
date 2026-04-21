using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsVillage;

public class WorldGenVillageStructure
{
	[JsonProperty]
	public string Code;

	[JsonProperty]
	public string Group;

	[JsonProperty]
	public int AttachmentPoint;

	[JsonProperty]
	public int VerticalOffset;

	[JsonProperty]
	public EnumVillageStructureSize Size;

	public BlockSchematicStructure[] Schematics;

	public WorldGenVillageStructure Init(ICoreServerAPI api, string modId)
	{
		IAsset asset = api.Assets.Get(new AssetLocation(modId, "worldgen/vsvillage/" + Code + ".json"));
		BlockSchematicStructure val = asset?.ToObject<BlockSchematicStructure>();
		if (val == null)
		{
			api.World.Logger.Warning("Could not load VillageStruce {0}", Code);
			return this;
		}
		Schematics = new BlockSchematicStructure[4];
		val.FromFileName = asset.Name;
		val.Init(api.World.BlockAccessor);
		val.TransformWhilePacked((IWorldAccessor)api.World, EnumOrigin.BottomCenter, 90 * (4 - AttachmentPoint), (EnumAxis?)null, false);
		val.LoadMetaInformationAndValidate(api.World.BlockAccessor, (IWorldAccessor)api.World, val.FromFileName);
		Schematics[0] = val;
		for (int i = 1; i < 4; i++)
		{
			Schematics[i] = val.ClonePacked() as BlockSchematicStructure;
			Schematics[i].TransformWhilePacked((IWorldAccessor)api.World, EnumOrigin.BottomCenter, i * 90, (EnumAxis?)null, false);
			Schematics[i].Init(api.World.BlockAccessor);
			Schematics[i].LoadMetaInformationAndValidate(api.World.BlockAccessor, (IWorldAccessor)api.World, val.FromFileName);
		}
		BlockSchematicStructure val2 = Schematics[0];
		Schematics[0] = Schematics[2];
		Schematics[2] = val2;
		return this;
	}

	public void Generate(IBlockAccessor blockAccessor, IWorldAccessor worldForCollectibleResolve, BlockPos pos, int orientation)
	{
		Schematics[orientation].PlaceReplacingBlocks(blockAccessor, worldForCollectibleResolve, pos, EnumReplaceMode.ReplaceAllNoAir, new Dictionary<int, Dictionary<int, int>>(), (int?)null, true);
		generateFoundation(blockAccessor, pos, Schematics[orientation], orientation);
	}

	private void generateFoundation(IBlockAccessor blockAccessor, BlockPos pos, BlockSchematicStructure schematic, int orientation)
	{
		BlockPos blockPos = pos.DownCopy();
		int length = schematic.blocksByPos.GetLength(0);
		int length2 = schematic.blocksByPos.GetLength(2);
		while (probe(blockAccessor, blockPos, length, length2))
		{
			for (int i = 0; i < length; i++)
			{
				for (int j = 0; j < length2; j++)
				{
					Block block = schematic.blocksByPos[i, 0, j];
					if (block != null)
					{
						blockAccessor.SetBlock(block.Id, blockPos.AddCopy(i, 0, j));
					}
				}
			}
			blockPos.Down();
		}
	}

	private bool probe(IBlockAccessor blockAccessor, BlockPos pos, int length, int width)
	{
		BlockPos[] array = new BlockPos[8]
		{
			pos.Copy(),
			pos.AddCopy(length, 0, 0),
			pos.AddCopy(0, 0, width),
			pos.AddCopy(length, 0, width),
			pos.AddCopy(length / 2, 0, 0),
			pos.AddCopy(0, 0, width / 2),
			pos.AddCopy(length / 2, 0, width),
			pos.AddCopy(length, 0, width / 2)
		};
		for (int i = 0; i < array.Length; i++)
		{
			if (blockAccessor.GetBlock(array[i], 1).Id == 0)
			{
				return true;
			}
		}
		return false;
	}
}
