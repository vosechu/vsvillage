#!/bin/bash
# Golden behavioral suite runner (fail-closed). Boots one dedicated server, runs a suite, and
# exits 0 only on a well-formed all-pass.
#
# Villager physics only runs when a real player is nearby: the engine skips physics for any entity
# no connected client tracks. So for suites that MOVE villagers this launches a game CLIENT that
# connects to the local server and parks it on the arena — that presence switches real physics on.
# Physics-free suites (nav-probe = direct FindPath, selftest = pass/fail stubs) run headless.
#
# Usage: scripts/golden-suite.sh [suite]
#   WITH_CLIENT=1|0  force / skip the client (default: on, except nav-probe/selftest)
#   WORLD_SRC=<path> world save to serve; MUST contain a pre-made character so the client spawns
#                    straight in. Served as a COPY, so scenario terrain edits never touch it.
#   VSTEST_DATA=<dir> throwaway data dir (default /tmp/vsgolden)
#   SETTLE=<seconds>  hard cap on the run (default 300)
set -u
SUITE="${1:-golden}"
GAME="${VINTAGE_STORY:-/Applications/Vintage Story.app}"
SERVER="$GAME/VintagestoryServer"
CLIENT="$GAME/Vintagestory"
DATA="${VSTEST_DATA:-/tmp/vsgolden}"
WORLD_SRC="${WORLD_SRC:-$HOME/Library/Application Support/VintagestoryData/Saves/foggy doodle world.vcdbs}"
PORT=42420
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODS_MAIN="$ROOT/VSVillage/bin/Debug/Mods"
MODS_HARNESS="$ROOT/VSVillage.TestHarness/bin/Debug/Mods"
TEMPLATE="$ROOT/scripts/golden-serverconfig.template.json"
LOG="$DATA/Logs/server-main.log"
RESULTS="$DATA/golden-results.txt"
PIPE="$DATA/in.pipe"

# Per-suite defaults (all overridable via env):
#  - physics-free suites need no client; movement suites launch one.
#  - `all` runs many scenarios serially in one boot, so it needs a larger settle cap.
case "$SUITE" in
  nav-probe|selftest) WITH_CLIENT="${WITH_CLIENT:-0}"; SETTLE="${SETTLE:-300}" ;;
  all)                WITH_CLIENT="${WITH_CLIENT:-1}"; SETTLE="${SETTLE:-900}" ;;
  *)                  WITH_CLIENT="${WITH_CLIENT:-1}"; SETTLE="${SETTLE:-300}" ;;
esac

SRV=""; HOLDER=""; CLI=""
send() { [ -n "$SRV" ] && kill -0 "$SRV" 2>/dev/null && printf '%s\n' "$1" > "$PIPE" 2>/dev/null; }
cleanup() {
  send "/stop"
  for i in $(seq 1 30); do { [ -n "$SRV" ] && kill -0 "$SRV" 2>/dev/null; } || break; sleep 1; done
  [ -n "$CLI" ] && kill "$CLI" 2>/dev/null
  [ -n "$HOLDER" ] && kill "$HOLDER" 2>/dev/null
  pkill -f VintagestoryServer 2>/dev/null
  rm -f "$PIPE"
}
fail() { echo "GOLDEN SUITE FAIL: $1"; cleanup 2>/dev/null; exit 1; }

echo "== build =="
VINTAGE_STORY="$GAME" dotnet build -c Debug "$ROOT/VSVillage/VSVillage.csproj" >/dev/null 2>&1 || fail "mod build failed"
VINTAGE_STORY="$GAME" dotnet build -c Debug "$ROOT/VSVillage.TestHarness/VSVillage.TestHarness.csproj" >/dev/null 2>&1 || fail "harness build failed"

