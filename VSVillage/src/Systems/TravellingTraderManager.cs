using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

public class TravellingTraderManager : ModSystem
{
    private sealed class TraderEntry
    {
        public long TraderId;

        public long GuardId;

        public string VillageId;
    }

    private static readonly string[] Specialties = new string[9] { "agriculture", "artisan", "buildmaterials", "clothing", "commodities", "furniture", "luxuries", "survivalgoods", "treasurehunter" };

    private static readonly string[] Sexes = new string[2] { "male", "female" };

    private const float SpawnChancePerTick = 0.2f;

    private const int TickIntervalMs = 180000;

    private const float SpawnHourMin = 5f;

    private const float SpawnHourMax = 10f;

    private const float DepartureHour = 20f;

    private readonly Dictionary<string, TraderEntry> _active = new Dictionary<string, TraderEntry>();

    private ICoreServerAPI _sapi;

    private const int MarketStallScanRadius = 35;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        api.Event.RegisterGameTickListener(OnTick, 180000);
        api.Logger.Notification("[TravellingTraderManager] Started.");
    }

    public void OnTraderDespawned(long traderId, string villageId)
    {
        if (villageId != null)
        {
            _active.Remove(villageId);
            _sapi.Logger.Debug($"[TravellingTraderManager] Trader {traderId} removed from village {villageId}.");
        }
    }

    public BlockPos GetActiveStallPos(string villageId)
    {
        if (string.IsNullOrEmpty(villageId))
        {
            return null;
        }
        if (!_active.TryGetValue(villageId, out var entry))
        {
            return null;
        }
        EntityBehaviorTravellingTrader beh = _sapi.World.GetEntityById(entry.TraderId)?.GetBehavior<EntityBehaviorTravellingTrader>();
        if (beh == null || beh.IsLeaving)
        {
            return null;
        }
        return beh.MarketStallPos;
    }

    private void OnTick(float dt)
    {
        VillageManager vm = _sapi.ModLoader.GetModSystem<VillageManager>();
        if (vm == null)
        {
            return;
        }
        List<string> dead = new List<string>();
        foreach (KeyValuePair<string, TraderEntry> kvp in _active)
        {
            Entity e = _sapi.World.GetEntityById(kvp.Value.TraderId);
            if (e == null || !e.Alive)
            {
                dead.Add(kvp.Key);
            }
        }
        foreach (string k in dead)
        {
            _sapi.Logger.Debug("[TravellingTraderManager] Pruning dead entry for village " + k + ".");
            _active.Remove(k);
        }
        float hour = _sapi.World.Calendar.HourOfDay;
        bool isMorning = hour >= 5f && hour <= 10f;
        foreach (Village village in vm.Villages.Values)
        {
            if (!_active.ContainsKey(village.Id) && village.Beds.Count != 0 && village.Workstations.Count != 0 && isMorning && _sapi.World.Rand.NextDouble() <= 0.20)
            {
                TrySpawn(village);
            }
        }
    }

    /// <summary>
    /// Admin-command entry point — bypasses the RNG roll and morning-time check,
    /// but still requires a market stall and valid spawn position.
    /// </summary>
    public void TryForceSpawn(Village village) => TrySpawn(village);

    private void TrySpawn(Village village)
    {
        _sapi.Logger.Debug("[TravellingTraderManager] Attempting spawn for village " + village.Id + ".");
        BlockPos stallPos = FindMarketStallPos(village);
        if (stallPos == null)
        {
            _sapi.Logger.Debug("[TravellingTraderManager] No outdoor stall position for " + village.Id + " — skipping.");
            return;
        }
        Vec3d spawnPos = FindSpawnPos(village);
        if (spawnPos == null)
        {
            _sapi.Logger.Debug("[TravellingTraderManager] No valid spawn pos for " + village.Id + " — skipping.");
            return;
        }
        string sex = Sexes[_sapi.World.Rand.Next(Sexes.Length)];
        string specialty = Specialties[_sapi.World.Rand.Next(Specialties.Length)];
        string traderCode = "vsvillage:travelling-trader-" + sex + "-" + specialty;
        string guardCode = "vsvillage:travelling-guard-" + sex;
        EntityProperties traderType = _sapi.World.GetEntityType(new AssetLocation(traderCode));
        EntityProperties guardType = _sapi.World.GetEntityType(new AssetLocation(guardCode));
        if (traderType == null)
        {
            _sapi.Logger.Warning("[TravellingTraderManager] Entity type not found: " + traderCode);
            return;
        }
        if (guardType == null)
        {
            _sapi.Logger.Warning("[TravellingTraderManager] Entity type not found: " + guardCode);
            return;
        }

        // Calculate visit window
        float hoursPerDay = _sapi.World.Calendar.HoursPerDay;
        float currentHour = _sapi.World.Calendar.HourOfDay;
        double hoursUntilDepart = (20f - currentHour + hoursPerDay) % hoursPerDay;
        if (hoursUntilDepart < 1.0)
        {
            hoursUntilDepart += hoursPerDay;
        }
        double visitEnd = _sapi.World.Calendar.TotalHours + hoursUntilDepart;

        Entity traderEntity = _sapi.ClassRegistry.CreateEntity(traderType);
        traderEntity.Pos.SetPos(spawnPos);
        _sapi.World.SpawnEntity(traderEntity);

        Vec3d guardPos = spawnPos.Clone().Add(3.0, 0.0, 0.0);
        Entity guardEntity = _sapi.ClassRegistry.CreateEntity(guardType);
        guardEntity.Pos.SetPos(guardPos);
        _sapi.World.SpawnEntity(guardEntity);

        // Set behavior properties after SpawnEntity — behaviors are live now
        EntityBehaviorTravellingTrader traderBeh = traderEntity.GetBehavior<EntityBehaviorTravellingTrader>();
        if (traderBeh != null)
        {
            traderBeh.VillageId = village.Id;
            traderBeh.MarketStallPos = stallPos;
            traderBeh.GuardEntityId = guardEntity.EntityId;
            traderBeh.VisitEndTotalHours = visitEnd;
            traderBeh.SpawnedTotalHours = _sapi.World.Calendar.TotalHours;
        }
        else
        {
            _sapi.Logger.Warning($"[TravellingTraderManager] Trader {traderEntity.EntityId} missing EntityBehaviorTravellingTrader.");
        }

        EntityBehaviorTravellingGuard guardBeh = guardEntity.GetBehavior<EntityBehaviorTravellingGuard>();
        if (guardBeh != null)
        {
            guardBeh.TraderEntityId = traderEntity.EntityId;
            guardBeh.MarketStallPos = stallPos;
        }
        else
        {
            _sapi.Logger.Warning($"[TravellingTraderManager] Guard {guardEntity.EntityId} missing EntityBehaviorTravellingGuard.");
        }

        _active[village.Id] = new TraderEntry
        {
            TraderId = traderEntity.EntityId,
            GuardId = guardEntity.EntityId,
            VillageId = village.Id
        };

        _sapi.Logger.Notification($"[TravellingTraderManager] Spawned {traderCode} (id {traderEntity.EntityId}) + guard (id {guardEntity.EntityId}) for village {village.Id}. Stall: {stallPos}. Visit ends at {visitEnd:F1} h.");
        string traderName = traderEntity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "a travelling trader";
        string villageName = ((!string.IsNullOrWhiteSpace(village.Name)) ? village.Name : Lang.Get("vsvillage:trader-village-unknown"));
        _sapi.BroadcastMessageToAllGroups(Lang.Get("vsvillage:trader-arriving", traderName, villageName), EnumChatType.Notification);
    }

    private BlockPos FindMarketStallPos(Village village)
    {
        BlockPos stall = ScanForMarketStallBlock(village, _sapi.World.BlockAccessor);
        if (stall != null)
            _sapi.Logger.Debug($"[TravellingTraderManager] Found marketstall block at {stall} for village {village.Id}.");
        return stall;
    }

    private BlockPos ScanForMarketStallBlock(Village village, IBlockAccessor ba)
    {
        int r = Math.Min(village.Radius, 35);
        int cy = village.Pos.Y;
        BlockPos tmp = new BlockPos(0);
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                int wx = village.Pos.X + dx;
                int wz = village.Pos.Z + dz;
                for (int dy = 8; dy >= -8; dy--)
                {
                    tmp.Set(wx, cy + dy, wz);
                    Block b = ba.GetBlock(tmp);
                    if (b != null && b.Code?.Path?.StartsWith("marketstall") == true)
                    {
                        return tmp.Copy();
                    }
                }
            }
        }
        return null;
    }

    private Vec3d FindSpawnPos(Village village)
    {
        IBlockAccessor ba = _sapi.World.BlockAccessor;
        const double baseRadius = 55.0;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            double angle = _sapi.World.Rand.NextDouble() * Math.PI * 2.0;
            double dist = baseRadius + (_sapi.World.Rand.NextDouble() * 4.0 - 2.0);
            int x = village.Pos.X + (int)(Math.Cos(angle) * dist);
            int z = village.Pos.Z + (int)(Math.Sin(angle) * dist);
            BlockPos candidate = new BlockPos(x, village.Pos.Y, z, 0);
            candidate = FindSurface(ba, candidate);
            if (candidate != null)
            {
                return candidate.ToVec3d().Add(0.5, 1.0, 0.5);
            }
        }
        return null;
    }

    private static BlockPos FindSurface(IBlockAccessor ba, BlockPos pos)
    {
        BlockPos check = pos.Copy();
        for (int dy = 5; dy >= -5; dy--)
        {
            check.Y = pos.Y + dy;
            Block floor = ba.GetBlock(check);
            Block space = ba.GetBlock(check.UpCopy());
            Block head = ba.GetBlock(check.UpCopy().UpCopy());
            bool hasFloor = floor.CollisionBoxes != null && floor.CollisionBoxes.Length != 0;
            bool spaceClear = space.CollisionBoxes == null || space.CollisionBoxes.Length == 0;
            bool headClear = head.CollisionBoxes == null || head.CollisionBoxes.Length == 0;
            if (hasFloor && spaceClear && headClear)
            {
                return check.UpCopy();
            }
        }
        return null;
    }

    private static bool IsOutdoors(IBlockAccessor ba, BlockPos pos)
    {
        BlockPos check = pos.UpCopy();
        for (int i = 0; i < 30; i++)
        {
            Block b = ba.GetBlock(check);
            if (b?.CollisionBoxes != null && b.CollisionBoxes.Length != 0)
            {
                return false;
            }
            check = check.UpCopy();
        }
        return true;
    }
}