using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

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


	// OwnerId from the in-memory village registry, or -1 if untracked. Used to reconcile serialized OwnerName on chunk load.
	protected virtual long GetCurrentOwnerId(Village village) => -1L;

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
				// Stale VillageId (copy-paste or different save). If we're outside the resolved village's radius, drop the stale ref.
				if (village != null && !IsWithinVillageRadius(village))
				{
					api.Logger.Warning("[VsVillage] Block at " + Pos + " has stale VillageId '" + VillageId + "' pointing to a village at " + village.Pos + " (radius " + village.Radius + ") - clearing stale data.");
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
			else if (village == null && vm != null)
			{
				// Not inside any known village radius - clear any leftover data so
				// this block doesn't silently claim a village it was pasted from.
				// Gated on vm != null so a load-order race (POI initializes before
				// VillageManager finishes loading) doesn't wipe persistent state.
				if (!string.IsNullOrEmpty(VillageId))
				{
					api.Logger.Warning("[VsVillage] Block at " + Pos + " references village '" + VillageId + "' but no matching village found - clearing.");
					VillageId = null;
					VillageName = null;
				}
			}

			// Reconcile OwnerName with the in-memory OwnerId: clear if owner was fired offline, refill if owner self-healed with a fresh entity id.
			if (village != null)
			{
				long currentOwnerId = GetCurrentOwnerId(village);
				if (currentOwnerId == -1L)
				{
					if (!string.IsNullOrEmpty(OwnerName))
					{
						OwnerName = null;
						MarkDirty();
					}
				}
				else if (string.IsNullOrEmpty(OwnerName))
				{
					Entity owner = api.World.GetEntityById(currentOwnerId);
					string name = owner?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
					if (!string.IsNullOrEmpty(name))
					{
						OwnerName = name;
						MarkDirty();
					}
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
		// RemoveFromVillage must tolerate a null village; subclasses guard internally.
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
