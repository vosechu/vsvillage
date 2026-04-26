using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
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
			byEntity.World?.PlaySoundAt(new AssetLocation("vsvillage:sounds/horn.ogg"), byEntity.Pos.X, byEntity.Pos.Y, byEntity.Pos.Z);
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
		// One per village/area — scan 60 blocks for an existing mechhelper.
		Entity[] existing = byEntity.World.GetEntitiesAround(
			byEntity.Pos.XYZ, 60f, 20f,
			e => e.Code?.Domain == "vsvillage" && e.Code?.Path == "village-mechhelper");

		if (existing.Length > 0)
		{
			if (byEntity is EntityPlayer ep)
			{
				IServerPlayer sp = ep.Player as IServerPlayer;
				sp?.SendMessage(GlobalConstants.GeneralChatGroup,
					Lang.Get("vsvillage:horn-mechhelper-exists"),
					EnumChatType.Notification);
			}
			return;
		}

		AssetLocation assetLocation = new AssetLocation("vsvillage:village-mechhelper");
		EntityProperties entityType = byEntity.World.GetEntityType(assetLocation);
		if (entityType == null)
		{
			byEntity.World.Logger.Error("ItemVillagerHorn: No such entity - {0}", assetLocation);
			return;
		}

		Entity entity = byEntity.World.ClassRegistry.CreateEntity(entityType);
		if (entity != null)
		{
			entity.Pos.X = (float)(blockSel.Position.X + ((!blockSel.DidOffset) ? blockSel.Face.Normali.X : 0)) + 0.5f;
			entity.Pos.Y = blockSel.Position.Y + ((!blockSel.DidOffset) ? blockSel.Face.Normali.Y : 0);
			entity.Pos.Z = (float)(blockSel.Position.Z + ((!blockSel.DidOffset) ? blockSel.Face.Normali.Z : 0)) + 0.5f;
			entity.Pos.Yaw = (float)byEntity.World.Rand.NextDouble() * 2f * (float)Math.PI;
			entity.Pos.SetFrom(entity.Pos);
			entity.PositionBeforeFalling.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
			byEntity.World.SpawnEntity(entity);
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
