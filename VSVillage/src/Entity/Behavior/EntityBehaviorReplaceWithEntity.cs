using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace VsVillage;

public class EntityBehaviorReplaceWithEntity : EntityBehavior
{
	public EntityBehaviorReplaceWithEntity(Entity entity)
		: base(entity)
	{
	}

	public override string PropertyName()
	{
		return "replacewithentity";
	}

	public override void Initialize(EntityProperties properties, JsonObject attributes)
	{
		base.Initialize(properties, attributes);
		AssetLocation assetLocation = new AssetLocation(attributes["entitycode"].AsString());
		EntityProperties entityType = base.entity.World.GetEntityType(assetLocation);
		if (entityType == null)
		{
			base.entity.World.Logger.Error("ItemCreature: No such entity - {0}", assetLocation);
			return;
		}
		Entity entity = base.entity.World.ClassRegistry.CreateEntity(entityType);
		if (entity != null)
		{
			entity.ServerPos.SetFrom(base.entity.ServerPos);
			entity.PositionBeforeFalling.Set(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);
			base.entity.World.SpawnEntity(entity);
			base.entity.Die(EnumDespawnReason.Removed);
		}
	}
}
