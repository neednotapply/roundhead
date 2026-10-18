#!/usr/bin/env bash
# Dev only: put our mod loader (version.dll) next to Melty's switched-off copy for local tests ("on"), and take it away
# again ("off"). Melty renames the loader to version.dll.melty-off whenever it starts, so run "on" right before a dev run
# (devrun_dev.sh and video/video_run.sh do it themselves) and "off" when testing is done.
. "$(dirname "$0")/env.sh"
for g in cuphead rounds; do
  if [ $g = cuphead ]; then d="$CUPHEAD_DIR"; else d="$ROUNDS_DIR"; fi
  src="$W/devinstall/proxy-$g/version.dll"
  if [ "$1" = on ]; then [ -f "$d/version.dll" ] || cp "$src" "$d/version.dll"
  else [ -f "$d/version.dll" ] && [ -f "$d/version.dll.melty-off" ] && cmp -s "$d/version.dll" "$d/version.dll.melty-off" && rm -f "$d/version.dll"; fi
done
ls "$ROUNDS_DIR" | grep -i "^version"
