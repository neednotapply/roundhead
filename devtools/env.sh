# Shared settings for the dev scripts (sourced, not run). Override any of them in the environment.
#   CUPROUNDS_WORK  work folder outside the repo: dev install, logs, screenshots, video takes (default ~/cuprounds-work)
#   CUPHEAD_DIR     your Steam Cuphead folder
#   ROUNDS_DIR      your Steam ROUNDS folder (the "old-rounds-for-mods" branch)
#   PY              Python with numpy and opencv for the video tools (default: python)
# The C# projects take the same folders as MSBuild properties: dotnet build -p:CupheadDir=... -p:RoundsDir=...
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$D/.." && pwd)"
W="${CUPROUNDS_WORK:-$USERPROFILE/cuprounds-work}"
W="$(cygpath -u "$W" 2>/dev/null || echo "$W")"
CUPHEAD_DIR="${CUPHEAD_DIR:-/d/SteamLibrary/steamapps/common/Cuphead}"
ROUNDS_DIR="${ROUNDS_DIR:-/d/SteamLibrary/steamapps/common/ROUNDS}"
# plain python for the video tools: "python -I" hides user-site packages such as numpy and opencv
PY="${PY:-python}"
# the C# build (tools/package.py) reads the game folders as MSBuild properties from the environment
export CupheadDir="${CupheadDir:-$(cygpath -w "$CUPHEAD_DIR")}"
export RoundsDir="${RoundsDir:-$(cygpath -w "$ROUNDS_DIR")}"
mkdir -p "$W"
export CUPROUNDS_WORK="$W"
