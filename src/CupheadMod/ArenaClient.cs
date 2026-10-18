using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Cuphead's end of the arena link (sheets/ipc.json "arena"): every frame it sends the camera, both players' ROUNDS
    /// input (plus the cursor, down and weapon-switch keys some cards use), the level's floors, walls and enemy hit areas
    /// (each with its enemy group and Light/Boss/NoStatus flags), and events (damage, blocked hits, revive, teleport);
    /// it reads back where ROUNDS's players are, their health and block, hits on Cuphead enemies, the effect ring and
    /// status table that <see cref="EnemyStatus"/> shows on the enemies, and ROUNDS's picture.
    /// While it is live, the Cuphead player objects only follow ROUNDS's players (Cuphead still uses them for enemy
    /// aim, hits, the camera and co-op).
    /// </summary>
    public static class ArenaClient
    {
        public static ArenaLink link;
        private const int R = ArenaLink.ResultOff;
        private static int lastResultBeat = -1;
        private static float lastResultChange = -100f;
        private static int levelSerial;
        private static Level seenLevel;
        private static readonly int[] jumpPresses = new int[2], shootPresses = new int[2], blockPresses = new int[2], acceptPresses = new int[2], navPresses = new int[2];
        private static readonly int[] switchPresses = new int[2];
        private static readonly float[] blockedHitAt = { -100f, -100f };
        private static readonly int[] seenBlocks = new int[2];
        private static readonly float[] hitUntil = new float[2];
        private static int fxRead, statusFrameSeen = -1;
        private static float statusFrameAt;
        /// <summary>CupheadButton.SwitchWeapon (LevelPlayerWeaponManager.HandleWeaponSwitch, LevelPlayerWeaponManager.cs:644).</summary>
        private const int SwitchWeaponButton = 5;
        private static readonly int[] life = new int[2];
        private static readonly float[] spawnSentAt = new float[2];
        private static float lastRoundsSeen = -100f, levelSeenAt;
        private static int roundsPidSeen;
        private static bool wasLive;
        private static int hitRead;
        private static int pickSerial, pickSlot = -1;
        private static float nextScan;
        private static bool hudHidden;
        public static int Mode;

        /// <summary>A tracked collider; on: written with its shape this frame; offSince: unscaled time it went off (-1 while on).</summary>
        private struct Col { public Collider2D col; public int id; public int kind; public bool on; public float offSince; }
        private static readonly List<Col> cols = new List<Col>();
        /// <summary>Entries WriteBounds puts first in the collider table (left, right, ceiling).</summary>
        private const int BoundCount = 3;
        /// <summary>Colliders tracked at most (twice the table): a full list first drops its longest-off entries.</summary>
        private const int MaxTracked = 2 * ArenaLink.MaxCols;
        private static bool colCapLogged, trackCapLogged;
        private static readonly Dictionary<int, DamageReceiver> targets = new Dictionary<int, DamageReceiver>();
        /// <summary>Group id (DamageReceiver instance id, sent in K_Group) to the receiver: the status table and effect ring name groups.</summary>
        private static readonly Dictionary<int, DamageReceiver> groups = new Dictionary<int, DamageReceiver>();

        public static void Init()
        {
            link = ArenaLink.Open();
            if (link == null) { Plugin.L.LogWarning("arena: no shared memory (" + ArenaLink.LastOpenNote + ")"); return; }
            Plugin.L.LogInfo("arena: shared memory " + ArenaLink.LastOpenNote);
            link.I(ArenaLink.C_Magic, ArenaLink.Magic);
            link.I(ArenaLink.C_Pid, System.Diagnostics.Process.GetCurrentProcess().Id);
            hitRead = link.I(R + ArenaLink.R_HitWrite);
            fxRead = link.I(R + ArenaLink.R_FxWrite);
            statusFrameSeen = link.I(R + ArenaLink.R_StatusFrame);
            // A counter that continues from the memory never looks like a fresh press to a ROUNDS that is still running.
            for (int i = 0; i < 2; i++) switchPresses[i] = link.I(ArenaLink.Slot2(i) + ArenaLink.U_SwitchPresses);
            // Life numbers continue from what ROUNDS last echoed (a ROUNDS from before may still be running).
            for (int i = 0; i < 2; i++) life[i] = Mathf.Max(0, link.I(ArenaLink.RSlot(i) + ArenaLink.Q_Life));
            pickSerial = link.I(ArenaLink.C_Pick + ArenaLink.P_Serial);
        }

        /// <summary>ROUNDS is answering and its sandbox is up.</summary>
        public static bool Live => link != null && link.I(R + ArenaLink.R_Magic) == ArenaLink.Magic && Time.unscaledTime - lastResultChange < 1.5f
            && link.I(R + ArenaLink.R_State) != ArenaLink.StateOff;

        /// <summary>ROUNDS's engine is beating the result block (it may still be loading its cards or starting its sandbox).</summary>
        public static bool RoundsRunning => link != null && link.I(R + ArenaLink.R_Magic) == ArenaLink.Magic && Time.unscaledTime - lastResultChange < 1.5f;

        /// <summary>Which ROUNDS process wrote the result (it changes when the launcher starts ROUNDS again).</summary>
        public static int RoundsPid => link == null ? 0 : link.I(R + ArenaLink.R_Pid);

        /// <summary>
        /// ROUNDS's real player is on its way for this Cuphead player (ROUNDS starting, loading cards, or started again):
        /// the player is held, hidden and safe instead of showing the stand-in. Gives up after a while, so a ROUNDS that
        /// never comes still leaves a playable stand-in.
        /// </summary>
        public static bool Waiting(BlobController c)
        {
            if (link == null || c == null || Mode != 1 || Drives(c)) return false;
            if (Time.unscaledTime - levelSeenAt > Tuning.roundsWaitSeconds) return false;
            return RoundsRunning || Plugin.Engine.Connected || Time.realtimeSinceStartup < Tuning.roundsWaitSeconds || Time.unscaledTime - lastRoundsSeen < 30f;
        }

        public static bool WaitingAny
        {
            get
            {
                foreach (var c in BlobController.All) if (Waiting(c)) return true;
                return false;
            }
        }

        /// <summary>ROUNDS has done this player's latest spawn: its result slot describes the current life.</summary>
        public static bool Synced(BlobController c) => link != null && link.I(ArenaLink.RSlot(c.index) + ArenaLink.Q_Life) == life[c.index];

        /// <summary>Where ROUNDS's player body is this frame (Cuphead units), for drawings that hang off it (the drop shadow).</summary>
        public static bool RoundsBody(BlobController c, out Vector2 centre, out float radius)
        {
            centre = Vector2.zero;
            radius = 0f;
            if (link == null || !Drives(c) || !Synced(c)) return false;
            int q = ArenaLink.RSlot(c.index);
            centre = new Vector2(link.F(q + ArenaLink.Q_X), link.F(q + ArenaLink.Q_Y));
            radius = link.F(q + ArenaLink.Q_Radius);
            return radius > 0.01f;
        }

        /// <summary>This Cuphead player is driven by ROUNDS right now.</summary>
        public static bool Drives(BlobController c)
        {
            return c != null && Mode == 1 && Live && link.I(ArenaLink.RSlot(c.index) + ArenaLink.Q_Present) != 0;
        }

        public static bool Picking => pickSlot >= 0;

        private static bool InLevel => Level.Current != null && BlobController.All.Count > 0;

        // ------------------------------------------------------------------ every frame

        public static void Tick()
        {
            if (link == null) return;
            int rb = link.I(R + ArenaLink.R_Beat);
            if (rb != lastResultBeat) { lastResultBeat = rb; lastResultChange = Time.unscaledTime; }
            if (RoundsRunning) lastRoundsSeen = Time.unscaledTime;
            bool live = Live;
            if (wasLive && !live) Plugin.L.LogWarning("arena: ROUNDS stopped answering (engine pid " + RoundsPid + "); see engine-exit.log");
            wasLive = live;
            // ROUNDS's world stands still while Cuphead is paused (pause menu, parry hit-stop).
            link.I(ArenaLink.C_Paused, PauseManager.state == PauseManager.State.Paused ? 1 : 0);
            int pid = RoundsPid;
            // The card link can come up after ROUNDS's player appeared (late or restarted ROUNDS), and a deck changes between
            // levels too (wiped at a win, a shop pack): keep sending until ROUNDS confirms, in any mode.
            if (live)
                for (int i = 0; i < 2; i++)
                    if (cardsFor[i] != pid && link.I(ArenaLink.RSlot(i) + ArenaLink.Q_Present) != 0 && Progress.PlayerPresent(i)) TrySyncCards(i);
            if (live && pid != roundsPidSeen)
            {
                // A ROUNDS (started again) appeared: its players need the current lives, cards and any running pick.
                if (roundsPidSeen != 0) Plugin.L.LogInfo("arena: ROUNDS engine " + pid + " is here (was " + roundsPidSeen + ")");
                roundsPidSeen = pid;
                if (Picking) { int slot = pickSlot; RequestPick(slot, Mathf.Clamp(Progress.Pending(slot), 1, ArenaLink.MaxPicked)); }
            }
            link.I(ArenaLink.C_Magic, ArenaLink.Magic);
            link.Inc(ArenaLink.C_Beat);
            link.F(ArenaLink.C_Scale, Tuning.moveWorldScale);
            Mode = Picking ? 2 : InLevel ? 1 : 0;
            // The level is over (won, lost, left): the decks as they stand reach ROUNDS now, never during a KO outro. A won
            // level wiped them, so its players go back to the plain gun before the next level; Cuphead's Level object can
            // outlive the fight (the results screen), so this does not wait for the level change.
            if (lastMode == 1 && Mode == 0)
                for (int i = 0; i < 2; i++) if (Progress.PlayerPresent(i)) SyncCards(i);
            lastMode = Mode;
            link.I(ArenaLink.C_Mode, Mode);
            FrameSize();
            if (Level.Current != seenLevel)
            {
                seenLevel = Level.Current;
                levelSeenAt = Time.unscaledTime;
                levelSerial++;
                cols.Clear();
                targets.Clear();
                groups.Clear();
                colCapLogged = trackCapLogged = false;
                nextScan = 0f;
                hudHidden = false;
                link.I(ArenaLink.C_Level, levelSerial);
                OnLevelChanged(seenLevel);
            }
            if (Mode == 1)
            {
                Camera cam = CupheadLevelCamera.Current != null ? CupheadLevelCamera.Current.camera : Camera.main;
                if (cam != null)
                {
                    link.F(ArenaLink.C_CamX, cam.transform.position.x);
                    link.F(ArenaLink.C_CamY, cam.transform.position.y);
                    link.F(ArenaLink.C_CamOrtho, cam.orthographicSize);
                    link.F(ArenaLink.C_Aspect, cam.aspect);
                }
                if (Time.unscaledTime >= nextScan) { Scan(); nextScan = Time.unscaledTime + 0.5f; }
                WriteCols();
                ReadHits();
                // Statuses never stop the beat: each step fails on its own.
                try { EnemyStatus.Tick(); } catch (System.Exception e) { EnemyStatus.LogOnce("status tick", e); }
                try { ReadFx(); } catch (System.Exception e) { EnemyStatus.LogOnce("effect ring", e); }
                try { ReadStatus(); } catch (System.Exception e) { EnemyStatus.LogOnce("status table", e); }
                if (Live && !hudHidden) HideCupheadHud();
            }
            else if (EnemyStatus.Active.Count > 0)
            {
                try { EnemyStatus.ClearAll(); } catch (System.Exception e) { EnemyStatus.LogOnce("status clear", e); }
            }
            for (int i = 0; i < 2; i++) WriteInput(i);
            if (Mode == 1 && Time.unscaledTime > nextLog)
            {
                nextLog = Time.unscaledTime + 3f;
                var sb = new System.Text.StringBuilder("arena: live=" + Live + " cols=" + cols.Count + " targets=" + targets.Count);
                foreach (var c in BlobController.All)
                {
                    if (c == null) continue;
                    int q = ArenaLink.RSlot(c.index);
                    sb.Append(" | p" + (c.index + 1) + " rounds=(" + link.F(q + ArenaLink.Q_X).ToString("0") + "," + link.F(q + ArenaLink.Q_Y).ToString("0") + ") hp=" + link.F(q + ArenaLink.Q_Hp).ToString("0")
                        + " dead=" + link.I(q + ArenaLink.Q_Dead) + " cup=(" + c.player.center.x.ToString("0") + "," + c.player.center.y.ToString("0") + ") cupDead=" + c.player.IsDead);
                }
                Plugin.L.LogInfo(sb.ToString());
            }
        }

        private static float nextLog;
        private static int lastMode;

        private static void FrameSize()
        {
            float aspect = 16f / 9f;
            if (Screen.width < 320) return; // minimised: keep the last size
            int w = Mathf.Min((int)Tuning.arenaMaxFrameWidth, Mathf.Max(320, Screen.width));
            int h = Mathf.Min(ArenaLink.MaxH, Mathf.RoundToInt(w / aspect));
            link.I(ArenaLink.C_FrameW, w);
            link.I(ArenaLink.C_FrameH, h);
        }

        // ------------------------------------------------------------------ input

        private static void WriteInput(int slot)
        {
            int s = ArenaLink.Slot(slot);
            BlobController c = null;
            foreach (var b in BlobController.All) if (b != null && b.index == slot) c = b;
            bool present = c != null || (Picking && slot == pickSlot) || (Mode == 0 && slot == 0);
            link.I(s + ArenaLink.S_Present, present ? 1 : 0);
            if (c != null && Mode == 1)
            {
                var input = c.input;
                bool act = c.CanAct;
                Vector2 move = act ? input.Move() : Vector2.zero;
                link.F(s + ArenaLink.S_MoveX, move.x);
                link.F(s + ArenaLink.S_MoveY, move.y);
                Vector2 aim = input.Aim(c.player.center);
                link.F(s + ArenaLink.S_AimX, aim.x);
                link.F(s + ArenaLink.S_AimY, aim.y);
                if (act && input.JumpDown()) link.I(s + ArenaLink.S_JumpPresses, ++jumpPresses[slot]);
                link.I(s + ArenaLink.S_JumpHeld, act && input.JumpHeld() ? 1 : 0);
                bool shoot = act && input.ShootHeld();
                if (act && input.ShootDown()) link.I(s + ArenaLink.S_ShootPresses, ++shootPresses[slot]);
                link.I(s + ArenaLink.S_ShootHeld, shoot ? 1 : 0);
                if (act && input.BlockDown()) link.I(s + ArenaLink.S_BlockPresses, ++blockPresses[slot]);
                WriteSlot2(slot, c, aim, move, act);
            }
            else if (Picking && slot == pickSlot)
            {
                Rewired.Player rp = PlayerManager.GetPlayerInput(slot == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
                bool kb = slot == 0;
                bool left = (rp != null && (rp.GetButtonDown(Buttons.PickLeft) || rp.GetNegativeButtonDown(0))) || (kb && (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)));
                bool right = (rp != null && (rp.GetButtonDown(Buttons.PickRight) || rp.GetButtonDown(0))) || (kb && (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)));
                bool accept = (rp != null && (rp.GetButtonDown(Buttons.PickAccept) || rp.GetButtonDown(Buttons.Jump))) || (kb && (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetMouseButtonDown(0)));
                if (TestMode.Active && Time.unscaledTime - pickStartedAt > 3.5f && Time.unscaledTime - lastTestAccept > 2.5f) { accept = true; lastTestAccept = Time.unscaledTime; }
                if (left || right)
                {
                    link.I(s + ArenaLink.S_NavDir, left ? -1 : 1);
                    link.I(s + ArenaLink.S_NavPresses, ++navPresses[slot]);
                }
                if (accept) link.I(s + ArenaLink.S_AcceptPresses, ++acceptPresses[slot]);
                if (kb)
                {
                    link.F(s + ArenaLink.S_MouseX, Input.mousePosition.x / Mathf.Max(1, Screen.width));
                    link.F(s + ArenaLink.S_MouseY, Input.mousePosition.y / Mathf.Max(1, Screen.height));
                }
                else link.F(s + ArenaLink.S_MouseX, -1f);
            }
            else
            {
                link.F(s + ArenaLink.S_MoveX, 0f);
                link.F(s + ArenaLink.S_MoveY, 0f);
                link.I(s + ArenaLink.S_JumpHeld, 0);
                link.I(s + ArenaLink.S_ShootHeld, 0);
            }
            if (c == null || Mode != 1)
            {
                int x = ArenaLink.Slot2(slot);
                link.I(x + ArenaLink.U_CursorOn, 0);
                link.I(x + ArenaLink.U_DownHeld, 0);
            }
        }

        /// <summary>
        /// The extra per-player keys (ArenaLink.Slot2): the cursor in Cuphead world units for Remote (the mouse for the
        /// keyboard player, else a point cursorAimReach along the aim), down held for PCE Super Jump, and a counter of
        /// Cuphead weapon-switch presses for the PCE Laser toggle. Counted here once per frame, not in
        /// LevelPlayerWeaponManager.HandleWeaponSwitch, which runs in FixedUpdate (LevelPlayerWeaponManager.cs:348-353)
        /// and would miss or double a press.
        /// </summary>
        private static void WriteSlot2(int slot, BlobController c, Vector2 aim, Vector2 move, bool act)
        {
            int x = ArenaLink.Slot2(slot);
            var input = c.input;
            bool mouse = input.MouseActive;
            Vector2 cursor = mouse ? input.MouseWorld() : (Vector2)c.player.center + aim * Tuning.cursorAimReach;
            link.F(x + ArenaLink.U_CursorX, cursor.x);
            link.F(x + ArenaLink.U_CursorY, cursor.y);
            link.I(x + ArenaLink.U_CursorOn, mouse ? 1 : 0);
            link.I(x + ArenaLink.U_DownHeld, act && move.y < -0.5f ? 1 : 0);
            if (!act) return;
            bool sw = false;
            try
            {
                var rp = c.player.input != null ? c.player.input.actions : null;
                sw = rp != null && rp.GetButtonDown(SwitchWeaponButton);
            }
            catch { }
            // ROUNDS's own Laser key (middle mouse) for the keyboard player.
            if (slot == 0 && Input.GetMouseButtonDown(2)) sw = true;
            if (sw) link.I(x + ArenaLink.U_SwitchPresses, ++switchPresses[slot]);
        }

        private static float pickStartedAt, lastTestAccept;

        // ------------------------------------------------------------------ players follow ROUNDS

        /// <summary>Called from BlobController.Update: put the Cuphead player where ROUNDS's player is, handle death.</summary>
        public static void Follow(BlobController c)
        {
            int q = ArenaLink.RSlot(c.index);
            if (!Synced(c))
            {
                // ROUNDS has not revived and moved its player for this life yet: what it reports is from before (old place,
                // old death). The Cuphead player holds still; the spawn is sent again in case ROUNDS's player did not exist yet.
                seenBlocks[c.index] = link.I(q + ArenaLink.Q_Blocks);
                if (Time.unscaledTime - spawnSentAt[c.index] > 0.5f) SendSpawn(c);
                return;
            }
            Vector2 centre = new Vector2(link.F(q + ArenaLink.Q_X), link.F(q + ArenaLink.Q_Y));
            Vector3 offset = (Vector3)c.player.center - c.player.transform.position;
            bool dead = link.I(q + ArenaLink.Q_Dead) != 0;
            if (!c.player.IsDead && !dead)
            {
                c.player.transform.position = new Vector3(centre.x - offset.x, centre.y - offset.y, c.player.transform.position.z);
                CheckPit(c, centre);
            }
            // ROUNDS's player died: Cuphead's player dies too (ghost in co-op, game over alone).
            if (dead && !c.player.IsDead) OnRoundsDeath(c);
            int held = link.I(q + ArenaLink.Q_Cards), owned = Progress.Owned(c.index).Length;
            if (owned > 0 && held >= owned)
                TestMode.Report("cards-applied", true, "ROUNDS's player " + (c.index + 1) + " holds " + held + " cards, applied by ROUNDS itself (ApplyCardStats): " + string.Join(", ", Progress.Owned(c.index)));
            // The ROUNDS block parries pink things the Cuphead way.
            int blocks = link.I(q + ArenaLink.Q_Blocks);
            if (blocks != seenBlocks[c.index])
            {
                seenBlocks[c.index] = blocks;
                StartParry(c);
            }
        }

        public static bool Blocking(BlobController c) => link.I(ArenaLink.RSlot(c.index) + ArenaLink.Q_Blocking) != 0;

        /// <summary>
        /// ROUNDS's player died in this life. Cuphead's player only dies while the level is being played: during the intro a
        /// run-and-gun level has no timeline yet (its game over card would break), and once the level is ending a death means
        /// nothing, so ROUNDS's player is brought back instead.
        /// </summary>
        private static void OnRoundsDeath(BlobController c)
        {
            Level lv = Level.Current;
            if (lv != null && lv.Started && !lv.Ending) KillCuphead(c);
            else Spawn(c);
        }

        /// <summary>Cuphead's own last hit (PlayerStatsManager.TakeDamage at one heart): its invincibility and level-end rules,
        /// then the death event (ghost in co-op, game over alone). Tried again next frame while Cuphead refuses.</summary>
        private static void KillCuphead(BlobController c)
        {
            var st = c.player.stats;
            st.SetHealth(1);
            Reflect.Call(st, "TakeDamage");
            if (c.player.IsDead || st.Health <= 0) Plugin.L.LogInfo("arena: ROUNDS player " + (c.index + 1) + " died");
        }

        /// <summary>A new Cuphead life for this player: ROUNDS revives its player at Cuphead's spot, and nothing ROUNDS reports
        /// for the slot counts until it echoes the life number back.</summary>
        private static void Spawn(BlobController c)
        {
            life[c.index]++;
            SendSpawn(c);
        }

        private static void SendSpawn(BlobController c)
        {
            spawnSentAt[c.index] = Time.unscaledTime;
            link.PostEvent(ArenaLink.EvSpawn, c.index, c.player.center.x, c.player.center.y, life: life[c.index]);
        }

        private static void StartParry(BlobController c)
        {
            var m = c.player.motor;
            if (m == null || c.player.IsDead || m.Parrying) return;
            object parryManager = Reflect.Get(m, "parryManager");
            Reflect.Set(Reflect.Get(m, "hitManager"), "state", "Inactive");
            Reflect.Set(parryManager, "state", "NotReady");
            Reflect.Set(m, "Parrying", true);
            Reflect.Raise(m, "OnParryEvent");
            c.StartCoroutine(EndParry(c));
        }

        private static System.Collections.IEnumerator EndParry(BlobController c)
        {
            yield return new WaitForSeconds(0.35f);
            if (c == null || c.player == null) yield break;
            var m = c.player.motor;
            Reflect.Set(m, "Parrying", false);
            Reflect.Set(Reflect.Get(m, "parryManager"), "state", "Ready");
        }

        /// <summary>A new level (or a retry): ROUNDS's player comes back to full health at Cuphead's spawn point.</summary>
        public static void OnPlayerSpawned(BlobController c)
        {
            if (link == null) return;
            Spawn(c);
            SyncCards(c.index);
        }

        public static void OnCupheadRevived(BlobController c)
        {
            if (link == null) return;
            Spawn(c);
        }

        /// <summary>Damage Cuphead dealt to its player goes to ROUNDS's health instead. Returns false to cancel Cuphead's own damage.</summary>
        public static bool OnCupheadDamage(BlobController c, DamageDealer.DamageInfo info)
        {
            if (info.damageSource == DamageDealer.DamageSource.Pit)
            {
                link.PostEvent(ArenaLink.EvPit, c.index, TestMode.Immune ? 0f : Tuning.pitDamage);
                return false;
            }
            if (Blocking(c))
            {
                // Still costs nothing, but ROUNDS sees the blocked hit (PCE Masochist records it in its HealthHandler
                // prefixes). Contact damage repeats every frame, so it is reported at most every blockedHitCooldown.
                if (!TestMode.Immune && Time.time - blockedHitAt[c.index] >= Tuning.blockedHitCooldown)
                {
                    blockedHitAt[c.index] = Time.time;
                    Vector2 bd = (Vector2)c.player.center - info.origin;
                    if (info.origin == Vector2.zero || bd.sqrMagnitude < 1f) bd = Vector2.up;
                    link.PostEvent(ArenaLink.EvBlockedHit, c.index, Tuning.healthPerHeart, bd.x, bd.y);
                }
                return false;
            }
            if (TestMode.Immune || Time.time < hitUntil[c.index]) return false;
            hitUntil[c.index] = Time.time + Tuning.arenaHitCooldown;
            Vector2 dir = (Vector2)c.player.center - info.origin;
            if (info.origin == Vector2.zero || dir.sqrMagnitude < 1f) dir = Vector2.up;
            link.PostEvent(ArenaLink.EvDamage, c.index, Tuning.healthPerHeart, dir.x, dir.y, Tuning.hitKnockback);
            return false;
        }

        // ------------------------------------------------------------------ cards

        private static readonly int[] cardsFor = new int[2];
        private static readonly float[] cardsTry = new float[2];

        /// <summary>Sends the save's cards for a player (level start, pick, new ROUNDS); resent until ROUNDS confirms it.</summary>
        public static void SyncCards(int slot)
        {
            cardsFor[slot] = -1;
            cardsTry[slot] = 0f;
            TrySyncCards(slot);
        }

        private static void TrySyncCards(int slot)
        {
            if (!Plugin.Engine.Connected || Time.unscaledTime < cardsTry[slot]) return;
            cardsTry[slot] = Time.unscaledTime + 1f;
            int pid = RoundsPid;
            string[] deck = Progress.Owned(slot);
            Plugin.Engine.ArenaCards(slot, deck, r =>
            {
                if (!r.ok) return;
                cardsFor[slot] = pid;
                Plugin.L.LogInfo("arena: player " + (slot + 1) + "'s deck sent to ROUNDS: " + (deck.Length == 0 ? "empty" : string.Join(", ", deck)));
            });
        }

        /// <summary>Starts ROUNDS's own card pick screen for a player; done() gets the picked card ids.</summary>
        public static void RequestPick(int slot, int count)
        {
            pickSlot = slot;
            pickStartedAt = Time.unscaledTime;
            lastTestAccept = Time.unscaledTime;
            SyncCards(slot);
            link.I(ArenaLink.C_Pick + ArenaLink.P_Slot, slot);
            link.I(ArenaLink.C_Pick + ArenaLink.P_Count, count);
            link.I(ArenaLink.C_Pick + ArenaLink.P_Serial, ++pickSerial);
        }

        /// <summary>The pick finished: the cards ROUNDS gave, else null while it is still running.</summary>
        public static string[] PickResult()
        {
            if (link.I(R + ArenaLink.R_PickDone) != pickSerial) return null;
            int n = Mathf.Min(link.I(R + ArenaLink.R_PickedCount), ArenaLink.MaxPicked);
            var ids = new string[n];
            for (int i = 0; i < n; i++) ids[i] = link.Str(R + ArenaLink.R_Picked + i * ArenaLink.PickedSize, ArenaLink.PickedSize);
            pickSlot = -1;
            return ids;
        }

        /// <summary>Gives the pick up on both sides: a request for no cards (count 0) tells ROUNDS to drop a pick it has not
        /// started and to end one it is running, so a stale pick never comes up later over a level.</summary>
        public static void CancelPick()
        {
            if (pickSlot < 0) return;
            link.I(ArenaLink.C_Pick + ArenaLink.P_Slot, pickSlot);
            link.I(ArenaLink.C_Pick + ArenaLink.P_Count, 0);
            link.I(ArenaLink.C_Pick + ArenaLink.P_Serial, ++pickSerial);
            pickSlot = -1;
        }

        public static float PickRunningFor => Time.unscaledTime - pickStartedAt;

        /// <summary>ROUNDS has taken the pick up (its pick screen is running).</summary>
        public static bool EnginePicking => link != null && link.I(R + ArenaLink.R_State) == ArenaLink.StatePicking;

        // ------------------------------------------------------------------ level shapes

        /// <summary>
        /// Every 0.5 s: new colliders on Cuphead's wall (18, 19) and floor (20) layers and enemy hit areas join the tracked
        /// list (destroyed ones leave it first). FindObjectsOfType sees active objects only, so a deactivated one stays
        /// tracked (and leaves the table after colliderEvictSeconds, <see cref="WriteCols"/>). The list holds MaxTracked; a
        /// full list makes room by dropping its longest-off entries, logged once per level when nothing can go.
        /// </summary>
        private static void Scan()
        {
            cols.RemoveAll(c => c.col == null);
            var known = new HashSet<int>();
            foreach (var c in cols) known.Add(c.id);
            foreach (var col in Object.FindObjectsOfType<Collider2D>())
            {
                int id = col.GetInstanceID();
                if (known.Contains(id)) continue;
                int layer = col.gameObject.layer;
                int kind = -1;
                if (layer == 18 || layer == 19) kind = ArenaLink.KindSolid;
                else if (layer == 20)
                {
                    var lp = col.GetComponent<LevelPlatform>() ?? col.GetComponentInParent<LevelPlatform>();
                    kind = lp != null && lp.canFallThrough ? ArenaLink.KindPlatform : ArenaLink.KindGround;
                }
                else
                {
                    var recv = col.GetComponent<DamageReceiver>();
                    if (recv == null) { var ch = col.GetComponent<DamageReceiverChild>(); if (ch != null) recv = ch.Receiver; }
                    if (recv != null && recv.type == DamageReceiver.Type.Enemy)
                    {
                        kind = ArenaLink.KindTarget;
                        targets[id] = recv;
                        // One group per receiver (a DamageReceiverChild's colliders join its Receiver's group).
                        groups[recv.GetInstanceID()] = recv;
                        EnemyStatus.NoteCollider(recv, col);
                    }
                }
                if (kind < 0) continue;
                if (cols.Count >= MaxTracked && !DropLongestOff())
                {
                    if (!trackCapLogged) { trackCapLogged = true; Plugin.L.LogWarning("arena: " + MaxTracked + " colliders tracked and all live; new ones are left out of ROUNDS"); }
                    break;
                }
                cols.Add(new Col { col = col, id = id, kind = kind, on = true, offSince = -1f });
            }
        }

        private static bool DropLongestOff()
        {
            int best = -1;
            for (int i = 0; i < cols.Count; i++)
                if (cols[i].offSince >= 0f && (best < 0 || cols[i].offSince < cols[best].offSince)) best = i;
            if (best < 0) return false;
            cols.RemoveAt(best);
            return true;
        }

        /// <summary>Cuphead keeps players inside Level.Left/Right/Ceiling by numbers, not colliders: ROUNDS gets them as walls.</summary>
        private static int WriteBounds()
        {
            var lv = Level.Current;
            if (lv == null) return 0;
            float l = lv.Left, r = lv.Right, top = lv.Ceiling, bottom = lv.Ground;
            float midY = (top + bottom) / 2f, height = Mathf.Abs(top - bottom) + 4000f, width = Mathf.Abs(r - l) + 4000f;
            BoundBox(0, -1, l - 200f, midY, 400f, height);
            BoundBox(1, -2, r + 200f, midY, 400f, height);
            BoundBox(2, -3, (l + r) / 2f, top + 200f, width, 400f);
            return BoundCount;
        }

        private static void BoundBox(int n, int id, float x, float y, float w, float h)
        {
            int e = ArenaLink.C_Cols + n * ArenaLink.ColSize;
            int d = e + ArenaLink.K_Data;
            link.I(e + ArenaLink.K_Id, id);
            link.I(e + ArenaLink.K_Kind, ArenaLink.KindSolid);
            link.I(e + ArenaLink.K_Shape, ArenaLink.ShapeBox);
            link.F(d, x); link.F(d + 4, y); link.F(d + 8, w); link.F(d + 12, h); link.F(d + 16, 0f);
            link.I(e + ArenaLink.K_Group, 0);
            link.I(e + ArenaLink.K_Flags, 0);
        }

        /// <summary>K_Group and K_Flags of a collider entry (0 for anything that is not an enemy hit area): the receiver's
        /// instance id, and Light/Boss (per receiver) plus NoStatus (every frame) from <see cref="EnemyStatus.Flags"/>.</summary>
        private static void WriteGroup(int e, Col c)
        {
            int grp = 0, flags = 0;
            DamageReceiver tr;
            if (c.kind == ArenaLink.KindTarget && targets.TryGetValue(c.id, out tr) && tr != null)
            {
                grp = tr.GetInstanceID();
                try { flags = EnemyStatus.Flags(tr, c.col); }
                catch (System.Exception ex) { EnemyStatus.LogOnce("target flags", ex); flags = ArenaLink.TfNoStatus; }
            }
            link.I(e + ArenaLink.K_Group, grp);
            link.I(e + ArenaLink.K_Flags, flags);
        }

        private static readonly float[] pitAt = new float[2];

        /// <summary>Below the level's floor line: Cuphead's pit (bounce back up with a hit, ROUNDS's out-of-bounds way).</summary>
        private static void CheckPit(BlobController c, Vector2 centre)
        {
            var lv = Level.Current;
            if (lv == null || centre.y > lv.Ground - 120f || Time.time - pitAt[c.index] < 0.6f) return;
            pitAt[c.index] = Time.time;
            link.PostEvent(ArenaLink.EvPit, c.index, TestMode.Immune ? 0f : Tuning.pitDamage);
        }

        /// <summary>
        /// The collider table: the level bounds, then at most MaxCols - BoundCount tracked colliders (the old cap of MaxCols
        /// left the last three tracked ones unsent). Live colliders get their shape, ones that went off recently ShapeOff,
        /// and ones off for colliderEvictSeconds are left out (ROUNDS removes the mirror, Arena.SyncMirrors). Everything in
        /// list order while it fits, so entries keep their slots; past the cap live ones go first, so only off ones are
        /// dropped (logged once per level).
        /// </summary>
        private static void WriteCols()
        {
            int n = WriteBounds(), cap = ArenaLink.MaxCols - n, live = 0, recent = 0;
            float now = Time.unscaledTime;
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                if (c.col == null) continue;
                var col = c.col;
                c.on = col.enabled && col.gameObject.activeInHierarchy && (c.kind != ArenaLink.KindTarget || TargetAlive(c.id));
                if (c.on) c.offSince = -1f; else if (c.offSince < 0f) c.offSince = now;
                cols[i] = c;
                if (c.on) live++; else if (now - c.offSince < Tuning.colliderEvictSeconds) recent++;
            }
            bool ordered = live + recent > cap;
            if (ordered && !colCapLogged)
            {
                colCapLogged = true;
                Plugin.L.LogWarning("arena: collider table full (" + live + " live and " + recent + " recently off for " + cap + " entries): " + (live > cap ? "some live colliders are left out of ROUNDS" : "recently off ones are dropped first"));
            }
            for (int pass = 0; pass < (ordered ? 2 : 1); pass++)
                for (int i = 0; i < cols.Count && n < ArenaLink.MaxCols; i++)
                {
                    var c = cols[i];
                    if (c.col == null || (!c.on && now - c.offSince >= Tuning.colliderEvictSeconds)) continue;
                    if (ordered && c.on != (pass == 0)) continue;
                    WriteCol(ArenaLink.C_Cols + n++ * ArenaLink.ColSize, c);
                }
            link.I(ArenaLink.C_ColCount, n);
        }

        private static void WriteCol(int e, Col c)
        {
            link.I(e + ArenaLink.K_Id, c.id);
            link.I(e + ArenaLink.K_Kind, c.kind);
            WriteGroup(e, c);
            int d = e + ArenaLink.K_Data;
            var col = c.col;
            if (!c.on)
            {
                link.I(e + ArenaLink.K_Shape, ArenaLink.ShapeOff);
                return;
            }
            Transform t = col.transform;
            var box = col as BoxCollider2D;
            var circle = col as CircleCollider2D;
            var poly = col as PolygonCollider2D;
            if (box != null)
            {
                Vector2 centre = t.TransformPoint(box.offset);
                Vector3 ls = t.lossyScale;
                link.I(e + ArenaLink.K_Shape, ArenaLink.ShapeBox);
                link.F(d, centre.x); link.F(d + 4, centre.y);
                link.F(d + 8, Mathf.Abs(box.size.x * ls.x)); link.F(d + 12, Mathf.Abs(box.size.y * ls.y));
                link.F(d + 16, t.eulerAngles.z);
            }
            else if (circle != null)
            {
                Vector2 centre = t.TransformPoint(circle.offset);
                Vector3 ls = t.lossyScale;
                link.I(e + ArenaLink.K_Shape, ArenaLink.ShapeCircle);
                link.F(d, centre.x); link.F(d + 4, centre.y);
                link.F(d + 8, circle.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y)));
            }
            else if (poly != null && poly.pathCount > 0 && poly.GetPath(0).Length >= 3 && poly.GetPath(0).Length <= 16)
            {
                var pts = poly.GetPath(0);
                link.I(e + ArenaLink.K_Shape, ArenaLink.ShapePoly);
                link.I(e + ArenaLink.K_Points, pts.Length);
                for (int k = 0; k < pts.Length; k++)
                {
                    Vector2 w = t.TransformPoint(pts[k] + poly.offset);
                    link.F(d + k * 8, w.x); link.F(d + k * 8 + 4, w.y);
                }
            }
            else
            {
                Bounds b = col.bounds;
                link.I(e + ArenaLink.K_Shape, ArenaLink.ShapeBox);
                link.F(d, b.center.x); link.F(d + 4, b.center.y);
                link.F(d + 8, b.size.x); link.F(d + 12, b.size.y);
                link.F(d + 16, 0f);
            }
        }

        private static bool TargetAlive(int id)
        {
            DamageReceiver r;
            return targets.TryGetValue(id, out r) && r != null && r.enabled && r.gameObject.activeInHierarchy;
        }

        // ------------------------------------------------------------------ ROUNDS bullets hit Cuphead enemies

        /// <summary>
        /// Hits on Cuphead enemies. Every kind (bullet, DoT tick, area, continuous, contact, aura) is a plain Neutral
        /// TakeDamage, never DamageSource.Super, which would hit-pause the enemy and shake the camera
        /// (DamageReceiver.cs:56-58). An entry without damage only carries its status and never calls TakeDamage; a DoT
        /// tick also pulses the poison tint before the status row arrives. A receiver that is not active in the hierarchy
        /// takes nothing (DamageReceiver.TakeDamage only checks enabled, DamageReceiver.cs:49): a dead, pooled or hidden
        /// enemy gets no leftover poison ticks for its next life. Entries are taken as <see cref="RingEntry"/> says.
        /// </summary>
        private static void ReadHits()
        {
            int write = link.I(R + ArenaLink.R_HitWrite);
            if (write < hitRead) hitRead = write; // a ROUNDS that started over counts from zero
            if (write - hitRead > ArenaLink.HitRing) hitRead = write - ArenaLink.HitRing + 1; // lapped: leave the slot ROUNDS writes next
            while (hitRead < write)
            {
                int h = R + ArenaLink.R_Hits + (hitRead % ArenaLink.HitRing) * ArenaLink.HitSize;
                int s = RingEntry(h + ArenaLink.H_Serial, hitRead, ref hitStuck, ref hitStuckAt);
                if (s < 0) break;
                hitRead++;
                if (s == 0) continue;
                Thread.MemoryBarrier();
                int id = link.I(h + ArenaLink.H_Target);
                float dmg = link.F(h + ArenaLink.H_Damage) * Tuning.damageScale;
                var pos = new Vector2(link.F(h + ArenaLink.H_X), link.F(h + ArenaLink.H_Y));
                int slot = link.I(h + ArenaLink.H_Slot);
                int kind = link.I(h + ArenaLink.H_Kind), rgba = link.I(h + ArenaLink.H_Color);
                Thread.MemoryBarrier();
                if (link.I(h + ArenaLink.H_Serial) != s) continue; // overwritten while copied
                DamageReceiver r;
                if (!targets.TryGetValue(id, out r) || r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                try
                {
                    if (dmg > 0f && !float.IsNaN(dmg) && !float.IsInfinity(dmg))
                    {
                        r.TakeDamage(new DamageDealer.DamageInfo(dmg, DamageDealer.Direction.Neutral, pos, DamageDealer.DamageSource.Neutral));
                        TestMode.OnBlobHit(slot, dmg, r);
                    }
                }
                catch (System.Exception e) { Plugin.L.LogWarning("arena: hit on target " + id + " failed: " + e.Message); }
                if (kind == ArenaLink.HitDot && rgba != 0)
                {
                    try { EnemyStatus.For(r).DotTick(rgba); }
                    catch (System.Exception e) { EnemyStatus.LogOnce("dot tint", e); }
                }
            }
        }

        private static int hitStuck = -1, fxStuck = -1;
        private static float hitStuckAt, fxStuckAt;

        /// <summary>
        /// One ring entry (hits, effects) by its serial, which PostHit/PostFx write last: 0 while the entry is written, then
        /// its index + 1 (ArenaLink.cs:140-171). Returns the serial when it is index + 1 (copy the fields, then check the
        /// serial is unchanged); 0 when it is newer (the entry was overwritten by a lap: skip it); -1 when it is 0 or older
        /// (not written yet: stop for this frame). An entry that stays unwritten for a second although the counter passed it
        /// (a ROUNDS that died mid-write or started over) is skipped, so a ring can never stall.
        /// </summary>
        private static int RingEntry(int serialAt, int index, ref int stuck, ref float stuckAt)
        {
            int s = link.I(serialAt);
            if (s == index + 1) { stuck = -1; return s; }
            if (s > index + 1) { stuck = -1; return 0; }
            if (stuck != index) { stuck = index; stuckAt = Time.unscaledTime; return -1; }
            if (Time.unscaledTime - stuckAt < 1f) return -1;
            stuck = -1;
            return 0;
        }

        // ------------------------------------------------------------------ ROUNDS statuses on Cuphead enemies

        /// <summary>A new level: statuses start clean, and the level's phase-change and end events drive EnemyStatus.</summary>
        private static void OnLevelChanged(Level lv)
        {
            try
            {
                EnemyStatus.ResetForLevel(lv);
                // The decks as they stand (a won level wiped them): ROUNDS's players take the change between levels, never in
                // the middle of a KO outro.
                for (int i = 0; i < 2; i++) if (Progress.PlayerPresent(i)) SyncCards(i);
                if (lv == null) return;
                lv.OnStateChangedEvent += EnemyStatus.PhaseChanged;   // Level.cs:1378-1385, boss phase change
                lv.OnPreWinEvent += EnemyStatus.SuspendAll;           // KO: death animations play at full speed
                lv.OnWinEvent += EnemyStatus.SuspendAll;
                lv.OnLoseEvent += EnemyStatus.SuspendAll;
                lv.OnLevelEndEvent += EnemyStatus.SuspendAll;
            }
            catch (System.Exception e) { EnemyStatus.LogOnce("level events", e); }
        }

        /// <summary>One-shot status events (coalesced per enemy per type per ROUNDS frame): forces, pulls and Flip act here;
        /// slow, stun, silence, DoT, gold and confuse only show a burst, their state comes from the status table. Entries
        /// are taken as <see cref="RingEntry"/> says. FxMurder still works, though Murder now comes from the save
        /// (EnemyStatus.SpendSavedMurders).</summary>
        private static void ReadFx()
        {
            int write = link.I(R + ArenaLink.R_FxWrite);
            if (write < fxRead) fxRead = write;
            if (write - fxRead > ArenaLink.FxRing) fxRead = write - ArenaLink.FxRing + 1; // lapped: leave the slot ROUNDS writes next
            while (fxRead < write)
            {
                int e = R + ArenaLink.R_Fx + (fxRead % ArenaLink.FxRing) * ArenaLink.FxSize;
                int serial = RingEntry(e + ArenaLink.FX_Serial, fxRead, ref fxStuck, ref fxStuckAt);
                if (serial < 0) break;
                fxRead++;
                if (serial == 0) continue;
                Thread.MemoryBarrier();
                int g = link.I(e + ArenaLink.FX_Target), type = link.I(e + ArenaLink.FX_Type);
                float a = link.F(e + ArenaLink.FX_A), b = link.F(e + ArenaLink.FX_B), c = link.F(e + ArenaLink.FX_C), d = link.F(e + ArenaLink.FX_D);
                Thread.MemoryBarrier();
                if (link.I(e + ArenaLink.FX_Serial) != serial) continue; // overwritten while copied
                try
                {
                    if (type == ArenaLink.FxMurder) { EnemyStatus.Murder((int)a); continue; } // waits for the level to start
                    if (type == ArenaLink.FxShake || EnemyStatus.Suspended) continue;          // ROUNDS's camera shake is not forwarded
                    DamageReceiver r;
                    if (!groups.TryGetValue(g, out r) || r == null || !r.enabled || !r.gameObject.activeInHierarchy || EnemyStatus.Immune(r)) continue;
                    var s = EnemyStatus.For(r);
                    switch (type)
                    {
                        case ArenaLink.FxForce: s.AddPush(new Vector2(a, b)); break;
                        case ArenaLink.FxPull: s.PullTo(new Vector2(a, b), c, d); break;
                        case ArenaLink.FxGravity: s.Gravity(a, b); break;
                        default: s.Pop(type, a); break;
                    }
                }
                catch (System.Exception ex) { EnemyStatus.LogOnce("effect " + type, ex); }
            }
        }

        /// <summary>
        /// The status table, every level frame: rows merge per enemy owner (several receivers can share one). Everything is
        /// cleared when ROUNDS stops rewriting it (R_StatusFrame unchanged for statusStaleSeconds) or is not live, while
        /// Suspended, and for bosses during phase-change immunity. Statuses hold still while the game is paused. The rows
        /// are taken under the table's sequence lock (<see cref="CopyStatus"/>); a frame without a whole copy changes
        /// nothing, so EndFrame never drops a status because of a torn read and every enemy keeps the last good copy.
        /// </summary>
        private static void ReadStatus()
        {
            int frame = link.I(R + ArenaLink.R_StatusFrame);
            bool paused = CupheadTime.IsPaused();
            if (frame != statusFrameSeen || paused) { statusFrameSeen = frame; statusFrameAt = Time.unscaledTime; }
            if (paused) return;
            bool clear = !Live || Time.unscaledTime - statusFrameAt > Tuning.statusStaleSeconds || EnemyStatus.Suspended;
            int n = 0;
            if (!clear && (n = CopyStatus()) < 0) return;
            EnemyStatus.BeginFrame();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var row = statusRows[i];
                    DamageReceiver r;
                    if (!groups.TryGetValue(row.group, out r) || r == null || !r.enabled || !r.gameObject.activeInHierarchy || EnemyStatus.Immune(r)) continue;
                    EnemyStatus.For(r).Merge(row.slow, row.stun, row.silence, row.dot, row.rgba, row.flags, row.aux);
                }
            }
            finally { EnemyStatus.EndFrame(); } // enemies with no row this frame drop their statuses and are restored
        }

        private struct StatusRow { public int group, rgba, flags; public float slow, stun, silence, dot, aux; }
        private static readonly StatusRow[] statusRows = new StatusRow[ArenaLink.MaxStatus];

        /// <summary>
        /// Copies the count and rows into statusRows under the sequence lock (ArenaLink.R_StatusFrame; Arena.WriteEffects
        /// makes it odd while it writes them, even after): the copy counts only when the frame was the same even value
        /// before and after it. Returns the row count, or -1 when no whole copy was had in a few tries (ROUNDS mid-write:
        /// the caller keeps last frame's statuses).
        /// </summary>
        private static int CopyStatus()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.SpinWait(64);
                int f1 = link.I(R + ArenaLink.R_StatusFrame);
                if ((f1 & 1) != 0) continue;
                Thread.MemoryBarrier();
                int n = Mathf.Clamp(link.I(R + ArenaLink.R_StatusCount), 0, ArenaLink.MaxStatus);
                for (int i = 0; i < n; i++)
                {
                    int e = R + ArenaLink.R_Status + i * ArenaLink.StatusSize;
                    statusRows[i] = new StatusRow
                    {
                        group = link.I(e + ArenaLink.T_Group), slow = link.F(e + ArenaLink.T_Slow), stun = link.F(e + ArenaLink.T_Stun),
                        silence = link.F(e + ArenaLink.T_Silence), dot = link.F(e + ArenaLink.T_Dot), rgba = link.I(e + ArenaLink.T_DotRgba),
                        flags = link.I(e + ArenaLink.T_Flags), aux = link.F(e + ArenaLink.T_Aux)
                    };
                }
                Thread.MemoryBarrier();
                if (link.I(R + ArenaLink.R_StatusFrame) == f1) return n;
            }
            return -1;
        }

        /// <summary>Some enemy can take statuses now: a live receiver outside its no-status window (intro, KO, level end, boss
        /// phase-change immunity, dead minion), as the engine's AnyEnemyTakesStatus looked for on its side.</summary>
        public static bool AnyTargetTakesStatus()
        {
            foreach (var r in groups.Values)
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && !EnemyStatus.NoStatusNow(r)) return true;
            return false;
        }

        private static void HideCupheadHud()
        {
            // ROUNDS shows health above each player and the cards in its card bar; Cuphead's hearts and super cards go.
            // Shrunk to nothing rather than switched off: Cuphead still starts its coroutines on them.
            foreach (var hp in Object.FindObjectsOfType<LevelHUDPlayerHealth>()) hp.transform.localScale = Vector3.zero;
            foreach (var sp in Object.FindObjectsOfType<LevelHUDPlayerSuper>()) sp.transform.localScale = Vector3.zero;
            hudHidden = true;
        }

        public static void Close()
        {
            if (link == null) return;
            try { link.I(ArenaLink.C_Mode, 0); link.I(ArenaLink.C_Magic, 0); } catch { }
            link.Dispose();
            link = null;
        }
    }

    /// <summary>
    /// Draws ROUNDS's two pictures inside Cuphead, lined up with the camera they were rendered for: the world picture
    /// (players, bullets, effects) on Cuphead's "Player" sorting layer, so the level's foreground covers the ROUNDS player
    /// the way it covers Cuphead's own (LevelPlayerAnimationController.cs:1003), and the UI picture (card bar) over
    /// everything. Full screen (both, UI last) while picking.
    /// </summary>
    public class ArenaLayer : MonoBehaviour
    {
        private Texture2D tex, texUi;
        private Sprite sprite, spriteUi;
        private SpriteRenderer sr, srUi;
        private int seen = -1;
        private int w, h;
        private float frameOrtho, frameX, frameY;
        private bool fullScreen;
        private float lastFrameAt = -100f;

        private void Awake()
        {
            var go = new GameObject("ROUNDS layer");
            DontDestroyOnLoad(go);
            sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 2; // just over the (hidden) Cuphead players, which sit at 1 and -1 on the Player layer
            var ugo = new GameObject("ROUNDS UI layer");
            DontDestroyOnLoad(ugo);
            srUi = ugo.AddComponent<SpriteRenderer>();
            srUi.sortingOrder = 32000;
        }

        private static int playerLayerId;
        private static bool playerLayerKnown;

        /// <summary>Cuphead's "Player" sorting layer (Default, with a warning, should a build lack it).</summary>
        private static int PlayerSortingLayer()
        {
            if (playerLayerKnown) return playerLayerId;
            playerLayerKnown = true;
            bool found = false;
            foreach (var l in SortingLayer.layers) if (l.name == "Player") { playerLayerId = l.id; found = true; }
            if (!found) Plugin.L.LogWarning("arena: Cuphead has no 'Player' sorting layer; the ROUNDS layer draws on Default");
            return playerLayerId;
        }

        private void LateUpdate()
        {
            var link = ArenaClient.link;
            if (link == null) return;
            if (ArenaClient.Live && ArenaClient.Mode != 0) TakeFrame(link);
            bool fresh = Time.unscaledTime - lastFrameAt < 0.5f && ArenaClient.Live;
            fullScreen = ArenaClient.Mode == 2;
            bool world = fresh && ArenaClient.Mode == 1 && tex != null;
            sr.enabled = world;
            srUi.enabled = world && texUi != null;
            if (world)
            {
                Camera cam = CupheadLevelCamera.Current != null ? CupheadLevelCamera.Current.camera : Camera.main;
                float height = frameOrtho * 2f;
                float s = height / (h / 100f);
                var pos = new Vector3(frameX, frameY, cam != null ? cam.transform.position.z + 50f : 0f);
                int layer = cam != null ? FirstLayer(cam.cullingMask) : 0;
                sr.transform.position = pos;
                sr.transform.localScale = new Vector3(s, s, 1f);
                sr.gameObject.layer = layer;
                sr.sortingLayerID = PlayerSortingLayer();
                srUi.transform.position = pos;
                srUi.transform.localScale = new Vector3(s, s, 1f);
                srUi.gameObject.layer = layer;
                var layers = SortingLayer.layers;
                if (layers.Length > 0) srUi.sortingLayerID = layers[layers.Length - 1].id;
            }
        }

        /// <summary>
        /// The newest ROUNDS picture with the camera it was rendered for. Arena.Publish fills a buffer that is neither
        /// R_Ready nor C_Reading, then writes the camera, ortho and size (one global record), then R_Ready, then
        /// R_FrameSerial (Arena.cs:1186-1217). So the serial is read first, then R_Ready, C_Reading and the metadata, and the
        /// serial again before the copy: unchanged means the metadata belongs to that buffer and no publish since could have
        /// picked it, as every later one sees it in C_Reading. Changed: read once more. After the copy the serial is checked
        /// again; ROUNDS back on the same buffer means the copy may be torn, so it is not applied (LoadRawTextureData fills
        /// only the CPU side until Apply, so the previous frame stays on screen) and is taken once more.
        /// </summary>
        private void TakeFrame(ArenaLink link)
        {
            const int R = ArenaLink.ResultOff;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int serial = link.I(R + ArenaLink.R_FrameSerial);
                if (serial == seen) return;
                Thread.MemoryBarrier();
                int ready = link.I(R + ArenaLink.R_Ready);
                link.I(ArenaLink.C_Reading, ready);
                int fw = link.I(R + ArenaLink.R_FrameW), fh = link.I(R + ArenaLink.R_FrameH);
                float fx = link.F(R + ArenaLink.R_FrameCamX), fy = link.F(R + ArenaLink.R_FrameCamY), fo = link.F(R + ArenaLink.R_FrameOrtho);
                Thread.MemoryBarrier();
                if (link.I(R + ArenaLink.R_FrameSerial) != serial) continue; // a newer frame landed meanwhile
                seen = serial;
                if (ready < 0 || ready > 2 || fw <= 0 || fh <= 0 || fw > ArenaLink.MaxW || fh > ArenaLink.MaxH) return;
                if (tex == null || texUi == null || fw != w || fh != h)
                {
                    w = fw; h = fh;
                    if (tex != null) Destroy(tex);
                    if (texUi != null) Destroy(texUi);
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    texUi = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
                    spriteUi = Sprite.Create(texUi, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
                    sr.sprite = sprite;
                    srUi.sprite = spriteUi;
                }
                tex.LoadRawTextureData(link.Frame(ready), w * h * 4);
                texUi.LoadRawTextureData(link.UiFrame(ready), w * h * 4);
                Thread.MemoryBarrier();
                if (link.I(R + ArenaLink.R_FrameSerial) != serial && link.I(R + ArenaLink.R_Ready) == ready) { seen = -1; continue; }
                tex.Apply(false);
                texUi.Apply(false);
                frameX = fx; frameY = fy; frameOrtho = fo;
                lastFrameAt = Time.unscaledTime;
                return;
            }
        }

        private static int FirstLayer(int mask)
        {
            if ((mask & 1) != 0) return 0;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        private GUIStyle note;

        private void OnGUI()
        {
            if (ArenaClient.WaitingAny)
            {
                if (note == null)
                {
                    note = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                    note.normal.textColor = Color.white;
                }
                float ks = Screen.height / 1080f;
                note.fontSize = Mathf.RoundToInt(28 * ks);
                GUI.Label(new Rect(0, Screen.height * 0.4f, Screen.width, 50 * ks), "ROUNDS is starting... your ROUNDS player drops in as soon as it is ready", note);
            }
            if (!fullScreen || tex == null || !ArenaClient.Live) return;
            GUI.depth = -1000;
            float sw = Screen.width, sh = Screen.height;
            GUI.color = new Color(0.118f, 0.118f, 0.13f, 1f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), Gfx.White);
            GUI.color = Color.white;
            float k = Mathf.Min(sw / w, sh / h);
            float dw = w * k, dh = h * k;
            GUI.DrawTexture(new Rect((sw - dw) / 2f, (sh - dh) / 2f, dw, dh), tex, ScaleMode.StretchToFill, true);
            if (texUi != null) GUI.DrawTexture(new Rect((sw - dw) / 2f, (sh - dh) / 2f, dw, dh), texUi, ScaleMode.StretchToFill, true);
        }
    }
}
