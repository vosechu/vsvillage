# VS Village Test Harness (fork-only)

Headless behavioral golden-test framework. A **separate mod assembly**, never shipped in the
release zip, loaded only via `--addModPath`. It spawns `AlwaysActive` villagers on a dedicated
server (no player needed) and asserts on live game state.

## Run

    scripts/golden-suite.sh golden     # exit 0 = pass, 1 = fail

Under the hood: build both mods -> boot one headless server -> `/vsvillage:test run golden`
-> read `<dataPath>/golden-results.txt` fail-closed -> stop.

Dev iteration on one command (interactive, not fail-closed):

    scripts/dev-run.sh 40 "/time set day" "/vsvillage:test run golden"
    # then read /tmp/vsgolden/Logs/server-main.log and /tmp/vsgolden/golden-results.txt

`$VINTAGE_STORY` must point at the game install; `$VSTEST_DATA` overrides the throwaway data
dir (default `/tmp/vsgolden`). Set it per-invocation if two suites might run on one host.

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
