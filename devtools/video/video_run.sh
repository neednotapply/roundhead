#!/usr/bin/env bash
# Film one test run of the real games: package like Melty (once per batch), start the launcher with a test file, record the
# Cuphead window from its first frame until SECS seconds after the ROUNDS blob spawns (or the DONE check), then close only
# our games. Takes go to $CUPROUNDS_WORK/video/raw/<name>/ (run.mp4, logs, checks).
#   bash video_run.sh <name> <scene or ""> <secs after spawn> [cards "A,B,C"] [scenario] [done check]
# Cards are exact ROUNDS card names, dealt to both bots (ROUNDHEAD_DEV_CARDS). SEED sets the test seed.
# Delete $CUPROUNDS_WORK/video/.packaged to repackage after a code change.
set -u
. "$(dirname "$0")/../env.sh"
V="$W/video"; DEV="$W/devinstall"; CHK="$W/checks.txt"
L="$LOCALAPPDATA/CupRounds/launcher.log"; TF="$LOCALAPPDATA/Melty/test/roundhead.json"
NAME="$1"; SCENE="$2"; SECS="$3"; CARDS="${4:-}"; SCEN="${5:-}"; DONE="${6:-never}"
OUT="$V/raw/$NAME"; rm -rf "$OUT"; mkdir -p "$OUT"
if tasklist | grep -qi "cuphead.exe\|rounds.exe"; then
  # only ours may be closed; a game the user started stays
  powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")" >/dev/null
  sleep 2
  if tasklist | grep -qi "cuphead.exe\|rounds.exe"; then echo "a game is already running (not ours): not filming"; exit 2; fi
fi
if [ ! -f "$V/.packaged" ]; then
  python -I "$ROOT/tools/sheets.py" preflight --code | tail -1
  pk=$(python -I "$ROOT/tools/package.py" 0.1.0 2>&1) || { echo "$pk" | tail -20; echo "package failed: not running"; exit 1; }
  echo "$pk" | tail -1
  unzip -oq "$ROOT/dist/Roundhead-0.1.0.zip" -d "$DEV"; touch "$V/.packaged"
fi
: > "$CHK"; : > "$L"; mkdir -p "$(dirname "$TF")"
printf '{"run":1,"runs":1,"seed":%d,"checks":"%s"}\n' "${SEED:-4321}" "$(cygpath -m "$CHK")" > "$TF"
export ROUNDHEAD_DEV="$SCEN"
if [ -n "$SCENE" ]; then export ROUNDHEAD_DEV_SCENE="$SCENE"; else unset ROUNDHEAD_DEV_SCENE; fi
if [ -n "$CARDS" ]; then export ROUNDHEAD_DEV_CARDS="$CARDS"; else unset ROUNDHEAD_DEV_CARDS; fi
STOP="$OUT/stop"; rm -f "$STOP"
bash "$D/devloader.sh" on >/dev/null
M=$(cygpath -w "$DEV/managed")
cmd //c start "" "$M\\CupRoundsLauncher.exe" --cuphead "$(cygpath -w "$CUPHEAD_DIR")" --rounds "$(cygpath -w "$ROUNDS_DIR")" --managed "$M"
"$PY" "$D/video/rec.py" "$(cygpath -w "$OUT/run.mp4")" --stop "$(cygpath -w "$STOP")" --secs 900 --wait 120 --method "${METHOD:-print}" > "$OUT/rec.log" 2>&1 &
RP=$!
t0=$(date +%s); spawn=""
while [ $(( $(date +%s) - t0 )) -lt 700 ]; do
  if [ -z "$spawn" ] && grep -q "blob-spawned" "$CHK"; then spawn=$(date +%s); echo "spawned at $(( spawn - t0 )) s"; fi
  if [ -n "$spawn" ] && [ $(( $(date +%s) - spawn )) -ge "$SECS" ]; then echo "filmed $SECS s after spawn"; break; fi
  if grep -q "$DONE" "$CHK"; then echo "done check seen"; sleep 6; break; fi
  if tail -2 "$L" | grep -q "Cuphead closed\|Could not play"; then echo ended; break; fi
  sleep 1
done
touch "$STOP"; wait $RP
echo "spawn_at=$(( ${spawn:-$t0} - t0 ))" > "$OUT/meta.txt"
sleep 1
powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")" >/dev/null
rm -f "$TF"
cp "$DEV/managed/cuphead/BepInEx/LogOutput.log" "$OUT/cuphead.log" 2>/dev/null
cp "$LOCALAPPDATA/CupRounds/rounds-profile/BepInEx/LogOutput.log" "$OUT/rounds.log" 2>/dev/null
cp "$CHK" "$OUT/checks.txt"
cat "$OUT/rec.log"; echo "checks: $(cut -d' ' -f1-2 "$CHK" | tr '\n' ' ')"
