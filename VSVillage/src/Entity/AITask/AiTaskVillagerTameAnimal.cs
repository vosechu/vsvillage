using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Shepherd task: once per day find a generation-0 enclosed animal, tame it
/// (set generation to 1) and broadcast a notification to all players.
/// </summary>
public class AiTaskVillagerTameAnimal : AiTaskBase
{
    private float searchRadius = 30f;

    public AiTaskVillagerTameAnimal(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        if (taskConfig["searchRadius"] != null)
            searchRadius = taskConfig["searchRadius"].AsFloat(30f);
    }

    public override bool ShouldExecute()
    {
        if (!IsShepherd()) return false;
        if (duringDayTimeFrames != null && duringDayTimeFrames.Length > 0 &&
            !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World)) return false;
        if (cooldownUntilMs > entity.World.ElapsedMilliseconds) return false;
        return true;
    }

    public override bool ContinueExecute(float dt) => false; // instant task

    public override void StartExecute()
    {
        base.StartExecute();
        TryTameOne();
    }

    // Lore / hostile mob prefixes — never tame these.
    private static readonly string[] LorePrefixes =
    {
        "bell-", "bellmini-", "bowtorn-", "drifter-", "locust-", "shiver-"
    };
    private static readonly string[] LoreExact = { "mechhelper" };

    private static bool IsLoreEntity(string path)
    {
        if (path == null) return false;
        foreach (string e in LoreExact)
            if (path == e) return true;
        foreach (string p in LorePrefixes)
            if (path.StartsWith(p)) return true;
        return false;
    }

    private void TryTameOne()
    {
        IBlockAccessor ba = entity.World.BlockAccessor;
        Entity[] nearby = entity.World.GetEntitiesAround(entity.Pos.XYZ, searchRadius, 6f, e =>
        {
            if (e == entity || !e.Alive) return false;
            if (e.Code?.Path == "player") return false;
            string path = e.Code?.Path;
            if (string.IsNullOrEmpty(path)) return false;
            if (e.Code.Domain == "vsvillage") return false;
            // Never tame lore/hostile mobs.
            if (IsLoreEntity(path)) return false;
            // Must be generation 0 (wild / not yet tamed)
            if (GetGeneration(e) != 0) return false;
            // Must be inside a pen or barn
            return IsEnclosed(e, ba);
        });

        if (nearby.Length == 0) return;

        // Pick a random one from the candidates
        Entity target = nearby[entity.World.Rand.Next(nearby.Length)];
        SetGeneration(target, 1);

        // Build the notification message
        string shepherdName = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "A shepherd";
        string villageName = entity.GetBehavior<EntityBehaviorVillager>()?.VillageName ?? "an unknown village";
        string animalName = GetAnimalDisplayName(target);
        BlockPos coords = target.Pos.AsBlockPos;
        string msg = $"{shepherdName}, a Shepherd at {villageName}, has successfully tamed a {animalName} at ({coords.X}, {coords.Y}, {coords.Z}).";

        (entity.Api as ICoreServerAPI)?.BroadcastMessageToAllGroups(msg, EnumChatType.Notification);
        entity.World.Logger.Notification("[VsVillage] " + msg);
    }

    // ── Generation helpers ──────────────────────────────────────────────────

    private static int GetGeneration(Entity e)
    {
        // VS stores generation directly as a WatchedAttribute int.
        return e.WatchedAttributes.GetInt("generation", 0);
    }

    private static void SetGeneration(Entity e, int gen)
    {
        e.WatchedAttributes.SetInt("generation", gen);
        e.WatchedAttributes.MarkPathDirty("generation");
    }

    // ── Enclosure check (pen or barn) ───────────────────────────────────────

    private static bool IsEnclosed(Entity e, IBlockAccessor ba)
    {
        BlockPos pos = e.Pos.XYZ.AsBlockPos;
        // Barn: the VS RoomRegistry says the spot above the animal is indoors
        if (IsInRoom(pos.UpCopy(), e.Api)) return true;
        return IsInPen(pos, ba);
    }

    private static bool IsInRoom(BlockPos pos, ICoreAPI api)
    {
        try
        {
            var roomReg = api.ModLoader.GetModSystem<RoomRegistry>();
            Room room = roomReg?.GetRoomForPosition(pos);
            return room != null;
        }
        catch { return false; }
    }

    private static bool IsInPen(BlockPos animalPos, IBlockAccessor ba)
    {
        const int MaxRadius = 7; // 15×15 max footprint
        var visited = new HashSet<(int x, int z)>();
        var queue = new Queue<(int x, int z)>();
        int ay = animalPos.Y;
        visited.Add((animalPos.X, animalPos.Z));
        queue.Enqueue((animalPos.X, animalPos.Z));
        int[] dxs = { 1, -1, 0, 0 };
        int[] dzs = { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            var (cx, cz) = queue.Dequeue();
            for (int i = 0; i < 4; i++)
            {
                int nx = cx + dxs[i], nz = cz + dzs[i];
                if (Math.Abs(nx - animalPos.X) > MaxRadius ||
                    Math.Abs(nz - animalPos.Z) > MaxRadius) return false;
                if (visited.Contains((nx, nz))) continue;
                Block body = ba.GetBlock(new BlockPos(nx, ay, nz));
                Block head = ba.GetBlock(new BlockPos(nx, ay + 1, nz));
                if (IsPenBarrier(body) || IsPenBarrier(head)) continue;
                visited.Add((nx, nz));
                queue.Enqueue((nx, nz));
            }
        }
        return true;
    }

    private static bool IsPenBarrier(Block block)
    {
        if (block?.Code == null) return false;
        string p = block.Code.Path;
        return p.Contains("fence") || p.Contains("gate");
    }

    // ── Misc ────────────────────────────────────────────────────────────────

    private static string GetAnimalDisplayName(Entity e)
    {
        // Try to get a readable name from the lang system; fall back to code path.
        string langKey = "item-creature-" + e.Code.Path;
        string translated = Lang.Get(langKey);
        if (!translated.StartsWith("item-creature-")) return translated;
        // Fall back: capitalise the code path
        string path = e.Code.Path;
        if (string.IsNullOrEmpty(path)) return e.Code.ToString();
        return char.ToUpper(path[0]) + path.Substring(1).Replace("-", " ");
    }

    private bool IsShepherd()
        => entity?.Code?.Path?.EndsWith("-shepherd") == true;
}
