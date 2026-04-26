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

	// Per-instance applicability — set once in the constructor, checked every tick.
	private readonly bool applicable;
	private readonly string[] excludeSuffixes;

	public AiTaskVillagerSocialize(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		// "onlyForEntitySuffix": if set, only entities whose code ends with this suffix run this task.
		string onlyFor = taskConfig["onlyForEntitySuffix"].AsString(null);
		applicable = onlyFor == null || (entity.Code?.Path?.EndsWith(onlyFor) ?? false);

		// "excludeEntitySuffixes": array of suffixes — entities matching ANY of these skip this task.
		JsonObject exclNode = taskConfig["excludeEntitySuffixes"];
		if (exclNode != null && exclNode.Exists)
			excludeSuffixes = exclNode.AsArray(System.Array.Empty<string>());
		else
			excludeSuffixes = System.Array.Empty<string>();
	}

	/// <summary>
	/// Block socialize when sleeping, outside time window, or excluded by entity suffix.
	/// </summary>
	public override bool ShouldExecute()
	{
		if (!applicable) return false;

		string path = entity?.Code?.Path;
		if (path != null)
		{
			foreach (string s in excludeSuffixes)
				if (path.EndsWith(s)) return false;
		}

		// Never socialize while sleeping.
		if (entity.AnimManager.IsAnimationActive("Lie"))
			return false;

		// Honour the configured day-time window.
		if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, 0f))
			return false;

		return base.ShouldExecute();
	}

	/// <summary>
	/// Abort mid-task if the villager falls asleep after socialize started.
	/// </summary>
	public override bool ContinueExecute(float dt)
	{
		if (entity.AnimManager.IsAnimationActive("Lie"))
			return false;

		return base.ContinueExecute(dt);
	}

	protected override Vec3d GetTargetPos()
	{
		if (entity.MountedOn != null)
			return null;

		if (entity.AnimManager.IsAnimationActive("Lie"))
			return null;

		Entity[] entitiesAround = entity.World.GetEntitiesAround(entity.Pos.XYZ, base.maxDistance, 2f,
			(Entity friend) =>
				(friend is EntityVillager fv && fv != entity && fv.Alive
				 && fv.MountedOn == null && !friend.AnimManager.IsAnimationActive("Lie"))
				|| friend is EntityPlayer);

		if (entitiesAround.Length != 0)
		{
			other = entitiesAround[entity.World.Rand.Next(0, entitiesAround.Length)];
			return other.Pos.XYZ;
		}
		return null;
	}

	protected override bool InteractionPossible()
	{
		if (other == null || other.Pos == null)
			return false;

		bool flag = entity.Pos.SquareDistanceTo(other.Pos) < 4f;
		if (flag)
		{
			var sapi = entity.Api as ICoreServerAPI;
			if (sapi != null)
			{
				// This villager greets.
				sapi.Network.BroadcastEntityPacket(entity.EntityId, 203, SerializerUtil.Serialize(0));
				// Target villager waves back — but only if they aren't sleeping.
				if (!other.AnimManager.IsAnimationActive("Lie"))
					sapi.Network.BroadcastEntityPacket(other.EntityId, 203, SerializerUtil.Serialize(0));
			}
		}
		return flag;
	}

	protected override void ApplyInteractionEffect()
	{
		other = null;
	}
}
