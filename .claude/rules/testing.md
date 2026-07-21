---
description: How to write and run tests for VS Village — the pure-logic unit suite and the behavioral golden harness. Applies when editing anything under VSVillage.Tests/ or VSVillage.TestHarness/.
globs: "{VSVillage.Tests,VSVillage.TestHarness}/**"
---

# VS Village testing convention

Two suites, both fork-only. `VSVillage.Tests` (xUnit, net10.0) covers **only pure decision
logic** extracted into plain classes — this is the default. `VSVillage.TestHarness` is a
separate mod that runs **behavioral golden tests** on a dedicated server — with a real game
client connected for the suites that move villagers (see the tier-discipline section below for
when each applies).

## The boundary (the one rule that matters)

A test may reference VS **value types** (e.g. `BlockPos`). A test must **never** construct
or invoke `ICoreAPI`, an `IWorldAccessor`, an `EntityAgent`, a `BlockEntity`, or the
pathfinder. The compiler does not enforce this — you must. If logic needs a world, it is
not unit-testable here; verify it in-game instead. Keep the testable decision (e.g. "how
many items to move given need N, available M") in a plain method that takes plain values.

## Conventions

- One test class per context: `ClassUnderTest_When_<context>`.
- Test methods are the "it": `It_<expected_behavior>`.
- Built-in `Assert` only (`Assert.True/Equal/False/Null`). No third-party assertion library.
- Table-driven cases use `[Theory]` + `[InlineData(...)]`.
- Namespace: `VsVillage.Tests`.

## Running

    VINTAGE_STORY="<game install>" dotnet test VSVillage.Tests/VSVillage.Tests.csproj

`VINTAGE_STORY` is required even for tests — `VintagestoryAPI.dll` (value types) resolves
from it. On macOS the install is `/Applications/Vintage Story.app`.

## Gotcha

The test project references `VintagestoryAPI.dll` **copy-local** (no `<Private>false>`),
unlike the mod project. Tests load VS value types at runtime; the test assembly is never
packaged, so copying is correct. Do not "fix" this to match the mod's `Private=false`.

## Behavioral golden tests (VSVillage.TestHarness) — the bar

Behavioral scenarios (spawn villagers on a dedicated server, assert on game state) are the most
expensive and most flake-prone tests here. The framework working is not a licence
to default to it. Prefer, in order:

1. A pure unit test in `VSVillage.Tests` (fast, deterministic, no server) — the default.
2. One more assertion on an existing scenario — it rides a run that already paid the settle
   window, so ~zero extra wall-clock and flake surface. Pack scenarios densely.
3. A new scenario — only with a written `IGoldenScenario.Justification`: why a unit test can't
   cover it, the 3am-page-worthy behavior it protects, and why it's durable.

Denser still: run independent behaviors CONCURRENTLY in one settle window rather than as serial
scenarios — the window is the dominant cost, so N behaviors in one window cost ~max, not sum. The
`golden` suite does this (`HaulGateScenario` = container-fetch + shepherd-feed-haul in disjoint
villages near spawn). The trade-off is per-run localization: a red merged gate doesn't say WHICH
behavior broke, so keep each behavior available as a standalone suite (`golden-suite.sh container`,
`... feedhaul`) to re-run in isolation. Behaviors that touch the same item/village WILL cross-
contaminate if merged naively (a farmer draining the shepherd's feed chest) — give each its own
village, sized so neither's `ScanContainers` reaches the other's containers.

Authoring rules (a scenario that breaks one does not belong in the suite):
- Never depend on random-world terrain — call `TestScene.BuildFlatArea` for a flat, loaded
  floor and place everything coplanar on it.
- Assert order-independent invariants (never "villager A took chest X").
- Villagers run overlapping tasks (fetch AND return-carry) so state oscillates — sample across
  the settle window and assert accumulated invariants, not an end-of-run snapshot.
- Pair every negative with a positive — a parked/dead villager passes all negatives vacuously.
- Reads are reliable because movement suites run with a client parked on the arena, which keeps the
  nearby chunks loaded. (Headless, a chest's `GetBlockEntity` or a villager's `GetEntityById` returned
  null for seconds at a time, and chunks neighbouring spawn decayed to permanently-unreadable — which
  forced per-read readability guards and spawn-chunk `dirX/dirZ` anchoring. The client route removed
  both; the scenarios now read directly and place arenas with plain offsets.)
- Still pair every negative with a positive/liveness control (e.g. `ShepherdFeedPensScenario`'s
  live-baker check) so a parked, dead, or never-spawned villager can't pass a negative vacuously. That
  discipline is about the actor being present, not about read reliability, so it stays.
- The engine skips entity physics for anything no CONNECTED CLIENT is near (`PhysicsManager.DoWork`:
  `if (entity.IsTracked == 0) continue;` — `AlwaysActive` keeps AI ticking but does not exempt
  physics). On a playerless server, walk vectors yield zero displacement and entities don't even fall.
  So movement suites run with a real CLIENT connected: `golden-suite.sh` launches one and teleports
  it onto the arena, which makes the nearby villagers `IsTracked` and their physics run. Physics-free
  suites (`nav-probe` = direct FindPath, `selftest`) skip the client. Without a client villagers only
  "move" via the stuck-recovery teleport — ~2 path nodes at a time, never through a cell a teleport
  can't land in (e.g. a closed door) — so a movement scenario freezing usually means the client didn't
  connect or park: check the server log for the `joins.` line and `Ok, teleported`. (This replaced an
  earlier `HeadlessPhysicsDriver` that hand-drove `OnPhysicsTick`; a control run proved the client
  route: no client froze the shepherd and failed `nav-gate`, a client crossed the closed gate and passed.)
- Assert what the SHEPHERD deposits, never what an animal consumed. A consumer animal is spawned PENNED
  via `ScenarioKit.PenAnimal` — with a client connected the animal comes alive (physics + AI) and would
  wander off, so a fence ring keeps it a fixed diet source beside its trough (a fence's tall collision
  box holds it in, no lid needed). It may nibble what was deposited, but the "filled at least once"
  latch survives that. And the shepherd feed feature refuses a trough with no suitable consumer nearby
  (no animal = no need), so EVERY shepherd trough-fill scenario needs a live animal of the right species
  beside the trough (chicken → small trough, pig/goat/sheep → large) or it silently fills nothing.
- Scenario worlds are fresh per suite run (`golden-suite.sh` wipes `$VSTEST_DATA` before boot):
  terrain edits are permanent, and imperfect teardowns from older scenario versions left ghost
  terrain that stalled later runs. Never assume a reused world is clean.
- Release every trough/chest claim your villagers hold on teardown (`ScenarioKit.ReleaseClaims`, before
  despawning). The `all` suite runs scenarios back-to-back in ONE boot with no restart to reset the
  process-static claim registries, so an un-released claim bleeds into the next scenario and blocks it.
- A new scenario auto-joins the one-boot `all` run (reflection over `IGoldenScenario`) — no list to edit.
  Set `InAllSuite => false` only for a framework stub (selftest) or an aspirational scenario (nav).

Run the suite: `scripts/golden-suite.sh golden` (exit 0 = pass). Movement suites launch a game
client, so they need a display and a world save with a character (`WORLD_SRC`, default the
`foggy doodle world` save); physics-free suites (`nav-probe`, `selftest`) run without one. There
is no push hook — run it by hand. See `VSVillage.TestHarness/README.md`.
