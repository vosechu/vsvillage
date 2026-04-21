using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class ItemVillagerHorn : Item
{
	public override string GetHeldTpUseAnimation(ItemSlot activeHotbarSlot, Entity byEntity)
	{
		return "eat";
	}

	public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
	{
		if (blockSel == null)
		{
			return;
		}
		handling = EnumHandHandling.PreventDefault;
		ICoreAPI coreAPI = byEntity.Api;
		if (coreAPI != null && coreAPI.Side == EnumAppSide.Server)
		{
			byEntity.World?.PlaySoundAt(new AssetLocation("vsvillage:sounds/horn.ogg"), ((Entity)byEntity).ServerPos.X, ((Entity)byEntity).ServerPos.Y, ((Entity)byEntity).ServerPos.Z);
			spawnVillager(byEntity, blockSel);
			if (!(byEntity is EntityPlayer) || (byEntity as EntityPlayer).Player.WorldData.CurrentGameMode != EnumGameMode.Creative)
			{
				slot.TakeOut(1);
				slot.MarkDirty();
			}
		}
	}

	private void spawnVillager(EntityAgent byEntity, BlockSelection blockSel)
	{
		AssetLocation assetLocation = new AssetLocation((byEntity.Api.World.Rand.Next(2) == 0) ? "vsvillage:villager-male-trader" : "vsvillage:villager-female-trader");
		EntityProperties entityType = byEntity.World.GetEntityType(assetLocation);
		if (entityType == null)
		{
			byEntity.World.Logger.Error("ItemCreature: No such entity - {0}", assetLocation);
			return;
		}
		Entity entity = byEntity.World.ClassRegistry.CreateEntity(entityType);
		if (entity != null)
		{
			entity.ServerPos.X = (float)(blockSel.Position.X + ((!blockSel.DidOffset) ? blockSel.Face.Normali.X : 0)) + 0.5f;
			entity.ServerPos.Y = blockSel.Position.Y + ((!blockSel.DidOffset) ? blockSel.Face.Normali.Y : 0);
			entity.ServerPos.Z = (float)(blockSel.Position.Z + ((!blockSel.DidOffset) ? blockSel.Face.Normali.Z : 0)) + 0.5f;
			entity.ServerPos.Yaw = (float)byEntity.World.Rand.NextDouble() * 2f * (float)Math.PI;
			entity.Pos.SetFrom(entity.ServerPos);
			entity.PositionBeforeFalling.Set(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);
			entity.Attributes.SetString("origin", "summoned");
			byEntity.World.SpawnEntity(entity);

			// Immediately integrate into nearby village so the villager
			// doesn't need to wait for the 5-second InitVillageAfterChunkLoading delay.
			EntityBehaviorVillager villagerBehavior = entity.GetBehavior<EntityBehaviorVillager>();
			if (villagerBehavior != null)
			{
				Village village = byEntity.Api.ModLoader.GetModSystem<VillageManager>()
					?.GetVillage(entity.ServerPos.AsBlockPos);
				if (village != null)
				{
					villagerBehavior.Village = village;
					BlockPos bedPos = village.FindFreeBed(entity.EntityId);
					if (bedPos != null)
					{
						villagerBehavior.Bed = bedPos;
					}
					village.VillagerSaveData[entity.EntityId] = new VillagerData
					{
						Id = entity.EntityId,
						Profession = villagerBehavior.Profession,
						Name = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "Trader"
					};
				}
			}
		}
		SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(50f, 100f, ColorUtil.ToRgba(75, 169, 169, 169), new Vec3d(), new Vec3d(2.0, 1.0, 2.0), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 3f, -0.075f, 0.5f, 3f, EnumParticleModel.Quad);
		simpleParticleProperties.MinPos = blockSel.Position.ToVec3d();
		byEntity.World.SpawnParticles(simpleParticleProperties);
	}

	public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
	{
		return secondsUsed < 3f;
	}

	public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
	{
		return new WorldInteraction[1]
		{
			new WorldInteraction
			{
				ActionLangCode = "vsvillage:interact-villager-horn",
				MouseButton = EnumMouseButton.Right
			}
		};
	}
}
