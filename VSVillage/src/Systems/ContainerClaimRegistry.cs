using System.Collections.Concurrent;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Profession-agnostic reservation of container positions so two villagers never
/// target the same container. Transient (in-memory); empty after a restart, which is
/// correct. Generalizes the trough-claim pattern in AiTaskVillagerFillTrough.
/// Pure logic — no world/entity references — so it is unit-tested off-engine.
/// </summary>
public class ContainerClaimRegistry
{
    public const long ClaimExpiryMs = 120_000L;

    private readonly struct Claim
    {
        public readonly long OwnerId;
        public readonly long ClaimedAtMs;
        public Claim(long ownerId, long claimedAtMs) { OwnerId = ownerId; ClaimedAtMs = claimedAtMs; }
    }

    // BlockPos has value equality/hashcode, so equal coordinates collide as one key.
    private readonly ConcurrentDictionary<BlockPos, Claim> claims = new();

    /// <summary>
    /// Reserve <paramref name="pos"/> for <paramref name="ownerId"/>. Grants when the
    /// position is unclaimed, already owned by the caller (idempotent — refreshes the
    /// clock), or the incumbent claim has expired. A failed claim never extends the
    /// incumbent. Returns whether the caller now holds the claim.
    /// </summary>
    public bool TryClaim(BlockPos pos, long ownerId, long nowMs)
    {
        if (claims.TryGetValue(pos, out Claim existing))
        {
            bool expired = nowMs - existing.ClaimedAtMs > ClaimExpiryMs; // strict >
            if (!expired && existing.OwnerId != ownerId) return false;
        }
        claims[pos.Copy()] = new Claim(ownerId, nowMs); // defensive copy of the mutable key
        return true;
    }

    /// <summary>Free <paramref name="pos"/> only if <paramref name="ownerId"/> holds it. No-op otherwise.</summary>
    public void Release(BlockPos pos, long ownerId)
    {
        if (claims.TryGetValue(pos, out Claim existing) && existing.OwnerId == ownerId)
        {
            claims.TryRemove(pos, out _);
        }
    }
}
