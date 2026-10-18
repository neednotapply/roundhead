#!/usr/bin/env bash
# Watch a Melty test run from outside: every 5 s for N s, note the game processes, Cuphead's plugin log in Melty's install
# and Roundhead's runtime folder. Output: $CUPROUNDS_WORK/devdata/meltywatch/<time>.log plus copies of the logs at the end.
#   bash meltywatch.sh [seconds]
# REAL_DATA: the runtime folder Melty's runs use (default %LOCALAPPDATA%\CupRounds). If this shell runs inside an app
# container that redirects %LOCALAPPDATA% (the Claude desktop app does), point it at the real folder through the UNC path,
# e.g. REAL_DATA='//localhost/C$/Users/<you>/AppData/Local/CupRounds'.
. "$(dirname "$0")/env.sh"
SECS=${1:-200}
OUT="$W/devdata/meltywatch"; mkdir -p "$OUT"; LOG="$OUT/$(date +%H%M%S).log"
MI="$(cygpath -u "$APPDATA")/Melty/mods/managed/roundhead"
REAL="${REAL_DATA:-$(cygpath -u "$LOCALAPPDATA")/CupRounds}"
t0=$(date +%s)
while [ $(( $(date +%s) - t0 )) -lt "$SECS" ]; do
  {
    echo "=== $(date +%H:%M:%S)"
    tasklist 2>/dev/null | grep -i "cuphead\|rounds" | awk '{print $1, $2, $5, $6}'
    echo "-- cuphead plugin log: $(wc -l < "$MI/cuphead/BepInEx/LogOutput.log" 2>/dev/null) lines; last: $(tail -1 "$MI/cuphead/BepInEx/LogOutput.log" 2>/dev/null | cut -c1-150)"
    echo "-- runtime folder: $(ls "$REAL" 2>/dev/null | tr '\n' ' ')"
    echo "-- launcher: $(tail -1 "$REAL/launcher.log" 2>/dev/null | cut -c1-120)"
    echo "-- engine log: $(wc -l < "$REAL/rounds-profile/BepInEx/LogOutput.log" 2>/dev/null) lines; last: $(tail -1 "$REAL/rounds-profile/BepInEx/LogOutput.log" 2>/dev/null | cut -c1-120)"
  } >> "$LOG"
  sleep 5
done
cp "$MI/cuphead/BepInEx/LogOutput.log" "$OUT/cuphead-melty.log" 2>/dev/null
cp "$REAL/launcher.log" "$OUT/launcher-real.log" 2>/dev/null
cp "$REAL/rounds-profile/BepInEx/LogOutput.log" "$OUT/rounds-real.log" 2>/dev/null
echo "WATCH DONE $LOG"
