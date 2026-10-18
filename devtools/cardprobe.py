"""Card effect bench (dev tool, v2): plays Cuphead's side of the arena link against the private dev ROUNDS engine
(rounds_dev.sh) and checks that every ROUNDS card's effect on opponents also lands on Cuphead enemies.

For each card, combo ("A+B") or design COMBO it resets ROUNDS player 1 (arena_cards [] then the run's cards, and a new
EvSpawn life; a fresh player per run is not available yet), drives a small script (aim, click fire, block at 1.0 s and
5.5 s because the block cooldown is 4 s + cdAdd, optional moves, hurt-first, blocked hits, cursor, weapon switch, ...)
next to mirrored enemies, and records every frame:
  - the hit ring: hit kinds and colours, damage per kind and per enemy group,
  - the effect ring (R_FxWrite / R_Fx, 256 entries): events by type with their max values,
  - the status table (R_StatusFrame / R_StatusCount / R_Status): max slow, stun, silence, dot, aux and flags per group,
  - the player (hp, max hp, blocks, position) and, every 0.5 s, the engine's dev op arena_probe (lifesteal, rewards...),
  - exceptions that ROUNDS and the engine logged meanwhile.
It then checks the run against the expectations table (design MATRIX / COMBOS) and prints PASS / FAIL per card with the
reason, warnings when the bench itself looks broken, and a summary.

Layout (Cuphead units, +y up, floor top at y=20): the player at x=180; A (id 5, group 500, boss) and A2 (id 6, group 500,
overlapping it: a two-collider boss) to the right; Bm (id 7, group 700, light minion) just behind the player; an optional
wall W between the player and A; with --coop the partner (slot 1) stands between the player and A, in the line of fire.

usage: python cardprobe.py out.json [cards or "A+B" combos ...] [--combos] [--coop] [--quick] [--stop-on-broken]
       python cardprobe.py --list        (print the expectations table; needs no engine)
No cards and no --combos = every card in the catalog. PROBE_FX_OFF=0 runs without the effect ring and status table.
"""
import argparse, json, math, mmap, os, socket, struct, sys, time

DEV = os.environ.get('PROBE_DEV_DIR', os.path.join(os.environ.get('CUPROUNDS_WORK', os.path.join(os.path.expanduser('~'), 'cuprounds-work')), 'devdata'))   # engine.port of the dev engine (rounds_dev.sh)
MAP = os.environ.get('PROBE_MAP', "Local\\CupRoundsArenaDev")                     # its private arena link (ROUNDHEAD_DEV_ARENA)
MAXW, MAXH = 1280, 720
SIZE = 131072 + 6 * MAXW * MAXH * 4   # three world + three UI frame buffers
R = 65536
ROUNDS_LOG = os.path.join(os.environ.get('USERPROFILE', ''), r'AppData\LocalLow\Landfall Games\Rounds\output_log.txt')
ENGINE_LOG = os.path.join(os.environ.get('LOCALAPPDATA', ''), r'CupRounds\rounds-profile\BepInEx\LogOutput.log')

# ---------------------------------------------------------------- link layout (src/Shared/ArenaLink.cs, sheets/ipc.json)
C_MAGIC, C_BEAT, C_PID, C_MODE, C_CAMX, C_CAMY, C_ORTHO, C_ASPECT = 0, 4, 8, 12, 16, 20, 24, 28
C_FRAMEW, C_FRAMEH, C_SCALE, C_LEVEL, C_COLCOUNT, C_EVW, C_PAUSED = 32, 36, 40, 48, 52, 56, 60
C_EVENTS, EVENT_SIZE, EVENT_RING = 192, 32, 64
C_COLS, COL_SIZE, K_GROUP, K_FLAGS = 4096, 160, 144, 148
TF_LIGHT, TF_BOSS, TF_NOSTATUS = 1, 2, 4
S_PRESENT, S_MOVEX, S_MOVEY, S_AIMX, S_AIMY, S_JUMPHELD, S_JUMPS, S_SHOOTHELD, S_BLOCKS = 0, 4, 8, 12, 16, 20, 24, 28, 32
S_SHOOTS = 56
U_CURX, U_CURY, U_CURON, U_DOWN, U_SWITCH = 0, 4, 8, 12, 16
EV_DAMAGE, EV_SPAWN, EV_BLOCKED = 1, 7, 8
R_HITWRITE, R_HITS, HIT_SIZE, HIT_RING = R + 44, R + 1024, 32, 128
Q_X, Q_Y, Q_HP, Q_MAXHP, Q_DEAD, Q_BLOCKS, Q_CARDS, Q_LIFE, Q_PRESENT = 4, 8, 20, 24, 28, 40, 56, 60, 0
FX_OFF = int(os.environ.get('PROBE_FX_OFF', '5120'))   # R_FxWrite; 0 = an engine without the effect ring
FX_SIZE, FX_RING = 32, 256
R_STATUSFRAME, R_STATUSCOUNT, R_STATUS, STATUS_SIZE, MAX_STATUS = R + 13320, R + 13324, R + 13328, 32, 256
HEALTH_PER_HEART = 34.0   # Tuning.healthPerHeart: what EvBlockedHit says the blocked Cuphead hit would have dealt

KIND = {0: 'Bullet', 1: 'Dot', 2: 'Area', 3: 'Continuous', 4: 'Contact', 5: 'Aura'}
KIND_ID = {v: k for k, v in KIND.items()}
FXN = {1: 'Slow', 2: 'Stun', 3: 'Silence', 4: 'Dot', 5: 'Force', 6: 'Pull', 7: 'Gravity', 8: 'Gold', 9: 'Confuse', 10: 'Murder', 11: 'Shake'}
FX_ID = {v: k for k, v in FXN.items()}
ST_GOLD, ST_CONFUSED, ST_HEAVY, ST_FLIPPED = 1, 2, 4, 8
D = 55.0   # raw ROUNDS damage of a fresh gun's bullet

def slot(s): return 64 + s * 64
def slot2(s): return 2560 + s * 32
def rslot(s): return R + 64 + s * 64

# ---------------------------------------------------------------- the bench's level
FLOOR = (1, 1, [0, 0, 3000, 40, 0])            # ground, top at y=20
WALLS = [(3, 0, [-900, 300, 40, 1200, 0]), (4, 0, [900, 300, 40, 1200, 0])]
PLAYER = (180.0, 60.0)
PARTNER = (240.0, 60.0)
A_BOX = (300.0, 160.0, 110.0, 200.0)           # centre x, y, w, h
A2_BOX = (300.0, 190.0, 80.0, 120.0)
BM_BOX = (100.0, 50.0, 60.0, 60.0)
WALL_W = (240.0, 120.0, 20.0, 200.0)
GROUP_A, GROUP_BM = 500, 700

# ---------------------------------------------------------------- scenarios

DEFAULT = dict(
    seconds=7.5,          # driven part of the run
    settle=2.0,           # then input off, still recording (damage over time, clouds)
    shoot=True, shoot_from=0.0, shoot_until=None,
    aim='A',              # 'A', 'Bm', 'alt' (A and Bm in turn), or a point [x, y]
    aim_offset=0.0,       # degrees, counter-clockwise from the aim point
    blocks=[1.0, 5.5],    # block cooldown 4 s + cdAdd (Block.cs:82-85)
    move=None,            # None, 'toward' (A), 'chase' (toward then away), or [[t0, t1, mx, my], ...]
    spawn=None,           # Cuphead spot; default PLAYER
    hurt=None, hurt_amount=50.0,   # times of an EvDamage (hurt first, for lifesteal checks)
    blocked_hit=None,     # times of an EvBlockedHit (inside the block window)
    cursor=None,          # [x, y]: U_CursorOn with the Cuphead cursor there (Remote)
    switch=None,          # times of a weapon-switch press (PCE Laser)
    down_hold=None,       # [t0, t1] with U_DownHeld (PCE Super Jump)
    jump=None,            # times of a jump press
    new_level=None,       # time of a new level serial (Murder)
    wall=False, bm=True, a2=True, a_y=None, nostatus=False,
    probe_every=0.5,
    roams=False,          # the script moves the player (broken-run check allows it)
)

def S(label='', checks=(), **kw):
    """One scenario: script overrides on DEFAULT plus its checks."""
    for k in kw:
        if k not in DEFAULT: raise KeyError('unknown scenario key ' + k)
    return dict(label=label, checks=list(checks), script=kw)

def script_of(sc):
    s = dict(DEFAULT); s.update(sc.get('script', {})); return s

def adjust(s, cards):
    """Cards that need a particular script whatever the scenario: Abyssal Countdown wants 7 s of standing still (timeToFill 7,
    AbyssalCountdown.cs:163-170), Chase wants move input toward an enemy (Chase.cs:28-33)."""
    names = set(canon(c) for c in cards)
    if 'abyssal countdown' in names and s['move'] is None: s['seconds'] = max(s['seconds'], 14.0)
    if 'chase' in names and s['move'] is None: s['move'] = 'toward'; s['roams'] = True
    return s

def script_key(s):
    return json.dumps({k: s[k] for k in sorted(s)}, sort_keys=True)

# ---------------------------------------------------------------- checks

class Check:
    def __init__(self, desc, fn): self.desc, self.fn = desc, fn

def _rng(v, r):
    return r is None or (r[0] <= v <= r[1])

def _g(group): return '' if group is None else '@%d' % group

def _sel_hits(r, kind=None, group=None, dmin=None, dmax=None, t0=None, t1=None, ids=None):
    out = []
    for h in r['_hits']:
        if kind is not None and h['kind'] != KIND_ID[kind]: continue
        if group is not None and h['g'] != group: continue
        if ids is not None and h['id'] not in ids: continue
        if dmin is not None and h['dmg'] < dmin: continue
        if dmax is not None and h['dmg'] > dmax: continue
        if t0 is not None and h['t'] < t0: continue
        if t1 is not None and h['t'] > t1: continue
        out.append(h)
    return out

def _sel_fx(r, typ, group=None, a=None, b=None, c=None, d=None, mag=None):
    out = []
    for e in r['_fx']:
        if e['type'] != FX_ID[typ]: continue
        if group is not None and e['g'] != group: continue
        if not (_rng(e['a'], a) and _rng(e['b'], b) and _rng(e['c'], c) and _rng(e['d'], d)): continue
        if mag is not None and not _rng(math.hypot(e['a'], e['b']), mag): continue
        out.append(e)
    return out

def _fmt(r): return '' if r is None else '[%g..%g]' % r

def hits(n_min=1, n_max=None, kind=None, group=None, dmin=None, dmax=None, t0=None, t1=None):
    desc = 'hits[%s%s]%s %s' % (kind or 'any', _g(group), (' of dmg %s..%s' % ('' if dmin is None else dmin, '' if dmax is None else dmax)) if dmin is not None or dmax is not None else '',
                               ('>= %d' % n_min) + ('' if n_max is None else ' and <= %d' % n_max))
    def fn(r, ctx):
        hs = _sel_hits(r, kind, group, dmin, dmax, t0, t1)
        return len(hs) >= n_min and (n_max is None or len(hs) <= n_max), '%d hits, %.1f dmg' % (len(hs), sum(h['dmg'] for h in hs))
    return Check(desc, fn)

