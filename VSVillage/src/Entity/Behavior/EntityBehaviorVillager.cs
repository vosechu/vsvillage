using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

public class EntityBehaviorVillager : EntityBehavior
{
    public VillagerPathfind Pathfind;

    public EnumVillagerProfession Profession;

    private Village _village;

    // Stored callback IDs so we can cancel them on early despawn.
    private long _initCallbackId = -1;
    private long _orphanCheckCallbackId = -1;
    private long _deathCleanupCallbackId = -1;

    public string VillageId => entity.WatchedAttributes.GetString("villageId");

    public string VillageName => entity.WatchedAttributes.GetString("villageName");

    public BlockPos Workstation
    {
        get
        {
            return entity.WatchedAttributes.GetBlockPos("workstation");
        }
        set
        {
            if (value != null)
            {
                entity.WatchedAttributes.SetBlockPos("workstation", value);
            }
            else
            {
                entity.WatchedAttributes.RemoveAttribute("workstation");
            }
            entity.WatchedAttributes.MarkPathDirty("workstation");
        }
    }

    public BlockPos Bed
    {
        get
        {
            return entity.WatchedAttributes.GetBlockPos("bed");
        }
        set
        {
            if (value != null)
            {
                entity.WatchedAttributes.SetBlockPos("bed", value);
            }
            else
            {
                entity.WatchedAttributes.RemoveAttribute("bed");
            }
            entity.WatchedAttributes.MarkPathDirty("bed");
        }
    }

    public Village Village
    {
        get
        {
            if (_village == null && !string.IsNullOrEmpty(VillageId))
            {
                _village = entity.Api?.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId);
            }
            return _village;
        }
        set
        {
            _village = value;
            entity.WatchedAttributes.SetString("villageId", value?.Id ?? "");
            entity.WatchedAttributes.MarkPathDirty("villageId");
            entity.WatchedAttributes.SetString("villageName", value?.Name ?? "");
            entity.WatchedAttributes.MarkPathDirty("villageName");
        }
    }

    public EntityBehaviorVillager(Entity entity)
        : base(entity)
    {
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        if (!Enum.TryParse(attributes["profession"].AsString(), ignoreCase: true, out EnumVillagerProfession parsedProfession))
        {
            entity.World.Logger.Warning("[VsVillage] Unknown profession '" + attributes["profession"].AsString() + "' on entity " + entity.EntityId + ", defaulting to villager.");
            parsedProfession = EnumVillagerProfession.farmer;
        }
        Profession = parsedProfession;
        if (entity.Api is ICoreServerAPI)
        {
            Pathfind = new VillagerPathfind(entity.Api as ICoreServerAPI);
            _initCallbackId = entity.World.RegisterCallback(delegate
            {
                _initCallbackId = -1;
                InitVillageAfterChunkLoading();
            }, 5000);
        }
    }

    private void InitVillageAfterChunkLoading()
    {
        entity.AnimManager?.StopAnimation("Lie");

        // Dead villagers should not persist — despawn the corpse immediately.
        if (!entity.Alive)
        {
            (entity.Api as ICoreServerAPI)?.World.DespawnEntity(entity, new EntityDespawnData
            {
                Reason = EnumDespawnReason.Death
            });
            return;
        }

        string savedVillageId = VillageId;
        VillageManager vm = entity.Api?.ModLoader.GetModSystem<VillageManager>();

        if (string.IsNullOrEmpty(savedVillageId))
        {
            // No VillageId — world-gen entity or unassigned founding villager. Leave it alone.
            entity.Api?.Logger.Notification(
                "[VsVillage] Villager " + entity.EntityId + " (" + entity.Code?.Path +
                ") has no VillageId — skipping auto-assignment.");
            return;
        }

        Village village = vm?.GetVillage(savedVillageId);

        if (village != null)
        {
            Village = village;
            village.VillagerSaveData[entity.EntityId] = new VillagerData
            {
                Id = entity.EntityId,
                Profession = Profession,
                Name = (entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "")
            };
        }
        else
        {
            // Stale VillageId — village was deleted or renamed. No auto-despawn.
            // Player can recover this villager via Management GUI "Recover Villagers".
            entity.Api?.Logger.Warning(
                "[VsVillage] Villager " + entity.EntityId + " (" + entity.Code?.Path +
                ") has stale VillageId '" + savedVillageId + "' — village not found. Use Recover Villagers in the Management GUI.");
        }
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        Village?.RemoveVillager(entity.EntityId);
        // Schedule corpse despawn — 60 s gives the player time to see what happened.
        if (entity.Api is ICoreServerAPI sapi)
        {
            long eid = entity.EntityId;
            _deathCleanupCallbackId = entity.World.RegisterCallback(delegate
            {
                _deathCleanupCallbackId = -1;
                Entity e = sapi.World.GetEntityById(eid);
                if (e != null && !e.Alive)
                {
                    sapi.World.DespawnEntity(e, new EntityDespawnData
                    {
                        Reason = EnumDespawnReason.Death
                    });
                }
            }, 60000);
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        // Cancel any pending callbacks so they don't fire against a dead entity reference.
        if (_initCallbackId != -1)        { entity.World.UnregisterCallback(_initCallbackId);        _initCallbackId = -1; }
        if (_orphanCheckCallbackId != -1) { entity.World.UnregisterCallback(_orphanCheckCallbackId); _orphanCheckCallbackId = -1; }
        if (_deathCleanupCallbackId != -1){ entity.World.UnregisterCallback(_deathCleanupCallbackId);_deathCleanupCallbackId = -1; }

        Village?.RemoveVillager(entity.EntityId);
    }

    public void RemoveVillage()
    {
        Village = null;
    }

    public override string PropertyName()
    {
        return "Villager";
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        base.GetInfoText(infotext);
        if (!string.IsNullOrEmpty(VillageName))
        {
            if (entity.Api is ICoreClientAPI coreClientAPI && coreClientAPI.Settings.Bool["showEntityDebugInfo"])
            {
                infotext.AppendLine(Lang.Get("vsvillage:lives-in-debug", Lang.Get(VillageName), (Workstation != null) ? ManagementGui.BlockPosToString(Workstation, entity.Api) : Lang.Get("vsvillage:nowhere"), (Bed != null) ? ManagementGui.BlockPosToString(Bed, entity.Api) : Lang.Get("vsvillage:nowhere")));
            }
            else
            {
                infotext.AppendLine(Lang.Get("vsvillage:lives-in", Lang.Get(VillageName)));
            }
        }
        infotext.AppendLine(Lang.Get("vsvillage:management-profession", Lang.Get("vsvillage:management-profession-" + Profession)));
    }
}