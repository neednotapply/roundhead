# Dev tools

Scripts used to test Roundhead on the real games and to film its Melty listing. They run in Git Bash on Windows (PowerShell for the two `.ps1` helpers) and assume you own Cuphead and ROUNDS on Steam.

Everything they produce goes to a work folder outside the repo, never into it.

| Setting | Default | What it is |
|---|---|---|
| `CUPROUNDS_WORK` | `~/cuprounds-work` | dev install, logs, screenshots, video takes, the BepInEx download |
| `CUPHEAD_DIR` | `/d/SteamLibrary/steamapps/common/Cuphead` | your Cuphead folder |
| `ROUNDS_DIR` | `/d/SteamLibrary/steamapps/common/ROUNDS` | your ROUNDS folder, on the `old-rounds-for-mods` branch |
| `PY` | `python` | Python with numpy, opencv-python, Pillow and imageio-ffmpeg, for the video tools |

All of them are read by [env.sh](env.sh). It also exports `CupheadDir` and `RoundsDir`, the folders the C# build reads, so the build and the launched games always use the same copies. The build and `tools/package.py` read `CUPROUNDS_WORK` too.

## Before the first run

1. Do steps 1 to 4 of "Building from source" in the [main README](../README.md): BepInEx in `$CUPROUNDS_WORK/dl/bepinex`, the five license texts in `$CUPROUNDS_WORK/dl`, the game folders, and the decompiled sources for the preflight.
2. Start Roundhead once the normal way so it sets up its ROUNDS profile in `%LOCALAPPDATA%\CupRounds`.
3. Run `devrun_dev.sh` once before the others. It builds the package and unpacks it into `$CUPROUNDS_WORK/devinstall`, which `devloader.sh` and `rounds_dev.sh` copy from.
4. Never run these while you play Roundhead yourself. They share the ROUNDS profile, and they close only the games they started.

## Testing

| Script | What it does |
|---|---|
| `devrun_dev.sh <scenario> <done check> [max shots] [cards]` | Packages the mod like Melty, starts a test run with the test bots and screenshots the Cuphead window. Scenarios: `""` (the plain Melty test), `retry`, `revive`, `statuses`, `win`. `ROUNDHEAD_DEV_SCENE` picks the level. |
| `levels_run.sh <level> ...` | The plain test run on a list of levels, with shots and logs per level. |
| `statuses_two.sh` | The boss status bench, on a clean boss and with Cold Bullets. |
| `rounds_dev.sh` | Starts only the hidden ROUNDS engine with a private data folder and arena link. It copies the last Release build of the engine into Roundhead's ROUNDS profile, which the launcher replaces again on its next normal start. |
| `cardprobe.py` | The card effect bench: plays Cuphead's side of the arena link against the dev engine. |
| `sweep.sh [N]` | Every card in `$CUPROUNDS_WORK/devdata/cardlist.txt` through the bench, N per fresh engine. Write that file first, one ROUNDS card name per line; `python -I cardprobe.py --list` prints the catalog. |
| `look_run.sh` | One engine run that logs how ROUNDS draws the player and gun, and dumps frames with `framedump.py`. |
| `meltywatch.sh [secs]` | Watches a Melty test run from outside. |
| `devloader.sh on/off` | Puts the mod loader back next to the copy Melty switched off, or takes it away. |
| `closeours.ps1`, `capture.ps1` | Close only our games; screenshot only the game window. |

The test mode itself lives in `src/CupheadMod/TestMode.cs` and reports its checks to the file Melty names in `%LOCALAPPDATA%\Melty\test\roundhead.json`.

## Filming

| Script | What it does |
|---|---|
| `video/video_run.sh <name> <level> <secs> [cards] [scenario] [done]` | Films one test run to `raw/<name>/run.mp4` at 1280x720, 30 fps. |
| `video/shoot_all.sh` | The batch behind the listing trailer: fourteen takes. |
| `video/sheets.py <take>` | Time-stamped 1 fps contact sheets for picking moments. |
| `video/frame.sh <take> <secs> <out.png>` | An exact full-resolution still. |
| `video/edit.py <edl.json>` | Cuts a trailer from the takes: crossfades, captions, title and end card. `edl_trailer.json` is the cut on the listing. |

`video/rec.py` records the window with PrintWindow from two overlapping threads. One thread only manages about 25 fps, and ffmpeg's own window grabber records Cuphead as solid black.
