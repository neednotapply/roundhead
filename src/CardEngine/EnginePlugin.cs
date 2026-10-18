using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Runs inside ROUNDS while Roundhead plays: waits until ROUNDS (and any card mods) have loaded every card,
    /// then serves card lists, card renders, card math and the player's ROUNDS look to Cuphead over localhost.
    /// </summary>
    [BepInPlugin("gg.melty.roundhead.cardengine", "Roundhead card engine", "0.1.1")]
    public class EnginePlugin : BaseUnityPlugin
    {
        public static EnginePlugin I;
        public static ManualLogSource L;
        private EngineServer server;
        public static bool Hidden;
        private ArenaLink startingLink;
        private float nextCupheadCheck;

        /// <summary>Ends the hidden engine when the Cuphead process that uses it is gone (its pid is in the control block).</summary>
        public static void CheckCuphead(ArenaLink link)
        {
            if (!Hidden || link == null) return;
            int pid = link.I(ArenaLink.C_Pid);
            if (pid <= 0) return;
            bool alive = true;
            try { using (var cup = Process.GetProcessById(pid)) alive = !cup.HasExited; }
            catch (ArgumentException) { alive = false; }
            catch { }
            if (!alive) Exit("Cuphead closed");
        }

        private void Awake()
        {
            I = this;
            L = Logger;
            DontDestroyOnLoad(gameObject);
            Hidden = Environment.GetCommandLineArgs().Any(a => a == "--roundhead-engine");
            Application.runInBackground = true;
            if (Hidden)
            {
                AudioListener.volume = 0f;
                Application.targetFrameRate = 60;
                // Nothing inside the hidden engine may close it while Cuphead plays: the launcher and Exit() end it directly.
                Application.wantsToQuit += () => { L.LogWarning("arena: ROUNDS asked to quit; ignored while it is Cuphead's card engine"); return false; };
                // Cuphead learns at once that ROUNDS is on its way (cards take a while): the result block beats with the sandbox off.
                startingLink = ArenaLink.Open();
                if (startingLink != null)
                {
                    startingLink.I(ArenaLink.ResultOff + ArenaLink.R_State, ArenaLink.StateOff);
                    startingLink.I(ArenaLink.ResultOff + ArenaLink.R_Pid, Process.GetCurrentProcess().Id);
                    startingLink.I(ArenaLink.ResultOff + ArenaLink.R_Magic, ArenaLink.Magic);
                }
            }
            StartCoroutine(WaitForCards());
        }

        private IEnumerator WaitForCards()
        {
            float started = Time.realtimeSinceStartup;
            while (CardChoice.instance == null || CardChoice.instance.cards == null || CardChoice.instance.cards.Length == 0)
                yield return new WaitForSecondsRealtime(0.5f);
            // Card mods register their cards a little after the menu appears; wait until the count settles.
            int last = -1;
            float stableSince = Time.realtimeSinceStartup;
            while (true)
            {
                int n = Cards.All().Count;
                if (n != last) { last = n; stableSince = Time.realtimeSinceStartup; }
                if (Time.realtimeSinceStartup - stableSince > 4f) break;
                if (Time.realtimeSinceStartup - started > 120f) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            L.LogInfo("ROUNDS cards ready: " + last + " cards");
            if (Environment.GetCommandLineArgs().Any(a => a == "--roundhead-dump")) Diagnostics.DumpPlayer();
            if (Environment.GetCommandLineArgs().Any(a => a == "--roundhead-dumpcards")) CardAudit.Dump();
            server = new EngineServer();
            server.Start();
            Arena.Create(gameObject);
        }

        private void Update()
        {
            if (Hidden) WindowHider.Tick();
            if (Arena.I == null && startingLink != null)
            {
                startingLink.Inc(ArenaLink.ResultOff + ArenaLink.R_Beat);
                // Still loading cards: if the Cuphead that asked for this engine is gone, there is nothing to load them for.
                if (Time.unscaledTime > nextCupheadCheck) { nextCupheadCheck = Time.unscaledTime + 2f; CheckCuphead(startingLink); }
            }
            server?.Pump();
        }

        private void OnApplicationQuit()
        {
            server?.Stop();
            // ROUNDS's sound system throws during Unity's own teardown and ROUNDS then reports a crash; the hidden
            // engine has nothing to save, so it ends here instead.
            if (Hidden) Exit("quit");
        }

        public static void Exit(string why)
        {
            // The disk log is flushed on a timer, so a line written right before Kill() is lost: flush it and leave a note.
            try { L.LogInfo("engine exiting: " + why); } catch { }
            try { foreach (var l in BepInEx.Logging.Logger.Listeners) (l as DiskLogListener)?.LogWriter?.Flush(); } catch { }
            try { File.AppendAllText(Path.Combine(Proto.DataDir(), "engine-exit.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + why + Environment.NewLine); } catch { }
            try { Arena.I?.link?.I(ArenaLink.ResultOff + ArenaLink.R_State, ArenaLink.StateOff); } catch { }
            try { I?.server?.Stop(); } catch { }
            try { Arena.I?.link?.Dispose(); } catch { }
            Process.GetCurrentProcess().Kill();
        }
    }

    /// <summary>Developer aid: lists the ROUNDS player prefab's parts and components in the BepInEx log.</summary>
    public static class Diagnostics
    {
        public static void DumpPlayer()
        {
            var pa = PlayerAssigner.instance ?? Resources.FindObjectsOfTypeAll<PlayerAssigner>().FirstOrDefault();
            if (pa == null || pa.playerPrefab == null) { EnginePlugin.L.LogInfo("dump: no player prefab"); return; }
            var go = pa.playerPrefab;
            var mv = go.GetComponent<PlayerMovement>();
            var jp = go.GetComponent<PlayerJump>();
            var gr = go.GetComponent<Gravity>();
            var pv = go.GetComponent<PlayerVelocity>();
            var cd = go.GetComponent<CharacterData>();
            EnginePlugin.L.LogInfo("dump-move: force " + (mv ? mv.force : -1) + " airControl " + (mv ? mv.airControl : -1) + " extraDrag " + (mv ? mv.extraDrag : -1)
                + " upForce " + (jp ? jp.upForce : -1) + " sideForce " + (jp ? jp.sideForce : -1) + " gravityForce " + (gr ? gr.gravityForce : -1) + " exponent " + (gr ? gr.exponent : -1)
                + " jumps " + (cd ? cd.jumps : -1) + " scale " + go.transform.localScale.x);
            DumpLayers(go);
            Dump(pa.playerPrefab.transform, 0);
            var holding = pa.playerPrefab.GetComponent<Holding>();
            if (holding != null && holding.holdable != null) { EnginePlugin.L.LogInfo("dump: holdable"); Dump(holding.holdable.transform, 0); }
        }

        private static string MaskNames(int m)
        {
            var n = new List<string>();
            for (int i = 0; i < 32; i++) if ((m & (1 << i)) != 0) n.Add(i + ":" + LayerMask.LayerToName(i));
            return string.Join(",", n.ToArray());
        }

        private static void DumpLayers(GameObject go)
        {
            for (int i = 0; i < 32; i++)
            {
                string ln = LayerMask.LayerToName(i);
                var hits = new List<string>();
                for (int j = 0; j < 32; j++) if (!Physics2D.GetIgnoreLayerCollision(i, j) && LayerMask.LayerToName(j) != "") hits.Add(j.ToString());
                if (ln != "") EnginePlugin.L.LogInfo("dump-layer " + i + " " + ln + " collides2D " + string.Join(",", hits.ToArray()));
            }
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (t.gameObject.layer != go.layer) EnginePlugin.L.LogInfo("dump-objlayer " + t.name + " " + t.gameObject.layer);
            EnginePlugin.L.LogInfo("dump-mask player layer " + go.layer + " PlayerCollision " + MaskNames(go.GetComponent<PlayerCollision>().mask)
                + " Leg " + string.Join(" | ", go.GetComponentsInChildren<LegRaycasters>(true).Select(l => MaskNames(l.mask)).ToArray())
                + " Wall " + string.Join(" | ", go.GetComponentsInChildren<WallRayCaster>(true).Select(l => MaskNames(l.mask)).ToArray()));
            var gun = go.GetComponent<Holding>()?.holdable?.GetComponent<Gun>();
            if (gun != null && gun.projectiles != null && gun.projectiles.Length > 0)
            {
                var bp = gun.projectiles[0].objectToSpawn;
                EnginePlugin.L.LogInfo("dump-bullet " + bp.name + " layer " + bp.layer);
                foreach (var rc in bp.GetComponentsInChildren<RayCastTrail>(true))
                    EnginePlugin.L.LogInfo("dump-bullet-mask mask " + MaskNames(rc.mask) + " player " + MaskNames(rc.playerMask) + " ignoreWalls " + MaskNames(rc.ignoreWallsMask));
                foreach (var c in bp.GetComponentsInChildren<Component>(true)) EnginePlugin.L.LogInfo("dump-bullet-comp " + c.GetType().Name + " on " + c.name);
            }
            foreach (var cam in Camera.allCameras) EnginePlugin.L.LogInfo("dump-cam " + cam.name + " depth " + cam.depth + " mask " + MaskNames(cam.cullingMask) + " ortho " + cam.orthographicSize + " pos " + cam.transform.position + " clear " + cam.clearFlags);
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>()) if (cv.isRootCanvas) EnginePlugin.L.LogInfo("dump-canvas " + cv.name + " mode " + cv.renderMode + " cam " + (cv.worldCamera ? cv.worldCamera.name : "-") + " layer " + cv.gameObject.layer + " path " + TPath(cv.transform));
            EnginePlugin.L.LogInfo("dump-sorting " + string.Join(",", SortingLayer.layers.Select(l => l.name).ToArray()));
        }

        private static string TPath(Transform t) => t.parent == null ? t.name : TPath(t.parent) + "/" + t.name;

        private static void Dump(Transform t, int depth)
        {
            var comps = t.GetComponents<Component>().Where(c => c != null).Select(c =>
            {
                string n = c.GetType().Name;
                var sr = c as SpriteRenderer;
                if (sr != null) n += "(" + (sr.sprite != null ? sr.sprite.name : "-") + " #" + ColorUtility.ToHtmlStringRGBA(sr.color) + " order " + sr.sortingOrder + ")";
                var lr = c as LineRenderer;
                if (lr != null) n += "(w " + lr.widthMultiplier + ")";
                var ps = c as ParticleSystem;
                if (ps != null) n += "(particles)";
                return n;
            });
            EnginePlugin.L.LogInfo("dump: " + new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " [off]") + " pos " + t.localPosition.ToString("0.00") + " scale " + t.localScale.ToString("0.00") + " : " + string.Join(", ", comps.ToArray()));
            if (depth < 8) foreach (Transform c in t) Dump(c, depth + 1);
        }
    }

    /// <summary>Keeps the ROUNDS window off screen and out of the taskbar while it works as the card engine.</summary>
    public static class WindowHider
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }

        private static float next;
        private static bool styled;

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 1f;
            IntPtr h = Process.GetCurrentProcess().MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (Screen.fullScreen) Screen.SetResolution(640, 360, false);
            RECT r;
            if (GetWindowRect(h, out r) && r.l > -30000)
            {
                if (!styled)
                {
                    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
                    ShowWindow(h, 0);
                    SetWindowLong(h, GWL_EXSTYLE, (GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
                    ShowWindow(h, 4);
                    styled = true;
                }
                SetWindowPos(h, IntPtr.Zero, -32000, -32000, 640, 360, 0x0010 | 0x0004);
            }
        }
    }

    /// <summary>The ROUNDS card list: the active cards (UnboundLib's when it is installed, else ROUNDS's own).</summary>
    public static class Cards
    {
        private static Dictionary<string, CardInfo> byId;
        private static int builtFor = -1;

        public static List<CardInfo> All()
        {
            var list = new List<CardInfo>();
            try
            {
                Type cm = Type.GetType("UnboundLib.Cards.CardManager, UnboundLib");
                if (cm != null)
                {
                    var f = cm.GetField("activeCards", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (f != null && f.GetValue(null) is IEnumerable<CardInfo> active) list.AddRange(active.Where(c => c != null));
                }
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("UnboundLib card list: " + e.Message); }
            if (list.Count == 0 && CardChoice.instance != null && CardChoice.instance.cards != null)
                list.AddRange(CardChoice.instance.cards.Where(c => c != null));
            return list;
        }

        public static string Id(CardInfo c) => c.gameObject.name;

        public static Dictionary<string, CardInfo> Map()
        {
            var all = All();
            if (byId == null || builtFor != all.Count)
            {
                byId = new Dictionary<string, CardInfo>();
                foreach (var c in all)
                {
                    string id = Id(c);
                    if (!byId.ContainsKey(id)) byId[id] = c;
                }
                builtFor = all.Count;
            }
            return byId;
        }

        public static CardInfo Get(string id)
        {
            CardInfo c;
            return Map().TryGetValue(id ?? "", out c) ? c : null;
        }

        public static float Weight(CardInfo c)
        {
            switch (c.rarity.ToString())
            {
                case "Common": return 10f;
                case "Uncommon": return 4f;
                case "Rare": return 1f;
            }
            // RarityLib and similar add rarities; give them a small weight.
            return 2f;
        }

        public static string ColorHex(CardInfo c)
        {
            try { return ColorUtility.ToHtmlStringRGB(CardChoice.instance.GetCardColor(c.colorTheme)); }
            catch { return ColorUtility.ToHtmlStringRGB(c.cardColor); }
        }

        public static string[] StatLines(CardInfo c)
        {
            if (c.cardStats == null) return new string[0];
            return c.cardStats.Where(s => s != null).Select(s => (s.positive ? "+ " : "- ") + (string.IsNullOrEmpty(s.amount) ? s.GetSimpleAmount() : s.amount) + " " + s.stat).ToArray();
        }

        /// <summary>ROUNDS's own pick rules: rarity weights, no duplicates of allowMultiple=false cards, blacklisted categories.</summary>
        public static List<string> Offer(string[] owned, int count, int seed)
        {
            var rng = new System.Random(seed);
            var ownedCards = owned.Select(Get).Where(c => c != null).ToList();
            var pool = All().Where(c => Allowed(c, ownedCards)).ToList();
            var picked = new List<string>();
            while (picked.Count < count && pool.Count > 0)
            {
                float total = pool.Sum(Weight);
                double roll = rng.NextDouble() * total;
                CardInfo chosen = pool[pool.Count - 1];
                foreach (var c in pool)
                {
                    roll -= Weight(c);
                    if (roll <= 0) { chosen = c; break; }
                }
                picked.Add(Id(chosen));
                pool.RemoveAll(c => c.cardName == chosen.cardName);
            }
            return picked;
        }

        private static bool Allowed(CardInfo c, List<CardInfo> owned)
        {
            foreach (var o in owned)
            {
                if (!o.allowMultiple && o.cardName == c.cardName) return false;
                if (o.blacklistedCategories != null && c.categories != null && o.blacklistedCategories.Any(b => c.categories.Contains(b))) return false;
                if (c.blacklistedCategories != null && o.categories != null && c.blacklistedCategories.Any(b => o.categories.Contains(b))) return false;
            }
            var g = c.GetComponent<Gun>();
            if (g != null && g.lockGunToDefault && owned.Any(o => { var og = o.GetComponent<Gun>(); return og != null && og.lockGunToDefault; })) return false;
            return true;
        }
    }

    public static partial class StatFold
    {
        /// <summary>ROUNDS CopyGunStats colour blend, on hex strings ("000000" = no colour).</summary>
        public static string MixColor(string current, Color add)
        {
            if (add == Color.black) return current;
            Color to;
            if (!ColorUtility.TryParseHtmlString("#" + current, out to)) to = Color.black;
            if (to == Color.black) to = add;
            float r = Mathf.Pow((to.r * to.r + add.r * add.r) / 2f, 0.5f);
            float g = Mathf.Pow((to.g * to.g + add.g * add.g) / 2f, 0.5f);
            float b = Mathf.Pow((to.b * to.b + add.b * add.b) / 2f, 0.5f);
            float h, s, v;
            Color.RGBToHSV(new Color(r, g, b, 1f), out h, out s, out v);
            return ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(h, 1f, 1f));
        }

        /// <summary>The whole build for a list of cards, plus the cards whose own code does more than change stats.</summary>
        public static BuildStats Build(string[] owned, List<string> partial)
        {
            var b = new BuildStats();
            foreach (string id in owned)
            {
                CardInfo c = Cards.Get(id);
                if (c == null) continue;
                Fold(b, c.GetComponent<Gun>(), c.GetComponent<CharacterStatModifiers>(), c.GetComponentInChildren<Block>());
                if (DoesMoreThanStats(c)) partial.Add(c.cardName);
            }
            return b;
        }

        private static bool DoesMoreThanStats(CardInfo c)
        {
            var g = c.GetComponent<Gun>();
            if (g != null && g.objectsToSpawn != null && g.objectsToSpawn.Length > 0) return true;
            var s = c.GetComponent<CharacterStatModifiers>();
            if (s != null && s.AddObjectToPlayer != null) return true;
            var k = c.GetComponentInChildren<Block>();
            if (k != null && k.objectsToSpawn != null && k.objectsToSpawn.Count > 0) return true;
            foreach (var mb in c.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                Type t = mb.GetType();
                for (Type bt = t.BaseType; bt != null; bt = bt.BaseType)
                    if (bt.FullName == "UnboundLib.Cards.CustomCard")
                    {
                        var m = t.GetMethod("OnAddCard");
                        if (m != null && m.DeclaringType == t && m.GetMethodBody() != null && m.GetMethodBody().GetILAsByteArray().Length > 2) return true;
                    }
            }
            return false;
        }
    }
}