echo "== stage world (fresh copy of $WORLD_SRC) =="
pkill -f VintagestoryServer 2>/dev/null; sleep 3
rm -rf "$DATA"; mkdir -p "$DATA/Saves" "$DATA/Logs"
[ -f "$WORLD_SRC" ] || fail "world save not found: $WORLD_SRC (set WORLD_SRC to a save with a pre-made character)"
cp "$WORLD_SRC" "$DATA/Saves/default.vcdbs"
sed -e "s|__GOLDEN_SEED__|8543321|" -e "s|__DATA_DIR__|$DATA|g" "$TEMPLATE" > "$DATA/serverconfig.json"

echo "== boot server (client=$WITH_CLIENT) =="
rm -f "$PIPE"; mkfifo "$PIPE"
sleep 900 > "$PIPE" & HOLDER=$!
# --addModPath is a sequence option: ONE flag, space-separated paths (repeating the flag NREs the server).
# VSVILLAGE_GOLDEN_ALLOW opts this throwaway server in to the destructive scenarios (GoldenRunner gates on it).
VINTAGE_STORY="$GAME" VSVILLAGE_GOLDEN_ALLOW=1 "$SERVER" \
  --dataPath "$DATA" --addModPath "$MODS_MAIN" "$MODS_HARNESS" --tracelog \
  < "$PIPE" > "$DATA/console.out" 2>&1 & SRV=$!

booted=0
for i in $(seq 1 120); do grep -q "Dedicated Server now running on Port" "$LOG" 2>/dev/null && { booted=1; break; }; sleep 1; done
[ "$booted" = 1 ] || fail "server never reached the Port line"

if [ "$WITH_CLIENT" = 1 ]; then
  echo "== launch client, connect to 127.0.0.1:$PORT =="
  VINTAGE_STORY="$GAME" "$CLIENT" --connect "127.0.0.1:$PORT" \
    --addModPath "$MODS_MAIN" "$MODS_HARNESS" > "$DATA/client.out" 2>&1 & CLI=$!
  joined=0
  for i in $(seq 1 90); do grep -qE "\] .* joins\." "$LOG" 2>/dev/null && { joined=1; break; }; sleep 1; done
  [ "$joined" = 1 ] || fail "client never joined in-world (see $DATA/client.out)"
  echo "   client joined; letting chunks settle"
  sleep 5
fi

echo "== run suite '$SUITE' =="
send "/time set day"; sleep 2
send "/vsvillage:test run $SUITE"

# Park the player on the arena so villagers are within physics-tracking range. Every scenario logs
# its arena centre via BuildFlatArea; all scenarios in a suite share spawn's centre, so one point
# covers the whole run. Re-issued in the wait loop below in case a teardown drops the player.
TP=""
if [ "$WITH_CLIENT" = 1 ]; then
  for i in $(seq 1 30); do grep -q "BuildFlatArea: floor" "$LOG" 2>/dev/null && break; sleep 1; done
  center=$(grep -oE "BuildFlatArea: floor y=[0-9]+ at [0-9]+, [0-9]+, [0-9]+" "$LOG" 2>/dev/null | head -1 | grep -oE "at [0-9]+, [0-9]+, [0-9]+")
  if [ -n "$center" ]; then
    cx=$(printf '%s' "$center" | grep -oE "[0-9]+" | sed -n 1p)
    cy=$(printf '%s' "$center" | grep -oE "[0-9]+" | sed -n 2p)
    cz=$(printf '%s' "$center" | grep -oE "[0-9]+" | sed -n 3p)
    TP="/tp p[] =$cx =$cy =$cz"
    echo "   parking player at =$cx =$cy =$cz"
    send "$TP"
  fi
fi

seen=0
for i in $(seq 1 $((SETTLE + 60))); do
  grep -q "GOLDEN SUITE COMPLETE" "$LOG" 2>/dev/null && { seen=1; break; }
  # Re-park every ~15s so a multi-scenario suite can't drift the player out of tracking range.
  [ -n "$TP" ] && [ $((i % 15)) -eq 0 ] && send "$TP"
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
