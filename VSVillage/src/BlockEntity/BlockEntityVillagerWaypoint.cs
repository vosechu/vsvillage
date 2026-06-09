using Vintagestory.API.Common;

namespace VsVillage;

public class BlockEntityVillagerWaypoint : BlockEntityVillagerPOI
{
	public override void AddToVillage(Village village)
	{
		if (village != null && Api.Side == EnumAppSide.Server)
		{
			village.Waypoints.Add(Pos);
			village.BuildWaypointGraph();
		}
	}

	// No Initialize override: base AddToVillage handles it. Earlier override double-added and skipped a null guard.

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
