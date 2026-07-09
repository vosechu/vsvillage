#!/bin/bash
# Dev convenience: build outputs must exist. Boot the dev server, send chat commands,
# wait <settle> seconds, stop. Then grep "$DATA/Logs/server-main.log" yourself.
# Usage: scripts/dev-run.sh <settle-seconds> "<command1>" "<command2>" ...
set -u
GAME="${VINTAGE_STORY:-/Applications/Vintage Story.app}"
SERVER="$GAME/VintagestoryServer"
DATA="${VSTEST_DATA:-/tmp/vsgolden}"
MODS_MAIN="$(pwd)/VSVillage/bin/Debug/Mods"
MODS_HARNESS="$(pwd)/VSVillage.TestHarness/bin/Debug/Mods"
LOG="$DATA/Logs/server-main.log"
PIPE="$DATA/in.pipe"
SETTLE="$1"; shift
pkill -f VintagestoryServer 2>/dev/null; sleep 3
mkdir -p "$DATA"; rm -f "$LOG" "$PIPE"; mkfifo "$PIPE"
sleep 900 > "$PIPE" & HOLDER=$!
VINTAGE_STORY="$GAME" "$SERVER" --dataPath "$DATA" --addModPath "$MODS_MAIN" "$MODS_HARNESS" --tracelog < "$PIPE" > "$DATA/console.out" 2>&1 & SRV=$!
for i in $(seq 1 120); do grep -q "Dedicated Server now running on Port" "$LOG" 2>/dev/null && break; sleep 1; done
for cmd in "$@"; do echo "$cmd" > "$PIPE"; sleep 2; done
sleep "$SETTLE"
echo "/stop" > "$PIPE"
for i in $(seq 1 30); do kill -0 "$SRV" 2>/dev/null || break; sleep 1; done
kill "$HOLDER" 2>/dev/null; pkill -f VintagestoryServer 2>/dev/null; rm -f "$PIPE"
echo "dev-run done; log at $LOG"
