using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// ROUNDS statuses shown on one Cuphead enemy. One component sits on each enemy owner (its AbstractLevelEntity, else
    /// the DamageReceiver's GameObject) and ArenaClient feeds it every level frame from ROUNDS's status table and effect
    /// ring (sheets/ipc.json "arena"). ROUNDS stays the authority on amounts and timers; this side only shows them, with
    /// Cuphead's own Super hit-pause pieces that every boss already tolerates (DamageReceiver.pauseAnim_cr,
    /// DamageReceiver.cs:71-103): AnimationHelper.Speed or Animator.speed on the receiver's animators and
    /// animatorsEffectedByPause, and IsHitPaused, which about 20 movers read (GroundHomingMovement.cs:56).
    /// Slow scales them and the enemy's own coroutine waits (<see cref="Dilate"/>), stun freezes them through a boss
    /// governor, silence turns the enemy's new projectiles into duds, poison, frost and gold tint its sprites, and forces
    /// move light enemies only (bosses flinch). Boss transforms, global time and Super damage are never touched, and
    /// everything is restored when a status ends, on death, phase change, KO, level end, staleness and while ROUNDS is
    /// not live.
    /// </summary>
    public class EnemyStatus : MonoBehaviour
    {
        // ------------------------------------------------------------------ registry (per level)

        /// <summary>Enemies showing something right now (the waits and silence patches only look at these).</summary>
        public static readonly List<EnemyStatus> Active = new List<EnemyStatus>();
        /// <summary>KO, win, lose or level end: nothing new is applied and every enemy plays at full speed.</summary>
        public static bool Suspended;
        /// <summary>Cuphead game seconds (CupheadTime.Delta); stands still while the game is paused.</summary>
        public static float Clock;
        /// <summary>Counters for the TestMode bench: every CupheadTime.WaitForSeconds call seen (inlining check), dilated
        /// waits, and enemy projectiles turned into duds.</summary>
        public static int WaitCalls, DilatedWaits, DudShots;

        /// <summary>Clock, not Time.time: Cuphead never touches Time.timeScale, its pause menu and parry hit-stop only stop
        /// CupheadTime (PauseManager.cs:33-48, AbstractParryEffect.cs:268), so a Time.time guard ran out during a pause.</summary>
        private static float bossImmuneUntil = -100f;
        private static int pendingMurder;
        private static bool reportedStatus, reportedWait, reportedDud;

        /// <summary>What Cuphead says about one receiver: boss (LevelProperties.X.Entity), platforming minion, walker, size;
        /// mover: a platforming type whose own script moves it every frame; carries: its owner holds a collider ArenaClient
        /// mirrors as level geometry (floor, platform, wall).</summary>
        private class Info
        {
            public bool boss, platforming, ground, mover, carries;
            public float size;
            public AbstractPlatformingLevelEnemy enemy;
            public readonly List<Collider2D> cols = new List<Collider2D>();
        }
        private static readonly Dictionary<DamageReceiver, Info> infos = new Dictionary<DamageReceiver, Info>();
        private static readonly Dictionary<DamageReceiver, EnemyStatus> owners = new Dictionary<DamageReceiver, EnemyStatus>();

        private static readonly Color Ice = new Color(0.8f, 0.9f, 1f), Frost = new Color(0.55f, 0.8f, 1f), Poison = new Color(0.29f, 0.84f, 0.32f),
            Gold = new Color(1f, 0.85f, 0.35f), Daze = new Color(1f, 1f, 0.5f), Hush = new Color(0.7f, 0.7f, 0.8f), StunStars = new Color(1f, 0.95f, 0.55f);
        private const float DotPulseTime = 0.35f, PopCooldown = 0.6f, PushSkin = 4f;
        // Cuphead's wall (18, 19) and floor (20) layers, as ArenaClient.Scan sorts them.
        private const int WallMask = (1 << 18) | (1 << 19) | (1 << 20);

        // ------------------------------------------------------------------ one enemy

        private class Anim
        {
            public Animator a;
            public AnimationHelper h;
            public float baseSpeed = 1f, wrote;
            public bool own;
        }

        private readonly List<Anim> anims = new List<Anim>();
        public readonly List<DamageReceiver> receivers = new List<DamageReceiver>();
        private SpriteRenderer[] rends = new SpriteRenderer[0];
        private Color[] baseC = new Color[0], wroteC = new Color[0];
        private bool boss, ground, inList, seen;
        // this frame's merged ROUNDS values (raw, as in the status table)
        private float slow, stun, silence, dot, aux;
        private int dotRgba, flags;
        // local state, timed on Clock
        private float episode, budget, budgetAt, immuneUntil, flinchUntil, nextFlinch, duty, dotPulse, flipStunUntil;
        private bool held, frozen, stunned, dutyHold, wasFlipped;
        private Vector2 pushV;
        private readonly float[] nextPop = new float[12];

        public bool Boss => boss;

        /// <summary>The enemy's status component (one per owner, shared by all its receivers).</summary>
        public static EnemyStatus For(DamageReceiver r)
        {
            EnemyStatus s;
            if (owners.TryGetValue(r, out s) && s != null) return s;
            GameObject go = OwnerOf(r);
            s = go.GetComponent<EnemyStatus>();
            if (s == null)
            {
                s = go.AddComponent<EnemyStatus>();
                s.Setup();
            }
            if (!s.receivers.Contains(r)) s.AddReceiver(r);
            owners[r] = s;
            return s;
        }

        private void Setup()
        {
            TakeRenderers();
            budgetAt = Clock;
        }

        private float rendsAt = -100f;

        /// <summary>The sprites to tint. Only taken while nothing is shown (all colours are the game's own then).</summary>
        private void TakeRenderers()
        {
            rends = GetComponentsInChildren<SpriteRenderer>(true);
            baseC = new Color[rends.Length];
            wroteC = new Color[rends.Length];
            for (int i = 0; i < rends.Length; i++) baseC[i] = wroteC[i] = rends[i].color;
            rendsAt = Clock;
        }

        private void AddReceiver(DamageReceiver r)
        {
            receivers.Add(r);
            var inf = InfoOf(r);
            boss |= inf.boss;
            ground |= inf.ground;
            // The animators a Super hit freezes (DamageReceiver.cs:73-85), then the owner's own.
            AddAnim(r.animator);
            if (r.animatorsEffectedByPause != null)
                foreach (var h in r.animatorsEffectedByPause) if (h != null) AddAnim(h.GetComponent<Animator>());
            AddAnim(GetComponent<Animator>());
            if (anims.Count == 0) AddAnim(GetComponentInChildren<Animator>());
        }

        private void AddAnim(Animator a)
        {
            if (a == null) return;
            for (int i = 0; i < anims.Count; i++) if (anims[i].a == a) return;
            anims.Add(new Anim { a = a, h = a.GetComponent<AnimationHelper>() });
        }

        /// <summary>One status-table row for one of this enemy's receivers (several receivers merge by max).</summary>
        public void Merge(float sl, float st, float si, float d, int rgba, int fl, float ax)
        {
            if (Dead) return;
            sl = Fin(sl); st = Fin(st); si = Fin(si); d = Fin(d); ax = Fin(ax);
            seen = true;
            slow = Mathf.Max(slow, sl);
            stun = Mathf.Max(stun, st);
            silence = Mathf.Max(silence, si);
            if (d > dot) { dot = d; if (rgba != 0) dotRgba = rgba; }
            flags |= fl;
            aux = Mathf.Max(aux, ax);
            Enlist();
            if (!reportedStatus && TestMode.Active && (sl > 0f || st > 0f || si > 0f || d > 0f || fl != 0))
            {
                reportedStatus = true;
                TestMode.Report("enemy-status", true, gameObject.name + " shows ROUNDS statuses (slow " + sl.ToString("0.00") + ", stun " + st.ToString("0.00") + ", silence " + si.ToString("0.00") + ", dot " + d.ToString("0.00") + ")");
            }
        }

        /// <summary>A ROUNDS damage-over-time tick landed: pulse the poison tint before (or without) a status row.</summary>
        public void DotTick(int rgba)
        {
            if (Suspended || Dead) return;
            if (rgba != 0) dotRgba = rgba;
            dotPulse = Clock + DotPulseTime;
            Enlist();
        }

        private void Enlist()
        {
            if (inList) return;
            // Everything was restored while idle, so the sprite list can be taken afresh (bosses grow parts between phases).
            if (Clock - rendsAt > 2f) TakeRenderers();
            inList = true;
            Active.Add(this);
        }

        private void Delist()
        {
            if (!inList) return;
            inList = false;
            Active.Remove(this);
        }

        /// <summary>The effective slow fraction after ROUNDS's 0.9 cap (CharacterStatModifiers.GetSlow), PCE Gold and
        /// Grounded minimums, and the balance scaling for bosses.</summary>
        private float EffSlow
        {
            get
            {
                float s = Mathf.Clamp(slow, 0f, 0.9f);
                if ((flags & ArenaLink.StGold) != 0) s = Mathf.Max(s, Tuning.goldSlow);
                if ((flags & ArenaLink.StHeavy) != 0) s = Mathf.Max(s, Tuning.heavySlow);
                return Mathf.Clamp01(s * (boss ? Tuning.bossSlowScale : Tuning.enemySlowScale));
            }
        }

        /// <summary>How fast this enemy's own time runs: 0 while frozen, 1 - slow while slowed (read by the waits patch).</summary>
        public float Speed => frozen ? 0f : 1f - EffSlow;

        public bool Silenced => silence > 0f;

        /// <summary>Every receiver gone, disabled or dead: its death animation plays at full speed.</summary>
        private bool Dead
        {
            get
            {
                for (int i = 0; i < receivers.Count; i++)
                {
                    var r = receivers[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var inf = InfoOf(r);
                    if (inf.enemy != null && inf.enemy.Dead) continue;
                    return false;
                }
                return true;
            }
        }

        /// <summary>Light enemies (small self-moving platforming minions and small flyers) may be displaced (<see cref="LightInfo"/>).</summary>
        private bool Light
        {
            get
            {
                if (boss) return false;
                bool any = false;
                for (int i = 0; i < receivers.Count; i++)
                {
                    if (receivers[i] == null) continue;
                    if (!LightInfo(InfoOf(receivers[i]))) return false;
                    any = true;
                }
                return any;
            }
        }

        /// <summary>Something still runs without a status row: a push, a flinch, a DoT pulse or a Flip stagger.</summary>
        private bool Busy => pushV != Vector2.zero || Clock < flinchUntil || Clock < dotPulse || Clock < flipStunUntil;

        /// <summary>
        /// Stun with the governor: a freeze lasts at most enemyStunCap (bossStunCap) per episode, and a boss that spent
        /// bossStunBudget frozen within about bossStunWindow becomes immune for bossStunImmunity, so Dazzle and Spray
        /// stuns or chained block stuns can never lock a boss.
        /// </summary>
        private bool StunNow(float dt)
        {
            float since = Mathf.Max(0f, Clock - budgetAt);
            budgetAt = Clock;
            bool want = stun > 0f || Clock < flipStunUntil;
            // The budget refills while not frozen, including time spent without any status.
            float idle = want ? Mathf.Max(0f, since - dt) : since;
            budget = Mathf.Max(0f, budget - idle * Tuning.bossStunBudget / Mathf.Max(0.01f, Tuning.bossStunWindow));
            if (!want) { episode = 0f; return false; }
            if (Clock < immuneUntil || episode >= (boss ? Tuning.bossStunCap : Tuning.enemyStunCap)) return false;
            episode += dt;
            if (boss)
            {
                budget += dt;
                if (budget >= Tuning.bossStunBudget) { budget = 0f; immuneUntil = Clock + Tuning.bossStunImmunity; }
            }
            return true;
        }

        private void FixedUpdate()
        {
            if (!inList || CupheadTime.IsPaused()) return;
            try
            {
                // Duty-cycled IsHitPaused slows the physics-step movers by the slow fraction.
                float s = EffSlow;
                if (Tuning.slowDutyCycle > 0f && s > 0f)
                {
                    duty += s;
                    dutyHold = duty >= 1f;
                    if (dutyHold) duty -= 1f;
                }
                else { duty = 0f; dutyHold = false; }
                SetHold(frozen || dutyHold);
            }
            catch (Exception e) { LogOnce("enemy status step", e); }
        }

        private void LateUpdate()
        {
            if (!inList || CupheadTime.IsPaused()) return;
            try
            {
                if (Dead) { Restore(); return; }
                float dt = CupheadTime.Delta[CupheadTime.Layer.Default];
                bool flipped = (flags & ArenaLink.StFlipped) != 0 && aux > 0f;
                if (flipped && !wasFlipped) flipStunUntil = Mathf.Max(flipStunUntil, Clock + Tuning.flipStun);
                wasFlipped = flipped;
                stunned = StunNow(dt);
                frozen = stunned || Clock < flinchUntil;
                if (EffSlow <= 0f) { dutyHold = false; duty = 0f; }
                // Re-applied every frame: a Super pause puts Speed back to 1 after 0.15 s (DamageReceiver.cs:89-102).
                ApplySpeed(Speed);
                SetHold(frozen || (Tuning.slowDutyCycle > 0f && dutyHold));
                Push(dt);
                Tint();
            }
            catch (Exception e) { LogOnce("enemy status", e); }
        }

        private void ApplySpeed(float sp)
        {
            for (int i = 0; i < anims.Count; i++)
            {
                var a = anims[i];
                if (a.a == null) continue;
                bool helper = a.h != null;
                float cur = helper ? a.h.Speed : a.a.speed;
                // The game changed the speed since our last write (a Super pause ended, a script sped it up): re-base.
                if (!a.own || Mathf.Abs(cur - a.wrote) > 1e-4f) a.baseSpeed = cur;
                float want = a.baseSpeed * sp;
                // AnimationHelper.Set rewrites animator.speed from Speed (AnimationHelper.cs:99-109), so write the helper.
                if (Mathf.Abs(cur - want) > 1e-4f) { if (helper) a.h.Speed = want; else a.a.speed = want; }
                a.wrote = want;
                a.own = true;
            }
        }

        private void SetHold(bool hold)
        {
            if (!hold && !held) return;
            for (int i = 0; i < receivers.Count; i++)
            {
                var r = receivers[i];
                if (r != null && r.IsHitPaused != hold) SetPaused(r, hold);
            }
            held = hold;
        }

        private void Tint()
        {
            Color c = Color.white;
            float k = Tuning.statusTintStrength, es = EffSlow;
            if (stunned) c = Color.Lerp(Color.white, Ice, 0.4f + 0.6f * Mathf.PingPong(Clock * 8f, 1f));
            else if (dot > 0f || Clock < dotPulse) c = Color.Lerp(Color.white, ColorOf(dotRgba, Poison), k);
            else if ((flags & ArenaLink.StGold) != 0) c = Color.Lerp(Color.white, Gold, k);
            else if ((flags & ArenaLink.StConfused) != 0) c = Color.Lerp(Color.white, Daze, k);
            else if (silence > 0f) c = Color.Lerp(Color.white, Hush, k);
            else if (es > 0f) c = Color.Lerp(Color.white, Frost, k * Mathf.Clamp01(es / 0.5f));
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                Color now = r.color;
                // The game changed the colour since our last write (HitFlash.cs:114-125, animated alpha): re-base on it.
                if (now != wroteC[i]) baseC[i] = now;
                Color w = baseC[i] * c;
                w.a = baseC[i].a;
                if (w != now) r.color = w;
                wroteC[i] = w;
            }
        }

        /// <summary>Back to how the game left it: speeds, IsHitPaused and colours the game has not changed since, and every
        /// shown status (gold, poison and its colour included) dropped. Also runs when the owner deactivates (OnDisable) or
        /// its last live receiver does (Dead, LateUpdate), so a reused enemy (Martini olives, DicePalaceBoozeLevelMartini.cs:
        /// 136-138; ObjectPool.Recycle/Spawn) comes back clean.</summary>
        public void Restore()
        {
            try
            {
                for (int i = 0; i < anims.Count; i++)
                {
                    var a = anims[i];
                    if (a.a == null || !a.own) { a.own = false; continue; }
                    bool helper = a.h != null;
                    float cur = helper ? a.h.Speed : a.a.speed;
                    if (Mathf.Abs(cur - a.wrote) <= 1e-4f) { if (helper) a.h.Speed = a.baseSpeed; else a.a.speed = a.baseSpeed; }
                    a.own = false;
                }
                SetHold(false);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i];
                    if (r == null) continue;
                    if (r.color == wroteC[i]) r.color = baseC[i];
                    baseC[i] = wroteC[i] = r.color;
                }
            }
            catch (Exception e) { LogOnce("enemy restore", e); }
            held = frozen = stunned = dutyHold = wasFlipped = false;
            duty = episode = 0f;
            slow = stun = silence = dot = aux = 0f;
            flags = dotRgba = 0;
            flinchUntil = dotPulse = flipStunUntil = 0f;
            pushV = Vector2.zero;
            Delist();
        }

        private void OnDisable() { Restore(); }

        private void OnDestroy() { Active.Remove(this); }

        // ------------------------------------------------------------------ forces (FxForce, FxPull, FxGravity)

        /// <summary>
        /// A ROUNDS velocity change in Cuphead units/s. A light enemy takes it as a decaying push; a boss or heavy enemy
        /// (a stationary or scripted platforming enemy, anything big, anything carrying level geometry) never moves and a
        /// strong impulse becomes Cuphead's own 0.15 s enemy hit-pause flinch (DamageReceiver.ENEMY_HIT_PAUSE_TIME), with a
        /// cooldown.
        /// </summary>
        public void AddPush(Vector2 dv)
        {
            dv = new Vector2(Fin(dv.x), Fin(dv.y));
            if (dv == Vector2.zero || Dead) return;
            if (!Light)
            {
                if (dv.magnitude >= Tuning.flinchThreshold && Clock >= nextFlinch)
                {
                    flinchUntil = Clock + Tuning.flinchTime;
                    nextFlinch = Clock + Tuning.flinchCooldown;
                    Enlist();
                }
                return;
            }
            pushV = Vector2.ClampMagnitude(pushV + dv * Tuning.enemyPushScale, Tuning.enemyPushMaxSpeed);
            Enlist();
        }

        /// <summary>A pull toward a point (Implode): the same impulse as a ROUNDS force on a 100-mass body.</summary>
        public void PullTo(Vector2 point, float roundsForce, float secs)
        {
            if (float.IsNaN(point.x) || float.IsNaN(point.y)) return;
            Vector2 dir = point - Centre;
            if (dir.sqrMagnitude < 1f) return;
            AddPush(dir.normalized * Mathf.Abs(roundsForce) / 100f * Tuning.moveWorldScale);
        }

        /// <summary>PCE Flip (gravity below 0) staggers anything briefly and makes a light flyer hop; Grounded shows through
        /// the Heavy flag as a slow.</summary>
        public void Gravity(float mul, float secs)
        {
            if (mul >= 0f || Dead) return;
            flipStunUntil = Mathf.Max(flipStunUntil, Clock + Tuning.flipStun);
            Enlist();
            if (Light && !ground) AddPush(Vector2.up * Tuning.flipHopSpeed / Mathf.Max(0.01f, Tuning.enemyPushScale));
        }

        private void Push(float dt)
        {
            if (pushV == Vector2.zero) return;
            if (pushV.sqrMagnitude < 1f || !Light) { pushV = Vector2.zero; return; }
            Vector2 step = pushV * dt;
            if (ground) step.y = 0f;
            float len = step.magnitude;
            if (len > 0.001f)
            {
                // Stop at Cuphead walls and floors instead of passing through them.
                Vector2 dir = step / len;
                float allowed = len;
                foreach (var h in Physics2D.RaycastAll(Centre, dir, len + PushSkin, WallMask))
                    if (h.collider != null && !h.collider.isTrigger && h.distance > 0.01f) allowed = Mathf.Min(allowed, Mathf.Max(0f, h.distance - PushSkin));
                if (allowed < len) { step = dir * allowed; pushV = Vector2.zero; }
            }
            Vector3 old = transform.position, p = old + (Vector3)step;
            var lv = Level.Current;
            if (lv != null)
            {
                // Inside the level bounds; something already outside only stops moving further out.
                float minX = lv.Left + 40f, maxX = lv.Right - 40f, minY = lv.Ground, maxY = lv.Ceiling;
                if (step.x < 0f && p.x < minX) p.x = Mathf.Max(p.x, Mathf.Min(old.x, minX));
                if (step.x > 0f && p.x > maxX) p.x = Mathf.Min(p.x, Mathf.Max(old.x, maxX));
                if (step.y < 0f && p.y < minY) p.y = Mathf.Max(p.y, Mathf.Min(old.y, minY));
                if (step.y > 0f && p.y > maxY) p.y = Mathf.Min(p.y, Mathf.Max(old.y, maxY));
            }
            transform.position = p;
            ShiftPath(p - old);
            pushV *= Mathf.Exp(-Tuning.enemyPushDrag * dt);
        }

        private static FieldInfo pathOffset;
        private PlatformingLevelPathMovementEnemy path;
        private bool pathTried;

        /// <summary>A path mover sets its position to _offset + path.Lerp(t) every step (PlatformingLevelPathMovementEnemy.cs:
        /// 34, 66, 149-151): the push moves the path with it, or the next step would undo it.</summary>
        private void ShiftPath(Vector3 d)
        {
            if (!pathTried)
            {
                pathTried = true;
                path = GetComponent<PlatformingLevelPathMovementEnemy>();
                if (path != null && pathOffset == null) pathOffset = AccessTools.Field(typeof(PlatformingLevelPathMovementEnemy), "_offset");
            }
            if (path == null || pathOffset == null || d == Vector3.zero) return;
            pathOffset.SetValue(path, (Vector3)pathOffset.GetValue(path) + d);
        }

        private Vector2 Centre
        {
            get
            {
                Bounds b;
                return BoundsOf(out b) ? (Vector2)b.center : (Vector2)transform.position;
            }
        }

        /// <summary>The union of this enemy's live hit areas.</summary>
        private bool BoundsOf(out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            for (int i = 0; i < receivers.Count; i++)
            {
                if (receivers[i] == null) continue;
                var cols = InfoOf(receivers[i]).cols;
                for (int k = 0; k < cols.Count; k++)
                {
                    var c = cols[k];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            }
            return any;
        }

        /// <summary>A projectile that appears inside (or within silenceSpawnPad of) one of this enemy's hit areas or its drawn
        /// body: attacks leave a mouth or a hand that the hit area often does not cover (the potato's spit starts 68 units
        /// outside its hit area, VeggiesLevelPotato.gunRoot).</summary>
        private bool SpawnedBy(Vector3 p)
        {
            for (int i = 0; i < receivers.Count; i++)
            {
                if (receivers[i] == null) continue;
                var cols = InfoOf(receivers[i]).cols;
                for (int k = 0; k < cols.Count; k++)
                {
                    var c = cols[k];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    if (Near(c.bounds, p)) return true;
                }
            }
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null || !r.enabled || r.sprite == null || !r.gameObject.activeInHierarchy) continue;
                if (Near(r.bounds, p)) return true;
            }
            return false;
        }

        private static bool Near(Bounds b, Vector3 p)
        {
            b.Expand(Tuning.silenceSpawnPad * 2f);
            return b.Contains(new Vector3(p.x, p.y, b.center.z));
        }

        // ------------------------------------------------------------------ one-shot bursts

        /// <summary>A short ring on the enemy when a status lands (frost, stun, silence, poison, gold, confuse); the lasting
        /// look comes from the status table.</summary>
        public void Pop(int type, float a)
        {
            if (type < 0 || type >= nextPop.Length || Clock < nextPop[type] || Dead) return;
            Color c;
            switch (type)
            {
                case ArenaLink.FxSlow: c = Frost; break;
                case ArenaLink.FxStun: c = StunStars; break;
                case ArenaLink.FxSilence: c = Hush; break;
                case ArenaLink.FxConfuse: c = Daze; break;
                case ArenaLink.FxDot: c = ColorOf(dotRgba, Poison); break;
                case ArenaLink.FxGold: c = Gold; break;
                default: return;
            }
            nextPop[type] = Clock + PopCooldown;
            Bounds b;
            if (!BoundsOf(out b)) return;
            SpriteRenderer reference = null;
            for (int i = 0; i < rends.Length && reference == null; i++) if (rends[i] != null && rends[i].enabled && rends[i].gameObject.activeInHierarchy) reference = rends[i];
            StatusPop.Spawn(b.center, Mathf.Clamp(Mathf.Max(b.size.x, b.size.y) * 0.8f, 80f, 360f), c, reference);
        }

        // ------------------------------------------------------------------ flags sent to ROUNDS (K_Flags)

        /// <summary>The collider entry's flags: Light or Boss (fixed per receiver; Light is lost once a hit area larger than
        /// lightEnemyMaxSize is seen) and NoStatus (every frame).</summary>
        public static int Flags(DamageReceiver r, Collider2D col)
        {
            var inf = InfoOf(r);
            if (col != null && col.enabled && col.gameObject.activeInHierarchy)
            {
                Vector3 sz = col.bounds.size;
                inf.size = Mathf.Max(inf.size, Mathf.Max(sz.x, sz.y));
            }
            int f = inf.boss ? ArenaLink.TfBoss : LightInfo(inf) ? ArenaLink.TfLight : 0;
            if (NoStatusNow(inf)) f |= ArenaLink.TfNoStatus;
            return f;
        }

        /// <summary>Remembers a hit area of a receiver (silence spawn test, push centre, bursts).</summary>
        public static void NoteCollider(DamageReceiver r, Collider2D col)
        {
            if (r == null || col == null) return;
            var cols = InfoOf(r).cols;
            if (!cols.Contains(col)) cols.Add(col);
        }

        /// <summary>
        /// ROUNDS applies no new statuses or forces while this is true (damage still lands): during the intro, at KO and
        /// level end, while Suspended, and for bosses during phase-change immunity, so a frozen animation can never stall
        /// Cuphead's level script.
        /// </summary>
        public static bool NoStatusNow(bool boss)
        {
            var lv = Level.Current;
            return Suspended || lv == null || !lv.Started || lv.Ending || (boss && Clock < bossImmuneUntil);
        }

        /// <summary>One receiver's no-status window: the level's (<see cref="NoStatusNow(bool)"/>), a boss's phase-change
        /// immunity, or a platforming enemy already dead (TfNoStatus, read by ROUNDS and the dev status bench).</summary>
        public static bool NoStatusNow(DamageReceiver r) => r == null || NoStatusNow(InfoOf(r));

        private static bool NoStatusNow(Info inf) => NoStatusNow(inf.boss) || (inf.enemy != null && inf.enemy.Dead);

        /// <summary>A boss within phase-change immunity: ROUNDS's leftover rows for it are ignored.</summary>
        public static bool Immune(DamageReceiver r) => r != null && Clock < bossImmuneUntil && InfoOf(r).boss;

        /// <summary>
        /// Light (pushed, not flinched): never a boss, never an owner that carries level geometry, no hit area larger than
        /// lightEnemyMaxSize, and for a platforming enemy only a type that moves itself, so the push blends into its own
        /// motion. Stationary and scripted ones keep their spot: PlatformingLevelShootingEnemy and its subclasses
        /// (AcornMaker, Lobber, Mushroom, BigEnemy...) write no position at all, and the rest set it from fixed points or a
        /// pivot (Clam, Lobster, Dragon, Woodpecker, Pretzel, Starfish, Flamer, Magician, Tuba, Chomper) or hold the
        /// rideable platform (MountainPlatformingLevelWall.cs:10, 56, 84).
        /// </summary>
        private static bool LightInfo(Info i) => !i.boss && !i.carries && i.size <= Tuning.lightEnemyMaxSize && (!i.platforming || i.mover);

        /// <summary>Platforming enemies whose own script moves them from where they are every step: ground walkers
        /// (velocity, PlatformingLevelGroundMovementEnemy.cs:340), path movers (their path moves with the push,
        /// <see cref="ShiftPath"/>), and these, which add their velocity to their position (Balloon.cs:79-93,
        /// Acorn.cs:112-145, Duck.cs:77, Jack.cs:75, Fish.cs:75, Krill.cs:37). Pole bots and totem logs are not: they sit in
        /// fixed slots of a column and only slide down a slot when the one below dies (PoleHandler.cs:27-30, PoleBot.cs:51-60,
        /// LogHandler.cs:61-73, Log.cs:61-70), so a push would leave them out of the column.</summary>
        private static readonly HashSet<Type> selfMovers = new HashSet<Type>
        {
            typeof(PlatformingLevelGroundMovementEnemy), typeof(PlatformingLevelPathMovementEnemy),
            typeof(CircusPlatformingLevelBalloon), typeof(ForestPlatformingLevelAcorn),
            typeof(FunhousePlatformingLevelDuck), typeof(FunhousePlatformingLevelJack), typeof(HarbourPlatformingLevelFish),
            typeof(HarbourPlatformingLevelKrill)
        };

        private static bool Moves(AbstractPlatformingLevelEnemy e)
        {
            for (Type t = e != null ? e.GetType() : null; t != null && t != typeof(AbstractPlatformingLevelEnemy); t = t.BaseType)
                if (selfMovers.Contains(t)) return true;
            return false;
        }

        private static readonly List<Collider2D> shapeBuf = new List<Collider2D>();

        /// <summary>The owner (what a push moves) holds a collider on Cuphead's wall or floor layers, which ArenaClient.Scan
        /// mirrors into ROUNDS as walls, floors and platforms the players stand on.</summary>
        private static bool CarriesShapes(GameObject owner)
        {
            owner.GetComponentsInChildren(true, shapeBuf);
            bool any = false;
            for (int k = 0; k < shapeBuf.Count && !any; k++) any = shapeBuf[k] != null && (WallMask & (1 << shapeBuf[k].gameObject.layer)) != 0;
            shapeBuf.Clear();
            return any;
        }

        private static Info InfoOf(DamageReceiver r)
        {
            Info i;
            if (infos.TryGetValue(r, out i)) return i;
            i = new Info();
            i.boss = IsBoss(r);
            i.enemy = Up<AbstractPlatformingLevelEnemy>(r);
            i.platforming = i.enemy != null;
            i.mover = Moves(i.enemy);
            GameObject go = OwnerOf(r);
            i.ground = go.GetComponent<PlatformingLevelGroundMovementEnemy>() != null || go.GetComponent<GroundHomingMovement>() != null || r.GetComponent<GroundHomingMovement>() != null;
            i.carries = CarriesShapes(go);
            infos[r] = i;
            return i;
        }

        /// <summary>A boss part: some AbstractLevelEntity above it is a LevelProperties.&lt;Boss&gt;.Entity (LevelProperties.cs:8).
        /// The whole parent chain counts, so a boss's sub-parts are never treated as light minions.</summary>
        public static bool IsBoss(DamageReceiver r)
        {
            for (Transform t = r != null ? r.transform : null; t != null; t = t.parent)
                foreach (var e in t.GetComponents<AbstractLevelEntity>())
                    if (e != null && !(e is AbstractPlatformingLevelEnemy) && IsBossType(e.GetType())) return true;
            return false;
        }

        private static bool IsBossType(Type t)
        {
            for (; t != null; t = t.BaseType)
                if (t.DeclaringType != null && t.DeclaringType.DeclaringType == typeof(LevelProperties)) return true;
            return false;
        }

        /// <summary>The enemy that owns a receiver: its nearest AbstractLevelEntity (found on inactive objects too), else
        /// the receiver's own GameObject.</summary>
        private static GameObject OwnerOf(DamageReceiver r)
        {
            var e = Up<AbstractLevelEntity>(r);
            return e != null ? e.gameObject : r.gameObject;
        }

        private static T Up<T>(Component c) where T : Component
        {
            for (Transform t = c != null ? c.transform : null; t != null; t = t.parent)
            {
                var x = t.GetComponent<T>();
                if (x != null) return x;
            }
            return null;
        }

        // ------------------------------------------------------------------ frame and level events (ArenaClient)

        /// <summary>Once per level frame, before the effect ring and status table are read.</summary>
        public static void Tick()
        {
            if (!CupheadTime.IsPaused()) Clock += CupheadTime.Delta[CupheadTime.Layer.Default];
            SpendSavedMurders();
            RunMurder();
        }

        public static void BeginFrame()
        {
            for (int i = 0; i < Active.Count; i++)
            {
                var s = Active[i];
                if (s == null) continue;
                s.seen = false;
                s.slow = s.stun = s.silence = s.dot = s.aux = 0f;
                s.flags = 0;
            }
        }

        /// <summary>Enemies that got no row this frame drop their statuses and are restored (unless a push, flinch, DoT
        /// pulse or Flip stagger is still running).</summary>
        public static void EndFrame()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (i >= Active.Count) continue;
                var s = Active[i];
                if (s == null) { Active.RemoveAt(i); continue; }
                if (!s.seen && !s.Busy) s.Restore();
            }
        }

        public static void ClearAll()
        {
            foreach (var s in Active.ToArray()) if (s != null) s.Restore();
            Active.Clear();
        }

        /// <summary>Level.OnPreWin/OnWin/OnLose/OnLevelEnd: KO, death animations and the level end play at full speed.</summary>
        public static void SuspendAll()
        {
            Suspended = true;
            try { ClearAll(); } catch (Exception e) { LogOnce("suspend", e); }
        }

        /// <summary>Level.OnStateChangedEvent (Level.cs:1378-1385): a boss phase change clears boss statuses and grants
        /// phaseChangeImmunity, so transition animations and their coroutines run at full speed.</summary>
        public static void PhaseChanged()
        {
            try
            {
                foreach (var s in Active.ToArray()) if (s != null && s.boss) s.Restore();
                bossImmuneUntil = Clock + Tuning.phaseChangeImmunity;
            }
            catch (Exception e) { LogOnce("phase change", e); }
        }

        public static void ResetForLevel(Level lv)
        {
            ClearAll();
            Suspended = false;
            bossImmuneUntil = -100f;
            infos.Clear();
            owners.Clear();
        }

        // ------------------------------------------------------------------ Murder (PCE)

        private static readonly HashSet<string> noMurderLevels = new HashSet<string>
        { "DiceGateLevel", "HouseLevel", "TutorialLevel", "ChaliceTutorialLevel", "ShmupTutorialLevel", "ChessCastleLevel", "TowerOfPowerLevel", "TestLevel", "FlyingTestLevel" };

        private static Level murderLevel;
        private static float murderStartedAt;
        private static bool murderDone;

        /// <summary>
        /// Cuphead owns Murder: Progress counts each Murder card picked as one unspent stack in the save (SlotSave.murder1/2),
        /// so a relaunch, a restarted ROUNDS or a replay of the save's cards can never arm it again (ROUNDS's MurderCard.
        /// OnAddCard runs on every re-application, Unbound ApplyCardStats_Patch.cs:23). Once per level, murderSettle into
        /// a running arena level where Murder means something (<see cref="MurderWorksIn"/>) and some enemy can take
        /// statuses (not in its no-status window, as the engine's AnyEnemyTakesStatus), every present player's stacks go
        /// through <see cref="Murder"/> (the FxMurder path) and are spent and saved. Never on the map or in a tutorial.
        /// </summary>
        private static void SpendSavedMurders()
        {
            var lv = Level.Current;
            if (lv != murderLevel) { murderLevel = lv; murderStartedAt = -1f; murderDone = false; }
            if (murderDone || lv == null || Suspended || !lv.Started || lv.Ending) return;
            if (murderStartedAt < 0f) murderStartedAt = Clock;
            if (Clock - murderStartedAt < Tuning.murderSettle || !ArenaClient.Live) return;
            if (!MurderWorksIn(lv)) { murderDone = true; return; }
            if (!ArenaClient.AnyTargetTakesStatus()) return;
            murderDone = true;
            for (int p = 0; p < 2; p++)
            {
                int n = Progress.PlayerPresent(p) ? Progress.Murders(p) : 0;
                if (n <= 0) continue;
                Murder(n);
                Progress.SpendMurders(p);
                Plugin.L.LogInfo("arena: Murder x" + n + " from player " + (p + 1) + "'s save spent in " + lv.GetType().Name);
            }
        }

        /// <summary>Where Murder acts: a run-and-gun level (on-screen minors) or a boss fight with named phases, not the
        /// tutorials, the house, Dice Palace's gate, the chess castle, Tower of Power, the test levels or a mausoleum.</summary>
        private static bool MurderWorksIn(Level lv)
        {
            if (lv.LevelType == Level.Type.Platforming) return true;
            var mausoleum = AccessTools.Field(typeof(Level), "isMausoleum"); // Level.cs:473
            return lv.LevelType == Level.Type.Battle && !noMurderLevels.Contains(lv.GetType().Name) && !(mausoleum != null && (bool)mausoleum.GetValue(lv));
        }

        /// <summary>FxMurder and the save's stacks (<see cref="SpendSavedMurders"/>): ROUNDS kills every opponent at the next
        /// battle start; here each stack skips the boss to its next named phase once the level has started (design choice:
        /// in the last phase that wins the fight).</summary>
        public static void Murder(int stacks)
        {
            pendingMurder = Mathf.Clamp(pendingMurder + Mathf.Max(1, stacks), 0, 16);
        }

        private static void RunMurder()
        {
            if (pendingMurder <= 0 || Suspended) return;
            var lv = Level.Current;
            if (lv == null || !lv.Started || lv.Ending) return;
            int n = pendingMurder;
            pendingMurder = 0;
            try
            {
                if (!MurderWorksIn(lv)) return;
                if (lv.LevelType == Level.Type.Platforming) { KillMinors(); return; }
                // e.g. FlyingBirdLevel.properties (FlyingBirdLevel.cs:15); AbstractLevelProperties.DealDamageToNextNamedState (:73-84)
                var f = AccessTools.Field(lv.GetType(), "properties");
                object props = f != null ? f.GetValue(lv) : null;
                MethodInfo skip = props != null ? AccessTools.Method(props.GetType(), "DealDamageToNextNamedState") : null;
                if (skip == null) { Plugin.L.LogWarning("arena: Murder found no boss phases in " + lv.GetType().Name); return; }
                for (int i = 0; i < n && !lv.Ending; i++) skip.Invoke(props, null);
                Plugin.L.LogInfo("arena: Murder x" + n + " skipped boss phases in " + lv.GetType().Name);
            }
            catch (Exception e) { LogOnce("murder", e); }
        }

        /// <summary>Run-and-gun levels have no boss phases: Murder kills every minor enemy on screen instead.</summary>
        private static void KillMinors()
        {
            var cam = CupheadLevelCamera.Current;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<AbstractPlatformingLevelEnemy>())
            {
                if (e == null || e.Dead || !e.isActiveAndEnabled) continue;
                if (cam != null && !cam.ContainsPoint(e.transform.position, new Vector2(50f, 50f))) continue;
                var r = e.GetComponent<DamageReceiver>();
                if (r == null || !r.enabled) continue;
                r.TakeDamage(new DamageDealer.DamageInfo(e.Health + 1f, DamageDealer.Direction.Neutral, e.transform.position, DamageDealer.DamageSource.Neutral));
            }
        }

        // ------------------------------------------------------------------ patches (Hooks.EnemyWait*, Hooks.SilencedShot)

        /// <summary>
        /// CupheadTime.WaitForSeconds prefix (CupheadTime.cs:107-125): a wait started by a slowed or stunned enemy runs on
        /// its own time (Delta x Speed: 0 while frozen, 1 - slow while slowed), and speeds back up the moment the status
        /// ends. A hard cap (maxWaitStretch x time + maxWaitExtra of game time) means no wait can hang a level script.
        /// Cuphead's own hit-pause and hit-flash waits keep their timing.
        /// </summary>
        public static bool Dilate(MonoBehaviour m, float time, CupheadTime.Layer layer, ref Coroutine result)
        {
            WaitCalls++;
            if (Active.Count == 0 || m == null) return true;
            try
            {
                if (m is DamageReceiver || m is HitFlash || m is Level || !m.gameObject.activeInHierarchy) return true;
                var s = m.GetComponentInParent<EnemyStatus>();
                if (s == null || !s.inList) return true;
                result = m.StartCoroutine(Wait(s, time, layer));
                DilatedWaits++;
                if (!reportedWait && TestMode.Active)
                {
                    reportedWait = true;
                    TestMode.Report("enemy-wait-dilation", true, m.GetType().Name + " waited on its own ROUNDS time (" + WaitCalls + " waits seen)");
                }
                return false;
            }
            catch (Exception e) { LogOnce("enemy wait", e); return true; }
        }

        private static IEnumerator Wait(EnemyStatus s, float time, CupheadTime.Layer layer)
        {
            float t = 0f, real = 0f, limit = time * Tuning.maxWaitStretch + Tuning.maxWaitExtra;
            while (t < time)
            {
                float d = CupheadTime.Delta[layer];
                real += d;
                t += d * (s != null && s.inList ? s.Speed : 1f);
                if (real >= limit) break; // never hang a level script
                yield return null;
            }
        }

        /// <summary>
        /// AbstractProjectile.Start postfix (AbstractProjectile.cs:230): a projectile that appears at a silenced (or
        /// confused) enemy becomes a harmless, faded dud (<see cref="DudShot"/>), never destroyed. Parryable ones stay
        /// live so parry interactions keep working; beams and contact attacks still hurt, as a silenced ROUNDS player can
        /// still move. Start also builds the projectile's DamageDealer (:232), so one already made a dud when it was
        /// spawned (<see cref="SilencedSpawn"/>) has the new dealer muted before its first physics step.
        /// </summary>
        public static void SilencedShot(AbstractProjectile p)
        {
            if (p == null) return;
            try
            {
                var d = DudShot.LiveCount > 0 ? DudShot.Of(p) : null;
                if (d != null) { d.Mute(); return; }
                if (Active.Count == 0 || p.CanParry) return;
                Decide(p, true);
            }
            catch (Exception e) { LogOnce("silenced shot", e); }
        }

        /// <summary>
        /// ObjectPool.Spawn postfix (ObjectPool.cs:161-200): a pooled projectile comes back with SetActive(true) (:181) and
        /// never runs Start again, so the dud test runs on every activation; the DudShot of its last life undid itself when
        /// it was recycled (:244-252). Spawn has placed it but its spawner sets the parry switch after Spawn returns
        /// (FlyingBirdLevelBird.cs:323), so a parryable dud is let go at its first LateUpdate instead.
        /// </summary>
        public static void SilencedSpawn(GameObject go)
        {
            if (Active.Count == 0 || go == null) return;
            try
            {
                var p = go.GetComponent<AbstractProjectile>();
                if (p != null && DudShot.Of(p) == null) Decide(p, false);
            }
            catch (Exception e) { LogOnce("silenced spawn", e); }
        }

        /// <summary>A dud exactly when a silenced enemy's hit area (padded by silenceSpawnPad) holds the new projectile.</summary>
        private static void Decide(AbstractProjectile p, bool parryKnown)
        {
            if (p.CompareTag("PlayerProjectile")) return;
            Vector3 pos = p.transform.position;
            for (int i = 0; i < Active.Count; i++)
            {
                var s = Active[i];
                if (s == null || !s.Silenced || !s.SpawnedBy(pos)) continue;
                DudShot.Make(p, parryKnown);
                DudShots++;
                if (!reportedDud && TestMode.Active)
                {
                    reportedDud = true;
                    TestMode.Report("enemy-silenced-shot", true, p.GetType().Name + " fired by silenced " + s.gameObject.name + " is a dud");
                }
                return;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static Action<DamageReceiver, bool> setPaused;
        private static MethodInfo setPausedMethod;
        private static bool setPausedTried;

        /// <summary>DamageReceiver.IsHitPaused has a private setter (DamageReceiver.cs:29).</summary>
        private static void SetPaused(DamageReceiver r, bool v)
        {
            if (!setPausedTried)
            {
                setPausedTried = true;
                setPausedMethod = AccessTools.PropertySetter(typeof(DamageReceiver), "IsHitPaused");
                try { if (setPausedMethod != null) setPaused = (Action<DamageReceiver, bool>)Delegate.CreateDelegate(typeof(Action<DamageReceiver, bool>), setPausedMethod); }
                catch { setPaused = null; }
            }
            if (setPaused != null) setPaused(r, v);
            else if (setPausedMethod != null) setPausedMethod.Invoke(r, new object[] { v });
        }

        /// <summary>A value read across processes: anything not finite counts as nothing.</summary>
        public static float Fin(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        private static Color ColorOf(int rgba, Color fallback)
        {
            if (rgba == 0) return fallback;
            float r, g, b, a;
            ArenaLink.FromRgba(rgba, out r, out g, out b, out a);
            return new Color(r, g, b, 1f);
        }

        private static readonly HashSet<string> errors = new HashSet<string>();

        /// <summary>A failing effect never breaks the frame: logged once, then skipped.</summary>
        public static void LogOnce(string what, Exception e)
        {
            if (errors.Count < 200 && errors.Add(what + e.Message)) Plugin.L.LogWarning("arena " + what + ": " + e);
        }
    }

    /// <summary>
    /// A silenced enemy's projectile, made harmless for this life: its own DamageDealer no longer damages players (the
    /// dealer's private DamageTypesManager, DamageDealer.cs:142/247; DealDamage then returns 0, :274-277) and its sprites
    /// are faded. It keeps its colliders and is never destroyed, so it still lands, bounces and dies the way its script
    /// expects (boss scripts keep references to their projectiles and some wait for them to land). Re-applied every frame
    /// in case the projectile builds a new dealer or animates its colour (re-based like EnemyStatus.Tint). When the
    /// projectile is recycled or disabled (ObjectPool.Recycle, ObjectPool.cs:244-252) it undoes itself: the Player flag
    /// the dealer had and the colours the game has not changed since come back, and the component goes, so the next life
    /// is decided afresh.
    /// </summary>
    public class DudShot : MonoBehaviour
    {
        private const float Alpha = 0.35f;
        /// <summary>DudShots not undone yet (SilencedShot skips its lookup while there are none).</summary>
        public static int LiveCount;
        private static FieldInfo dealerField, typesField;
        private static bool fieldsTried;
        private AbstractProjectile p;
        private DamageDealer dd;
        private DamageDealer.DamageTypesManager types;
        private bool hurtPlayer, parryKnown, undone;
        private SpriteRenderer[] rends;
        private Color[] baseC, wroteC;

        /// <summary>The projectile's dud of this life, else null (an undone one may linger until the end of the frame).</summary>
        public static DudShot Of(AbstractProjectile p)
        {
            var first = p != null ? p.GetComponent<DudShot>() : null;
            if (first == null || !first.undone) return first;
            foreach (var d in p.GetComponents<DudShot>()) if (d != null && !d.undone) return d;
            return null;
        }

        /// <summary>parryKnown false: decided at ObjectPool.Spawn, before the spawner set CanParry for this life.</summary>
        public static void Make(AbstractProjectile p, bool parryKnown)
        {
            if (p == null) return;
            var d = Of(p);
            if (d == null)
            {
                d = p.gameObject.AddComponent<DudShot>();
                d.p = p;
                d.parryKnown = parryKnown;
                LiveCount++;
            }
            d.Mute();
        }

        private void LateUpdate()
        {
            if (undone) return;
            if (!parryKnown)
            {
                parryKnown = true;
                if (p != null && p.CanParry) { Undo(); return; } // pink: parry interactions stay live
            }
            Mute();
        }

        private void OnDisable() { Undo(); }

        private void OnDestroy() { if (!undone) { undone = true; LiveCount = Mathf.Max(0, LiveCount - 1); } }

        public void Mute()
        {
            if (undone) return;
            try
            {
                if (p == null) { Undo(); return; }
                if (!fieldsTried)
                {
                    fieldsTried = true;
                    dealerField = AccessTools.Field(typeof(AbstractProjectile), "damageDealer");
                    typesField = AccessTools.Field(typeof(DamageDealer), "damageTypes");
                }
                var cur = dealerField != null ? dealerField.GetValue(p) as DamageDealer : null;
                if (cur != dd)
                {
                    // A new dealer (AbstractProjectile.Start builds one, :232): remember its own Player flag.
                    dd = cur;
                    types = dd != null && typesField != null ? typesField.GetValue(dd) as DamageDealer.DamageTypesManager : null;
                    hurtPlayer = types != null ? types.Player : p.GetDamagesType(DamageReceiver.Type.Player);
                }
                if (types != null)
                {
                    // Set again by the game since the last frame: that is the flag to give back.
                    if (types.Player) { hurtPlayer = true; types.Player = false; }
                }
                else if (dd != null) dd.SetDamageFlags(false, p.GetDamagesType(DamageReceiver.Type.Enemy), p.GetDamagesType(DamageReceiver.Type.Other));
                if (rends == null)
                {
                    rends = GetComponentsInChildren<SpriteRenderer>(true);
                    baseC = new Color[rends.Length];
                    wroteC = new Color[rends.Length];
                    for (int i = 0; i < rends.Length; i++) baseC[i] = wroteC[i] = rends[i] != null ? rends[i].color : Color.white;
                }
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i];
                    if (r == null) continue;
                    Color now = r.color;
                    if (now != wroteC[i]) baseC[i] = now; // the game changed it (fade-in, animation): re-base
                    Color w = baseC[i];
                    if (w.a > Alpha) w.a = Alpha;
                    if (w != now) r.color = w;
                    wroteC[i] = w;
                }
            }
            catch (Exception e) { EnemyStatus.LogOnce("dud shot", e); Undo(); }
        }

        /// <summary>Gives the projectile back as the game left it and removes the component.</summary>
        private void Undo()
        {
            if (undone) return;
            undone = true;
            LiveCount = Mathf.Max(0, LiveCount - 1);
            try
            {
                if (p != null && dd != null && (dealerField == null || dealerField.GetValue(p) == dd))
                {
                    if (types != null) { if (!types.Player) types.Player = hurtPlayer; }
                    else dd.SetDamageFlags(p.GetDamagesType(DamageReceiver.Type.Player), p.GetDamagesType(DamageReceiver.Type.Enemy), p.GetDamagesType(DamageReceiver.Type.Other));
                }
                if (rends != null)
                    for (int i = 0; i < rends.Length; i++)
                        if (rends[i] != null && rends[i].color == wroteC[i]) rends[i].color = baseC[i];
            }
            catch (Exception e) { EnemyStatus.LogOnce("dud undo", e); }
            Destroy(this);
        }
    }

    /// <summary>A short expanding ring on an enemy when a ROUNDS status lands on it.</summary>
    public class StatusPop : MonoBehaviour
    {
        private const float Life = 0.35f;
        private static Sprite ring;
        private SpriteRenderer sr;
        private Color col;
        private float t, size;

        public static void Spawn(Vector3 at, float size, Color c, SpriteRenderer reference)
        {
            if (ring == null) ring = Gfx.Ring();
            var go = new GameObject("ROUNDS status");
            var p = go.AddComponent<StatusPop>();
            p.sr = go.AddComponent<SpriteRenderer>();
            p.sr.sprite = ring;
            p.col = new Color(c.r, c.g, c.b, 0.85f);
            p.sr.color = p.col;
            p.size = size;
            if (reference != null)
            {
                go.layer = reference.gameObject.layer;
                p.sr.sortingLayerID = reference.sortingLayerID;
                p.sr.sortingOrder = reference.sortingOrder + 100;
                at.z = reference.transform.position.z;
            }
            go.transform.position = at;
            go.transform.localScale = Vector3.one * size * 0.6f;
        }

        private void Update()
        {
            if (CupheadTime.IsPaused()) return;
            t += CupheadTime.Delta[CupheadTime.Layer.Default];
            float k = t / Life;
            if (k >= 1f) { Destroy(gameObject); return; }
            transform.localScale = Vector3.one * size * Mathf.Lerp(0.6f, 1.3f, k);
            Color c = col;
            c.a *= 1f - k;
            sr.color = c;
        }
    }
}
