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
		if (base.entity.World.Side != EnumAppSide.Server) return;
		string code = attributes?["entitycode"]?.AsString();
		if (string.IsNullOrEmpty(code))
		{
			base.entity.World.Logger.Warning("EntityBehaviorReplaceWithEntity: missing 'entitycode' attribute on {0}", base.entity.Code);
			return;
		}
		AssetLocation assetLocation = new AssetLocation(code);
		EntityProperties entityType = base.entity.World.GetEntityType(assetLocation);
		if (entityType == null)
		{
			base.entity.World.Logger.Error("ItemCreature: No such entity - {0}", assetLocation);
			return;
		}
		Entity entity = base.entity.World.ClassRegistry.CreateEntity(entityType);
		if (entity != null)
		{
			entity.Pos.SetFrom(base.entity.Pos);
			entity.PositionBeforeFalling.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
			base.entity.World.SpawnEntity(entity);
			base.entity.Die(EnumDespawnReason.Removed);
		}
	}
}
