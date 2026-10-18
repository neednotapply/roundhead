using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// One Cuphead enemy's statuses (one per Cuphead DamageReceiver, the "group"), kept with ROUNDS's own victim rules:
    /// slow (CharacterStatModifiers.RPCA_AddSlow / GetSlow / Update decay), stun (StunHandler.AddStun), silence
    /// (SilenceHandler), damage over time (DamageOverTime), and the one-shot forces of this frame. Sent to Cuphead
    /// every ROUNDS frame (status table) plus as effect events when something new lands. It lasts one enemy life: when no
    /// mirror of the group is live any more, Arena.SyncMirrors ends it (<see cref="EndLife"/>).
    /// </summary>
    public class TargetStatus
    {
        public int group, flagsIn;                                  // flagsIn = K_Flags from Cuphead (light, boss, no-status)
        public float slowSlow, fastSlow, aura, auraUntil, stun, silence, dot, aux;
        public int dotRgba, flags;
        public Vector2 dv; public float flying; public bool cont;   // force this frame, Cuphead units/s
        public float fxSlow, fxStun, fxSilence; public bool fxFast; // coalesced events this frame
        public readonly Dictionary<ProjectileHit, float> held = new Dictionary<ProjectileHit, float>();
        public readonly Dictionary<string, Immunity> immune = new Dictionary<string, Immunity>();
        /// <summary>Arena.SyncMirrors: the last mirror sync that saw a live mirror of the group, and whether its life is running
        /// (true from creation, so a status made for a group that is already gone is ended at the next sync).</summary>
        public int seen; public bool alive = true;

        /// <summary>PlayerImmunity's Immunities entry (PlayerImmunity.cs:24-42): the damage that started it and the seconds left
        /// on ROUNDS's scaled clock (counted down by TimeHandler.deltaTime in <see cref="Tick"/>, PlayerImmunity.cs:12-22).
        /// An entry at 0 or below is gone (PlayerImmunity removes it; here it stays to be reused for the same name).</summary>
        public sealed class Immunity { public float time, dmg; }

        /// <summary>CharacterStatModifiers.GetSlow: the larger slow, capped at 0.9.</summary>
        public float Slow => Mathf.Clamp(Mathf.Max(Mathf.Max(slowSlow, fastSlow), Time.time < auraUntil ? aura : 0f), 0f, 0.9f);
        public bool Any => Slow > 0f || stun > 0f || silence > 0f || dot > 0f || flags != 0;

        /// <summary>CharacterStatModifiers.Update decay (0.3/s slow, 2/s fast slow), and the timers.</summary>
        public void Tick(float dt)
        {
            if (slowSlow > 0f) slowSlow = Mathf.Clamp(slowSlow - dt * 0.3f, 0f, 10f);
            if (fastSlow > 0f) fastSlow = Mathf.Clamp(fastSlow - dt * 2f, 0f, 1f);
            stun = Mathf.Max(0f, stun - dt);
            silence = Mathf.Max(0f, silence - dt);
            dot = Mathf.Max(0f, dot - dt);
            aux = Mathf.Max(0f, aux - dt);
            if (silence <= 0f) flags &= ~ArenaLink.StConfused;
            if (aux <= 0f) flags &= ~(ArenaLink.StHeavy | ArenaLink.StFlipped);
            foreach (var im in immune.Values) if (im.time > 0f) im.time -= dt;
        }

        /// <summary>Cuphead flagged the enemy no-status (intro, boss phase change, KO, level end): what is running stops too,
        /// so a boss leaves its phase-change immunity clean. Gold and damage over time stay until the life ends (EndLife).</summary>
        public void ClearTransient()
        {
            slowSlow = fastSlow = aura = stun = silence = aux = 0f;
            auraUntil = 0f;
            flags &= ArenaLink.StGold;
        }

        /// <summary>The enemy is gone (every mirror of the group off or out of the collider table): everything goes, Gold and
        /// damage over time included (GoldEffect lasts until death, design.txt:1274), so the status table stops listing it and
        /// a reactivated receiver (pooled enemies, Martini olives) starts clean. Arena.SyncMirrors also stops the DoT ticks.</summary>
        public void EndLife()
        {
            slowSlow = fastSlow = aura = auraUntil = stun = silence = dot = aux = 0f;
            dotRgba = flags = flagsIn = 0;
            dv = Vector2.zero; flying = 0f; cont = false;
            fxSlow = fxStun = fxSilence = 0f; fxFast = false;
            held.Clear();
            immune.Clear();
            alive = false;
        }
    }

    /// <summary>
    /// A Cuphead enemy hit area inside ROUNDS (layer 15 trigger under the sandbox map). It is a full ROUNDS victim without
    /// being a Player: damage, damage over time, slow, stun, silence, forces and gravity land on its <see cref="TargetStatus"/>,
    /// and every damage path ends in <see cref="Hit"/>, which reports the hit to Cuphead and pays the attacker's
    /// on-damage rewards (lifesteal, Taste of Blood, Brawler...) exactly where HealthHandler.DoDamage would.
    /// Effect patches find enemies through <see cref="All"/>, <see cref="Closest"/> and <see cref="CanSee"/>.
    /// </summary>
    public class CupheadTarget : Damagable
    {
        public static readonly List<CupheadTarget> All = new List<CupheadTarget>();
        /// <summary>The bullet whose ProjectileHit.RPCA_DoHit is running (set by the effect patches).</summary>
        public static ProjectileHit CurrentHit;
        /// <summary>Open (non-null) while one area damage call runs that ROUNDS would land once per victim: the enemy groups it
        /// already damaged. Today one PCE LaserHurtbox.HurtBox tick (ArenaModHooks.LaserTickStart/End): it skips repeats
        /// per Collider2D only (LaserHurtbox.cs:327-345), and an enemy is one CupheadTarget per mirrored collider.</summary>
        public static HashSet<int> OncePerGroup;

        public int id, group;
        public Collider2D Col;

        public TargetStatus St => Arena.I.Status(group);
        public bool Alive => Col != null && Col.enabled && gameObject.activeInHierarchy;
        public bool Light => (St.flagsIn & ArenaLink.TfLight) != 0;
        public bool Boss => (St.flagsIn & ArenaLink.TfBoss) != 0;
        public bool NoStatus => (St.flagsIn & ArenaLink.TfNoStatus) != 0;
        public Vector2 Centre => Col != null ? (Vector2)Col.bounds.center : (Vector2)transform.position;

        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() { All.Remove(this); }

        /// <summary>False when the open <see cref="OncePerGroup"/> call already damaged this enemy.</summary>
        private bool FirstOfGroup() => OncePerGroup == null || OncePerGroup.Add(group);

        public override void CallTakeDamage(Vector2 damage, Vector2 damagePosition, GameObject damagingWeapon = null, Player damagingPlayer = null, bool lethal = true)
        {
            var ph = CurrentHit;
            CurrentHit = null;
            if (!FirstOfGroup()) return;
            if (ph != null) BulletStatuses(ph, damagingPlayer);
            Hit(damage, damagePosition, damagingPlayer, ArenaLink.HitBullet, Color.black);
        }

        public override void TakeDamage(Vector2 damage, Vector2 damagePosition, GameObject damagingWeapon = null, Player damagingPlayer = null, bool lethal = true, bool ignoreBlock = false)
        {
            if (!FirstOfGroup()) return;
            Hit(damage, damagePosition, damagingPlayer, ArenaLink.HitBullet, Color.black);
        }

        public override void TakeDamage(Vector2 damage, Vector2 damagePosition, Color dmgColor, GameObject damagingWeapon = null, Player damagingPlayer = null, bool lethal = true, bool ignoreBlock = false)
        {
            if (!FirstOfGroup()) return;
            Hit(damage, damagePosition, damagingPlayer, ArenaLink.HitBullet, dmgColor);
        }

        /// <summary>The single damage path: to Cuphead (raw ROUNDS units, Cuphead scales), then the attacker's DealtDamage
        /// (HealthHandler.cs:235-238) so lifesteal and every on-damage card fires. A null victim is safe there.</summary>
        public void Hit(Vector2 damage, Vector2 pos, Player by, int kind, Color c)
        {
            if (Arena.I == null || damage.sqrMagnitude <= 0f) return;
            Arena.I.TargetHit(id, damage.magnitude, pos, by, kind, c);
            if (by != null && Arena.I.SlotOf(by) >= 0)
            {
                try { by.data.stats.DealtDamage(damage, false, null); }
                catch (System.Exception e) { Arena.I.LogOnce("DealtDamage", e); }
            }
        }

        /// <summary>What a bullet carries besides damage (ProjectileHit.RPCA_DoHit only gives these to a HealthHandler).</summary>
        private void BulletStatuses(ProjectileHit ph, Player by)
        {
            if (NoStatus) return;
            if (ph.movementSlow != 0f) AddSlow(ph.movementSlow, false);
            if (ph.force != 0f) AddForce(ph.transform.forward * ph.force, ForceMode2D.Impulse, 0f);
            if (ph.percentageDamage != 0f)
                Hit(ph.transform.forward * ph.percentageDamage * Tuning.percentDamageFlat / Tuning.damageScale, ph.transform.position, by, ArenaLink.HitBullet, Color.black);
            Arena.I.GravityOnHit(this, by);
        }

        public void AddSlow(float a, bool fast)
        {
            if (NoStatus) return;
            var s = St;
            if (fast) s.fastSlow = Mathf.Clamp(s.fastSlow, a, 1f);
            else s.slowSlow = Mathf.Clamp(s.slowSlow, a, 10f);
            s.fxSlow = Mathf.Max(s.fxSlow, a);
            s.fxFast |= fast;
        }

        public void AddSlowAddative(float a, float max)
        {
            if (NoStatus) return;
            var s = St;
            if (s.slowSlow < max) s.slowSlow = Mathf.Clamp(s.slowSlow + a, 0f, max);
            s.fxSlow = Mathf.Max(s.fxSlow, a);
        }

        public void AddAuraSlow(float a, float secs)
        {
            if (NoStatus) return;
            var s = St;
            s.aura = Time.time < s.auraUntil ? Mathf.Max(s.aura, a) : a;
            s.auraUntil = Time.time + secs;
        }

        public void AddStun(float t)
        {
            if (NoStatus) return;
            var s = St;
            if (t > s.stun) s.stun = t;
            s.fxStun = Mathf.Max(s.fxStun, t);
        }

        public void AddSilence(float t, bool confuse = false)
        {
            if (NoStatus) return;
            var s = St;
            if (t > s.silence) s.silence = t;
            if (confuse) s.flags |= ArenaLink.StConfused;
            s.fxSilence = Mathf.Max(s.fxSilence, t);
        }

        /// <summary>PlayerVelocity.AddForce for a 100-mass body (Force mode x0.02), turned into Cuphead units/s.</summary>
        public void AddForce(Vector2 f, ForceMode2D mode, float flyingFor, bool continuous = false)
        {
            if (NoStatus) return;
            var s = St;
            s.dv += f * (mode == ForceMode2D.Force ? 0.02f : 1f) / 100f * Arena.I.Scale;
            s.flying = Mathf.Max(s.flying, flyingFor);
            s.cont |= continuous;
        }

        public void AddPull(Vector2 roundsPoint, float force, float time)
        {
            if (NoStatus) return;
            Vector2 c = Arena.I.ToCuphead(roundsPoint);
            Arena.I.PostFx(group, ArenaLink.FxPull, c.x, c.y, force, time);
        }

        public void AddGravity(float mul, float time)
        {
            if (NoStatus) return;
            var s = St;
            s.aux = Mathf.Max(s.aux, time);
            s.flags |= mul < 0f ? ArenaLink.StFlipped : ArenaLink.StHeavy;
            Arena.I.PostFx(group, ArenaLink.FxGravity, mul, time);
        }

        /// <summary>DamageOverTime for an enemy: the same tick maths (DamageOverTime.cs:24-36), each tick through Hit.</summary>
        public void StartDot(Vector2 dmg, Vector2 pos, float time, float interval, Color c, Player by)
        {
            if (time <= 0f || interval <= 0f) { Hit(dmg, pos, by, ArenaLink.HitDot, c); return; }
            var s = St;
            s.dot = Mathf.Max(s.dot, time);
            s.dotRgba = Arena.Rgba(c);
            Arena.I.PostFx(group, ArenaLink.FxDot, dmg.magnitude, time, interval);
            StartCoroutine(Dot(dmg, pos, time, interval, c, by));
        }

        private IEnumerator Dot(Vector2 dmg, Vector2 pos, float time, float interval, Color c, Player by)
        {
            float dealt = 0f, total = dmg.magnitude, dpt = total / time * interval;
            if (dpt <= 0f) yield break;
            while (dealt < total)
            {
                dealt += dpt;
                Hit(dmg.normalized * dpt, pos, by, ArenaLink.HitDot, c);
                // ROUNDS waits interval / TimeHandler.timeScale; scaled time also stands still while Cuphead is paused.
                yield return new WaitForSeconds(interval / Mathf.Max(0.01f, TimeHandler.timeScale));
            }
        }

        /// <summary>ProjectileHit's playersHit / holdPlayerFor for an enemy: one hit per bullet per hold window.</summary>
        public bool TryHold(ProjectileHit p)
        {
            var s = St;
            float until;
            if (s.held.TryGetValue(p, out until) && Time.time < until) return false;
            if (s.held.Count > 64) s.held.Clear();
            s.held[p] = Time.time + Mathf.Max(0.05f, p.holdPlayerFor);
            return true;
        }

        public const int SightMask = (1 << Arena.SolidLayer) | (1 << Arena.BulletFloorLayer);

        /// <summary>Walls and floors block sight; enemy hit areas (triggers) never do.</summary>
        public static bool CanSee(Vector2 from, Vector2 to)
        {
            foreach (var h in Physics2D.LinecastAll(from, to, SightMask))
                if (h.collider != null && !h.collider.isTrigger) return false;
            return true;
        }

        /// <summary>The closest live enemy hit area within range (measured to its bounds), optionally in sight.</summary>
        public static CupheadTarget Closest(Vector2 from, float range, bool sight)
        {
            CupheadTarget best = null;
            float bd = range;
            foreach (var t in All)
            {
                if (t == null || !t.Alive) continue;
                Vector2 p = t.Col.bounds.ClosestPoint(from);
                float d = Vector2.Distance(from, p);
                if (d < bd && (!sight || CanSee(from, p) || CanSee(from, t.Centre))) { bd = d; best = t; }
            }
            return best;
        }
    }
}
