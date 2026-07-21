# VS Village Test Harness (fork-only)

Behavioral golden-test framework. A **separate mod assembly**, never shipped in the release zip,
loaded only via `--addModPath`. It spawns `AlwaysActive` villagers on a dedicated server and
asserts on live game state. Suites that move villagers also connect a real game client — villager
physics only runs for entities a client is near — so those need a display; physics-free suites
(`nav-probe`, `selftest`) run headless.

## Run

    scripts/golden-suite.sh golden      # exit 0 = pass, 1 = fail
    scripts/golden-suite.sh nav-probe   # headless (no client), pathfinder diagnostic

Under the hood: build both mods -> serve a fresh copy of a world with a character -> boot one
dedicated server -> (for movement suites) launch a game client and park it on the arena ->
`/vsvillage:test run <suite>` -> read `<dataPath>/golden-results.txt` fail-closed -> stop.

Env: `$VINTAGE_STORY` points at the game install; `$VSTEST_DATA` is the throwaway data dir
(default `/tmp/vsgolden`); `$WORLD_SRC` is the world save served (default the `foggy doodle world`
save — must contain a pre-made character so the client spawns straight in; served as a COPY, so
scenario terrain edits never touch it); `WITH_CLIENT=1|0` forces / skips the client.

## Safety: scenarios permanently edit terrain

Scenarios call `TestScene.BuildFlatArea`, which **permanently** replaces blocks (teardown removes
the spawned chests/villagers but does not restore terrain). So a run is only safe against a
throwaway world. Two guards keep it there:

- The runner serves a COPY of the world under an isolated `--dataPath`, never the real save.
- `GoldenRunner` refuses `/vsvillage:test run` unless the server env has `VSVILLAGE_GOLDEN_ALLOW=1`.
  `golden-suite.sh` sets it; a normal game launch never does — so even if the harness mod is
  accidentally left loaded in a real game, the command is inert (it prints how to opt in rather
  than carving up spawn). To watch a scenario live in the client, set the same var on the client
  launch.

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
