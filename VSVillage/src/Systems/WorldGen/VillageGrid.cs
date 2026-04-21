using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class VillageGrid
{
	public const int pathWidth = 2;

	public const int squareSize = 7;

	public EnumgGridSlot[,] grid;

	public List<StructureWithOrientation> structures = new List<StructureWithOrientation>();

	public int capacity;

	public readonly int width;

	public readonly int height;

	public int avgheight;

	public VillageType VillageType;

	public VillageGrid(int width = 1, int height = 1)
	{
		this.width = width * 8 + 1;
		this.height = height * 8 + 1;
		capacity = this.width / 2 * (this.height / 2);
	}

	public void Init(VillageType type, LCGRandom rand, ICoreAPI api)
	{
		grid = new EnumgGridSlot[width, height];
		for (int i = 0; i < width; i++)
		{
			for (int j = 0; j < height; j++)
			{
				grid[i, j] = EnumgGridSlot.EMPTY;
			}
		}
		VillageType = type;
		foreach (StructureGroup structureGroup in type.StructureGroups)
		{
			if (structureGroup.MatchingStructures.Count == 0)
			{
				api.Logger.Error("Could not find any matching structures for group {0}!", structureGroup.Code);
				continue;
			}
			int num = rand.NextInt(structureGroup.MaxStructuresPerVillage + 1 - structureGroup.MinStructuresPerVillage) + structureGroup.MinStructuresPerVillage;
			for (int k = 0; k < num; k++)
			{
				tryAddStructure(structureGroup.MatchingStructures[rand.NextInt(structureGroup.MatchingStructures.Count)], rand);
			}
		}
	}

	public bool BigSlotAvailable(int x, int y)
	{
		return grid[x * 8 + 1, y * 8 + 1] == EnumgGridSlot.EMPTY;
	}

	public bool MediumSlotAvailable(int x, int y)
	{
		return grid[x * 4 + 1, y * 4 + 1] == EnumgGridSlot.EMPTY;
	}

	public bool SmallSlotAvailable(int x, int y)
	{
		return grid[x * 2 + 1, y * 2 + 1] == EnumgGridSlot.EMPTY;
	}

	public void AddBigStructure(WorldGenVillageStructure structure, int x, int y, int orientation)
	{
		capacity -= 16;
		structures.Add(new StructureWithOrientation
		{
			structure = structure,
			orientation = orientation,
			gridCoords = new Vec2i(x * 8 + 1, y * 8 + 1)
		});
		for (int i = 0; i < 7; i++)
		{
			for (int j = 0; j < 7; j++)
			{
				grid[x * 8 + 1 + i, y * 8 + 1 + j] = EnumgGridSlot.STRUCTURE;
			}
		}
		switch (orientation)
		{
		case 0:
			grid[x * 8 + 4, y * 8 + 8] = EnumgGridSlot.STREET;
			break;
		case 1:
			grid[x * 8 + 8, y * 8 + 4] = EnumgGridSlot.STREET;
			break;
		case 2:
			grid[x * 8 + 4, y * 8] = EnumgGridSlot.STREET;
			break;
		case 3:
			grid[x * 8, y * 8 + 4] = EnumgGridSlot.STREET;
			break;
		}
	}

	public void AddMediumStructure(WorldGenVillageStructure structure, int x, int y, int orientation)
	{
		capacity -= 4;
		structures.Add(new StructureWithOrientation
		{
			structure = structure,
			orientation = orientation,
			gridCoords = new Vec2i(x * 4 + 1, y * 4 + 1)
		});
		for (int i = 0; i < 3; i++)
		{
			for (int j = 0; j < 3; j++)
			{
				grid[x * 4 + 1 + i, y * 4 + 1 + j] = EnumgGridSlot.STRUCTURE;
			}
		}
		switch (orientation)
		{
		case 0:
			grid[x * 4 + 2, y * 4 + 4] = EnumgGridSlot.STREET;
			break;
		case 1:
			grid[x * 4 + 4, y * 4 + 2] = EnumgGridSlot.STREET;
			break;
		case 2:
			grid[x * 4 + 2, y * 4] = EnumgGridSlot.STREET;
			break;
		case 3:
			grid[x * 4, y * 4 + 2] = EnumgGridSlot.STREET;
			break;
		}
	}

	public void AddSmallStructure(WorldGenVillageStructure structure, int x, int y, int orientation)
	{
		capacity--;
		structures.Add(new StructureWithOrientation
		{
			structure = structure,
			orientation = orientation,
			gridCoords = new Vec2i(x * 2 + 1, y * 2 + 1)
		});
		grid[x * 2 + 1, y * 2 + 1] = EnumgGridSlot.STRUCTURE;
		switch (orientation)
		{
		case 0:
			grid[x * 2 + 1, y * 2 + 2] = EnumgGridSlot.STREET;
			break;
		case 1:
			grid[x * 2 + 2, y * 2 + 1] = EnumgGridSlot.STREET;
			break;
		case 2:
			grid[x * 2 + 1, y * 2] = EnumgGridSlot.STREET;
			break;
		case 3:
			grid[x * 2, y * 2 + 1] = EnumgGridSlot.STREET;
			break;
		}
	}

	public bool tryAddStructure(WorldGenVillageStructure structure, LCGRandom random)
	{
		int orientation = random.NextInt(4);
		switch (structure.Size)
		{
		case EnumVillageStructureSize.LARGE:
		{
			if (capacity < 16)
			{
				return false;
			}
			List<Vec2i> list3 = new List<Vec2i>();
			for (int m = 0; m < width / 8; m++)
			{
				for (int n = 0; n < height / 8; n++)
				{
					if (BigSlotAvailable(m, n))
					{
						list3.Add(new Vec2i(m, n));
					}
				}
			}
			Vec2i vec2i3 = list3[random.NextInt(list3.Count)];
			AddBigStructure(structure, vec2i3.X, vec2i3.Y, orientation);
			return true;
		}
		case EnumVillageStructureSize.MEDIUM:
		{
			if (capacity < 4)
			{
				return false;
			}
			List<Vec2i> list2 = new List<Vec2i>();
			for (int k = 0; k < width / 4; k++)
			{
				for (int l = 0; l < height / 4; l++)
				{
					if (MediumSlotAvailable(k, l))
					{
						list2.Add(new Vec2i(k, l));
					}
				}
			}
			Vec2i vec2i2 = list2[random.NextInt(list2.Count)];
			AddMediumStructure(structure, vec2i2.X, vec2i2.Y, orientation);
			return true;
		}
		case EnumVillageStructureSize.SMALL:
		{
			if (capacity < 1)
			{
				return false;
			}
			List<Vec2i> list = new List<Vec2i>();
			for (int i = 0; i < width / 2; i++)
			{
				for (int j = 0; j < height / 2; j++)
				{
					if (SmallSlotAvailable(i, j))
					{
						list.Add(new Vec2i(i, j));
					}
				}
			}
			Vec2i vec2i = list[random.NextInt(list.Count)];
			AddSmallStructure(structure, vec2i.X, vec2i.Y, orientation);
			return true;
		}
		default:
			return false;
		}
	}

	public BlockPos getEnd(BlockPos start)
	{
		Vec2i vec2i = GridCoordsToMapCoords(width, height);
		return start.AddCopy(vec2i.X + 3, 20, vec2i.Y + 3);
	}

	public BlockPos getMiddle(BlockPos start)
	{
		Vec2i vec2i = GridCoordsToMapCoords(width, height);
		return start.AddCopy((vec2i.X + 3) / 2, 20, (vec2i.Y + 3) / 2);
	}

	public void connectStreets()
	{
		List<Vec2i> connectedStreets = new List<Vec2i>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		bool flag = false;
		for (int i = 0; i < width * height; i++)
		{
			if (grid[num, num2] == EnumgGridSlot.STREET)
			{
				addStreedToGrid(connectedStreets, new Vec2i(num, num2));
			}
			num--;
			num2++;
			if (num < 0 || num2 >= height)
			{
				num3 = (flag ? (width - 1) : (num3 + 1));
				num4 = (flag ? (num4 + 1) : num4);
				flag |= num3 >= width - 1;
				num = num3;
				num2 = num4;
			}
		}
	}

	private void addStreedToGrid(List<Vec2i> connectedStreets, Vec2i newStreet)
	{
		if (connectedStreets.Count == 0)
		{
			connectedStreets.Add(newStreet);
			return;
		}
		Vec2i vec2i = connectedStreets[0];
		int num = Math.Abs(newStreet.X - vec2i.X) + Math.Abs(newStreet.Y - vec2i.Y);
		foreach (Vec2i connectedStreet in connectedStreets)
		{
			int num2 = Math.Abs(newStreet.X - connectedStreet.X) + Math.Abs(newStreet.Y - connectedStreet.Y);
			if (num2 < num)
			{
				vec2i = connectedStreet;
				num = num2;
			}
		}
		int num3 = vec2i.X;
		int num4 = vec2i.Y;
		bool flag = true;
		int num5 = Math.Sign((float)(newStreet.X - num3) + 0.5f);
		int num6 = Math.Sign((float)(newStreet.Y - num4) + 0.5f);
		bool? flag2 = null;
		while (Math.Abs(newStreet.X - num3) + Math.Abs(newStreet.Y - num4) > 1)
		{
			bool flag3 = num4 % 2 == 0 && inWidthBounds(num3 + num5) && grid[num3 + num5, num4] != EnumgGridSlot.STRUCTURE;
			bool flag4 = num3 % 2 == 0 && inHeightBounds(num4 + num6) && grid[num3, num4 + num6] != EnumgGridSlot.STRUCTURE;
			flag &= (flag3 && (newStreet.X - num3) * num5 > 0) || (flag4 && (newStreet.Y - num4) * num6 > 0);
			if (!flag)
			{
				if (!flag2.HasValue)
				{
					flag2 = flag3;
				}
				if ((flag4 && flag2 == true) || (flag3 && flag2 == false))
				{
					if (flag2 == true)
					{
						num4 += num6;
					}
					else
					{
						num3 += num5;
					}
					flag2 = null;
					flag = true;
					num5 = Math.Sign((float)(newStreet.X - num3) + 0.5f);
					num6 = Math.Sign((float)(newStreet.Y - num4) + 0.5f);
				}
				else if (flag2 == true)
				{
					num3 += num5;
				}
				else
				{
					num4 += num6;
				}
			}
			else if ((flag3 && Math.Abs(newStreet.X - num3) >= Math.Abs(newStreet.Y - num4)) || !flag4)
			{
				num3 += num5;
			}
			else if (flag4)
			{
				num4 += num6;
			}
			grid[num3, num4] = EnumgGridSlot.STREET;
			connectedStreets.Add(new Vec2i(num3, num4));
		}
	}

	public string debugPrintGrid()
	{
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < height; i++)
		{
			for (int j = 0; j < width; j++)
			{
				stringBuilder.Append((int)grid[j, height - 1 - i]).Append(" ");
			}
			stringBuilder.Append("\n");
		}
		return stringBuilder.ToString();
	}

	public Vec2i GridCoordsToMapCoords(int x, int y)
	{
		return new Vec2i(GridDistToMapDist(x), GridDistToMapDist(y));
	}

	public static int GridDistToMapDist(int x)
	{
		return x * 2 + x / 2 * 5;
	}

	public Vec2i GridCoordsToMapSize(int x, int y)
	{
		return new Vec2i((x % 2 == 0) ? 2 : 7, (y % 2 == 0) ? 2 : 7);
	}

	public void GenerateStreets(BlockPos start, IBlockAccessor blockAccessor, IWorldAccessor worldForCollectibleResolve)
	{
		int id = worldForCollectibleResolve.GetBlock(new AssetLocation(VillageType.StreetCode)).Id;
		int id2 = worldForCollectibleResolve.GetBlock(new AssetLocation(VillageType.BridgeCode)).Id;
		for (int i = 0; i < width; i++)
		{
			for (int j = 0; j < height; j++)
			{
				if (grid[i, j] == EnumgGridSlot.STREET)
				{
					GenerateStreetPart(start, i, j, blockAccessor, id, id2, i % 4 + j % 4 == 0);
				}
			}
		}
	}

	private void GenerateStreetPart(BlockPos start, int x, int z, IBlockAccessor blockAccessor, int idpath, int idbridge, bool generateWaypoint)
	{
		Vec2i vec2i = GridCoordsToMapCoords(x, z);
		Vec2i vec2i2 = GridCoordsToMapSize(x, z);
		for (int i = 0; i < vec2i2.X; i++)
		{
			for (int j = 0; j < vec2i2.Y; j++)
			{
				BlockPos blockPos = start.AddCopy(vec2i.X + i, 0, vec2i.Y + j);
				int terrainMapheightAt = blockAccessor.GetTerrainMapheightAt(blockPos);
				int rainMapHeightAt = blockAccessor.GetRainMapHeightAt(blockPos);
				int blockId = idbridge;
				blockPos.Y = rainMapHeightAt;
				if (terrainMapheightAt >= rainMapHeightAt || blockAccessor.GetBlock(blockPos, 2).Id == 0)
				{
					blockPos.Y = terrainMapheightAt;
					blockId = idpath;
				}
				blockAccessor.SetBlock(blockId, blockPos);
				blockAccessor.SetBlock(0, blockPos.Add(0, 1, 0));
				blockAccessor.SetBlock(0, blockPos.Add(0, 1, 0));
				if (generateWaypoint && i == 0 && j == 0)
				{
					blockAccessor.SetBlock(blockAccessor.GetBlock(new AssetLocation("vsvillage:waypoint")).Id, blockPos.Add(0, -1, 0));
					blockAccessor.SpawnBlockEntity("VillagerWaypoint", blockPos);
					blockAccessor.SetBlock(blockAccessor.GetBlock(new AssetLocation("game:multiblock-monolithic-0-p1-0")).Id, blockPos.Add(0, 1, 0));
					blockAccessor.SetBlock(blockAccessor.GetBlock(new AssetLocation("game:multiblock-monolithic-0-p2-0")).Id, blockPos.Add(0, 1, 0));
				}
			}
		}
	}

	public void GenerateHouses(BlockPos start, IBlockAccessor blockAccessor, IWorldAccessor worldForCollectibleResolve)
	{
		foreach (StructureWithOrientation structure in structures)
		{
			GenerateHouse(structure, start, blockAccessor, worldForCollectibleResolve);
		}
	}

	private void GenerateHouse(StructureWithOrientation house, BlockPos start, IBlockAccessor blockAccessor, IWorldAccessor worldForCollectibleResolve)
	{
		Vec2i vec2i = GridCoordsToMapCoords(house.gridCoords.X, house.gridCoords.Y);
		BlockPos blockPos = start.AddCopy(vec2i.X, 0, vec2i.Y);
		Vec2i vec2i2 = connectingPathOffset(house);
		BlockPos blockPos2 = blockPos.AddCopy(vec2i2.X, 0, vec2i2.Y);
		blockPos2.Y = blockAccessor.GetTerrainMapheightAt(blockPos2);
		while (blockAccessor.GetBlock(blockPos2.UpCopy(), 2).Id != 0)
		{
			blockPos2.Up();
		}
		blockPos.Y = blockPos2.Y + house.structure.VerticalOffset;
		house.structure.Generate(blockAccessor, worldForCollectibleResolve, blockPos, house.orientation);
	}

	private Vec2i connectingPathOffset(StructureWithOrientation house)
	{
		EnumVillageStructureSize size = house.structure.Size;
		return house.orientation switch
		{
			0 => new Vec2i(getSize(size) / 2, getSize(size)), 
			1 => new Vec2i(getSize(size), getSize(size) / 2), 
			2 => new Vec2i(getSize(size) / 2, -1), 
			3 => new Vec2i(-1, getSize(size) / 2), 
			_ => throw new ArgumentException("House has invalid orientation."), 
		};
	}

	private int getSize(EnumVillageStructureSize size)
	{
		return size switch
		{
			EnumVillageStructureSize.SMALL => 7, 
			EnumVillageStructureSize.MEDIUM => 16, 
			EnumVillageStructureSize.LARGE => 34, 
			_ => throw new ArgumentException("House has invalid size."), 
		};
	}

	private bool inHeightBounds(int y)
	{
		if (y >= 0)
		{
			return y < height;
		}
		return false;
	}

	private bool inWidthBounds(int x)
	{
		if (x >= 0)
		{
			return x < width;
		}
		return false;
	}
}
