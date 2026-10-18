#!/usr/bin/env bash
# Exact full-resolution still from a take: bash frame.sh <take> <seconds> <out.png>   (takes in $CUPROUNDS_WORK/video/raw)
. "$(dirname "$0")/../env.sh"
FF="$("$PY" -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())" 2>/dev/null || echo ffmpeg)"
"$FF" -hide_banner -loglevel error -y -ss "$2" -i "$W/video/raw/$1/run.mp4" -frames:v 1 "$3" && echo "$3"
