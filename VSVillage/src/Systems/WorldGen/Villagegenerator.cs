using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;

namespace VsVillage;

public class VillageGenerator : ModStdWorldGen
{
	public List<WorldGenVillageStructure> Structures = new List<WorldGenVillageStructure>();

	public Dictionary<string, List<string>> VillageNames = new Dictionary<string, List<string>>();

	public List<VillageType> Villages = new List<VillageType>();

	public VillageConfig Config;

	private ICoreServerAPI sapi;

	private IWorldGenBlockAccessor worldgenBlockAccessor;

	private LCGRandom rand;

	public override double ExecuteOrder()
	{
		return 0.45;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		rand = new LCGRandom(sapi.World.Seed);
		api.Event.InitWorldGenerator(initWorldGen, "standard");
		api.Event.ChunkColumnGeneration(handler, EnumWorldGenPass.TerrainFeatures, "standard");
		api.Event.GetWorldgenBlockAccessor(delegate(IChunkProviderThread chunkProvider)
		{
			worldgenBlockAccessor = chunkProvider.GetBlockAccessor(updateHeightmap: false);
		});
		try
		{
			Config = api.LoadModConfig<VillageConfig>("villageconfig.json");
			if (Config != null)
			{
				api.Logger.Notification("Mod Config successfully loaded.");
				return;
			}
			api.Logger.Notification("No Mod Config specified. Falling back to default settings");
			Config = new VillageConfig();
		}
		catch
		{
			Config = new VillageConfig();
			api.Logger.Error("Failed to load custom mod configuration. Falling back to default settings!");
		}
		finally
		{
			api.StoreModConfig(Config, "villageconfig.json");
		}
	}

	private TextCommandResult onCmdDebugVillage(TextCommandCallingArgs args)
	{
		VillageType villageType;
		if (args.ArgCount < 1)
		{
			villageType = Villages[sapi.World.Rand.Next(0, Villages.Count)];
		}
		else
		{
			string villageName = (string)args[0];
			villageType = Villages.Find((VillageType match) => match.Code == villageName);
			if (villageType == null)
			{
				return TextCommandResult.Error($"Could not find village with name {villageName}.");
			}
		}
		VillageGrid villageGrid = new VillageGrid(villageType.Length, villageType.Height);
		villageGrid.Init(villageType, rand, sapi);
		BlockPos start = ((Entity)args.Caller.Player.Entity).ServerPos.XYZInt.ToBlockPos();
		if (args.ArgCount > 1 && (string)args[1] == "probeTerrain" && !probeTerrain(start, villageGrid, sapi.World.BlockAccessor, villageType))
		{
			return TextCommandResult.Error("Terrain is too steep/ damp for generating a village");
		}
		villageGrid.connectStreets();
		Village village = new Village
		{
			Pos = villageGrid.getMiddle(start),
			Name = VillageNames[villageType.Names][rand.NextInt(villageType.Names.Length)],
			Api = sapi,
			Gatherplaces = new HashSet<BlockPos>(),
			Workstations = new Dictionary<BlockPos, VillagerWorkstation>(),
			Beds = new Dictionary<BlockPos, VillagerBed>(),
			VillagerSaveData = new Dictionary<long, VillagerData>(),
			Radius = VillageGrid.GridDistToMapDist(villageGrid.width)
		};
		sapi.ModLoader.GetModSystem<VillageManager>().Villages.TryAdd(village.Id, village);
		villageGrid.GenerateHouses(start, sapi.World.BlockAccessor, sapi.World);
		villageGrid.GenerateStreets(start, sapi.World.BlockAccessor, sapi.World);
		return TextCommandResult.Success();
	}

	private void initWorldGen()
	{
		((ModStdWorldGen)this).LoadGlobalConfig(sapi);
		foreach (Mod mod in sapi.ModLoader.Mods)
		{
			Structures.AddRange(sapi.Assets.TryGet(new AssetLocation(mod.Info.ModID, "config/villagestructures.json"))?.ToObject<List<WorldGenVillageStructure>>().ConvertAll((WorldGenVillageStructure structure) => structure.Init(sapi, mod.Info.ModID)) ?? new List<WorldGenVillageStructure>());
			Villages.AddRange(sapi.Assets.TryGet(new AssetLocation(mod.Info.ModID, "config/villagetypes.json"))?.ToObject<List<VillageType>>() ?? new List<VillageType>());
			VillageNames.AddRange(sapi.Assets.TryGet(new AssetLocation(mod.Info.ModID, "config/villagenames.json"))?.ToObject<Dictionary<string, List<string>>>() ?? new Dictionary<string, List<string>>());
		}
		foreach (WorldGenVillageStructure structure in Structures)
		{
			foreach (VillageType village in Villages)
			{
				foreach (StructureGroup structureGroup in village.StructureGroups)
				{
					if (structure.Group == structureGroup.Code && structure.Size == structureGroup.Size)
					{
						structureGroup.MatchingStructures.Add(structure);
					}
				}
			}
		}
		foreach (VillageType village2 in Villages)
		{
			village2.StructureGroups.Sort(delegate(StructureGroup a, StructureGroup b)
			{
				int size = (int)b.Size;
				return size.CompareTo((int)a.Size);
			});
		}
		IChatCommandApi chatCommands = sapi.ChatCommands;
		CommandArgumentParsers parsers = chatCommands.Parsers;
		chatCommands.Create("genvillage").WithDescription("Generate a village right where you are standing right now.").WithArgs(parsers.OptionalWordRange("villagetype", Villages.ConvertAll((VillageType type) => type.Code).ToArray()), parsers.OptionalWord("probeTerrain"))
			.RequiresPrivilege(Privilege.root)
			.WithExamples("genvillage tiny probeTerrain", "genvillage aged-village1")
			.HandleWith(onCmdDebugVillage);
	}

