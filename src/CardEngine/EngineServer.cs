using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>Localhost server for the Cuphead side (sheets/ipc.json). Requests are handled on Unity's main thread.</summary>
    public class EngineServer
    {
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private readonly object gate = new object();
        private readonly Queue<KeyValuePair<StreamWriter, string>> inbox = new Queue<KeyValuePair<StreamWriter, string>>();
        private readonly Queue<KeyValuePair<StreamWriter, Request>> renders = new Queue<KeyValuePair<StreamWriter, Request>>();
        private bool rendering;
        private readonly Dictionary<string, string> renderCache = new Dictionary<string, string>();
        private string portFile;
        private readonly FrameServer frames = new FrameServer();
        private readonly Puppet[] puppets = new Puppet[2];
        private float nextFrame;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            acceptThread = new Thread(Accept) { IsBackground = true, Name = "Roundhead engine" };
            acceptThread.Start();
            Directory.CreateDirectory(Proto.DataDir());
            portFile = Path.Combine(Proto.DataDir(), Proto.PortFileName);
            File.WriteAllText(portFile, port.ToString());
            EnginePlugin.L.LogInfo("Roundhead card engine listening on 127.0.0.1:" + port);
            frames.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            frames.Stop();
            try { if (portFile != null && File.Exists(portFile)) File.Delete(portFile); } catch { }
        }

        private void Accept()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { break; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        private void Serve(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                var ns = c.GetStream();
                var reader = new StreamReader(ns, new UTF8Encoding(false));
                var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) != null)
                    lock (gate) inbox.Enqueue(new KeyValuePair<StreamWriter, string>(writer, line));
            }
            catch { }
            try { c.Close(); } catch { }
        }

        private static void Send(StreamWriter w, Response r)
        {
            string json = Json.Write(r);
            try { lock (w) w.WriteLine(json); } catch { }
        }

        public void Pump()
        {
            while (true)
            {
                KeyValuePair<StreamWriter, string> item;
                lock (gate)
                {
                    if (inbox.Count == 0) break;
                    item = inbox.Dequeue();
                }
                Request req;
                try { req = Json.Read<Request>(item.Value); }
                catch (Exception e) { EnginePlugin.L.LogWarning("bad request: " + e.Message); continue; }
                try { Handle(item.Key, req); }
                catch (Exception e)
                {
                    EnginePlugin.L.LogError(req.op + " failed: " + e);
                    Send(item.Key, new Response { rid = req.rid, ok = false, error = e.Message });
                }
            }
            PumpPuppets();
            if (!rendering && renders.Count > 0)
            {
                var jobs = new List<KeyValuePair<StreamWriter, Request>>();
                while (renders.Count > 0 && jobs.Count < 6) jobs.Add(renders.Dequeue());
                EnginePlugin.I.StartCoroutine(RenderBatch(jobs));
            }
        }

        private void Handle(StreamWriter w, Request req)
        {
            switch (req.op)
            {
                case Ops.Hello: Send(w, OnHello(req)); break;
                case Ops.Arena_cards:
                    Arena.I?.SetCards(req.slot, req.owned);
                    Send(w, new Response { rid = req.rid, ok = Arena.I != null });
                    break;
                case Ops.Catalog: Send(w, OnCatalog(req)); break;
                case Ops.Offer: Send(w, OnOffer(req)); break;
                case Ops.Render: OnRender(w, req); break;
                case Ops.Build: Send(w, OnBuild(req)); break;
                case Ops.Skin: Send(w, OnSkin(req)); break;
                case Ops.Ping: Send(w, OnPing(req)); break;
                case Ops.Pose: OnPose(req); break;
                // Dev only (the card bench, cardprobe.py); declared as Ops.Arena_probe in sheets/ipc.json.
                case "arena_probe": Send(w, OnArenaProbe(req)); break;
                default: Send(w, new Response { rid = req.rid, ok = false, error = "unknown op " + req.op }); break;
            }
        }

        public Response OnHello(Request req)
        {
            return new Response
            {
                rid = req.rid,
                ok = true,
                cardCount = Cards.All().Count,
                roundsVersion = Application.version,
                mods = Chainloader.PluginInfos.Values.Select(p => p.Metadata.Name + " " + p.Metadata.Version).ToArray()
            };
        }

        public Response OnCatalog(Request req)
        {
            var list = Cards.All().Select(c => new CardData
            {
                id = Cards.Id(c),
                name = c.cardName ?? "",
                description = c.cardDestription ?? "",
                rarity = c.rarity.ToString(),
                theme = c.colorTheme.ToString(),
                colorHex = Cards.ColorHex(c),
                stats = Cards.StatLines(c)
            }).ToArray();
            return new Response { rid = req.rid, ok = true, cards = list, cardCount = list.Length };
        }

        public Response OnOffer(Request req)
        {
            var ids = Cards.Offer(req.owned ?? new string[0], Mathf.Clamp(req.count, 1, 10), req.seed);
            return new Response { rid = req.rid, ok = ids.Count > 0, ids = ids.ToArray(), error = ids.Count > 0 ? "" : "no cards available" };
        }

        public Response OnBuild(Request req)
        {
            var partial = new List<string>();
            BuildStats b = StatFold.Build(req.owned ?? new string[0], partial);
            return new Response { rid = req.rid, ok = true, stats = b, partial = partial.ToArray() };
        }

        public Response OnSkin(Request req)
        {
            return new Response { rid = req.rid, ok = true, skin = SkinExport.Export(Mathf.Clamp(req.slot, 0, 3)) };
        }

        public Response OnPing(Request req) => new Response { rid = req.rid, ok = true };

        /// <summary>Dev only: one arena player seen from inside ROUNDS (<see cref="ArenaProbe"/>), for card effects that never
        /// cross the arena link. The reply carries it as a JSON object in ids[0] (Protocol.cs stays unchanged); cardCount is
        /// the number of cards held.</summary>
        public Response OnArenaProbe(Request req)
        {
            if (Arena.I == null) return new Response { rid = req.rid, ok = false, error = "no arena" };
            var pr = ArenaProbe.Take(Arena.I, Mathf.Clamp(req.slot, 0, 1));
            return new Response
            {
                rid = req.rid,
                ok = pr.present,
                cardCount = pr.cards,
                ids = new[] { Json.Write(pr) },
                error = pr.present ? "" : "no ROUNDS player in slot " + req.slot
            };
        }

        /// <summary>A pose for one player's ROUNDS puppet; the puppet is made on the first pose (no answer is sent).</summary>
        public void OnPose(Request req)
        {
            int slot = Mathf.Clamp(req.slot, 0, 1);
            if (puppets[slot] == null)
            {
                puppets[slot] = new Puppet(slot);
                if (!puppets[slot].Create()) return;
            }
            puppets[slot].SetPose(req);
        }

        private void PumpPuppets()
        {
            float dt = Time.deltaTime;
            bool send = Time.realtimeSinceStartup >= nextFrame && frames.HasClients;
            if (send) nextFrame = Time.realtimeSinceStartup + 1f / 30f;
            for (int i = 0; i < 2; i++)
            {
                var p = puppets[i];
                if (p == null || !p.Ok) continue;
                p.Tick(dt);
                if (send && p.Active) frames.Broadcast(i, p.Render());
            }
        }

        public void OnRender(StreamWriter w, Request req)
        {
            string png;
            if (renderCache.TryGetValue(req.id, out png))
            {
                Send(w, new Response { rid = req.rid, ok = true, png = png });
                return;
            }
            renders.Enqueue(new KeyValuePair<StreamWriter, Request>(w, req));
        }

        private IEnumerator RenderBatch(List<KeyValuePair<StreamWriter, Request>> jobs)
        {
            rendering = true;
            var cards = new List<CardInfo>();
            var owners = new List<KeyValuePair<StreamWriter, Request>>();
            foreach (var job in jobs)
            {
                CardInfo card = Cards.Get(job.Value.id);
                if (card == null) Send(job.Key, new Response { rid = job.Value.rid, ok = false, error = "unknown card " + job.Value.id });
                else { cards.Add(card); owners.Add(job); }
            }
            var results = new string[cards.Count];
            if (cards.Count > 0) yield return CardRenderer.RenderMany(cards.ToArray(), results);
            for (int i = 0; i < owners.Count; i++)
            {
                string png = results[i];
                if (png != null) renderCache[owners[i].Value.id] = png;
                Send(owners[i].Key, new Response { rid = owners[i].Value.rid, ok = png != null, png = png ?? "", error = png == null ? "render failed" : "" });
            }
            rendering = false;
        }
    }

    /// <summary>
    /// Dev only (op "arena_probe", for cardprobe.py): one arena player as ROUNDS sees it, for card effects that never cross
    /// the arena link (lifesteal, Taste of Blood, Brawler, Scavenger, Refresh, Pacifist, Masochist, Chase, Low Ground,
    /// Thank You Sir), the player's own statuses (co-op leak tests on the partner), and a census of bullets (outside
    /// Cuphead's view) and Dazzle stun objects per enemy. Every part is read on its own, so one failing read only adds a note.
    /// Positions are Cuphead units; times are ROUNDS Time.time.
    /// </summary>
    [Serializable]
    public class ArenaProbe
    {
        public int slot;
        public bool present;
        public float time;
        public float x, y, vx, vy;
        public float hp, maxHp;
        public bool dead, playing;
        // on-damage rewards (CharacterStatModifiers.DealtDamage, CharacterStatModifiers.cs:127-156)
        public float lifeSteal, sinceDealtDamage = -1f, regen;
        public bool refreshOnDamage;
        // movement (PlayerMovement.cs:59 multiplies by movementSpeed; Chase and Taste of Blood change it)
        public float moveSpeed, jump, tasteOfBloodSpeed, rageSpeed, attackSpeedMultiplier;
        public int respawns, remainingRespawns;
        // the player's own statuses (a co-op partner must keep these at 0 when only enemies are targeted)
        public float slow, slowSlow, fastSlow, stunTime, silenceTime;
        public bool stunned, silenced;
        // gun (gunDamage = Gun.damage x bulletDamageMultiplier, the multiplier the ramping cards change)
        public float gunDamage, damage, bulletDamageMultiplier, attackSpeed, gunAttackSpeedMultiplier, projectileSpeed, sinceAttack;
        public int projectiles, reflects, bursts;
        public float reloadTime;
        public int ammo = -1, maxAmmo = -1;
        // block (Block.Cooldown, Block.cs:82-85; the counter runs up from 0 after each block)
        public float blockCooldown, blockCdLeft, sinceBlock;
        public bool blocking;
        public int additionalBlocks;
        /// <summary>PCE BlockAdditionalData.timeOfLastSuccessfulBlock (Masochist), Time.time; -100 when never.</summary>
        public float masochistLastBlock = -100f;
        public int cards;
        public string[] cardNames = new string[0];
        // census
        public int bulletsAlive, bulletsOutsideView, stunObjects, stunObjectsLoose, laserSegments = -1;
        public ProbeTarget[] targets = new ProbeTarget[0];
        public string[] notes = new string[0];

        private static readonly AccessTools.FieldRef<CharacterStatModifiers, float> SinceDealt = AccessTools.FieldRefAccess<CharacterStatModifiers, float>("sinceDealtDamage");
        private static readonly AccessTools.FieldRef<GunAmmo, int> CurrentAmmo = AccessTools.FieldRefAccess<GunAmmo, int>("currentAmmo");
        private static readonly AccessTools.FieldRef<PlayerVelocity, Vector2> Velocity = AccessTools.FieldRefAccess<PlayerVelocity, Vector2>("velocity");

        public static ArenaProbe Take(Arena a, int slot)
        {
            var pr = new ArenaProbe { slot = slot, time = Time.time };
            var notes = new List<string>();
            Player p = null;
            if (PlayerManager.instance != null)
                foreach (var q in PlayerManager.instance.players) if (q != null && a.SlotOf(q) == slot) { p = q; break; }
            pr.present = p != null;
            Part(notes, "census", () => pr.Census(a));
            if (p == null) { pr.notes = notes.ToArray(); return pr; }
            var d = p.data;
            Part(notes, "body", () =>
            {
                Vector2 c = a.ToCuphead(p.transform.position), v = Velocity(d.playerVel) * a.Scale;
                pr.x = c.x; pr.y = c.y; pr.vx = v.x; pr.vy = v.y;
                pr.hp = d.health; pr.maxHp = d.maxHealth; pr.dead = d.dead; pr.playing = d.isPlaying;
                pr.stunned = d.isStunned; pr.silenced = d.isSilenced; pr.stunTime = d.stunTime; pr.silenceTime = d.silenceTime;
                pr.cards = d.currentCards != null ? d.currentCards.Count : 0;
                pr.cardNames = d.currentCards != null ? d.currentCards.Where(k => k != null).Select(k => k.cardName ?? k.name).ToArray() : new string[0];
            });
            Part(notes, "stats", () =>
            {
                var s = d.stats;
                pr.lifeSteal = s.lifeSteal; pr.regen = s.regen; pr.refreshOnDamage = s.refreshOnDamage;
                pr.sinceDealtDamage = SinceDealt(s);
                pr.moveSpeed = s.movementSpeed; pr.jump = s.jump; pr.tasteOfBloodSpeed = s.tasteOfBloodSpeed; pr.rageSpeed = s.rageSpeed;
                pr.attackSpeedMultiplier = s.attackSpeedMultiplier; pr.respawns = s.respawns; pr.remainingRespawns = s.remainingRespawns;
                pr.slow = s.GetSlow(); pr.slowSlow = s.slowSlow; pr.fastSlow = s.fastSlow;
            });
            Part(notes, "gun", () =>
            {
                var g = d.weaponHandler.gun;
                pr.damage = g.damage; pr.bulletDamageMultiplier = g.bulletDamageMultiplier; pr.gunDamage = g.damage * g.bulletDamageMultiplier;
                pr.attackSpeed = g.attackSpeed; pr.gunAttackSpeedMultiplier = g.attackSpeedMultiplier; pr.projectileSpeed = g.projectileSpeed;
                pr.sinceAttack = g.sinceAttack; pr.projectiles = g.numberOfProjectiles; pr.reflects = g.reflects; pr.bursts = g.bursts;
                var ga = g.GetComponentInChildren<GunAmmo>();
                if (ga != null)
                {
                    pr.ammo = CurrentAmmo(ga); pr.maxAmmo = ga.maxAmmo;
                    pr.reloadTime = (ga.reloadTime + ga.reloadTimeAdd) * ga.reloadTimeMultiplier;   // GunAmmo.ReloadTime (GunAmmo.cs:77-80)
                }
            });
            Part(notes, "block", () =>
            {
                var b = d.block;
                pr.blockCooldown = b.Cooldown(); pr.blockCdLeft = Mathf.Max(0f, b.Cooldown() - b.counter); pr.sinceBlock = b.sinceBlock;
                pr.blocking = b.sinceBlock < 0.3f;   // Block.IsBlocking without its status-effect side effect (Block.cs:340-347)
                pr.additionalBlocks = b.additionalBlocks;
            });
            Part(notes, "masochist", () =>
            {
                var get = MasochistData;
                if (get != null) pr.masochistLastBlock = Traverse.Create(get.Invoke(null, new object[] { d.block })).Field("timeOfLastSuccessfulBlock").GetValue<float>();
            });
            pr.notes = notes.ToArray();
            return pr;
        }

        /// <summary>Bullets (and how many left Cuphead's view, grown by 2 ROUNDS units), Dazzle stun objects per enemy, PCE laser hurtboxes.</summary>
        private void Census(Arena a)
        {
            var bullets = UnityEngine.Object.FindObjectsOfType<ProjectileHit>();
            bulletsAlive = bullets.Length;
            var link = a.link;
            float cx = link.F(ArenaLink.C_CamX), cy = link.F(ArenaLink.C_CamY), half = link.F(ArenaLink.C_CamOrtho), aspect = link.F(ArenaLink.C_Aspect);
            if (half > 1f && aspect > 0.1f)
            {
                Vector2 lo = a.ToRounds(cx - half * aspect, cy - half) - Vector2.one * 2f, hi = a.ToRounds(cx + half * aspect, cy + half) + Vector2.one * 2f;
                foreach (var b in bullets)
                {
                    if (b == null) continue;
                    Vector2 p = b.transform.position;
                    if (p.x < lo.x || p.x > hi.x || p.y < lo.y || p.y > hi.y) bulletsOutsideView++;
                }
            }
            // Dazzle's E_StunOverTime carries a StunPlayer; ThereCanOnlyBeOne should leave one per enemy (design MATRIX: DAZZLE).
            var perTarget = new Dictionary<CupheadTarget, int>();
            foreach (var sp in UnityEngine.Object.FindObjectsOfType<StunPlayer>())
            {
                if (sp == null) continue;
                stunObjects++;
                var t = sp.GetComponentInParent<CupheadTarget>();
                if (t == null) { stunObjectsLoose++; continue; }
                int n; perTarget.TryGetValue(t, out n); perTarget[t] = n + 1;
            }
            var list = new List<ProbeTarget>();
            foreach (var t in CupheadTarget.All)
            {
                if (t == null) continue;
                int n; perTarget.TryGetValue(t, out n);
                var st = a.Status(t.group);
                Vector2 c = a.ToCuphead(t.Centre);
                list.Add(new ProbeTarget
                {
                    id = t.id, group = t.group, alive = t.Alive, x = c.x, y = c.y, stunObjects = n, flagsIn = st.flagsIn,
                    slow = st.Slow, stun = st.stun, silence = st.silence, dot = st.dot, flags = st.flags
                });
            }
            targets = list.ToArray();
            var laser = LaserHurtbox;
            if (laser != null) laserSegments = UnityEngine.Object.FindObjectsOfType(laser).Length;
        }

        // Looked up once: AccessTools.TypeByName scans every assembly (and warns about MMHOOK each time).
        private static readonly System.Reflection.MethodInfo MasochistData = FindMasochistData();
        private static readonly Type LaserHurtbox = AccessTools.TypeByName("PCE.Cards.LaserHurtbox");

        private static System.Reflection.MethodInfo FindMasochistData()
        {
            var ext = AccessTools.TypeByName("PCE.Extensions.BlockExtension");
            return ext != null ? AccessTools.Method(ext, "GetAdditionalData", new[] { typeof(Block) }) : null;
        }

        private static void Part(List<string> notes, string name, Action read)
        {
            try { read(); }
            catch (Exception e) { notes.Add(name + ": " + e.GetType().Name + " " + e.Message); }
        }
    }

    /// <summary>One Cuphead enemy hit area in an <see cref="ArenaProbe"/>: where it is and its ROUNDS-side statuses.</summary>
    [Serializable]
    public class ProbeTarget
    {
        public int id, group, flagsIn, flags, stunObjects;
        public bool alive;
        public float x, y, slow, stun, silence, dot;
    }
}
