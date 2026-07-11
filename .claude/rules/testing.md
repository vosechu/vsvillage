---
description: How to write and run tests for VS Village — the pure-logic unit suite and the behavioral golden harness. Applies when editing anything under VSVillage.Tests/ or VSVillage.TestHarness/.
globs: "{VSVillage.Tests,VSVillage.TestHarness}/**"
---

# VS Village testing convention

Two suites, both fork-only. `VSVillage.Tests` (xUnit, net10.0) covers **only pure decision
logic** extracted into plain classes — this is the default. `VSVillage.TestHarness` is a
separate mod that runs **behavioral golden tests** on a headless dedicated server (see the
tier-discipline section below for when each applies).

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

Headless behavioral scenarios (spawn villagers on a dedicated server, assert on game state)
are the most expensive and most flake-prone tests here. The framework working is not a licence
to default to it. Prefer, in order:

1. A pure unit test in `VSVillage.Tests` (fast, deterministic, no server) — the default.
2. One more assertion on an existing scenario — it rides a run that already paid the settle
   window, so ~zero extra wall-clock and flake surface. Pack scenarios densely.
3. A new scenario — only with a written `IGoldenScenario.Justification`: why a unit test can't
   cover it, the 3am-page-worthy behavior it protects, and why it's durable.

Authoring rules (a scenario that breaks one does not belong in the suite):
- Never depend on random-world terrain — call `TestScene.BuildFlatArea` for a flat, loaded
  floor and place everything coplanar on it.
- Assert order-independent invariants (never "villager A took chest X").
- Villagers run overlapping tasks (fetch AND return-carry) so state oscillates — sample across
  the settle window and assert accumulated invariants, not an end-of-run snapshot.
- Pair every negative with a positive — a parked/dead villager passes all negatives vacuously.
- Reads are unreliable headless — a chest's `GetBlockEntity` or a villager's `GetEntityById`
  intermittently returns null for seconds at a time. So a point-in-time full-world census (e.g.
  total-item conservation) is not assertable, and even a monotonic read-based check can false-fire;
  gate any read on the BE/entity actually being loaded (see `ContainerFetchScenario.IsChestReadable`).

Run the suite: `scripts/golden-suite.sh golden` (exit 0 = pass). Gate it on push:
`git config core.hooksPath scripts/hooks`. See `VSVillage.TestHarness/README.md`.
