using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VsVillage;

public class InventoryVillagerGear : InventoryBase
{
	private ItemSlot[] slots;

	private string owningEntity;

	public ItemSlot leftHandSlot { get; set; }

	public ItemSlot rightHandSlot { get; set; }

	public override ItemSlot this[int slotId]
	{
		get
		{
			return slots[slotId];
		}
		set
		{
			slots[slotId] = value;
		}
	}

	public override int Count => slots.Length;

	public InventoryVillagerGear(string owningEntity, string inventoryID, ICoreAPI api)
		: base(inventoryID, api)
	{
		InventoryVillagerGear inventory = this;
		this.owningEntity = owningEntity;
		leftHandSlot = new ItemSlotUniversal(this);
		rightHandSlot = new ItemSlotUniversal(this);
		List<ItemSlot> list = new List<string>(Enum.GetNames(typeof(VillagerGearType))).ConvertAll((Converter<string, ItemSlot>)((string gearType) => new ItemSlotVillagerGear((VillagerGearType)Enum.Parse(typeof(VillagerGearType), gearType), owningEntity, inventory)));
		list.Add(rightHandSlot);
		list.Add(leftHandSlot);
		slots = list.ToArray();
	}

	public override void FromTreeAttributes(ITreeAttribute tree)
	{
		slots = SlotsFromTreeAttributes(tree, slots);
		owningEntity = tree.GetString("owningEntity");
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		SlotsToTreeAttributes(slots, tree);
		tree.SetString("owningEntity", owningEntity);
	}

	public override void LateInitialize(string inventoryID, ICoreAPI api)
	{
		base.LateInitialize(inventoryID, api);
		ItemSlot[] array = slots;
		for (int i = 0; i < array.Length; i++)
		{
			array[i].Itemstack?.ResolveBlockOrItem(api.World);
		}
	}

	public override WeightedSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots = null)
	{
		if (sourceSlot?.Itemstack?.Item is ItemVillagerGear itemVillagerGear)
		{
			return new WeightedSlot
			{
				weight = 1f,
				slot = slots[(int)itemVillagerGear.type]
			};
		}
		if (rightHandSlot.Empty)
		{
			return new WeightedSlot
			{
				weight = 1f,
				slot = rightHandSlot
			};
		}
		if (leftHandSlot.Empty)
		{
			return new WeightedSlot
			{
				weight = 0.5f,
				slot = leftHandSlot
			};
		}
		return base.GetBestSuitedSlot(sourceSlot, op, skipSlots);
	}
}
