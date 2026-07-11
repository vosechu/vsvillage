# VS Village Test Harness (fork-only)

Headless behavioral golden-test framework. A **separate mod assembly**, never shipped in the
release zip, loaded only via `--addModPath`. It spawns `AlwaysActive` villagers on a dedicated
server (no player needed) and asserts on live game state.

## Run

    scripts/golden-suite.sh golden     # exit 0 = pass, 1 = fail

Under the hood: build both mods -> boot one headless server -> `/vsvillage:test run golden`
-> read `<dataPath>/golden-results.txt` fail-closed -> stop.

`$VINTAGE_STORY` must point at the game install; `$VSTEST_DATA` overrides the throwaway data
dir (default `/tmp/vsgolden`). Set it per-invocation if two suites might run on one host.

## Safety: scenarios permanently edit terrain

Scenarios call `TestScene.BuildFlatArea`, which **permanently** replaces blocks (teardown removes
the spawned chests/villagers but does not restore terrain). So a run is only safe against a
throwaway world. Two guards keep it there:

- The runner scripts use an isolated `--dataPath` (a fresh generated world), never a real save.
- `GoldenRunner` refuses `/vsvillage:test run` unless the server env has `VSVILLAGE_GOLDEN_ALLOW=1`.
  `golden-suite.sh` sets it; a normal game launch never does — so even if the harness mod is
  accidentally left loaded in a real game, the command is inert (it prints how to opt in rather
  than carving up spawn). To watch a scenario live in the client, set the same var on the client
  launch.

## Gate it on every push

    git config core.hooksPath scripts/hooks

`scripts/hooks/pre-push` runs the suite; a failure blocks the push. It is advisory
(`git push --no-verify` bypasses it) — keep it fast so no one wants to.

## Add a scenario — read the bar first

Behavioral tests are the exception, not the default. Prefer a `VSVillage.Tests` unit test;
failing that, add an assertion to an existing scenario; only then a new scenario, which must
state its `IGoldenScenario.Justification`. Two rules keep scenarios durable:

- **Deterministic environment.** Never depend on random-world terrain. Call
  `TestScene.BuildFlatArea` to lay a flat, loaded floor and place everything coplanar on it.
- **Assert over the window, order-independently.** Villagers run overlapping tasks (fetch AND
  return-carry), so state oscillates. Sample across the settle window and assert accumulated
  invariants ("chest was emptied at least once", "control was never touched") rather than an
  end-of-run snapshot. Pair every negative with a positive — a parked villager passes all
  negatives vacuously.

See `.claude/rules/testing.md` for the full tier-discipline ladder.
