using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerMeleeAttack : AiTaskMeleeAttack
{
	// Soldiers write from AI thread concurrently, sleep task reads. ConcurrentDictionary required.
	internal static readonly ConcurrentDictionary<string, long> VillageAlarms = new ConcurrentDictionary<string, long>();
	// Engaging soldier's entity id, keyed by village. ChaseEntity reads this so far-away allies can rally to the fight.
	internal static readonly ConcurrentDictionary<string, long> VillageAlarmEngagers = new ConcurrentDictionary<string, long>();
	internal const long AlarmDurationMs = 90_000L;

	// -ReportCooldownMs so the first engagement always fires (long.MinValue would underflow the cooldown subtraction).
	private long _lastReportedAtMs = -ReportCooldownMs;
	private const long ReportCooldownMs  = 60_000L;  // only report once per minute per soldier
	private const double ReportRadiusSq  = 200.0 * 200.0; // broadcast radius (blocks²)


	public AnimationMetaData baseAnimMeta { get; set; }

	public AnimationMetaData stabAnimMeta { get; set; }

	public AnimationMetaData slashAnimMeta { get; set; }

	public float unarmedDamage { get; set; }

	public float armedDamageMultiplier { get; set; }

	public AiTaskVillagerMeleeAttack(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		baseAnimMeta = animMeta;
		unarmedDamage = damage;
		armedDamageMultiplier = taskConfig["armedDamageMultiplier"].AsFloat(4f);
		if (taskConfig["stabanimation"].Exists)
		{
			stabAnimMeta = new AnimationMetaData
			{
				Code = taskConfig["stabanimation"].AsString()?.ToLowerInvariant(),
				Animation = taskConfig["stabanimation"].AsString()?.ToLowerInvariant(),
				// Read both lower-case and camelCase JSON keys (both ship in different configs).
				AnimationSpeed = taskConfig["stabanimationspeed"].AsFloat(
					taskConfig["stabanimationSpeed"].AsFloat(1f)),
				// Suppress concurrent idle/yawn anims so the strike plays cleanly (otherwise the soldier looks like she's shrugging at the target).
				SupressDefaultAnimation = true,
				Weight = 5f
			}.Init();
		}
		if (taskConfig["slashanimation"].Exists)
		{
			slashAnimMeta = new AnimationMetaData
			{
				Code = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				Animation = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				AnimationSpeed = taskConfig["slashanimationspeed"].AsFloat(
					taskConfig["slashanimationSpeed"].AsFloat(1f)),
				SupressDefaultAnimation = true,
				Weight = 5f
			}.Init();
		}
	}

	// Idle/gesture anims that can blend over the strike if not stopped first.
	private static readonly string[] BlockingIdleAnims = new[]
	{
		"idle", "idleyawn", "nod", "greet", "welcome", "laugh",
		"refuse", "clap", "jugglingballs-juggle"
	};

	public override bool IsTargetableEntity(Entity e, float range)
	{
		if (e == attackedByEntity && e != null && e.Alive)
		{
			return true;
		}
		return base.IsTargetableEntity(e, range);
	}

	public override void StartExecute()
	{
		// Stop any idle/gesture that would blend over the strike anim.
		foreach (string code in BlockingIdleAnims)
		{
			if (entity.AnimManager.IsAnimationActive(code))
			{
				entity.AnimManager.StopAnimation(code);
			}
		}

		string path = entity.Code?.Path ?? "";
		bool isCombatant = path.EndsWith("-soldier") || path.EndsWith("-archer");

		// Auto-equip a spear if bare-handed on engage. Activity equipspear is window-gated so noon engagements would otherwise fight unarmed.
		if (isCombatant && entity.RightHandItemSlot != null && entity.RightHandItemSlot.Empty)
		{
			Item spear = entity.World.GetItem(new AssetLocation("game:spear-generic-blackbronze"));
			if (spear != null)
			{
				entity.RightHandItemSlot.Itemstack = new ItemStack(spear);
				entity.RightHandItemSlot.MarkDirty();
			}
		}

		if (entity.RightHandItemSlot != null && !entity.RightHandItemSlot.Empty)
		{
			damage = Math.Max(entity.RightHandItemSlot.Itemstack.Item.AttackPower * armedDamageMultiplier, unarmedDamage);
			animMeta = (entity.RightHandItemSlot.Itemstack.Item.Code.Path.Contains("spear") ? stabAnimMeta : slashAnimMeta);
		}
		else if (isCombatant)
		{
			damage = unarmedDamage * armedDamageMultiplier;
			animMeta = ((stabAnimMeta != null && path.EndsWith("-archer")) ? stabAnimMeta : (slashAnimMeta ?? baseAnimMeta));
		}
		else
		{
			damage = unarmedDamage;
			animMeta = baseAnimMeta;
		}
		base.StartExecute();

		// Snap yaw to target so ContinueExecute sees correctYaw=true on first tick (otherwise damped turn rate from chase makes the strike lag 1-3s).
		if (targetEntity != null)
		{
			double dx = targetEntity.Pos.X - entity.Pos.X;
			double dz = targetEntity.Pos.Z - entity.Pos.Z;
			entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
		}

		// Soldiers report the engagement to nearby players and wake sleeping allies.
		TryReportHostile();
	}

	public override void OnEntityHurt(DamageSource source, float damage)
	{
		Entity causeEntity = source.CauseEntity;
		if (causeEntity == null || !causeEntity.HasBehavior<EntityBehaviorVillager>())
		{
			Entity sourceEntity = source.SourceEntity;
			if (sourceEntity == null || !sourceEntity.HasBehavior<EntityBehaviorVillager>())
			{
				base.OnEntityHurt(source, damage);
			}
		}
	}

	public void OnAllyAttacked(Entity byEntity)
	{
		if (targetEntity == null || !targetEntity.Alive)
		{
			targetEntity = byEntity;
		}
	}

	// Soldiers raise a village alarm (wakes other soldiers) and chat-notify nearby players. Throttled to once per minute per soldier.
	private void TryReportHostile()
	{
		if (entity.Code?.Path?.EndsWith("-soldier") != true) return;

		long now = entity.World.ElapsedMilliseconds;
		if (now - _lastReportedAtMs < ReportCooldownMs) return;
		if (targetEntity == null || !targetEntity.Alive) return;

		EntityBehaviorVillager vb = entity.GetBehavior<EntityBehaviorVillager>();
		Village village = vb?.Village;
		if (village == null) return;

		_lastReportedAtMs = now;
		VillageAlarms[village.Id] = now;
		VillageAlarmEngagers[village.Id] = entity.EntityId;

		// Compose the message.
		string soldierName = entity.WatchedAttributes.GetString("nametag");
		if (string.IsNullOrEmpty(soldierName))
			soldierName = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "A soldier";

		string villageName = !string.IsNullOrEmpty(village.Name) ? village.Name : "the village";
		string enemyType  = FormatEnemyType(targetEntity.Code?.Path ?? "");
		string message    = $"{soldierName}, soldier of {villageName}, reports a hostile {enemyType}!";

		// Broadcast to players within range.
		ICoreServerAPI sapi = entity.Api as ICoreServerAPI;
		if (sapi == null) return;

		Vec3d myPos   = entity.Pos.XYZ;
		IPlayer[] all = sapi.World.AllOnlinePlayers;
		for (int i = 0; i < all.Length; i++)
		{
			IServerPlayer sp = all[i] as IServerPlayer;
			if (sp?.Entity == null) continue;
			if (sp.Entity.Pos.SquareDistanceTo(myPos) <= ReportRadiusSq)
				sp.SendMessage(0, message, EnumChatType.Notification);
		}
	}

	// Turns a raw entity code path ("drifter-normal", "wolf-male", "hellboar-female")
	// into a readable display name ("Drifter", "Wolf", "Hellboar").
	private static string FormatEnemyType(string codePath)
	{
		int dash = codePath.IndexOf('-');
		string name = dash > 0 ? codePath.Substring(0, dash) : codePath;
		if (name.Length == 0) return "hostile creature";
		return char.ToUpperInvariant(name[0]) + name.Substring(1);
	}
}
