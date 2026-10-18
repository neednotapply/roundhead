"""Build all three projects and assemble the release zip (layout from sheets/components.json).

usage: python -I tools/package.py <version>
"""
import hashlib, os, shutil, subprocess, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.environ.get("CUPROUNDS_WORK") or os.path.join(os.path.expanduser("~"), "cuprounds-work")
BEPINEX = os.path.join(WORK, "dl", "bepinex")
DL = os.path.join(WORK, "dl")


def build(proj):
    r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", os.path.join(ROOT, "src", proj)], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-2000:])
        sys.exit("build failed: " + proj)


def copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "0.1.0"
    subprocess.run([sys.executable, "-I", os.path.join(ROOT, "tools", "sheets.py"), "gen"], check=True)
    if subprocess.run([sys.executable, "-I", os.path.join(ROOT, "tools", "sheets.py"), "preflight", "--code"]).returncode != 0:
        sys.exit("preflight not clean; fix the open items first")
    for p in ("CupheadMod", "CardEngine", "Launcher"):
        build(p)
    stage = os.path.join(ROOT, "dist", "stage")
    shutil.rmtree(stage, ignore_errors=True)
    m = os.path.join(stage, "managed")
    copy(os.path.join(ROOT, "src", "Launcher", "bin", "Release", "net48", "CupRoundsLauncher.exe"), os.path.join(m, "CupRoundsLauncher.exe"))
    cfg = os.path.join(ROOT, "src", "Launcher", "bin", "Release", "net48", "CupRoundsLauncher.exe.config")
    if os.path.exists(cfg):
        copy(cfg, os.path.join(m, "CupRoundsLauncher.exe.config"))
    for game in ("cuphead", "rounds"):
        core = os.path.join(BEPINEX, "BepInEx", "core")
        for f in os.listdir(core):
            if not f.endswith(".xml"):
                copy(os.path.join(core, f), os.path.join(m, game, "BepInEx", "core", f))
    copy(os.path.join(ROOT, "src", "CupheadMod", "bin", "Release", "net35", "CupRounds.Cuphead.dll"),
         os.path.join(m, "cuphead", "BepInEx", "plugins", "CupRounds", "CupRounds.Cuphead.dll"))
    copy(os.path.join(ROOT, "src", "CardEngine", "bin", "Release", "net472", "CupRounds.CardEngine.dll"),
         os.path.join(m, "rounds", "BepInEx", "plugins", "CupRounds.CardEngine", "CupRounds.CardEngine.dll"))
    copy(os.path.join(BEPINEX, "winhttp.dll"), os.path.join(stage, "proxy-cuphead", "version.dll"))
    copy(os.path.join(BEPINEX, "winhttp.dll"), os.path.join(stage, "proxy-rounds", "version.dll"))
    copy(os.path.join(DL, "BepInEx-LICENSE.txt"), os.path.join(m, "third-party-notices", "BepInEx-LICENSE.txt"))
    copy(os.path.join(DL, "UnityDoorstop-LICENSE.txt"), os.path.join(m, "third-party-notices", "UnityDoorstop-LICENSE.txt"))
    # BepInEx/core also carries HarmonyX, MonoMod and Mono.Cecil (all MIT): their own license texts go with them
    for extra in ("HarmonyX-LICENSE.txt", "MonoMod-LICENSE.txt", "Mono.Cecil-LICENSE.txt"):
        if os.path.exists(os.path.join(DL, extra)):
            copy(os.path.join(DL, extra), os.path.join(m, "third-party-notices", extra))
        else:
            print("warning: %s is missing from %s (see README, Building from source)" % (extra, DL))
    with open(os.path.join(m, "third-party-notices", "NOTICE.txt"), "w", encoding="utf-8") as fh:
        fh.write("BepInEx 5.4.23.2 - MIT - https://github.com/BepInEx/BepInEx\n"
                 "HarmonyX (0Harmony.dll, 0Harmony20.dll, HarmonyXInterop.dll) - MIT - https://github.com/BepInEx/HarmonyX\n"
                 "MonoMod (MonoMod.RuntimeDetour.dll, MonoMod.Utils.dll) - MIT - https://github.com/MonoMod/MonoMod\n"
                 "Mono.Cecil (Mono.Cecil*.dll) - MIT - https://github.com/jbevain/cecil\n"
                 "UnityDoorstop 4.3.0 - LGPL-2.1 - https://github.com/NeighTools/UnityDoorstop (version 4.3.0, shipped unmodified as version.dll)\n")
    copy(os.path.join(ROOT, "README.md"), os.path.join(m, "README.md"))
    copy(os.path.join(ROOT, "LICENSE.txt"), os.path.join(m, "LICENSE.txt"))
    out = os.path.join(ROOT, "dist", "Roundhead-%s.zip" % version)
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for d, _, fs in os.walk(stage):
            for f in sorted(fs):
                p = os.path.join(d, f)
                z.write(p, os.path.relpath(p, stage).replace(os.sep, "/"))
    subprocess.run([sys.executable, "-I", os.path.join(ROOT, "tools", "sheets.py"), "preflight", "--code", "--package", out], check=True)
    data = open(out, "rb").read()
    print("package:", out)
    print("size:", len(data))
    print("sha256:", hashlib.sha256(data).hexdigest())


if __name__ == "__main__":
    main()
