using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public abstract class BlockEntityVillagerPOI : BlockEntity
{
	public string VillageId { get; set; }

	public string VillageName { get; set; }

	public string OwnerName { get; set; }

	public Vec3d Position => Pos.ToVec3d();

	public abstract void AddToVillage(Village village);

	public abstract void RemoveFromVillage(Village village);

	public abstract bool BelongsToVillage(Village village);

	public override void Initialize(ICoreAPI api)
	{
		base.Initialize(api);
		if (api.Side != EnumAppSide.Client)
		{
			VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
			Village village = null;

			if (!string.IsNullOrEmpty(VillageId))
			{
				village = vm?.GetVillage(VillageId);
				// Stale VillageId from a copy-paste / different save: the village we
				// found (if any) may be in a completely different location. If this
				// block is outside that village's radius, discard the stale reference
				// and fall back to a position search so we don't ghost-register.
				if (village != null && !IsWithinVillageRadius(village))
				{
					api.Logger.Warning("[VsVillage] Block at " + Pos + " has stale VillageId '" + VillageId + "' pointing to a village at " + village.Pos + " (radius " + village.Radius + ") — clearing stale data.");
					VillageId = null;
					VillageName = null;
					village = null;
					MarkDirty();
				}
			}

			if (village == null)
			{
				village = vm?.GetVillage(Pos);
			}

			if (village != null && !BelongsToVillage(village) && IsWithinVillageRadius(village))
			{
				VillageId = village.Id;
				VillageName = village.Name;
				AddToVillage(village);
			}
			else if (village == null)
			{
				// Not inside any known village radius — clear any leftover data so
				// this block doesn't silently claim a village it was pasted from.
				if (!string.IsNullOrEmpty(VillageId))
				{
					api.Logger.Warning("[VsVillage] Block at " + Pos + " references village '" + VillageId + "' but no matching village found — clearing.");
					VillageId = null;
					VillageName = null;
				}
			}
		}
	}

	private bool IsWithinVillageRadius(Village village)
	{
		if (village?.Pos == null) return false;
		int radius = village.Radius;
		return Math.Abs(Pos.X - village.Pos.X) <= radius &&
		       Math.Abs(Pos.Z - village.Pos.Z) <= radius;
	}

	public void RemoveVillage()
	{
		VillageId = null;
		VillageName = null;
		MarkDirty();
	}

	public override void OnBlockBroken(IPlayer byPlayer = null)
	{
		base.OnBlockBroken(byPlayer);
		RemoveFromVillage(Api.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId));
	}

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
	{
		base.FromTreeAttributes(tree, worldAccessForResolve);
		VillageId = tree.GetString("villageId");
		VillageName = tree.GetString("villageName");
		OwnerName = tree.GetString("ownerName");
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree);
		tree.SetString("villageId", VillageId);
		tree.SetString("villageName", VillageName);
		tree.SetString("ownerName", OwnerName);
	}

	public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
	{
		base.GetBlockInfo(forPlayer, dsc);
		if (!string.IsNullOrEmpty(VillageName))
		{
			dsc.AppendLine().Append(Lang.Get("vsvillage:resides-in", Lang.Get(VillageName)));
		}
		if (!string.IsNullOrEmpty(OwnerName))
		{
			dsc.AppendLine().Append(Lang.Get("vsvillage:owned-by", Lang.Get(OwnerName)));
		}
	}
}
