#!/usr/bin/env bash
# Full card sweep with the card bench: chunks of N cards, a fresh dev engine for each chunk.
#   bash sweep.sh [N] [extra cardprobe args]
# Card list: $CUPROUNDS_WORK/devdata/cardlist.txt, one ROUNDS card name per line. Output: devdata/sweep (or SWEEP_OUT).
. "$(dirname "$0")/env.sh"
cd "$W"
N=${1:-10}; shift
OUT=${SWEEP_OUT:-devdata/sweep}; mkdir -p "$OUT"
mapfile -t CARDS < devdata/cardlist.txt
i=0; chunk=0
while [ $i -lt ${#CARDS[@]} ]; do
  part=("${CARDS[@]:$i:$N}")
  bash "$D/rounds_dev.sh" >/dev/null
  for k in $(seq 1 40); do [ -f devdata/engine.port ] && break; sleep 1; done
  timeout 1500 python -I "$D/cardprobe.py" "$OUT/chunk$chunk.json" "${part[@]}" "$@" > "$OUT/chunk$chunk.log" 2>&1
  echo "chunk $chunk done: $(grep -E '^SUMMARY' "$OUT/chunk$chunk.log")"
  i=$((i+N)); chunk=$((chunk+1))
done
echo SWEEP DONE
