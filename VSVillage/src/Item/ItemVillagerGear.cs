using System;
using Vintagestory.API.Common;

namespace VsVillage;

public class ItemVillagerGear : Item
{
	public VillagerGearType type
	{
		get
		{
			if (Enum.TryParse<VillagerGearType>(Variant["type"].ToUpper(), out var result))
			{
				return result;
			}
			return VillagerGearType.HEAD;
		}
	}

	public string weaponAssetLocation => Attributes["weaponAssetLocation"].AsString();

	public int backpackSlots => Attributes["backpackslots"].AsInt();
}
