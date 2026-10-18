using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CupRounds
{
    /// <summary>
    /// The real ROUNDS game running Roundhead's players (sheets/components.json "arena"). A map built from Cuphead's level
    /// starts ROUNDS's own sandbox (GM_Test), so the players are ROUNDS's players: its movement, gun, bullets, block,
    /// health and every card effect, mod cards included. Cuphead's level shapes and the boss's hit areas are mirrored in
    /// each frame through <see cref="ArenaLink"/>; ROUNDS sends back where its players are, bullet hits and its picture.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class Arena : MonoBehaviour
    {
        public static Arena I;
        public ArenaLink link;

        /// <summary>The arena sits this far below ROUNDS's menu and card-pick area (ROUNDS units).</summary>
        public const float OffsetY = -1000f;
        private const int PlatformLayer0 = 6, PlatformLayer1 = 7;
        /// <summary>Walls (layer 0), the bullets' copy of Cuphead floors (17, in every bullet mask and Sneaky's ground mask,
        /// never in ignoreWallsMask), and enemy hit areas (15, in every bullet mask).</summary>
        public const int SolidLayer = 0, BulletFloorLayer = 17, TargetLayer = 15;

        private enum Phase { Idle, MapStarting, Ready }
        private Phase phase = Phase.Idle;
        private GameObject mapGo;
        private readonly Player[] players = new Player[2];
        private readonly bool[] creating = new bool[2];
        private readonly int[] seenShoot = new int[2];
        private readonly int[] seenJump = new int[2], seenBlock = new int[2], seenAccept = new int[2], seenNav = new int[2], blockCount = new int[2];
        private readonly float[] dropUntil = new float[2];
        private readonly Vector2[] lastMouse = { new Vector2(-1, -1), new Vector2(-1, -1) };
        private readonly string[][] wantedCards = new string[2][];
        private int eventRead;
        private int levelSeen = -1;
        private int pickSeen;
        private bool picking;
        private int pickSlot = -1;
        private readonly Dictionary<int, Mirror> mirrors = new Dictionary<int, Mirror>();
        private readonly HashSet<int> seenIds = new HashSet<int>();
        private float scale = 37f;
        private int mode;
        private float lastControlBeat = -1f;
        private int lastBeatValue = -1;
        private bool hadCuphead;
        private float nextPidCheck;
        private bool paused;
        private readonly float[] lastFeet = { float.NegativeInfinity, float.NegativeInfinity }; // per player body height, last FixedUpdate
        private readonly int[] life = new int[2]; // the last Cuphead life (EvSpawn) done per slot, echoed in Q_Life
        private readonly string[][] lastCards = new string[2][];
        private readonly HashSet<string> stepErrors = new HashSet<string>();
        public bool Paused => paused;
        /// <summary>True while a Cuphead hit is being applied to a ROUNDS player (it has no ROUNDS attacker).</summary>
        public static bool CupheadHit;

        private static readonly AccessTools.FieldRef<PlayerVelocity, Vector2> VelRef = AccessTools.FieldRefAccess<PlayerVelocity, Vector2>("velocity");
        private static readonly AccessTools.FieldRef<PlayerVelocity, bool> SimRef = AccessTools.FieldRefAccess<PlayerVelocity, bool>("simulated");
        private static readonly AccessTools.FieldRef<PlayerVelocity, float> MassRef = AccessTools.FieldRefAccess<PlayerVelocity, float>("mass");

        private class Mirror
        {
            public int id, kind, shape, points;
            public readonly float[] data = new float[32];
            public GameObject[] objects = new GameObject[0]; // solid/target: [0]; ground: [p0, p1, bullets]; platform: [p0, p1]
            public float top;
            /// <summary>The floor's outline in ROUNDS world space for the one-way test (<see cref="Outline"/>): box corners or
            /// polygon points (pn of them, wind = +1 counter-clockwise / -1 clockwise), or a circle (pn 0, r &gt; 0); its x-range.
            /// flat: an unrotated box, whose top is its surface everywhere.</summary>
            public readonly float[] px = new float[16], py = new float[16];
            public int pn, wind;
            public bool flat;
            public float cx, cy, r, minX, maxX;
        }

        public static void Create(GameObject host)
        {
            var link = ArenaLink.Open();
            if (link == null) { EnginePlugin.L.LogWarning("arena: could not open shared memory"); return; }
            I = host.AddComponent<Arena>();
            I.link = link;
            // A ROUNDS started (or started again) while Cuphead runs only acts on what Cuphead sends from now on.
            I.eventRead = link.I(ArenaLink.C_EventWrite);
            I.pickSeen = link.I(ArenaLink.C_Pick + ArenaLink.P_Serial);
            link.I(ArenaLink.ResultOff + ArenaLink.R_Magic, ArenaLink.Magic);
            link.I(ArenaLink.ResultOff + ArenaLink.R_Pid, System.Diagnostics.Process.GetCurrentProcess().Id);
            // The status table's sequence lock starts even: a ROUNDS that died while writing it (WriteEffects) left it odd.
            int sf = link.I(ArenaLink.ResultOff + ArenaLink.R_StatusFrame);
            if ((sf & 1) != 0) link.I(ArenaLink.ResultOff + ArenaLink.R_StatusFrame, sf + 1);
            host.AddComponent<ArenaRenderer>();
            host.AddComponent<ArenaViewCam>();
            var harmony = new Harmony("roundhead.arena");
            foreach (var t in typeof(Arena).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0 && !t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Any(m => m.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)) continue;
                try { harmony.CreateClassProcessor(t, true).Patch(); }
                catch (Exception e) { EnginePlugin.L.LogWarning("arena: patches in " + t.Name + " not applied: " + e.Message); }
            }
            // Patches on mod classes (found by name at runtime, so the engine needs no mod at build time).
            try { ArenaModHooks.Apply(harmony); } catch (Exception e) { EnginePlugin.L.LogWarning("arena: mod hooks: " + e.Message); }
            EnginePlugin.L.LogInfo("arena: shared memory " + ArenaLink.LastOpenNote);
        }

        public Vector2 ToRounds(float cx, float cy) => new Vector2(cx / scale, cy / scale + OffsetY);
        public Vector2 ToCuphead(Vector2 r) => new Vector2(r.x * scale, (r.y - OffsetY) * scale);
        public int Mode => mode;
        public bool Picking => picking;
        public bool WorldReady => phase == Phase.Ready;

        public int SlotOf(Player p)
        {
            for (int i = 0; i < 2; i++) if (players[i] != null && players[i] == p) return i;
            return -1;
        }

        public int SlotOf(CharacterData d) => d == null ? -1 : SlotOf(d.player);

        public int SlotOfPlayerId(int playerId)
        {
            for (int i = 0; i < 2; i++) if (players[i] != null && players[i].playerID == playerId) return i;
            return -1;
        }

        // ------------------------------------------------------------------ main loop

        private void Update()
        {
            // Heartbeat first: Cuphead calls ROUNDS gone when this beat stands still, so nothing below may stop it.
            link.I(ArenaLink.ResultOff + ArenaLink.R_Magic, ArenaLink.Magic);
            link.Inc(ArenaLink.ResultOff + ArenaLink.R_Beat);
            int beat = link.I(ArenaLink.C_Beat);
            if (beat != lastBeatValue) { lastBeatValue = beat; lastControlBeat = Time.unscaledTime; }
            bool cupheadAlive = link.I(ArenaLink.C_Magic) == ArenaLink.Magic && Time.unscaledTime - lastControlBeat < 3f;
            if (cupheadAlive) hadCuphead = true;
            // Only Cuphead's process going away ends this hidden ROUNDS. A Cuphead that stops beating for a while (alt-tab,
            // minimised, a long load) comes back, and ROUNDS's players must still be here when it does.
            if (EnginePlugin.Hidden && Time.unscaledTime > nextPidCheck)
            {
                nextPidCheck = Time.unscaledTime + 2f;
                EnginePlugin.CheckCuphead(link);
            }

            // A quiet Cuphead (in the background, loading) keeps its last mode and ROUNDS's world waits, frozen in time.
            if (cupheadAlive) mode = link.I(ArenaLink.C_Mode);
            paused = !cupheadAlive || link.I(ArenaLink.C_Paused) != 0;
            float s = link.F(ArenaLink.C_Scale);
            if (s > 1f) scale = s;
            Step("world", () =>
            {
                if (phase != Phase.Idle && mapGo == null) LoseWorld();
                // The sandbox starts as soon as Cuphead is there, so the map's card pick can use ROUNDS's own pick screen.
                if (cupheadAlive && phase == Phase.Idle) StartWorld();
                if (phase == Phase.MapStarting && GameManager.instance != null && GameManager.instance.isPlaying && MapManager.instance.currentMap != null) phase = Phase.Ready;
            });

            if (phase == Phase.Ready)
            {
                Step("players", SpawnPlayers);
                Step("level", CheckLevel);
                Step("mirrors", SyncMirrors);
                Step("events", ReadEvents);
                Step("status", () => { float dt = TimeHandler.deltaTime; foreach (var st in status.Values) st.Tick(dt); });
                Step("cards", ApplyWantedCards);
                Step("park", ParkOrPlay);
                Step("pick", CheckPick);
                Step("log", LogState);
            }
            Step("result", WriteResult);
        }

        private void SpawnPlayers()
        {
            for (int i = 0; i < 2; i++)
                if (players[i] == null && !creating[i] && (link.I(ArenaLink.Slot(i) + ArenaLink.S_Present) != 0 || i == 0))
                    StartCoroutine(MakePlayer(i));
        }

        private void CheckLevel()
        {
            int level = link.I(ArenaLink.C_Level);
            if (level != levelSeen) { levelSeen = level; ClearMirrors(); }
        }

        /// <summary>One arena step: a failure is logged once per message and the frame goes on, so results keep flowing.</summary>
        private void Step(string name, Action step)
        {
            try { step(); }
            catch (Exception e) { if (stepErrors.Add(name + ": " + e.Message)) EnginePlugin.L.LogError("arena " + name + " failed: " + e); }
        }

        /// <summary>ROUNDS unloaded its scene: the sandbox, the players and the mirrors are gone, so build them again.</summary>
        private void LoseWorld()
        {
            EnginePlugin.L.LogWarning("arena: the ROUNDS sandbox was unloaded; starting it again");
            StopAllCoroutines();
            if (picking) link.I(ArenaLink.ResultOff + ArenaLink.R_PickDone, pickSeen); // stopped coroutines skip their finally
            phase = Phase.Idle;
            mirrors.Clear();
            levelSeen = -1;
            picking = false;
            pickSlot = -1;
            for (int i = 0; i < 2; i++) { players[i] = null; creating[i] = false; if (lastCards[i] != null) wantedCards[i] = lastCards[i]; }
        }

        private void LateUpdate()
        {
            // After every card effect of this frame: summed DoT / continuous hits, coalesced effect events, the status table.
            if (phase == Phase.Ready) Step("effects", WriteEffects);
        }

        private void FixedUpdate()
        {
            if (phase != Phase.Ready) return;
            // Cuphead's floors let you jump up through them: each player's copy of a floor is solid only while the player's body
            // (its centre) is above the copy's surface under it (slopes and rotated ground: the surface at the player's x, not the
            // shape's top). Not the feet: ROUNDS's collision pushes out softly (PlayerCollision.cs:100-106), so a hard landing sinks
            // the feet well into the floor while the body stays about a radius above it; jumping up through, the floor comes on
            // once the centre is past the surface and ROUNDS lifts the player onto it.
            for (int i = 0; i < 2; i++)
            {
                var p = players[i];
                if (p == null) continue;
                Vector3 pos = p.transform.position;
                float body = pos.y, was = lastFeet[i];
                lastFeet[i] = body;
                // Above it last step counts too: a fast fall can pass the surface in one step, and ROUNDS's own
                // go-through-wall ray (PlayerCollision.FixedUpdate) only catches it while the floor is still solid.
                float high = Mathf.Max(body, was);
                bool dropping = Time.time < dropUntil[i];
                foreach (var m in mirrors.Values)
                {
                    if (m.kind != ArenaLink.KindGround && m.kind != ArenaLink.KindPlatform) continue;
                    var go = m.objects.Length > i ? m.objects[i] : null;
                    if (go == null) continue;
                    bool on = m.shape != ArenaLink.ShapeOff && high > Surface(m, pos.x, high + 0.35f) && !(dropping && m.kind == ArenaLink.KindPlatform);
                    var col = go.GetComponent<Collider2D>();
                    if (col != null && col.enabled != on) col.enabled = on;
                }
            }
        }

        /// <summary>A floor copy's surface height under x (clamped into the shape's x-range): an unrotated box's top; a circle's
        /// centre.y + sqrt(r^2 - dx^2); for a rotated box or a polygon, where the vertical line meets a top edge (an edge with
        /// the shape below it). Of several top edges (a concave shape) the highest at or below 'upTo' (the one the player is
        /// on); with none there, the lowest. For a convex shape that is simply the highest intersection with its edges.</summary>
        private static float Surface(Mirror m, float x, float upTo)
        {
            if (m.flat) return m.top;
            if (m.pn == 0)
            {
                if (m.r <= 0f) return m.top;
                float dx = Mathf.Clamp(x - m.cx, -m.r, m.r);
                return m.cy + Mathf.Sqrt(Mathf.Max(0f, m.r * m.r - dx * dx));
            }
            x = Mathf.Clamp(x, m.minX, m.maxX);
            float best = float.NegativeInfinity, low = float.PositiveInfinity;
            for (int k = 0; k < m.pn; k++)
            {
                int j = k + 1 < m.pn ? k + 1 : 0;
                float x0 = m.px[k], x1 = m.px[j], ex = x1 - x0;
                // Counter-clockwise, the inside is left of each edge: a top edge runs right to left (clockwise: left to right).
                if (ex * m.wind >= 0f || x < Mathf.Min(x0, x1) || x > Mathf.Max(x0, x1)) continue;
                float y = m.py[k] + (x - x0) * (m.py[j] - m.py[k]) / ex;
                if (y <= upTo && y > best) best = y;
                if (y < low) low = y;
            }
            return best > float.NegativeInfinity ? best : low < float.PositiveInfinity ? low : m.top;
        }

        private static float Radius(Player p)
        {
            var c = p.GetComponent<CircleCollider2D>();
            return c != null ? c.radius * p.transform.localScale.x : 0.7f;
        }

        // ------------------------------------------------------------------ world and players

        private void StartWorld()
        {
            phase = Phase.MapStarting;
            // A Map appearing while ROUNDS is not playing starts ROUNDS's own sandbox (Map.Awake -> GM_Test), offline.
            var scene = SceneManager.CreateScene("Roundhead Arena");
            mapGo = new GameObject("Roundhead Cuphead Level");
            mapGo.SetActive(false);
            SceneManager.MoveGameObjectToScene(mapGo, scene);
            mapGo.transform.position = Vector3.zero;
            var map = mapGo.AddComponent<Map>();
            map.wasSpawned = true;
            var sp = new GameObject("Spawn");
            sp.transform.SetParent(mapGo.transform, false);
            sp.transform.position = new Vector3(0f, OffsetY + 5f, 0f);
            sp.AddComponent<SpawnPoint>();
            mapGo.SetActive(true);
            EnginePlugin.L.LogInfo("arena: ROUNDS sandbox map created");
        }

        private IEnumerator MakePlayer(int slot)
        {
            creating[slot] = true;
            try
            {
                foreach (object step in MakePlayerSteps(slot)) yield return step;
            }
            finally { creating[slot] = false; }
        }

        private IEnumerable MakePlayerSteps(int slot)
        {
            // Slots fill in order so ROUNDS player ids match Cuphead's player numbers.
            while (slot == 1 && players[0] == null) yield return null;
            int before = PlayerAssigner.instance.players.Count;
            yield return StartCoroutine(PlayerAssigner.instance.CreatePlayer(null, false));
            // CreatePlayer gives up silently (max players, waiting for Photon): never wait forever.
            float giveUp = Time.unscaledTime + 5f;
            while (PlayerAssigner.instance.players.Count <= before)
            {
                if (Time.unscaledTime > giveUp) { EnginePlugin.L.LogWarning("arena: ROUNDS did not make player " + (slot + 1) + "; trying again"); yield break; }
                yield return null;
            }
            var data = PlayerAssigner.instance.players[PlayerAssigner.instance.players.Count - 1];
            Traverse.Create(PlayerAssigner.instance).Method("SetPlayersCanJoin", false).GetValue();
            var p = data.player;
            players[slot] = p;
            yield return null;
            try { p.GetFaceOffline(); } catch (Exception e) { EnginePlugin.L.LogWarning("arena face: " + e.Message); }
            // Each player stands on its own copy of Cuphead's floors (they are one-way per player).
            int own = slot == 0 ? PlatformLayer0 : PlatformLayer1;
            var pc = p.GetComponent<PlayerCollision>();
            pc.mask = (pc.mask | (1 << own)) & ~(1 << BulletFloorLayer);
            foreach (var l in p.GetComponentsInChildren<LegRaycasters>(true))
            {
                l.mask = (l.mask | (1 << own)) & ~(1 << BulletFloorLayer);
                // The drawn feet (IkLeg) cast their own ground ray with their own mask: without the player's floor layer they
                // never plant or step and just drag along. LegRaycasters listed them in Awake, before Unparent can move them.
                var legs = Traverse.Create(l).Field("legs").GetValue<IkLeg[]>();
                if (legs != null) foreach (var leg in legs) if (leg != null) leg.mask = (leg.mask | (1 << own)) & ~(1 << BulletFloorLayer);
            }
            int feet = 0;
            foreach (var leg in p.GetComponentsInChildren<IkLeg>(true)) { leg.mask = (leg.mask | (1 << own)) & ~(1 << BulletFloorLayer); feet++; }
            EnginePlugin.L.LogInfo("arena: player " + p.playerID + " feet step on layer " + own + " (" + feet + " legs in the player)");
            foreach (var w in p.GetComponentsInChildren<WallRayCaster>(true)) w.mask = (w.mask | (1 << own)) & ~(1 << BulletFloorLayer);
            var gm = Traverse.Create(data).Field("groundMask");
            gm.SetValue((LayerMask)((((LayerMask)gm.GetValue()).value | (1 << own)) & ~(1 << BulletFloorLayer)));
            // Only real blocks start a Cuphead parry: Empower's remote block (an impact effect) does not.
            data.block.BlockAction = (Action<BlockTrigger.BlockTriggerType>)Delegate.Combine(data.block.BlockAction, new Action<BlockTrigger.BlockTriggerType>(t => { if (t != BlockTrigger.BlockTriggerType.Empower) blockCount[slot]++; }));
            foreach (var m in mirrors.Values) IgnoreOthers(m);
            Teleport(slot, ToCuphead(new Vector2(0f, OffsetY + 5f)));
            life[slot] = -1; // a fresh body has done no Cuphead life yet: Cuphead re-sends its spawn, then it is followed
            if (lastCards[slot] != null) wantedCards[slot] = lastCards[slot];
            EnginePlugin.L.LogInfo("arena: ROUNDS player " + p.playerID + " is Cuphead player " + (slot + 1));
            DumpLook(p);
        }

        /// <summary>Developer aid (ROUNDHEAD_DEV_DUMP set): how ROUNDS draws this player and its gun, renderer by renderer.</summary>
        private static void DumpLook(Player p)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_DUMP"))) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                var skinH = p.GetComponentInChildren<PlayerSkinHandler>(true);
                sb.Append("arena look: player " + p.playerID + " simpleSkin=" + (skinH != null ? skinH.simpleSkin.ToString() : "?"));
                var skin = PlayerSkinBank.GetPlayerSkinColors(p.playerID);
                if (skin != null) sb.Append(" color=" + ColorUtility.ToHtmlStringRGBA(skin.color) + " background=" + ColorUtility.ToHtmlStringRGBA(skin.backgroundColor) + " particle=" + ColorUtility.ToHtmlStringRGBA(skin.particleEffect));
                var h = p.GetComponent<Holding>();
                var roots = new List<Transform> { p.transform };
                if (h != null && h.holdable != null) roots.Add(h.holdable.transform);
                foreach (var root in roots)
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        var parts = new List<string>();
                        foreach (var c in t.GetComponents<Component>())
                        {
                            if (c is SpriteRenderer sr) parts.Add("SpriteRenderer(" + (sr.enabled ? "on" : "off") + " " + (sr.sprite != null ? sr.sprite.name : "-") + " " + ColorUtility.ToHtmlStringRGBA(sr.color) + " " + sr.sortingLayerName + "/" + sr.sortingOrder + " mask=" + sr.maskInteraction + " " + Shader(sr.sharedMaterial) + ")");
                            else if (c is SpriteMask m) parts.Add("SpriteMask(" + (m.enabled ? "on" : "off") + " " + (m.sprite != null ? m.sprite.name : "-") + " " + (m.isCustomRangeActive ? SortingLayer.IDToName(m.backSortingLayerID) + "/" + m.backSortingOrder + ".." + SortingLayer.IDToName(m.frontSortingLayerID) + "/" + m.frontSortingOrder : "all") + ")");
                            else if (c is ParticleSystem ps)
                            {
                                var main = ps.main;
                                var r = ps.GetComponent<ParticleSystemRenderer>();
                                parts.Add("ParticleSystem(" + (ps.isPlaying ? "playing" : "stopped") + " " + ps.particleCount + "p " + ColorUtility.ToHtmlStringRGBA(main.startColor.colorMin) + "-" + ColorUtility.ToHtmlStringRGBA(main.startColor.colorMax)
                                    + (r != null ? " " + r.sortingLayerName + "/" + r.sortingOrder + " mask=" + r.maskInteraction + " " + Shader(r.sharedMaterial) : "") + ")");
                            }
                            else if (c is SetTeamColor stc) parts.Add("SetTeamColor(" + stc.colorType + ")");
                            else if (c is LineRenderer lr) parts.Add("LineRenderer(" + (lr.enabled ? "on" : "off") + " " + ColorUtility.ToHtmlStringRGBA(lr.startColor) + ")");
                            else if (c is MeshRenderer mr) parts.Add("MeshRenderer(" + (mr.enabled ? "on" : "off") + " " + (mr.sharedMaterial != null && mr.sharedMaterial.HasProperty("_Color") ? ColorUtility.ToHtmlStringRGBA(mr.sharedMaterial.color) : "-") + " " + Shader(mr.sharedMaterial) + ")");
                        }
                        if (parts.Count == 0) continue;
                        sb.Append("\n  " + (root == p.transform ? "player" : "gun") + Rel(t, root) + (t.gameObject.activeInHierarchy ? "" : " [inactive]") + " " + string.Join(" ", parts.ToArray()));
                    }
                // Every component on the parts that give ROUNDS its look, and on its main camera (soft-shadow renderer).
                foreach (var t in new[] { p.transform.Find("Art/Health"), h != null && h.holdable != null ? h.holdable.transform.Find("Spring/Handle") : null, MainCam.instance != null ? MainCam.instance.cam.transform : null })
                {
                    if (t == null) continue;
                    var names = new List<string>();
                    foreach (var c in t.GetComponents<Component>()) if (c != null) names.Add(c.GetType().Name + (c is Behaviour b && !b.enabled ? "[off]" : ""));
                    sb.Append("\n  components of " + t.name + ": " + string.Join(", ", names.ToArray()));
                }
                // The particle skin lives outside the player (ROUNDS unparents it): every skin particle system in the scene.
                foreach (var sp in UnityEngine.Object.FindObjectsOfType<PlayerSkinParticle>())
                {
                    var ps = sp.GetComponent<ParticleSystem>();
                    if (ps == null) continue;
                    var main = ps.main;
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    sb.Append("\n  skin " + Rel(sp.transform, null) + " at " + sp.transform.position.ToString("0.0") + " ParticleSystem(" + (ps.isPlaying ? "playing" : "stopped") + " " + ps.particleCount + "p " + ColorUtility.ToHtmlStringRGBA(main.startColor.colorMin) + "-" + ColorUtility.ToHtmlStringRGBA(main.startColor.colorMax)
                        + (r != null ? " " + r.sortingLayerName + "/" + r.sortingOrder + " mask=" + r.maskInteraction + " " + Shader(r.sharedMaterial) : "") + ")");
                }
                EnginePlugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena look: " + e.Message); }
        }

        private static string Shader(Material m) => m != null && m.shader != null ? m.shader.name : "-";
        private static string Rel(Transform t, Transform root) => t == root || t == null ? "" : Rel(t.parent, root) + "/" + t.name;

        /// <summary>Where a player stood when it was parked (a card pick in the middle of a level): it comes back there when
        /// play goes on, instead of staying under the level.</summary>
        private readonly Vector3?[] parkedFrom = new Vector3?[2];

        private void ParkOrPlay()
        {
            for (int i = 0; i < 2; i++)
            {
                var p = players[i];
                if (p == null) continue;
                bool play = mode == 1 && link.I(ArenaLink.Slot(i) + ArenaLink.S_Present) != 0;
                if (play == p.data.isPlaying) continue;
                if (!play && paused && mode == 1) continue; // Cuphead is only quiet: keep the player, frozen
                p.data.isPlaying = play;
                SimRef(p.data.playerVel) = play;
                if (!play)
                {
                    parkedFrom[i] = p.transform.position;
                    p.transform.position = new Vector3(i * 6f, OffsetY - 400f, 0f);
                }
                else if (parkedFrom[i].HasValue)
                {
                    Teleport(i, ToCuphead(parkedFrom[i].Value));
                    parkedFrom[i] = null;
                }
            }
        }

        public void Teleport(int slot, Vector2 cupheadPos)
        {
            var p = players[slot];
            if (p == null) return;
            p.transform.position = ToRounds(cupheadPos.x, cupheadPos.y);
            VelRef(p.data.playerVel) = Vector2.zero;
            // ROUNDS's gravity grows with the time off the ground (Gravity.FixedUpdate: sinceGrounded ^ exponent): a body that
            // waited parked or fell before the level's floors came would arrive falling hard enough to pass a floor.
            p.data.sinceGrounded = 0f;
            p.data.sinceWallGrab = float.PositiveInfinity; // its "never grabbed" default (no free wall jump)
            lastFeet[slot] = float.NegativeInfinity;
            p.GetComponent<PlayerCollision>().IgnoreWallForFrames(3);
        }

        // ------------------------------------------------------------------ input (GeneralInput prefix)

        public void FeedInput(GeneralInput g, int slot)
        {
            g.ResetInput();
            var data = players[slot].data;
            int s = ArenaLink.Slot(slot);
            int jumps = link.I(s + ArenaLink.S_JumpPresses), blocks = link.I(s + ArenaLink.S_BlockPresses), shots = link.I(s + ArenaLink.S_ShootPresses);
            bool jumpPressed = jumps != seenJump[slot], blockPressed = blocks != seenBlock[slot], shotPressed = shots != seenShoot[slot];
            seenJump[slot] = jumps;
            seenBlock[slot] = blocks;
            seenShoot[slot] = shots;
            if (GameManager.lockInput || g.stunnedInput || !data.isPlaying || mode != 1) return;
            Vector2 move = new Vector2(link.F(s + ArenaLink.S_MoveX), link.F(s + ArenaLink.S_MoveY));
            g.direction = EightWay(move);
            if (g.direction != Vector3.zero) g.latestPressedDirection = g.direction;
            Vector3 aim = new Vector3(link.F(s + ArenaLink.S_AimX), link.F(s + ArenaLink.S_AimY), 0f);
            if (aim.sqrMagnitude > 0.0001f) aim.Normalize();
            g.aimDirection = aim;
            if (g.aimDirection != Vector3.zero) g.aimDirection += Vector3.up * 0.13f / Mathf.Clamp(data.weaponHandler.gun.projectileSpeed, 1f, 100f);
            if (g.aimDirection != Vector3.zero) g.lastAimDirection = g.aimDirection;
            bool held = link.I(s + ArenaLink.S_JumpHeld) != 0;
            // Down + jump on a Cuphead platform drops through it instead of jumping (Cuphead's rule).
            if (jumpPressed && move.y < -0.5f && data.isGrounded) { dropUntil[slot] = Time.time + 0.35f; jumpPressed = false; held = false; }
            g.jumpIsPressed = held;
            g.jumpWasPressed = jumpPressed;
            if (!g.silencedInput)
            {
                g.shootIsPressed = link.I(s + ArenaLink.S_ShootHeld) != 0;
                // ROUNDS guns fire on the press (auto fire only when cards make them fast); clicks between frames still count.
                g.shootWasPressed = shotPressed || (g.shootIsPressed && !wasShooting[slot]);
                g.shootWasReleased = !g.shootIsPressed && wasShooting[slot];
                g.shieldWasPressed = blockPressed;
            }
            wasShooting[slot] = g.shootIsPressed;
        }

        private readonly bool[] wasShooting = new bool[2];

        private static Vector3 EightWay(Vector2 v)
        {
            if (v.magnitude < 0.3f) return Vector3.zero;
            float a = Mathf.Round(Mathf.Atan2(v.y, v.x) / (Mathf.PI / 4f)) * (Mathf.PI / 4f);
            return new Vector3(Mathf.Round(Mathf.Cos(a) * 1000f) / 1000f, Mathf.Round(Mathf.Sin(a) * 1000f) / 1000f, 0f);
        }

        // ------------------------------------------------------------------ Cuphead's level mirrored in ROUNDS

        private void ClearMirrors()
        {
            foreach (var m in mirrors.Values) foreach (var o in m.objects) if (o != null) Destroy(o);
            mirrors.Clear();
            status.Clear();
            agg.Clear();
        }

        // ------------------------------------------------------------------ enemy statuses (ROUNDS rules, sent to Cuphead)

        public float Scale => scale;
        private readonly Dictionary<int, TargetStatus> status = new Dictionary<int, TargetStatus>();

        public TargetStatus Status(int g)
        {
            TargetStatus s;
            if (!status.TryGetValue(g, out s)) status[g] = s = new TargetStatus { group = g };
            return s;
        }

        /// <summary>PlayerImmunity.IsImune (PlayerImmunity.cs:24-42) for an enemy, returning whether the hit lands: a named source
        /// still on cooldown blocks it unless its damage (Explosion.cs:100: scale x damage x rangeMultiplier) beats the stored
        /// one, which then takes over with a fresh window. Windows run on ROUNDS's scaled clock (TargetStatus.Tick), so they
        /// stand still while Cuphead is paused.</summary>
        public bool Immunity(int g, string name, float secs, float dmg)
        {
            var s = Status(g);
            TargetStatus.Immunity im;
            if (s.immune.TryGetValue(name, out im) && im.time > 0f && dmg <= im.dmg) return false;
            if (im == null) s.immune[name] = im = new TargetStatus.Immunity();
            im.time = secs;
            im.dmg = dmg;
            return true;
        }

        private readonly HashSet<string> onceErrors = new HashSet<string>();
        public void LogOnce(string what, Exception e)
        {
            if (onceErrors.Add(what + e.Message)) EnginePlugin.L.LogWarning("arena " + what + ": " + e);
        }

        public static int Rgba(Color c) => ArenaLink.Rgba(c.r, c.g, c.b, c.a);

        private struct AggKey
        {
            public int id, slot, kind;
            public override int GetHashCode() => (id * 397) ^ (slot * 31) ^ kind;
            public override bool Equals(object o) => o is AggKey && ((AggKey)o).id == id && ((AggKey)o).slot == slot && ((AggKey)o).kind == kind;
        }
        private struct AggHit { public float dmg; public Vector2 pos; public int rgba; }
        private readonly Dictionary<AggKey, AggHit> agg = new Dictionary<AggKey, AggHit>();

        public void PostFx(int group, int type, float a, float b = 0f, float c = 0f, float d = 0f, int slot = -1) => link.PostFx(group, type, a, b, c, d, slot);

        private static readonly System.Reflection.MethodInfo MuData = AccessTools.Method(AccessTools.TypeByName("ModdingUtils.Extensions.CharacterStatModifiersExtension"), "GetAdditionalData");

        /// <summary>ModdingUtils' gravity-on-damage (Flip, Grounded) only runs on a HealthHandler: read it from the attacker.</summary>
        public void GravityOnHit(CupheadTarget t, Player by)
        {
            if (MuData == null || by == null) return;
            try
            {
                var tr = Traverse.Create(MuData.Invoke(null, new object[] { by.data.stats }));
                float dur = tr.Field("gravityDurationOnDoDamage").GetValue<float>(), mul = tr.Field("gravityMultiplierOnDoDamage").GetValue<float>();
                if (dur > 0f && mul != 1f) t.AddGravity(mul, dur);
            }
            catch (Exception e) { LogOnce("gravity", e); }
        }

        /// <summary>Once per frame: summed damage-over-time / continuous hits, coalesced effect events, the status table.</summary>
        private void WriteEffects()
        {
            foreach (var kv in agg) link.PostHit(kv.Key.id, kv.Value.dmg, kv.Value.pos.x, kv.Value.pos.y, kv.Key.slot, kv.Key.kind, kv.Value.rgba);
            agg.Clear();
            foreach (var s in status.Values)
            {
                if (s.fxSlow > 0f) link.PostFx(s.group, ArenaLink.FxSlow, s.fxSlow, s.fxFast ? 1f : 0f);
                if (s.fxStun > 0f) link.PostFx(s.group, ArenaLink.FxStun, s.fxStun);
                if (s.fxSilence > 0f) link.PostFx(s.group, (s.flags & ArenaLink.StConfused) != 0 ? ArenaLink.FxConfuse : ArenaLink.FxSilence, s.fxSilence);
                if (s.dv != Vector2.zero) link.PostFx(s.group, ArenaLink.FxForce, s.dv.x, s.dv.y, s.flying, s.cont ? 1f : 0f);
                s.fxSlow = s.fxStun = s.fxSilence = s.flying = 0f;
                s.fxFast = s.cont = false;
                s.dv = Vector2.zero;
            }
            // The table under its sequence lock (ArenaLink.R_StatusFrame): odd while the rows and count are written, even once
            // they are all in. The finally writes the count of rows done and makes it even again even when a row throws, so a
            // reader is never locked out and never takes rows past the count.
            int r = ArenaLink.ResultOff, n = 0, fo = r + ArenaLink.R_StatusFrame;
            int f = link.I(fo) & ~1; // even (a ROUNDS that died mid-write left it odd)
            link.I(fo, f + 1);
            System.Threading.Thread.MemoryBarrier();
            try
            {
                foreach (var s in status.Values)
                {
                    if (!s.Any) continue;
                    if (n >= ArenaLink.MaxStatus) break;
                    int e = r + ArenaLink.R_Status + n++ * ArenaLink.StatusSize;
                    link.I(e + ArenaLink.T_Group, s.group);
                    link.F(e + ArenaLink.T_Slow, s.Slow);
                    link.F(e + ArenaLink.T_Stun, s.stun);
                    link.F(e + ArenaLink.T_Silence, s.silence);
                    link.F(e + ArenaLink.T_Dot, s.dot);
                    link.I(e + ArenaLink.T_DotRgba, s.dotRgba);
                    link.I(e + ArenaLink.T_Flags, s.flags);
                    link.F(e + ArenaLink.T_Aux, s.aux);
                }
            }
            finally
            {
                link.I(r + ArenaLink.R_StatusCount, n);
                System.Threading.Thread.MemoryBarrier();
                link.I(fo, f + 2);
            }
        }

        private int syncs;
        private readonly List<int> ended = new List<int>();

        private void SyncMirrors()
        {
            int n = Mathf.Min(link.I(ArenaLink.C_ColCount), ArenaLink.MaxCols);
            seenIds.Clear();
            syncs++;
            for (int i = 0; i < n; i++)
            {
                int e = ArenaLink.C_Cols + i * ArenaLink.ColSize;
                int id = link.I(e + ArenaLink.K_Id);
                if (id == 0) continue;
                seenIds.Add(id);
                int kind = link.I(e + ArenaLink.K_Kind);
                Mirror m;
                if (!mirrors.TryGetValue(id, out m) || m.kind != kind)
                {
                    if (m != null) foreach (var o in m.objects) if (o != null) Destroy(o);
                    m = NewMirror(id, kind);
                    mirrors[id] = m;
                }
                int shape = link.I(e + ArenaLink.K_Shape);
                int pts = Mathf.Clamp(link.I(e + ArenaLink.K_Points), 0, 16);
                bool changed = shape != m.shape || pts != m.points;
                for (int k = 0; k < 32; k++)
                {
                    float v = link.F(e + ArenaLink.K_Data + k * 4);
                    if (v != m.data[k]) { m.data[k] = v; changed = true; }
                }
                m.shape = shape;
                m.points = pts;
                if (changed) Shape(m);
                if (kind == ArenaLink.KindTarget && m.objects.Length > 0 && m.objects[0] != null)
                {
                    var ct = m.objects[0].GetComponent<CupheadTarget>();
                    int g = link.I(e + ArenaLink.K_Group);
                    ct.group = g != 0 ? g : id;
                    // An off hit area (collider or receiver disabled or inactive, ArenaClient.WriteCols) keeps its mirror but
                    // not its enemy's life.
                    if (shape != ArenaLink.ShapeOff)
                    {
                        var st = Status(ct.group);
                        if (!st.alive) st.EndLife(); // back from the dead (a pooled or reused receiver): starts clean
                        st.alive = true;
                        st.seen = syncs;
                        st.flagsIn = link.I(e + ArenaLink.K_Flags);
                        // Cuphead says no statuses now (intro, boss phase change, KO, level end): what is running stops too.
                        if ((st.flagsIn & ArenaLink.TfNoStatus) != 0) st.ClearTransient();
                    }
                }
            }
            if (mirrors.Count != seenIds.Count)
                foreach (int id in mirrors.Keys.Where(k => !seenIds.Contains(k)).ToList())
                {
                    foreach (var o in mirrors[id].objects) if (o != null) Destroy(o);
                    mirrors.Remove(id);
                }
            // An enemy with no live mirror left (all off or out of the table: it died, or was deactivated to be reused) ends its
            // life: its statuses go, Gold and damage over time included, and its DoT ticks stop (CupheadTarget.Dot). Two syncs
            // in a row: the collider table has no lock, so one torn read can miss a live enemy for a frame.
            foreach (var s in status.Values)
                if (s.alive && s.seen < syncs - 1) { s.EndLife(); ended.Add(s.group); }
            if (ended.Count == 0) return;
            foreach (var m in mirrors.Values)
            {
                if (m.kind != ArenaLink.KindTarget || m.objects.Length == 0 || m.objects[0] == null) continue;
                var ct = m.objects[0].GetComponent<CupheadTarget>();
                if (ct != null && ended.Contains(ct.group)) ct.StopAllCoroutines();
            }
            ended.Clear();
        }

        private Mirror NewMirror(int id, int kind)
        {
            var m = new Mirror { id = id, kind = kind, shape = -1 };
            switch (kind)
            {
                case ArenaLink.KindGround:
                    m.objects = new[] { Obj("floor p1", PlatformLayer0), Obj("floor p2", PlatformLayer1), Obj("floor bullets", BulletFloorLayer) };
                    break;
                case ArenaLink.KindPlatform:
                    m.objects = new[] { Obj("platform p1", PlatformLayer0), Obj("platform p2", PlatformLayer1) };
                    break;
                case ArenaLink.KindTarget:
                    var t = Obj("cuphead target " + id, TargetLayer);
                    var ct = t.AddComponent<CupheadTarget>();
                    ct.id = id;
                    ct.group = id;
                    // RayHitPoison looks for a DamageOverTime on what it hit; the effect patches turn it into enemy poison.
                    t.AddComponent<DamageOverTime>();
                    m.objects = new[] { t };
                    break;
                default:
                    m.objects = new[] { Obj(id < 0 ? "level bound" : "wall", SolidLayer) };
                    break;
            }
            IgnoreOthers(m);
            return m;
        }

        private GameObject Obj(string name, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(mapGo.transform, false);
            return go;
        }

        /// <summary>A player's floors never touch the other player.</summary>
        private void IgnoreOthers(Mirror m)
        {
            if (m.kind != ArenaLink.KindGround && m.kind != ArenaLink.KindPlatform) return;
            for (int slot = 0; slot < 2; slot++)
            {
                var p = players[slot];
                if (p == null) continue;
                var other = m.objects.Length > 1 - slot ? m.objects[1 - slot] : null;
                if (other == null) continue;
                foreach (var oc in other.GetComponents<Collider2D>())
                    foreach (var pc in p.GetComponentsInChildren<Collider2D>(true)) Physics2D.IgnoreCollision(oc, pc, true);
            }
        }

        private void Shape(Mirror m)
        {
            foreach (var go in m.objects)
            {
                if (go == null) continue;
                // Only a collider of the wrong type goes; the right one is reused (moving bosses update in place).
                System.Type want = m.shape == ArenaLink.ShapeBox ? typeof(BoxCollider2D) : m.shape == ArenaLink.ShapeCircle ? typeof(CircleCollider2D)
                    : m.shape == ArenaLink.ShapePoly && m.points >= 3 ? typeof(PolygonCollider2D) : null;
                foreach (var c in go.GetComponents<Collider2D>()) if (c.GetType() != want) DestroyImmediate(c);
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                Collider2D col = null;
                if (m.shape == ArenaLink.ShapeBox)
                {
                    // data: centre x, y, width, height, angle (deg), Cuphead units
                    Vector2 c = ToRounds(m.data[0], m.data[1]);
                    go.transform.position = c;
                    go.transform.rotation = Quaternion.Euler(0f, 0f, m.data[4]);
                    var b = go.GetComponent<BoxCollider2D>() ?? go.AddComponent<BoxCollider2D>();
                    b.size = new Vector2(Mathf.Max(0.01f, m.data[2] / scale), Mathf.Max(0.01f, m.data[3] / scale));
                    col = b;
                }
                else if (m.shape == ArenaLink.ShapeCircle)
                {
                    Vector2 c = ToRounds(m.data[0], m.data[1]);
                    go.transform.position = c;
                    var ci = go.GetComponent<CircleCollider2D>() ?? go.AddComponent<CircleCollider2D>();
                    ci.radius = Mathf.Max(0.01f, m.data[2] / scale);
                    col = ci;
                }
                else if (m.shape == ArenaLink.ShapePoly && m.points >= 3)
                {
                    // Centred on its points, so things that follow the target (sticky bombs, Dazzle, Thruster) sit on it.
                    var pts = new Vector2[m.points];
                    Vector2 cen = Vector2.zero;
                    for (int k = 0; k < m.points; k++)
                    {
                        pts[k] = ToRounds(m.data[k * 2], m.data[k * 2 + 1]);
                        cen += pts[k];
                    }
                    cen /= m.points;
                    go.transform.position = cen;
                    for (int k = 0; k < pts.Length; k++) pts[k] -= cen;
                    var pc = go.GetComponent<PolygonCollider2D>() ?? go.AddComponent<PolygonCollider2D>();
                    pc.points = pts;
                    col = pc;
                }
                if (col != null && m.kind == ArenaLink.KindTarget)
                {
                    col.isTrigger = true;
                    var ct = go.GetComponent<CupheadTarget>();
                    if (ct != null) ct.Col = col;
                }
            }
            if (m.kind == ArenaLink.KindGround || m.kind == ArenaLink.KindPlatform) Outline(m);
            IgnoreOthers(m);
        }

        /// <summary>The shape's outline in ROUNDS world space, the same numbers the colliders above are built from: box corners
        /// (counter-clockwise, rotated by data[4] as the box is), polygon points with their winding, or the circle; plus the
        /// x-range and the highest point (top). Kept per mirror for the one-way floor test (<see cref="Surface"/>).</summary>
        private void Outline(Mirror m)
        {
            m.pn = 0; m.wind = 1; m.flat = false; m.r = 0f;
            m.top = float.NegativeInfinity;
            if (m.shape == ArenaLink.ShapeBox)
            {
                Vector2 c = ToRounds(m.data[0], m.data[1]);
                float hx = Mathf.Max(0.01f, m.data[2] / scale) * 0.5f, hy = Mathf.Max(0.01f, m.data[3] / scale) * 0.5f;
                float a = m.data[4] * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                for (int k = 0; k < 4; k++)
                {
                    float lx = k == 1 || k == 2 ? hx : -hx, ly = k >= 2 ? hy : -hy; // (-,-) (+,-) (+,+) (-,+)
                    m.px[k] = c.x + lx * cs - ly * sn;
                    m.py[k] = c.y + lx * sn + ly * cs;
                }
                m.pn = 4;
                m.flat = Mathf.Abs(Mathf.DeltaAngle(m.data[4], 0f)) < 0.01f || Mathf.Abs(Mathf.DeltaAngle(m.data[4], 180f)) < 0.01f;
            }
            else if (m.shape == ArenaLink.ShapeCircle)
            {
                Vector2 c = ToRounds(m.data[0], m.data[1]);
                m.cx = c.x; m.cy = c.y; m.r = Mathf.Max(0.01f, m.data[2] / scale);
                m.minX = c.x - m.r; m.maxX = c.x + m.r;
                m.top = c.y + m.r;
                return;
            }
            else if (m.shape == ArenaLink.ShapePoly && m.points >= 3)
            {
                float area = 0f;
                for (int k = 0; k < m.points; k++)
                {
                    Vector2 w = ToRounds(m.data[k * 2], m.data[k * 2 + 1]);
                    m.px[k] = w.x; m.py[k] = w.y;
                }
                for (int k = 0; k < m.points; k++)
                {
                    int j = k + 1 < m.points ? k + 1 : 0;
                    area += m.px[k] * m.py[j] - m.px[j] * m.py[k];
                }
                m.pn = m.points;
                m.wind = area >= 0f ? 1 : -1;
            }
            if (m.pn == 0) return;
            m.minX = float.PositiveInfinity; m.maxX = float.NegativeInfinity;
            for (int k = 0; k < m.pn; k++)
            {
                m.minX = Mathf.Min(m.minX, m.px[k]); m.maxX = Mathf.Max(m.maxX, m.px[k]);
                m.top = Mathf.Max(m.top, m.py[k]);
            }
        }

        public void TargetHit(int id, float damage, Vector2 roundsPos, Player by, int kind = ArenaLink.HitBullet, Color c = default(Color))
        {
            Vector2 p = ToCuphead(roundsPos);
            int slot = by != null ? SlotOf(by) : -1;
            int rgba = Rgba(c);
            if (kind == ArenaLink.HitDot || kind == ArenaLink.HitContinuous)
            {
                var k = new AggKey { id = id, slot = slot, kind = kind };
                AggHit a;
                agg.TryGetValue(k, out a);
                a.dmg += damage;
                a.pos = p;
                a.rgba = rgba;
                agg[k] = a;
                return;
            }
            link.PostHit(id, damage, p.x, p.y, slot, kind, rgba);
        }

        // ------------------------------------------------------------------ events from Cuphead

        private void ReadEvents()
        {
            int write = link.I(ArenaLink.C_EventWrite);
            if (write - eventRead > ArenaLink.EventRing) eventRead = write - ArenaLink.EventRing;
            while (eventRead < write)
            {
                int e = ArenaLink.C_Events + (eventRead % ArenaLink.EventRing) * ArenaLink.EventSize;
                eventRead++;
                int type = link.I(e + ArenaLink.E_Type), slot = link.I(e + ArenaLink.E_Slot);
                if (slot < 0 || slot > 1 || players[slot] == null) continue;
                var p = players[slot];
                float a = link.F(e + ArenaLink.E_A), b = link.F(e + ArenaLink.E_B), c = link.F(e + ArenaLink.E_C), d = link.F(e + ArenaLink.E_D);
                try
                {
                    switch (type)
                    {
                        case ArenaLink.EvBlockedHit:
                            // A Cuphead hit that landed during the ROUNDS block: ROUNDS sees it blocked (card mods record the block).
                            if (p.data.block.sinceBlock < 0.3f) p.data.healthHandler.CallTakeDamage(new Vector2(b, c).normalized * Mathf.Max(1f, a), p.transform.position);
                            break;
                        case ArenaLink.EvDamage:
                        {
                            CupheadHit = true;
                            Vector2 dir = new Vector2(b, c);
                            if (dir.sqrMagnitude < 0.01f) dir = Vector2.up;
                            p.data.healthHandler.CallTakeDamage(dir.normalized * a, p.transform.position);
                            if (d > 0f) p.data.healthHandler.CallTakeForce(dir.normalized * d * MassRef(p.data.playerVel), ForceMode2D.Impulse);
                            CupheadHit = false;
                            break;
                        }
                        case ArenaLink.EvRevive:
                            p.data.healthHandler.Revive(true);
                            break;
                        case ArenaLink.EvSpawn:
                        {
                            // A new Cuphead life (level start, retry, co-op revive, ROUNDS started again): ROUNDS's own full
                            // revive at Cuphead's spot, once per life number, echoed back in Q_Life.
                            int serial = link.I(e + ArenaLink.E_Life);
                            if (serial == life[slot]) break;
                            p.data.healthHandler.Revive(true);
                            Teleport(slot, new Vector2(a, b));
                            life[slot] = serial;
                            break;
                        }
                        case ArenaLink.EvTeleport:
                            Teleport(slot, new Vector2(a, b));
                            break;
                        case ArenaLink.EvKill:
                            if (!p.data.dead) p.data.healthHandler.DoDamage(Vector2.up * (p.data.health + 1000f), p.transform.position, Color.white, null, null, false, true, true);
                            break;
                        case ArenaLink.EvPit:
                            // ROUNDS's own out-of-bounds bounce: a strong push back up and a hit.
                            p.data.healthHandler.CallTakeForce(Vector2.up * 200f * MassRef(p.data.playerVel), ForceMode2D.Impulse, false, true);
                            p.data.healthHandler.CallTakeDamage(Vector2.up * a, p.transform.position);
                            break;
                    }
                }
                catch (Exception ex) { EnginePlugin.L.LogWarning("arena event " + type + ": " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ cards

        public void SetCards(int slot, string[] owned)
        {
            if (slot < 0 || slot > 1) return;
            lastCards[slot] = wantedCards[slot] = owned ?? new string[0];
            EnginePlugin.L.LogInfo("arena: deck for player " + (slot + 1) + ": " + (wantedCards[slot].Length == 0 ? "empty" : string.Join(", ", wantedCards[slot])) + (players[slot] == null ? " (no player yet)" : ""));
        }

        private void ApplyWantedCards()
        {
            for (int slot = 0; slot < 2; slot++)
            {
                var p = players[slot];
                var want = wantedCards[slot];
                if (p == null || want == null || creating[slot]) continue;
                wantedCards[slot] = null;
                if (p.data.currentCards == null) p.data.currentCards = new List<CardInfo>();
                var have = p.data.currentCards.Where(c => c != null).GroupBy(c => c.gameObject.name).ToDictionary(g => g.Key, g => g.Count());
                var wantCount = want.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                // A card the deck does not have (the deck was wiped at a win, or another save was loaded): start over from a fresh player, ROUNDS's way.
                if (have.Any(h => { int w; wantCount.TryGetValue(h.Key, out w); return h.Value > w; }))
                {
                    ResetCards(p);
                    have.Clear();
                }
                foreach (var g in want.GroupBy(x => x))
                {
                    int has;
                    have.TryGetValue(g.Key, out has);
                    var card = Cards.Get(g.Key);
                    if (card == null) { EnginePlugin.L.LogWarning("arena: unknown card " + g.Key); continue; }
                    for (int k = has; k < g.Count(); k++) ApplyCard(p, card);
                }
            }
        }

        /// <summary>ROUNDS's own reset between games (PlayerManager.ResetCharacters for one player): new gun, base stats and block,
        /// no cards, empty card bar.</summary>
        private static void ResetCards(Player p)
        {
            Traverse.Create(p).Method("FullReset").GetValue();
            // FullReset leaves what ApplyCardStats also changes: jumps, gravity, regeneration, ammo regen, bullet, gun sounds.
            try
            {
                var pre = PlayerAssigner.instance.playerPrefab;
                p.data.jumps = pre.GetComponent<CharacterData>().jumps;
                p.data.currentJumps = p.data.jumps;
                p.GetComponent<Gravity>().gravityForce = pre.GetComponent<Gravity>().gravityForce;
                p.data.healthHandler.regeneration = pre.GetComponent<HealthHandler>().regeneration;
                var gun = p.data.weaponHandler.gun;
                var preHold = pre.GetComponent<Holding>();
                var preGun = preHold != null && preHold.holdable != null ? preHold.holdable.GetComponent<Gun>() : null;
                var ammo = gun.GetComponentInChildren<GunAmmo>();
                var preAmmo = preGun != null ? preGun.GetComponentInChildren<GunAmmo>() : null;
                if (ammo != null) ammo.ammoReg = preAmmo != null ? preAmmo.ammoReg : 0f;
                if (preGun != null && preGun.projectiles.Length > 0 && gun.projectiles.Length > 0) gun.projectiles[0].objectToSpawn = preGun.projectiles[0].objectToSpawn;
                var audio = p.GetComponent<PlayerAudioModifyers>();
                if (audio != null) audio.modifyers.Clear();
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena: stat reset: " + e.Message); }
            p.data.currentCards.Clear();
            try
            {
                var bars = Traverse.Create(CardBarHandler.instance).Field("cardBars").GetValue<CardBar[]>();
                if (bars != null && p.playerID >= 0 && p.playerID < bars.Length) bars[p.playerID].ClearBar();
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena: card bar reset: " + e.Message); }
            EnginePlugin.L.LogInfo("arena: player " + p.playerID + " reset to the plain gun (the deck was wiped, or is another save's)");
        }

        /// <summary>Gives a player a card the way ROUNDS does after a pick (ApplyCardStats), so card mods run their own code too.</summary>
        public static void ApplyCard(Player p, CardInfo prefab)
        {
            GameObject go = null;
            try
            {
                go = Instantiate(prefab.gameObject, new Vector3(0f, 5000f, 0f), Quaternion.identity);
                go.GetComponent<CardInfo>().sourceCard = prefab;
                go.GetComponentInChildren<ApplyCardStats>().Pick(p.playerID, true, PickerType.Player);
                EnginePlugin.L.LogInfo("arena: player " + p.playerID + " got " + prefab.cardName);
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena: could not apply " + prefab.cardName + ": " + e); }
            if (go != null) Destroy(go);
        }

        // ------------------------------------------------------------------ ROUNDS card pick

        private bool pickCancelled;

        private void CheckPick()
        {
            int serial = link.I(ArenaLink.C_Pick + ArenaLink.P_Serial);
            if (serial == pickSeen) return;
            int slot = link.I(ArenaLink.C_Pick + ArenaLink.P_Slot);
            int count = link.I(ArenaLink.C_Pick + ArenaLink.P_Count);
            if (count <= 0)
            {
                // Cuphead gave the pick up: a running one ends, one not started yet never starts.
                pickSeen = serial;
                if (picking) pickCancelled = true;
                else link.I(ArenaLink.ResultOff + ArenaLink.R_PickDone, serial);
                return;
            }
            if (picking) return;
            count = Mathf.Clamp(count, 1, ArenaLink.MaxPicked);
            if (slot < 0 || slot > 1 || players[slot] == null || creating[slot]) return;
            pickSeen = serial;
            pickCancelled = false;
            StartCoroutine(RunPick(serial, slot, count));
        }

        private IEnumerator RunPick(int serial, int slot, int count)
        {
            picking = true;
            pickSlot = slot;
            try
            {
                foreach (object step in RunPickSteps(slot, count)) yield return step;
            }
            finally
            {
                // Also when ROUNDS's pick throws: Cuphead gets the cards picked so far and the arena is not stuck picking.
                picking = false;
                pickSlot = -1;
                link.I(ArenaLink.ResultOff + ArenaLink.R_PickDone, serial);
            }
        }

        private IEnumerable RunPickSteps(int slot, int count)
        {
            link.I(ArenaLink.ResultOff + ArenaLink.R_PickedCount, 0);
            var p = players[slot];
            seenNav[slot] = link.I(ArenaLink.Slot(slot) + ArenaLink.S_NavPresses);
            seenAccept[slot] = link.I(ArenaLink.Slot(slot) + ArenaLink.S_AcceptPresses);
            EnginePlugin.L.LogInfo("arena: ROUNDS card pick for player " + (slot + 1) + " x" + count);
            for (int i = 0; i < count && !pickCancelled; i++)
            {
                CardChoiceVisuals.instance.Show(p.playerID, true);
                // ROUNDS's pick, driven here so its own failures are caught; it ends early only when nothing can be picked
                // or Cuphead gives the pick up.
                var cc = CardChoice.instance;
                var t = Traverse.Create(cc);
                var it = cc.DoPick(1, p.playerID, PickerType.Player);
                float stuckSince = -1f;
                while (true)
                {
                    if (pickCancelled) { EnginePlugin.L.LogInfo("arena: Cuphead gave the pick up; ending it"); AbortRoundsPick(cc); break; }
                    bool more;
                    try { more = it.MoveNext(); }
                    catch (Exception e) { EnginePlugin.L.LogError("arena: ROUNDS pick threw: " + e); AbortRoundsPick(cc); break; }
                    if (!more) break;
                    var spawned = t.Field("spawnedCards").GetValue<List<GameObject>>();
                    bool selectable = t.Field("pickrID").GetValue<int>() != -1 && !t.Field("isPlaying").GetValue<bool>() && spawned != null && spawned.Any(c => c != null);
                    if (cc.IsPicking && !selectable && t.Field("pickrID").GetValue<int>() != -1)
                    {
                        if (stuckSince < 0f) stuckSince = Time.unscaledTime;
                        else if (Time.unscaledTime - stuckSince > 8f) { EnginePlugin.L.LogError("arena: ROUNDS pick stalled; ending it"); AbortRoundsPick(cc); break; }
                    }
                    else stuckSince = -1f;
                    yield return it.Current;
                }
                yield return new WaitForSecondsRealtime(0.1f);
            }
            CardChoiceVisuals.instance.Hide();
            yield return new WaitForSecondsRealtime(0.35f);
        }

        private static void AbortRoundsPick(CardChoice cc)
        {
            try
            {
                cc.IsPicking = false;
                Traverse.Create(cc).Field("pickrID").SetValue(-1);
                UIHandler.instance.StopShowPicker();
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("arena: pick abort: " + e.Message); }
        }

        public void RecordPicked(int playerId, CardInfo card)
        {
            int slot = SlotOfPlayerId(playerId);
            if (slot < 0 || card == null) return;
            var src = card.sourceCard != null ? card.sourceCard : card;
            int n = link.I(ArenaLink.ResultOff + ArenaLink.R_PickedCount);
            if (n >= ArenaLink.MaxPicked) return;
            link.Str(ArenaLink.ResultOff + ArenaLink.R_Picked + n * ArenaLink.PickedSize, ArenaLink.PickedSize, src.gameObject.name);
            link.I(ArenaLink.ResultOff + ArenaLink.R_PickedCount, n + 1);
            EnginePlugin.L.LogInfo("arena: player " + (slot + 1) + " picked " + src.cardName);
        }

        /// <summary>Card pick input from Cuphead: left/right presses, the mouse over a card, accept.</summary>
        public int TakeNav(int slot)
        {
            int s = ArenaLink.Slot(slot);
            int n = link.I(s + ArenaLink.S_NavPresses);
            if (n == seenNav[slot]) return 0;
            seenNav[slot] = n;
            return link.I(s + ArenaLink.S_NavDir);
        }

        public bool TakeAccept(int slot)
        {
            int n = link.I(ArenaLink.Slot(slot) + ArenaLink.S_AcceptPresses);
            if (n == seenAccept[slot]) return false;
            seenAccept[slot] = n;
            return true;
        }

        /// <summary>The card under the mouse (pick camera space), or -1 when the mouse has not moved.</summary>
        public int MouseCard(int slot, List<GameObject> cards, Camera cam)
        {
            int s = ArenaLink.Slot(slot);
            var m = new Vector2(link.F(s + ArenaLink.S_MouseX), link.F(s + ArenaLink.S_MouseY));
            if (m.x < 0f || cam == null || (m - lastMouse[slot]).sqrMagnitude < 1e-6f) return -1;
            lastMouse[slot] = m;
            Vector3 w = cam.ViewportToWorldPoint(new Vector3(m.x, m.y, 10f));
            int best = -1;
            float bestD = 6f * 6f;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                float d = ((Vector2)cards[i].transform.position - (Vector2)w).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ result for Cuphead

        private float nextLog;

        private void LogState()
        {
            if (mode == 1 && Time.unscaledTime > nextLog)
            {
                nextLog = Time.unscaledTime + 3f;
                var sb = new System.Text.StringBuilder("arena: mirrors " + mirrors.Count + " (" + string.Join(",", mirrors.Values.GroupBy(m => m.kind).Select(g => g.Key + ":" + g.Count()).ToArray()) + ")");
                for (int i = 0; i < 2; i++)
                    if (players[i] != null) sb.Append(" | p" + (i + 1) + " at " + players[i].transform.position.ToString("0.0") + " active " + players[i].gameObject.activeSelf + " dead " + players[i].data.dead + " hp " + players[i].data.health.ToString("0") + " grounded " + players[i].data.isGrounded);
                EnginePlugin.L.LogInfo(sb.ToString() + (paused ? " (paused)" : ""));
            }
        }

        private void WriteResult()
        {
            int r = ArenaLink.ResultOff;
            link.I(r + ArenaLink.R_State, phase != Phase.Ready ? ArenaLink.StateOff : picking ? ArenaLink.StatePicking : ArenaLink.StateArena);
            link.I(r + ArenaLink.R_Level, levelSeen);
            for (int i = 0; i < 2; i++)
            {
                int q = ArenaLink.RSlot(i);
                try { WriteSlot(i, q); }
                catch (Exception e)
                {
                    link.I(q + ArenaLink.Q_Present, 0);
                    if (stepErrors.Add("result" + i + ": " + e.Message)) EnginePlugin.L.LogError("arena result p" + (i + 1) + " failed: " + e);
                }
            }
        }

        private void WriteSlot(int i, int q)
        {
                var p = players[i];
                bool present = p != null && !creating[i];
                link.I(q + ArenaLink.Q_Present, present ? 1 : 0);
                if (!present) return;
                var d = p.data;
                Vector2 c = ToCuphead(p.transform.position);
                Vector2 v = VelRef(d.playerVel) * scale;
                link.F(q + ArenaLink.Q_X, c.x);
                link.F(q + ArenaLink.Q_Y, c.y);
                link.F(q + ArenaLink.Q_VX, v.x);
                link.F(q + ArenaLink.Q_VY, v.y);
                link.F(q + ArenaLink.Q_Hp, d.health);
                link.F(q + ArenaLink.Q_MaxHp, d.maxHealth);
                link.I(q + ArenaLink.Q_Dead, d.dead ? 1 : 0); // a respawn card's death (inactive, not dead) comes back by itself
                link.I(q + ArenaLink.Q_Blocking, d.block.sinceBlock < 0.3f ? 1 : 0);
                link.I(q + ArenaLink.Q_Grounded, d.isGrounded ? 1 : 0);
                link.I(q + ArenaLink.Q_Blocks, blockCount[i]);
                link.F(q + ArenaLink.Q_AimX, d.input.lastAimDirection.x);
                link.F(q + ArenaLink.Q_AimY, d.input.lastAimDirection.y);
                link.F(q + ArenaLink.Q_Radius, Radius(p) * scale);
                link.I(q + ArenaLink.Q_Cards, d.currentCards != null ? d.currentCards.Count : 0);
                link.I(q + ArenaLink.Q_Life, life[i]); // last: everything above describes this life
        }

        private void OnDestroy()
        {
            try { link?.I(ArenaLink.ResultOff + ArenaLink.R_State, ArenaLink.StateOff); } catch { }
        }
    }

    /// <summary>
    /// Renders the ROUNDS layer for Cuphead after everything moved: in levels a camera over Cuphead's view (players,
    /// bullets, effects) and, as a second picture, ROUNDS's game UI (card bar, pick texts); while picking, ROUNDS's card
    /// pick view. Both go into the shared frame buffers, un-premultiplied so Cuphead can draw them with its standard sprite
    /// shader: the world picture at the Cuphead player's depth (under Cuphead's foreground), the UI over everything.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public class ArenaRenderer : MonoBehaviour
    {
        private Camera cam, uiCam;
        private RenderTexture rt, rtUi, rtUiW;
        private int w, h;
        private Canvas uiCanvas;
        private bool uiMissingLogged;
        private static string TPath(Transform t) => t.parent == null ? t.name : TPath(t.parent) + "/" + t.name;

        private struct Pending { public AsyncGPUReadbackRequest world, ui, uiW; public float[] meta; }
        private readonly Queue<Pending> pending = new Queue<Pending>();
        private Texture2D readTex, readTexUi, readTexUiW;
        private int frameSerial;

        private float statAt, statDt, statRender, statPublish;
        private int statFrames, statPublished;
        private readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();

        private void Stats()
        {
            statFrames++;
            statDt += Time.unscaledDeltaTime;
            if (Time.unscaledTime - statAt < 5f) return;
            if (statFrames > 0 && Arena.I != null && Arena.I.Mode != 0)
                EnginePlugin.L.LogInfo(string.Format("arena perf: {0:0.0} fps, render {1:0.0} ms, publish {2:0.0} ms, {3} frames sent, {4}x{5}", statFrames / statDt, statRender / statFrames, statPublish / Mathf.Max(1, statPublished), statPublished, w, h));
            statAt = Time.unscaledTime; statFrames = 0; statDt = 0f; statRender = 0f; statPublish = 0f; statPublished = 0;
        }

        private void LateUpdate()
        {
            Stats();
            var a = Arena.I;
            if (a == null || !a.WorldReady || (a.Mode != 1 && a.Mode != 2)) { DrainOnly(); return; }
            var link = a.link;
            int fw = Mathf.Clamp(link.I(ArenaLink.C_FrameW), 64, ArenaLink.MaxW), fh = Mathf.Clamp(link.I(ArenaLink.C_FrameH), 64, ArenaLink.MaxH);
            if (rt == null || rtUi == null || rtUiW == null || fw != w || fh != h) Setup(fw, fh);
            float camX, camY, ortho;
            if (a.Mode == 1 && !a.Picking)
            {
                Vector2 c = a.ToRounds(link.F(ArenaLink.C_CamX), link.F(ArenaLink.C_CamY));
                camX = c.x; camY = c.y;
                ortho = Mathf.Max(1f, link.F(ArenaLink.C_CamOrtho) / Mathf.Max(1f, link.F(ArenaLink.C_Scale)));
            }
            else
            {
                // ROUNDS's own card pick view (its main camera frame).
                camX = 0f;
                camY = 0f;
                ortho = 20f;
            }
            cam.transform.position = new Vector3(camX, camY, -100f);
            cam.orthographicSize = ortho;
            // In levels the layer is see-through over Cuphead; the pick screen is ROUNDS's own dark screen.
            cam.backgroundColor = a.Mode == 1 && !a.Picking ? new Color(0f, 0f, 0f, 0f) : PickBackground;
            PickCam = cam;
            sw.Reset(); sw.Start();
            cam.Render();
            HookUi();
            // The UI picture twice, over clear black and over white (its coverage is the difference: exact for Unity's UI
            // shader, text and additive glows alike); cleared every frame even while the canvas is missing.
            uiCam.targetTexture = rtUi;
            uiCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            uiCam.Render();
            uiCam.targetTexture = rtUiW;
            uiCam.backgroundColor = Color.white;
            uiCam.Render();
            statRender += (float)sw.Elapsed.TotalMilliseconds;
            var meta = new[] { a.ToCuphead(new Vector2(camX, camY)).x, a.ToCuphead(new Vector2(camX, camY)).y, ortho * link.F(ArenaLink.C_Scale), a.Mode == 1 && !a.Picking ? 1f : 2f };
            if (SystemInfo.supportsAsyncGPUReadback && !SyncRead)
            {
                if (pending.Count < 6) pending.Enqueue(new Pending { world = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32), ui = AsyncGPUReadback.Request(rtUi, 0, TextureFormat.RGBA32), uiW = AsyncGPUReadback.Request(rtUiW, 0, TextureFormat.RGBA32), meta = meta });
                GL.Flush();
                DrainOnly();
            }
            else
            {
                sw.Reset(); sw.Start();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                RenderTexture.active = rtUi;
                readTexUi.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                RenderTexture.active = rtUiW;
                readTexUiW.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                RenderTexture.active = prev;
                statRender += (float)sw.Elapsed.TotalMilliseconds;
                Publish(readTex.GetRawTextureData<byte>(), readTexUi.GetRawTextureData<byte>(), readTexUiW.GetRawTextureData<byte>(), meta);
            }
        }

        public static Camera PickCam;
        public static readonly Color PickBackground = new Color(0.118f, 0.118f, 0.13f, 1f);
        public static bool SyncRead = Environment.GetCommandLineArgs().Contains("--arena-sync");

        private void DrainOnly()
        {
            while (pending.Count > 0)
            {
                var head = pending.Peek();
                if (!head.world.done || !head.ui.done || !head.uiW.done) break;
                pending.Dequeue();
                if (head.world.hasError || head.ui.hasError || head.uiW.hasError) continue;
                var data = head.world.GetData<byte>();
                var ui = head.ui.GetData<byte>();
                var uiW = head.uiW.GetData<byte>();
                if (data.Length == w * h * 4 && ui.Length == w * h * 4 && uiW.Length == w * h * 4) Publish(data, ui, uiW, head.meta);
            }
        }

        private void Setup(int fw, int fh)
        {
            w = fw; h = fh;
            // Readbacks still in flight read the old texture: finish them before it goes, and never release it while active.
            foreach (var q in pending) { if (!q.world.done) q.world.WaitForCompletion(); if (!q.ui.done) q.ui.WaitForCompletion(); if (!q.uiW.done) q.uiW.WaitForCompletion(); }
            pending.Clear();
            if (RenderTexture.active == rt || RenderTexture.active == rtUi || RenderTexture.active == rtUiW) RenderTexture.active = null;
            if (cam != null) cam.targetTexture = null;
            if (uiCam != null) uiCam.targetTexture = null;
            if (rt != null) { rt.Release(); Destroy(rt); }
            if (rtUi != null) { rtUi.Release(); Destroy(rtUi); }
            if (rtUiW != null) { rtUiW.Release(); Destroy(rtUiW); }
            if (readTex != null) Destroy(readTex);
            if (readTexUi != null) Destroy(readTexUi);
            if (readTexUiW != null) Destroy(readTexUiW);
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            rtUi = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            rtUi.Create();
            rtUiW = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            rtUiW.Create();
            readTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            readTexUi = new Texture2D(w, h, TextureFormat.RGBA32, false);
            readTexUiW = new Texture2D(w, h, TextureFormat.RGBA32, false);
            pending.Clear();
            if (cam == null)
            {
                var go = new GameObject("Roundhead Arena Camera");
                DontDestroyOnLoad(go);
                cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 1000f;
                var main = MainCam.instance != null ? MainCam.instance.cam : Camera.main;
                cam.cullingMask = main != null ? main.cullingMask : ~0;
                var ugo = new GameObject("Roundhead UI Camera");
                DontDestroyOnLoad(ugo);
                ugo.transform.position = new Vector3(5000f, 5000f, -100f);
                uiCam = ugo.AddComponent<Camera>();
                uiCam.enabled = false;
                uiCam.orthographic = true;
                uiCam.clearFlags = CameraClearFlags.SolidColor;   // its own picture, over black then over white
                uiCam.cullingMask = 1 << 5;
                uiCam.nearClipPlane = 0.1f;
                uiCam.farClipPlane = 1000f;
            }
            cam.targetTexture = rt;
            uiCam.targetTexture = rtUi;
        }

        /// <summary>ROUNDS's game UI canvas (card bar and pick texts) renders through our UI camera.</summary>
        private void HookUi()
        {
            if (uiCanvas != null) return;
            var go = GameObject.Find("Game/UI/UI_Game/Canvas");
            if (go == null) { if (!uiMissingLogged) { uiMissingLogged = true; EnginePlugin.L.LogWarning("arena: ROUNDS game UI canvas not found"); } return; }
            uiCanvas = go.GetComponent<Canvas>();
            if (uiCanvas == null) return;
            var kids = new List<string>();
            foreach (Transform k in go.transform) kids.Add(k.name + (k.gameObject.activeSelf ? "" : "[off]"));
            EnginePlugin.L.LogInfo("arena: ROUNDS game UI: " + string.Join(", ", kids.ToArray()) + " | CardBarHandler " + (CardBarHandler.instance != null ? TPath(CardBarHandler.instance.transform) + (CardBarHandler.instance.gameObject.activeInHierarchy ? "" : " [inactive]") : "none"));
            uiCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            uiCanvas.worldCamera = uiCam;
            uiCanvas.planeDistance = 10f;
        }

        private unsafe void Publish(Unity.Collections.NativeArray<byte> src, Unity.Collections.NativeArray<byte> ui, Unity.Collections.NativeArray<byte> uiW, float[] meta)
        {
            byte* p = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(src);
            byte* u = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(ui);
            byte* uw = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(uiW);
            Publish(p, u, uw, src.Length, meta);
        }

        /// <summary>Both pictures into the same buffer index, the world one then the UI one, then the metadata and the
        /// ready index and serial (the order Cuphead's reader relies on).</summary>
        private unsafe void Publish(byte* src, byte* ui, byte* uiW, int len, float[] meta)
        {
            var link = Arena.I.link;
            sw.Reset(); sw.Start();
            int r = ArenaLink.ResultOff;
            int ready = link.I(r + ArenaLink.R_Ready), reading = link.I(ArenaLink.C_Reading);
            int target = 0;
            while (target == ready || target == reading) target++;
            Unpremultiply((uint*)src, (uint*)((byte*)link.Frame(target).ToPointer()), len / 4);
            UiFrame((uint*)ui, (uint*)uiW, (uint*)((byte*)link.UiFrame(target).ToPointer()), len / 4);
            link.F(r + ArenaLink.R_FrameCamX, meta[0]);
            link.F(r + ArenaLink.R_FrameCamY, meta[1]);
            link.F(r + ArenaLink.R_FrameOrtho, meta[2]);
            link.I(r + ArenaLink.R_FrameW, w);
            link.I(r + ArenaLink.R_FrameH, h);
            link.I(r + ArenaLink.R_Ready, target);
            link.I(r + ArenaLink.R_FrameSerial, ++frameSerial);
            statPublish += (float)sw.Elapsed.TotalMilliseconds;
            statPublished++;
        }

        /// <summary>
        /// The UI picture's coverage, exactly, from its two renders: over black a pixel holds a·c, over white a·c + (1 - a),
        /// so the difference is 1 - a whatever the shader blended with (Unity's UI shader writes a·a as alpha, text blends
        /// premultiplied, glows add). ROUNDS draws its card bar for its own dark screen (a 21 % fill, a 70 % border): over
        /// Cuphead's bright levels the coverage is raised with 1 - (1 - a)^uiOpacityBoost so the bar reads as solid as the
        /// players (sheets/tuning.json). Colours stay as ROUNDS draws them.
        /// </summary>
        private static readonly byte[] UiAlpha = BuildTable(a => 1.0 - Math.Pow(1.0 - a / 255.0, Tuning.uiOpacityBoost));
        private static byte[] BuildTable(Func<int, double> f)
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, f(i))) * 255.0);
            return t;
        }

        private static unsafe void UiFrame(uint* black, uint* white, uint* d, int n)
        {
            for (int i = 0; i < n; i++)
            {
                uint b = black[i], wpx = white[i];
                uint br = b & 0xFF, bg = (b >> 8) & 0xFF, bb = (b >> 16) & 0xFF;
                uint wr = wpx & 0xFF, wg = (wpx >> 8) & 0xFF, wb = (wpx >> 16) & 0xFF;
                // 1 - a per channel (the white gained what the UI did not cover): a blend leaves the same gap in all three,
                // an additive glow (the card bar's fill) saturates the white and leaves gaps of 255 - B, so the largest gap
                // is the one closest to 1 - a in either case.
                uint tr = wr > br ? wr - br : 0u, tg = wg > bg ? wg - bg : 0u, tb = wb > bb ? wb - bb : 0u;
                uint gap = tr > tg ? (tr > tb ? tr : tb) : (tg > tb ? tg : tb);
                uint al = gap >= 247u ? 0u : 255u - gap;
                if (al == 0u)
                {
                    // Nothing covered (an additive glow, or next to nothing): keep what was drawn over black as a soft
                    // coloured pixel, as strong as its brightest channel.
                    uint m = br > bg ? (br > bb ? br : bb) : (bg > bb ? bg : bb);
                    if (m < 8u) { d[i] = 0u; continue; }
                    al = m;
                }
                uint cr = Math.Min(255u, br * 255u / al), cg = Math.Min(255u, bg * 255u / al), cb = Math.Min(255u, bb * 255u / al);
                d[i] = ((uint)UiAlpha[al] << 24) | (cb << 16) | (cg << 8) | cr;
            }
        }

        /// <summary>Unity's render target holds premultiplied colour; Cuphead's sprite shader wants straight colour.</summary>
        private static unsafe void Unpremultiply(uint* s, uint* d, int n)
        {
            for (int i = 0; i < n; i++)
            {
                uint px = s[i];
                if (px == 0u) { d[i] = 0u; continue; }
                uint al = px >> 24;
                if (al == 255u) { d[i] = px; continue; }
                uint cr = px & 0xFF, cg = (px >> 8) & 0xFF, cb = (px >> 16) & 0xFF;
                if (al == 0u)
                {
                    // Additive glow (no alpha written): keep it as a soft coloured pixel.
                    uint m = cr > cg ? (cr > cb ? cr : cb) : (cg > cb ? cg : cb);
                    if (m < 8u) { d[i] = 0u; continue; }
                    al = m;
                }
                cr = Math.Min(255u, cr * 255u / al);
                cg = Math.Min(255u, cg * 255u / al);
                cb = Math.Min(255u, cb * 255u / al);
                d[i] = (al << 24) | (cb << 16) | (cg << 8) | cr;
            }
        }
    }

    /// <summary>Harmony patches that hand ROUNDS's players to Cuphead's input and level (sheets/hooks.json, engine rows).</summary>
    public static class ArenaPatches
    {
        [HarmonyPrefix, HarmonyPatch(typeof(GeneralInput), "Update")]
        public static bool InputUpdate(GeneralInput __instance)
        {
            var a = Arena.I;
            if (a == null || __instance.controlledElseWhere) return true;
            int slot = a.SlotOf(__instance.GetComponent<CharacterData>());
            if (slot < 0) return true;
            a.FeedInput(__instance, slot);
            return false;
        }

        /// <summary>The hidden engine still hears the player's gamepad (InControl reads XInput without focus): ROUNDS's escape menu
        /// would stop its time, and its QUIT GAME / MAIN MENU would close or reload the engine in the middle of a level.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(EscapeMenuHandler), "Update")]
        public static bool EscapeMenu()
        {
            if (!EnginePlugin.Hidden) return true;
            EscapeMenuHandler.isEscMenu = false;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(QuitButton), "Quit")]
        public static bool QuitPressed()
        {
            if (!EnginePlugin.Hidden) return true;
            EnginePlugin.L.LogWarning("arena: ROUNDS's QUIT GAME was pressed; ignored");
            return false;
        }

        /// <summary>Only the arena makes players: a gamepad or keyboard press must not join an extra ROUNDS player.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerAssigner), "LateUpdate")]
        public static bool Joining() => !EnginePlugin.Hidden;

        /// <summary>ROUNDS's world stands still while Cuphead is paused or away (ROUNDS's own pause: its time scale at 0).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(TimeHandler), "Update")]
        public static void PauseWithCuphead()
        {
            if (Arena.I == null || !Arena.I.WorldReady || !Arena.I.Paused || Arena.I.Picking) return;
            // Unity's own time stops (physics, timed waits such as damage over time); ROUNDS's scale stays 1, so nothing it
            // divides by it breaks.
            Time.timeScale = 0f;
            TimeHandler.deltaTime = 0f;
            TimeHandler.fixedDeltaTime = 0f;
        }

        /// <summary>Co-op: ROUNDS's players are opponents in ROUNDS, partners in Cuphead. A partner's bullet passes through you
        /// (your own bounced bullets still hit you, as in ROUNDS).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ProjectileHit), "Hit")]
        public static bool PartnerBullet(ProjectileHit __instance, HitInfo hit)
        {
            var a = Arena.I;
            if (a == null || hit == null || hit.transform == null) return true;
            var target = hit.transform.GetComponentInParent<Player>();
            if (target == null || a.SlotOf(target) < 0) return true;
            var owner = __instance.ownPlayer;
            if (owner == null) { var sa = __instance.GetComponent<SpawnedAttack>(); if (sa != null) owner = sa.spawner; }
            return owner == null || owner == target || a.SlotOf(owner) < 0;
        }

        /// <summary>Co-op: no damage from a partner of any kind (explosions, card effects).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(HealthHandler), "DoDamage")]
        public static bool PartnerDamage(HealthHandler __instance, Player damagingPlayer)
        {
            var a = Arena.I;
            if (a == null || damagingPlayer == null) return true;
            var me = __instance.GetComponent<Player>();
            return me == null || damagingPlayer == me || a.SlotOf(me) < 0 || a.SlotOf(damagingPlayer) < 0;
        }

        /// <summary>Cuphead's camera decides the screen edges; ROUNDS's fixed arena box does not apply.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(OutOfBoundsHandler), "LateUpdate")]
        public static bool OutOfBounds() => Arena.I == null || !Arena.I.WorldReady;

        /// <summary>Cuphead decides when a fallen player comes back (co-op revive, retry).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(GM_Test), "PlayerDied")]
        public static bool SandboxDied() => Arena.I == null || !Arena.I.WorldReady;

        [HarmonyPrefix, HarmonyPatch(typeof(CardChoice), "Pick")]
        public static void Picked(CardChoice __instance, GameObject pickedCard)
        {
            if (Arena.I == null || pickedCard == null) return;
            int pickr = Traverse.Create(__instance).Field("pickrID").GetValue<int>();
            Arena.I.RecordPicked(pickr, pickedCard.GetComponent<CardInfo>());
        }

        /// <summary>ROUNDS's card pick, driven by the Cuphead player's keys, stick or mouse.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(CardChoice), "DoPlayerSelect")]
        public static bool PlayerSelect(CardChoice __instance)
        {
            var a = Arena.I;
            if (a == null || !a.Picking) return true;
            var t = Traverse.Create(__instance);
            int pickr = t.Field("pickrID").GetValue<int>();
            int slot = a.SlotOfPlayerId(pickr);
            if (slot < 0) return true;
            var spawned = t.Field("spawnedCards").GetValue<List<GameObject>>();
            if (spawned == null || spawned.Count == 0) return false;
            int cur = t.Field("currentlySelectedCard").GetValue<int>();
            cur += a.TakeNav(slot);
            int mouse = a.MouseCard(slot, spawned, ArenaRenderer.PickCam);
            if (mouse >= 0) cur = mouse;
            cur = Mathf.Clamp(cur, 0, spawned.Count - 1);
            float counter = t.Field("counter").GetValue<float>();
            for (int j = 0; j < spawned.Count; j++)
            {
                if (spawned[j] == null) continue;
                var vis = spawned[j].GetComponentInChildren<CardVisuals>();
                if (vis != null && (vis.isSelected != (cur == j) || counter > 0.2f))
                {
                    counter = 0f;
                    spawned[j].GetComponent<PhotonView>().RPC("RPCA_ChangeSelected", RpcTarget.All, cur == j);
                }
            }
            t.Field("counter").SetValue(counter);
            t.Field("currentlySelectedCard").SetValue(cur);
            bool busy = t.Field("isPlaying").GetValue<bool>();
            if (a.TakeAccept(slot) && !busy && spawned[cur] != null)
            {
                __instance.Pick(spawned[cur]);
                t.Field("pickrID").SetValue(-1);
            }
            if (CardChoiceVisuals.instance.currentCardSelected != cur) Traverse.Create(CardChoiceVisuals.instance).Method("SetCurrentSelected", cur).GetValue();
            return false;
        }
    }
}
