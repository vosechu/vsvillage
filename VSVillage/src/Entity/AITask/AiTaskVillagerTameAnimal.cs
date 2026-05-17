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

// Shepherd task: once per day find a generation-0 enclosed animal, tame it
// (set generation to 1) and broadcast a notification to all players.
public class AiTaskVillagerTameAnimal : AiTaskBase
{
    private float searchRadius = 30f;

    // Resolved lazily on first use because Api may not be ready at task construction.
    // The "inanimate" tag is set on every vanilla nonliving entity (boats, armor stands,
    // straw dummies, mannequins, bobbers, echochambers, elevators, library resonators,
    // skeleton-arm, etc.) so a single overlap check excludes the whole class.
    private TagSetFast inanimateTagSet;
    private bool inanimateTagsResolved;

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

    // Lore / hostile mob prefixes - never tame these.
    private static readonly string[] LorePrefixes =
    {
        "bell-", "bellmini-", "bowtorn-", "drifter-", "locust-", "shiver-"
    };
    private static readonly string[] LoreExact = { "mechhelper" };

    private TagSetFast ResolveInanimateTags()
    {
        if (inanimateTagsResolved) return inanimateTagSet;
        // TryCreate logs registry issues itself. If "inanimate" is unregistered for
        // any reason the set comes back empty and the overlap check is skipped, so
        // we degrade to the pre-fix behaviour rather than blocking the task.
        entity.Api.EntityTagRegistry.TryCreateTagSetAndLogIssues(out inanimateTagSet, "inanimate");
        inanimateTagsResolved = true;
        return inanimateTagSet;
    }

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
        TagSetFast inanimateSet = ResolveInanimateTags();
        // Pen check rooted on shepherd's workstation, matching hire validation. No workstation, no tame.
        BlockPos wsPos = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (wsPos == null) return;
        Entity[] nearby = entity.World.GetEntitiesAround(entity.Pos.XYZ, searchRadius, 6f, e =>
        {
            if (e == entity || !e.Alive) return false;
            if (e.Code?.Path == "player") return false;
            string path = e.Code?.Path;
            if (string.IsNullOrEmpty(path)) return false;
            if (e.Code.Domain == "vsvillage") return false;
            // Skip everything tagged "inanimate" by its entity JSON. Covers vanilla
            // nonliving entities (boats, armor stands, straw dummies, mannequins,
            // bobbers, echochambers, elevators, library resonators, skeleton-arm)
            // and any modded entity that follows the convention.
            if (!inanimateSet.IsEmpty && e.Tags.Overlaps(inanimateSet)) return false;
            // Never tame lore/hostile mobs.
            if (IsLoreEntity(path)) return false;
            // Must be generation 0 (wild / not yet tamed)
            if (GetGeneration(e) != 0) return false;
            // Must be in THIS shepherd's pen (same BFS the hire check uses).
            return VillagerHireRequirementChecker.IsAnimalInShepherdPen(wsPos, e.Pos.XYZ.AsBlockPos, ba);
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

    // === Generation helpers ===

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

    // === Misc ===

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
