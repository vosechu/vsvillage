using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Slot-1 ambient gesture task.  When another villager is already within
/// <c>maxdistance</c> blocks the villager turns to face them, plays a brief
/// gesture animation, and broadcasts the voice packet — no movement involved.
/// Safe to run concurrently with slot-0 work tasks without pulling the
/// villager away from their workstation.
/// </summary>
public class AiTaskVillagerAmbientChat : AiTaskBase
{
	private readonly string gestureAnim;
	private readonly int gestureDurationMs;
	private readonly float searchRadius;

	private Entity chatPartner;
	private long taskStartedAt;
	private int currentDurationMs;

	public AiTaskVillagerAmbientChat(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		gestureAnim       = taskConfig["gesture"].AsString("nod");
		gestureDurationMs = taskConfig["gestureDurationMs"].AsInt(2000);
		searchRadius      = taskConfig["maxdistance"].AsFloat(3.5f);
	}

	public override bool ShouldExecute()
	{
		if (entity.AnimManager.IsAnimationActive("Lie")) return false;
		if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, 0f)) return false;
		if (cooldownUntilMs > entity.World.ElapsedMilliseconds) return false;

		// Only fire when another villager is already nearby — no walking needed.
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
