using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsVillage;

// Server-side handler for Begin Build and Cancel actions. GUI opens client-side directly via
// catalog + BE; no server-to-client context packet needed, just client-to-server commits.
public class BuildingMarkerNetwork : ModSystem
{
    public const string ChannelName = "vsvillagebuildermarker";

    private ICoreServerAPI _sapi;
    private IServerNetworkChannel _serverChannel;
    private IClientNetworkChannel _clientChannel;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<BuildingMarkerSelectMessage>()
            .RegisterMessageType<BuildingMarkerCancelMessage>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        _sapi = api;
        _serverChannel = api.Network.GetChannel(ChannelName) as IServerNetworkChannel;
        _serverChannel.SetMessageHandler<BuildingMarkerSelectMessage>(HandleSelect);
        _serverChannel.SetMessageHandler<BuildingMarkerCancelMessage>(HandleCancel);
    }

    public override void StartClientSide(Vintagestory.API.Client.ICoreClientAPI api)
    {
        base.StartClientSide(api);
        _clientChannel = api.Network.GetChannel(ChannelName) as IClientNetworkChannel;
    }

    public void SendSelectToServer(BuildingMarkerSelectMessage msg)
    {
        _clientChannel?.SendPacket(msg);
    }

    public void SendCancelToServer(BuildingMarkerCancelMessage msg)
    {
        _clientChannel?.SendPacket(msg);
    }

    private void HandleSelect(IServerPlayer fromPlayer, BuildingMarkerSelectMessage msg)
    {
        if (msg == null || msg.MarkerPos == null || string.IsNullOrEmpty(msg.SelectedId)) return;

        BlockEntityBuildingMarker be = _sapi.World.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(msg.MarkerPos);
        if (be == null) return;
        if (be.ConstructionStarted) return;

        BuildingCatalog catalog = _sapi.ModLoader.GetModSystem<BuildingCatalog>();
        BuildingDefinition def = catalog?.Get(msg.SelectedId);
        if (def == null) return;

        if (_sapi.World.BlockAccessor.GetBlock(msg.MarkerPos) is not BlockBuildingMarker marker) return;
        if (marker.SizeBucket != def.Size) return;

        VillageManager vm = _sapi.ModLoader.GetModSystem<VillageManager>();
        Village village = vm?.GetVillage(msg.MarkerPos);
        if (village == null)
        {
            fromPlayer.SendIngameError("vsvillage:building-marker-no-village",
                Lang.Get("vsvillage:building-marker-no-village"));
            return;
        }

        if (!ChargeGear(fromPlayer, def.CostGears))
        {
            fromPlayer.SendIngameError("vsvillage:building-marker-cant-afford",
                Lang.Get("vsvillage:building-marker-cant-afford"));
            return;
        }

        int normalisedAngle = ((msg.RotationAngle % 360) + 360) % 360;
        normalisedAngle = normalisedAngle / 90 * 90;

        be.SelectedSchematicId = def.Id;
        be.CostPaidGears = def.CostGears;
        be.DaysRequired = def.DaysRequired;
        be.DaysCompleted = 0;
        be.LastDayChecked = _sapi.World.Calendar.TotalDays;
        // Cheese protection: bump PlacementCreditDay so the placement day's 5pm cannot credit.
        // If placed BEFORE 5pm, today's 5pm is still on placement day, so block one creditDay forward.
        // If placed AT/AFTER 5pm, today's 5pm has already crossed; the raw creditDay is the right anchor.
        {
            double pNow = _sapi.World.Calendar.TotalDays;
            double pHour = (pNow - System.Math.Floor(pNow)) * 24.0;
            int rawDay = BlockEntityBuildingMarker.CreditDayFromTotalDays(pNow);
            be.PlacementCreditDay = (pHour < 17.0) ? rawDay + 1 : rawDay;
        }
        be.BuilderCountToday = 0;
        be.ConstructionStarted = true;
        be.RotationAngle = normalisedAngle;
        be.MarkDirty(true);

        SpawnScaffold(msg.MarkerPos, def, normalisedAngle);
        village.EnqueueConstruction(msg.MarkerPos.Copy());
    }

    private void HandleCancel(IServerPlayer fromPlayer, BuildingMarkerCancelMessage msg)
    {
        if (msg == null || msg.MarkerPos == null) return;

        if (_sapi.World.BlockAccessor.GetBlock(msg.MarkerPos) is not BlockBuildingMarker marker) return;
        BlockEntityBuildingMarker be = _sapi.World.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(msg.MarkerPos);
        if (be == null) return;

        if (!be.ConstructionStarted)
        {
            // Pre-build: marker placed but no build started. Return the item and remove.
            ReturnMarkerItem(fromPlayer, marker);
            _sapi.World.BlockAccessor.SetBlock(0, msg.MarkerPos);
            return;
        }

        if (be.DemolitionInProgress) return;

        int refund = BlockEntityBuildingMarker.CalculateRefund(be.CostPaidGears, be.DaysCompleted, be.DaysRequired);
        if (refund > 0) GiveGears(fromPlayer, refund);

        if (be.DaysCompleted == 0)
        {
            VillageManager vm = _sapi.ModLoader.GetModSystem<VillageManager>();
            vm?.GetVillage(msg.MarkerPos)?.DequeueConstruction(msg.MarkerPos);
            BuildingCatalog catalog = _sapi.ModLoader.GetModSystem<BuildingCatalog>();
            BuildingDefinition def = catalog?.Get(be.SelectedSchematicId);
            ClearBuildVolume(msg.MarkerPos, marker, def, be.RotationAngle);
            _sapi.World.BlockAccessor.SetBlock(0, msg.MarkerPos);
        }
        else
        {
            be.DemolitionInProgress = true;
            be.DemolitionDaysRemaining = be.DaysCompleted;
            be.MarkDirty(true);
        }
    }

    // Cage scaffold: 1-block shell OUTSIDE schematic volume. Build spawns inside the
    // cage, scaffold removed at FinaliseConstruction for a clean reveal.
    private void SpawnScaffold(BlockPos center, BuildingDefinition def, int rotationAngle)
    {
        Block scaffold = _sapi.World.GetBlock(new AssetLocation("vsvillage:buildingscaffold"));
        if (scaffold == null)
        {
            _sapi.Logger.Error("[VsVillage] BuildingMarkerNetwork: vsvillage:buildingscaffold not registered.");
            return;
        }

        int sx = def.SizeX, sz = def.SizeZ;
        if (rotationAngle == 90 || rotationAngle == 270) { int tmp = sx; sx = sz; sz = tmp; }

        int halfL = sx / 2;
        int innerMinDx = -halfL, innerMaxDx = (sx - 1) - halfL;
        int innerMinDz = -(sz + 1), innerMaxDz = -2;
        int minDx = innerMinDx - 1, maxDx = innerMaxDx + 1;
        int minDz = innerMinDz - 1, maxDz = innerMaxDz + 1;
        int minDy = 0;
        int maxDy = def.SizeY - 1;
        if (maxDy < minDy) return;

        for (int dy = minDy; dy <= maxDy; dy++)
            for (int dx = minDx; dx <= maxDx; dx++)
                for (int dz = minDz; dz <= maxDz; dz++)
                {
                    bool perimeter = dx == maxDx || dx == minDx || dz == maxDz || dz == minDz;
                    bool ceiling = dy == maxDy;
                    if (!perimeter && !ceiling) continue;
                    BlockPos pos = new BlockPos(center.X + dx, center.Y + dy, center.Z + dz, center.dimension);
                    _sapi.World.BlockAccessor.SetBlock(scaffold.BlockId, pos);
                }
    }

    // Clears cage extents (def + rotation) when known. Falls back to the bucket cap
    // when def is null - happens only if the schematic vanished from the catalog between
    // build start and cancel.
    private void ClearBuildVolume(BlockPos center, BlockBuildingMarker marker, BuildingDefinition def, int rotationAngle)
    {
        int minDx, maxDx, minDz, maxDz, minDy, maxDy;
        if (def != null)
        {
            int sx = def.SizeX, sz = def.SizeZ;
            if (rotationAngle == 90 || rotationAngle == 270) { int tmp = sx; sx = sz; sz = tmp; }
            int halfL = sx / 2;
            minDx = -halfL - 1;
            maxDx = (sx - 1) - halfL + 1;
            minDz = -(sz + 1) - 1;
            maxDz = -1;
            // Foundation lives at Pos.Y - 1 so cancel must nuke that row too.
            minDy = -1;
            maxDy = def.SizeY - 1;
        }
        else
        {
            // Fallback: schematic missing from catalog. Use bucket cap + cage shell, rotation-aware.
            var b = marker.FootprintBounds;
            int bxz = (b.maxDx - b.minDx + 1);
            int bzz = -(b.minDz);
            if (rotationAngle == 90 || rotationAngle == 270) { int tmp = bxz; bxz = bzz; bzz = tmp; }
            int halfL = bxz / 2;
            minDx = -halfL - 1;
            maxDx = (bxz - 1) - halfL + 1;
            minDz = -bzz - 1;
            maxDz = -1;
            minDy = b.minDy;
            maxDy = b.maxDy;
        }

        for (int dy = minDy; dy <= maxDy; dy++)
            for (int dx = minDx; dx <= maxDx; dx++)
                for (int dz = minDz; dz <= maxDz; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    BlockPos pos = new BlockPos(center.X + dx, center.Y + dy, center.Z + dz, center.dimension);
                    _sapi.World.BlockAccessor.SetBlock(0, pos);
                }
    }

    private void ReturnMarkerItem(IServerPlayer player, BlockBuildingMarker marker)
    {
        if (marker == null) return;
        ItemStack stack = new ItemStack(marker, 1);
        if (!player.InventoryManager.TryGiveItemstack(stack, true))
        {
            _sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        }
    }

    private void GiveGears(IServerPlayer player, int amount)
    {
        Item gear = _sapi.World.GetItem(new AssetLocation("game:gear-rusty"));
        if (gear == null) return;
        ItemStack stack = new ItemStack(gear, amount);
        if (!player.InventoryManager.TryGiveItemstack(stack, true))
        {
            _sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        }
    }

    private static bool ChargeGear(IServerPlayer player, int amount)
    {
        if (amount <= 0) return true;
        int remaining = amount;
        IPlayerInventoryManager mgr = player.InventoryManager;
        foreach (string invName in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            IInventory inv = mgr.GetOwnInventory(invName);
            if (inv == null) continue;
            foreach (ItemSlot slot in inv)
            {
                if (slot.Empty || slot.Itemstack?.Collectible?.Code?.Path != "gear-rusty") continue;
                int take = System.Math.Min(slot.StackSize, remaining);
                slot.TakeOut(take);
                slot.MarkDirty();
                remaining -= take;
                if (remaining <= 0) return true;
            }
        }
        return remaining <= 0;
    }
}