def dmg(lo, hi=None, kind=None, group=None):
    desc = 'dmg[%s%s] >= %g%s' % (kind or 'any', _g(group), lo, '' if hi is None else ' and <= %g' % hi)
    def fn(r, ctx):
        v = sum(h['dmg'] for h in _sel_hits(r, kind, group))
        return v >= lo and (hi is None or v <= hi), '%.1f' % v
    return Check(desc, fn)

def per_hit(kind, lo, hi, group=None, how='median', t0=None, t1=None):
    desc = '%s damage per hits[%s%s] in %g..%g' % (how, kind, _g(group), lo, hi)
    def fn(r, ctx):
        v = sorted(h['dmg'] for h in _sel_hits(r, kind, group, t0=t0, t1=t1))
        if not v: return False, 'no such hits'
        x = {'median': v[len(v) // 2], 'max': v[-1], 'min': v[0]}[how]
        return lo <= x <= hi, '%.2f (of %d hits)' % (x, len(v))
    return Check(desc, fn)

def first_hit(kind, lo, rest_hi=None):
    desc = 'first hits[%s] >= %g%s' % (kind, lo, '' if rest_hi is None else ', median of the rest <= %g' % rest_hi)
    def fn(r, ctx):
        v = [h['dmg'] for h in _sel_hits(r, kind)]
        if not v: return False, 'no such hits'
        rest = sorted(v[1:])
        ok = v[0] >= lo and (rest_hi is None or not rest or rest[len(rest) // 2] <= rest_hi)
        return ok, 'first %.1f, rest median %s' % (v[0], '%.1f' % rest[len(rest) // 2] if rest else '-')
    return Check(desc, fn)

def fx(typ, n_min=1, group=None, a=None, b=None, c=None, d=None, mag=None):
    parts = [p for p in (('A' + _fmt(a)) if a else '', ('B' + _fmt(b)) if b else '', ('C' + _fmt(c)) if c else '',
                         ('D' + _fmt(d)) if d else '', ('|AB|' + _fmt(mag)) if mag else '') if p]
    desc = 'fx[%s%s]%s x >= %d' % (typ, _g(group), (' ' + ' '.join(parts)) if parts else '', n_min)
    def fn(r, ctx):
        if not r['fxOn']: return False, 'effect ring off (PROBE_FX_OFF=0)'
        es = _sel_fx(r, typ, group, a, b, c, d, mag)
        alls = _sel_fx(r, typ, group)
        return len(es) >= n_min, '%d matching of %d%s' % (len(es), len(alls), (' (A max %.2f, B max %.2f)' % (max(e['a'] for e in alls), max(e['b'] for e in alls))) if alls else '')
    return Check(desc, fn)

def fx_none(typ, group=None):
    def fn(r, ctx):
        n = len(_sel_fx(r, typ, group))
        return n == 0, '%d events' % n
    return Check('no fx[%s%s]' % (typ, _g(group)), fn)

def fx_total_max(n):
    def fn(r, ctx): return len(r['_fx']) <= n, '%d events' % len(r['_fx'])
    return Check('fx events <= %d' % n, fn)

def fx_sign(typ, group, neg=True, frac=0.6):
    """Most events of this type push against the shot (+x): Tractor Beam pulls (A < 0)."""
    def fn(r, ctx):
        es = _sel_fx(r, typ, group)
        if not es: return False, 'no events'
        k = sum(1 for e in es if (e['a'] < 0) == neg)
        return k >= frac * len(es), '%d of %d have A %s 0' % (k, len(es), '<' if neg else '>=')
    return Check('fx[%s%s] mostly A %s 0' % (typ, _g(group), '<' if neg else '>'), fn)

def fx_streak(typ, group, lo=None, hi=None, d=None, gap=0.25):
    """Longest run of events (gaps <= gap s): a continuous source such as Thruster."""
    def fn(r, ctx):
        ts = sorted(e['t'] for e in _sel_fx(r, typ, group, d=d))
        best, start, last = 0.0, None, None
        for t in ts:
            if last is None or t - last > gap: start = t
            last = t
            best = max(best, t - start)
        return (lo is None or best >= lo) and (hi is None or best <= hi), 'longest %.2f s over %d events' % (best, len(ts))
    return Check('fx[%s%s]%s continuous for %s..%s s' % (typ, _g(group), '' if d is None else ' D' + _fmt(d), lo, hi), fn)

def force_vs_control(ratio):
    def fn(r, ctx):
        mine = max([math.hypot(e['a'], e['b']) for e in _sel_fx(r, 'Force')] or [0.0])
        c = ctx.control(r)
        base = max([math.hypot(e['a'], e['b']) for e in _sel_fx(c, 'Force')] or [0.0])
        if base <= 0: return mine > 0, 'max |dv| %.0f (control has none)' % mine
        return mine >= ratio * base, 'max |dv| %.0f vs control %.0f' % (mine, base)
    return Check('max fx[Force] >= %gx the control' % ratio, fn)

def pull_near_player(dist=150.0):
    def fn(r, ctx):
        es = _sel_fx(r, 'Pull')
        if not es: return False, 'no pull'
        px, py = r['player']['start']
        best = min(math.hypot(e['a'] - px, e['b'] - py) for e in es)
        return best <= dist, 'closest pull point %.0f from the player' % best
    return Check('fx[Pull] point within %g of the player' % dist, fn)

def st(field, lo=None, hi=None, group=GROUP_A):
    desc = 'st.%s%s max %s' % (field, _g(group), ('in %g..%g' % (lo, hi)) if lo is not None and hi is not None else ('>= %g' % lo) if lo is not None else '<= %g' % hi)
    def fn(r, ctx):
        if not r['fxOn']: return False, 'status table off'
        if r['statusFrames'] == 0: return False, 'the status table never updated'
        gs = [group] if group is not None else list(r['_status'].keys())
        v = max([r['_status'].get(g, {}).get(field, 0.0) for g in gs] or [0.0])
        return (lo is None or v >= lo) and (hi is None or v <= hi), '%.3f' % v
    return Check(desc, fn)

def st_all_max(field, hi):
    def fn(r, ctx):
        v = max([s.get(field, 0.0) for s in r['_status'].values()] or [0.0])
        return v <= hi, '%.3f' % v
    return Check('st.%s <= %g on every group' % (field, hi), fn)

def flag(bit, name, group=GROUP_A):
    def fn(r, ctx):
        gs = [group] if group is not None else list(r['_status'].keys())
        f = 0
        for g in gs: f |= r['_status'].get(g, {}).get('flags', 0)
        return (f & bit) != 0, 'flags seen 0x%x' % f
    return Check('st.flags%s has %s' % (_g(group), name), fn)

def _chan(v): return [(v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255]

def dot_rgba(expected, group=GROUP_A, tol=8):
    def fn(r, ctx):
        seen = r['_status'].get(group, {}).get('dotRgba', [])
        ok = any(all(abs(a - b) <= tol for a, b in zip(_chan(v), _chan(expected))) for v in seen)
        return ok, 'seen ' + (', '.join('0x%08X' % (v & 0xFFFFFFFF) for v in seen) or 'none')
    return Check('st.dotRgba%s ~ 0x%08X' % (_g(group), expected), fn)

def hit_rgba(kind, expected=None, green=False, tol=8):
    def fn(r, ctx):
        cs = sorted(set(h['rgba'] for h in _sel_hits(r, kind) if h['rgba']))
        if green: ok = any(_chan(v)[1] > _chan(v)[0] and _chan(v)[1] > _chan(v)[2] for v in cs)
        else: ok = any(all(abs(a - b) <= tol for a, b in zip(_chan(v), _chan(expected))) for v in cs)
        return ok, 'H_Color ' + (', '.join('0x%08X' % (v & 0xFFFFFFFF) for v in cs[:4]) or 'none')
    return Check('H_Color on hits[%s] %s' % (kind, 'green' if green else '~ 0x%08X' % expected), fn)

def decay(group, lo, hi, field='slow', typ='Slow'):
    """Seconds from the last fx event of this type to the status reading 0 (ROUNDS's linear decay)."""
    def fn(r, ctx):
        es = _sel_fx(r, typ, group)
        if not es: return False, 'no fx'
        tl = max(e['t'] for e in es)
        idx = ['slow', 'stun', 'silence', 'dot'].index(field)
        z = next((t for t, g, *v in r['_series'] if g == group and t > tl and v[idx] <= 1e-4), None)
        if z is None: return False, 'still > 0 at the end (last event at %.2f s)' % tl
        return lo <= z - tl <= hi, '%.2f s' % (z - tl)
    return Check('st.%s%s decays to 0 in %g..%g s after the last fx' % (field, _g(group), lo, hi), fn)

def dot_per_bullet(expected, tol=0.15):
    """Poison: dmg[Dot] = expected x (bullet hits)."""
    def fn(r, ctx):
        b = len(_sel_hits(r, 'Bullet'))
        dd = sum(h['dmg'] for h in _sel_hits(r, 'Dot'))
        if b == 0: return False, 'no bullet hits'
        return abs(dd / b - expected) <= tol * expected, '%.1f Dot damage per bullet (%d bullets)' % (dd / b, b)
    return Check('dmg[Dot] ~ %g x bullets (+-%d%%)' % (expected, tol * 100), fn)

def no_dup_hits(kind, group=GROUP_A, ids=(5, 6)):
    """One hit source hits a multi-collider enemy once: never hits of this kind on both of its colliders in the same frame."""
    def fn(r, ctx):
        by = {}
        for h in _sel_hits(r, kind, group):
            by.setdefault(h['tick'], set()).add(h['id'])
        dup = sum(1 for s in by.values() if all(i in s for i in ids))
        return dup == 0, '%d frames with both colliders hit' % dup
    return Check('one %s hit per source on group %d (A and A2 never both)' % (kind, group), fn)

def no_dup_area(group=GROUP_A, ids=(5, 6)): return no_dup_hits('Area', group, ids)

def rate_max(kind, group, per_s):
    def fn(r, ctx):
        ts = sorted(h['t'] for h in _sel_hits(r, kind, group))
        best, j = 0, 0
        for i, t in enumerate(ts):
            while ts[j] < t - 1.0: j += 1
            best = max(best, i - j + 1)
        return best <= per_s, 'max %d in 1 s' % best
    return Check('hits[%s%s] <= %d per second' % (kind, _g(group), per_s), fn)

def vs_control(metric_name, metric, ratio=None, extra=0, le=False):
    """Compare with the same scenario run without cards."""
    def fn(r, ctx):
        c = ctx.control(r)
        mine, base = metric(r), metric(c)
        ok = mine <= base * ratio + extra if le else mine >= base * ratio + extra
        return ok, '%.1f vs control %.1f' % (mine, base)
    op = '<=' if le else '>='
    return Check('%s %s %gx control%s' % (metric_name, op, ratio, ' + %g' % extra if extra else ''), fn)

def n_bullet(r): return len(_sel_hits(r, 'Bullet'))
def n_any(r): return len(r['_hits'])
def peak_y(r): return r['player']['peakY']

def hp_rose(amount):
    def fn(r, ctx):
        p = r['player']
        return p['hpRise'] >= amount, 'rose %.1f (after hurt %.1f, max %.1f, of %.1f)' % (p['hpRise'], p['hpAfterHurt'], p['hpMaxAfter'], p['maxHp0Card'])
    return Check('hp rises >= %g after the hurt' % amount, fn)

def hp_steady(tol=1.0):
    def fn(r, ctx):
        p = r['player']
        return p['hpMin'] >= p['hp0'] - tol, 'min %.1f of %.1f' % (p['hpMin'], p['hp0'])
    return Check('player hp never drops (no self hits)', fn)

def hp_floor(lo, dropped=None):
    def fn(r, ctx):
        p = r['player']
        ok = p['hpMin'] >= lo and (dropped is None or p['hpMin'] <= p['hp0'] - dropped)
        return ok, 'min %.1f of %.1f' % (p['hpMin'], p['hp0'])
    return Check('player hp stays >= %g%s' % (lo, '' if dropped is None else ' and drops >= %g' % dropped), fn)

def maxhp_ratio(lo):
    def fn(r, ctx):
        p = r['player']
        v = p['maxHpMax'] / max(1.0, p['maxHp0Card'])
        return v >= lo, 'max hp up to %.1f from %.1f' % (p['maxHpMax'], p['maxHp0Card'])
    return Check('Q_MaxHp rises to >= %gx' % lo, fn)

def maxhp_vs_control(lo):
    def fn(r, ctx):
        c = ctx.control(r)
        v = r['player']['maxHp0Card'] / max(1.0, c['player']['maxHp0Card'])
        return v >= lo, 'max hp %.1f vs control %.1f' % (r['player']['maxHp0Card'], c['player']['maxHp0Card'])
    return Check('Q_MaxHp >= %gx the control' % lo, fn)

def masochist_recent(at, tol=0.6):
    """PCE BlockAdditionalData.timeOfLastSuccessfulBlock is 'now' when the blocked hit was posted at `at` s."""
    def fn(r, ctx):
        xs = [(s['t'], s['time'] - s['masochistLastBlock']) for s in r['probe'] if s.get('slot', 0) == 0 and 'masochistLastBlock' in s and s['t'] > at + 0.1]
        if not xs: return False, 'no probe samples after the blocked hit'
        t, age = xs[0]
        return abs(age - (t - at)) <= tol, 'last successful block %.2f s before the sample at %.2f s (expected ~%.2f)' % (age, t, t - at)
    return Check('probe.masochistLastBlock ~ the blocked hit at %g s' % at, fn)

def blocks(lo, hi=None, before=None):
    def fn(r, ctx):
        n = sum(1 for t in r['player']['blockTimes'] if before is None or t <= before)
        return n >= lo and (hi is None or n <= hi), '%d blocks%s' % (n, '' if before is None else ' before %g s' % before)
    return Check('Q_Blocks +%d%s%s' % (lo, '' if hi is None else '..%d' % hi, '' if before is None else ' before %g s' % before), fn)

def probe(field, agg, op, value, slot=0, t0=None, t1=None):
    """arena_probe samples: agg in max/min/last/first over the samples in [t0, t1]."""
    desc = 'probe%s.%s %s %s %g%s' % ('' if slot == 0 else '[p2]', field, agg, op, value, '' if t0 is None else ' in %g..%g s' % (t0, t1))
    def fn(r, ctx):
        xs = [s[field] for s in r['probe'] if s.get('slot', 0) == slot and field in s and (t0 is None or t0 <= s['t'] <= t1)]
        if not xs: return False, 'no probe samples' + ('' if r['probeOn'] else ' (engine has no arena_probe)')
        v = {'max': max(xs), 'min': min(xs), 'last': xs[-1], 'first': xs[0]}[agg]
        if isinstance(v, bool): v = float(v)
        ok = {'>=': v >= value, '<=': v <= value, '==': abs(v - value) < 1e-6, '<': v < value, '>': v > value}[op]
        return ok, '%s %.3f' % (agg, v)
    return Check(desc, fn)

def probe_rise(field, amount, slot=0):
    def fn(r, ctx):
        xs = [s[field] for s in r['probe'] if s.get('slot', 0) == slot and field in s]
        if len(xs) < 2: return False, 'not enough probe samples'
        return max(xs) - xs[0] >= amount, 'from %g to %g' % (xs[0], max(xs))
    return Check('probe.%s rises by >= %g' % (field, amount), fn)

def stun_objects_max(n):
    def fn(r, ctx):
        worst = 0
        for s in r['probe']:
            for t in s.get('targets', []): worst = max(worst, t.get('stunObjects', 0))
        return worst <= n, 'max %d per target' % worst
    return Check('probe.stunObjects <= %d per target' % n, fn)

def no_exc(pattern=None):
    def fn(r, ctx):
        bad = [e for e in r['errors'] if 'Exception' in e and (pattern is None or pattern in e) and not any(k in e for k in PCE_STARTUP)]
        return not bad, bad[0][:160] if bad else 'none'
    return Check('no exceptions' + ('' if pattern is None else ' in ' + pattern), fn)

def within(xr, yr):
    def fn(r, ctx):
        p = r['player']
        ok = xr[0] <= p['xMin'] and p['xMax'] <= xr[1] and yr[0] <= p['yMin'] and p['yMax'] <= yr[1]
        return ok, 'x %.0f..%.0f, y %.0f..%.0f' % (p['xMin'], p['xMax'], p['yMin'], p['yMax'])
    return Check('player stays in x %g..%g, y %g..%g' % (xr + yr), fn)

def cards_held(lo):
    def fn(r, ctx): return r['cardsHeld'] >= lo, '%d cards' % r['cardsHeld']
    return Check('Q_Cards >= %d' % lo, fn)

def moved_right(dx):
    def fn(r, ctx):
        p = r['player']
        return p['xMax'] - p['start'][0] >= dx, 'moved %.0f' % (p['xMax'] - p['start'][0])
    return Check('player moved >= %g toward A' % dx, fn)

def partner_checks():
    """--coop: whatever the card, the partner is never hurt, slowed, stunned or silenced by it."""
    def hp(r, ctx):
        p = r.get('partner')
        if not p: return False, 'no partner'
        return p['hpMin'] >= p['hp0'] - 1.0, 'partner hp min %.1f of %.1f' % (p['hpMin'], p['hp0'])
    return [Check('co-op: partner hp unchanged', hp),
            probe('slow', 'max', '<=', 0.001, slot=1), probe('stunned', 'max', '<=', 0, slot=1), probe('silenced', 'max', '<=', 0, slot=1)]

# ---------------------------------------------------------------- expectations (design MATRIX)
# card (lower-case name) -> (expected signals, [scenarios]). Every scenario also gets no_exc(); cards without an entry get
# the generic "hits > 0" check.

NO_SHOT = dict(shoot=False)

def bullet_card(lo=45, hi=65, extra=()):
    return [S('', [hits(1, kind='Bullet'), per_hit('Bullet', lo, hi)] + list(extra))]

def bounce_card(extra=()):
    return [S('', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 1.5, 2, le=True),
                   probe('bulletsOutsideView', 'max', '<=', 0)] + list(extra))]

EXPECT = {
    'poison': ('1 direct damage per bullet, 10 Dot ticks of 9.35 (93.5) over 3 s, green tint', [
        S('', [per_hit('Bullet', 0.5, 2.0), dmg(50, kind='Dot'), dot_per_bullet(93.5), fx('Dot', a=(80, 110), b=(2.9, 3.1), c=(0.25, 0.35)),
               st('dot', 0.1), dot_rgba(0x4AD652FF), hit_rgba('Dot', 0x4AD652FF)], settle=3.5)]),
    'parasite': ('17 Dot ticks of 4.125 (70.1) over 5 s, purple; 50% lifesteal on every tick', [
        S('hurt first', [dmg(50, kind='Dot'), fx('Dot', a=(60, 80), b=(4.9, 5.1)), dot_rgba(0x9E79FFFF), hp_rose(15),
                         probe('sinceDealtDamage', 'min', '<', 0.4)], hurt=[0.2], settle=5.5)]),
    'cold bullets': ('each hit slows 0.7 (normal), decaying 0.3/s (~2.33 s); damage 55', [
        S('', [fx('Slow', a=(0.65, 0.75), b=(-0.01, 0.01)), st('slow', 0.6, 0.72), decay(GROUP_A, 1.8, 2.9), per_hit('Bullet', 45, 65)], settle=3.2),
        S('no-status flag', [hits(1, kind='Bullet'), fx_none('Slow'), st('slow', hi=0.001)], nostatus=True)]),
    'dazzle': ('a 0.1 s stun every 0.35 s for ~2 s per hit, one Dazzle object per enemy, no StunPlayer NRE', [
        S('', [fx('Stun', 3, GROUP_A, a=(0.05, 0.15)), st('stun', 0.01, 0.12), stun_objects_max(1), no_exc('StunPlayer')]),
        S('A and Bm', [fx('Stun', 2, GROUP_A), fx('Stun', 2, GROUP_BM)], aim='alt')]),
    'emp': ('blocking fires weak EMP bullets: small hits, slow 0.7, knockback', [
        S('block only', [hits(1, kind='Bullet', dmax=15), fx('Slow', a=(0.6, 0.75)), fx('Force'), blocks(1)], **NO_SHOT)]),
    'bombs away': ('blocking fires bombs (13.75) that stick and blast (30 x v, force 9000), one blast hit per enemy; max hp x1.3', [
        S('block only', [hits(1, kind='Bullet', dmax=20), hits(1, kind='Area'), no_dup_area(), fx('Force'), maxhp_vs_control(1.25)], **NO_SHOT)]),
    'drill ammo': ('each bullet hits an enemy once and ends there', [
        S('', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 1.5, 2, le=True)])]),
    'bouncy': ('bullets bounce off walls and view edges and end at the enemy (today 0 hits)', bounce_card()),
    'riccochet': ('as Bouncy; damage per hit unchanged', bounce_card([per_hit('Bullet', 40, 70)])),
    'mayhem': ('as Bouncy', bounce_card()),
    'trickster': ('direct hits at the card -20% (44); x1.8 per bounce, never gained from the boss', bounce_card([per_hit('Bullet', 40, 80)])),
    'target bounce': ('after a bounce the bullet re-aims at the nearest enemy (never the shooter)', [
        S('aim at the floor', [hits(1, kind='Bullet'), hp_steady()], aim=[240, 20])]),
    'super ball': ('as Bouncy, x0.75 damage per bounce', bounce_card()),
    'homing': ('bullets curve toward the nearest visible enemy within 70 degrees', [
        # 60 deg below A (20 deg under the horizon) a plain gun goes into the floor under it (aimed above, bullets arc up
        # and drop onto the tall box); A stays inside homing's 70 deg cone, so homing turns them up into it.
        S('aim 60 deg below A', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 1.5, 2)], aim_offset=-60)]),
    'remote': ('bullets steer toward the Cuphead cursor', [
        S('cursor on A, aim 40 deg up', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 1.5, 2), probe('bulletsOutsideView', 'max', '<=', 0)],
          aim_offset=40, cursor=[A_BOX[0], A_BOX[1]])]),
    'sneaky': ('bullets curve away from floors and walls, never dodge enemies', [
        S('', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 0.6)])]),
    'big bullet': ('the larger radius counts against enemies', [S('', [hits(1, kind='Bullet'), vs_control('hits[Bullet]', n_bullet, 0.8)])]),
    'ghost bullets': ('bullets pass walls and floors but hit enemies, x0.5 damage (today 0 hits)', [
        S('', [hits(1, kind='Bullet'), per_hit('Bullet', 20, 35)]),
        S('wall W', [hits(1, kind='Bullet'), per_hit('Bullet', 20, 35)], wall=True)]),
    'grow': ('damage grows with distance', bullet_card(45, 200)),
    'close quarters': ('about 110 per hit up close', bullet_card(85, 135)),
    'fastball': ('damage or projectile change only', bullet_card(40, 80)),
    'quick shot': ('damage or projectile change only', bullet_card(30, 80)),
    'straight shot': ('damage or projectile change only', bullet_card(30, 80)),
    'comb': ('main bullets x0.7 (38.5), each with 2 side bullets at x0.5 of that (19.25, CombEffect.combDamageMult)', [
        S('', [vs_control('hits[Bullet]', n_bullet, 1.0), per_hit('Bullet', 30, 45, how='max'), per_hit('Bullet', 15, 22, how='min')])]),
    'ant': ('110 per hit', bullet_card(90, 130)),
    'explosive bullet': ('each hit blasts 30 x v (~29), force 9000 x v, one hit per enemy per blast', [
        S('', [hits(1, kind='Area'), per_hit('Area', 12, 35), no_dup_area(), fx('Force', group=GROUP_A)])]),
    'timed detonation': ('sticky bombs blast 30 x v, one hit per enemy per blast', [
        S('', [hits(1, kind='Area'), no_dup_area(), fx('Force', group=GROUP_A)])]),
    'toxic cloud': ('clouds tick 8 x cloud scale (ObjectsToSpawn scaleFromDamage: smaller with -25% dmg) every 0.15 s, fast slow 0.7, green', [
        S('', [hits(1, kind='Area', group=GROUP_A), per_hit('Area', 4, 8.5), hit_rgba('Area', green=True), st('slow', 0.6, 0.75),
               rate_max('Area', GROUP_A, 13), fx('Slow', b=(0.99, 1.01))])]),
    'demonic pact': ('splash of 2 per bullet; each shot costs 5 hp (non-lethal)', [
        S('', [hits(1, kind='Area'), per_hit('Area', 0.1, 2.5, how='max'), hp_floor(1, dropped=5)])]),
    'fragmentation': ('impacts burst into half-damage fragments', [
        S('', [vs_control('hits[Bullet]', n_bullet, 1.2), hits(1, kind='Bullet', dmin=20, dmax=35)])]),
    'fireworks': ('bullets pop into 5 near the top of the Cuphead view', [
        S('aim up', [probe('bulletsAlive', 'max', '>=', 4)], aim=[200, 2000])]),
    'pac-bullets': ('bullets wrap at the Cuphead view edges (today they leave the level)', [
        S('aim left, away from A', [hits(1, kind='Bullet'), probe('bulletsOutsideView', 'max', '<=', 0), within((-700, 700), (-200, 1100))], aim=[-2000, 100])]),
    'laser': ('weapon switch turns it on: max(gun.damage, 1) x 4 x intensity (4, fading) every 0.1 s (LaserHurtbox.cs:342), < 20 segments', [
        S('switch, then fire', [hits(10, kind='Bullet'), per_hit('Bullet', 3.5, 4.5, how='max', t0=0.3), dmg(60, kind='Bullet'),
                                no_dup_hits('Bullet'), probe('laserSegments', 'max', '<', 20)], switch=[0.2])]),
    'thruster': ('a thruster sticks and pushes along the shot: ~4.6 s on a light enemy, ~0.92 s on a boss', [
        S('one shot at the boss', [fx('Force', 5, GROUP_A, d=(0.99, 1.01)), fx_streak('Force', GROUP_A, 0.4, 1.6, d=(0.99, 1.01))], shoot_until=0.2),
        # the gun turns on a spring: half a second to swing round from the last run's aim before the one shot
        S('one shot at Bm', [fx_streak('Force', GROUP_BM, 3.0, None, d=(0.99, 1.01))], aim='Bm', shoot_from=0.5, shoot_until=0.7, settle=3.0)]),
    'buckshot': ('knockback x2.5 x (dmg 0.4)^2 = 0.4 per pellet (ProjectileHit.cs:110); 4-5 pellets a frame: 1.6-2x', [S('', [fx('Force', group=GROUP_A), force_vs_control(1.3)])]),
    'tractor beam': ('knockback becomes a pull (dv against the shot)', [S('', [fx('Force', group=GROUP_A), fx_sign('Force', GROUP_A, neg=True)])]),
    'flip': ('gravity x-0.5 for 1.5 s per hit (a short stun on Cuphead)', [
        S('', [fx('Gravity', a=(-0.6, -0.4), b=(1.4, 1.6)), flag(ST_FLIPPED, 'Flipped'), st('aux', 0.1)])]),
    'grounded': ('gravity x3 for 3 s per hit (a 30% slow on Cuphead)', [
        S('', [fx('Gravity', a=(2.9, 3.1), b=(2.9, 3.1)), flag(ST_HEAVY, 'Heavy'), st('aux', 0.1)])]),
    'leech': ('75% lifesteal on damage dealt to enemies', [
        S('hurt first', [hp_rose(30), probe('sinceDealtDamage', 'min', '<', 0.6)], hurt=[0.2])]),
    'taste of blood': ('30% lifesteal, +50% move speed for 3 s after dealing damage', [
        S('hurt first', [hp_rose(10), probe('moveSpeed', 'max', '>=', 1.4), probe('sinceDealtDamage', 'min', '<', 3)], hurt=[0.2])]),
    'brawler': ('x3 health for 3 s after dealing damage', [S('', [maxhp_ratio(2.5)])]),
    'scavenger': ('dealing damage reloads the weapon (so it fires much more)', [
        S('', [vs_control('hits[Bullet]', n_bullet, 1.4), probe('sinceDealtDamage', 'min', '<', 0.6)])]),
    'refresh': ('the block recharges after 10+ damage (0.5 s cooldown)', [
        S('block every second', [blocks(2, before=4.4)], blocks=[0.5, 1.5, 2.5, 3.5])]),
    'pacifist iv': ('the bonus ramps only while not dealing damage and resets on every enemy hit', [
        S('idle 8 s, then fire', [first_hit('Bullet', 1.4 * D, 1.25 * D), probe('sinceDealtDamage', 'min', '<', 0.6)], shoot_from=8.0, seconds=12.0, blocks=[])]),
    'masochist iv': ('resets when a Cuphead hit is blocked by the ROUNDS block', [
        S('blocked hit at 1.08 s', [masochist_recent(1.08), per_hit('Bullet', 30, 1.45 * D, t0=1.6, t1=4.0), hp_steady()],
          blocks=[1.0], blocked_hit=[1.08])]),
    'overpower': ('blocking hits each enemy within 4 for 15% of max hp x blast scale (19.5+), once per group, plus a push; no NRE', [
        S('block only', [hits(1, 2, kind='Area', group=GROUP_A), per_hit('Area', 19, 32), fx('Force'), no_exc('Explosion_Overpower')], **NO_SHOT)]),
    'frost slam': ('slow 2.2 x v (normal): the boss sits at the 0.9 cap; 1 x v damage and a push', [
        S('block only', [fx('Slow', group=GROUP_A, a=(1.0, 2.3), b=(-0.01, 0.01)), st('slow', 0.85, 0.901), per_hit('Area', 0.0, 1.05, how='max'), fx('Force')], **NO_SHOT)]),
    'silence': ('silence 3.2 x v s, slow 0.6 x v, 10 x v damage, a push', [
        S('block only', [fx('Silence', group=GROUP_A, a=(0.5, 3.3)), st('silence', 0.5), fx('Slow', group=GROUP_A, a=(0.05, 0.61)),
                         hits(1, kind='Area', group=GROUP_A), per_hit('Area', 0.5, 10.5, how='max')], **NO_SHOT)]),
    'shockwave': ('force 30000 (flyingFor 0.7), slow 0.5, 1 damage', [
        S('block only', [fx('Force', group=GROUP_A, mag=(3000, 1e9), c=(0.6, 0.8)), fx('Slow', a=(0.4, 0.55)), per_hit('Area', 0.0, 1.05, how='max')], **NO_SHOT)]),
    'implode': ('0.1 s after blocking: a pull toward the player plus slow 0.5, no damage', [
        S('block only', [fx('Pull', group=GROUP_A), pull_near_player(150), fx('Slow', a=(0.4, 0.55)), hits(0, 0, kind='Area')], **NO_SHOT)]),
    'static field': ('1 damage every 0.05 s and fast slow 0.85 while inside, ~4.5 s', [
        S('block only', [hits(10, kind='Area', group=GROUP_A), st('slow', 0.8, 0.86), fx('Slow', b=(0.99, 1.01))], **NO_SHOT)]),
    # blast scale = the blocker's size, 1.2 x (maxHp/100 x 1.2)^0.2 (CharacterStatModifiers.cs:262): 1.35 at Supernova's 150 hp
    'supernova': ('a 2.1 s pull, then stun 1.5 x (1+1.35)/2 = 1.76, fast slow 0.5 x 1.35 x falloff and 30 x v damage', [
        S('block only', [fx('Force', 3, GROUP_A), fx('Stun', group=GROUP_A, a=(1.65, 1.85)), st('stun', 1.0), fx('Slow', a=(0.45, 0.7), b=(0.99, 1.01)),
                         hits(1, kind='Area', group=GROUP_A)], **NO_SHOT)]),
    'saw': ('continuous 60 dps plus a push on the closest visible enemy in range', [
        S('block once', [dmg(100, kind='Continuous'), fx('Force', d=(0.99, 1.01))], blocks=[1.0], seconds=11.5, **NO_SHOT)]),
    'abyssal countdown': ('after standing still 7 s: 90 dps saw for 5 s', [
        S('stand still 14 s', [dmg(100, kind='Continuous')], blocks=[], seconds=14.0, **NO_SHOT)]),
    'radiance': ('during reload, rings deal 4 and a push to the closest enemy once per ring', [
        S('', [hits(1, kind='Area'), per_hit('Area', 1, 6, how='max'), fx('Force')])]),
    'chilling presence': ('every 0.2 s the nearest visible enemy within 4.5 gets +0.5 slow, up to 0.7; no FollowPlayer NRE', [
        S('', [st('slow', 0.45, 0.72, group=GROUP_BM), no_exc('FollowPlayer')])]),
    'lifestealer': ('every 0.3 s: 5 damage to the nearest enemy within 4, heal 8', [
        S('hurt first', [hits(3, kind='Aura'), per_hit('Aura', 4, 6), hp_rose(8)], hurt=[0.2], **NO_SHOT)]),
    'chase': ('+60% move speed while moving toward a visible enemy', [
        S('toward A, then away', [probe('moveSpeed', 'max', '>=', 1.5, t0=0.5, t1=1.6), probe('moveSpeed', 'max', '<=', 1.05, t0=2.1, t1=3.0), moved_right(150)],
          # no Bm: the run passes x=100, so walking back would be walking toward Bm (Chase rightly stays on)
          spawn=[-500, 60], move='chase', blocks=[], seconds=3.5, probe_every=0.25, roams=True, bm=False, **NO_SHOT)]),
    'radar shot': ('blocking auto-fires at the nearest visible enemy within 12', [S('block only', [hits(1, kind='Bullet')], **NO_SHOT)]),
    'shield charge': ('the charge bumps each enemy once for 25 and knocks back', [
        S('block at A', [hits(1, 2, kind='Contact', group=GROUP_A), per_hit('Contact', 20, 30), fx('Force')], roams=True, **NO_SHOT)]),
    'empower': ('the next shot does x1.5 (82.5) and runs block effects where it lands; it is not a parry', [
        S('', [per_hit('Bullet', 75, 90, how='max'), blocks(1, 2)])]),
    'echo': ('every block happens twice (additionalBlocks 1: one delayed Echo block, Block.cs:153)', [S('', [blocks(2, 2)])]),
    'healing field': ('heals you and your partner, never Cuphead enemies', [
        S('hurt first', [hp_rose(30), hits(0, 0), fx_total_max(0)], hurt=[0.2], **NO_SHOT)]),
    'discombobulate': ('blocking confuses enemies within 5 for 2 s (a silence on Cuphead)', [
        S('block only', [fx('Confuse', group=GROUP_A, a=(1.5, 2.5)), st('silence', 1.0), flag(ST_CONFUSED, 'Confused')], **NO_SHOT)]),
    'glare': ('visible enemies get 15% slow per stack, refreshed continuously', [
        S('', [st('slow', 0.1, 0.2)]),
        S('A behind wall W', [st('slow', hi=0.001)], wall=True)]),
    'low ground': ('its buffs turn on when a Cuphead enemy is above you', [
        S('A raised', [probe('gunDamage', 'max', '>=', 1.4)], a_y=440, a2=False, **NO_SHOT),
        S('A level', [probe('gunDamage', 'max', '<=', 1.1)], a_y=60, a2=False, **NO_SHOT)]),
    'king midas': ('an enemy you touch turns gold (lasting 50% slow) until it dies', [
        S('walk into A', [fx('Gold'), flag(ST_GOLD, 'Gold', group=None)], move='toward', roams=True, **NO_SHOT)]),
    # Cuphead keeps Murder stacks in its save and spends them itself; in ROUNDS the card must do nothing (no FxMurder,
    # no RPCA_Die on anyone, the bench player alive)
    'murder': ('the engine does nothing with Murder: no FxMurder, nobody dies (Cuphead spends the saved stacks)', [
        S('new level at 1 s', [fx_none('Murder'), hp_steady()], new_level=1.0, **NO_SHOT)]),
    'demonic possession': ('random self effects; bullets spawn above the Cuphead view, not at world (0,100)', [
        S('', [probe('bulletsOutsideView', 'last', '<=', 0)], seconds=12.0)]),
    'thank you sir, may i have another?': ('each Cuphead hit on you adds max ammo', [
        S('hurt x3', [probe_rise('maxAmmo', 3)], hurt=[0.5, 1.0, 1.5], hurt_amount=10.0, **NO_SHOT)]),
    'pac-player': ('the player wraps at the Cuphead view edges and stays in the level', [
        S('walk off the left edge', [within((-720, 720), (-200, 1100))], move=[[0.2, 4.5, -1, 0]], roams=True, **NO_SHOT)]),
    'super jump': ('holding down charges a higher jump', [
        S('hold down 1.2 s, jump', [vs_control('peak y', peak_y, 1.0, 20)], down_hold=[0.3, 1.5], jump=[1.6], blocks=[], seconds=3.0, roams=True, **NO_SHOT)]),
    'piercing bullets': ('nothing to act on (enemies never block)', [S('', [vs_control('hits[Bullet]', n_bullet, 0.7), per_hit('Bullet', 40, 70)])]),
    'punching bullets': ('nothing to act on', [S('', [vs_control('hits[Bullet]', n_bullet, 0.7), per_hit('Bullet', 40, 70)])]),
    'friendly bullets': ('partner damage is already blocked', [S('', [vs_control('hits[Bullet]', n_bullet, 0.7)])]),
    'gamble': ('grants cards', [S('', [cards_held(2)])]),
    'risky gamble': ('grants cards', [S('', [cards_held(2)])]),
    'jackpot': ('grants cards', [S('', [cards_held(2)])]),
    'small jackpot': ('grants cards', [S('', [cards_held(2)])]),
    'common': ('random card; never resolves in the sandbox (out of scope)', [S('', [])]),
    'uncommon': ('random card; never resolves in the sandbox (out of scope)', [S('', [])]),
    'rare': ('random card; never resolves in the sandbox (out of scope)', [S('', [])]),
    'shuffle': ('never resolves in the sandbox (out of scope)', [S('', [])]),
    'glass cannon': ('110 per hit', bullet_card(95, 125)),
    'spray': ('many small hits (13.75)', [S('', [vs_control('hits[Bullet]', n_bullet, 1.0), per_hit('Bullet', 8, 20)])]),
    'teleport': ('player-side block mechanic', [S('', [hits(1), within((-900, 900), (-200, 1500))], roams=True)]),
}
# Stat and self cards: ApplyCardStats plus normal bullets (generic check: hits > 0).
for _n in ['barrage', 'burst', 'careful planning', 'combine', 'fast forward', 'quick reload', 'steady shot', 'wind up', 'tank', 'huge',
           'decay', 'phoenix', 'prisitne perseverence', 'jetpack', 'old jetpack', 'moon shoes', 'mulligan', 'last stand',
           'survivalist i', 'survivalist ii', 'survivalist iii', 'survivalist iv', 'wildcard i', 'wildcard ii', 'wildcard iii', 'wildcard iv',
           'shields up', 'tactical reload', 'defender', 'retreat', 'pacifist i', 'pacifist ii', 'pacifist iii',
           'masochist i', 'masochist ii', 'masochist iii']:
    EXPECT.setdefault(_n, ('stats only; damage reaches enemies through ProjectileHit', [S('', [hits(1)])]))