	private bool probeTerrain(BlockPos start, VillageGrid grid, IBlockAccessor blockAccessor, VillageType type)
	{
		int num = grid.width * grid.height * 4;
		int num2 = 0;
		ClimateCondition climateAt = blockAccessor.GetClimateAt(start);
		if (climateAt.Temperature > (float)type.MaxTemp || climateAt.Temperature < (float)type.MinTemp || climateAt.Rainfall > type.MaxRain || climateAt.Rainfall < type.MinRain)
		{
			return false;
		}
		for (int i = 0; i < grid.width - 1; i++)
		{
			for (int j = 0; j < grid.height - 1; j++)
			{
				int num3 = blockAccessor.GetTerrainMapheightAt(start);
				int num4 = num3;
				for (int k = 0; k < 2; k++)
				{
					for (int l = 0; l < 2; l++)
					{
						Vec2i vec2i = grid.GridCoordsToMapCoords(i + k, j + l);
						int terrainMapheightAt = blockAccessor.GetTerrainMapheightAt(start.AddCopy(vec2i.X, 0, vec2i.Y));
						num3 = Math.Max(num3, terrainMapheightAt);
						num4 = Math.Min(num4, terrainMapheightAt);
						if (k == 0 && l == 0 && blockAccessor.GetBlock(new BlockPos(start.X + vec2i.X, terrainMapheightAt + 1, start.Z + vec2i.Y, 0), 2).Id != 0)
						{
							num2++;
						}
					}
				}
				num -= num3 - num4;
			}
		}
		if (num > 0)
		{
			return num2 < grid.width * grid.height / 2;
		}
		return false;
	}

	private void handler(IChunkColumnGenerateRequest request)
	{
		IMapRegion mapRegion = request.Chunks[0].MapChunk.MapRegion;
		if (request.ChunkX % 4 != 0 || request.ChunkZ % 4 != 0 || Villages.Count == 0 || rand.NextFloat() > Config.VillageChance || mapRegion.GeneratedStructures.Find((GeneratedStructure structure) => structure.Group == "village") != null)
		{
			return;
		}
		VillageType villageType = Villages[rand.NextInt(Villages.Count)];
		VillageGrid villageGrid = new VillageGrid(villageType.Length, villageType.Height);
		BlockPos blockPos = new BlockPos(32 * request.ChunkX, 0, 32 * request.ChunkZ, 0);
		BlockPos end = villageGrid.getEnd(blockPos);
		if (worldgenBlockAccessor.GetChunk(blockPos.X / 32, 0, blockPos.Z / 32) != null && worldgenBlockAccessor.GetChunk(blockPos.X / 32, 0, end.Z / 32) != null && worldgenBlockAccessor.GetChunk(end.X / 32, 0, blockPos.Z / 32) != null && worldgenBlockAccessor.GetChunk(end.X / 32, 0, end.Z / 32) != null)
		{
			worldgenBlockAccessor.BeginColumn();
			if (probeTerrain(blockPos, villageGrid, worldgenBlockAccessor, villageType))
			{
				villageGrid.Init(villageType, rand, sapi);
				mapRegion.GeneratedStructures.Add(new GeneratedStructure
				{
					Code = villageGrid.VillageType.Code,
					Group = "village",
					Location = new Cuboidi(blockPos, end)
				});
				villageGrid.connectStreets();
				Village village = new Village
				{
					Pos = villageGrid.getMiddle(blockPos),
					Name = VillageNames[villageType.Names][rand.NextInt(villageType.Names.Length)],
					Api = sapi,
					Gatherplaces = new HashSet<BlockPos>(),
					Workstations = new Dictionary<BlockPos, VillagerWorkstation>(),
					Beds = new Dictionary<BlockPos, VillagerBed>(),
					VillagerSaveData = new Dictionary<long, VillagerData>(),
					Radius = VillageGrid.GridDistToMapDist(villageGrid.width)
				};
				sapi.ModLoader.GetModSystem<VillageManager>().Villages.TryAdd(village.Id, village);
				villageGrid.GenerateHouses(blockPos, worldgenBlockAccessor, sapi.World);
				villageGrid.GenerateStreets(blockPos, worldgenBlockAccessor, sapi.World);
			}
		}
	}
}
