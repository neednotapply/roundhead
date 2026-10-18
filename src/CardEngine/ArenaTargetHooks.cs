using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Photon.Pun;
using Sonigon;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// "The screen" for ROUNDS code that measures it: a never-rendered camera that sits on Cuphead's view of the arena.
    /// ROUNDS's MainCam stays at the menu (the card pick needs it), 1000 units above the arena, so ScreenEdgeBounce,
    /// Looper and PCE Fireworks would see every bullet below the bottom edge. They are handed this camera instead.
    /// Its pixel rect is the hidden window (no target texture), which is what those scripts divide by.
    /// Also gives the level rectangle inside Cuphead's bound walls, for wrapping (OutOfBoundsHandler.GetPoint) and Teleport.
    /// </summary>
    [DefaultExecutionOrder(-490)]
    public class ArenaViewCam : MonoBehaviour
    {
        /// <summary>The view camera (disabled; only used for WorldToScreenPoint / ScreenToWorldPoint).</summary>
        public static Camera Cam { get; private set; }
        /// <summary>True while <see cref="Cam"/> shows a Cuphead level view (a level, or a pick started from one).</summary>
        public static bool HasView { get; private set; }
        /// <summary>Cuphead's camera view in ROUNDS units.</summary>
        public static Rect View { get; private set; }
        /// <summary>The view cut at the inner faces of Cuphead's level-bound walls (ArenaClient.WriteBounds).</summary>
        public static Rect Level { get; private set; }
        /// <summary><see cref="Level"/> pulled in at the walls, so a body or bullet stopped by a wall still reaches "the edge".</summary>
        public static Rect Wrap { get; private set; }

        /// <summary>ROUNDS units kept between a level-bound wall and the wrap edge (about a player radius).</summary>
        private const float WallInset = 1f;
        /// <summary>Cuphead's bound boxes are the level's size plus 4000 px long (ArenaClient.WriteBounds); nothing else is this long.</summary>
        private const float MinBoundSpan = 80f;
        /// <summary>ROUNDS's own screen: MainCam at the origin, OutOfBoundsHandler's +-35.56 x +-20 box (OutOfBoundsHandler.cs:125-130).</summary>
        private const float RoundsOrtho = 20f, RoundsAspect = 35.56f / 20f;

        private void Awake()
        {
            if (Cam != null) return;
            var go = new GameObject("Roundhead View Cam");
            DontDestroyOnLoad(go);
            Cam = go.AddComponent<Camera>();
            Cam.enabled = false;            // never renders
            Cam.orthographic = true;
            Cam.cullingMask = 0;
            Cam.clearFlags = CameraClearFlags.Nothing;
            Cam.targetTexture = null;
            Cam.rect = new Rect(0f, 0f, 1f, 1f);
            Cam.nearClipPlane = 0.1f;
            Cam.farClipPlane = 1000f;
            SetCam(Vector2.zero, RoundsOrtho, RoundsAspect);
        }

        private void OnDestroy()
        {
            if (Cam != null) Destroy(Cam.gameObject);
            Cam = null;
            HasView = false;
        }

        private void Update()
        {
            try { Refresh(); }
            catch (Exception e) { if (Arena.I != null) Arena.I.LogOnce("view cam", e); }
        }

        private static void Refresh()
        {
            var a = Arena.I;
            if (Cam == null) return;
            if (a == null || a.link == null || !a.WorldReady || a.Mode == 0)
            {
                // No level: ROUNDS's own screen (nothing of ours flies then).
                HasView = false;
                SetCam(Vector2.zero, RoundsOrtho, RoundsAspect);
                return;
            }
            // Cuphead writes its camera only in levels (ArenaClient.Tick). During a pick the last level view stays, so bullets
            // still in flight keep sane edges; ROUNDS's pick screen uses its own MainCam and never this camera.
            if (a.Mode == 1 && !a.Picking)
            {
                float ortho = a.link.F(ArenaLink.C_CamOrtho), aspect = a.link.F(ArenaLink.C_Aspect);
                if (ortho > 1f && aspect > 0.1f)
                {
                    SetCam(a.ToRounds(a.link.F(ArenaLink.C_CamX), a.link.F(ArenaLink.C_CamY)), Mathf.Max(1f, ortho / Mathf.Max(1f, a.Scale)), Mathf.Clamp(aspect, 0.5f, 4f));
                    HasView = true;
                }
            }
            if (HasView) FindWalls();
        }

        private static void SetCam(Vector2 centre, float ortho, float aspect)
        {
            Cam.transform.position = new Vector3(centre.x, centre.y, -100f);
            Cam.transform.rotation = Quaternion.identity;
            Cam.orthographicSize = ortho;
            Cam.aspect = aspect;            // fixed, so the hidden window's own shape does not matter
            float h = ortho, w = ortho * aspect;
            View = Level = Wrap = new Rect(centre.x - w, centre.y - h, 2f * w, 2f * h);
        }

        /// <summary>Cut the view at the level-bound walls (mirrored as solids on layer 0).</summary>
        private static void FindWalls()
        {
            Rect v = View;
            float reach = 2f * Mathf.Max(v.width, v.height);
            float lx = v.xMin, rx = v.xMax, ty = v.yMax, wlx = v.xMin, wrx = v.xMax, wty = v.yMax, f;
            if (WallFace(v.center, Vector2.left, reach, false, out f) && f > v.xMin) { lx = f; wlx = f + WallInset; }
            if (WallFace(v.center, Vector2.right, reach, false, out f) && f < v.xMax) { rx = f; wrx = f - WallInset; }
            if (WallFace(v.center, Vector2.up, reach, true, out f) && f < v.yMax) { ty = f; wty = f - WallInset; }
            if (rx - lx < 2f) { lx = v.xMin; rx = v.xMax; }
            if (wrx - wlx < 2f) { wlx = lx; wrx = rx; }
            if (ty - v.yMin < 2f) ty = v.yMax;
            if (wty - v.yMin < 2f) wty = ty;
            Level = Rect.MinMaxRect(lx, v.yMin, rx, ty);
            Wrap = Rect.MinMaxRect(wlx, v.yMin, wrx, wty);
        }

        private static bool WallFace(Vector2 from, Vector2 dir, float reach, bool ceiling, out float face)
        {
            face = 0f;
            float best = float.PositiveInfinity;
            foreach (var h in Physics2D.RaycastAll(from, dir, reach, 1 << Arena.SolidLayer))
            {
                var c = h.collider;
                if (c == null || c.isTrigger || !(c is BoxCollider2D) || h.distance <= 0.01f || h.distance >= best) continue;
                Vector3 size = c.bounds.size;
                if ((ceiling ? size.x : size.y) < MinBoundSpan) continue;
                best = h.distance;
                face = ceiling ? h.point.y : h.point.x;
            }
            return best < float.PositiveInfinity;
        }
    }

    /// <summary>
    /// Harmony patches that point ROUNDS's targeting, area and camera-based card effects at Cuphead enemies
    /// (sheets/hooks.json, e_view_* / e_target_* rows): Homing, Target Bounce, Radar Shot, Chase, Saw and Abyssal Countdown,
    /// Radiance, the Chilling Presence and Lifestealer auras, Shield Charge, Thruster, Remote, Teleport, and the screen
    /// edges of Bouncy / Pac-Bullets / Pac-Player. ROUNDS only ever looks for other ROUNDS players (the shooter, or the
    /// co-op partner on the other team); here the victim is the closest live <see cref="CupheadTarget"/>, never the partner.
    /// Each patch only takes over for the arena's own players; a failure is logged once and the frame goes on.
    /// </summary>
    [HarmonyPatch]
    public static class ArenaTargetPatches
    {
        // ------------------------------------------------------------------ private ROUNDS fields

        private static readonly AccessTools.FieldRef<ScreenEdgeBounce, Camera> EdgeCam = AccessTools.FieldRefAccess<ScreenEdgeBounce, Camera>("mainCam");
        private static readonly AccessTools.FieldRef<Looper, Camera> LoopCam = AccessTools.FieldRefAccess<Looper, Camera>("mainCam");

        private static readonly AccessTools.FieldRef<Homing, MoveTransform> HMove = AccessTools.FieldRefAccess<Homing, MoveTransform>("move");
        private static readonly AccessTools.FieldRef<Homing, bool> HOn = AccessTools.FieldRefAccess<Homing, bool>("isOn");
        private static readonly AccessTools.FieldRef<Homing, FlickerEvent[]> HFlicks = AccessTools.FieldRefAccess<Homing, FlickerEvent[]>("flicks");
        private static readonly AccessTools.FieldRef<Homing, bool> HSound = AccessTools.FieldRefAccess<Homing, bool>("soundHomingCanPlay");

        private static readonly AccessTools.FieldRef<BounceEffectRetarget, MoveTransform> BrMove = AccessTools.FieldRefAccess<BounceEffectRetarget, MoveTransform>("move");
        private static readonly AccessTools.FieldRef<BounceEffectRetarget, PhotonView> BrView = AccessTools.FieldRefAccess<BounceEffectRetarget, PhotonView>("view");

        private static readonly AccessTools.FieldRef<RadarShot, Player> RdPlayer = AccessTools.FieldRefAccess<RadarShot, Player>("player");
        private static readonly AccessTools.FieldRef<RadarShot, WeaponHandler> RdWh = AccessTools.FieldRefAccess<RadarShot, WeaponHandler>("wh");
        private static readonly AccessTools.FieldRef<Gun, Vector3> ForceDir = AccessTools.FieldRefAccess<Gun, Vector3>("forceShootDir");

        private static readonly AccessTools.FieldRef<Chase, Player> ChPlayer = AccessTools.FieldRefAccess<Chase, Player>("player");
        private static readonly AccessTools.FieldRef<Chase, LineEffect> ChLine = AccessTools.FieldRefAccess<Chase, LineEffect>("lineEffect");
        private static readonly AccessTools.FieldRef<Chase, bool> ChOn = AccessTools.FieldRefAccess<Chase, bool>("isOn");
        private static readonly AccessTools.FieldRef<Chase, Player> ChCurrent = AccessTools.FieldRefAccess<Chase, Player>("currentTarget");

        private static readonly AccessTools.FieldRef<LineRangeEffect, SpawnedAttack> LrSpawned = AccessTools.FieldRefAccess<LineRangeEffect, SpawnedAttack>("spawned");
        private static readonly AccessTools.FieldRef<LineRangeEffect, LineEffect> LrLine = AccessTools.FieldRefAccess<LineRangeEffect, LineEffect>("lineEffect");
        private static readonly AccessTools.FieldRef<LineRangeEffect, bool> LrDone = AccessTools.FieldRefAccess<LineRangeEffect, bool>("done");
        private static readonly MethodInfo LineRadius = AccessTools.Method(typeof(LineEffect), "GetRadius");

        private static readonly AccessTools.FieldRef<PlayerInRangeTrigger, float> TrCounter = AccessTools.FieldRefAccess<PlayerInRangeTrigger, float>("counter");
        private static readonly AccessTools.FieldRef<PlayerInRangeTrigger, bool> TrDone = AccessTools.FieldRefAccess<PlayerInRangeTrigger, bool>("done");
        private static readonly AccessTools.FieldRef<PlayerInRangeTrigger, Player> TrOwn = AccessTools.FieldRefAccess<PlayerInRangeTrigger, Player>("ownPlayer");
        private static readonly AccessTools.FieldRef<PlayerInRangeSlow, PlayerInRangeTrigger> SlTrigger = AccessTools.FieldRefAccess<PlayerInRangeSlow, PlayerInRangeTrigger>("trigger");
        private static readonly AccessTools.FieldRef<PlayerInRangeSlow, AttackLevel> SlLevel = AccessTools.FieldRefAccess<PlayerInRangeSlow, AttackLevel>("level");
        private static readonly AccessTools.FieldRef<DealDamageToPlayer, CharacterData> DdData = AccessTools.FieldRefAccess<DealDamageToPlayer, CharacterData>("data");

        private static readonly AccessTools.FieldRef<ShieldCharge, CharacterData> ScData = AccessTools.FieldRefAccess<ShieldCharge, CharacterData>("data");
        private static readonly AccessTools.FieldRef<ShieldCharge, AttackLevel> ScLevel = AccessTools.FieldRefAccess<ShieldCharge, AttackLevel>("level");
        private static readonly AccessTools.FieldRef<ShieldCharge, Vector3> ScDir = AccessTools.FieldRefAccess<ShieldCharge, Vector3>("dir");
        private static readonly AccessTools.FieldRef<ShieldCharge, bool> ScCancel = AccessTools.FieldRefAccess<ShieldCharge, bool>("cancelForce");
        private static readonly AccessTools.FieldRef<ShieldCharge, float> ScBlockTime = AccessTools.FieldRefAccess<ShieldCharge, float>("blockTime");

        private static readonly AccessTools.FieldRef<Thruster, FollowLocalPos> ThFollow = AccessTools.FieldRefAccess<Thruster, FollowLocalPos>("follow");
        private static readonly AccessTools.FieldRef<Thruster, Vector2> ThFwd = AccessTools.FieldRefAccess<Thruster, Vector2>("startForward");

        private static readonly AccessTools.FieldRef<RemoteControl, SpawnedAttack> RcSpawned = AccessTools.FieldRefAccess<RemoteControl, SpawnedAttack>("spawned");

        private static readonly AccessTools.FieldRef<Teleport, CharacterData> TpData = AccessTools.FieldRefAccess<Teleport, CharacterData>("data");
        private static readonly AccessTools.FieldRef<Teleport, AttackLevel> TpLevel = AccessTools.FieldRefAccess<Teleport, AttackLevel>("level");

        private static void Fail(string what, Exception e) { if (Arena.I != null) Arena.I.LogOnce(what, e); }

        private static bool Live => Arena.I != null && Arena.I.WorldReady;

        /// <summary>GamefeelManager.GameFeel (screen shake), skipped when the manager is not there.</summary>
        private static void Feel(Vector2 force)
        {
            if (GamefeelManager.instance == null) return;
            try { GamefeelManager.GameFeel(force); }
            catch (Exception e) { Fail("game feel", e); }
        }

        /// <summary>Forget entries whose effect object is gone (the keys of these maps are ROUNDS effect components).</summary>
        private static void Prune<K, V>(Dictionary<K, V> map) where K : UnityEngine.Object
        {
            if (map.Count < 64) return;
            var dead = new List<K>();
            foreach (var k in map.Keys) if ((UnityEngine.Object)k == null) dead.Add(k);
            foreach (var k in dead) map.Remove(k);
        }

        // ------------------------------------------------------------------ screen edges = the Cuphead view

        /// <summary>Bouncy, Riccochet, Mayhem, Trickster, Target Bounce, Super Ball: bounce at the Cuphead view's edges
        /// (ScreenEdgeBounce.cs:36 takes MainCam, :46-53 and :81 measure the bullet with it).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(ScreenEdgeBounce), "Start")]
        public static void EdgeOnCupheadView(ScreenEdgeBounce __instance)
        {
            if (Live && ArenaViewCam.Cam != null) EdgeCam(__instance) = ArenaViewCam.Cam;
        }

        /// <summary>Looper wraps at the Cuphead view's edges (Looper.cs:17).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Looper), "Awake")]
        public static void LoopOnCupheadView(Looper __instance)
        {
            if (Live && ArenaViewCam.Cam != null) LoopCam(__instance) = ArenaViewCam.Cam;
        }

        /// <summary>ROUNDS's fixed screen box (OutOfBoundsHandler.cs:125-130) becomes the Cuphead level view. ModdingUtils'
        /// BoundsPointFromWorldPosition inverts GetPoint(0) and GetPoint(1), so PCE Pac-Bullets and Pac-Player wrap there
        /// (just in front of a level-bound wall) instead of 1000 units above the arena.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(OutOfBoundsHandler), "GetPoint")]
        public static bool BoundsOnCupheadView(Vector3 p, ref Vector3 __result)
        {
            if (!Live || !ArenaViewCam.HasView) return true;
            Rect v = ArenaViewCam.Wrap;
            __result = new Vector3(Mathf.Lerp(v.xMin, v.xMax, p.x), Mathf.Lerp(v.yMin, v.yMax, p.y), 0f);
            return false;
        }

        // ------------------------------------------------------------------ Homing and Target Bounce

        /// <summary>Homing curves toward the closest visible Cuphead enemy, never the shooter or the partner (ROUNDS uses
        /// PlayerManager.GetClosestPlayer, shooter included). Steering maths unchanged (Homing.cs:41-103).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Homing), "Update")]
        public static bool HomeOnEnemy(Homing __instance)
        {
            if (!Live) return true;
            try
            {
                var move = HMove(__instance);
                if (move == null) return false;
                Vector2 pos = __instance.transform.position;
                var t = CupheadTarget.Closest(pos, float.PositiveInfinity, true);
                bool on = false;
                if (t != null)
                {
                    Vector2 c = t.Centre;
                    Vector3 goal = (Vector3)c + __instance.transform.right * move.selectedSpread * Vector2.Distance(pos, c) * __instance.spread;
                    float ang = Vector3.Angle(__instance.transform.root.forward, goal - __instance.transform.position);
                    if (ang < 70f)
                    {
                        on = true;
                        float dt = TimeHandler.deltaTime;
                        move.velocity -= move.velocity * ang * dt * __instance.scalingDrag;
                        move.velocity -= move.velocity * dt * __instance.drag;
                        move.velocity += Vector3.ClampMagnitude(goal - __instance.transform.position, 1f) * dt * move.localForce.magnitude * 0.025f * __instance.amount;
                        move.velocity.z = 0f;
                        move.velocity += Vector3.up * dt * move.gravity * move.multiplier;
                    }
                }
                if (on && !HOn(__instance))
                {
                    move.simulateGravity++;
                    if (HSound(__instance))
                    {
                        HSound(__instance) = false;
                        if (__instance.soundHomingFound != null) SoundManager.Instance.PlayAtPosition(__instance.soundHomingFound, SoundManager.Instance.GetTransform(), __instance.transform);
                    }
                }
                else if (!on && HOn(__instance))
                {
                    move.simulateGravity--;
                    HSound(__instance) = true;
                }
                HOn(__instance) = on;
                var flicks = HFlicks(__instance);
                if (flicks != null) foreach (var f in flicks) if (f != null) f.isOn = on;
                if (__instance.rot1 != null) __instance.rot1.target = on ? 10f : 50f;
                if (__instance.rot2 != null) __instance.rot2.target = on ? -10f : -50f;
            }
            catch (Exception e) { Fail("homing", e); }
            return false;
        }

        /// <summary>Target Bounce re-aims at the closest visible Cuphead enemy after a bounce: the same 0.1 s pause, line and
        /// set-velocity call as BounceEffectRetarget.cs:127-206, never toward the shooter or the partner.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(BounceEffectRetarget), "DoBounce")]
        public static bool RetargetEnemy(BounceEffectRetarget __instance, HitInfo hit)
        {
            if (!Live || hit == null) return true;
            try { __instance.StartCoroutine(RetargetTo(__instance, hit)); }
            catch (Exception e) { Fail("target bounce", e); }
            return false;
        }

        private static IEnumerator RetargetTo(BounceEffectRetarget b, HitInfo hit)
        {
            var move = BrMove(b);
            if (move == null) move = b.GetComponentInParent<MoveTransform>();
            var view = BrView(b);
            bool mine = view == null || view.IsMine;
            var t = RetargetStart(b, hit, move, mine);
            yield return new WaitForSeconds(0.1f);
            if (mine && b != null && move != null) RetargetEnd(b, move, t);
        }

        private static CupheadTarget RetargetStart(BounceEffectRetarget b, HitInfo hit, MoveTransform move, bool mine)
        {
            CupheadTarget t = null;
            try
            {
                t = CupheadTarget.Closest((Vector2)b.transform.position + hit.normal * 0.1f, float.PositiveInfinity, true);
                if (t != null && mine) b.StartCoroutine(DrawLineTo(b, t.transform));
                if (move != null)
                {
                    move.enabled = false;
                    var trail = hit.rigidbody != null ? move.GetComponent<RayCastTrail>() : null;
                    if (trail != null) trail.IgnoreRigFor(hit.rigidbody, 0.5f);
                }
            }
            catch (Exception e) { Fail("target bounce", e); }
            return t;
        }

        private static void RetargetEnd(BounceEffectRetarget b, MoveTransform move, CupheadTarget t)
        {
            try
            {
                Vector2 pos = b.transform.position, v = move.velocity;
                if (t != null && t.Alive)
                {
                    Vector2 c = t.Centre;
                    v = (c + Vector2.up * move.GetUpwardsCompensation(pos, c) - pos).normalized * move.velocity.magnitude;
                    if (b.soundTargetBounceTargetPlayer != null) SoundManager.Instance.PlayAtPosition(b.soundTargetBounceTargetPlayer, SoundManager.Instance.GetTransform(), b.transform);
                }
                var rpc = b.GetComponentInParent<ChildRPC>();
                if (rpc != null) rpc.CallFunction("TargetBounce", v);    // SetNewVel: move.enabled = true, move.velocity = v
                else { move.enabled = true; move.velocity = v; }
            }
            catch (Exception e) { Fail("target bounce", e); if (move != null) move.enabled = true; }
        }

        private static IEnumerator DrawLineTo(BounceEffectRetarget b, Transform target)
        {
            var line = b != null ? b.GetComponentInChildren<LineEffect>(true) : null;
            if (line == null) yield break;
            line.StartDraw();
            while (line != null && target != null && b != null)
            {
                line.DrawLine(b.transform.position, target.position);
                yield return null;
            }
        }

        // ------------------------------------------------------------------ Radar Shot and Chase

        /// <summary>Radar Shot scans for the closest visible Cuphead enemy within range (measured to its closest point: a boss's
        /// centre is far from its edge) and auto-fires attack-level shots at it (RadarShot.cs:20-60). Never at the partner.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(RadarShot), "Go")]
        public static bool RadarEnemy(RadarShot __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady) return true;
            var pl = RdPlayer(__instance);
            if (a.SlotOf(pl) < 0) return true;
            try
            {
                var t = CupheadTarget.Closest(pl.transform.position, __instance.range, true);
                if (t == null) return false;
                __instance.StartCoroutine(Boops(__instance, t));
                var lv = __instance.GetComponent<AttackLevel>();
                if (pl.data.view == null || pl.data.view.IsMine) __instance.StartCoroutine(RadarShoot(RdWh(__instance), pl, t, lv != null ? lv.attackLevel : 1));
            }
            catch (Exception e) { Fail("radar shot", e); }
            return false;
        }

        private static IEnumerator Boops(RadarShot r, CupheadTarget t)
        {
            if (r.boops == null) yield break;
            foreach (var b in r.boops) if (b != null) b.Play();
            float c = 0f;
            while (c < 1f && t != null && r != null)
            {
                c += TimeHandler.deltaTime;
                Vector3 at = t.Centre;
                foreach (var b in r.boops) if (b != null) b.transform.position = at;
                yield return null;
            }
        }

        private static IEnumerator RadarShoot(WeaponHandler wh, Player pl, CupheadTarget t, int shots)
        {
            for (int i = 0; i < shots; i++)
            {
                yield return new WaitForSeconds(0.1f);
                if (t == null || pl == null || wh == null || wh.gun == null) yield break;
                try
                {
                    var gun = wh.gun;
                    Vector3 at = t.Centre;
                    float dist = Vector3.Distance(at, pl.transform.position);
                    float comp = Mathf.Pow(dist, 2f) * 0.015f / gun.projectileSpeed;      // Gun.GetRangeCompensation (Gun.cs:249-252)
                    ForceDir(gun) = comp * Vector3.up + at - pl.transform.position;
                    gun.Attack(0f, true, 1f, 1f, false);
                }
                catch (Exception e) { Fail("radar shot", e); }
                finally { if (wh != null && wh.gun != null) ForceDir(wh.gun) = Vector3.zero; }
            }
        }

        private static readonly Dictionary<Chase, int> chaseGroup = new Dictionary<Chase, int>();

        /// <summary>Chase's +60% speed turns on while moving within 70 degrees toward a visible Cuphead enemy (Chase.cs:87-122;
        /// ROUNDS takes the closest other-team player: nobody solo, the partner in co-op). The line re-points when the chased
        /// enemy changes, which ROUNDS's currentTarget check meant to do.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Chase), "Update")]
        public static bool ChaseEnemy(Chase __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady) return true;
            var me = ChPlayer(__instance);
            if (a.SlotOf(me) < 0) return true;
            try
            {
                Vector3 dir = me.data.input.direction;
                var t = CupheadTarget.Closest(__instance.transform.position, float.PositiveInfinity, true);
                if (t != null && (dir == Vector3.zero || Vector2.Angle(t.Centre - (Vector2)__instance.transform.position, dir) > 70f)) t = null;
                var le = ChLine(__instance);
                if (t != null)
                {
                    int g;
                    if (ChCurrent(__instance) != me || !chaseGroup.TryGetValue(__instance, out g) || g != t.group)
                    {
                        ChCurrent(__instance) = me;
                        if (!chaseGroup.ContainsKey(__instance)) Prune(chaseGroup);
                        chaseGroup[__instance] = t.group;
                        __instance.switchTargetEvent?.Invoke();
                        if (le != null) le.Play(__instance.transform, t.transform);
                    }
                    if (!ChOn(__instance))
                    {
                        ChOn(__instance) = true;
                        __instance.turnOnEvent?.Invoke();
                    }
                }
                else
                {
                    if (ChOn(__instance))
                    {
                        ChOn(__instance) = false;
                        __instance.turnOffEvent?.Invoke();
                    }
                    if (le != null && le.isPlaying)
                    {
                        le.Stop();
                        le.gameObject.SetActive(false);
                    }
                    ChCurrent(__instance) = null;
                    chaseGroup.Remove(__instance);
                }
            }
            catch (Exception e) { Fail("chase", e); }
            return false;
        }

        // ------------------------------------------------------------------ Saw, Abyssal Countdown, Radiance

        /// <summary>Saw and Abyssal Countdown saw the closest visible Cuphead enemy within range x scale: damage per second as
        /// continuous hits (summed per frame) plus both of ROUNDS's force terms (Saw.cs:26-82). The partner is never shoved.
        /// Abyssal's saw sits under the player, where Saw.Start finds no owner (Saw.cs:23); its own SpawnedAttack names it.</summary>
        /// <summary>Saw.Start reads transform.root's SpawnedAttack, which Abyssal's saw under the player does not have (an NRE
        /// in plain ROUNDS too, leaving owner null): the nearest SpawnedAttack names the owner, else the player it sits on.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Saw), "Start")]
        public static bool SawOwner(Saw __instance)
        {
            var sa = __instance.transform.root.GetComponent<SpawnedAttack>() ?? __instance.GetComponentInParent<SpawnedAttack>();
            __instance.owner = sa != null && sa.spawner != null ? sa.spawner : __instance.GetComponentInParent<Player>();
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Saw), "Update")]
        public static bool SawEnemies(Saw __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady) return true;
            if (__instance.owner == null)
            {
                var sa = __instance.GetComponentInParent<SpawnedAttack>();
                __instance.owner = sa != null && sa.spawner != null ? sa.spawner : __instance.GetComponentInParent<Player>();
            }
            var owner = __instance.owner;
            if (a.SlotOf(owner) < 0) return true;
            try
            {
                Vector2 c = __instance.transform.position;
                float sc = __instance.transform.localScale.x, dt = TimeHandler.deltaTime;
                var t = CupheadTarget.Closest(c, __instance.range * sc, true);
                bool on = t != null;
                if (on)
                {
                    Vector2 at = t.Col.bounds.ClosestPoint(c), n = (at - c).normalized;
                    if (n == Vector2.zero) n = (t.Centre - c).normalized;
                    if (n == Vector2.zero) n = Vector2.up;
                    if (__instance.damage != 0f) t.Hit(dt * __instance.damage * n, at, owner, ArenaLink.HitContinuous, Color.black);
                    if (__instance.force != 0f)
                    {
                        float k = Mathf.Clamp01(1f - Vector2.Distance(c, at) / __instance.range);
                        var fd = new Vector2(n.x, n.y * 0.5f);
                        t.AddForce(fd * sc * k * dt * __instance.force, ForceMode2D.Force, 0f, true);           // playerVel.AddForce(..., Force)
                        t.AddForce(fd * k * 0.0005f * dt * __instance.force, ForceMode2D.Impulse, 0f, true);    // healthHandler.TakeForce
                    }
                    if (__instance.sparkTransform != null)
                    {
                        __instance.sparkTransform.position = at;
                        __instance.sparkTransform.rotation = Quaternion.LookRotation(n);
                    }
                    Feel((n + (Vector2)UnityEngine.Random.onUnitSphere).normalized * __instance.shake * dt * 20f);
                }
                if (__instance.parts != null)
                    foreach (var ps in __instance.parts)
                    {
                        if (ps == null) continue;
                        if (on && !ps.isPlaying) ps.Play();
                        else if (!on && ps.isPlaying) ps.Stop();
                    }
            }
            catch (Exception e) { Fail("saw", e); }
            return false;
        }

        /// <summary>Radiance: each sun ring hits the closest visible Cuphead enemy once, as its radius passes the enemy's
        /// nearest edge, with ROUNDS's damage and push (LineRangeEffect.cs:23-41). The partner is no longer hit or pushed.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LineRangeEffect), "Update")]
        public static bool RadianceRing(LineRangeEffect __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady) return true;
            var sp = LrSpawned(__instance);
            if (sp == null || a.SlotOf(sp.spawner) < 0) return true;
            try
            {
                if (LrDone(__instance) || !sp.IsMine()) return false;
                var le = LrLine(__instance);
                Vector2 c = __instance.transform.position;
                var t = CupheadTarget.Closest(c, float.PositiveInfinity, true);
                if (t == null || le == null || LineRadius == null) return false;
                float rad = (float)LineRadius.Invoke(le, null), s = __instance.transform.localScale.x;
                Vector2 at = t.Col.bounds.ClosestPoint(c);
                float d = Vector2.Distance(c, at);
                if (d < rad + 2f && d > rad - 2f)
                {
                    LrDone(__instance) = true;
                    Vector2 dir = (at - c).normalized;
                    if (dir == Vector2.zero) dir = Vector2.up;
                    t.Hit(__instance.dmg * s * dir, at, sp.spawner, ArenaLink.HitArea, Color.black);
                    t.AddForce(__instance.knockback * s * dir, ForceMode2D.Impulse, 0f);
                }
            }
            catch (Exception e) { Fail("radiance", e); }
            return false;
        }

        // ------------------------------------------------------------------ Chilling Presence and Lifestealer

        private static readonly Dictionary<PlayerInRangeTrigger, CupheadTarget> chosen = new Dictionary<PlayerInRangeTrigger, CupheadTarget>();

        /// <summary>The enemy an arena player's "other player" aura is on right now (its slow, drain and particle), or null.</summary>
        public static CupheadTarget AuraTarget(Component c)
        {
            if (c == null) return null;
            var trig = c.GetComponent<PlayerInRangeTrigger>();
            if (trig == null) trig = c.GetComponentInParent<PlayerInRangeTrigger>();
            CupheadTarget t;
            return trig != null && chosen.TryGetValue(trig, out t) && t != null && t.Alive ? t : null;
        }

        /// <summary>An "other player" aura of one of the arena's players: its victims are Cuphead enemies.</summary>
        private static bool OurAura(PlayerInRangeTrigger t)
        {
            var a = Arena.I;
            return a != null && a.WorldReady && t != null && t.targetType == PlayerInRangeTrigger.TargetType.OtherPlayer && a.SlotOf(TrOwn(t)) >= 0;
        }

        /// <summary>Chilling Presence and Lifestealer fire on the closest visible Cuphead enemy within range x the owner's scale,
        /// on ROUNDS's cooldown (PlayerInRangeTrigger.cs:48-77). ROUNDS uses GetOtherPlayer: nobody solo, the partner in co-op.
        /// The heal (RegenerateAfterStandStill) stays native: it reads inRange.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerInRangeTrigger), "Update")]
        public static bool AuraOnEnemies(PlayerInRangeTrigger __instance)
        {
            if (!OurAura(__instance)) return true;
            try
            {
                TrCounter(__instance) += TimeHandler.deltaTime;
                __instance.inRange = false;
                __instance.target = null;
                if (TrDone(__instance)) return false;
                var t = CupheadTarget.Closest(__instance.transform.position, __instance.range * __instance.transform.root.localScale.x, true);
                if (t == null) { chosen.Remove(__instance); return false; }
                if (!chosen.ContainsKey(__instance)) Prune(chosen);
                chosen[__instance] = t;
                if (TrCounter(__instance) >= __instance.cooldown)
                {
                    TrCounter(__instance) = 0f;
                    __instance.triggerEvent?.Invoke();
                    __instance.inRange = true;
                    if (!__instance.repeating) TrDone(__instance) = true;
                }
            }
            catch (Exception e) { Fail("aura", e); }
            return false;
        }

        /// <summary>Chilling Presence: ROUNDS's additive slow (slowAmount x level, up to maxSlow + 0.25 per extra level,
        /// PlayerInRangeSlow.cs:25-31) on the aura's enemy.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerInRangeSlow), "Update")]
        public static bool ChillEnemy(PlayerInRangeSlow __instance)
        {
            var trig = SlTrigger(__instance);
            if (!OurAura(trig)) return true;
            try
            {
                CupheadTarget t;
                if (trig.inRange && chosen.TryGetValue(trig, out t) && t != null && t.Alive)
                {
                    var lvl = SlLevel(__instance);
                    float lv = lvl != null ? lvl.attackLevel : 1f;
                    t.AddSlowAddative(__instance.slowAmount * lv, __instance.maxSlow + (lv - 1f) * 0.25f);
                }
            }
            catch (Exception e) { Fail("chilling presence", e); }
            return false;
        }

        /// <summary>Lifestealer's drain hits the aura's enemy (ROUNDS caches GetOtherPlayer and would hit the partner or throw,
        /// DealDamageToPlayer.cs:30-46). Demonic Pact's own cost (targetPlayer Own) stays native.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(DealDamageToPlayer), "Go")]
        public static bool DrainEnemy(DealDamageToPlayer __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady || __instance.targetPlayer != DealDamageToPlayer.TargetPlayer.Other) return true;
            var data = DdData(__instance);
            if (data == null || a.SlotOf(data.player) < 0) return true;
            try
            {
                var t = AuraTarget(__instance);
                if (t == null) return false;
                if (__instance.soundDamage != null) SoundManager.Instance.Play(__instance.soundDamage, t.transform);
                t.Hit(__instance.damage * Vector2.up, t.Centre, data.player, ArenaLink.HitAura, Color.black);
            }
            catch (Exception e) { Fail("lifestealer", e); }
            return false;
        }

        // ------------------------------------------------------------------ Shield Charge

        private class ChargeState { public readonly HashSet<int> hit = new HashSet<int>(); public float last = -1f; }
        private static readonly Dictionary<ShieldCharge, ChargeState> charges = new Dictionary<ShieldCharge, ChargeState>();

        /// <summary>Shield Charge bumps each Cuphead enemy group once per charge, as RPCA_Collide does to a player
        /// (ShieldCharge.cs:135-150): damage, knockback, your recoil and stop, and the automatic block. ROUNDS only fires it
        /// on player-against-player contact, which enemy hit areas never make.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(ShieldCharge), "Update")]
        public static void ChargeIntoEnemies(ShieldCharge __instance)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady) return;
            try
            {
                var data = ScData(__instance);
                if (data == null || a.SlotOf(data.player) < 0 || (data.view != null && !data.view.IsMine)) return;
                ChargeState st;
                if (!charges.TryGetValue(__instance, out st)) { Prune(charges); charges[__instance] = st = new ChargeState(); }
                float bt = ScBlockTime(__instance);
                if (bt < 0f) { st.hit.Clear(); st.last = bt; return; }       // Collide ignores contacts once blockTime < 0 (:154)
                if (bt > st.last) st.hit.Clear();                              // a new charge (DoCharge clears hitDatas, :94)
                st.last = bt;
                var circle = data.GetComponent<CircleCollider2D>();
                float r = (circle != null ? circle.radius : 0.5f) * data.transform.localScale.x;
                Vector2 me = data.transform.position;
                Vector2 dir = ScDir(__instance);
                var lvl = ScLevel(__instance);
                int lv = lvl != null ? lvl.LevelsUp() : 0;
                foreach (var col in Physics2D.OverlapCircleAll(me, r, 1 << Arena.TargetLayer))
                {
                    var t = col != null ? col.GetComponent<CupheadTarget>() : null;
                    if (t == null || !t.Alive || !st.hit.Add(t.group)) continue;
                    ScCancel(__instance) = true;
                    if (__instance.hitPart != null)
                    {
                        if (dir != Vector2.zero) __instance.hitPart.transform.rotation = Quaternion.LookRotation(dir);
                        __instance.hitPart.Play();
                    }
                    t.Hit(dir * (__instance.damage + lv * __instance.damagePerLevel), col.bounds.ClosestPoint(me), data.player, ArenaLink.HitContact, Color.black);
                    t.AddForce(dir * (__instance.knockBack + lv * __instance.knockBackPerLevel), ForceMode2D.Impulse, 0f);
                    data.healthHandler.TakeForce(-dir * __instance.knockBack, ForceMode2D.Impulse, false, true);
                    data.healthHandler.TakeForce(-dir * __instance.stopForce, ForceMode2D.Impulse, true, true);
                    data.block.RPCA_DoBlock(false, true, BlockTrigger.BlockTriggerType.ShieldCharge);
                    Feel(dir * __instance.shake);
                }
            }
            catch (Exception e) { Fail("shield charge", e); }
        }

        /// <summary>Co-op: charging into your partner no longer knocks them back (ShieldCharge.cs:144's TakeForce skips PartnerDamage).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ShieldCharge), "Collide")]
        public static bool NoPartnerCharge(Player player) => Arena.I == null || Arena.I.SlotOf(player) < 0;

        // ------------------------------------------------------------------ Thruster

        private static CupheadTarget ThrusterTarget(Thruster th)
        {
            var f = ThFollow(th);
            return f != null && f.target != null ? f.target.GetComponent<CupheadTarget>() : null;
        }

        /// <summary>A thruster stuck to a light enemy lasts the full player time; ROUNDS cuts it to x0.2 for anything that is
        /// not a player (Thruster.cs:34-46), which bosses keep.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Thruster), "Start")]
        public static void ThrusterLife(Thruster __instance)
        {
            if (!Live) return;
            try
            {
                var t = ThrusterTarget(__instance);
                if (t == null || !t.Light) return;
                const float undo = 1f / 0.2f;
                var ps = __instance.GetComponentInChildren<ParticleSystem>();
                if (ps != null && !ps.isPlaying) { var main = ps.main; main.duration *= undo; }   // a playing system kept its duration
                var de = __instance.GetComponent<DelayEvent>();
                if (de != null) de.time *= undo;
                var ra = __instance.GetComponent<RemoveAfterSeconds>();
                if (ra != null) ra.seconds *= undo;
            }
            catch (Exception e) { Fail("thruster", e); }
        }

        /// <summary>The thruster pushes the enemy it is stuck to every physics step, TakeForce(startForward x force, Force) as on a
        /// player (Thruster.cs:62); ROUNDS switches itself off without a Player root (:69-77).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Thruster), "FixedUpdate")]
        public static bool ThrusterPush(Thruster __instance)
        {
            if (!Live) return true;
            try
            {
                var t = ThrusterTarget(__instance);
                if (t == null) return true;
                if (t.Alive) t.AddForce(ThFwd(__instance) * __instance.force, ForceMode2D.Force, 0f, true);
            }
            catch (Exception e) { Fail("thruster", e); }
            return false;
        }

        // ------------------------------------------------------------------ Remote and Teleport

        /// <summary>Remote steers toward the Cuphead cursor (the keyboard player's mouse, sent in the Slot2 block) or else the aim
        /// Cuphead sends. RemoteControl.Update picks its steering direction from the device aim or, for a player without a device,
        /// the hidden window's mouse through MainCam, which sends every bullet straight up. ROUNDS's own Update still runs: this
        /// only hands it <see cref="RemoteAim"/> at the point where both of its aim branches meet, just before the spread is
        /// added (the first "aim + Cross(forward, aim) * spread").</summary>
        [HarmonyTranspiler, HarmonyPatch(typeof(RemoteControl), "Update")]
        public static IEnumerable<CodeInstruction> SteerWithCuphead(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var forward = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.forward));
            var cross = AccessTools.Method(typeof(Vector3), nameof(Vector3.Cross));
            var aim = AccessTools.Method(typeof(ArenaTargetPatches), nameof(RemoteAim));
            for (int i = 0; i + 3 < list.Count; i++)
            {
                // ldloc aim; call Vector3.forward; ldloc aim; call Vector3.Cross
                if (!list[i].IsLdloc() || !list[i + 1].Calls(forward) || !list[i + 2].IsLdloc() || !list[i + 3].Calls(cross)) continue;
                var store = StoreFor(list[i]);
                if (store == null) break;
                // aim = RemoteAim(this, aim); the merge point's labels move to the first inserted instruction
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                first.labels.AddRange(list[i].labels);
                list[i].labels.Clear();
                list.InsertRange(i, new[] { first, new CodeInstruction(list[i].opcode, list[i].operand), new CodeInstruction(OpCodes.Call, aim), store });
                return list;
            }
            EnginePlugin.L.LogWarning("arena hook: RemoteControl.Update's aim was not found; Remote bullets keep ROUNDS's own aim");
            return list;
        }

        /// <summary>The stloc that matches an ldloc of the same local.</summary>
        private static CodeInstruction StoreFor(CodeInstruction load)
        {
            var op = load.opcode;
            if (op == OpCodes.Ldloc_0) return new CodeInstruction(OpCodes.Stloc_0);
            if (op == OpCodes.Ldloc_1) return new CodeInstruction(OpCodes.Stloc_1);
            if (op == OpCodes.Ldloc_2) return new CodeInstruction(OpCodes.Stloc_2);
            if (op == OpCodes.Ldloc_3) return new CodeInstruction(OpCodes.Stloc_3);
            if (op == OpCodes.Ldloc_S) return new CodeInstruction(OpCodes.Stloc_S, load.operand);
            if (op == OpCodes.Ldloc) return new CodeInstruction(OpCodes.Stloc, load.operand);
            return null;
        }

        /// <summary>The direction a Remote bullet steers to: for an arena player's bullet, toward the Cuphead cursor while that
        /// player's cursor is on, else the aim Cuphead sends; anything else keeps the direction ROUNDS picked.</summary>
        public static Vector3 RemoteAim(RemoteControl rc, Vector3 rounds)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady || rc == null) return rounds;
            try
            {
                var sp = RcSpawned(rc);
                if (sp == null || sp.spawner == null) return rounds;
                int s = a.SlotOf(sp.spawner);
                if (s < 0) return rounds;
                int x = ArenaLink.Slot2(s);
                if (a.link.I(x + ArenaLink.U_CursorOn) == 0) return sp.spawner.data.input.aimDirection;
                Vector3 dir = (Vector3)a.ToRounds(a.link.F(x + ArenaLink.U_CursorX), a.link.F(x + ArenaLink.U_CursorY)) - rc.transform.position;
                dir.z = 0f;
                return dir.normalized;
            }
            catch (Exception e) { Fail("remote", e); return rounds; }
        }

        /// <summary>Where the teleport starts (DelayMove steps from the Teleport's own position, Teleport.cs:45).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Teleport), "Go")]
        public static void TeleportFrom(Teleport __instance, out Vector3 __state) { __state = __instance.transform.position; }

        /// <summary>Teleport never leaves the level: ROUNDS takes the last free spot of 10 steps along the aim (Teleport.cs:39-75),
        /// which a double Teleport (12+ units) finds beyond Cuphead's 10.8-unit bound walls or ceiling. When it landed outside
        /// the level view, the same steps run again keeping only spots inside it. The move is synchronous except for Empower's,
        /// which lands a frame later at the impact point and is left alone.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Teleport), "Go")]
        public static void TeleportInside(Teleport __instance, BlockTrigger.BlockTriggerType triggerType, Vector3 __state)
        {
            var a = Arena.I;
            if (a == null || !a.WorldReady || !ArenaViewCam.HasView || triggerType == BlockTrigger.BlockTriggerType.Empower) return;
            try
            {
                var data = TpData(__instance);
                var lvl = TpLevel(__instance);
                if (data == null || lvl == null || a.SlotOf(data.player) < 0) return;
                Rect r = ArenaViewCam.Level;
                var root = __instance.transform.root;
                Vector3 now = root.position;
                if (r.Contains(now)) return;
                Vector3 pos = __state;
                Vector3 best = r.Contains(__state) ? __state : new Vector3(Mathf.Clamp(__state.x, r.xMin + 0.5f, r.xMax - 0.5f), Mathf.Clamp(__state.y, r.yMin + 0.5f, r.yMax - 0.5f), __state.z);
                float step = __instance.distance * lvl.attackLevel / 10f;
                for (int i = 0; i < 10; i++)
                {
                    pos += step * data.aimDirection;
                    if (r.Contains(pos) && !Physics2D.OverlapCircle(pos, 0.5f)) best = pos;
                }
                best.z = now.z;
                root.position = best;
                if (__instance.parts != null) foreach (var ps in __instance.parts) if (ps != null) ps.transform.position = best;
            }
            catch (Exception e) { Fail("teleport", e); }
        }
    }

    /// <summary>PCE Fireworks pop in the top of the Cuphead view: FireworkEffect measures MainCam (FireworkEffect.cs:108, 131),
    /// so it is handed <see cref="ArenaViewCam"/>. Found by name; skipped when PCE is not installed.</summary>
    [HarmonyPatch]
    public static class ArenaFireworkPatch
    {
        private static readonly Type Firework = AccessTools.TypeByName("PCE.MonoBehaviours.FireworkEffect");

        private static bool Prepare() => Firework != null && AccessTools.Method(Firework, "Start") != null;

        private static MethodBase TargetMethod() => AccessTools.Method(Firework, "Start");

        [HarmonyPostfix]
        public static void FireworkOnCupheadView(MonoBehaviour __instance)
        {
            if (Arena.I == null || !Arena.I.WorldReady || ArenaViewCam.Cam == null) return;
            try
            {
                var f = Traverse.Create(__instance).Field("mainCam");
                if (f.FieldExists()) f.SetValue(ArenaViewCam.Cam);
            }
            catch (Exception e) { Arena.I.LogOnce("fireworks", e); }
        }
    }
}
