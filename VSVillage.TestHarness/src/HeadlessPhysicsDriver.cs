using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace VsVillageTest;

// Restores entity locomotion physics on a playerless headless server.
//
// The engine's PhysicsManager.DoWork skips `OnPhysicsTick` for every entity with IsTracked == 0, and
// IsTracked is set purely from distance to the nearest CONNECTED CLIENT (AlwaysActive exempts an entity
// from the State gate and keeps its AI ticking, but not from tracking). So with zero clients no entity
// on the server gets physics: a commanded walk vector produces no displacement and entities don't even
// fall under gravity. Every "moving" villager in earlier golden runs was actually
// AiTaskGotoAndInteract's stuck-recovery teleporting ~2 path nodes at a time along the A* route.
//
// This driver calls each untracked, Active, non-player entity's IPhysicsTickable.OnPhysicsTick at the
// engine's own fixed 30Hz step, on the main thread — the same thread PhysicsManager uses below 800
// tickables. AfterPhysicsTick needs no help (the engine already calls it for ALL tickables, ungated).
// The IsTracked == 0 guard makes the driver stand down automatically for any entity a real client is
// tracking (watch mode), so nothing is ever double-ticked.
//
// Gated on VSVILLAGE_GOLDEN_ALLOW=1 (the golden runner and watch-mode recipe set it), so a harness mod
// accidentally left loaded in a real game never burns physics CPU on far-away entities.
public class HeadlessPhysicsDriver : ModSystem
{
    private const float StepSeconds = 1f / 30f;   // the engine's fixed physics step
    private const float MaxAccumSeconds = 0.4f;   // same runaway clamp as PhysicsManager

    private ICoreServerAPI sapi;
    private float accum;
    private bool loggedOnce;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        if (Environment.GetEnvironmentVariable("VSVILLAGE_GOLDEN_ALLOW") != "1") return;
        sapi = api;
        api.Event.RegisterGameTickListener(OnTick, 16);
        api.Logger.Notification("[harness] HeadlessPhysicsDriver active: driving physics for untracked entities at 30Hz");
    }

    private void OnTick(float dt)
    {
        accum = Math.Min(accum + dt, MaxAccumSeconds);
        int steps = (int)(accum / StepSeconds);
        if (steps == 0) return;
        accum -= steps * StepSeconds;

        foreach (KeyValuePair<long, Entity> kv in sapi.World.LoadedEntities)
        {
            Entity e = kv.Value;
            if (e is EntityPlayer || e.IsTracked != 0 || e.State != EnumEntityState.Active || !e.Alive) continue;

            IPhysicsTickable tickable = FindTickable(e);
            if (tickable == null) continue;

            for (int i = 0; i < steps; i++) tickable.OnPhysicsTick(StepSeconds);
            if (!loggedOnce) { loggedOnce = true; sapi.Logger.Notification("[harness] physics-driving first entity: {0}", e.Code); }
        }
    }

    private static IPhysicsTickable FindTickable(Entity e)
    {
        List<EntityBehavior> behaviors = e.SidedProperties?.Behaviors;
        if (behaviors == null) return null;
        for (int i = 0; i < behaviors.Count; i++)
            if (behaviors[i] is IPhysicsTickable t) return t;
        return null;
    }
}
