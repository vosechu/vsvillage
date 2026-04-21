using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerSocialize : AiTaskGotoAndInteract
{
	public Entity other { get; set; }

	public AiTaskVillagerSocialize(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
	}

	protected override Vec3d GetTargetPos()
	{
		Entity[] entitiesAround = entity.World.GetEntitiesAround(((Entity)entity).ServerPos.XYZ, base.maxDistance, 2f, (Entity friend) => (friend is EntityVillager && friend != entity && friend.Alive) || friend is EntityPlayer);
		if (entitiesAround.Length != 0)
		{
			other = entitiesAround[entity.World.Rand.Next(0, entitiesAround.Length)];
			return other.ServerPos.XYZ;
		}
		return null;
	}

	protected override bool InteractionPossible()
	{
		if (other == null || other.ServerPos == null)
		{
			return false;
		}
		bool flag = ((Entity)entity).ServerPos.SquareDistanceTo(other.ServerPos) < 4f;
		if (flag)
		{
			(entity.Api as ICoreServerAPI).Network.BroadcastEntityPacket(entity.EntityId, 203, SerializerUtil.Serialize(0));
		}
		return flag;
	}

	protected override void ApplyInteractionEffect()
	{
		other = null;
	}
}
