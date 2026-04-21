using System;
using Vintagestory.API.Common;

namespace VsVillage;

public class BlockEntityVillagerBrazier : BlockEntityVillagerPOI
{
	public override void Initialize(ICoreAPI api)
	{
		base.Initialize(api);
		RegisterGameTickListener((Action<float>)delegate
		{
			if (Api.World.Calendar.FullHourOfDay < 17)
			{
				Extinguish();
			}
		}, 5000, 0);
	}

	public override void AddToVillage(Village village)
	{
		village.Gatherplaces.Add(Pos);
	}

	public override void RemoveFromVillage(Village village)
	{
		village?.Gatherplaces.Remove(Pos);
	}

	public override bool BelongsToVillage(Village village)
	{
		if (village.Id == base.VillageId && village.Name == base.VillageName)
		{
			return village.Gatherplaces.Contains(Pos);
		}
		return false;
	}

	public void Toggle(bool ignite)
	{
		if (ignite)
		{
			Ignite();
		}
		else
		{
			Extinguish();
		}
	}

	public void Extinguish()
	{
		if (base.Block.Variant["burnstate"] != "extinct")
		{
			Block block = Api.World.GetBlock(base.Block.CodeWithVariant("burnstate", "extinct"));
			Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
			base.Block = block;
		}
	}

	public void Ignite()
	{
		if (base.Block.Variant["burnstate"] != "lit")
		{
			Block block = Api.World.GetBlock(base.Block.CodeWithVariant("burnstate", "lit"));
			Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
			base.Block = block;
		}
	}
}
