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

    // Single-stack carry slot: real items a villager is ferrying between a container and its
    // work. Mirrors the Workstation/Bed WatchedAttributes idiom so it persists and auto-syncs.
    // State-only in v1 (no in-hand rendering). A stack loaded from disk needs its Collectible
    // re-resolved, or it is inert — hence the ResolveBlockOrItem on read.
    public ItemStack CarrySlot
    {
        get
        {
            ItemStack stack = entity.WatchedAttributes.GetItemstack("carryslot");
            stack?.ResolveBlockOrItem(entity.World);
            return stack?.Collectible == null ? null : stack;
        }
        set
        {
            if (value != null)
            {
                entity.WatchedAttributes.SetItemstack("carryslot", value);
            }
            else
            {
                entity.WatchedAttributes.RemoveAttribute("carryslot");
            }
            entity.WatchedAttributes.SetLong("carryslotChangedMs", entity.World.ElapsedMilliseconds);
            entity.WatchedAttributes.MarkPathDirty("carryslot");
        }
    }

    public bool IsCarryEmpty => CarrySlot == null;

    public long CarryChangedMs => entity.WatchedAttributes.GetLong("carryslotChangedMs", 0);

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

    // Updated by AiTaskGotoAndInteract.StartExecute whenever a path-based task fires.
    // AiTaskVillagerGotoWork reads this to bypass its time-window gate when the
    // villager has been idle for too long (catches a baker whose oven task is failing).
    public long LastBusyAtMs;

    public void TouchBusy() => LastBusyAtMs = entity.World.ElapsedMilliseconds;

    public EntityBehaviorVillager(Entity entity)
        : base(entity)
    {
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        LastBusyAtMs = entity.World.ElapsedMilliseconds;
        if (!Enum.TryParse(attributes["profession"].AsString(), ignoreCase: true, out EnumVillagerProfession parsedProfession))
        {
            entity.World.Logger.Warning("[VsVillage] Unknown profession '" + attributes["profession"].AsString() + "' on entity " + entity.EntityId + ", defaulting to villager.");
            parsedProfession = EnumVillagerProfession.farmer;
        }
        Profession = parsedProfession;
        if (entity.Api is ICoreServerAPI)
        {
            Pathfind = new VillagerPathfind(entity.Api as ICoreServerAPI);
            // The carry orphan timer stamps ElapsedMilliseconds, which is per-session and resets
            // on load — a stamp persisted from a prior session is "in the future" relative to the
            // new clock, so return-carry would never fire for a villager carrying across a reload.
            // Re-stamp to now on load so the 30 s window restarts from load.
            if (entity.WatchedAttributes.HasAttribute("carryslot"))
            {
                entity.WatchedAttributes.SetLong("carryslotChangedMs", entity.World.ElapsedMilliseconds);
            }
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

        // Dead villagers should not persist - despawn the corpse immediately.
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
            // No VillageId - world-gen entity or unassigned founding villager. Leave it alone.
            entity.Api?.Logger.Debug(
                "[VsVillage] Villager " + entity.EntityId + " (" + entity.Code?.Path +
                ") has no VillageId - skipping auto-assignment.");
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

            // Self-heal: if the villager remembers a workstation/bed and the village
            // entry is unowned (pre-fix save file, or some other path nulled the
            // OwnerId), re-claim it. We only re-claim free slots - if the player has
            // since reassigned that structure to someone else, leave it alone.
            BlockPos savedWs = Workstation;
            if (savedWs != null && village.Workstations.TryGetValue(savedWs, out VillagerWorkstation ws))
            {
                if (ws.OwnerId == -1L && ws.Profession == Profession)
                {
                    ws.OwnerId = entity.EntityId;
                    BlockEntityVillagerWorkstation wsBe = entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(savedWs);
                    if (wsBe != null)
                    {
                        wsBe.OwnerName = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
                        wsBe.MarkDirty();
                    }
                }
                else if (ws.OwnerId != entity.EntityId)
                {
                    // Someone else owns it now - drop the stale reference.
                    Workstation = null;
                }
            }

            BlockPos savedBed = Bed;
            if (savedBed != null && village.Beds.TryGetValue(savedBed, out VillagerBed bedEntry))
            {
                if (bedEntry.OwnerId == -1L)
                {
                    bedEntry.OwnerId = entity.EntityId;
                    BlockEntityVillagerBed bedBe = entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(savedBed);
                    if (bedBe != null)
                    {
                        bedBe.OwnerName = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName;
                        bedBe.MarkDirty();
                    }
                }
                else if (bedEntry.OwnerId != entity.EntityId)
                {
                    Bed = null;
                }
            }
        }
        else
        {
            // Stale VillageId - village was deleted or renamed. No auto-despawn.
            // Player can recover this villager via Management GUI "Recover Villagers".
            entity.Api?.Logger.Warning(
                "[VsVillage] Villager " + entity.EntityId + " (" + entity.Code?.Path +
                ") has stale VillageId '" + savedVillageId + "' - village not found. Use Recover Villagers in the Management GUI.");
        }
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        Village?.RemoveVillager(entity.EntityId);
        // Drop any carried stack as a world item so death conserves items instead of
        // destroying them. Server-side only (world mutation).
        if (entity.Api is ICoreServerAPI && CarrySlot != null)
        {
            ItemStack dropped = CarrySlot;
            CarrySlot = null;
            entity.World.SpawnItemEntity(dropped, entity.ServerPos.XYZ.AddCopy(0.0, 0.5, 0.0));
        }
        // Schedule corpse despawn - 60 s gives the player time to see what happened.
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
        if (_deathCleanupCallbackId != -1){ entity.World.UnregisterCallback(_deathCleanupCallbackId);_deathCleanupCallbackId = -1; }

        // Only clear village ownership for genuine removal. Transient despawn reasons
        // (OutOfRange = player walked away, Unload = region unloaded, Disconnect =
        // last player left) persist the entity to disk and respawn it intact, so
        // wiping OwnerId on workstations/beds would unassign every villager any time
        // the player took a long trip. Death is handled in OnEntityDeath above; its
        // cleanup callback redundantly fires us with Death later, which we let
        // through (harmless second call once entries are already gone).
        if (despawn != null
            && despawn.Reason != EnumDespawnReason.Death
            && despawn.Reason != EnumDespawnReason.Removed
            && despawn.Reason != EnumDespawnReason.Combusted
            && despawn.Reason != EnumDespawnReason.Expire
            && despawn.Reason != EnumDespawnReason.PickedUp)
        {
            return;
        }

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
        // .edi (the `edi` client command) toggles ClientSettings.ExtendedDebugInfo, whose
        // settings key is "extendedDebugInfo" — NOT "showEntityDebugInfo" (a different debug mode).
        if (entity.Api is ICoreClientAPI debugApi && debugApi.Settings.Bool["extendedDebugInfo"])
        {
            ItemStack carried = CarrySlot;
            infotext.AppendLine(Lang.Get("vsvillage:carrying", carried != null
                ? carried.StackSize + "x " + carried.GetName()
                : Lang.Get("vsvillage:nothing")));
        }
    }
}