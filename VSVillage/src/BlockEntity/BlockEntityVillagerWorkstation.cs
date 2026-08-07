using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace VsVillage;

public class BlockEntityVillagerWorkstation : BlockEntityVillagerPOI
{
	// Display mirror of VillagerWorkstation.ShopType. Village data stays authoritative;
	// this exists so the hover tooltip can read it client side.
	public string ShopType { get; set; }

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

	// Same reason the base reconciles OwnerName: the tooltip mirror can go stale if the
	// block entity is rebuilt while village data keeps the real value.
	public override void Initialize(ICoreAPI api)
	{
		base.Initialize(api);
		if (api.Side == EnumAppSide.Client || Profession != EnumVillagerProfession.trader) return;

		Village village = api.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId);
		if (village == null || !village.Workstations.TryGetValue(Pos, out VillagerWorkstation ws)) return;

		string real = TraderShopType.Sanitize(ws.ShopType);
		if (TraderShopType.Sanitize(ShopType) != real)
		{
			ShopType = real;
			MarkDirty();
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

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
	{
		base.FromTreeAttributes(tree, worldAccessForResolve);
		ShopType = tree.GetString("shopType");
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree);
		tree.SetString("shopType", ShopType);
	}

	public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
	{
		base.GetBlockInfo(forPlayer, dsc);
		// Gated on village membership like the base's resides-in line: an unregistered
		// station has no shop to speak of.
		if (Profession == EnumVillagerProfession.trader && !string.IsNullOrEmpty(VillageName))
		{
			dsc.AppendLine().Append(Lang.Get("vsvillage:shoptype-info",
				Lang.Get("vsvillage:shoptype-" + TraderShopType.Sanitize(ShopType))));
		}
	}
}
