using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CupRounds
{
    /// <summary>
    /// Harmony patches that give Cuphead enemies (<see cref="CupheadTarget"/>) every ROUNDS opponent effect that bullets,
    /// damage over time and explosions carry (sheets/hooks.json, engine rows): poison and parasite ticks, the bullet's slow,
    /// knockback and gravity (through <see cref="CupheadTarget.CurrentHit"/>), one hit per bullet per enemy, bounce and drill
    /// bullets that end at an enemy, every Explosion card (slow, silence, stun, push, pull, Overpower) with one blast hit per
    /// enemy, Dazzle, the aura particle that follows the victim, and bullet size / Ghost Bullets against enemy hit areas.
    /// Co-op: explosions never slow, silence, stun or push the partner. A failing effect never breaks the frame.
    /// </summary>
    public static class ArenaEffectPatches
    {
        // ------------------------------------------------------------------ damage over time (Poison, Parasite)

        /// <summary>RayHitPoison calls the hit object's DamageOverTime (RayHitPoison.cs:29-32); on an enemy the same tick maths
        /// run on the CupheadTarget, each tick through Hit (DoT colour, lifesteal per tick as DoDamage would pay).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(DamageOverTime), "TakeDamageOverTime")]
        public static bool EnemyDot(DamageOverTime __instance, Vector2 damage, Vector2 position, float time, float interval, Color color, Player damagingPlayer)
        {
            if (Arena.I == null || __instance == null) return true;
            var t = __instance.GetComponent<CupheadTarget>();
            if (t == null) return true;
            try { t.StartDot(damage, position, time, interval, color, damagingPlayer); }
            catch (Exception e) { Arena.I.LogOnce("enemy dot", e); }
            return false; // DamageOverTime's own coroutine would call a null HealthHandler (DamageOverTime.cs:34)
        }

        // ------------------------------------------------------------------ bullets

        /// <summary>The running bullet, so CupheadTarget.CallTakeDamage can apply what ProjectileHit only gives a HealthHandler
        /// (movementSlow ProjectileHit.cs:256-258, knockback :293-306, ModdingUtils gravity).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ProjectileHit), "RPCA_DoHit")]
        public static void BulletHitStart(ProjectileHit __instance, out ProjectileHit __state)
        {
            __state = CupheadTarget.CurrentHit;
            CupheadTarget.CurrentHit = __instance;
        }

        /// <summary>Clears the running bullet even when RPCA_DoHit throws (a nested hit gets the outer one back).</summary>
        [HarmonyFinalizer, HarmonyPatch(typeof(ProjectileHit), "RPCA_DoHit")]
        public static void BulletHitEnd(ProjectileHit __state)
        {
            CupheadTarget.CurrentHit = __state;
        }

        /// <summary>ROUNDS holds a player for holdPlayerFor after a hit (playersHit, ProjectileHit.cs:178-189); an enemy has no
        /// HealthHandler, so a drilling or piercing bullet hit it every frame. One hit per bullet per enemy group per hold.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ProjectileHit), "Hit")]
        public static bool HoldEnemy(ProjectileHit __instance, HitInfo hit)
        {
            if (Arena.I == null || hit == null) return true;
            try
            {
                var t = hit.collider != null ? hit.collider.GetComponent<CupheadTarget>() : hit.transform != null ? hit.transform.GetComponent<CupheadTarget>() : null;
                return t == null || t.TryHold(__instance);
            }
            catch (Exception e) { Arena.I.LogOnce("enemy hold", e); return true; }
        }

        /// <summary>A bouncing bullet is used up on a player (RayHitReflect.cs:29-32) and now on an enemy too: it no longer
        /// bounces off bosses, Trickster gains nothing from the boss, and Target Bounce does not re-aim after the hit.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(RayHitReflect), "DoHitEffect")]
        public static void EnemyEndsBounce(RayHitReflect __instance, HitInfo hit)
        {
            if (Arena.I == null || hit == null || hit.transform == null) return;
            if (hit.transform.GetComponent<CupheadTarget>() != null) __instance.reflects -= 10;
        }

        /// <summary>A drill treats anything but a Player as a wall (RayHitDrill.cs:118-121), so it drilled through bosses and hit
        /// them every frame. A postfix, so the drill's own state changes still run exactly as for a player hit.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(RayHitDrill), "DoHitEffect")]
        public static void DrillEndsAtEnemy(HitInfo hit, ref HasToReturn __result)
        {
            if (__result != HasToReturn.hasToReturnNow || Arena.I == null || hit == null || hit.transform == null) return;
            if (hit.transform.GetComponent<CupheadTarget>() != null) __result = HasToReturn.canContinue;
        }

        /// <summary>Bullet size (RayCastTrail.cs:40, 54) only counted against playerMask, and Ghost Bullets' ignoreWallsMask
        /// (Gun.cs:603-605) is the Player layer only: both now include enemy hit areas. Awake runs at Instantiate, before
        /// Gun.ApplyProjectileStats copies ignoreWallsMask into mask. Floors (17) and walls (0) stay out of ignoreWallsMask.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(RayCastTrail), "Awake")]
        public static void BulletsSeeEnemies(RayCastTrail __instance)
        {
            if (Arena.I == null) return;
            __instance.ignoreWallsMask = __instance.ignoreWallsMask.value | (1 << Arena.TargetLayer);
            __instance.playerMask = __instance.playerMask.value | (1 << Arena.TargetLayer);
        }

        // ------------------------------------------------------------------ explosions

        /// <summary>One enemy hit area inside a blast: its collider, ROUNDS's range multiplier and distance (Explosion.cs:196-202).</summary>
        public sealed class BlastPart
        {
            public CupheadTarget t;
            public Collider2D col;
            public float rangeMultiplier, distance;
        }

        /// <summary>The blast whose Explode is running: the enemy groups already handled, and each group's hit areas inside it.</summary>
        public sealed class BlastInfo
        {
            public Explosion ex;
            public readonly HashSet<int> done = new HashSet<int>();
            public Dictionary<int, List<BlastPart>> groups;
        }

        private static BlastInfo blast;

        /// <summary>Explode -> DoExplosionEffects is synchronous (Explosion.cs:187-214): this blast's per-enemy bookkeeping.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Explosion), "Explode")]
        public static void BlastStart(Explosion __instance, out BlastInfo __state)
        {
            __state = blast;
            blast = Arena.I != null ? new BlastInfo { ex = __instance } : null;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Explosion), "Explode")]
        public static void BlastEnd(BlastInfo __state)
        {
            blast = __state;
        }

        /// <summary>
        /// DoExplosionEffects (Explosion.cs:88-185) with a Cuphead enemy as the victim. ROUNDS gates everything but damage on
        /// CharacterData, and its hitPlayerAction gets null there (the Overpower NRE that aborted the whole blast). The enemy
        /// takes slow, silence, Overpower, damage (HitArea, dmgColor), the Implosion pull, force with flyingFor, and stun, with
        /// ROUNDS's scale flags, line of sight and per-name immunity, once per enemy group per blast (a boss with several
        /// mirrored hit areas is one victim; its closest visible hit area decides the falloff). Heals never reach enemies.
        /// Co-op: a partner's blast only heals you (PartnerDamage guards DoDamage only, not slow, silence, stun or force).
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Explosion), "DoExplosionEffects")]
        public static bool Blast(Explosion __instance, Collider2D hitCol, float rangeMultiplier, float distance)
        {
            var a = Arena.I;
            if (a == null || __instance == null || hitCol == null) return true;
            var ex = __instance;
            SpawnedAttack sp = null;
            CupheadTarget t = null;
            try
            {
                sp = ex.GetComponent<SpawnedAttack>();
                t = hitCol.GetComponentInParent<CupheadTarget>();
                if (t == null)
                {
                    var v = hitCol.GetComponentInParent<Player>();
                    var owner = sp != null ? sp.spawner : null;
                    bool partner = v != null && owner != null && v != owner && a.SlotOf(v) >= 0 && a.SlotOf(owner) >= 0;
                    return !partner || ex.damage < 0f; // the owner's own self-effects stay native, as in ROUNDS
                }
            }
            catch (Exception e) { a.LogOnce("blast filter", e); return t == null; }

            try
            {
                var by = sp != null ? sp.spawner : null;
                Vector2 c = ex.transform.position;
                List<BlastPart> parts = null;
                var info = blast;
                if (info != null && info.ex == ex)
                {
                    if (!info.done.Add(t.group)) return false; // this enemy already took this blast
                    if (info.groups == null) info.groups = BlastGroups(ex);
                    info.groups.TryGetValue(t.group, out parts);
                }
                if (parts == null || parts.Count == 0) parts = new List<BlastPart> { new BlastPart { t = t, col = hitCol, rangeMultiplier = rangeMultiplier, distance = distance } };
                BlastPart hit = null, first = null;
                foreach (var p in parts)
                {
                    if (p.t == null || p.col == null) continue;
                    if (first == null) first = p;
                    if (!ex.ignoreWalls && !BlastSees(c, p)) continue;
                    hit = p;
                    break;
                }
                // ROUNDS checks immunity before sight, so a blocked blast still starts it (Explosion.cs:100), with the damage
                // the victim would take (scale x damage x rangeMultiplier): the hit area that takes it, else the closest.
                var at = hit ?? first;
                if (at == null) return false;
                if (ex.immunity > 0f && !a.Immunity(t.group, ex.gameObject.name, ex.immunity, (ex.scaleDmg ? ex.transform.localScale.x : 1f) * ex.damage * at.rangeMultiplier)) return false;
                if (hit != null) BlastEnemy(ex, by, hit.t, hit.col, hit.rangeMultiplier, hit.distance);
            }
            catch (Exception e) { a.LogOnce("blast on enemy", e); }
            return false;
        }

        /// <summary>Every enemy hit area inside the blast, grouped by enemy, closest first (the same rules as Explode).</summary>
        private static Dictionary<int, List<BlastPart>> BlastGroups(Explosion ex)
        {
            var groups = new Dictionary<int, List<BlastPart>>();
            float r = ex.range * (ex.scaleRadius ? ex.transform.localScale.x : 1f);
            if (r <= 0f) return groups;
            Vector2 c = ex.transform.position;
            foreach (var col in Physics2D.OverlapCircleAll(c, r))
            {
                if (col == null || col.gameObject.layer == 19) continue; // Explosion.cs:193
                var t = col.GetComponentInParent<CupheadTarget>();
                if (t == null) continue;
                float d = Vector2.Distance(c, col.bounds.ClosestPoint(c));
                float v = ex.staticRangeMultiplier ? 1f : Mathf.Clamp01(1f - d / r);
                List<BlastPart> list;
                if (!groups.TryGetValue(t.group, out list)) groups[t.group] = list = new List<BlastPart>();
                list.Add(new BlastPart { t = t, col = col, rangeMultiplier = v, distance = d });
            }
            foreach (var list in groups.Values) list.Sort((x, y) => x.distance.CompareTo(y.distance));
            return groups;
        }

        /// <summary>ROUNDS's line of sight (PlayerManager.CanSeePlayer: walls and floors block, spawned attacks and players do
        /// not), to the enemy's closest point or its centre. A wall the blast starts on does not count.</summary>
        private static bool BlastSees(Vector2 from, BlastPart p)
        {
            return Sight(from, p.col.bounds.ClosestPoint(from)) || Sight(from, p.col.bounds.center);
        }

        private static bool Sight(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            foreach (var h in Physics2D.RaycastAll(from, d / len, len, CupheadTarget.SightMask))
            {
                if (h.collider == null || h.collider.isTrigger || h.distance < 0.02f) continue;
                var root = h.transform.root;
                if (root.GetComponent<SpawnedAttack>() != null || root.GetComponent<Player>() != null) continue; // PlayerManager.cs:124
                return false;
            }
            return true;
        }

        /// <summary>Explosion.cs:90-180 for one enemy, in ROUNDS's order.</summary>
        private static void BlastEnemy(Explosion ex, Player by, CupheadTarget t, Collider2D col, float rm, float distance)
        {
            float s = ex.transform.localScale.x;
            float dmgS = ex.scaleDmg ? s : 1f, forceS = ex.scaleForce ? s : 1f, slowS = ex.scaleSlow ? s : 1f, silenceS = ex.scaleSilence ? s : 1f;
            float stunS = ex.scaleStun ? (1f + s) * 0.5f : 1f;
            Vector2 c = ex.transform.position;
            Vector2 at = col.bounds.ClosestPoint(c);
            // ROUNDS pushes along centre -> closest point (:144, :169); a bomb stuck inside the bounds pushes from the enemy's centre.
            Vector2 dir = at - c;
            if (dir.sqrMagnitude < 1e-6f) dir = t.Centre - c;
            dir = dir.sqrMagnitude < 1e-6f ? Vector2.up : dir.normalized;

            if (ex.slow != 0f) t.AddSlow(ex.slow * rm * slowS, ex.fastSlow);            // :104-117 (enemies never block)
            if (ex.silence != 0f) t.AddSilence(ex.silence * rm * silenceS);            // :118-121
            if (ex.hitPlayerAction != null)                                           // :126; Overpower is its only listener
                foreach (Delegate d in ex.hitPlayerAction.GetInvocationList())
                {
                    var op = d.Target as Explosion_Overpower;
                    if (op == null || by == null) continue;                               // other listeners need a CharacterData
                    // Explosion_Overpower.cs:19: dmgPer100Hp % of the blocker's max HP (x scale), away from the blocker
                    float hp = by.data.maxHealth * op.dmgPer100Hp * 0.01f * op.transform.localScale.x;
                    Vector2 away = t.Centre - (Vector2)by.transform.position;
                    away = away.sqrMagnitude < 1e-6f ? Vector2.up : away.normalized;
                    if (hp > 0f) t.Hit(away * hp, at, by, ArenaLink.HitArea, Color.black);
                }
            if (ex.damage > 0f)                                                       // :138-157; heals (< 0) never reach enemies
            {
                t.Hit(dir * (dmgS * ex.damage * rm), at, by, ArenaLink.HitArea, ex.dmgColor);
                if (ex.DealDamageAction != null)
                {
                    try { ex.DealDamageAction(t); }                                       // visual pulses (LineEffect/ParticleExplosionModifier)
                    catch (Exception e) { Arena.I.LogOnce("blast damage action", e); }
                }
            }
            if (ex.HitTargetAction != null)                                           // :161-164, gated on CharacterData in ROUNDS
                foreach (Delegate d in ex.HitTargetAction.GetInvocationList())
                {
                    var imp = d.Target as Implosion;
                    if (imp != null) { t.AddPull(c, imp.force, imp.time); continue; }   // Implosion.cs:36 (force already x scale, :20)
                    try { ((Action<Damagable, float>)d)(t, distance); }
                    catch (Exception e) { Arena.I.LogOnce("blast target action", e); }
                }
            // :165-175, mass 100 (forceIgnoreMass changes nothing at mass 100, HealthHandler.cs:159-162)
            if (ex.force != 0f) t.AddForce(dir * (rm * ex.force * forceS), ForceMode2D.Impulse, ex.flyingFor * rm);
            if (ex.stun > 0f) t.AddStun(ex.stun * stunS);                              // :176-179
        }

        /// <summary>ParticleExplosionModifier.Start reads its own ParticleSystem (ParticleExplosionModifier.cs:15-16); Healing
        /// field's circle has none, so plain ROUNDS throws there before hooking anything. With nothing to animate it is skipped.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ParticleExplosionModifier), "Start")]
        public static bool PulseNeedsParticles(ParticleExplosionModifier __instance)
        {
            return __instance != null && __instance.GetComponent<ParticleSystem>() != null;
        }

        /// <summary>Safety net for any other non-player Damagable: Explosion.cs:126 passes a null CharacterData.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Explosion_Overpower), "HitPlayer")]
        public static bool OverpowerNeedsPlayer(Explosion_Overpower __instance, CharacterData data)
        {
            if (data == null) return false;
            var sp = __instance != null ? __instance.GetComponent<SpawnedAttack>() : null;
            return sp != null && sp.spawner != null;
        }

        // ------------------------------------------------------------------ Dazzle

        /// <summary>E_StunOverTime is spawned as a child of the hit object (ObjectsToSpawn.cs:89-92) and stuns its parent player
        /// every 0.35 s; on an enemy GetComponentInParent&lt;Player&gt; was null (the NRE at StunPlayer.cs:27). It stuns the
        /// enemy it is attached to (AddStun keeps the larger stun, StunHandler.AddStun).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(StunPlayer), "Go")]
        public static bool DazzleEnemy(StunPlayer __instance)
        {
            if (Arena.I == null || __instance == null) return true;
            try
            {
                var t = __instance.GetComponentInParent<CupheadTarget>();
                if (t != null) { t.AddStun(__instance.time); return false; }
                // Stuck to a wall or floor copy: nobody to stun (vanilla throws every 0.35 s there too).
                if (__instance.targetPlayer == StunPlayer.TargetPlayer.Self && __instance.GetComponentInParent<Player>() == null) return false;
            }
            catch (Exception e) { Arena.I.LogOnce("dazzle", e); return false; }
            return true;
        }

        /// <summary>ThereCanOnlyBeOne searches transform.root (ThereCanOnlyBeOne.cs:11), which is the shared sandbox map for every
        /// mirrored enemy and wall, so only one Dazzle could exist across all of them. It now keeps one per enemy (or per map
        /// piece), pokes the existing one and removes the newcomer, as ROUNDS does per player.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ThereCanOnlyBeOne), "Start")]
        public static bool OnePerEnemy(ThereCanOnlyBeOne __instance)
        {
            if (Arena.I == null || __instance == null) return true;
            try
            {
                Transform scope = null;
                var t = __instance.GetComponentInParent<CupheadTarget>();
                if (t != null) scope = t.transform;
                else if (__instance.GetComponentInParent<Player>() == null && __instance.transform.parent != null)
                {
                    // a wall or floor copy: the map piece directly under the map root
                    var p = __instance.transform;
                    while (p.parent != null && p.parent != p.root) p = p.parent;
                    scope = p;
                }
                if (scope == null) return true; // on a player (or loose): ROUNDS's own rule
                bool other = false;
                foreach (var o in scope.GetComponentsInChildren<ThereCanOnlyBeOne>())
                    if (o != null && o != __instance) { other = true; o.Poke(); }
                if (other) Object.Destroy(__instance.gameObject);
            }
            catch (Exception e) { Arena.I.LogOnce("dazzle one per enemy", e); }
            return false;
        }

        // ------------------------------------------------------------------ aura victim particle (Chilling Presence, Lifestealer)

        /// <summary>FollowPlayer's private fields, in their own holder so a missing field can only break this one patch.</summary>
        private static class FollowFields
        {
            public static readonly AccessTools.FieldRef<FollowPlayer, Player> Own = AccessTools.FieldRefAccess<FollowPlayer, Player>("ownPlayer");
            public static readonly AccessTools.FieldRef<FollowPlayer, Vector3> Scale = AccessTools.FieldRefAccess<FollowPlayer, Vector3>("startScale");
        }

        /// <summary>FollowPlayer{target=Other} follows GetOtherPlayer (FollowPlayer.cs:29-39): null when solo (one NRE per frame)
        /// and the partner in co-op. It now sits on the enemy the aura chose, else the closest enemy, else is parked.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(FollowPlayer), "LateUpdate")]
        public static bool FollowEnemy(FollowPlayer __instance)
        {
            var a = Arena.I;
            if (a == null || __instance == null || __instance.target != FollowPlayer.Target.Other) return true;
            try
            {
                var own = FollowFields.Own(__instance);
                if (own == null) return false; // Start has not found an owner: nothing to follow (ROUNDS would throw)
                if (a.SlotOf(own) < 0) return true;
                var t = AuraChoice(__instance.GetComponentInParent<PlayerInRangeTrigger>());
                if (t == null) t = CupheadTarget.Closest(own.transform.position, float.PositiveInfinity, false);
                var tr = __instance.transform;
                if (__instance.inheritScale)
                {
                    Vector3 ps = own.transform.localScale, ss = FollowFields.Scale(__instance);
                    tr.localScale = new Vector3(ps.x * ss.x, ps.y * ss.y, ps.z * ss.z);
                }
                tr.position = t != null ? (Vector3)t.Centre : new Vector3(0f, Arena.OffsetY - 400f, 0f);
                tr.rotation = Quaternion.identity;
            }
            catch (Exception e) { a.LogOnce("follow enemy", e); }
            return false;
        }

        private static bool choiceLooked;
        private static Dictionary<PlayerInRangeTrigger, CupheadTarget> choice;

        /// <summary>The enemy an aura trigger picked: the aura bridge's own map when one exists in this engine (found once by its
        /// type, so this file does not depend on where it lives), else the aura's rule (closest visible enemy in range,
        /// PlayerInRangeTrigger.cs:67).</summary>
        private static CupheadTarget AuraChoice(PlayerInRangeTrigger trig)
        {
            if (trig == null) return null;
            if (!choiceLooked)
            {
                choiceLooked = true;
                choice = FindChoiceMap();
            }
            CupheadTarget t;
            if (choice != null) return choice.TryGetValue(trig, out t) && t != null && t.Alive ? t : null;
            return CupheadTarget.Closest(trig.transform.position, trig.range * trig.transform.root.localScale.x, true);
        }

        private static Dictionary<PlayerInRangeTrigger, CupheadTarget> FindChoiceMap()
        {
            Type[] types;
            try { types = typeof(Arena).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
            var want = typeof(Dictionary<PlayerInRangeTrigger, CupheadTarget>);
            foreach (var ty in types)
            {
                if (ty == null || ty == typeof(ArenaEffectPatches) || ty.ContainsGenericParameters) continue; // not this file's own cache
                foreach (var f in ty.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType != want) continue;
                    var map = f.GetValue(null) as Dictionary<PlayerInRangeTrigger, CupheadTarget>;
                    if (map != null) return map;
                }
            }
            return null;
        }
    }
}
