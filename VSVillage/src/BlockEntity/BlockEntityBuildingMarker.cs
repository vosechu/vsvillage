using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Sign + active phase state holder. Day-tick advances DaysCompleted only when the builder
// visited during the day, replaces top-2-Y schematic layers each work-day, finalises on last.
public class BlockEntityBuildingMarker : BlockEntity
{
    public string SelectedSchematicId;
    public double PlacedAtHours;
    public bool ConstructionStarted;
    public int CostPaidGears;
    public int DaysRequired;
    public int DaysCompleted;
    public double LastDayChecked;
    public int BuilderCountToday;
    public bool DemolitionInProgress;
    public int DemolitionDaysRemaining;
    public int RotationAngle;

    // Calendar day index (5pm-anchored) when ConstructionStarted was set. Day-credit refuses
    // to fire while currentCreditDay <= PlacementCreditDay, so placement-day work never counts.
    public int PlacementCreditDay;

    // Work-day boundary used for credit gating. End of villager work window (8-17 per villager.json).
    private const double WorkEndHour = 17.0;

    public static int CreditDayFromTotalDays(double totalDays) => (int)Math.Floor(totalDays - WorkEndHour / 24.0);

    // 0% done = full refund. Each work-day costs proportional labor. Floor at 25% of cost.
    public static int CalculateRefund(int costPaid, int daysCompleted, int daysRequired)
    {
        if (costPaid <= 0) return 0;
        if (daysCompleted <= 0 || daysRequired <= 0) return costPaid;
        int minRefund = Math.Max(1, costPaid / 4);
        float progressFraction = Math.Min(1f, (float)daysCompleted / daysRequired);
        return Math.Max(minRefund, (int)Math.Round(costPaid * (1f - progressFraction)));
    }

    private long dayTickListenerId = -1;

    private BlockSchematic _rotatedCache;
    private int _rotatedCacheAngle = int.MinValue;

