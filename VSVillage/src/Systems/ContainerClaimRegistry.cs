using System.Collections.Immutable;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Profession-agnostic reservation of container positions so two villagers never target the same
/// container. Transient (in-memory); empty after a restart, which is correct.
///
/// The store is an <b>immutable snapshot</b> that is replaced wholesale on every claim/release —
/// never mutated in place. Each claim first rebuilds the map without expired entries, so it stays
/// bounded (old snapshots are GC'd) and stale claims never accumulate. There is no overwrite path
/// to hide a bug in, and value semantics make the whole thing trivially reasoned about.
///
/// Thread-affinity: every caller runs on the single main server tick thread
/// (ServerSystemEntitySimulation.TickEntities → AI task OnGameTick, verified serial in VS 1.22.3),
/// so the read-modify-write on <c>claims</c> in <see cref="TryClaim"/> needs no lock. This is
/// <b>not</b> thread-safe — do not call it from a physics or async path.
///
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
    private ImmutableDictionary<BlockPos, Claim> claims = ImmutableDictionary<BlockPos, Claim>.Empty;

    /// <summary>
    /// Reserve <paramref name="pos"/> for <paramref name="ownerId"/>. Grants when the position is
    /// unclaimed, already owned by the caller (idempotent — refreshes the clock), or the incumbent
    /// claim has expired. A failed claim never extends the incumbent. Returns whether the caller
    /// now holds the claim.
    /// </summary>
    public bool TryClaim(BlockPos pos, long ownerId, long nowMs)
    {
        ImmutableDictionary<BlockPos, Claim> live = WithoutExpired(claims, nowMs);
        if (live.TryGetValue(pos, out Claim existing) && existing.OwnerId != ownerId)
        {
            return false; // held by another, still live (expired incumbents were dropped above)
        }
        claims = live.SetItem(pos.Copy(), new Claim(ownerId, nowMs)); // defensive copy of the mutable key
        return true;
    }

    /// <summary>Free <paramref name="pos"/> only if <paramref name="ownerId"/> holds it. No-op otherwise.</summary>
    public void Release(BlockPos pos, long ownerId)
    {
        if (claims.TryGetValue(pos, out Claim existing) && existing.OwnerId == ownerId)
        {
            claims = claims.Remove(pos);
        }
    }

    /// <summary>Returns the map with every expired claim dropped (strict <c>&gt;</c> — a claim exactly
    /// at the threshold is still live), or the same instance if nothing expired.</summary>
    private static ImmutableDictionary<BlockPos, Claim> WithoutExpired(ImmutableDictionary<BlockPos, Claim> map, long nowMs)
    {
        ImmutableDictionary<BlockPos, Claim> result = map;
        foreach (System.Collections.Generic.KeyValuePair<BlockPos, Claim> kv in map)
        {
            if (nowMs - kv.Value.ClaimedAtMs > ClaimExpiryMs)
            {
                result = result.Remove(kv.Key);
            }
        }
        return result;
    }
}
