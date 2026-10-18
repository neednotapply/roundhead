#!/usr/bin/env bash
# Start only the hidden ROUNDS engine with the freshly built engine dll (dev). Extra args are passed to ROUNDS.
# It uses a private data folder and a private arena link, so it never meets a real Roundhead session.
# Needs one normal Roundhead launch first (that sets up the ROUNDS profile in %LOCALAPPDATA%\CupRounds\rounds-profile).
. "$(dirname "$0")/env.sh"
PROF="$LOCALAPPDATA/CupRounds/rounds-profile/BepInEx"
DEVDATA="$W/devdata"
powershell -NoProfile -File "$(cygpath -w "$D/closeours.ps1")" >/dev/null
for i in 1 2 3 4 5 6 7 8 9 10; do tasklist | grep -qi "^Rounds.exe" || break; sleep 1; done
cp "$ROOT/src/CardEngine/bin/Release/net472/CupRounds.CardEngine.dll" "$PROF/plugins/CupRounds.CardEngine/"
mkdir -p "$DEVDATA"
rm -f "$DEVDATA/engine.port" "$DEVDATA/frames.port"
: > "$PROF/LogOutput.log" 2>/dev/null
PRE=$(cygpath -w "$PROF/core/BepInEx.Preloader.dll")
cd "$ROUNDS_DIR"
export ROUNDHEAD_DEV_DATA="$(cygpath -m "$DEVDATA")"
export ROUNDHEAD_DEV_ARENA='Local\CupRoundsArenaDev'
SteamAppId=1557740 SteamGameId=1557740 ./Rounds.exe --doorstop-enabled true --doorstop-target-assembly "$PRE" --roundhead-engine -batchmode -screen-fullscreen 0 -screen-width 640 -screen-height 360 --dev-marker "$(basename "$W")" "$@" > /dev/null 2>&1 &
echo "started $!"
