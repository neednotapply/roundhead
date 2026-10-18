#!/usr/bin/env bash
# The Cuphead status bench twice: on a clean boss, then with ROUNDS's own Cold Bullets slowing it at the same time.
. "$(dirname "$0")/env.sh"
cd "$W"; mkdir -p devdata
# a neutral card: random picks can include passive slows (PCE Glare) that skew the timing checks
SHOT_EVERY=3 bash "$D/devrun_dev.sh" statuses 'dev-statuses$' 20 "Defender" > devdata/statuses-clean.log 2>&1
echo "clean: $(grep -E '^(pass|fail) dev-status|^fail' devdata/statuses-clean.log | tr '\n' ' ' | cut -c1-900)"
cp devinstall/managed/cuphead/BepInEx/LogOutput.log devdata/statuses-clean-cuphead.log
SHOT_EVERY=3 bash "$D/devrun_dev.sh" statuses 'dev-statuses$' 20 "Cold bullets" > devdata/statuses-cold.log 2>&1
echo "cold: $(grep -E '^(pass|fail) dev-status|^fail' devdata/statuses-cold.log | tr '\n' ' ' | cut -c1-900)"
cp devinstall/managed/cuphead/BepInEx/LogOutput.log devdata/statuses-cold-cuphead.log
echo "STATUSES DONE"
