#!/bin/bash
# Golden behavioral suite runner (fail-closed). Builds both mods, boots one headless server,
# runs the given suite (default: golden), and exits 0 only on a well-formed all-pass.
# Usage: scripts/golden-suite.sh [suite]   (env: SETTLE overrides the AI settle window)
set -u
SUITE="${1:-golden}"
SETTLE="${SETTLE:-35}"
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
mkdir -p "$DATA"; rm -f "$LOG" "$RESULTS" "$PIPE"; mkfifo "$PIPE"
sleep 900 > "$PIPE" & HOLDER=$!
# --addModPath is a sequence option: ONE flag, space-separated paths (repeating the flag NREs the server).
VINTAGE_STORY="$GAME" "$SERVER" --dataPath "$DATA" --addModPath "$MODS_MAIN" "$MODS_HARNESS" --tracelog < "$PIPE" > "$DATA/console.out" 2>&1 & SRV=$!

booted=0
for i in $(seq 1 120); do
  if grep -q "Dedicated Server now running on Port" "$LOG" 2>/dev/null; then booted=1; break; fi
  sleep 1
done

cleanup() { echo "/stop" > "$PIPE" 2>/dev/null; for i in $(seq 1 30); do kill -0 "$SRV" 2>/dev/null || break; sleep 1; done; kill "$HOLDER" 2>/dev/null; pkill -f VintagestoryServer 2>/dev/null; rm -f "$PIPE"; }

[ "$booted" = 1 ] || { cleanup; fail "server never reached the Port line"; }

echo "== run suite '$SUITE' =="
echo "/time set day" > "$PIPE"; sleep 2
echo "/vsvillage:test run $SUITE" > "$PIPE"

seen=0
for i in $(seq 1 $((SETTLE + 60))); do
  if grep -q "GOLDEN SUITE COMPLETE" "$LOG" 2>/dev/null; then seen=1; break; fi
  sleep 1
done
cleanup

# --- fail-closed evaluation ---
[ "$seen" = 1 ] || fail "no completion sentinel (server hang/crash)"
[ -s "$RESULTS" ] || fail "results file missing or empty"
grep -qE "^SUMMARY $SUITE " "$RESULTS" || fail "no SUMMARY line for '$SUITE'"
scenarios=$(grep -oE "scenarios=[0-9]+" "$RESULTS" | head -1 | cut -d= -f2)
failed=$(grep -oE "failed=[0-9]+" "$RESULTS" | head -1 | cut -d= -f2)
[ "${scenarios:-0}" -gt 0 ] || fail "zero scenarios ran"
grep -qE "\[Error\]|Exception" "$LOG" && fail "errors in server log"
[ "${failed:-1}" = 0 ] || fail "$failed scenario(s) failed (see $RESULTS)"

echo "GOLDEN SUITE PASS: $scenarios scenario(s), 0 failed"
exit 0
