#!/bin/bash
# Golden behavioral suite runner (fail-closed). Builds both mods, boots one headless server,
# runs the given suite (default: golden), and exits 0 only on a well-formed all-pass.
# Usage: scripts/golden-suite.sh [suite]   (env: SETTLE overrides the AI settle window)
set -u
SUITE="${1:-golden}"
# Outer wait budget (seconds) for the completion sentinel, NOT the per-scenario settle window
# (each scenario hardcodes its own SettleSeconds; most exit early via IsSettled). Must exceed the
# WORST-CASE sum of settle windows for the largest suite (nav: 3 x 90s) — a generous budget costs
# nothing on success because the wait loop breaks the moment the sentinel appears.
SETTLE="${SETTLE:-300}"
GAME="${VINTAGE_STORY:-/Applications/Vintage Story.app}"
SERVER="$GAME/VintagestoryServer"
DATA="${VSTEST_DATA:-/tmp/vsgolden}"
MODS_MAIN="$(pwd)/VSVillage/bin/Debug/Mods"
MODS_HARNESS="$(pwd)/VSVillage.TestHarness/bin/Debug/Mods"
LOG="$DATA/Logs/server-main.log"
RESULTS="$DATA/golden-results.txt"
PIPE="$DATA/in.pipe"

fail() { echo "GOLDEN SUITE FAIL: $1"; exit 1; }

echo "== build =="
VINTAGE_STORY="$GAME" dotnet build -c Debug VSVillage/VSVillage.csproj >/dev/null 2>&1 || fail "mod build failed"
VINTAGE_STORY="$GAME" dotnet build -c Debug VSVillage.TestHarness/VSVillage.TestHarness.csproj >/dev/null 2>&1 || fail "harness build failed"

echo "== boot server =="
pkill -f VintagestoryServer 2>/dev/null; sleep 3
# Pinned-seed world, REUSED across runs (one boot per suite run; no per-run worldgen). Random spawn
# terrain is a real hazard — one roll put spawn at sea level and cascading liquid updates ground the
# tick rate to ~16%. So: if the resident world was built from our pinned seed, reuse it as-is;
# otherwise migrate ONCE (wipe, install the seed-pinned config template, let boot create the world).
# Scenario arenas self-heal via TestScene.BuildFlatArea, so reuse stays safe run-over-run.
SEED="${VSTEST_SEED:-8543321}"
TEMPLATE="$(pwd)/scripts/golden-serverconfig.template.json"
if ! grep -q "\"Seed\": \"$SEED\"" "$DATA/serverconfig.json" 2>/dev/null || ! ls "$DATA"/Saves/*.vcdbs >/dev/null 2>&1; then
  echo "== migrate: creating pinned-seed world (seed=$SEED) =="
  rm -rf "$DATA"; mkdir -p "$DATA"
  sed -e "s|__GOLDEN_SEED__|$SEED|" -e "s|__DATA_DIR__|$DATA|g" "$TEMPLATE" > "$DATA/serverconfig.json"
fi
mkdir -p "$DATA"; rm -f "$LOG" "$RESULTS" "$PIPE"; mkfifo "$PIPE"
sleep 900 > "$PIPE" & HOLDER=$!
# --addModPath is a sequence option: ONE flag, space-separated paths (repeating the flag NREs the server).
# VSVILLAGE_GOLDEN_ALLOW opts this throwaway server in to the destructive scenarios (GoldenRunner gates on it).
VINTAGE_STORY="$GAME" VSVILLAGE_GOLDEN_ALLOW=1 "$SERVER" --dataPath "$DATA" --addModPath "$MODS_MAIN" "$MODS_HARNESS" --tracelog < "$PIPE" > "$DATA/console.out" 2>&1 & SRV=$!

booted=0
for i in $(seq 1 120); do
  if grep -q "Dedicated Server now running on Port" "$LOG" 2>/dev/null; then booted=1; break; fi
  sleep 1
done

# Only write to the pipe while the server (its sole reader) is alive — otherwise the open-for-write
# blocks forever. A crashed server must yield a clean exit 1, never a wedged job.
send() { kill -0 "$SRV" 2>/dev/null && printf '%s\n' "$1" > "$PIPE" 2>/dev/null; }
cleanup() { send "/stop"; for i in $(seq 1 30); do kill -0 "$SRV" 2>/dev/null || break; sleep 1; done; kill "$HOLDER" 2>/dev/null; pkill -f VintagestoryServer 2>/dev/null; rm -f "$PIPE"; }

[ "$booted" = 1 ] || { cleanup; fail "server never reached the Port line"; }

echo "== run suite '$SUITE' =="
send "/time set day"; sleep 2
send "/vsvillage:test run $SUITE"

seen=0
for i in $(seq 1 $((SETTLE + 60))); do
  if grep -q "GOLDEN SUITE COMPLETE" "$LOG" 2>/dev/null; then seen=1; break; fi
  sleep 1
done
cleanup

# --- fail-closed evaluation ---
[ "$seen" = 1 ] || fail "no completion sentinel (server hang/crash)"
[ -s "$RESULTS" ] || fail "results file missing or empty"
# Scope the counts to the matched SUMMARY line, not any line in the file — a future check
# description containing "failed=0" must not be able to feed the gate a false green.
summary=$(grep -E "^SUMMARY $SUITE " "$RESULTS")
[ -n "$summary" ] || fail "no SUMMARY line for '$SUITE'"
scenarios=$(printf '%s' "$summary" | grep -oE "scenarios=[0-9]+" | cut -d= -f2)
failed=$(printf '%s' "$summary" | grep -oE "failed=[0-9]+" | cut -d= -f2)
[ "${scenarios:-0}" -gt 0 ] || fail "zero scenarios ran"
# Match real error markers (log-level [Error], fatal unhandled exceptions, exception-type lines
# ending in ':') — not a bare "exception" substring in a benign notification. Still fail-safe.
grep -qE "\[Error\]|Unhandled exception|Exception:" "$LOG" && fail "errors in server log"
[ "${failed:-1}" = 0 ] || fail "$failed scenario(s) failed (see $RESULTS)"

echo "GOLDEN SUITE PASS: $scenarios scenario(s), 0 failed"
exit 0
