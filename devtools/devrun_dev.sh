#!/usr/bin/env bash
# A dev scenario on the real games, screenshotting the Cuphead window: package like Melty, a test run with ROUNDHEAD_DEV set,
# shots every SHOT_EVERY s (default 1) from the moment the blob spawns until the DONE check is reported or MAX shots.
#   bash devrun_dev.sh <scenario> <done check> [max shots] [cards]     e.g. bash devrun_dev.sh revive dev-revive 60
# Scenarios: "" (the plain Melty test run), retry, revive, statuses, win. ROUNDHEAD_DEV_SCENE picks the level
# (a Levels enum name such as Platforming_Level_1_1, or a scene name such as scene_level_flower).
# Never run it while you play Roundhead yourself: it shares the ROUNDS profile and closes only the games it started.
set -u
. "$(dirname "$0")/env.sh"
DEV="$W/devinstall"; CHK="$W/checks.txt"
L="$LOCALAPPDATA/CupRounds/launcher.log"
TF="$LOCALAPPDATA/Melty/test/roundhead.json"
SCEN="$1"; DONE="$2"; MAX=${3:-60}; CARDS=${4:-}; EVERY=${SHOT_EVERY:-1}
powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")"
python -I "$ROOT/tools/sheets.py" preflight --code | tail -1
pk=$(python -I "$ROOT/tools/package.py" 0.1.0 2>&1) || { echo "$pk" | tail -20; echo "package failed: not running"; exit 1; }
echo "$pk" | tail -1
unzip -oq "$ROOT/dist/Roundhead-0.1.0.zip" -d "$DEV"
: > "$CHK"
: > "$L"
mkdir -p "$(dirname "$TF")"
printf '{"run":1,"runs":1,"seed":4321,"checks":"%s"}\n' "$(cygpath -m "$CHK")" > "$TF"
export ROUNDHEAD_DEV="$SCEN"
[ -n "$CARDS" ] && export ROUNDHEAD_DEV_CARDS="$CARDS"
# Melty switches the mod loader off whenever it looks at the game folders: put it back right before the launch.
bash "$D/devloader.sh" on >/dev/null
M=$(cygpath -w "$DEV/managed")
cmd //c start "" "$M\\CupRoundsLauncher.exe" --cuphead "$(cygpath -w "$CUPHEAD_DIR")" --rounds "$(cygpath -w "$ROUNDS_DIR")" --managed "$M"
OUT="$W/devshots-$SCEN"; mkdir -p "$OUT"; rm -f "$OUT"/*.png
CAP=$(cygpath -w "$D/capture.ps1")
n=0; t0=$(date +%s)
while [ $(( $(date +%s) - t0 )) -lt 400 ]; do
  if grep -q "blob-spawned" "$CHK" && [ $n -lt $MAX ] && tasklist | grep -qi cuphead.exe; then
    n=$((n+1)); powershell -NoProfile -File "$CAP" -ProcessName Cuphead -Out "$(cygpath -w "$OUT")\s$(printf %03d $n).png" >/dev/null 2>&1
  fi
  if grep -q "$DONE" "$CHK"; then echo "done check seen"; break; fi
  if [ $n -ge $MAX ]; then echo "max shots"; break; fi
  if tail -2 "$L" | grep -q "Cuphead closed\|Could not play"; then echo ended; break; fi
  sleep "$EVERY"
done
echo "loop left after $(( $(date +%s) - t0 )) s"
sleep 2
powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")"
rm -f "$TF"
tail -3 "$L"; echo "--- checks"; cat "$CHK"; echo "--- shots: $n in $OUT"
