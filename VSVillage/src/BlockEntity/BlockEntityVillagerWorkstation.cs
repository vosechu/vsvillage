using System;

namespace VsVillage;

public class BlockEntityVillagerWorkstation : BlockEntityVillagerPOI
{
	// Defensive: missing/unparseable variant falls back to farmer so misconfigured workstation blocks don't crash chunk load.
	public EnumVillagerProfession Profession
	{
		get
		{
			string variant = base.Block?.Variant?["profession"];
			if (string.IsNullOrEmpty(variant)) return EnumVillagerProfession.farmer;
			return Enum.TryParse<EnumVillagerProfession>(variant, out var p) ? p : EnumVillagerProfession.farmer;
		}
	}

	public override void AddToVillage(Village village)
	{
		village.Workstations[Pos] = new VillagerWorkstation
		{
			OwnerId = -1L,
			Pos = Pos,
			Profession = Profession
		};
	}

	protected override long GetCurrentOwnerId(Village village)
	{
		return (village != null && village.Workstations.TryGetValue(Pos, out VillagerWorkstation ws)) ? ws.OwnerId : -1L;
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
