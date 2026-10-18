#!/usr/bin/env bash
# The plain test run (bots play) on a list of Cuphead levels, one after the other, with screenshots and both games' logs
# kept per level under $CUPROUNDS_WORK/devdata/levels/<level>/. Run-and-gun levels must be named by their Levels enum name.
#   bash levels_run.sh scene_level_slime scene_level_flower Platforming_Level_1_1 ...      (MAX=25 shots each by default)
. "$(dirname "$0")/env.sh"
cd "$W"; mkdir -p devdata/levels
for scene in "$@"; do
  out="devdata/levels/$scene"; rm -rf "$out"; mkdir -p "$out"
  echo "=== $scene $(date +%H:%M:%S)"
  # no done check (nothing matches "never"): the shots run to MAX, 2 s apart
  ROUNDHEAD_DEV_SCENE="$scene" SHOT_EVERY=2 bash "$D/devrun_dev.sh" "" 'never' ${MAX:-25} > "$out/run.log" 2>&1
  if grep -q "engine never answered" "$out/run.log"; then
    echo "engine never answered; once more"
    ROUNDHEAD_DEV_SCENE="$scene" SHOT_EVERY=2 bash "$D/devrun_dev.sh" "" 'never' ${MAX:-25} > "$out/run.log" 2>&1
  fi
  cp devinstall/managed/cuphead/BepInEx/LogOutput.log "$out/cuphead.log" 2>/dev/null
  cp "$LOCALAPPDATA/CupRounds/rounds-profile/BepInEx/LogOutput.log" "$out/rounds.log" 2>/dev/null
  cp "$LOCALAPPDATA/CupRounds/launcher.log" "$out/launcher.log" 2>/dev/null
  mv devshots-/*.png "$out/" 2>/dev/null
  echo "checks: $(grep -E '^(pass|fail)' "$out/run.log" | cut -d' ' -f1-2 | tr '\n' ' ')"
  echo "cuphead errors: $(grep -c 'Exception\|Error' "$out/cuphead.log") rounds errors: $(grep -c 'Exception\|Error' "$out/rounds.log") shots: $(ls "$out"/*.png 2>/dev/null | wc -l)"
done
echo LEVELS DONE
