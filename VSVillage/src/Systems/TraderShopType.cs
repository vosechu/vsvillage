using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

// Shop specialty of a trader workstation. Repoints the assigned trader villager's
// TradeProps at a different tradelist at runtime, server side only.
public static class TraderShopType
{
	public const string Default = "general";

	// No luxuries or treasurehunter: a player's own village should not become a source
	// of ruin loot or precious goods. World traders stay the only outlet for those.
	public static readonly string[] Types = new string[8]
	{
		"general",
		"agriculture",
		"artisan",
		"buildmaterials",
		"clothing",
		"commodities",
		"furniture",
		"survivalgoods"
	};

	// Records which list the trader's current stock was drawn from. Cache only, so an
	// offline trader can spot a type change on next load. Village data stays authoritative.
	public const string AppliedAttribute = "vsvillageShopType";

	private static readonly int[] costLadder = new int[4] { 2, 5, 10, 20 };

	public static bool IsKnown(string type)
	{
		return !string.IsNullOrEmpty(type) && Array.IndexOf(Types, type) >= 0;
	}

	public static string Sanitize(string type)
	{
		return IsKnown(type) ? type : Default;
	}

	public static int CostFor(int changes)
	{
		if (changes < 0) changes = 0;
		return costLadder[Math.Min(changes, costLadder.Length - 1)];
	}

	// A vsvillage file wins over the vanilla one so retuning a village shop never
	// retunes vanilla worldgen traders or this mod's travelling traders.
	public static AssetLocation ResolveListPath(IAssetManager assets, string type)
	{
		type = Sanitize(type);
		if (type == Default) return new AssetLocation("vsvillage", "config/tradelists/villager-trader.json");

		AssetLocation own = new AssetLocation("vsvillage", "config/tradelists/shop-" + type + ".json");
		if (assets?.TryGet(own) != null) return own;
		return new AssetLocation("game", "config/tradelists/trader-" + type + ".json");
	}

	public static bool ListExists(IAssetManager assets, string type)
	{
		return assets?.TryGet(ResolveListPath(assets, type)) != null;
	}

	// Rebuilds only when the trader's stock came from a different list. Re-running it
	// otherwise would let a player reroll a trader's wares by reassigning them.
	public static void ApplyForWorkstation(Entity traderEntity, string shopType)
	{
		if (traderEntity == null) return;
		string desired = Sanitize(shopType);
		string applied = Sanitize(traderEntity.WatchedAttributes.GetString(AppliedAttribute));
		Apply(traderEntity, desired, desired != applied);
	}

	// rebuild=false only repoints TradeProps so the next scheduled restock draws from the
	// right list; the saved stock is left alone. rebuild=true wipes and refills it now.
	public static bool Apply(Entity traderEntity, string type, bool rebuild)
	{
		if (!(traderEntity is EntityTradingHumanoid trader)) return false;
		ICoreAPI api = trader.Api;
		if (api == null || api.Side != EnumAppSide.Server) return false;
		if (trader.Inventory == null) return false;

		type = Sanitize(type);
		AssetLocation path = ResolveListPath(api.Assets, type);

		TradeProperties props;
		try
		{
			IAsset asset = api.Assets.TryGet(path);
			if (asset == null)
			{
				api.Logger.Error("[VsVillage] Shop tradelist '" + path + "' not found for trader " + trader.EntityId + ". Keeping previous stock.");
				return false;
			}
			props = asset.ToObject<TradeProperties>();
		}
		catch (Exception e)
		{
			api.Logger.Error("[VsVillage] Failed deserializing shop tradelist '" + path + "' for trader " + trader.EntityId + ". Keeping previous stock.");
			api.Logger.Error(e);
			return false;
		}
		// Vanilla's own 7-day restock dereferences all three without a null check, so a
		// malformed override file would NRE in OnGameTick rather than fail here.
		if (props?.Selling?.List == null || props.Buying?.List == null || props.Money == null)
		{
			api.Logger.Error("[VsVillage] Shop tradelist '" + path + "' is missing money, buying or selling. Keeping previous stock.");
			return false;
		}

		// Never share a TradeProperties instance: the fill below shuffles its lists in place
		// and TradeItem.Resolve mutates AttributesToIgnore. One fresh object per trader.
		trader.TradeProps = props;

		if (!rebuild) return true;

		ClearSlots(trader.Inventory.SellingSlots);
		ClearSlots(trader.Inventory.BuyingSlots);
		FillSlots(trader, props.Selling, trader.Inventory.SellingSlots, EnumTradeDirection.Sell);
		FillSlots(trader, props.Buying, trader.Inventory.BuyingSlots, EnumTradeDirection.Buy);

		// The money slot is deliberately left alone. Refilling it here would make
		// re-specialising a drained shop cheaper than the gears it hands back.
		trader.WatchedAttributes.SetDouble("lastRefreshTotalDays", trader.World.Calendar.TotalDays);

		// Type-check rather than HasAttribute: a corrupt save could hold a non-tree here,
		// and vanilla's GetOrCreateTradeStore would NRE on the cast.
		if (!(trader.WatchedAttributes["traderInventory"] is ITreeAttribute store))
		{
			store = new TreeAttribute();
			trader.WatchedAttributes["traderInventory"] = store;
		}
		trader.Inventory.ToTreeAttributes(store);

		trader.WatchedAttributes.SetString(AppliedAttribute, type);
		// MarkAllDirty is what pushes traderInventory to the client; MarkPathDirty is not enough here.
		trader.WatchedAttributes.MarkAllDirty();
		return true;
	}

	// Cart slots (16-19, 36-39) are deliberately untouched: the selling cart can hold
	// player items and vanilla drops those in InventoryTrader.Close.
	private static void ClearSlots(ItemSlotTrade[] slots)
	{
		foreach (ItemSlotTrade slot in slots)
		{
			if (slot == null) continue;
			slot.TradeItem = null;
			slot.Itemstack = null;
			slot.MarkDirty();
		}
	}

	// SellingSlots/BuyingSlots are hardcoded to 16 whatever maxItems says, so the array
	// length is the real cap. Overshooting it silently drops randomly-chosen items.
	private static void FillSlots(EntityTradingHumanoid trader, TradeList list, ItemSlotTrade[] slots, EnumTradeDirection direction)
	{
		if (list?.List == null || list.List.Length == 0) return;

		list.List.Shuffle(trader.World.Rand);
		int wanted = Math.Min(list.List.Length, Math.Min(list.MaxItems, slots.Length));
		int filled = 0;

		for (int i = 0; i < list.List.Length && filled < wanted; i++)
		{
			TradeItem item = list.List[i];
			if (item == null) continue;
			if (!item.Resolve(trader.World, "vsvillage shop tradelist")) continue;

			if (item.ResolvedItemstack?.Collectible is ITradeableCollectible tradeable
				&& !tradeable.ShouldTrade(trader, item, direction)) continue;

			ResolvedTradeItem resolved = item.Resolve(trader.World);
			if (resolved?.Stack == null || resolved.Stock <= 0) continue;

			slots[filled].SetTradeItem(resolved);
			slots[filled].MarkDirty();
			filled++;
		}
	}
}
