using Vintagestory.API.Common;

namespace VsVillage;

public class BlockEntityVillagerWaypoint : BlockEntityVillagerPOI
{
	public override void AddToVillage(Village village)
	{
		if (village != null && Api.Side == EnumAppSide.Server)
		{
			village.Waypoints.Add(Pos);
		}
	}

	public override void Initialize(ICoreAPI api)
	{
		base.Initialize(api);
		Api.ModLoader.GetModSystem<VillageManager>().GetVillage(base.VillageId)?.Waypoints.Add(Pos);
	}

	public override void RemoveFromVillage(Village village)
	{
		village?.RemoveWaypoint(Pos);
	}

	public override bool BelongsToVillage(Village village)
	{
		if (village.Id == base.VillageId && village.Name == base.VillageName)
		{
			return village.Waypoints.Contains(Pos);
		}
		return false;
	}
}