# ---------------------------------------------------------------- design COMBOS
COMBOS = [
    ('Poison+Explosive bullet', 'per bullet: 1 Bullet hit, 1 Area hit (~30 x v), 10 Dot ticks', [
        S('', [per_hit('Bullet', 0.5, 2.0), hits(1, kind='Area'), dmg(50, kind='Dot'), fx('Dot'), dot_rgba(0x4AD652FF)], settle=3.5)]),
    ('Poison+Parasite', 'two DoTs at half each; Parasite lifesteal on every tick', [
        S('hurt first', [dmg(50, kind='Dot'), fx('Dot', 2), hp_rose(15)], hurt=[0.2], settle=5.5)]),
    ('Drill ammo+Poison', '1 Bullet hit and 10 Dot ticks per bullet; never several hits per bullet', [
        S('', [dmg(50, kind='Dot'), vs_control('hits[Bullet]', n_bullet, 1.5, 2, le=True)], settle=3.5)]),
    ('Bouncy+Poison', 'bank shots land; exactly 1 FxDot per bullet; nothing outside the view', [
        S('', [fx('Dot'), Check('fx[Dot] count <= bullet hits + 1', lambda r, c: (len(_sel_fx(r, 'Dot')) <= n_bullet(r) + 1, '%d Dot fx, %d bullets' % (len(_sel_fx(r, 'Dot')), n_bullet(r)))),
               probe('bulletsOutsideView', 'max', '<=', 0)], settle=3.5)]),
    ('Trickster+Explosive bullet', 'blasts scale with bounce damage; a bounce off the boss adds nothing; 1 hit per bullet', [
        S('', [hits(1, kind='Area'), vs_control('hits[Bullet]', n_bullet, 1.5, 2, le=True)])]),
    ('Homing+Target bounce', 'bullets curve and re-aim at A only; shooter never hit', [S('', [hits(1, kind='Bullet'), hp_steady()])]),
    ('Homing+Bouncy+Mayhem', 'bullets stay in view and end on A', [
        S('', [hits(1, kind='Bullet'), probe('bulletsOutsideView', 'max', '<=', 0), vs_control('hits[Bullet]', n_bullet, 1.5, 2, le=True)])]),
    ('Dazzle+Spray', 'one Dazzle object per enemy; stuns at ~0.35 s cadence', [S('', [stun_objects_max(1), fx('Stun', 3, GROUP_A), no_exc('StunPlayer')])]),
    ('Frost slam+Chilling presence+Cold bullets', 'slow takes the max, the additive slow its own cap, never above 0.9', [
        S('', [st_all_max('slow', 0.9001), fx('Slow', group=GROUP_A)])]),
    ('Static field+Toxic cloud', 'two fast slows: 0.85 while in both; never above 0.9', [S('', [st('slow', 0.8, 0.9001), st_all_max('slow', 0.9001)])]),
    ('Overpower+Bombs away+Echo', 'per block: 2 Overpower hits per group, 2 x 6 bombs, no NRE, Q_Blocks +2', [
        S('block only', [hits(2, kind='Area', group=GROUP_A), no_exc('Explosion_Overpower'), blocks(2)], **NO_SHOT)]),
    # Brawler x3 on 100 x 1.3 x 2 hp = 780; blast scale 1.2 x (7.8 x 1.2)^0.2 = 1.877: 0.15 x 780 x 1.877 = 219.6
    ('Overpower+Brawler+Tank', 'Overpower = 0.15 x max hp x blast scale, 3x right after a hit (up to ~220)', [S('', [hits(1, kind='Area', group=GROUP_A), per_hit('Area', 20, 230, how='max'), no_exc('Explosion_Overpower')])]),
    ('Empower+Frost slam+Silence+Supernova', 'the empowered impact runs the block effects on A; Q_Blocks counts only real blocks', [
        S('', [fx('Slow', group=GROUP_A), fx('Silence', group=GROUP_A), fx('Stun', group=GROUP_A), blocks(1, 2)])]),
    ('Supernova+Implode+Dazzle', 'pulls plus stun sources on a boss; bounded freeze', [S('', [fx('Stun', group=GROUP_A), fx('Pull', group=GROUP_A), no_exc()])]),
    ('Toxic cloud+Leech', 'lifesteal from cloud ticks (0.75 x 8 per tick)', [S('hurt first', [hp_rose(20), hits(1, kind='Area')], hurt=[0.2])]),
    ('Static field+Refresh', 'field ticks add up to 10 damage, so the block keeps refreshing', [
        S('block every 0.5 s', [blocks(3, before=4.4)], blocks=[1.0 + 0.5 * i for i in range(10)])]),
    ('Thruster+Tractor beam+Timed detonation', 'opposing forces plus a sticky bomb', [S('', [fx('Force', group=GROUP_A), hits(1, kind='Area')])]),
    ('Saw+Abyssal countdown', 'two saws, summed continuous hits (~150 dps)', [
        S('block once, stand still', [dmg(150, kind='Continuous')], blocks=[1.0], seconds=14.0, **NO_SHOT)]),
    ('Ghost bullets+Homing', 'behind wall W: no homing curve, but a straight shot through the wall still hits', [S('wall W', [hits(1, kind='Bullet')], wall=True)]),
    ('Pac-bullets+Bouncy', 'bullets wrap 3 times, then bounce at the edges; nothing outside the view', [
        S('aim left', [hits(1, kind='Bullet'), probe('bulletsOutsideView', 'max', '<=', 0)], aim=[-2000, 100])]),
    ('Fragmentation+Cold bullets', 'fragments carry the slow; st.slow stays 0.7 (max rule)', [S('', [fx('Slow', 2, GROUP_A), st('slow', 0.6, 0.72)])]),
    ('Radar shot+Poison', 'auto-shots carry the gun\'s on-hit effects', [S('block only', [hits(1, kind='Bullet'), dmg(50, kind='Dot')], settle=3.5, **NO_SHOT)]),
    ('Shield charge+Overpower', '1 Contact hit (25) plus Overpower blasts', [S('block at A', [hits(1, kind='Contact'), hits(1, kind='Area')], roams=True, **NO_SHOT)]),
    ('Pacifist IV+Explosive bullet', 'only the first bullet (and its blast) is boosted', [
        S('idle 8 s, then fire', [first_hit('Bullet', 1.4 * D, 1.25 * D)], shoot_from=8.0, seconds=12.0, blocks=[])]),
    ('Demonic pact+Leech', 'self damage never heals; enemy damage does', [S('', [hp_floor(1), hits(1, kind='Area')])]),
]

