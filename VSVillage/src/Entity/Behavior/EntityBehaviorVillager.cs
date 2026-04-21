using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

public class EntityBehaviorVillager : EntityBehavior
{
	public VillagerPathfind Pathfind;

	public EnumVillagerProfession Profession;

	private Village _village;

	public string VillageId => entity.WatchedAttributes.GetString("villageId");

	public string VillageName => entity.WatchedAttributes.GetString("villageName");

	public BlockPos Workstation
	{
		get
		{
			return entity.WatchedAttributes.GetBlockPos("workstation");
		}
		set
		{
			if (value != null)
			{
				entity.WatchedAttributes.SetBlockPos("workstation", value);
			}
			else
			{
				entity.WatchedAttributes.RemoveAttribute("workstationX");
			}
			entity.WatchedAttributes.MarkPathDirty("workstationX");
		}
	}

	public BlockPos Bed
	{
		get
		{
			return entity.WatchedAttributes.GetBlockPos("bed");
		}
		set
		{
			if (value != null)
			{
				entity.WatchedAttributes.SetBlockPos("bed", value);
			}
			else
			{
				entity.WatchedAttributes.RemoveAttribute("bedX");
			}
			entity.WatchedAttributes.MarkPathDirty("bedX");
		}
	}

	public Village Village
	{
		get
		{
			if (_village == null && !string.IsNullOrEmpty(VillageId))
			{
				_village = entity.Api.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId);
			}
			return _village;
		}
		set
		{
			_village = value;
			entity.WatchedAttributes.SetString("villageId", value?.Id);
			entity.WatchedAttributes.MarkPathDirty("villageId");
			entity.WatchedAttributes.SetString("villageName", value?.Name);
			entity.WatchedAttributes.MarkPathDirty("villageName");
		}
	}

	public EntityBehaviorVillager(Entity entity)
		: base(entity)
	{
	}

	public override void Initialize(EntityProperties properties, JsonObject attributes)
	{
		Profession = Enum.Parse<EnumVillagerProfession>(attributes["profession"].AsString());
		if (entity.Api is ICoreServerAPI)
		{
			Pathfind = new VillagerPathfind(entity.Api as ICoreServerAPI);
			entity.World.RegisterCallback(delegate
			{
				InitVillageAfterChunkLoading();
			}, 5000);
		}
	}

	private void InitVillageAfterChunkLoading()
	{
		// Clear any persisted sleep pose from before server shutdown.
		// The sleep task will re-trigger StartSleeping() if it's still sleep time.
		entity.AnimManager?.StopAnimation("Lie");

		Village village = null;
		if (entity.Alive)
		{
			if (!string.IsNullOrEmpty(VillageId))
			{
				village = entity.Api.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId);
			}
			if (village == null)
			{
				village = entity.Api.ModLoader.GetModSystem<VillageManager>()?.GetVillage(entity.ServerPos.AsBlockPos);
			}
			if (village != null)
			{
				Village = village;
				village.VillagerSaveData[entity.EntityId] = new VillagerData
				{
					Id = entity.EntityId,
					Profession = Profession,
					Name = (entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "S\u0337\u0351\u0305\u0300\u035b\u0313\u030b\u030c\u0321\u032a\u0326\u032e\u031c\u032e\u0333e\u0338\u0305\u0303\u0332\u0326\u033b\u0317\u0349r\u0337\u030c\u0306\u0315\u0346\u035c\u0354\u032e\u032e\u0317v\u0335\u034a\u0304\u0358\u0360\u0315\u0348\u0325\u0329\u0333a\u0336\u0340\u0341\u0340\u0344\u031e\u0331\u035c\u0331\u035c\u033bn\u0337\u0343\u0307\u0358\u032b\u0355\u0323\u035ct\u0334\u0342\u0343\u0345\u033b\u032b\u0339\u033a\u033b\u0356")
				};
			}
		}
	}

	public override void OnEntityDeath(DamageSource damageSourceForDeath)
	{
		Village?.RemoveVillager(entity.EntityId);
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		Village?.RemoveVillager(entity.EntityId);
	}

	public void RemoveVillage()
	{
		Village = null;
	}

	public override string PropertyName()
	{
		return "Villager";
	}

	public override void GetInfoText(StringBuilder infotext)
	{
		base.GetInfoText(infotext);
		if (!string.IsNullOrEmpty(VillageName))
		{
			if (entity.Api is ICoreClientAPI coreClientAPI && coreClientAPI.Settings.Bool["showEntityDebugInfo"])
			{
				infotext.AppendLine(Lang.Get("vsvillage:lives-in-debug", Lang.Get(VillageName), (Workstation != null) ? ManagementGui.BlockPosToString(Workstation, entity.Api) : Lang.Get("vsvillage:nowhere"), (Bed != null) ? ManagementGui.BlockPosToString(Bed, entity.Api) : Lang.Get("vsvillage:nowhere")));
			}
			else
			{
				infotext.AppendLine(Lang.Get("vsvillage:lives-in", Lang.Get(VillageName)));
			}
		}
		infotext.AppendLine(Lang.Get("vsvillage:management-profession", Lang.Get("vsvillage:management-profession-" + Profession)));
	}
}
