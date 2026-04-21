using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class Village
{
	[ProtoMember(1)]
	public BlockPos Pos;

	[ProtoMember(2)]
	public int Radius;

	[ProtoMember(3)]
	public string Name;

	[ProtoMember(4)]
	public Dictionary<BlockPos, VillagerBed> Beds = new Dictionary<BlockPos, VillagerBed>();

	[ProtoMember(5)]
	public Dictionary<BlockPos, VillagerWorkstation> Workstations = new Dictionary<BlockPos, VillagerWorkstation>();

	[ProtoMember(6)]
	public HashSet<BlockPos> Gatherplaces = new HashSet<BlockPos>();

	[ProtoMember(7)]
	public Dictionary<long, VillagerData> VillagerSaveData = new Dictionary<long, VillagerData>();

	[ProtoMember(8)]
	public HashSet<BlockPos> Waypoints = new HashSet<BlockPos>();

	public ICoreAPI Api;

	public string Id => "village-" + Pos.ToString();

	public List<EntityBehaviorVillager> Villagers => VillagerSaveData.Values.ToList().ConvertAll((VillagerData data) => Api.World.GetEntityById(data.Id)?.GetBehavior<EntityBehaviorVillager>());

	public void Init(ICoreAPI api)
	{
		Api = api;
	}

	public BlockPos FindFreeBed(long villagerId)
	{
		foreach (VillagerBed value in Beds.Values)
		{
			if (value.OwnerId == -1 || value.OwnerId == villagerId)
			{
				value.OwnerId = villagerId;
				string text = Api.World.GetEntityById(villagerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
				BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
				if (blockEntity != null && !string.IsNullOrEmpty(text))
				{
					blockEntity.OwnerName = text;
					blockEntity.MarkDirty();
				}
				return value.Pos;
			}
		}
		return null;
	}

	public BlockPos FindFreeWorkstation(long villagerId, EnumVillagerProfession profession)
	{
		foreach (VillagerWorkstation value in Workstations.Values)
		{
			if (value.Profession == profession && (value.OwnerId == -1 || value.OwnerId == villagerId))
			{
				value.OwnerId = villagerId;
				string text = Api.World.GetEntityById(villagerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
				BlockEntityVillagerWorkstation blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(value.Pos);
				if (blockEntity != null && !string.IsNullOrEmpty(text))
				{
					blockEntity.OwnerName = text;
					blockEntity.MarkDirty();
				}
				return value.Pos;
			}
		}
		return null;
	}

	public void ClearBedOwner(long villagerId)
	{
		foreach (VillagerBed value in Beds.Values)
		{
			if (value.OwnerId == villagerId)
			{
				value.OwnerId = -1L;
				BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
				if (blockEntity != null)
				{
					blockEntity.OwnerName = null;
					blockEntity.MarkDirty();
				}
			}
		}
	}

	public BlockPos FindRandomGatherplace()
	{
		if (Gatherplaces.Count == 0)
		{
			return null;
		}
		return Gatherplaces.ElementAt(Api.World.Rand.Next(Gatherplaces.Count));
	}

	public void RemoveVillager(long villagerId)
	{
		VillagerSaveData.Remove(villagerId);
		foreach (VillagerBed value in Beds.Values)
		{
			if (value.OwnerId == villagerId)
			{
				value.OwnerId = -1L;
				BlockEntityVillagerBed blockEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(value.Pos);
				if (blockEntity != null)
				{
					blockEntity.OwnerName = null;
					blockEntity.MarkDirty();
				}
			}
		}
		foreach (VillagerWorkstation value2 in Workstations.Values)
		{
			if (value2.OwnerId == villagerId)
			{
				value2.OwnerId = -1L;
			}
			BlockEntityVillagerWorkstation blockEntity2 = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(value2.Pos);
			if (blockEntity2 != null)
			{
				blockEntity2.OwnerName = null;
				blockEntity2.MarkDirty();
			}
		}
	}

	public BlockPos FindNearesWaypoint(BlockPos pos)
	{
		BlockPos blockPos = null;
		foreach (BlockPos waypoint in Waypoints)
		{
			if (blockPos == null || Pos.ManhattenDistance(pos) < blockPos.ManhattenDistance(pos))
			{
				blockPos = waypoint;
			}
		}
		return blockPos;
	}

	public void RemoveWaypoint(BlockPos pos)
	{
		Waypoints.Remove(pos);
	}
}
