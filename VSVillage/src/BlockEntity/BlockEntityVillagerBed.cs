namespace VsVillage;

public class BlockEntityVillagerBed : BlockEntityVillagerPOI
{
	public float Yaw => base.Block.Attributes["yaw"].AsFloat();

	public override void AddToVillage(Village village)
	{
		village.Beds[Pos] = new VillagerBed
		{
			OwnerId = -1L,
			Pos = Pos
		};
	}

	public override void RemoveFromVillage(Village village)
	{
		village?.Beds.Remove(Pos);
	}

	public override bool BelongsToVillage(Village village)
	{
		if (village.Id == base.VillageId && village.Name == base.VillageName)
		{
			return village.Beds.ContainsKey(Pos);
		}
		return false;
	}
}
