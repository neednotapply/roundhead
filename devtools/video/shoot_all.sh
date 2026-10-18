#!/usr/bin/env bash
# The filming batch behind the Melty trailer: one take per boss with a dealt card build, then the knockout scenario.
# About two minutes per take. Log: $CUPROUNDS_WORK/video/raw/batch.log. Then: sheets.py per take, pick moments, edit.py.
. "$(dirname "$0")/../env.sh"
mkdir -p "$W/video/raw"; rm -f "$W/video/.packaged"
take() { echo "=== $1 $(date +%H:%M:%S)"; SEED=$RANDOM bash "$D/video/video_run.sh" "$@" 2>&1 | tail -3; }
{
take veggies "" 70 "Big Bullet,Poison bullets,SPRAY"
take slime scene_level_slime 70 "Timed detonation,Big Bullet,Poison bullets"
take flower scene_level_flower 70 "Big Bullet,Poison bullets,Barrage"
take frogs scene_level_frogs 70 "SPRAY,Thruster"
take baroness scene_level_baroness 70 "Frost slam,Healing field,Big Bullet"
take clown scene_level_clown 70 "Barrage,Cold bullets"
take dragon scene_level_dragon 70 "Timed detonation,Fast ball"
take pirate scene_level_pirate 70 "Big Bullet,Thruster,Tank"
take bee scene_level_bee 70 "SPRAY,Poison bullets"
take train scene_level_train 70 "Barrage,Fast ball"
take devil scene_level_devil 70 "Big Bullet,Timed detonation,Poison bullets"
take forest Platforming_Level_1_1 70 "Poison bullets,SPRAY"
take mouse scene_level_mouse 70 "Big Bullet,Barrage"
# the developer win scenario: bots immune, each boss phase ended by a scripted hit; use only its KNOCKOUT screens
take knockout "" 90 "" win dev-win-wipe
echo "BATCH DONE $(date +%H:%M:%S)"
} > "$W/video/raw/batch.log" 2>&1
bash "$D/devloader.sh" off >/dev/null
