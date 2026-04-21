using Vintagestory.API.Common;

namespace VsVillage;

public class ItemSlotVillagerGear : ItemSlotSurvival
{
	private VillagerGearType type { get; }

	private string owningEntity { get; }

	public ItemSlotVillagerGear(VillagerGearType type, string owningEntity, InventoryBase inventory)
		: base(inventory)
	{
		this.type = type;
		this.owningEntity = owningEntity;
	}

	public ItemSlotVillagerGear(InventoryBase inventory)
		: base(inventory)
	{
	}

	public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
	{
		if (base.CanTakeFrom(sourceSlot, priority))
		{
			return isCorrectAccessory(sourceSlot);
		}
		return false;
	}

	public override bool CanHold(ItemSlot itemstackFromSourceSlot)
	{
		if (base.CanHold(itemstackFromSourceSlot))
		{
			return isCorrectAccessory(itemstackFromSourceSlot);
		}
		return false;
	}

	private bool isCorrectAccessory(ItemSlot sourceSlot)
	{
		if (sourceSlot.Itemstack.Item is ItemVillagerGear itemVillagerGear)
		{
			return type == itemVillagerGear.type;
		}
		return false;
	}
}
