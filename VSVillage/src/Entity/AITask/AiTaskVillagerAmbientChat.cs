using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

// Slot-1 ambient gesture task.  When another villager is already within
// maxdistance blocks the villager turns to face them, plays a brief
// gesture animation, and broadcasts the voice packet - no movement involved.
// Safe to run concurrently with slot-0 work tasks without pulling the
// villager away from their workstation.
public class AiTaskVillagerAmbientChat : AiTaskBase
{
	private readonly string gestureAnim;
	private readonly int gestureDurationMs;
	private readonly float searchRadius;
	private readonly bool applicable;
	private readonly string[] excludeSuffixes;

	private Entity chatPartner;
	private long taskStartedAt;
	private int currentDurationMs;

	public AiTaskVillagerAmbientChat(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		gestureAnim       = taskConfig["gesture"].AsString("nod");
		gestureDurationMs = taskConfig["gestureDurationMs"].AsInt(2000);
		searchRadius      = taskConfig["maxdistance"].AsFloat(3.5f);

		// "onlyForEntitySuffix": if set, only entities whose code ends with this suffix run this task.
		string onlyFor = taskConfig["onlyForEntitySuffix"].AsString(null);
		applicable = onlyFor == null || (entity.Code?.Path?.EndsWith(onlyFor) ?? false);

		// "excludeEntitySuffixes": array of suffixes - entities matching ANY of these skip this task.
		JsonObject exclNode = taskConfig["excludeEntitySuffixes"];
		if (exclNode != null && exclNode.Exists)
			excludeSuffixes = exclNode.AsArray(System.Array.Empty<string>());
		else
			excludeSuffixes = System.Array.Empty<string>();
	}

	public override bool ShouldExecute()
	{
		// Profession / entity-suffix gating. Constructor parses these from JSON but
		// the original ShouldExecute didn't enforce them - so an "excludeEntitySuffixes"
		// entry like ["-soldier", "-archer"] on the evening seiza variant of this task
		// was silently ignored, and soldiers/archers were sitting down at sunset
		// alongside civilians.
		if (!applicable) return false;
		string myPath = entity.Code?.Path ?? "";
		for (int i = 0; i < excludeSuffixes.Length; i++)
		{
			if (myPath.EndsWith(excludeSuffixes[i])) return false;
		}

		if (entity.AnimManager.IsAnimationActive("Lie")) return false;
		if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, 0f)) return false;
		if (cooldownUntilMs > entity.World.ElapsedMilliseconds) return false;

		// Only fire when another villager is already nearby - no walking needed.
		Entity[] nearby = entity.World.GetEntitiesAround(entity.Pos.XYZ, searchRadius, 2f,
			e => e is EntityVillager v && v != entity && v.Alive
			     && !v.AnimManager.IsAnimationActive("Lie"));

		if (nearby.Length == 0) return false;
		chatPartner = nearby[entity.World.Rand.Next(nearby.Length)];
		return true;
	}

	public override void StartExecute()
	{
		base.StartExecute();
		taskStartedAt     = entity.World.ElapsedMilliseconds;
		currentDurationMs = gestureDurationMs + entity.World.Rand.Next(600);

		// Turn to face the chat partner without moving.
		if (chatPartner != null)
		{
			double dx = chatPartner.Pos.X - entity.Pos.X;
			double dz = chatPartner.Pos.Z - entity.Pos.Z;
			entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
		}

		// Broadcast a voice greeting packet (same as AiTaskVillagerSocialize).
		if (entity.Api is ICoreServerAPI sapi)
			sapi.Network.BroadcastEntityPacket(entity.EntityId, 203, SerializerUtil.Serialize(0));

		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation      = gestureAnim,
			Code           = gestureAnim,
			AnimationSpeed = 1f,
			BlendMode      = EnumAnimationBlendMode.Average
		}.Init());
	}

	public override bool ContinueExecute(float dt)
	{
		if (entity.AnimManager.IsAnimationActive("Lie")) return false;
		return entity.World.ElapsedMilliseconds - taskStartedAt < currentDurationMs;
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation(gestureAnim);
		chatPartner = null;
		base.FinishExecute(cancelled); // sets cooldownUntilMs = ElapsedMilliseconds + random(mincooldown..maxcooldown)
	}
}
