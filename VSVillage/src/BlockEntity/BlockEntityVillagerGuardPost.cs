using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsVillage;

public class BlockEntityVillagerGuardPost : BlockEntityVillagerPOI
{
	public override void AddToVillage(Village village)
	{
		village.GuardPosts[Pos] = new VillagerGuardPost
		{
			Pos = Pos
		};
	}

	// Two owners, so the POI base's single OwnerName cannot represent this block. Opting out keeps
	// the base from writing a half-truth; GetBlockInfo resolves both names live instead.
	protected override long GetCurrentOwnerId(Village village)
	{
		return -1L;
	}

	public override void RemoveFromVillage(Village village)
	{
		if (village == null || !village.GuardPosts.TryGetValue(Pos, out VillagerGuardPost post)) return;

		ReleaseGuard(post.DayOwnerId);
		ReleaseGuard(post.NightOwnerId);
		village.GuardPosts.Remove(Pos);
	}

	public override bool BelongsToVillage(Village village)
	{
		if (village.Id == base.VillageId && village.Name == base.VillageName)
		{
			return village.GuardPosts.ContainsKey(Pos);
		}
		return false;
	}

	// Unloaded guards keep a stale post attribute; GuardDuty.ValidateAssignment clears those on next tick.
	private void ReleaseGuard(long ownerId)
	{
		if (ownerId == -1L) return;
		EntityBehaviorVillager beh = Api?.World.GetEntityById(ownerId)?.GetBehavior<EntityBehaviorVillager>();
		if (beh == null) return;
		beh.GuardPost = null;
		beh.GuardShift = EnumGuardShift.none;
	}

	public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
	{
		base.GetBlockInfo(forPlayer, dsc);

		Village village = Api?.ModLoader.GetModSystem<VillageManager>()?.GetVillage(VillageId);
		if (village == null || !village.GuardPosts.TryGetValue(Pos, out VillagerGuardPost post)) return;

		dsc.AppendLine().Append(Lang.Get("vsvillage:guardpost-day-watch", OwnerNameOf(village, post.DayOwnerId)));
		dsc.AppendLine().Append(Lang.Get("vsvillage:guardpost-night-watch", OwnerNameOf(village, post.NightOwnerId)));
	}

	private static string OwnerNameOf(Village village, long ownerId)
	{
		if (ownerId != -1L && village.VillagerSaveData.TryGetValue(ownerId, out VillagerData data))
			return data.Name;
		return Lang.Get("vsvillage:nobody");
	}
}
