using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Patches on card mods' own classes (PCE, ModdingUtils, MapsExtended), found by name at runtime so the engine has no
    /// build-time dependency on any mod; a mod that is not installed is skipped silently. In ROUNDS these cards only ever look
    /// at other ROUNDS players, which in Roundhead means the co-op partner (or nobody when solo). Each hook makes the card's
    /// opponent effect land on Cuphead enemies instead (<see cref="CupheadTarget"/>), and never on the partner:
    /// Glare and Low Ground (InConeEffect), King Midas, Discombobulate, Murder, Laser, Super Jump, Demonic Possession's
    /// sky bullets and Thank You Sir. Masochist needs nothing here (Arena.ReadEvents EvBlockedHit reaches PCE's own
    /// HealthHandler.CallTakeDamage prefix) and Tractor Beam rides on the bullet's force (CupheadTarget.BulletStatuses).
    /// MapsExtended, which many card packs pull in, measures screen-edge bullets in ROUNDS's map box; here that is the
    /// Cuphead view.
    /// </summary>
    public static class ArenaModHooks
    {
        /// <summary>KingMidasEffect.range (KingMidasEffect.cs:15).</summary>
        private const float MidasRange = 1.75f;
        /// <summary>GoldEffect.movementSpeedReduction (GoldEffect.cs:9): the raw ROUNDS value; Cuphead scales it.</summary>
        private const float GoldSlow = 0.5f;
        /// <summary>Glare is re-applied every frame while an enemy is in sight and fades this soon after sight is lost.</summary>
        private const float GlareRefresh = 0.25f;
        /// <summary>Glare: -15% movement per stack (GlareCard.glare: 1 - 0.15).</summary>
        private const float GlarePerStack = 0.85f;
        /// <summary>Laser bounces kept in Roundhead's closed level (PCE allows 1000, LaserHurtbox.maxReflects).</summary>
        private const int LaserMaxReflects = 16;
        /// <summary>LaserHurtbox.maxDistance.</summary>
        private const float LaserMaxDistance = 1000f;
        /// <summary>A Cuphead ceiling this close over the sky spawn would swallow the nuke PCE fires upward.</summary>
        private const float NukeRoofCheck = 30f;
        /// <summary>MapsExtended's ScreenEdgeBounce.Update transpiler class (MapsExtended 1.4).</summary>
        private const string MapsEdge = "MapsExt.ScreenEdgeBounce_Patch";

        private static Type inCone, reversible, laserEffect, thankYou;
        private static MethodInfo pceStats, pceBlock, pceGun, muStats, zeroGGun, applyMods, clearMods;
        private static FieldInfo coneOwner, coneRay, coneRange, coneAngle, coneSight, coneOthersFunc, coneToOthers;
        private static readonly AccessTools.FieldRef<PlayerVelocity, bool> Simulated = AccessTools.FieldRefAccess<PlayerVelocity, bool>("simulated");
        private static readonly List<CupheadTarget> coneHits = new List<CupheadTarget>();
        private static int applied;

        // ------------------------------------------------------------------ setup

        public static void Apply(Harmony harmony)
        {
            inCone = ModType("ModdingUtils.MonoBehaviours.InConeEffect");
            reversible = ModType("ModdingUtils.MonoBehaviours.ReversibleEffect");
            laserEffect = ModType("PCE.Cards.LaserEffect");
            thankYou = ModType("PCE.RoundsEffects.ThankYouSirMayIHaveAnotherWasDealtDamageEffect");
            pceStats = Extension("PCE.Extensions.CharacterStatModifiersExtension", typeof(CharacterStatModifiers));
            pceBlock = Extension("PCE.Extensions.BlockExtension", typeof(Block));
            pceGun = Extension("PCE.Extensions.GunExtension", typeof(Gun));
            muStats = Extension("ModdingUtils.Extensions.CharacterStatModifiersExtension", typeof(CharacterStatModifiers));
            zeroGGun = Extension("ZeroGBulletPatch.Extensions.GunExtension", typeof(Gun));
            if (reversible != null)
            {
                applyMods = AccessTools.Method(reversible, "ApplyModifiers");
                clearMods = AccessTools.Method(reversible, "ClearModifiers", new[] { typeof(bool) });
            }
            if (inCone != null)
            {
                coneOwner = AccessTools.Field(inCone, "player");
                coneRay = AccessTools.Field(inCone, "centerRay");
                coneRange = AccessTools.Field(inCone, "range");
                coneAngle = AccessTools.Field(inCone, "angle");
                coneSight = AccessTools.Field(inCone, "needsLineOfSight");
                coneOthersFunc = AccessTools.Field(inCone, "otherPlayerEffectFunc");
                coneToOthers = AccessTools.Field(inCone, "applyToOthers");
            }

            // Glare, Low Ground (ModdingUtils InConeEffect.cs:121-160)
            Patch(harmony, "ModdingUtils.MonoBehaviours.InConeEffect", "GetAllEnemyPlayers", postfix: nameof(NoPartnerEnemies));
            Patch(harmony, "ModdingUtils.MonoBehaviours.InConeEffect", "ConditionsMet", postfix: nameof(EnemyInCone));
            Patch(harmony, "ModdingUtils.MonoBehaviours.InConeEffect", "Update", postfix: nameof(GlareEnemies));
            // King Midas (KingMidasEffect.cs:26-52)
            Patch(harmony, "PCE.MonoBehaviours.KingMidasEffect", "Update", prefix: nameof(MidasEnemies));
            // Discombobulate (DiscombobulateCard GetDoBlockAction, OnDiscombobulateActivate)
            Patch(harmony, "PCE.Cards.DiscombobulateCard", "GetDoBlockAction", postfix: nameof(DiscombobulateEnemies));
            Patch(harmony, "PCE.Cards.DiscombobulateCard", "OnDiscombobulateActivate", prefix: nameof(NoPartnerDiscombobulate));
            // Murder (MurderCard.OnAddCard kills through RPCA_Die in GM_Test; CommitMurders runs on BattleStart, PCE.cs:122)
            Patch(harmony, "PCE.Cards.MurderCard", "OnAddCard", prefix: nameof(MurderCounts));
            Patch(harmony, "PCE.Cards.MurderCard", "CommitMurders", prefix: nameof(NoMurders));
            // Laser (LaserHurtbox.GetPositions, HurtBox; toggled by GeneralInputPatchUpdate.cs:17-31, which reads the hidden window)
            Patch(harmony, "PCE.Cards.LaserHurtbox", "GetPositions", prefix: nameof(LaserPath));
            Patch(harmony, "PCE.Cards.LaserHurtbox", "HurtBox", prefix: nameof(LaserTickStart), finalizer: nameof(LaserTickEnd));
            Patch(harmony, "PCE.Cards.GeneralInputPatchUpdate", "Postfix", prefix: nameof(NoNativeLaserToggle));
            // Super Jump (SuperJumpEffect.OnUpdate reads playerActions.Down)
            Patch(harmony, "PCE.MonoBehaviours.SuperJumpEffect", "OnUpdate", transpiler: nameof(SuperJumpDown));
            // Demonic Possession (DemonicPossessionEffect Nuke/Rain/Wall spawn at world (0,100))
            Patch(harmony, "PCE.MonoBehaviours.SpawnBulletsEffect", "SetPosition", prefix: nameof(SkyOverCuphead));
            // Thank You Sir (ThankYouSirMayIHaveAnotherWasDealtDamageEffect needs a ROUNDS attacker; Cuphead hits have none)
            if (thankYou != null && reversible != null)
            {
                var take = AccessTools.Method(typeof(HealthHandler), "TakeDamage", new[] { typeof(Vector2), typeof(Vector2), typeof(Color), typeof(GameObject), typeof(Player), typeof(bool), typeof(bool) });
                try { harmony.Patch(take, prefix: Hm(nameof(ThankYouCuphead))); applied++; }
                catch (Exception e) { EnginePlugin.L.LogWarning("arena mod hook HealthHandler.TakeDamage: " + e.Message); }
            }
            // MapsExtended's screen edges (its ScreenEdgeBounce.Update transpiler swaps the camera for these two)
            Patch(harmony, MapsEdge, "GetNormalizedMapPosition", prefix: nameof(EdgePlaceInCupheadView));
            Patch(harmony, MapsEdge, "NormalizedMapPositionToWorldPosition", prefix: nameof(EdgePointInCupheadView));
            // The Laser switch runs once per frame.
            if (Arena.I != null && Arena.I.GetComponent<ArenaModTicker>() == null) Arena.I.gameObject.AddComponent<ArenaModTicker>();
            EnginePlugin.L.LogInfo("arena: mod hooks applied: " + applied + " (PCE " + (pceStats != null ? "found" : "absent") + ", ModdingUtils " + (muStats != null ? "found" : "absent") + ", MapsExtended " + (ModType(MapsEdge) != null ? "found" : "absent") + ")");
        }

        /// <summary>A loaded type by full name, or null without any log (the mod is simply not installed).</summary>
        private static Type ModType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static MethodInfo Extension(string typeName, Type arg)
        {
            var t = ModType(typeName);
            return t != null ? AccessTools.Method(t, "GetAdditionalData", new[] { arg }) : null;
        }

        private static HarmonyMethod Hm(string name) => name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ArenaModHooks), name));

        private static void Patch(Harmony harmony, string typeName, string method, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            var t = ModType(typeName);
            if (t == null) return;
            try
            {
                var m = AccessTools.DeclaredMethod(t, method);
                if (m == null) { EnginePlugin.L.LogWarning("arena mod hook: " + typeName + "." + method + " not found (mod version changed?)"); return; }
                harmony.Patch(m, prefix: Hm(prefix), postfix: Hm(postfix), transpiler: Hm(transpiler), finalizer: Hm(finalizer), ilmanipulator: null); // HarmonyX 2.9's current overload
                applied++;
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena mod hook " + typeName + "." + method + ": " + e.Message); }
        }

        private static object Data(MethodInfo ext, object of) => ext == null || of == null ? null : ext.Invoke(null, new[] { of });

        private static T Get<T>(object data, string field)
        {
            var f = data != null ? AccessTools.Field(data.GetType(), field) : null;
            return f != null ? (T)f.GetValue(data) : default(T);
        }

        private static void Set(object data, string field, object value)
        {
            var f = data != null ? AccessTools.Field(data.GetType(), field) : null;
            if (f != null) f.SetValue(data, value);
        }

        /// <summary>ModdingUtils PlayerStatus.PlayerAliveAndSimulated.</summary>
        private static bool AliveAndSimulated(Player p) => p != null && p.data != null && !p.data.dead && Simulated(p.data.playerVel);

        /// <summary>The arena is in a Cuphead level (not the card pick, not the map).</summary>
        private static bool InLevel(Arena a) => a != null && a.WorldReady && a.Mode == 1 && !a.Picking;

        private static Vector2 Closest(CupheadTarget t, Vector2 from) => t.Col.bounds.ClosestPoint(from);

        private static bool Sees(Vector2 eye, CupheadTarget t, Vector2 closest) => CupheadTarget.CanSee(eye, closest) || CupheadTarget.CanSee(eye, t.Centre);

        // ------------------------------------------------------------------ Glare, Low Ground (ModdingUtils InConeEffect)

        /// <summary>The partner is no enemy: InConeEffect's enemy players are the other team (InConeEffect.cs:144-147), which in
        /// co-op is the partner. Glare no longer slows them and Low Ground no longer counts them.</summary>
        private static void NoPartnerEnemies(ref List<Player> __result)
        {
            var a = Arena.I;
            if (a == null || __result == null) return;
            __result.RemoveAll(p => a.SlotOf(p) >= 0);
        }

        /// <summary>A Cuphead enemy in the cone meets the condition too (Low Ground: an enemy above you).</summary>
        private static void EnemyInCone(MonoBehaviour __instance, ref bool __result)
        {
            var a = Arena.I;
            if (__result || !InLevel(a) || __instance == null || coneOwner == null) return;
            try
            {
                var owner = coneOwner.GetValue(__instance) as Player;
                if (owner == null || a.SlotOf(owner) < 0) return;
                __result = ConeTargets(__instance, owner).Count > 0;
            }
            catch (Exception e) { a.LogOnce("cone condition", e); }
        }

        /// <summary>Glare on Cuphead enemies: every enemy in sight is slowed while it stays in sight. Each Glare card adds its
        /// own cone and each cone applies x0.85^glare (GlareCard.OnAddCard, GlareCard.glare), so n cards make 0.85^(n*n).</summary>
        private static void GlareEnemies(MonoBehaviour __instance)
        {
            var a = Arena.I;
            if (!InLevel(a) || __instance == null || !IsGlare(__instance)) return;
            try
            {
                var owner = coneOwner.GetValue(__instance) as Player;
                if (owner == null || a.SlotOf(owner) < 0 || !AliveAndSimulated(owner)) return;
                int cones = 0;
                Component first = null;
                foreach (var c in owner.GetComponents(inCone))
                    if (IsGlare(c)) { cones++; if (first == null) first = c; }
                if (first != __instance) return; // once per player per frame
                float glare = Get<float>(Data(pceStats, owner.data.stats), "glare");
                if (glare <= 0f) return;
                float slow = 1f - Mathf.Pow(GlarePerStack, glare * cones);
                foreach (var t in ConeTargets(__instance, owner)) t.AddAuraSlow(slow, GlareRefresh);
            }
            catch (Exception e) { a.LogOnce("glare", e); }
        }

        private static bool IsGlare(Component cone)
        {
            if (cone == null || coneOthersFunc == null || coneToOthers == null || coneOwner == null || !(bool)coneToOthers.GetValue(cone)) return false;
            var d = coneOthersFunc.GetValue(cone) as Delegate;
            return d != null && d.Method != null && d.Method.DeclaringType != null && d.Method.DeclaringType.FullName == "PCE.Cards.GlareCard";
        }

        /// <summary>InConeEffect.OtherPlayerInCone (InConeEffect.cs:157-160) for Cuphead enemies: range, cone angle and sight
        /// are measured to the enemy's centre or to the nearest point of its hit area (bosses are big).</summary>
        private static List<CupheadTarget> ConeTargets(Component cone, Player owner)
        {
            coneHits.Clear();
            Vector2 from = owner.transform.position;
            Vector2 ray = (Vector2)coneRay.GetValue(cone);
            float range = (float)coneRange.GetValue(cone), half = Mathf.Abs((float)coneAngle.GetValue(cone) / 2f);
            bool sight = (bool)coneSight.GetValue(cone);
            foreach (var t in CupheadTarget.All)
            {
                if (t == null || !t.Alive) continue;
                Vector2 p = Closest(t, from), c = t.Centre;
                if (Vector2.Distance(from, p) > range) continue;
                if (Vector2.Angle(ray, c - from) > half && Vector2.Angle(ray, p - from) > half) continue;
                if (sight && !Sees(from, t, p)) continue;
                coneHits.Add(t);
            }
            return coneHits;
        }

        // ------------------------------------------------------------------ King Midas

        /// <summary>KingMidasEffect.Update on Cuphead enemies: an enemy you touch turns gold (GoldEffect: -50% movement until it
        /// dies). It never looks at the partner, whom PCE would gild for the rest of their life.</summary>
        private static bool MidasEnemies(MonoBehaviour __instance)
        {
            var a = Arena.I;
            if (a == null) return true;
            try
            {
                var owner = __instance != null ? __instance.GetComponent<Player>() : null;
                int slot = a.SlotOf(owner);
                if (slot < 0 || !InLevel(a) || !AliveAndSimulated(owner)) return false;
                Vector2 at = owner.transform.position;
                foreach (var t in CupheadTarget.All)
                {
                    if (t == null || !t.Alive || t.NoStatus) continue;
                    var st = t.St;
                    if ((st.flags & ArenaLink.StGold) != 0 || Vector2.Distance(Closest(t, at), at) > MidasRange) continue;
                    st.flags |= ArenaLink.StGold; // in the status table until the enemy is gone (TargetStatus.EndLife)
                    a.PostFx(t.group, ArenaLink.FxGold, GoldSlow, 0f, 0f, 0f, slot);
                }
            }
            catch (Exception e) { a.LogOnce("king midas", e); }
            return false;
        }

        // ------------------------------------------------------------------ Discombobulate

        /// <summary>The block action PCE adds (with the first card) also confuses Cuphead enemies: every enemy within
        /// discombobulateRange of the block that you can see, for discombobulateDuration, on any trigger but None (the test in
        /// DiscombobulateCard.GetDoBlockAction). Cuphead's AI has no controls to reverse, so it becomes confuse = silence.</summary>
        private static void DiscombobulateEnemies(ref Action<BlockTrigger.BlockTriggerType> __result, Player player, Block block)
        {
            var p = player;
            var b = block;
            __result = (Action<BlockTrigger.BlockTriggerType>)Delegate.Combine(__result, new Action<BlockTrigger.BlockTriggerType>(t =>
            {
                if (t != BlockTrigger.BlockTriggerType.None) ConfuseEnemies(p, b);
            }));
        }

        private static void ConfuseEnemies(Player p, Block b)
        {
            var a = Arena.I;
            if (!InLevel(a) || p == null || b == null || a.SlotOf(p) < 0) return;
            try
            {
                var data = Data(pceBlock, b);
                float range = Get<float>(data, "discombobulateRange"), duration = Get<float>(data, "discombobulateDuration");
                if (range <= 0f || duration <= 0f) return;
                Vector2 at = b.transform.position, eye = p.transform.position;
                foreach (var t in CupheadTarget.All)
                {
                    if (t == null || !t.Alive) continue;
                    Vector2 c = Closest(t, at);
                    if (Vector2.Distance(at, c) < range && Sees(eye, t, c)) t.AddSilence(duration, true);
                }
            }
            catch (Exception e) { a.LogOnce("discombobulate", e); }
        }

        /// <summary>Never reverse the co-op partner's controls.</summary>
        private static bool NoPartnerDiscombobulate(int playerID) => Arena.I == null || Arena.I.SlotOfPlayerId(playerID) < 0;

        // ------------------------------------------------------------------ Murder

        /// <summary>MurderCard.OnAddCard does nothing in the arena. In the sandbox PCE kills every other-team player 2 s after the
        /// pick through RPCA_Die (MurderCard.cs:22-33), which is the partner, past PartnerDamage; outside it, it adds a ModdingUtils
        /// murder stack (MurderCard.cs:34-35). Neither runs: Cuphead owns Murder stacks (a per-save unspent count, spent once per
        /// level on the boss, EnemyStatus.Murder), so a card re-applied to a fresh ROUNDS player (Arena.ApplyWantedCards after a
        /// relaunch, a ROUNDS restart or a sandbox reload) never counts or arms anything here.</summary>
        private static bool MurderCounts() => Arena.I == null;

        /// <summary>The BattleStart hook would kill the partner too (MurderCard.CommitMurders); Cuphead spends Murder itself.</summary>
        private static bool NoMurders(ref IEnumerator __result)
        {
            if (Arena.I == null) return true;
            __result = Nothing();
            return false;
        }

        private static IEnumerator Nothing() { yield break; }

        // ------------------------------------------------------------------ Laser

        private static readonly int[] switchSeen = new int[2];
        private static readonly bool[] switchKnown = new bool[2];

        /// <summary>Cuphead's weapon-switch presses (Slot2 U_SwitchPresses) toggle the PCE Laser the way PCE's switchWeapon
        /// handler does (GeneralInputPatchUpdate.cs:17-31); that handler reads the hidden ROUNDS window, which never gets input.</summary>
        private static void LaserSwitches(Arena a)
        {
            if (pceGun == null) return;
            for (int s = 0; s < 2; s++)
            {
                int n = a.link.I(ArenaLink.Slot2(s) + ArenaLink.U_SwitchPresses);
                if (!switchKnown[s]) { switchKnown[s] = true; switchSeen[s] = n; continue; }
                if (n == switchSeen[s]) continue;
                switchSeen[s] = n;
                if (!InLevel(a)) continue;
                foreach (var p in PlayerManager.instance.players)
                    if (a.SlotOf(p) == s) { ToggleLaser(a, p); break; }
            }
        }

        private static void ToggleLaser(Arena a, Player p)
        {
            try
            {
                var gun = p.GetComponent<Holding>().holdable.GetComponent<Gun>();
                var ad = Data(pceGun, gun);
                if (ad == null || !Get<bool>(ad, "canBeLaser")) return;
                bool on = !Get<bool>(ad, "isLaser");
                Set(ad, "isLaser", on);
                var le = laserEffect != null ? p.GetComponent(laserEffect) : null;
                if (on)
                {
                    Set(Data(zeroGGun, gun), "arcTrajectoryRotationalCompensationDisabled", true);
                    if (le != null && applyMods != null) applyMods.Invoke(le, null);
                }
                else
                {
                    if (le != null && clearMods != null) clearMods.Invoke(le, new object[] { false });
                    Set(Data(zeroGGun, gun), "arcTrajectoryRotationalCompensationDisabled", false);
                }
                EnginePlugin.L.LogInfo("arena: player " + (a.SlotOf(p) + 1) + " laser " + (on ? "on" : "off"));
            }
            catch (Exception e) { a.LogOnce("laser toggle", e); }
        }

        /// <summary>PCE's own toggle stays off for arena players, so a stray press the hidden window hears cannot toggle twice.</summary>
        private static bool NoNativeLaserToggle(object[] __args)
        {
            var a = Arena.I;
            if (a == null || __args == null || __args.Length == 0) return true;
            var gi = __args[0] as GeneralInput;
            return gi == null || a.SlotOf(gi.GetComponent<CharacterData>()) < 0;
        }

        /// <summary>LaserHurtbox.GetPositions in the arena: the beam passes Cuphead enemies the way it passes ROUNDS players (its
        /// mask skips the Player layer, not the enemies' trigger hit areas), still bounces off Cuphead's floors and walls, and
        /// ends at the level bounds as it would leave an open ROUNDS map. A hard cap stops the 1000 bounces of a closed level.</summary>
        private static bool LaserPath(Component __instance, ref Vector3[] __result)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady || __instance == null) return true;
            try
            {
                var tr = Traverse.Create(__instance);
                var gun = tr.Field("gun").GetValue<Gun>();
                if (gun == null || gun.shootPosition == null) return true;
                int mask = tr.Field("layerMask").GetValue<int>() & ~(1 << Arena.TargetLayer) & ~((1 << 6) | (1 << 7)); // 6/7: per-player copies of floor layer 17
                var pts = new List<Vector3> { gun.shootPosition.position };
                Vector3 dir = (Quaternion)AccessTools.Method(typeof(Gun), "getShootRotation").Invoke(gun, new object[] { 0, 0, 0f }) * Vector3.forward;
                RaycastHit2D hit = Ray(pts[0], dir, mask, LaserMaxDistance);
                if (hit.collider != null && Vector3.Distance(hit.point, pts[0]) < 0.001f) { __result = pts.ToArray(); return false; }
                int n = 0;
                bool ended = false;
                while (hit.collider != null && n < LaserMaxReflects)
                {
                    n++;
                    pts.Add(hit.point);
                    if (LevelBound(a, hit.collider)) { ended = true; break; }
                    dir = Vector2.Reflect(dir, hit.normal);
                    hit = Ray(hit.point + (Vector2)(0.01f * dir), dir, mask, LaserMaxDistance);
                }
                if (!ended && hit.collider == null) pts.Add(pts[pts.Count - 1] + dir * 100000f); // into the open, as LaserHurtbox does
                __result = pts.ToArray();
                return false;
            }
            catch (Exception e) { a.LogOnce("laser path", e); return true; }
        }

        private static readonly HashSet<int> laserGroups = new HashSet<int>();

        /// <summary>One LaserHurtbox damage tick (HurtBox, every damageTickDelay, LaserHurtbox.cs:327-345) skips repeats per
        /// Collider2D only and damages the Damagable of every collider its capsules overlap. A ROUNDS player is one collider; a
        /// Cuphead enemy is one CupheadTarget per mirrored collider, so the tick now hits each enemy group once
        /// (<see cref="CupheadTarget.OncePerGroup"/>, open until <see cref="LaserTickEnd"/>).</summary>
        private static void LaserTickStart(out HashSet<int> __state)
        {
            __state = CupheadTarget.OncePerGroup;
            var groups = __state == null ? laserGroups : new HashSet<int>(); // a nested call (none known) keeps the outer set
            groups.Clear();
            CupheadTarget.OncePerGroup = groups;
        }

        /// <summary>Closes the tick's group set, also when HurtBox throws.</summary>
        private static void LaserTickEnd(HashSet<int> __state)
        {
            CupheadTarget.OncePerGroup = __state;
        }

        /// <summary>The first solid (non-trigger) hit along a ray.</summary>
        private static RaycastHit2D Ray(Vector2 from, Vector2 dir, int mask, float distance)
        {
            RaycastHit2D best = default(RaycastHit2D);
            float bd = float.MaxValue;
            foreach (var h in Physics2D.RaycastAll(from, dir, distance, mask))
                if (h.collider != null && !h.collider.isTrigger && h.distance < bd) { bd = h.distance; best = h; }
            return best;
        }

        /// <summary>The level-bound walls Cuphead sends (ArenaClient.WriteBounds: ids -1..-3, 400 Cuphead units thick and at
        /// least 4000 long). A wall named "level bound" also counts.</summary>
        private static bool LevelBound(Arena a, Collider2D c)
        {
            if (c.gameObject.name == "level bound") return true;
            var box = c as BoxCollider2D;
            if (box == null || c.gameObject.layer != Arena.SolidLayer || c.gameObject.name != "wall") return false;
            float sx = box.size.x * a.Scale, sy = box.size.y * a.Scale;
            return Mathf.Abs(Mathf.Min(sx, sy) - 400f) < 2f && Mathf.Max(sx, sy) >= 3999f;
        }

        // ------------------------------------------------------------------ Super Jump

        /// <summary>SuperJumpEffect.OnUpdate reads playerActions.Down (InControl), which the hidden window never hears: every
        /// "Down.IsPressed" there becomes <see cref="DownHeld"/>, which reads Cuphead's down key for arena players.</summary>
        private static IEnumerable<CodeInstruction> SuperJumpDown(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var held = AccessTools.Method(typeof(ArenaModHooks), nameof(DownHeld));
            int n = 0;
            for (int i = 1; i < list.Count; i++)
            {
                var m = list[i].operand as MethodInfo;
                var f = list[i - 1].operand as FieldInfo;
                if (m == null || m.Name != "get_IsPressed" || f == null || f.Name != "Down" || list[i - 1].opcode != OpCodes.Ldfld) continue;
                // [PlayerActions] ldfld Down; callvirt IsPressed  ->  [PlayerActions] call DownHeld; nop (labels stay where they were)
                list[i - 1].opcode = OpCodes.Call;
                list[i - 1].operand = held;
                list[i].opcode = OpCodes.Nop;
                list[i].operand = null;
                n++;
            }
            if (n == 0) EnginePlugin.L.LogWarning("arena mod hook: Super Jump's down key read not found; it will not charge from Cuphead");
            return list;
        }

        /// <summary>PlayerActions.Down.IsPressed, from Cuphead's down key (Slot2 U_DownHeld, or the fed move direction) for an
        /// arena player.</summary>
        public static bool DownHeld(PlayerActions actions)
        {
            var a = Arena.I;
            if (a != null && actions != null && PlayerManager.instance != null)
                foreach (var p in PlayerManager.instance.players)
                {
                    if (p == null || p.data == null || p.data.playerActions != actions) continue;
                    int s = a.SlotOf(p);
                    if (s < 0) break;
                    return a.link.I(ArenaLink.Slot2(s) + ArenaLink.U_DownHeld) != 0 || (p.data.input != null && p.data.input.direction.y < -0.5f);
                }
            return actions != null && actions.Down != null && actions.Down.IsPressed;
        }

        // ------------------------------------------------------------------ Demonic Possession

        /// <summary>Nuke, Rain and Wall spawn at world (0,100), about 1100 units above the arena. They now start over the top
        /// of Cuphead's view; under a Cuphead ceiling they start just below it (a bullet cannot start inside a wall) and the
        /// nuke, which PCE fires upward to fall back down, is dropped straight down instead.</summary>
        private static void SkyOverCuphead(Component __instance, ref Vector3 pos)
        {
            var a = Arena.I;
            if (!InLevel(a) || pos != new Vector3(0f, 100f, 0f)) return;
            try
            {
                Rect v;
                if (!CupheadView(a, out v)) return;
                Vector2 p = new Vector2(v.center.x, v.yMax + 2f);
                int steps = 0;
                while (InSolid(p) && steps < 16) { p.y -= 0.5f; steps++; }
                pos = new Vector3(p.x, p.y, 0f);
                bool roofed = steps > 0 || Ray(p, Vector2.up, 1 << Arena.SolidLayer, NukeRoofCheck).collider != null;
                if (!roofed) return;
                var dirs = Traverse.Create(__instance).Field("directionsToShoot").GetValue<List<Vector3>>();
                if (dirs != null) for (int i = 0; i < dirs.Count; i++) if (dirs[i].y > 0.5f) dirs[i] = Vector3.down;
            }
            catch (Exception e) { a.LogOnce("sky bullets", e); }
        }

        private static bool InSolid(Vector2 p)
        {
            foreach (var c in Physics2D.OverlapPointAll(p, 1 << Arena.SolidLayer)) if (c != null && !c.isTrigger) return true;
            return false;
        }

        /// <summary>Cuphead's camera rectangle in ROUNDS units (the numbers ArenaRenderer renders with).</summary>
        private static bool CupheadView(Arena a, out Rect v)
        {
            v = default(Rect);
            float ortho = a.link.F(ArenaLink.C_CamOrtho), aspect = a.link.F(ArenaLink.C_Aspect);
            if (ortho <= 0f) return false;
            Vector2 c = a.ToRounds(a.link.F(ArenaLink.C_CamX), a.link.F(ArenaLink.C_CamY));
            float h = ortho / a.Scale, w = h * Mathf.Clamp(aspect, 0.5f, 4f);
            v = new Rect(c.x - w, c.y - h, 2f * w, 2f * h);
            return true;
        }

        // ------------------------------------------------------------------ MapsExtended screen edges

        /// <summary>MapsExtended's ScreenEdgeBounce.Update places a bullet in ROUNDS's map box at the origin (71.12 x 40, or a
        /// custom map's size) instead of through the camera ArenaTargetPatches.EdgeOnCupheadView hands it. The arena is 1000
        /// units below that box, so every Bouncy, Mayhem, Ring or Super Ball bullet was at its bottom edge on its first frame and
        /// was moved up there by the bounce (RayHitReflect puts the bullet on the hit point). In the arena the box is the
        /// Cuphead view: the bullet's place in <see cref="ArenaViewCam"/>, clamped to it, as MapsExtended clamps to its box.</summary>
        private static bool EdgePlaceInCupheadView(Vector3 worldPosition, ref Vector3 __result)
        {
            var cam = ArenaViewCam.Cam;
            if (Arena.I == null || !Arena.I.WorldReady || cam == null) return true;
            Vector3 v = cam.WorldToViewportPoint(worldPosition);
            __result = new Vector3(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y), 0f);
            return false;
        }

        /// <summary>Back from that place to the world: the point of the Cuphead view there (the edge point the bullet bounces at).</summary>
        private static bool EdgePointInCupheadView(Vector3 normalizedPosition, ref Vector3 __result)
        {
            var cam = ArenaViewCam.Cam;
            if (Arena.I == null || !Arena.I.WorldReady || cam == null) return true;
            Vector3 w = cam.ViewportToWorldPoint(new Vector3(normalizedPosition.x, normalizedPosition.y, -cam.transform.position.z));
            __result = new Vector3(w.x, w.y, normalizedPosition.z);
            return false;
        }

        // ------------------------------------------------------------------ Thank You Sir

        /// <summary>Thank You Sir's ammo for a Cuphead hit. PCE's effect is a ModdingUtils WasHitEffect, run only from a ROUNDS
        /// bullet's RPCA_DoHit (ModdingUtils ProjectileHitPatchRPCA_DoHit) and only with a lastSourceOfDamage
        /// (ThankYouSirMayIHaveAnotherWasDealtDamageEffect.WasDealtDamage). A Cuphead hit (EvDamage, Arena.CupheadHit) has
        /// neither, so the same ReversibleEffect is added here for each Cuphead hit that lands (HealthHandler.cs:212 rules).</summary>
        private static void ThankYouCuphead(HealthHandler __instance, Vector2 damage, bool ignoreBlock)
        {
            var a = Arena.I;
            if (!Arena.CupheadHit || a == null || damage == Vector2.zero || __instance == null) return;
            try
            {
                var p = __instance.GetComponent<Player>();
                if (p == null || a.SlotOf(p) < 0 || p.GetComponent(thankYou) == null) return;
                var d = p.data;
                if (!d.isPlaying || d.dead || !Simulated(d.playerVel) || (!ignoreBlock && d.block.IsBlocking())) return;
                var ammo = p.GetComponent<Holding>().holdable.GetComponent<Gun>().GetComponentInChildren<GunAmmo>();
                int n = Get<int>(Data(pceStats, d.stats), "thankyousirmayihaveanother");
                if (ammo == null || ammo.maxAmmo >= 99 || n <= 0) return;
                var e = p.gameObject.AddComponent(reversible);
                var mod = AccessTools.Field(reversible, "gunAmmoStatModifier").GetValue(e);
                Set(mod, "maxAmmo_add", n);
                Set(mod, "currentAmmo_add", n);
            }
            catch (Exception e) { a.LogOnce("thank you sir", e); }
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>Called by <see cref="ArenaModTicker"/> every frame, after the arena's own update.</summary>
        public static void Tick()
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady || PlayerManager.instance == null) return;
            try { LaserSwitches(a); } catch (Exception e) { a.LogOnce("laser switch", e); }
        }
    }

    /// <summary>Runs <see cref="ArenaModHooks.Tick"/> every frame on the engine host, after <see cref="Arena"/> (order -500).</summary>
    public class ArenaModTicker : MonoBehaviour
    {
        private void Update() => ArenaModHooks.Tick();
    }
}
