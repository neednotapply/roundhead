#!/usr/bin/env bash
# One short dev-engine run: how ROUNDS draws the player and gun (engine log, "arena look") and the world/UI frames as PNGs.
# Output: $CUPROUNDS_WORK/devdata/look/
. "$(dirname "$0")/env.sh"
cd "$W"
if tasklist | grep -qi "^cuphead.exe\|^Rounds.exe"; then echo "a game is running; not starting the dev engine"; exit 1; fi
export ROUNDHEAD_DEV_DUMP=1
bash "$D/rounds_dev.sh"
for k in $(seq 1 40); do [ -f devdata/engine.port ] && break; sleep 1; done
mkdir -p devdata/look; rm -f devdata/look/*
timeout 300 python -I "$D/cardprobe.py" devdata/look/probe.json "Cold bullets" "Big bullets" > devdata/look/probe.log 2>&1 &
PROBE=$!
prev=0
for s in 15 25 35 50; do sleep $((s - prev)); prev=$s; "$PY" "$D/framedump.py" devdata/look/f$s; done
wait $PROBE
echo "probe: $(grep -E '^SUMMARY' devdata/look/probe.log)"
LOG="$LOCALAPPDATA/CupRounds/rounds-profile/BepInEx/LogOutput.log"
grep -n "arena look" -A 80 "$LOG" | head -170 > devdata/look/look.txt
echo "look lines: $(grep -c '' devdata/look/look.txt); perf: $(grep 'arena perf' "$LOG" | tail -1)"
powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")" >/dev/null
echo LOOK DONE
