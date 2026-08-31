using System;
using System.Collections.Generic;
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

    // Slots 0 and 1 of the villagerinv are the villager's hands, not free space:
    // EntityDressedHumanoid maps RightHandItemSlot to Inventory[0] and LeftHandItemSlot to
    // Inventory[1]. Anything a villager picks up goes at or after this index. Write below it
    // and you overwrite whatever the villager is holding; drop below it and you hand the
    // player equipment the villager was issued rather than items a player ever placed, which
    // makes killing villagers a way to farm it. Checked against VSSurvivalMod: nothing in
    // vanilla touches slots 2 and up today. A future game update could, and this reservation
    // would break with no error, so re-check it after a game version bump.
    public const int FirstCarrySlot = 2;

    public InventoryBase Inventory => entity.GetBehavior<EntityBehaviorVillagerInv>()?.Inventory;

    // Empty when villager.json stops listing the "villagerinventory" behavior. Callers must
    // handle that rather than assume a villager can carry anything.
    public IEnumerable<ItemSlot> CarrySlots()
    {
        InventoryBase inventory = Inventory;
        if (inventory == null) yield break;
        for (int i = FirstCarrySlot; i < inventory.Count; i++)
        {
            yield return inventory[i];
        }
    }

    public BlockPos GuardPost
    {
        get
        {
            return entity.WatchedAttributes.GetBlockPos("guardpost");
        }
        set
        {
            if (value != null)
            {
                entity.WatchedAttributes.SetBlockPos("guardpost", value);
            }
            else
            {
                entity.WatchedAttributes.RemoveAttribute("guardpost");
            }
            entity.WatchedAttributes.MarkPathDirty("guardpost");
        }
    }

    public EnumGuardShift GuardShift
    {
        get
        {
            return GuardDuty.ParseShift(entity.WatchedAttributes.GetString("guardshift"));
        }
        set
        {
            if (value != EnumGuardShift.none)
            {
                entity.WatchedAttributes.SetString("guardshift", value.ToString());
            }
            else
            {
                entity.WatchedAttributes.RemoveAttribute("guardshift");
            }
            entity.WatchedAttributes.MarkPathDirty("guardshift");
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

            ApplyShopType(village);
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

    // TradeProps is set from entity JSON during Initialize, which runs before this, so the
    // stall's specialty has to be re-applied here. Rebuild only when the stock is stale,
    // otherwise a reload would reshuffle every trader's inventory.
    private void ApplyShopType(Village village)
    {
        if (Profession != EnumVillagerProfession.trader) return;

        BlockPos wsPos = Workstation;
        if (wsPos == null || !village.Workstations.TryGetValue(wsPos, out VillagerWorkstation ws)) return;

        TraderShopType.ApplyForWorkstation(entity, ws.ShopType);
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        Village?.RemoveVillager(entity.EntityId);
        // Carry slots only, for as long as a villager's tools are conjured rather than made. The
        // vanilla "dropContentsOnDeath" attribute is EntityBehaviorContainer.Inventory.DropAll,
        // which empties the hands too, so today it turns a death into a source of items nobody
        // crafted. Once the smith actually produces what villagers carry, dropping the hands
        // becomes fair and this can widen to the whole inventory.
        if (entity.World.Side == EnumAppSide.Server)
        {
            foreach (ItemSlot slot in CarrySlots())
            {
                if (slot.Empty) continue;
                entity.World.SpawnItemEntity(slot.TakeOutWhole(), entity.Pos.XYZ);
                slot.MarkDirty();
            }
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
                infotext.AppendLine(Lang.Get("vsvillage:lives-in-debug", VillageName, (Workstation != null) ? ManagementGui.BlockPosToString(Workstation, entity.Api) : Lang.Get("vsvillage:nowhere"), (Bed != null) ? ManagementGui.BlockPosToString(Bed, entity.Api) : Lang.Get("vsvillage:nowhere")));
            }
            else
            {
                infotext.AppendLine(Lang.Get("vsvillage:lives-in", VillageName));
            }
        }
        infotext.AppendLine(Lang.Get("vsvillage:management-profession", Lang.Get("vsvillage:management-profession-" + Profession)));

        BlockPos post = GuardPost;
        if (post != null && GuardShift != EnumGuardShift.none)
        {
            infotext.AppendLine(Lang.Get("vsvillage:guardpost-duty",
                Lang.Get("vsvillage:guardpost-shift-" + GuardShift),
                ManagementGui.BlockPosToString(post, entity.Api)));
        }

        string carried = DescribeCarriedItems();
        if (carried != null)
        {
            infotext.AppendLine(Lang.Get("vsvillage:villager-carrying", carried));
        }
    }

    // Null when nothing is carried, so the caller prints no line at all rather than an empty
    // "Carrying:". Reads CarrySlots, so the hands are left out on purpose: a conjured spear is
    // not something the villager picked up, and listing it would read as loot.
    private string DescribeCarriedItems()
    {
        List<string> parts = null;
        foreach (ItemSlot slot in CarrySlots())
        {
            if (slot.Empty) continue;
            (parts ??= new List<string>()).Add($"{slot.Itemstack.StackSize}x {slot.Itemstack.GetName()}");
        }
        return parts == null ? null : string.Join(", ", parts);
    }

    // Push the carry slots to the client. They live in WatchedAttributes, but only ToBytes
    // re-serializes them: a pickup mid-life marks the path dirty without refreshing what it
    // holds, so the mouseover would keep showing the previous contents. Call this after
    // changing what a villager carries, or the readout quietly lies.
    public void SyncInventory()
    {
        entity.GetBehavior<EntityBehaviorVillagerInv>()?.storeInv();
    }
}