# ---------------------------------------------------------------- the link and the engine (opened by main)

m = None
sockf = None
rid = [0]
by_name, alias, names_of = {}, {}, {}

def wi(o, v): struct.pack_into('<i', m, o, int(v))
def wf(o, v): struct.pack_into('<f', m, o, float(v))
def ri(o): return struct.unpack_from('<i', m, o)[0]
def rf(o): return struct.unpack_from('<f', m, o)[0]

def call(op, **kw):
    rid[0] += 1
    sockf.write(json.dumps(dict(rid=rid[0], op=op, **kw)) + '\n'); sockf.flush()
    while True:
        line = sockf.readline()
        if not line: raise RuntimeError('engine closed')
        r = json.loads(line)
        if r.get('rid') == rid[0]: return r

def canon(x):
    return alias.get(x.lower(), x.lower().replace('__pce__', ''))

class Bench:
    def __init__(self, coop):
        self.coop = coop
        self.beat = ri(C_BEAT)
        self.evw = ri(C_EVW)
        self.life = [max(0, ri(rslot(0) + Q_LIFE)), max(0, ri(rslot(1) + Q_LIFE))]
        self.counters = {'block': ri(slot(0) + S_BLOCKS), 'shoot': ri(slot(0) + S_SHOOTS), 'jump': ri(slot(0) + S_JUMPS), 'switch': ri(slot2(0) + U_SWITCH)}
        self.probe_ok = True
        self.layout = None
        self.level = ri(C_LEVEL)

    # -- control block
    def tick(self):
        self.beat += 1; wi(C_BEAT, self.beat)

    def post_event(self, t, s, a=0.0, b=0.0, c=0.0, d=0.0, lf=0):
        e = C_EVENTS + (self.evw % EVENT_RING) * EVENT_SIZE
        wi(e, self.evw + 1); wi(e + 4, t); wi(e + 8, s); wf(e + 12, a); wf(e + 16, b); wf(e + 20, c); wf(e + 24, d); wi(e + 28, lf)
        self.evw += 1; wi(C_EVW, self.evw)

    def write_cols(self, s):
        """The bench's level; returns {target id: group}."""
        a_y = s['a_y'] if s['a_y'] is not None else A_BOX[1]
        fl = TF_NOSTATUS if s['nostatus'] else 0
        cols = [FLOOR] + WALLS
        tg = [(5, GROUP_A, TF_BOSS | fl, (A_BOX[0], a_y, A_BOX[2], A_BOX[3]))]
        if s['a2']: tg.append((6, GROUP_A, TF_BOSS | fl, (A2_BOX[0], a_y + (A2_BOX[1] - A_BOX[1]), A2_BOX[2], A2_BOX[3])))
        if s['bm']: tg.append((7, GROUP_BM, TF_LIGHT | fl, BM_BOX))
        if s['wall']: cols = cols + [(8, 0, list(WALL_W) + [0])]
        rows = [(cid, kind, data, 0, 0) for cid, kind, data in cols] + [(cid, 3, list(box) + [0], g, f) for cid, g, f, box in tg]
        key = json.dumps(rows)
        if key != self.layout:
            for i, (cid, kind, data, g, f) in enumerate(rows):
                e = C_COLS + i * COL_SIZE
                wi(e, cid); wi(e + 4, kind); wi(e + 8, 0); wi(e + 12, 0)
                for k in range(32): wf(e + 16 + k * 4, data[k] if k < len(data) else 0)
                wi(e + K_GROUP, g); wi(e + K_FLAGS, f)
            wi(C_COLCOUNT, len(rows))
            self.layout = key
            self.wait(0.3)   # ROUNDS mirrors it next frame
        return {cid: g for cid, g, f, box in tg}

    def wait(self, sec, fn=None, rec=None):
        t0 = time.time()
        while True:
            t = time.time() - t0
            if t >= sec: break
            self.tick()
            if fn: fn(t)
            if rec: rec.poll(t)
            time.sleep(1 / 60)

    def spawn(self, s, pos):
        self.life[s] += 1
        self.post_event(EV_SPAWN, s, pos[0], pos[1] + 40, 0, 0, self.life[s])
        t0 = time.time()
        while ri(rslot(s) + Q_LIFE) != self.life[s] and time.time() - t0 < 5:
            self.tick(); time.sleep(1 / 60)
        return ri(rslot(s) + Q_LIFE) == self.life[s]

    def drain(self):
        """Wait until hits from the previous run (damage over time, saws, clouds) have stopped (at most 6 s)."""
        last, n0, t0, quiet = ri(R_HITWRITE), ri(R_HITWRITE), time.time(), time.time()
        while time.time() - t0 < 6 and time.time() - quiet < 0.6:
            self.tick(); time.sleep(1 / 60)
            w = ri(R_HITWRITE)
            if w != last: last, quiet = w, time.time()
        return ri(R_HITWRITE) - n0

    def probe(self, s):
        if not self.probe_ok: return None
        try:
            r = call('arena_probe', slot=s)
        except Exception:
            self.probe_ok = False; return None
        if not r.get('ok') and 'unknown op' in (r.get('error') or ''):
            self.probe_ok = False; return None
        try: return json.loads(r['ids'][0]) if r.get('ids') else None
        except Exception: return None

    # -- one run
    def run(self, cards, sc, label=''):
        s = adjust(script_of(sc), cards)
        ids = [by_name.get(x.lower(), x) for x in cards]
        ids2g = self.write_cols(s)
        notes = []
        leftover = self.drain()
        if leftover: notes.append('%d hits from the previous run drained first' % leftover)
        logpos = (log_size(ROUNDS_LOG), log_size(ENGINE_LOG))
        call('arena_cards', slot=0, owned=[])
        self.wait(0.3)
        rr = call('arena_cards', slot=0, owned=ids)
        self.wait(0.4)
        start = tuple(s['spawn']) if s['spawn'] else PLAYER
        if not self.spawn(0, start): notes.append('spawn not confirmed (Q_Life)')
        if self.coop and not self.spawn(1, PARTNER): notes.append('partner spawn not confirmed')
        self.wait(0.3)
        held = ri(rslot(0) + Q_CARDS)
        rec = Recorder(self, s, ids2g, start)
        s0, x0 = slot(0), slot2(0)
        st_ = {'blocked': set(), 'hurt': set(), 'bh': set(), 'sw': set(), 'jump': set(), 'jumpUntil': -1.0, 'level': False, 'nextProbe': 0.0, 'clickWas': False}
        aim_pts = {'A': (A_BOX[0], (s['a_y'] if s['a_y'] is not None else A_BOX[1])), 'Bm': (BM_BOX[0], BM_BOX[1])}

        def once(key, times, t, act):
            for bt in times or []:
                if bt <= t and bt not in st_[key]:
                    st_[key].add(bt); act(bt)

        def drive(t):
            px, py = rf(rslot(0) + Q_X), rf(rslot(0) + Q_Y)
            # move
            mx = my = 0.0
            mv = s['move']
            if mv == 'toward':
                dx = aim_pts['A'][0] - px
                mx = 0.0 if abs(dx) < 30 else (1.0 if dx > 0 else -1.0)
            elif mv == 'chase':
                mx = 1.0 if 0.3 <= t < 1.6 else -1.0 if 1.8 <= t < 3.0 else 0.0
            elif isinstance(mv, list):
                for t0, t1, ax, ay in mv:
                    if t0 <= t < t1: mx, my = ax, ay
            wf(s0 + S_MOVEX, mx); wf(s0 + S_MOVEY, my)
            # aim
            a = s['aim']
            if a == 'alt': a = 'A' if int(t // 1.5) % 2 == 0 else 'Bm'
            tx, ty = aim_pts[a] if isinstance(a, str) else a
            vx, vy = tx - px, ty - py
            if s['aim_offset']:
                ang = math.radians(s['aim_offset']); c, sn = math.cos(ang), math.sin(ang)
                vx, vy = vx * c - vy * sn, vx * sn + vy * c
            wf(s0 + S_AIMX, vx); wf(s0 + S_AIMY, vy)
            # fire: clicks at 4 Hz (ROUNDS guns fire on the press)
            firing = s['shoot'] and t >= s['shoot_from'] and (s['shoot_until'] is None or t < s['shoot_until'])
            click = firing and (t * 4) % 1 < 0.5
            if click and not st_['clickWas']:
                self.counters['shoot'] += 1; wi(s0 + S_SHOOTS, self.counters['shoot']); rec.shots += 1
            st_['clickWas'] = click
            wi(s0 + S_SHOOTHELD, 1 if click else 0)
            # blocks, hurt, blocked hits, weapon switch, jumps, down held, cursor, new level
            def blk(bt): self.counters['block'] += 1; wi(s0 + S_BLOCKS, self.counters['block']); rec.presses.append(round(t, 2))
            once('blocked', s['blocks'], t, blk)
            once('hurt', s['hurt'], t, lambda bt: (self.post_event(EV_DAMAGE, 0, s['hurt_amount'], 0.0, 1.0, 0.0), rec.hurt_at.append(t)))
            once('bh', s['blocked_hit'], t, lambda bt: self.post_event(EV_BLOCKED, 0, HEALTH_PER_HEART, -1.0, 0.0))
            def sw(bt): self.counters['switch'] += 1; wi(x0 + U_SWITCH, self.counters['switch'])
            once('sw', s['switch'], t, sw)
            def jp(bt): self.counters['jump'] += 1; wi(s0 + S_JUMPS, self.counters['jump']); st_['jumpUntil'] = t + 0.35
            once('jump', s['jump'], t, jp)
            wi(s0 + S_JUMPHELD, 1 if t < st_['jumpUntil'] else 0)
            dh = s['down_hold']
            wi(x0 + U_DOWN, 1 if dh and dh[0] <= t < dh[1] else 0)
            if s['cursor']:
                wf(x0 + U_CURX, s['cursor'][0]); wf(x0 + U_CURY, s['cursor'][1]); wi(x0 + U_CURON, 1)
            if s['new_level'] is not None and t >= s['new_level'] and not st_['level']:
                st_['level'] = True; self.level += 1; wi(C_LEVEL, self.level)
            # engine probe
            if t >= st_['nextProbe']:
                st_['nextProbe'] = t + s['probe_every']
                rec.take_probe(t)

        self.wait(s['seconds'], drive, rec)
        for o, v in ((S_SHOOTHELD, 0), (S_JUMPHELD, 0)): wi(s0 + o, v)
        for o in (S_MOVEX, S_MOVEY): wf(s0 + o, 0)
        wi(x0 + U_CURON, 0); wi(x0 + U_DOWN, 0)
        nxt = [s['seconds']]
        def settle(t):
            tt = s['seconds'] + t
            if tt >= nxt[0]: nxt[0] = tt + 0.5; rec.take_probe(tt)
        self.wait(s['settle'], settle, _Shift(rec, s['seconds']))
        errors = log_errors(ROUNDS_LOG, logpos[0]) + log_errors(ENGINE_LOG, logpos[1])
        res = rec.result()
        res.update(name='+'.join(cards) if cards else '(control)', label=label, cards=cards, ids=ids, applied=rr.get('ok'), cardsHeld=held,
                   errors=errors, notes=notes + rec.notes, script={k: v for k, v in s.items() if v != DEFAULT[k]})
        return res

class _Shift:
    """Recorder clock continued after the driven part (settle)."""
    def __init__(self, rec, off): self.rec, self.off = rec, off
    def poll(self, t): self.rec.poll(self.off + t)

class Recorder:
    """Everything that reaches 'Cuphead' during one run, read every bench frame."""
    def __init__(self, bench, s, ids2g, start):
        self.b, self.s, self.ids2g, self.start = bench, s, ids2g, start
        self.hit_read = ri(R_HITWRITE)
        self.fx_on = FX_OFF > 0
        self.fx_read = ri(R + FX_OFF) if self.fx_on else 0
        self.sframe = ri(R_STATUSFRAME) if self.fx_on else 0
        self.frames = 0
        self.cur = {}
        self.hits, self.fx, self.series, self.probes = [], [], [], []
        self.status = {}
        self.ticks = 0
        self.lost = {'hits': 0, 'fx': 0}
        self.notes = []
        self.shots, self.presses, self.hurt_at = 0, [], []
        q = rslot(0)
        self.blocks0 = ri(q + Q_BLOCKS)
        self.p = dict(start=[rf(q + Q_X), rf(q + Q_Y)], hp0=rf(q + Q_HP), maxHp0Card=rf(q + Q_MAXHP), hpMin=1e9, hpMaxAfter=-1e9, hpAfterHurt=None,
                      maxHpMax=0.0, hpRise=0.0, xMin=1e9, xMax=-1e9, yMin=1e9, yMax=-1e9, peakY=-1e9, blockTimes=[], dead=0, maxDist=0.0)
        self.partner = None
        if bench.coop:
            q1 = rslot(1)
            self.partner = dict(hp0=rf(q1 + Q_HP), hpMin=1e9)

    def take_probe(self, t):
        for s in ([0, 1] if self.b.coop else [0]):
            pr = self.b.probe(s)
            if pr is None: continue
            pr['t'] = round(t, 2)
            self.probes.append(pr)

    def poll(self, t):
        self.ticks += 1
        # hit ring: ROUNDS writes an entry's serial (n + 1) last (0 while writing), so an entry counts only when its serial is
        # read + 1 before and after copying; a lapped ring resyncs to write - ring + 1 (the slot being written is left alone)
        def ring(write_off, base, size, ring_n, read, kind, take):
            w = ri(write_off)
            if w - read >= ring_n:
                self.lost[kind] += w - read - ring_n + 1; read = w - ring_n + 1
            while read < w:
                e = base + (read % ring_n) * size
                s1 = ri(e)
                if s1 == 0 or s1 < read + 1: break            # not written yet: next poll
                if s1 == read + 1:
                    item = take(e)
                    if ri(e) == s1: getattr(self, kind).append(item)
                    else: self.lost[kind] += 1                # overwritten while we copied it
                else: self.lost[kind] += 1                    # overwritten before we got to it
                read += 1
            return read
        def take_hit(e):
            hid = ri(e + 4)
            return dict(t=round(t, 3), tick=self.ticks, id=hid, g=self.ids2g.get(hid, hid), dmg=rf(e + 8), x=rf(e + 12), y=rf(e + 16),
                        slot=ri(e + 20), kind=ri(e + 24), rgba=ri(e + 28) & 0xFFFFFFFF)
        self.hit_read = ring(R_HITWRITE, R_HITS, HIT_SIZE, HIT_RING, self.hit_read, 'hits', take_hit)
        if self.fx_on:
            take_fx = lambda e: dict(t=round(t, 3), tick=self.ticks, g=ri(e + 4), type=ri(e + 8), a=rf(e + 12), b=rf(e + 16), c=rf(e + 20), d=rf(e + 24), slot=ri(e + 28))
            self.fx_read = ring(R + FX_OFF, R + FX_OFF + 4, FX_SIZE, FX_RING, self.fx_read, 'fx', take_fx)
            # status table: R_StatusFrame is a sequence lock (odd while ROUNDS writes); a copy counts only when the same even
            # value is read before and after it
            frame = ri(R_STATUSFRAME)
            if frame != self.sframe and frame % 2 == 0:
                n = min(max(0, ri(R_STATUSCOUNT)), MAX_STATUS)
                cur = {}
                for i in range(n):
                    e = R_STATUS + i * STATUS_SIZE
                    cur[ri(e)] = (rf(e + 4), rf(e + 8), rf(e + 12), rf(e + 16), ri(e + 20) & 0xFFFFFFFF, ri(e + 24), rf(e + 28))
                if ri(R_STATUSFRAME) == frame:
                    self.frames += 1
                    self.sframe = frame
                    self.cur = cur
                else: self.torn = getattr(self, 'torn', 0) + 1
            for g in set(self.cur) | set(self.ids2g.values()):
                v = self.cur.get(g, (0.0, 0.0, 0.0, 0.0, 0, 0, 0.0))
                self.series.append((round(t, 3), g, v[0], v[1], v[2], v[3]))
                if g not in self.cur: continue
                sm = self.status.setdefault(g, dict(slow=0.0, stun=0.0, silence=0.0, dot=0.0, aux=0.0, flags=0, dotRgba=[], slowSeconds=0.0))
                for k, x in zip(('slow', 'stun', 'silence', 'dot'), v[:4]): sm[k] = max(sm[k], x)
                sm['aux'] = max(sm['aux'], v[6])
                sm['flags'] |= v[5]
                if v[4] and v[4] not in sm['dotRgba']: sm['dotRgba'].append(v[4])
                if v[0] > 0: sm['slowSeconds'] += 1 / 60
        # the player (and partner)
        q = rslot(0)
        x, y, hp, mh = rf(q + Q_X), rf(q + Q_Y), rf(q + Q_HP), rf(q + Q_MAXHP)
        p = self.p
        p['hpMin'] = min(p['hpMin'], hp)
        if not self.hurt_at or t >= self.hurt_at[0] + 0.05:
            # the largest rise after a low point once hurt (lifesteal can start before the hurt shows in Q_Hp)
            if p['hpAfterHurt'] is None or hp < p['hpAfterHurt']: p['hpAfterHurt'] = hp; p['_peak'] = hp
            p['_peak'] = max(p.get('_peak', hp), hp)
            if p['_peak'] - p['hpAfterHurt'] > p['hpRise']: p['hpRise'] = p['_peak'] - p['hpAfterHurt']; p['hpMaxAfter'] = p['_peak']
        p['maxHpMax'] = max(p['maxHpMax'], mh)
        p['xMin'], p['xMax'], p['yMin'], p['yMax'] = min(p['xMin'], x), max(p['xMax'], x), min(p['yMin'], y), max(p['yMax'], y)
        p['peakY'] = max(p['peakY'], y)
        p['maxDist'] = max(p['maxDist'], math.hypot(x - self.start[0], y - (self.start[1] + 40)))
        p['dead'] = max(p['dead'], ri(q + Q_DEAD))
        nb = ri(q + Q_BLOCKS) - self.blocks0
        while len(p['blockTimes']) < nb: p['blockTimes'].append(round(t, 2))
        p['end'] = [x, y]
        p['hpEnd'] = hp
        if self.partner is not None:
            self.partner['hpMin'] = min(self.partner['hpMin'], rf(rslot(1) + Q_HP))

    def result(self):
        p = self.p
        if p['hpAfterHurt'] is None: p['hpAfterHurt'] = p['hp0']
        if p['hpMaxAfter'] < 0: p['hpMaxAfter'] = p['hpAfterHurt']
        p.pop('_peak', None)
        if self.lost['hits']: self.notes.append('hit ring overflow: %d hits lost' % self.lost['hits'])
        if self.lost['fx']: self.notes.append('effect ring overflow: %d events lost' % self.lost['fx'])
        if self.fx_on and self.frames == 0: self.notes.append('the status table never updated (engine without WriteEffects?)')
        byk = {}
        for h in self.hits:
            k = byk.setdefault(KIND.get(h['kind'], str(h['kind'])), dict(n=0, dmg=0.0, min=1e9, max=0.0, groups={}, colors=[]))
            k['n'] += 1; k['dmg'] += h['dmg']; k['min'] = min(k['min'], h['dmg']); k['max'] = max(k['max'], h['dmg'])
            k['groups'][h['g']] = k['groups'].get(h['g'], 0) + 1
            if h['rgba'] and '%08X' % h['rgba'] not in k['colors']: k['colors'].append('%08X' % h['rgba'])
        for k in byk.values(): k['dmg'] = round(k['dmg'], 2); k['min'] = round(k['min'], 2); k['max'] = round(k['max'], 2)
        byt = {}
        for e in self.fx:
            k = byt.setdefault(FXN.get(e['type'], str(e['type'])), dict(n=0, groups={}, maxA=-1e9, minA=1e9, maxB=-1e9, maxC=-1e9, maxD=-1e9, maxMag=0.0))
            k['n'] += 1; k['groups'][e['g']] = k['groups'].get(e['g'], 0) + 1
            k['maxA'] = max(k['maxA'], e['a']); k['minA'] = min(k['minA'], e['a']); k['maxB'] = max(k['maxB'], e['b'])
            k['maxC'] = max(k['maxC'], e['c']); k['maxD'] = max(k['maxD'], e['d']); k['maxMag'] = max(k['maxMag'], math.hypot(e['a'], e['b']))
        for k in byt.values():
            for f in ('maxA', 'minA', 'maxB', 'maxC', 'maxD', 'maxMag'): k[f] = round(k[f], 3)
        status = {g: dict(v, dotRgba=['%08X' % c for c in v['dotRgba']], slowSeconds=round(v['slowSeconds'], 2), **{f: round(v[f], 3) for f in ('slow', 'stun', 'silence', 'dot', 'aux')})
                  for g, v in self.status.items()}
        slim = [s for i, s in enumerate(self.series) if i % 6 == 0]   # ~10 Hz per group for the file
        return dict(
            hits={'total': len(self.hits), 'byKind': byk, 'shots': self.shots}, fx={'total': len(self.fx), 'byType': byt},
            status=status, statusFrames=self.frames, fxOn=self.fx_on, probeOn=self.b.probe_ok,
            player=dict((k, (round(v, 1) if isinstance(v, float) else v)) for k, v in p.items()), partner=self.partner,
            blockPresses=self.presses, probe=self.probes, seriesSample=slim[:600],
            hitsFirst=[dict(h, kind=KIND.get(h['kind'], h['kind']), rgba='%08X' % h['rgba'], dmg=round(h['dmg'], 2)) for h in self.hits[:40]],
            fxFirst=[dict(e, type=FXN.get(e['type'], e['type']), a=round(e['a'], 3), b=round(e['b'], 3), c=round(e['c'], 3), d=round(e['d'], 3)) for e in self.fx[:60]],
            _hits=self.hits, _fx=self.fx, _status=self.status, _series=self.series)

# ---------------------------------------------------------------- logs

def log_size(path):
    try: return os.path.getsize(path)
    except OSError: return 0

# PCE's own one-off at asset setup (once per ROUNDS session, the first time the card is used, next to its "prefab was
# already registered" warning): the template object it builds runs Start with no parent. Plain ROUNDS + PCE does the same.
PCE_STARTUP = ('CombEffect.Start ()', 'LaserHurtbox.Start ()')

def log_errors(path, since):
    try:
        with open(path, 'rb') as f:
            f.seek(since); t = f.read().decode('utf-8', 'replace')
    except OSError: return []
    out, lines = [], t.split('\n')
    for i, l in enumerate(lines):
        if ('Exception' in l or 'Error' in l) and 'Fallback handler' not in l:
            line = (l.strip() + ' | ' + (lines[i + 1].strip() if i + 1 < len(lines) else ''))[:300]
            if line not in out: out.append(line)
    return out[:12]

# ---------------------------------------------------------------- verdicts

class Ctx:
    def __init__(self, bench): self.bench, self.controls = bench, {}
    def control(self, r):
        key = r['_scriptKey']
        if key not in self.controls:
            sc = dict(label='control', checks=[], script=r['script'])
            self.controls[key] = self.bench.run([], sc, 'control')
            self.controls[key]['_scriptKey'] = key
        return self.controls[key]

def judge(r, checks, ctx):
    out, ok_all = [], True
    for c in checks:
        try: ok, detail = c.fn(r, ctx)
        except Exception as e: ok, detail = False, 'check failed: %r' % e
        out.append([bool(ok), c.desc, detail])
        ok_all &= bool(ok)
    return ('PASS' if ok_all else 'FAIL') if checks else 'INFO', out

def plan_for(cards, quick, combo_table):
    """[(label, scenario, signals)] for a card list."""
    keyset = frozenset(canon(c) for c in cards)
    for name, sig, scs in combo_table:
        if frozenset(canon(c) for c in name.split('+')) == keyset and len(cards) > 1:
            return [(sc['label'], sc, sig) for sc in (scs[:1] if quick else scs)]
    if len(cards) == 1 and canon(cards[0]) in EXPECT:
        sig, scs = EXPECT[canon(cards[0])]
        return [(sc['label'], sc, sig) for sc in (scs[:1] if quick else scs)]
    return [('', S('', [hits(1)]), 'no table entry: damage should reach the enemy (hits > 0)')]

def print_table():
    print('%-36s %s' % ('CARD', 'EXPECTED SIGNALS  ||  CHECKS'))
    for n in sorted(EXPECT):
        sig, scs = EXPECT[n]
        print('%-36s %s' % (n, sig))
        for sc in scs:
            print('%-36s   [%s] %s' % ('', sc['label'] or 'default', '; '.join(c.desc for c in sc['checks']) or '(info only)'))
    print()
    for n, sig, scs in COMBOS:
        print('%-36s %s' % (n, sig))
        for sc in scs:
            print('%-36s   [%s] %s' % ('', sc['label'] or 'default', '; '.join(c.desc for c in sc['checks'])))

def strip(r):
    return {k: v for k, v in r.items() if not k.startswith('_')}

def main():
    global m, sockf
    ap = argparse.ArgumentParser(description='ROUNDS card effect bench (see the module docstring)')
    ap.add_argument('out', nargs='?', help='result json')
    ap.add_argument('cards', nargs='*', help='card ids or names; "A+B" for a combo')
    ap.add_argument('--combos', action='store_true', help='also run the design COMBOS')
    ap.add_argument('--coop', action='store_true', help='partner (slot 1) between the player and A; adds partner leak checks')
    ap.add_argument('--quick', action='store_true', help='only the first scenario of each card')
    ap.add_argument('--stop-on-broken', action='store_true', help='stop when the bench looks broken')
    ap.add_argument('--list', action='store_true', help='print the expectations table and exit')
    args = ap.parse_args()
    if args.list or not args.out:
        print_table(); return

    m = mmap.mmap(-1, SIZE, tagname=MAP)
    port = int(open(os.path.join(DEV, 'engine.port')).read())
    sock = socket.create_connection(('127.0.0.1', port))
    sockf = sock.makefile('rw', encoding='utf-8', newline='\n')
    cat = call('catalog')
    for c in cat['cards']:
        nm = c['name'].lower()
        by_name[c['id'].lower()] = c['id']; by_name[nm] = c['id']
        alias[c['id'].lower()] = nm; alias[nm] = nm
        names_of[c['id']] = c['name']

    # Cuphead's side of the link: header, camera (view x -640..640, y -60..660), level, players
    wi(C_MAGIC, 0x43524133); wi(C_PID, os.getpid()); wi(C_MODE, 1); wi(C_PAUSED, 0)
    wf(C_CAMX, 0); wf(C_CAMY, 300); wf(C_ORTHO, 360); wf(C_ASPECT, 16 / 9); wi(C_FRAMEW, 640); wi(C_FRAMEH, 360); wf(C_SCALE, 37.0)
    wi(C_LEVEL, ri(C_LEVEL) + 1)
    wi(slot(0) + S_PRESENT, 1); wi(slot(1) + S_PRESENT, 1 if args.coop else 0)
    bench = Bench(args.coop)
    bench.wait(1.0)
    if args.coop:
        t0 = time.time()
        while ri(rslot(1) + Q_PRESENT) == 0 and time.time() - t0 < 15: bench.wait(0.2)
        if ri(rslot(1) + Q_PRESENT) == 0: print('WARNING: the partner (slot 1) never appeared', flush=True)
        call('arena_cards', slot=1, owned=[])
    ctx = Ctx(bench)

    jobs = []
    for n in args.cards:
        jobs.append(n.split('+'))
    if args.combos:
        jobs += [n.split('+') for n, sig, scs in COMBOS]
    if not args.cards and not args.combos:
        jobs = [[c['id']] for c in cat['cards']]

    warnings, results = [], []
    def warn(msg):
        warnings.append(msg); print('WARNING: ' + msg, flush=True)

    base = bench.run([], dict(label='control', checks=[], script={}), 'control')
    base['_scriptKey'] = script_key(script_of({}))
    ctx.controls[base['_scriptKey']] = base
    print('control: %d hits, %.1f dmg, %d fx, %d status frames%s' % (base['hits']['total'], sum(h['dmg'] for h in base['_hits']), len(base['_fx']), base['statusFrames'],
                                                                     '' if bench.probe_ok else ' (no arena_probe in this engine)'), flush=True)
    if base['hits']['total'] == 0: warn('the control run has no hits: is the engine in the arena and the player alive?')
    base_hits = max(1, base['hits']['total'])
    zero_streak, runs = 0, 0
    out_doc = lambda: dict(control=strip(base), results=[strip(r) for r in results], warnings=warnings, summary=summarize(results))

    stop = False
    for cards in jobs:
        for label, sc, sig in plan_for(cards, args.quick, COMBOS):
            checks = sc['checks'] + [no_exc()] + (partner_checks() if args.coop else [])
            try:
                r = bench.run(cards, sc, label)
                r['_scriptKey'] = script_key(adjust(script_of(sc), cards))
                r['verdict'], r['checks'] = judge(r, checks, ctx)
            except Exception as e:
                r = dict(name='+'.join(cards), label=label, cards=cards, failed=repr(e), verdict='FAIL', checks=[[False, 'run', repr(e)]], hits={'total': 0})
            r['signals'] = sig
            results.append(r)
            runs += 1
            fails = [c for c in r['checks'] if not c[0]]
            print('%-4s %-40s %s' % (r['verdict'], (r['name'] + (' [' + label + ']' if label else ''))[:40],
                                     ('; '.join('%s: %s' % (c[1], c[2]) for c in fails[:3])) if fails else '(%d checks)' % len(r['checks'])), flush=True)
            for n in r.get('notes', []): print('       note: ' + n, flush=True)
            # is the bench itself still sane?
            pl = r.get('player') or {}
            s = adjust(script_of(sc), cards)
            if pl:
                if pl.get('dead'): warn('%s: the player died during the run' % r['name'])
                if not s['roams'] and pl.get('maxDist', 0) > 600: warn('%s: the player went %.0f units away from its start (ended at %s)' % (r['name'], pl['maxDist'], pl.get('end')))
                if pl.get('yMin', 0) < -400 or pl.get('yMax', 0) > 2000: warn('%s: the player left the level (y %.0f..%.0f)' % (r['name'], pl['yMin'], pl['yMax']))
            expects_hits = s['shoot'] and isinstance(s['aim'], str) and not s['aim_offset']
            if expects_hits and r['hits']['total'] == 0:
                zero_streak += 1
                if zero_streak == 3: warn('3 runs in a row without any hit (last: %s): the bench may be broken; restart the engine (rounds_dev.sh)' % r['name'])
            else: zero_streak = 0
            if runs % 10 == 0:
                c = bench.run([], dict(label='control', checks=[], script={}), 'control')
                if c['hits']['total'] < 0.8 * base_hits:
                    warn('control after %d runs: %d hits against %d at the start: a card left the bench broken; restart the engine' % (runs, c['hits']['total'], base_hits))
                    if args.stop_on_broken: stop = True
            if args.stop_on_broken and zero_streak >= 3: stop = True
            json.dump(out_doc(), open(args.out, 'w'), indent=1, default=str)
            if stop: break
        if stop: print('stopped: the bench looks broken', flush=True); break

    call('arena_cards', slot=0, owned=[])
    wi(C_MODE, 0)
    summ = summarize(results)
    json.dump(out_doc(), open(args.out, 'w'), indent=1, default=str)
    print('\nSUMMARY: %d PASS, %d FAIL, %d INFO of %d runs' % (summ['pass'], summ['fail'], summ['info'], len(results)))
    for f in summ['failed']: print('  FAIL %-38s %s' % (f['name'][:38], f['why'][:150]))
    if warnings:
        print('%d warnings (bench health):' % len(warnings))
        for w in warnings: print('  ' + w)

def summarize(results):
    p = sum(1 for r in results if r.get('verdict') == 'PASS')
    f = [r for r in results if r.get('verdict') == 'FAIL']
    i = sum(1 for r in results if r.get('verdict') == 'INFO')
    return {'pass': p, 'fail': len(f), 'info': i,
            'failed': [{'name': r['name'] + (' [' + r['label'] + ']' if r.get('label') else ''),
                        'why': '; '.join('%s: %s' % (c[1], c[2]) for c in r['checks'] if not c[0])} for r in f]}

if __name__ == '__main__':
    main()
