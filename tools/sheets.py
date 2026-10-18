"""CupRounds design sheets: preflight and code generation.

usage: python -I tools/sheets.py preflight [--code] [--package <zip>]
       python -I tools/sheets.py gen
The sheets in sheets/*.json are the source of truth. Preflight lists every
unfilled cell, every cross-sheet reference that does not resolve, every game
target that does not exist in the decompiled games and (with --code) every
symbol a row names that is missing from src/.
"""
import json, os, re, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEETS = os.path.join(ROOT, "sheets")
SRC = os.path.join(ROOT, "src")
GEN = os.path.join(SRC, "Shared", "Generated")
DECOMP = os.path.join(os.path.expanduser("~"), "cuprounds-decomp")


def load():
    out = {}
    for f in sorted(os.listdir(SHEETS)):
        if f.endswith(".json"):
            with open(os.path.join(SHEETS, f), encoding="utf-8") as fh:
                d = json.load(fh)
            out[d["sheet"]] = d
    return out


def ids(s, name):
    return {r["id"] for r in s[name]["rows"]}


def split(v):
    return [x.strip() for x in str(v).split(",") if x.strip() and x.strip() != "none"]


_src_cache = None


def src_files():
    global _src_cache
    if _src_cache is None:
        _src_cache = {}
        for d, _, fs in os.walk(SRC):
            for f in fs:
                if f.endswith(".cs"):
                    p = os.path.join(d, f)
                    with open(p, encoding="utf-8") as fh:
                        _src_cache[p] = fh.read()
    return _src_cache


def symbol_exists(sym):
    cls, meth = sym.split(".", 1)
    for p, text in src_files().items():
        m = re.search(r"\b(class|struct)\s+%s\b" % re.escape(cls), text)
        if not m:
            continue
        if re.search(r"\b%s\s*\(" % re.escape(meth), text[m.start():]):
            return True
    return False


def game_target_exists(game, target):
    parts = target.split(".")  # Class.Method, or Class.Nested.Method (the nested class lives in the outer class's file)
    cls, meth = parts[0], parts[-1]
    p = os.path.join(DECOMP, game, cls + ".cs")
    if not os.path.exists(p):
        return False
    with open(p, encoding="utf-8") as fh:
        return re.search(r"\b%s\s*\(" % re.escape(meth), fh.read()) is not None


def rounds_field_exists(component, field):
    p = os.path.join(DECOMP, "rounds", component + ".cs")
    if not os.path.exists(p):
        return False
    with open(p, encoding="utf-8") as fh:
        return re.search(r"public\s+[\w\.\[\]<>]+\s+%s\b" % re.escape(field), fh.read()) is not None


