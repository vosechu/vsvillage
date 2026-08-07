using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
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

	private bool _worldGenInitialized;

	// Cached on InitWorldGenerator so we don't do a ModSystem lookup on every
	// candidate chunk inside handler(). Null if the survival mod isn't loaded.
	private GenStoryStructures storySystem;

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
				api.Logger.Debug("[VsVillage] Mod Config successfully loaded.");
				return;
			}
			api.Logger.Debug("[VsVillage] No Mod Config specified. Falling back to default settings");
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
				return TextCommandResult.Error("Could not find village with name " + villageName + ".");
			}
		}
		VillageGrid villageGrid = new VillageGrid(villageType.Length, villageType.Height);
		villageGrid.Init(villageType, rand, sapi);
		BlockPos start = args.Caller.Player.Entity.Pos.XYZInt.ToBlockPos();
		if (args.ArgCount > 1 && (string)args[1] == "probeTerrain" && !probeTerrain(start, villageGrid, sapi.World.BlockAccessor, villageType))
		{
			return TextCommandResult.Error("Terrain is too steep/ damp for generating a village");
		}
		villageGrid.connectStreets();
		BlockPos middle = villageGrid.getMiddle(start);
		middle.Y = sapi.World.BlockAccessor.GetTerrainMapheightAt(middle);
		Village village = new Village
		{
			Pos = middle,
			// Addons supply names as lang keys (vsvillagetowers:Danebury). GetUnformatted returns
			// the input untouched when it is not a key, so plain names still pass through.
			Name = Lang.GetUnformatted(VillageNames[villageType.Names][rand.NextInt(villageType.Names.Length)]),
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
		if (_worldGenInitialized) return;
		_worldGenInitialized = true;
		LoadGlobalConfig(sapi);
		storySystem = sapi.ModLoader.GetModSystem<GenStoryStructures>();
		foreach (Mod mod in sapi.ModLoader.Mods)
		{
			Structures.AddRange(sapi.Assets.TryGet(new AssetLocation(mod.Info.ModID, "config/villagestructures.json"))?.ToObject<List<WorldGenVillageStructure>>().ConvertAll((WorldGenVillageStructure worldGenVillageStructure) => worldGenVillageStructure.Init(sapi, mod.Info.ModID)) ?? new List<WorldGenVillageStructure>());
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

	private sealed class PendingVillage { public int ChunkX, ChunkZ, Retries; }

	private readonly List<PendingVillage> pendingVillages = new List<PendingVillage>();

	private const int MaxPendingRetries = 30;

	private readonly object villageGenLock = new object();

	private enum PlaceResult { Success, Rejected, CornersNotLoaded }

	private void handler(IChunkColumnGenerateRequest request)
	{
		if (request.ChunkX % 4 != 0 || request.ChunkZ % 4 != 0) return;

		lock (villageGenLock)
		{
			drainPendingVillages();
			IMapRegion mapRegion = request.Chunks[0].MapChunk.MapRegion;
			PlaceResult r = tryGenerateAtChunk(request.ChunkX, request.ChunkZ, mapRegion);

			if (r == PlaceResult.CornersNotLoaded && !isAlreadyPending(request.ChunkX, request.ChunkZ))
			{
				pendingVillages.Add(new PendingVillage { ChunkX = request.ChunkX, ChunkZ = request.ChunkZ });
			}
		}
	}

	private void drainPendingVillages()
	{
		for (int i = pendingVillages.Count - 1; i >= 0; i--)
		{
			PendingVillage p = pendingVillages[i];
			p.Retries++;
			if (p.Retries > MaxPendingRetries)
			{
				pendingVillages.RemoveAt(i);
				continue;
			}

			IMapChunk mc = worldgenBlockAccessor.GetMapChunk(p.ChunkX, p.ChunkZ);
			if (mc == null) continue;

			PlaceResult r = tryGenerateAtChunk(p.ChunkX, p.ChunkZ, mc.MapRegion);
			if (r != PlaceResult.CornersNotLoaded) pendingVillages.RemoveAt(i);
		}
	}

	private bool isAlreadyPending(int chunkX, int chunkZ)
	{
		for (int i = 0; i < pendingVillages.Count; i++)
			if (pendingVillages[i].ChunkX == chunkX && pendingVillages[i].ChunkZ == chunkZ) return true;
		return false;
	}

	private PlaceResult tryGenerateAtChunk(int chunkX, int chunkZ, IMapRegion mapRegion)
	{
		// Per-position seed (not sequential state) so a retry from the pending queue rolls
		// the same random outcomes as the original attempt for this chunk.
		rand.InitPositionSeed(chunkX, chunkZ);

		if (Villages.Count == 0) return PlaceResult.Rejected;
		if (rand.NextFloat() > Config.VillageChance) return PlaceResult.Rejected;
		if (mapRegion.GeneratedStructures.Find((GeneratedStructure structure) => structure.Group == "village") != null)
			return PlaceResult.Rejected;

		VillageType villageType = Villages[rand.NextInt(Villages.Count)];
		VillageGrid villageGrid = new VillageGrid(villageType.Length, villageType.Height);
		BlockPos blockPos = new BlockPos(32 * chunkX, 0, 32 * chunkZ, 0);
		BlockPos end = villageGrid.getEnd(blockPos);

		if (TooCloseToExistingVillage(blockPos, end)) return PlaceResult.Rejected;
		if (TooCloseToSpawn(blockPos, end, 300)) return PlaceResult.Rejected;

		// Avoid spawning on vanilla trader camps (Group="trader") and dungeon
		// surface entrances (Group="stairs"). Span full world height so the XZ
		// overlap check works regardless of surface Y (blockPos.Y is always 0).
		Cuboidi villageCuboid = new Cuboidi(blockPos.X, 0, blockPos.Z, end.X, sapi.World.BlockAccessor.MapSizeY, end.Z);
		int worldgenBuf = Config.WorldgenBufferBlocks;
		foreach (GeneratedStructure gs in mapRegion.GeneratedStructures)
		{
			if (gs.Group != "trader" && gs.Group != "stairs") continue;
			if (gs.Location.Clone().GrowBy(worldgenBuf, 0, worldgenBuf).Intersects(villageCuboid)) return PlaceResult.Rejected;
		}

		// Story locations are placed in a later worldgen pass (Vegetation), so they
		// aren't in mapregion.GeneratedStructures yet. Query the planned-location
		// dictionary on GenStoryStructures instead (cached in initWorldGen).
		if (storySystem == null) storySystem = sapi.ModLoader.GetModSystem<GenStoryStructures>();
		if (storySystem?.Structures != null)
		{
			foreach (KeyValuePair<string, StoryStructureLocation> kv in storySystem.Structures)
			{
				Cuboidi loc = kv.Value?.Location;
				if (loc == null) continue;
				if (loc.Clone().GrowBy(worldgenBuf, 0, worldgenBuf).Intersects(villageCuboid)) return PlaceResult.Rejected;
			}
		}

		const int apronMargin = 8;

		// Full-footprint check (not just corners): BlockAccessorWorldGen.SetBlock silently drops
		// writes outside the currently-generating chunk batch, so a concave loaded-chunk boundary
		// could otherwise pass a 4-corner check while an interior chunk is still ungenerated.
		if (!AllFootprintChunksLoaded(blockPos, end, apronMargin))
		{
			return PlaceResult.CornersNotLoaded;
		}

		worldgenBlockAccessor.BeginColumn();

		if (!probeTerrain(blockPos, villageGrid, worldgenBlockAccessor, villageType))
		{
			return PlaceResult.Rejected;
		}

		bool offsetApplied = ScanRiverAndValley(blockPos, end, villageGrid, apronMargin, out bool rejected, out BlockPos offsetBlockPos, out BlockPos offsetEnd);
		if (rejected) return PlaceResult.Rejected;
		if (offsetApplied)
		{
			blockPos = offsetBlockPos;
			end = offsetEnd;
		}

		// Reject genuinely swampy sites; marks watered grid cells blocked so no building lands on one.
		if (!ScanWaterAndBlockCells(blockPos, villageGrid)) return PlaceResult.Rejected;

		int[] heights = SampleHeights(blockPos, end, worldgenBlockAccessor);
		if (!CheckCliffs(blockPos, end, heights)) return PlaceResult.Rejected;

		int minH = heights[0], maxH = heights[0];
		for (int i = 1; i < heights.Length; i++)
		{
			if (heights[i] < minH) minH = heights[i];
			if (heights[i] > maxH) maxH = heights[i];
		}
		if (maxH - minH > villageType.MaxHeightRange) return PlaceResult.Rejected;

		villageGrid.Init(villageType, rand, sapi);
		mapRegion.GeneratedStructures.Add(new GeneratedStructure
		{
			Code = villageGrid.VillageType.Code,
			Group = "village",
			Location = new Cuboidi(blockPos, end)
		});
		villageGrid.connectStreets();
		BlockPos middle = villageGrid.getMiddle(blockPos);
		middle.Y = worldgenBlockAccessor.GetTerrainMapheightAt(middle);
		Village village = new Village
		{
			Pos = middle,
			// Addons supply names as lang keys (vsvillagetowers:Danebury). GetUnformatted returns
			// the input untouched when it is not a key, so plain names still pass through.
			Name = Lang.GetUnformatted(VillageNames[villageType.Names][rand.NextInt(villageType.Names.Length)]),
			Api = sapi,
			Gatherplaces = new HashSet<BlockPos>(),
			Workstations = new Dictionary<BlockPos, VillagerWorkstation>(),
			Beds = new Dictionary<BlockPos, VillagerBed>(),
			VillagerSaveData = new Dictionary<long, VillagerData>(),
			Radius = VillageGrid.GridDistToMapDist(villageGrid.width),
			ClaimStart = blockPos.Copy(),
			ClaimEnd = end.Copy()
		};
		VillageManager villageManager = sapi.ModLoader.GetModSystem<VillageManager>();
		villageManager.Villages.TryAdd(village.Id, village);
		// Claim registration mutates the server's shared claim list and broadcasts it; this runs
		// on a worldgen thread, so hand it to the main thread.
		sapi.Event.EnqueueMainThreadTask(() => villageManager.RegisterVillageClaim(sapi, village), "vsvillage-claim");
		villageGrid.GenerateHouses(blockPos, worldgenBlockAccessor, sapi.World);
		villageGrid.GenerateStreets(blockPos, worldgenBlockAccessor, sapi.World);

		// A village spans multiple chunk columns from one trigger chunk; a neighbour column
		// already past its own lighting pass never gets auto-relit by this later cross-chunk write.
		FinalizeChunks(blockPos, end);
		return PlaceResult.Success;
	}

	// Scans an apron-padded box for river overlap. Also runs terrain-depression analysis (min height
	// per column/row) and - if a narrow stream valley sits under the footprint - tries shifting the
	// site sideways (both axes) so the village doesn't straddle the streambed.
	private bool ScanRiverAndValley(BlockPos blockPos, BlockPos end, VillageGrid villageGrid, int apronMargin, out bool rejected, out BlockPos offsetBlockPos, out BlockPos offsetEnd)
	{
		rejected = false;
		offsetBlockPos = null;
		offsetEnd = null;

		int scanX1 = Math.Max(0, blockPos.X - apronMargin);
		int scanZ1 = Math.Max(0, blockPos.Z - apronMargin);
		int scanX2 = end.X + apronMargin;
		int scanZ2 = end.Z + apronMargin;
		int scanWidth = scanX2 - scanX1 + 1;
		int scanHeight = scanZ2 - scanZ1 + 1;
		int[] colMinH = new int[scanWidth];
		int[] rowMinH = new int[scanHeight];
		for (int i = 0; i < scanWidth; i++) colMinH[i] = int.MaxValue;
		for (int i = 0; i < scanHeight; i++) rowMinH[i] = int.MaxValue;

		ushort[] lastRiverDist = null;
		IMapChunk lastMc = null;
		int lastCX = -1, lastCZ = -1;
		bool chunkGenerating = true;
		for (int wx = scanX1; wx <= scanX2; wx++)
		{
			for (int wz = scanZ1; wz <= scanZ2; wz++)
			{
				int cx = wx / 32, cz = wz / 32;
				if (cx != lastCX || cz != lastCZ)
				{
					lastMc = worldgenBlockAccessor.GetMapChunk(cx, cz);
					lastRiverDist = lastMc?.GetModdata<ushort[]>("riverDistance");
					lastCX = cx; lastCZ = cz;
					chunkGenerating = worldgenBlockAccessor.GetChunk(cx, 0, cz) != null;
				}
				if (lastRiverDist != null && lastRiverDist[(wz % 32) * 32 + (wx % 32)] == 0)
				{
					rejected = true;
					return false;
				}

				if (lastMc != null && chunkGenerating)
				{
					int lx = wx % 32, lz = wz % 32;
					int surfaceH = lastMc.WorldGenTerrainHeightMap[lz * 32 + lx];
					int ci = wx - scanX1;
					int ri = wz - scanZ1;
					if (surfaceH < colMinH[ci]) colMinH[ci] = surfaceH;
					if (surfaceH < rowMinH[ri]) rowMinH[ri] = surfaceH;
				}
			}
		}

		const int maxValleyWidth = 14;
		const int minValleyWidth = 3;
		const int depthThreshold = 2;

		// Check columns (N-S flowing stream -> offset in X)
		int globalMinX = 0;
		for (int x = 1; x < scanWidth; x++)
			if (colMinH[x] < colMinH[globalMinX]) globalMinX = x;

		int valleyLevel = colMinH[globalMinX];
		int valleyStartX = globalMinX, valleyEndX = globalMinX;
		while (valleyStartX > 0 && colMinH[valleyStartX - 1] <= valleyLevel + depthThreshold
			   && valleyEndX - valleyStartX + 1 < maxValleyWidth) valleyStartX--;
		while (valleyEndX < scanWidth - 1 && colMinH[valleyEndX + 1] <= valleyLevel + depthThreshold
			   && valleyEndX - valleyStartX + 1 < maxValleyWidth) valleyEndX++;

		int valleyWidthX = valleyEndX - valleyStartX + 1;
		if (valleyWidthX >= minValleyWidth)
		{
			int offX = valleyWidthX + 3;

			BlockPos candidate = blockPos.AddCopy(offX, 0, 0);
			BlockPos candidateEnd = villageGrid.getEnd(candidate);
			if (AllFootprintChunksLoaded(candidate, candidateEnd, apronMargin) && !FootprintHasRiver(candidate, candidateEnd))
			{
				offsetBlockPos = candidate;
				offsetEnd = candidateEnd;
				return true;
			}

			candidate = blockPos.AddCopy(-offX, 0, 0);
			candidateEnd = villageGrid.getEnd(candidate);
			if (AllFootprintChunksLoaded(candidate, candidateEnd, apronMargin) && !FootprintHasRiver(candidate, candidateEnd))
			{
				offsetBlockPos = candidate;
				offsetEnd = candidateEnd;
				return true;
			}
		}

		// Check rows (E-W flowing stream -> offset in Z)
		int globalMinZ = 0;
		for (int z = 1; z < scanHeight; z++)
			if (rowMinH[z] < rowMinH[globalMinZ]) globalMinZ = z;

		valleyLevel = rowMinH[globalMinZ];
		int valleyStartZ = globalMinZ, valleyEndZ = globalMinZ;
		while (valleyStartZ > 0 && rowMinH[valleyStartZ - 1] <= valleyLevel + depthThreshold
			   && valleyEndZ - valleyStartZ + 1 < maxValleyWidth) valleyStartZ--;
		while (valleyEndZ < scanHeight - 1 && rowMinH[valleyEndZ + 1] <= valleyLevel + depthThreshold
			   && valleyEndZ - valleyStartZ + 1 < maxValleyWidth) valleyEndZ++;

		int valleyWidthZ = valleyEndZ - valleyStartZ + 1;
		if (valleyWidthZ >= minValleyWidth)
		{
			int offZ = valleyWidthZ + 3;

			BlockPos candidate = blockPos.AddCopy(0, 0, offZ);
			BlockPos candidateEnd = villageGrid.getEnd(candidate);
			if (AllFootprintChunksLoaded(candidate, candidateEnd, apronMargin) && !FootprintHasRiver(candidate, candidateEnd))
			{
				offsetBlockPos = candidate;
				offsetEnd = candidateEnd;
				return true;
			}

			candidate = blockPos.AddCopy(0, 0, -offZ);
			candidateEnd = villageGrid.getEnd(candidate);
			if (AllFootprintChunksLoaded(candidate, candidateEnd, apronMargin) && !FootprintHasRiver(candidate, candidateEnd))
			{
				offsetBlockPos = candidate;
				offsetEnd = candidateEnd;
				return true;
			}
		}

		return false;
	}

	// Valley offsets can shift the footprint into ground the original scan never covered;
	// re-check the rivers moddata there so the village cannot land on a missed river.
	private bool FootprintHasRiver(BlockPos startPos, BlockPos endPos)
	{
		ushort[] riverDist = null;
		int lastCX = -1, lastCZ = -1;
		for (int wx = startPos.X; wx <= endPos.X; wx++)
		{
			for (int wz = startPos.Z; wz <= endPos.Z; wz++)
			{
				int cx = wx / 32, cz = wz / 32;
				if (cx != lastCX || cz != lastCZ)
				{
					riverDist = worldgenBlockAccessor.GetMapChunk(cx, cz)?.GetModdata<ushort[]>("riverDistance");
					lastCX = cx; lastCZ = cz;
				}
				if (riverDist != null && riverDist[(wz % 32) * 32 + (wx % 32)] == 0) return true;
			}
		}
		return false;
	}

	// Marks every watered grid cell (deep pond or shallow marsh) as blocked so no building lands on it,
	// and rejects genuinely swampy sites outright: too much water leaves no buildable slots.
	private bool ScanWaterAndBlockCells(BlockPos blockPos, VillageGrid villageGrid)
	{
		int floodedCells = 0, waterCells = 0, totalCells = 0;
		BlockPos cellPos = new BlockPos(0);
		for (int i = 0; i < villageGrid.width - 1; i++)
		{
			for (int j = 0; j < villageGrid.height - 1; j++)
			{
				Vec2i corner = villageGrid.GridCoordsToMapCoords(i, j);
				cellPos.Set(blockPos.X + corner.X, 0, blockPos.Z + corner.Y);

				if (worldgenBlockAccessor.GetChunk(cellPos.X / 32, 0, cellPos.Z / 32) == null)
				{
					totalCells++;
					continue;
				}

				int surfaceY = worldgenBlockAccessor.GetTerrainMapheightAt(cellPos);
				int waterDepth = 0;
				for (int dy = 1; dy <= 5; dy++)
				{
					int y = surfaceY + dy;
					if (worldgenBlockAccessor.GetChunk(cellPos.X / 32, y / 32, cellPos.Z / 32) == null) break;
					cellPos.Y = y;
					Block b = worldgenBlockAccessor.GetBlock(cellPos, 2);
					if (b?.LiquidCode != null) waterDepth++;
					else break;
				}
				totalCells++;
				if (waterDepth > 2) floodedCells++;
				if (waterDepth > 0)
				{
					waterCells++;
					villageGrid.BlockCell(i, j);
				}
			}
		}

		return !(floodedCells > totalCells * 0.8f || waterCells > totalCells * 0.6f);
	}

	private void FinalizeChunks(BlockPos start, BlockPos end)
	{
		const int apron = 8;
		int cx1 = Math.Max(0, (start.X - apron) / 32), cz1 = Math.Max(0, (start.Z - apron) / 32);
		int cx2 = (end.X + apron) / 32, cz2 = (end.Z + apron) / 32;
		int mapSizeY = worldgenBlockAccessor.MapSizeY;

		for (int cx = cx1; cx <= cx2; cx++)
		{
			for (int cz = cz1; cz <= cz2; cz++)
			{
				IMapChunk mc = worldgenBlockAccessor.GetMapChunk(cx, cz);
				if (mc == null) continue;
				if (worldgenBlockAccessor.GetChunk(cx, 0, cz) == null) continue;

				RebuildHeightmap(mc, cx, cz, mapSizeY);
				worldgenBlockAccessor.RunScheduledBlockLightUpdates(cx, cz);
				SunFloodChunkColumn(cx, cz, mapSizeY);
				mc.MarkDirty();
			}
		}
	}

	private void SunFloodChunkColumn(int cx, int cz, int mapSizeY)
	{
		int chunkCount = mapSizeY / 32;
		IWorldChunk[] chunks = new IWorldChunk[chunkCount];
		for (int cy = 0; cy < chunkCount; cy++)
			chunks[cy] = worldgenBlockAccessor.GetChunk(cx, cy, cz);

		sapi.WorldManager.SunFloodChunkColumnForWorldGen(chunks, cx, cz);
	}

	// Terrain height maps go stale after these cross-chunk writes; the sunlight flood below reads them.
	private void RebuildHeightmap(IMapChunk mc, int chunkX, int chunkZ, int mapSizeY)
	{
		ushort[] rainMap = mc.RainHeightMap;
		ushort[] terrainMap = mc.WorldGenTerrainHeightMap;
		int baseX = chunkX * 32, baseZ = chunkZ * 32;
		int chunkCount = mapSizeY / 32;
		BlockPos tmpPos = new BlockPos(0);

		bool[] subPresent = new bool[chunkCount];
		for (int cy = 0; cy < chunkCount; cy++)
			subPresent[cy] = worldgenBlockAccessor.GetChunk(chunkX, cy, chunkZ) != null;

		for (int lx = 0; lx < 32; lx++)
		{
			for (int lz = 0; lz < 32; lz++)
			{
				int idx = lz * 32 + lx;
				rainMap[idx] = 0;
				terrainMap[idx] = 0;
				bool rainSet = false, heightSet = false;

				for (int y = mapSizeY - 1; y >= 0; y--)
				{
					int cy = y / 32;
					if (!subPresent[cy])
					{
						y = cy * 32;
						continue;
					}

					tmpPos.Set(baseX + lx, y, baseZ + lz);
					Block block = worldgenBlockAccessor.GetBlock(tmpPos);

					if (block.Id == 0) continue;

					if (!block.RainPermeable && !rainSet)
					{
						rainMap[idx] = (ushort)y;
						rainSet = true;
					}
					if (block.SideSolid[BlockFacing.UP.Index] && !heightSet)
					{
						terrainMap[idx] = (ushort)y;
						heightSet = true;
					}
					if (rainSet && heightSet) break;
				}
			}
		}
	}

	private int[] SampleHeights(BlockPos start, BlockPos end, IWorldGenBlockAccessor blockAccessor)
	{
		int widthX = end.X - start.X + 1;
		int widthZ = end.Z - start.Z + 1;
		int[] heights = new int[widthX * widthZ];
		int idx = 0;
		for (int dx = 0; dx < widthX; dx++)
		{
			for (int dz = 0; dz < widthZ; dz++)
			{
				heights[idx++] = blockAccessor.GetTerrainMapheightAt(new BlockPos(start.X + dx, 0, start.Z + dz, start.dimension));
			}
		}
		return heights;
	}

	// Samples heights every 4 blocks and rejects a site with an extreme (>22 block) drop
	// more than 8 blocks in from the footprint edge - avoids generating on a cliff/ravine.
	private bool CheckCliffs(BlockPos start, BlockPos end, int[] heights)
	{
		const int extremeDrop = 22;
		int widthX = end.X - start.X + 1;
		int widthZ = end.Z - start.Z + 1;
		const int sampleStep = 4;
		int sampleMin = int.MaxValue;

		for (int x = 0; x < widthX; x += sampleStep)
			for (int z = 0; z < widthZ; z += sampleStep)
			{
				int h = heights[x * widthZ + z];
				if (h < sampleMin) sampleMin = h;
			}

		for (int x = 0; x < widthX; x += sampleStep)
		{
			for (int z = 0; z < widthZ; z += sampleStep)
			{
				int h = heights[x * widthZ + z];
				int drop = h - sampleMin;
				int distToEdge = Math.Min(Math.Min(x, widthX - 1 - x), Math.Min(z, widthZ - 1 - z));
				if (distToEdge >= 8 && drop > extremeDrop) return false;
			}
		}
		return true;
	}

	// BlockAccessorWorldGen.SetBlock silently drops writes outside the currently-generating
	// chunk batch, so every chunk in the footprint (not just the 4 corners) must be checked.
	private bool AllFootprintChunksLoaded(BlockPos start, BlockPos end, int margin = 0)
	{
		int cx1 = (start.X - margin) / 32, cz1 = (start.Z - margin) / 32;
		int cx2 = (end.X + margin) / 32, cz2 = (end.Z + margin) / 32;

		for (int cx = cx1; cx <= cx2; cx++)
			for (int cz = cz1; cz <= cz2; cz++)
				if (worldgenBlockAccessor.GetChunk(cx, 0, cz) == null) return false;
		return true;
	}

	private bool TooCloseToExistingVillage(BlockPos start, BlockPos end)
	{
		VillageManager villageManager = sapi.ModLoader.GetModSystem<VillageManager>();
		if (villageManager?.Villages == null || villageManager.Villages.IsEmpty) return false;

		int footprint = Math.Max(end.X - start.X, end.Z - start.Z);
		int spacing = footprint + Config.WorldgenBufferBlocks;
		int spacingSq = spacing * spacing;

		foreach (Village existing in villageManager.Villages.Values)
		{
			if (existing?.Pos == null) continue;
			int dx = existing.Pos.X - start.X;
			int dz = existing.Pos.Z - start.Z;
			if (dx * dx + dz * dz < spacingSq) return true;
		}
		return false;
	}

	// Buffer around the true world spawn (map center), not (0,0) which is just the map corner.
	private bool TooCloseToSpawn(BlockPos villageStart, BlockPos villageEnd, int safeRadius = 300)
	{
		int spawnX = sapi.World.BlockAccessor.MapSizeX / 2;
		int spawnZ = sapi.World.BlockAccessor.MapSizeZ / 2;

		int closestX = Math.Max(villageStart.X, Math.Min(spawnX, villageEnd.X));
		int closestZ = Math.Max(villageStart.Z, Math.Min(spawnZ, villageEnd.Z));

		long dx = closestX - spawnX;
		long dz = closestZ - spawnZ;

		return (dx * dx + dz * dz) < (long)safeRadius * safeRadius;
	}
}