    // Mirrors WorldEdit.PasteBlockData: ClonePacked, TransformWhilePacked, Init.
    // TransformWhilePacked internally calls Pack at its tail, so the packed Indices
    // and SizeX/Y/Z are already correct - no explicit Pack needed.
    private BlockSchematic GetEffectiveSchematic(BuildingDefinition def)
    {
        if (def?.Schematic == null) return null;
        int angle = ((RotationAngle % 360) + 360) % 360;
        if (angle == 0) return def.Schematic;
        if (_rotatedCache != null && _rotatedCacheAngle == angle) return _rotatedCache;
        _rotatedCache = def.Schematic.ClonePacked();
        _rotatedCache.TransformWhilePacked(Api.World, EnumOrigin.BottomCenter, angle);
        _rotatedCache.Init(Api.World.BlockAccessor);
        _rotatedCacheAngle = angle;
        return _rotatedCache;
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            dayTickListenerId = RegisterGameTickListener(OnSlowTick, 5000);
            if (LastDayChecked <= 0.0) LastDayChecked = api.World.Calendar.TotalDays;
        }
    }

    public override void OnBlockPlaced(ItemStack byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        if (Api?.Side != EnumAppSide.Server) return;
        PlacedAtHours = Api.World.Calendar.TotalHours;
        MarkDirty(true);
    }

    public override void OnBlockUnloaded()
    {
        if (dayTickListenerId != -1)
        {
            UnregisterGameTickListener(dayTickListenerId);
            dayTickListenerId = -1;
        }
        base.OnBlockUnloaded();
    }

    public bool IsComplete => ConstructionStarted && DaysCompleted >= DaysRequired;

    // In-memory set of builder entity IDs who have registered today. Cleared on day tick.
    private readonly System.Collections.Generic.HashSet<long> _buildersVisitedToday = new System.Collections.Generic.HashSet<long>();

    // Called by AiTaskVillagerBuild while the builder is at the marker.
    public void RegisterBuilderPresence(long entityId)
    {
        if (Api?.Side != EnumAppSide.Server) return;
        if (!ConstructionStarted) return;
        if (!_buildersVisitedToday.Add(entityId)) return;
        BuilderCountToday = Math.Max(BuilderCountToday, _buildersVisitedToday.Count);
        MarkDirty(false);
    }

    // credits/day: 1 per builder present, capped at 3.
    private static int BuilderBonus(int count) => Math.Min(count, 3);

    private void OnSlowTick(float dt)
    {
        if (Api?.Side != EnumAppSide.Server) return;
        if (!ConstructionStarted) return;

        double now = Api.World.Calendar.TotalDays;
        int currentCreditDay = CreditDayFromTotalDays(now);
        int lastCreditDay = CreditDayFromTotalDays(LastDayChecked);
        if (currentCreditDay <= lastCreditDay) return;

        LastDayChecked = now;

        if (DemolitionInProgress)
        {
            MarkDirty(true);
            DemolitionTick();
            return;
        }

        MarkDirty(true);

        if (currentCreditDay <= PlacementCreditDay) return;

        if (BuilderCountToday == 0) return;

        BuildingCatalog catalog = Api.ModLoader.GetModSystem<BuildingCatalog>();
        BuildingDefinition def = catalog?.Get(SelectedSchematicId);
        if (def == null)
        {
            Api.Logger.Warning("[VsVillage] BuildingMarker at {0}: schematic '{1}' not in catalog, skipping progression.",
                Pos, SelectedSchematicId);
            return;
        }

        int credits = BuilderBonus(BuilderCountToday);
        _buildersVisitedToday.Clear();
        BuilderCountToday = 0;

        for (int i = 0; i < credits; i++)
        {
            DaysCompleted++;
            if (DaysCompleted >= DaysRequired)
            {
                FinaliseConstruction(def);
                MarkDirty(true);
                return;
            }
            ReplaceLayerPair(def, DaysCompleted - 1);
        }

        MarkDirty(true);
    }

    // Demolition: top-down, double credit rate. Each credit removes one layer-pair.
    // Scaffold stays until DemolitionDaysRemaining hits 0, then FinaliseDemolition clears it.
    private void DemolitionTick()
    {
        if (BuilderCountToday == 0) return;

        BuildingCatalog catalog = Api.ModLoader.GetModSystem<BuildingCatalog>();
        BuildingDefinition def = catalog?.Get(SelectedSchematicId);
        if (def == null)
        {
            Api.Logger.Warning("[VsVillage] BuildingMarker at {0}: schematic '{1}' missing during demolition, forcing completion.",
                Pos, SelectedSchematicId);
            _buildersVisitedToday.Clear();
            BuilderCountToday = 0;
            FinaliseDemolition(null);
            return;
        }

        int credits = Math.Min(BuilderBonus(BuilderCountToday) * 2, DemolitionDaysRemaining);
        _buildersVisitedToday.Clear();
        BuilderCountToday = 0;

        for (int i = 0; i < credits; i++)
        {
            DemolitionDaysRemaining--;
            RemoveLayerPair(def, DemolitionDaysRemaining);
        }

        if (DemolitionDaysRemaining <= 0)
        {
            FinaliseDemolition(def);
            return;
        }

        MarkDirty(true);
    }

    // Ground-up progression so the cage stays full while floors stack inside it.
    // Day 0 = foundation (Y=0, Y=1). Day N = Y=2N, Y=2N+1.
    private void ReplaceLayerPair(BuildingDefinition def, int dayZeroIdx)
    {
        BlockSchematic s = GetEffectiveSchematic(def);
        if (s == null) return;
        int botSchemY = dayZeroIdx * 2;
        int topSchemY = botSchemY + 1;
        if (botSchemY < s.SizeY) ReplaceLayer(s, botSchemY);
        if (topSchemY < s.SizeY) ReplaceLayer(s, topSchemY);
    }

    // Schematic Y=0 is the ground/foundation row, embedded one block below the marker.
    // Schematic centred on marker X, 2 blocks back from marker (+Z edge at dz=-2). sx = dx + schemHalfL; sz = dz + SizeZ + 1.
    private void ReplaceLayer(BlockSchematic s, int schemY)
    {
        IBlockAccessor ba = Api.World.BlockAccessor;
        int schemHalfL = s.SizeX / 2;
        int worldY = Pos.Y - 1 + schemY;

        int minDx = -schemHalfL;
        int maxDx = (s.SizeX - 1) - schemHalfL;
        int minDz = -(s.SizeZ + 1);
        int maxDz = -2;

        for (int dx = minDx; dx <= maxDx; dx++)
        for (int dz = minDz; dz <= maxDz; dz++)
        {
            BlockPos worldPos = new BlockPos(Pos.X + dx, worldY, Pos.Z + dz, Pos.dimension);

            int sx = dx + schemHalfL;
            int sz = dz + (s.SizeZ + 1);
            int schemBlockId = ResolveSchematicCell(s, sx, schemY, sz);
            if (schemBlockId == 0 && schemY == 0) continue;
            ba.SetBlock(schemBlockId, worldPos);
        }
    }

    // Mirrors ReplaceLayerPair but removes placed blocks instead of placing them.
    private void RemoveLayerPair(BuildingDefinition def, int dayZeroIdx)
    {
        BlockSchematic s = GetEffectiveSchematic(def);
        if (s == null) return;
        int botSchemY = dayZeroIdx * 2;
        int topSchemY = botSchemY + 1;
        if (botSchemY < s.SizeY) RemoveLayer(s, botSchemY);
        if (topSchemY < s.SizeY) RemoveLayer(s, topSchemY);
    }

    // Only removes cells the schematic placed (schemBlockId != 0). Never punches terrain.
    private void RemoveLayer(BlockSchematic s, int schemY)
    {
        IBlockAccessor ba = Api.World.BlockAccessor;
        int schemHalfL = s.SizeX / 2;
        int worldY = Pos.Y - 1 + schemY;

        int minDx = -schemHalfL;
        int maxDx = (s.SizeX - 1) - schemHalfL;
        int minDz = -(s.SizeZ + 1);
        int maxDz = -2;

        for (int dx = minDx; dx <= maxDx; dx++)
        for (int dz = minDz; dz <= maxDz; dz++)
        {
            int sx = dx + schemHalfL;
            int sz = dz + (s.SizeZ + 1);
            int schemBlockId = ResolveSchematicCell(s, sx, schemY, sz);
            if (schemBlockId == 0) continue;
            ba.SetBlock(0, new BlockPos(Pos.X + dx, worldY, Pos.Z + dz, Pos.dimension));
        }
    }

    private void FinaliseDemolition(BuildingDefinition def)
    {
        if (def != null)
        {
            BlockSchematic s = GetEffectiveSchematic(def);
            if (s != null) RemoveCageShell(s);
        }
        VillageManager vm = Api.ModLoader.GetModSystem<VillageManager>();
        vm?.GetVillage(Pos)?.DequeueConstruction(Pos);
        Api.World.BlockAccessor.SetBlock(0, Pos);
    }

    // Place handles BEs + decors. ReplaceAll clears the schematic box first.
    // After placement, strip the 1-block cage shell that surrounds the schematic.
    private void FinaliseConstruction(BuildingDefinition def)
    {
        IBlockAccessor ba = Api.World.BlockAccessor;
        BlockSchematic s = GetEffectiveSchematic(def);
        if (s == null) return;

        int schemHalfL = s.SizeX / 2;
        BlockPos startPos = new BlockPos(
            Pos.X - schemHalfL,
            Pos.Y - 1,
            Pos.Z - (s.SizeZ + 1),
            Pos.dimension);

        s.Place(ba, Api.World, startPos, EnumReplaceMode.ReplaceAllNoAir, replaceMetaBlocks: true);
        s.PlaceDecors(ba, startPos);

        RemoveCageShell(s);

        VillageManager vm = Api.ModLoader.GetModSystem<VillageManager>();
        vm?.GetVillage(Pos)?.DequeueConstruction(Pos);
        ba.SetBlock(0, Pos);
    }

    // Mirrors SpawnScaffold's cage extents and clears every scaffold cell on the shell.
    private void RemoveCageShell(BlockSchematic s)
    {
        IBlockAccessor ba = Api.World.BlockAccessor;
        Block scaffold = Api.World.GetBlock(new AssetLocation("vsvillage:buildingscaffold"));
        int scaffoldId = scaffold?.BlockId ?? 0;

        int halfL = s.SizeX / 2;
        int innerMinDx = -halfL, innerMaxDx = (s.SizeX - 1) - halfL;
        int innerMinDz = -(s.SizeZ + 1), innerMaxDz = -2;
        int minDx = innerMinDx - 1, maxDx = innerMaxDx + 1;
        int minDz = innerMinDz - 1, maxDz = innerMaxDz + 1;
        int minDy = 0, maxDy = s.SizeY - 1;

        for (int dy = minDy; dy <= maxDy; dy++)
            for (int dx = minDx; dx <= maxDx; dx++)
                for (int dz = minDz; dz <= maxDz; dz++)
                {
                    bool perimeter = dx == maxDx || dx == minDx || dz == maxDz || dz == minDz;
                    bool ceiling = dy == maxDy;
                    if (!perimeter && !ceiling) continue;
                    BlockPos p = new BlockPos(Pos.X + dx, Pos.Y + dy, Pos.Z + dz, Pos.dimension);
                    if (scaffoldId == 0 || ba.GetBlock(p).BlockId == scaffoldId)
                        ba.SetBlock(0, p);
                }
    }

    // Index is packed (y << 20) | (z << 10) | x. Returns 0 (air) when the cell isn't in the schematic.
    private int ResolveSchematicCell(BlockSchematic s, int sx, int sy, int sz)
    {
        if (s == null) return 0;
        uint key = (uint)((sy << 20) | (sz << 10) | sx);
        for (int i = 0; i < s.Indices.Count; i++)
        {
            if (s.Indices[i] != key) continue;
            int storedId = s.BlockIds[i];
            if (!s.BlockCodes.TryGetValue(storedId, out AssetLocation code) || code == null) return 0;
            Block world = Api.World.GetBlock(code);
            return world?.BlockId ?? 0;
        }
        return 0;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (SelectedSchematicId != null) tree.SetString("schematicId", SelectedSchematicId);
        tree.SetDouble("placedAt", PlacedAtHours);
        tree.SetBool("constructionStarted", ConstructionStarted);
        tree.SetInt("costPaidGears", CostPaidGears);
        tree.SetInt("daysRequired", DaysRequired);
        tree.SetInt("daysCompleted", DaysCompleted);
        tree.SetDouble("lastDayChecked", LastDayChecked);
        tree.SetInt("builderCountToday", BuilderCountToday);
        tree.SetBool("demolitionInProgress", DemolitionInProgress);
        tree.SetInt("demolitionDaysRemaining", DemolitionDaysRemaining);
        tree.SetInt("rotationAngle", RotationAngle);
        tree.SetInt("placementCreditDay", PlacementCreditDay);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolve)
    {
        base.FromTreeAttributes(tree, worldForResolve);
        SelectedSchematicId  = tree.GetString("schematicId", null);
        PlacedAtHours        = tree.GetDouble("placedAt", 0.0);
        ConstructionStarted  = tree.GetBool("constructionStarted", false);
        CostPaidGears        = tree.GetInt("costPaidGears", 0);
        DaysRequired         = tree.GetInt("daysRequired", 0);
        DaysCompleted        = tree.GetInt("daysCompleted", 0);
        LastDayChecked       = tree.GetDouble("lastDayChecked", 0.0);
        BuilderCountToday       = tree.GetInt("builderCountToday", 0);
        DemolitionInProgress    = tree.GetBool("demolitionInProgress", false);
        DemolitionDaysRemaining = tree.GetInt("demolitionDaysRemaining", 0);
        RotationAngle           = tree.GetInt("rotationAngle", 0);
        PlacementCreditDay      = tree.GetInt("placementCreditDay", int.MinValue);
    }
}