def preflight(check_code, package):
    s = load()
    problems = []
    # 1. every cell filled
    for name, sh in s.items():
        cols = list(sh["columns"].keys())
        for i, row in enumerate(sh["rows"]):
            for c in cols:
                v = row.get(c)
                if v is None or (isinstance(v, str) and v.strip() == ""):
                    problems.append("%s[%s].%s is empty" % (name, row.get("id", i), c))
            for c in row:
                if c not in cols:
                    problems.append("%s[%s] has unknown column %s" % (name, row.get("id", i), c))
        seen = set()
        for row in sh["rows"]:
            if row.get("id") in seen:
                problems.append("%s has duplicate id %s" % (name, row["id"]))
            seen.add(row.get("id"))
    # 2. cross-sheet references
    eff, tun, ipc = ids(s, "effects"), ids(s, "tuning"), ids(s, "ipc")
    for r in s["stats"]["rows"]:
        if r["effect"] not in eff:
            problems.append("stats[%s].effect -> %s does not resolve" % (r["id"], r["effect"]))
        if not rounds_field_exists(r["component"], r["field"]):
            problems.append("stats[%s]: ROUNDS %s.%s not found" % (r["id"], r["component"], r["field"]))
    used = {r["effect"] for r in s["stats"]["rows"]}
    for r in s["effects"]["rows"]:
        for t in split(r["tuning"]):
            if t not in tun:
                problems.append("effects[%s].tuning -> %s does not resolve" % (r["id"], t))
        if r["id"] not in used:
            problems.append("effects[%s] is not driven by any stat" % r["id"])
    for r in s["hooks"]["rows"]:
        for e in split(r["effects"]):
            if e not in eff:
                problems.append("hooks[%s].effects -> %s does not resolve" % (r["id"], e))
        target = r["target"]
        if "(runtime patch)" in target:
            continue  # found by reflection at runtime (Rewired has no decompiled source)
        game = "cuphead"
        if target.startswith("ROUNDS "):
            game, target = "rounds", target[len("ROUNDS "):]
        if not game_target_exists(game, target):
            problems.append("hooks[%s]: %s %s not found" % (r["id"], game, target))
    for r in s["content"]["rows"]:
        if r["ipc"] != "none" and r["ipc"] not in ipc:
            problems.append("content[%s].ipc -> %s does not resolve" % (r["id"], r["ipc"]))
    # 3. code symbols
    if check_code:
        checks = []
        for r in s["effects"]["rows"]:
            checks.append(("effects", r["id"], r["symbol"]))
        for r in s["hooks"]["rows"]:
            checks.append(("hooks", r["id"], r["symbol"]))
        for r in s["controls"]["rows"]:
            checks.append(("controls", r["id"], r["symbol"]))
        for r in s["ipc"]["rows"]:
            checks.append(("ipc", r["id"], r["server"]))
            checks.append(("ipc", r["id"], r["client"]))
        for r in s["progression"]["rows"]:
            checks.append(("progression", r["id"], r["symbol"]))
        for r in s["launcher"]["rows"]:
            checks.append(("launcher", r["id"], r["symbol"]))
        for sheet, rid, sym in checks:
            if sym.startswith("dev:"):
                # developer tools live in devtools/ (in the repository, not in the shipped package)
                if not os.path.exists(os.path.join(ROOT, "devtools", sym[4:])):
                    problems.append("%s[%s]: dev tool %s missing" % (sheet, rid, sym[4:]))
                continue
            if not symbol_exists(sym):
                problems.append("%s[%s]: %s not implemented in src/" % (sheet, rid, sym))
        gen_build = os.path.join(GEN, "BuildStats.g.cs")
        if os.path.exists(gen_build):
            text = open(gen_build, encoding="utf-8").read()
            for r in s["stats"]["rows"]:
                if not re.search(r"public \w+ %s\b" % r["id"], text):
                    problems.append("stats[%s] missing from generated BuildStats" % r["id"])
        else:
            problems.append("generated code missing: run gen")
    # 4. package contents
    if package:
        with zipfile.ZipFile(package) as z:
            names = z.namelist()
        for r in s["components"]["rows"]:
            zp = r["zipPath"]
            if zp.endswith("/"):
                if not any(n.startswith(zp) and not n.endswith("/") for n in names):
                    problems.append("components[%s]: %s empty in package" % (r["id"], zp))
            elif zp not in names:
                problems.append("components[%s]: %s missing from package" % (r["id"], zp))
    total = sum(len(sh["rows"]) * len(sh["columns"]) for sh in s.values())
    print("preflight: %d sheets, %d rows, %d cells" % (len(s), sum(len(sh["rows"]) for sh in s.values()), total))
    for p in problems:
        print("  OPEN " + p)
    print("clean" if not problems else "%d open items" % len(problems))
    return 0 if not problems else 1


CS_TYPE = {"float": "float", "int": "int", "bool": "bool", "color": "string"}


def lit(t, v):
    if t == "float":
        return repr(float(v)) + "f"
    if t == "int":
        return str(int(v))
    if t == "bool":
        return "true" if v else "false"
    return '"%s"' % v


