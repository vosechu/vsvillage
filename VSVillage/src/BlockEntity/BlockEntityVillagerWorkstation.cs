using System;

namespace VsVillage;

public class BlockEntityVillagerWorkstation : BlockEntityVillagerPOI
{
	public EnumVillagerProfession Profession => Enum.Parse<EnumVillagerProfession>(base.Block.Variant["profession"]);

	public override void AddToVillage(Village village)
	{
		village.Workstations[Pos] = new VillagerWorkstation
		{
			OwnerId = -1L,
			Pos = Pos,
			Profession = Profession
		};
	}

	public override void RemoveFromVillage(Village village)
	{
		village?.Workstations.Remove(Pos);
	}

	public override bool BelongsToVillage(Village village)
	{
		if (village.Id == base.VillageId && village.Name == base.VillageName)
		{
			return village.Workstations.ContainsKey(Pos);
		}
		return false;
	}
}
