using System;
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
	// ── Village alarm registry ────────────────────────────────────────────────
	// Keyed by Village.Id (string), value = ElapsedMilliseconds when alarm was raised.
	// Static so AiTaskVillagerSleep can read it without any registration ceremony.
	// Bounded by the number of distinct villages that have ever seen combat.
	internal static readonly Dictionary<string, long> VillageAlarms = new Dictionary<string, long>();
	internal const long AlarmDurationMs = 90_000L; // how long sleeping soldiers stay awake after an alarm

	// Initialise to -ReportCooldownMs so the very first engagement always fires.
	// Using long.MinValue caused unsigned-overflow in (now - long.MinValue) which
	// evaluated to a large negative number, making the cooldown check always skip.
	private long _lastReportedAtMs = -ReportCooldownMs;
	private const long ReportCooldownMs  = 60_000L;  // only report once per minute per soldier
	private const double ReportRadiusSq  = 200.0 * 200.0; // broadcast radius (blocks²)

	// ─────────────────────────────────────────────────────────────────────────

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
				AnimationSpeed = taskConfig["stabanimationSpeed"].AsFloat(1f)
			}.Init();
		}
		if (taskConfig["slashanimation"].Exists)
		{
			slashAnimMeta = new AnimationMetaData
			{
				Code = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				Animation = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				AnimationSpeed = taskConfig["slashanimationSpeed"].AsFloat(1f)
			}.Init();
		}
	}

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
		string path = entity.Code?.Path ?? "";
		bool isCombatant = path.EndsWith("-soldier") || path.EndsWith("-archer");
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

	/// <summary>
	/// Called at the start of each melee engagement.  Only soldiers send reports —
	/// archers are day guards and are asleep when most hostiles appear.
	/// Raises a village-wide alarm (wakes sleeping soldiers) and sends a chat
	/// notification to nearby players.  Throttled to once per minute per soldier.
	/// </summary>
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
		VillageAlarms[village.Id] = now; // wake any sleeping soldiers in this village

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

	/// <summary>
	/// Turns a raw entity code path ("drifter-normal", "wolf-male", "hellboar-female")
	/// into a readable display name ("Drifter", "Wolf", "Hellboar").
	/// </summary>
	private static string FormatEnemyType(string codePath)
	{
		int dash = codePath.IndexOf('-');
		string name = dash > 0 ? codePath.Substring(0, dash) : codePath;
		if (name.Length == 0) return "hostile creature";
		return char.ToUpperInvariant(name[0]) + name.Substring(1);
	}
}