def gen():
    s = load()
    os.makedirs(GEN, exist_ok=True)
    hdr = "// <auto-generated> from sheets/*.json by tools/sheets.py gen. Edit the sheet, not this file.\n"
    # BuildStats (shared by both plugins)
    lines = [hdr, "using System;", "", "namespace CupRounds", "{", "    [Serializable]", "    public class BuildStats", "    {"]
    for r in s["stats"]["rows"]:
        lines.append("        /// <summary>%s.%s (%s) -> %s</summary>" % (r["component"], r["field"], r["op"], r["effect"]))
        lines.append("        public %s %s = %s;" % (CS_TYPE[r["type"]], r["id"], lit(r["type"], r["base"])))
    lines += ["    }", "}", ""]
    write(os.path.join(GEN, "BuildStats.g.cs"), "\n".join(lines))
    # StatFold (ROUNDS card engine only): one statement per row
    comp_var = {"Gun": "g", "CharacterStatModifiers": "c", "Block": "k"}
    body = []
    for r in s["stats"]["rows"]:
        v = comp_var[r["component"]]
        src = "%s.%s" % (v, r["field"])
        dst = "b.%s" % r["id"]
        op = r["op"]
        if op == "mult":
            code = "%s *= %s;" % (dst, src)
        elif op == "add":
            code = "%s += %s;" % (dst, src)
        elif op == "add_clamp_1_90":
            code = "%s = UnityEngine.Mathf.Clamp(%s + %s, 1, 90);" % (dst, dst, src)
        elif op == "proj_weighted_mult":
            code = "%s *= 1f - num * (1f - %s);" % (dst, src)
            if r["id"] == "damage":
                code += " if (b.damage < 0.25f) b.damage = 0.25f;"
        elif op == "or_true":
            code = "if (%s) %s = true;" % (src, dst)
        elif op == "and_false":
            code = "if (!%s) %s = false;" % (src, dst)
        elif op == "set_nonzero":
            code = "if (%s != 0f) %s = %s;" % (src, dst, src)
        elif op == "color_mix":
            code = "%s = MixColor(%s, %s);" % (dst, dst, src)
        elif op == "lock_attack":
            code = "if (g.lockGunToDefault) %s = %s;" % (dst, src)
        else:
            raise SystemExit("unknown op " + op)
        guard = {"g": "g != null", "c": "c != null", "k": "k != null"}[v]
        body.append("            if (%s) { %s } // %s" % (guard, code, r["id"]))
    lines = [hdr, "#if CARDENGINE", "using UnityEngine;", "", "namespace CupRounds", "{", "    public static partial class StatFold", "    {",
             "        /// <summary>Folds one card's components into the build, following ROUNDS ApplyCardStats.ApplyStats / CopyGunStats.</summary>",
             "        public static void Fold(BuildStats b, Gun g, CharacterStatModifiers c, Block k)", "        {",
             "            float num = 1f;",
             "            if (g != null && g.numberOfProjectiles != 0 && b.numberOfProjectiles != 1) num = (float)g.numberOfProjectiles / ((float)g.numberOfProjectiles + (float)b.numberOfProjectiles);"]
    lines += body
    lines += ["        }", "    }", "}", "#endif", ""]
    write(os.path.join(GEN, "StatFold.g.cs"), "\n".join(lines))
    # Tuning, progression, controls, ops
    lines = [hdr, "namespace CupRounds", "{", "    public static class Tuning", "    {"]
    for r in s["tuning"]["rows"]:
        lines.append("        /// <summary>%s (%s): %s</summary>" % (r["id"], r["unit"], r["why"]))
        lines.append("        public const float %s = %sf;" % (r["id"], repr(float(r["value"]))))
    lines += ["    }", "", "    public static class Rules", "    {"]
    for r in s["progression"]["rows"]:
        n = "".join(p.capitalize() for p in r["id"].split("_"))
        lines.append("        /// <summary>%s</summary>" % r["when"])
        lines.append("        public const int %sPicks = %d;" % (n, r["picks"]))
        lines.append("        public const int %sOffer = %d;" % (n, r["offerSize"]))
        lines.append("        public const int %sCost = %d;" % (n, r["cost"]))
    lines += ["    }", "", "    public static class Buttons", "    {"]
    for r in s["controls"]["rows"]:
        n = "".join(p.capitalize() for p in r["id"].split("_"))
        lines.append("        /// <summary>%s</summary>" % r["notes"])
        lines.append("        public const int %s = %d;" % (n, r["cupheadButton"]))
    lines += ["    }", "", "    public static class Ops", "    {"]
    for r in s["ipc"]["rows"]:
        lines.append("        public const string %s = \"%s\";" % (r["id"].capitalize(), r["id"]))
    lines += ["    }", "}", ""]
    write(os.path.join(GEN, "Constants.g.cs"), "\n".join(lines))
    print("generated into " + GEN)


def write(p, text):
    with open(p, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a:
        print(__doc__)
        sys.exit(2)
    if a[0] == "gen":
        gen()
    elif a[0] == "preflight":
        pkg = a[a.index("--package") + 1] if "--package" in a else None
        sys.exit(preflight("--code" in a, pkg))
    else:
        print(__doc__)
        sys.exit(2)
