using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Melty test runs: while %LOCALAPPDATA%\Melty\test\roundhead.json exists, skip the menus, give player 1 three
    /// ROUNDS cards picked by the running ROUNDS, drop into The Root Pack (Botanic Panic) and let a bot aim, shoot and
    /// block. Each check is appended as "pass name" / "fail name: why". Cuphead's own save is never written.
    /// </summary>
    public static class TestMode
    {
        public const string Slug = "roundhead";

        [Serializable]
        private class TestFile
        {
            public string testId = "";
            public int run;
            public int runs;
            public int seed;
            public string checks = "";
        }

        public static bool Active { get; private set; }
        public static int Seed { get; private set; }
        private static string checksPath;
        private static readonly HashSet<string> reported = new HashSet<string>();
        private enum Step { WaitTitle, LoadingData, PickCards, OnMap, LoadLevel, InLevel }
        private static Step step = Step.WaitTitle;
        private static float stepAt;
        private static float lastBlock;
        private static float startedAt;
        private static float mapDoneAt = -1f;

        public static void Init()
        {
            try
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                string p = Path.Combine(Path.Combine(Path.Combine(local, "Melty"), "test"), Slug + ".json");
                if (!File.Exists(p)) return;
                var t = Json.Read<TestFile>(File.ReadAllText(p));
                Active = true;
                Seed = t.seed != 0 ? t.seed : 1234;
                checksPath = t.checks;
                startedAt = Time.realtimeSinceStartup;
                // Cuphead pauses whenever its window is not in front; a test run plays itself while the creator uses the PC.
                Application.runInBackground = true;
                UnityEngine.Random.InitState(Seed);
                Plugin.L.LogInfo("Melty test run " + t.run + "/" + t.runs + " (seed " + Seed + "): going straight to a boss fight");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("could not read Melty test file: " + e.Message);
            }
        }

        public static void Report(string check, bool pass, string why)
        {
            if (!Active || string.IsNullOrEmpty(checksPath) || reported.Contains(check)) return;
            reported.Add(check);
            string line = (pass ? "pass " : "fail ") + check + (pass ? "" : ": " + why);
            try
            {
                File.AppendAllText(checksPath, line + Environment.NewLine);
                Plugin.L.LogInfo("test check: " + line + " (" + why + ")");
            }
            catch (Exception e) { Plugin.L.LogWarning("could not write test check: " + e.Message); }
        }

        public static void OnBlobSpawned()
        {
            if (!Active) return;
            int n = 0;
            foreach (var b in BlobController.All) if (b != null) n++;
            if (n >= 2) Report("coop-blobs", true, "both co-op players are ROUNDS blobs with their own builds");
        }

        public static void OnBlobHit(int player, float damage, DamageReceiver receiver)
        {
            if (!Active || receiver == null || receiver.type != DamageReceiver.Type.Enemy) return;
            Report("blob-hits-boss", true, "a ROUNDS bullet hit " + receiver.gameObject.name + " for " + damage.ToString("0.0"));
        }

        /// <summary>While the status bench measures the boss, the bots neither shoot nor block: their cards (Shield Charge,
        /// Dazzle, knockback...) would put statuses and flinches of their own on it, and their fire would kill it mid-bench.</summary>
        private static bool benchQuiet;

        public static bool BotShoot(int player) => Active && !benchQuiet && player == 0 && step == Step.InLevel && Level.Current != null && Level.Current.Started;

        /// <summary>The bot strolls back and forth so the recording shows ROUNDS running.</summary>
        public static bool BotMove(int player, out float dir)
        {
            dir = 0f;
            if (!BotShoot(player) && !(Active && player == 1 && step == Step.InLevel)) return false;
            float t = Time.time + player * 1.3f;
            float phase = Mathf.Repeat(t, 6f);
            dir = phase < 1.6f ? 1f : phase < 3f ? 0f : phase < 4.6f ? -1f : 0f;
            return true;
        }

        /// <summary>The bot jumps every few seconds (a held jump, then a tap).</summary>
        public static bool BotJump(int player, out bool held)
        {
            held = false;
            if (!(Active && step == Step.InLevel && Level.Current != null && Level.Current.Started)) return false;
            float t = Mathf.Repeat(Time.time + player * 0.9f, 3.5f);
            held = t < 0.25f;
            return true;
        }

        public static bool BotBlock(int player)
        {
            if (!BotShoot(player) || Time.time - lastBlock < 5f) return false;
            lastBlock = Time.time;
            return true;
        }

        public static bool BotAim(int player, Vector2 from, out Vector2 dir)
        {
            dir = Vector2.right;
            if (!BotShoot(player)) return false;
            DamageReceiver t = RoundsBullet.Nearest(from);
            if (t == null) return false;
            Vector2 to = (Vector2)t.transform.position - from;
            to.y += 40f;
            dir = to.sqrMagnitude > 1f ? to.normalized : Vector2.right;
            return true;
        }

        /// <summary>Test runs start the blob a little way into the arena so the recording shows it clearly.</summary>
        public static void PlaceBot(BlobController c)
        {
            if (!Active || c.index != 0) return;
            c.StartCoroutine(PlaceBotLater(c));
        }

        private static System.Collections.IEnumerator PlaceBotLater(BlobController c)
        {
            yield return new WaitForSeconds(0.5f);
            if (c != null && c.player != null)
                c.player.transform.position += new Vector3(240f, 0f, 0f);
        }

        /// <summary>A Melty test run plays for real: the bots take Cuphead's hits and die. Once every player is dead (Cuphead's
        /// game over) the level is retried after a short look at the card, as a player would, so the recording keeps showing
        /// the fight (the dev retry scenario does its own timing).</summary>
        private const float TestRetryDelay = 6f;
        private static float testDeadSince = -1f;

        /// <summary>Three developer scenarios keep the bots immune to Cuphead's hits: the status bench holds them still and
        /// quiet (benchQuiet) while it measures the boss, so they could neither dodge nor block; the win check needs them alive
        /// until the boss is down; the revive check needs player 2 alive while player 1's staged death is parried. Melty test
        /// runs play for real.</summary>
        public static bool Immune => Active && (Dev == "statuses" || Dev == "win" || Dev == "revive");

        /// <summary>The test level, with the dev cards dealt first. ROUNDHEAD_DEV_SCENE: a Levels name (Platforming_Level_1_1,
        /// Pirate: Cuphead's own scene lookup, the run-and-gun scenes are not named like their enum) or a raw scene name.</summary>
        private static void LoadTestLevel()
        {
            DevCards();
            string scene = Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_SCENE");
            Levels devLevel;
            if (!string.IsNullOrEmpty(scene) && TryLevel(scene, out devLevel)) SceneLoader.LoadLevel(devLevel, SceneLoader.Transition.Fade);
            else if (!string.IsNullOrEmpty(scene)) SceneLoader.LoadScene(scene, SceneLoader.Transition.Fade, SceneLoader.Transition.Iris);
            else SceneLoader.LoadLevel(Levels.Veggies, SceneLoader.Transition.Fade);
        }

        private static bool TryLevel(string name, out Levels level)
        {
            foreach (Levels l in Enum.GetValues(typeof(Levels)))
                if (string.Equals(l.ToString(), name, StringComparison.OrdinalIgnoreCase)) { level = l; return true; }
            level = Levels.Veggies;
            return false;
        }

        private static void TestRetry(float now)
        {
            // Dev scenarios (retry, revive, statuses) run their own sequences: none of them wants this reload.
            if (!Active || Dev.Length != 0 || Level.Current == null || !Level.Current.Started || BlobController.All.Count == 0) return;
            bool allDead = true;
            foreach (var c in BlobController.All) if (c != null && c.player != null && !c.player.IsDead) allDead = false;
            // A level can also be lost with everyone alive (a Mausoleum ghost reaching the urn): the game over is the same.
            bool lost = Level.Current.Ending && !Level.Won;
            if (!allDead && !lost) { testDeadSince = -1f; return; }
            if (testDeadSince < 0f) { testDeadSince = now; return; }
            // The loss pick is made on the game-over card first; the retry follows it by a few seconds.
            if (PickOverlay.I != null && PickOverlay.I.HoldsGameOver()) { testDeadSince = Mathf.Max(testDeadSince, now - TestRetryDelay + 3f); return; }
            if (now - testDeadSince < TestRetryDelay) return;
            testDeadSince = -1f;
            Report("test-retry", true, "every player died; the level is retried " + TestRetryDelay + " s after the game over");
            SceneLoader.ReloadLevel();
        }

        // Developer scenario (ROUNDHEAD_DEV=retry): in the test level, ROUNDS's players die, the level is retried twice,
        // and each time both players must come back alive and driven by ROUNDS within 10 s (the bots take real hits, so one
        // may well be dead again by then). Never runs for players.
        private static readonly string Dev = Environment.GetEnvironmentVariable("ROUNDHEAD_DEV") ?? "";
        private static int devRound, devChecked;
        private static float devAt = -1f, devDeadAt = -1f, devReloadAt = -1f;
        private static Level devLevel;
        private static bool devAliveSeen;

        private static void DevRetry(float now)
        {
            if (Dev != "retry" || Level.Current == null || !Level.Current.Started) return;
            if (Level.Current != devLevel) { devLevel = Level.Current; devAt = now; devDeadAt = -1f; devAliveSeen = false; }
            bool anyDead = false, allDriven = BlobController.All.Count > 0;
            foreach (var c in BlobController.All)
            {
                if (c == null) continue;
                if (c.player.IsDead) anyDead = true;
                if (!c.Arena) allDriven = false;
            }
            if (devRound > 0 && devDeadAt < 0f && allDriven && !anyDead) devAliveSeen = true;
            if (devRound > 0 && devDeadAt < 0f && now - devAt > 10f && devChecked < devRound)
            {
                devChecked = devRound;
                bool ok = devAliveSeen;
                Plugin.L.LogInfo("dev: after retry " + devRound + ": " + (ok ? "OK" : "FAIL") + " (came back alive and driven " + devAliveSeen + "; now driven by ROUNDS " + allDriven + ", any dead " + anyDead + ")");
                Report("dev-retry-" + devRound, ok, "players came back alive and driven by ROUNDS within 10 s of retry " + devRound);
                if (devRound >= 2) return;
            }
            if (devRound >= 2 && now - devAt > 11f) return;
            if (devDeadAt < 0f && now - devAt > 12f && ArenaClient.link != null)
            {
                devDeadAt = now;
                foreach (var c in BlobController.All) if (c != null) ArenaClient.link.PostEvent(ArenaLink.EvKill, c.index);
                Plugin.L.LogInfo("dev: killing the ROUNDS players (round " + (devRound + 1) + ")");
            }
            // The reload waits for the game-over card and the loss picks made on it (they take a few seconds to come up).
            bool cardDone = PickOverlay.I != null && PickOverlay.I.GameOverReady && !PickOverlay.I.HoldsGameOver();
            if (devDeadAt > 0f && devReloadAt < 0f && now - devDeadAt > 5f && (cardDone || now - devDeadAt > 25f))
            {
                devReloadAt = now;
                Plugin.L.LogInfo("dev: players dead " + anyDead + "; retrying the level");
                devRound++;
                devReloadAt = -1f;
                devDeadAt = 1e9f; // until the reloaded level appears
                SceneLoader.ReloadLevel();
            }
        }

        // Developer scenario (ROUNDHEAD_DEV=win): in the test level the players (immune here) get two cards each once ROUNDS
        // drives both (as loss picks would give), ROUNDS must hold them, then the boss is killed (every boss hit area takes
        // lethal damage until the level is won) and the decks must be wiped on Cuphead's side and ROUNDS's (its players hold
        // no cards within 10 s of the win). Never runs for players.
        private static Level devWinLevel;
        private static float devWinAt, devWinArmedAt = -1f, devWinWonAt = -1f, devWinLastHit = -1f;
        private static bool devWinDone;
        private static readonly HashSet<DamageReceiver> devWinHit = new HashSet<DamageReceiver>();

        private static void DevWin(float now)
        {
            if (Dev != "win" || devWinDone || ArenaClient.link == null) return;
            if (devWinWonAt < 0f)
            {
                if (Level.Current == null || !Level.Current.Started) return;
                if (Level.Current != devWinLevel) { devWinLevel = Level.Current; devWinAt = now; devWinArmedAt = -1f; }
                if (Level.Current.Ending && !Level.Won) { Report("dev-win-wipe", false, "the level was lost before the boss went down"); devWinDone = true; return; }
            }
            bool driven = BlobController.All.Count >= 2;
            foreach (var b in BlobController.All) if (b == null || !b.Arena) driven = false;
            if (devWinArmedAt < 0f && (driven || now - devWinAt > 15f))
            {
                devWinArmedAt = now;
                foreach (string id in new[] { "Defender", "Barrage" })
                    for (int p = 0; p < 2; p++) if (Progress.PlayerPresent(p)) Progress.AddCard(p, id);
                Plugin.L.LogInfo("dev: decks dealt for the win check: " + string.Join(", ", Progress.Owned(0)));
                return;
            }
            if (devWinArmedAt < 0f) return;
            int held0 = ArenaClient.link.I(ArenaLink.RSlot(0) + ArenaLink.Q_Cards), held1 = ArenaClient.link.I(ArenaLink.RSlot(1) + ArenaLink.Q_Cards);
            if (devWinWonAt < 0f)
            {
                if (Level.Won) { devWinWonAt = now; Plugin.L.LogInfo("dev: level won with decks of " + Progress.Owned(0).Length + " and " + Progress.Owned(1).Length + " (ROUNDS held " + held0 + " and " + held1 + ")"); return; }
                // ROUNDS must hold the dealt cards before the boss goes down; then every boss hit area takes lethal damage, twice a second.
                if (held0 < Progress.Owned(0).Length) { if (now - devWinArmedAt > 20f) { Report("dev-win-wipe", false, "ROUNDS never held the dealt cards (" + held0 + " of " + Progress.Owned(0).Length + ")"); devWinDone = true; } return; }
                if (now - devWinLastHit < 0.5f) return;
                devWinLastHit = now;
                // One lethal hit per hit area: a second hit on a dying boss restarts its death animation (VeggiesLevelPotato.Die
                // calls StopAllCoroutines and triggers "Dead" again), and it would never finish.
                foreach (var r in UnityEngine.Object.FindObjectsOfType<DamageReceiver>())
                    if (Usable(r) && IsBossReceiver(r) && devWinHit.Add(r))
                        try { r.TakeDamage(new DamageDealer.DamageInfo(9999f, DamageDealer.Direction.Neutral, r.transform.position, DamageDealer.DamageSource.Neutral)); Plugin.L.LogInfo("dev: lethal hit on " + OwnerOf(r).name); } catch (Exception e) { Plugin.L.LogWarning("dev: boss hit: " + e.Message); }
                if (now - devWinArmedAt > 90f) { Report("dev-win-wipe", false, "the level was not won within 90 s of hitting the boss"); devWinDone = true; }
                return;
            }
            bool decksEmpty = Progress.Owned(0).Length == 0 && Progress.Owned(1).Length == 0;
            bool roundsEmpty = held0 == 0 && held1 == 0;
            // The decks are wiped at the win; ROUNDS's players are reset once the fight is over (ArenaClient leaves arena mode),
            // the KO outro left alone.
            if (!decksEmpty) { Report("dev-win-wipe", false, "the win left decks of " + Progress.Owned(0).Length + " and " + Progress.Owned(1).Length + " cards"); devWinDone = true; return; }
            if (roundsEmpty)
            {
                Report("dev-win-wipe", true, "the win wiped both decks (ROUNDS still held its cards through the KO) and ROUNDS's players hold none " + (now - devWinWonAt).ToString("0.0") + " s after the win (arena mode " + ArenaClient.Mode + ")");
                devWinDone = true;
            }
            else if (now - devWinWonAt > 60f)
            {
                Report("dev-win-wipe", false, "60 s after the win ROUNDS still holds " + held0 + "/" + held1 + " cards (arena mode " + ArenaClient.Mode + ")");
                devWinDone = true;
            }
        }

        // Developer scenario (ROUNDHEAD_DEV=revive): in the test level ROUNDS's player 1 dies 12 s in, 4 s later its ghost is
        // parried (PlayerDeathEffect.OnParrySwitch, what the partner's parry does) and player 1 must be back, alive and driven
        // by ROUNDS. Also checks the ghost is the ROUNDS one (Cuphead's sprite off, a RoundsGhost on). Never runs for players.
        private static Level devReviveLevel;
        private static float devReviveAt, devReviveKilledAt = -1f, devReviveParriedAt = -1f;
        private static bool devReviveDone;

        private static void DevRevive(float now)
        {
            if (Dev != "revive" || Level.Current == null || !Level.Current.Started || devReviveDone || ArenaClient.link == null) return;
            if (Level.Current != devReviveLevel) { devReviveLevel = Level.Current; devReviveAt = now; devReviveKilledAt = devReviveParriedAt = -1f; }
            BlobController c = null;
            foreach (var b in BlobController.All) if (b != null && b.index == 0) c = b;
            if (c == null) return;
            if (devReviveKilledAt < 0f && now - devReviveAt > 12f)
            {
                devReviveKilledAt = now;
                ArenaClient.link.PostEvent(ArenaLink.EvKill, 0);
                Plugin.L.LogInfo("dev: killing ROUNDS player 1 for the revive check");
            }
            if (devReviveKilledAt > 0f && devReviveParriedAt < 0f && now - devReviveKilledAt > 4f)
            {
                devReviveParriedAt = now;
                PlayerDeathEffect ghost = null;
                foreach (var e in UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>())
                    if (e != null && Reflect.Get(e, "playerId") is PlayerId id && id == PlayerId.PlayerOne) ghost = e;
                var sprite = ghost != null ? Reflect.Get<SpriteRenderer>(ghost, "spriteRenderer") : null;
                bool ours = ghost != null && ghost.GetComponent<RoundsGhost>() != null && sprite != null && !sprite.enabled;
                Report("dev-revive-ghost", ours, ghost == null ? "no death ghost for player 1 after 4 s" : ours ? "player 1's ghost is the ROUNDS ghost (Cuphead's sprite off)" : "player 1's ghost still shows Cuphead's sprite");
                if (ghost != null) Reflect.Call(ghost, "OnParrySwitch");
            }
            if (devReviveParriedAt > 0f && now - devReviveParriedAt > 8f)
            {
                devReviveDone = true;
                bool ok = c.player != null && !c.player.IsDead && c.player.gameObject.activeInHierarchy && c.Arena;
                Report("dev-revive", ok, ok ? "player 1 is back alive and driven by ROUNDS 8 s after the ghost was parried" : "player 1 did not come back (dead " + (c.player != null && c.player.IsDead) + ", driven " + c.Arena + ")");
            }
        }

        // Developer scenario (ROUNDHEAD_DEV=statuses): the Cuphead half of ROUNDS's enemy statuses, without ROUNDS. In the test
        // level (Botanic Panic; a run-and-gun level through ROUNDHEAD_DEV_SCENE) it feeds EnemyStatus directly, as
        // ArenaClient.ReadStatus / ReadFx would, and reports dev-status-* checks (design: "Cuphead-side status bench").
        // cardprobe.py covers the ROUNDS side and the protocol. EnemyStatus is reached by name, so this builds without it.
        // Never runs for players: it needs a Melty test run (Active) and the variable.
        private static Level devStatusLevel;
        private static StatusApi devApi;
        private static readonly List<DevWant> devWants = new List<DevWant>();
        private static Transform waitOwner;
        private static int waitCalls;
        private static bool devPatched;

        /// <summary>The statuses the bench holds on one enemy; re-merged every frame (also right after EnemyStatus.EndFrame,
        /// which drops statuses the ROUNDS table did not refresh).</summary>
        private sealed class DevWant
        {
            public object status;
            public float slow, stunUntil, silenceUntil;
        }

        /// <summary>EnemyStatus (src/CupheadMod/EnemyStatus.cs) found by name: For, Merge, AddPush, Speed, Active, EndFrame, ...</summary>
        private sealed class StatusApi
        {
            public Type type;
            public MethodInfo forR, merge, addPush, phaseChanged, noStatusNow, endFrame;
            public PropertyInfo speed;
            public FieldInfo active, pushV;

            public static StatusApi Find(out string missing)
            {
                missing = "";
                var t = typeof(TestMode).Assembly.GetType("CupRounds.EnemyStatus");
                if (t == null) { missing = "type CupRounds.EnemyStatus"; return null; }
                var a = new StatusApi
                {
                    type = t,
                    forR = AccessTools.Method(t, "For", new[] { typeof(DamageReceiver) }),
                    merge = AccessTools.Method(t, "Merge"),
                    addPush = AccessTools.Method(t, "AddPush", new[] { typeof(Vector2) }),
                    phaseChanged = AccessTools.Method(t, "PhaseChanged"),
                    noStatusNow = AccessTools.Method(t, "NoStatusNow", new[] { typeof(DamageReceiver) }),
                    endFrame = AccessTools.Method(t, "EndFrame"),
                    speed = AccessTools.Property(t, "Speed"),
                    active = AccessTools.Field(t, "Active"),
                    pushV = AccessTools.Field(t, "pushV")
                };
                if (a.forR == null) missing += " For(DamageReceiver)";
                if (a.merge == null || a.merge.GetParameters().Length != 7) missing += " Merge(slow, stun, silence, dot, rgba, flags, aux)";
                if (a.speed == null) missing += " Speed";
                if (a.active == null) missing += " Active";
                return missing.Length == 0 ? a : null;
            }

            public object Of(DamageReceiver r) => forR.Invoke(null, new object[] { r });
            public void Merge(object s, float slow, float stun, float silence) => merge.Invoke(s, new object[] { slow, stun, silence, 0f, 0, 0, 0f });
            public float Speed(object s) => Convert.ToSingle(speed.GetValue(s, null));
            public bool IsActive(object s) { var l = active.GetValue(null) as IList; return l != null && l.Contains(s); }
        }

        /// <summary>Developer aid (ROUNDHEAD_DEV_CARDS="Poison bullets,Cold bullets,..."): these ROUNDS cards are put in every
        /// present player's deck before the test level, so their effects on a real boss can be watched. Test saves only.</summary>
        private static void DevCards()
        {
            // Every Melty test run: player 1 starts the level with Cold Bullets in the deck (a head start, as a shop pack would
            // be), so the recording shows a card effect on the boss from the first attempt and the enemy-status and
            // enemy-wait-dilation checks do not wait for a loss pick. Not in a dev scenario (the status bench wants a boss with
            // no other status on it).
            if (Active && Dev.Length == 0 && Array.IndexOf(Progress.Owned(0), "Cold bullets") < 0) Progress.AddCard(0, "Cold bullets");
            string list = Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_CARDS");
            if (string.IsNullOrEmpty(list)) return;
            foreach (string raw in list.Split(','))
            {
                string id = raw.Trim();
                if (id.Length == 0) continue;
                for (int p = 0; p < 2; p++) if (Progress.PlayerPresent(p)) Progress.AddCard(p, id);
            }
            Plugin.L.LogInfo("dev: cards added for the test level: " + list);
        }

        private static void DevStatuses()
        {
            if (Dev != "statuses" || Level.Current == null || !Level.Current.Started || Level.Current == devStatusLevel) return;
            devStatusLevel = Level.Current;
            Plugin.I.StartCoroutine(StatusBench(Level.Current));
        }

        /// <summary>Harmony prefix on EnemyStatus.EndFrame (dev only, patched by the bench): the bench's statuses count as this
        /// frame's rows, as ROUNDS's table would, so EndFrame never restores the enemy between frames (a restore resets the
        /// stun governor's episode, which would let a bench stun outlast its cap).</summary>
        private static void ReMerge()
        {
            if (devApi == null) return;
            float now = Time.time;
            foreach (var w in devWants)
                if (w.status != null) devApi.Merge(w.status, w.slow, Mathf.Max(0f, w.stunUntil - now), Mathf.Max(0f, w.silenceUntil - now));
        }

        /// <summary>Harmony prefix on CupheadTime.WaitForSeconds (dev only): counts the boss's waits that reach the method,
        /// i.e. were not inlined by Mono (design: "Per-enemy coroutine time", inlining caveat).</summary>
        private static void CountWait(MonoBehaviour m)
        {
            if (waitOwner != null && m != null && m.transform.IsChildOf(waitOwner)) waitCalls++;
        }

        private static void PatchDev()
        {
            if (devPatched) return;
            devPatched = true;
            var h = new Harmony(Plugin.Guid + ".dev-statuses");
            var count = new HarmonyMethod(AccessTools.Method(typeof(TestMode), nameof(CountWait)));
            foreach (var m in new[] { AccessTools.Method(typeof(CupheadTime), "WaitForSeconds", new[] { typeof(MonoBehaviour), typeof(float) }),
                                      AccessTools.Method(typeof(CupheadTime), "WaitForSeconds", new[] { typeof(MonoBehaviour), typeof(float), typeof(CupheadTime.Layer) }) })
                if (m != null) h.Patch(m, prefix: count);
            if (devApi != null && devApi.endFrame != null) h.Patch(devApi.endFrame, prefix: new HarmonyMethod(AccessTools.Method(typeof(TestMode), nameof(ReMerge))));
        }

        private static float Tune(string name, float fallback)
        {
            try
            {
                var t = typeof(TestMode).Assembly.GetType("CupRounds.Tuning");
                var f = t != null ? t.GetField(name, BindingFlags.Public | BindingFlags.Static) : null;
                return f != null ? Convert.ToSingle(f.GetValue(null)) : fallback;
            }
            catch { return fallback; }
        }

        /// <summary>LevelProperties.&lt;Boss&gt;.Entity subclasses are bosses (LevelProperties.cs:8), as ArenaClient tags them.</summary>
        private static bool IsBossReceiver(DamageReceiver r)
        {
            var e = r.GetComponentInParent<AbstractLevelEntity>();
            if (e == null || e is AbstractPlatformingLevelEnemy) return false;
            for (var t = e.GetType(); t != null; t = t.BaseType)
                if (t.DeclaringType != null && t.DeclaringType.DeclaringType == typeof(LevelProperties)) return true;
            return false;
        }

        private static bool Usable(DamageReceiver r) => r != null && r.type == DamageReceiver.Type.Enemy && r.enabled && r.gameObject.activeInHierarchy;

        private static Transform OwnerOf(DamageReceiver r)
        {
            if (r == null) return null;
            var e = r.GetComponentInParent<AbstractLevelEntity>();
            return e != null ? e.transform : r.transform;
        }

        private static Animator AnimOf(DamageReceiver r) => r.animator != null ? r.animator : OwnerOf(r).GetComponentInChildren<Animator>();

        private static bool Running(Level lv, DamageReceiver r) => Level.Current == lv && !lv.Ending && Usable(r);

        /// <summary>Runs one bench step; an exception in it fails that check instead of stopping the bench.</summary>
        private static IEnumerator Guard(string check, IEnumerator step, DamageReceiver boss = null)
        {
            if (boss != null && !Usable(boss)) { Report(check, false, "inconclusive: the boss is gone before this step"); yield break; }
            while (true)
            {
                object cur;
                try
                {
                    if (!step.MoveNext()) break;
                    cur = step.Current;
                }
                catch (Exception e)
                {
                    // A reflection call's own exception is the interesting one.
                    Exception inner = e;
                    while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                    string where = inner.StackTrace != null ? inner.StackTrace.Split('\n')[0].Trim() : "";
                    Report(check, false, "bench error: " + inner.GetType().Name + " " + inner.Message + " " + where);
                    Plugin.L.LogWarning("dev statuses " + check + ": " + e);
                    break;
                }
                yield return cur;
            }
            devWants.Clear();
        }

        private static IEnumerator StatusBench(Level lv)
        {
            yield return new WaitForSeconds(4f);   // past the intro: the boss is attacking
            string missing;
            devApi = StatusApi.Find(out missing);
            if (devApi == null) { Report("dev-statuses", false, "EnemyStatus API not found:" + missing); yield break; }
            benchQuiet = true;
            PatchDev();
            DamageReceiver boss = null;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<DamageReceiver>()) if (Usable(r) && IsBossReceiver(r)) { boss = r; break; }
            if (boss != null)
            {
                Plugin.L.LogInfo("dev: status bench on " + boss.name + " in " + lv.CurrentLevel);
                yield return Guard("dev-status-wait-counter", WaitCounterStep(boss, lv), boss);
                yield return Guard("dev-status-slow", SlowStep(boss, lv), boss);
                yield return Guard("dev-status-stun", StunStep(boss, lv), boss);
                yield return Guard("dev-status-silence", SilenceStep(boss, lv), boss);
                yield return Guard("dev-status-tint", TintStep(boss, lv), boss);
                yield return Guard("dev-status-stale", StaleStep(boss, lv), boss);
                yield return Guard("dev-status-push-boss", BossPushStep(boss, lv), boss);
                yield return Guard("dev-status-phase", PhaseStep(boss, lv), boss);
            }
            else Plugin.L.LogInfo("dev: no boss in " + lv.CurrentLevel + "; boss steps skipped");
            // Only a small enemy that moves itself takes pushes (EnemyStatus.LightInfo): a ground walker or path mover.
            DamageReceiver light = null;
            float maxSize = Tune("lightEnemyMaxSize", 220f);
            foreach (var e in UnityEngine.Object.FindObjectsOfType<AbstractPlatformingLevelEnemy>())
            {
                if (!(e is PlatformingLevelGroundMovementEnemy) && !(e is PlatformingLevelPathMovementEnemy)) continue;
                var r = e.GetComponentInChildren<DamageReceiver>();
                var c = r != null ? r.GetComponent<Collider2D>() : null;
                if (Usable(r) && c != null && Mathf.Max(c.bounds.size.x, c.bounds.size.y) <= maxSize) { light = r; break; }
            }
            if (light != null) yield return Guard("dev-status-push-light", LightPushStep(light, lv));
            else Plugin.L.LogInfo("dev: no platforming enemy in " + lv.CurrentLevel + "; minion push skipped");
            // Not automated: step 5 of the design (KO while statuses are re-applied every frame) ends the level.
            benchQuiet = false;
            Report("dev-statuses", boss != null || light != null, "status bench finished in " + lv.CurrentLevel);
        }

        private static DevWant Hold(DamageReceiver r, float slow = 0f, float stun = 0f, float silence = 0f)
        {
            var w = new DevWant { status = devApi.Of(r), slow = slow, stunUntil = stun > 0f ? Time.time + stun : 0f, silenceUntil = silence > 0f ? Time.time + silence : 0f };
            devWants.Add(w);
            ReMerge();
            return w;
        }

        // 9: waits from the boss reach CupheadTime.WaitForSeconds (so the dilation prefix can act on them)
        private static IEnumerator WaitCounterStep(DamageReceiver boss, Level lv)
        {
            waitOwner = OwnerOf(boss); waitCalls = 0;
            float until = Time.time + 6f;
            while (Time.time < until && Running(lv, boss)) yield return null;
            Report("dev-status-wait-counter", waitCalls > 0, waitCalls + " boss waits reached CupheadTime.WaitForSeconds in 6 s");
            waitOwner = null;
        }

        // 1: slow 0.7 on the boss: animator speed ~ base x (1 - 0.7 x bossSlowScale); a 1 s boss wait takes 1 / that
        private static IEnumerator SlowStep(DamageReceiver boss, Level lv)
        {
            var anim = AnimOf(boss);
            float want = 1f - 0.7f * Tune("bossSlowScale", 0.6f);
            yield return new WaitForEndOfFrame();
            float baseSpeed = anim != null ? anim.speed : 1f;
            var w = Hold(boss, slow: 0.7f);
            float waited = -1f;
            Plugin.I.StartCoroutine(TimeWait(OwnerOf(boss).GetComponent<MonoBehaviour>() ?? boss, 1f, v => waited = v));
            var ratios = new List<float>();
            float t0 = Time.time;
            while (Time.time - t0 < 3f && Running(lv, boss))
            {
                ReMerge();
                yield return new WaitForEndOfFrame();
                if (anim != null && baseSpeed > 0.01f && Time.time - t0 > 0.3f) ratios.Add(anim.speed / baseSpeed);
                yield return null;
            }
            devWants.Remove(w);
            ratios.Sort();
            float ratio = ratios.Count > 0 ? ratios[ratios.Count / 2] : -1f;
            Report("dev-status-slow-anim", anim != null && Mathf.Abs(ratio - want) <= 0.1f * want, "boss animator at " + ratio.ToString("0.00") + " x its speed (want " + want.ToString("0.00") + ")");
            float until = Time.time + 3f;
            while (waited < 0f && Time.time < until) yield return null;
            float expect = 1f / want;
            Report("dev-status-slow-wait", waited > 0f && Mathf.Abs(waited - expect) <= 0.1f * expect, "a 1 s boss wait took " + waited.ToString("0.00") + " s (want " + expect.ToString("0.00") + ")");
        }

        private static IEnumerator TimeWait(MonoBehaviour m, float secs, Action<float> done)
        {
            float t0 = Time.time;
            yield return CupheadTime.WaitForSeconds(m, secs);
            done(Time.time - t0);
        }

        // 2: stun 1.5 s on the boss: frozen (speed 0 / IsHitPaused) for at most bossStunCap, then moving again
        private static IEnumerator StunStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1.5f);
            var anim = AnimOf(boss);
            float cap = Tune("bossStunCap", 1f);
            var w = Hold(boss, stun: 1.5f);
            float t0 = Time.time, streak = 0f, best = 0f, last = Time.time;
            bool paused = false;
            while (Time.time - t0 < 2.5f && Running(lv, boss))
            {
                ReMerge();
                yield return new WaitForEndOfFrame();
                bool frozen = (anim != null && anim.speed < 0.01f) || boss.IsHitPaused;
                paused |= boss.IsHitPaused;
                streak = frozen ? streak + (Time.time - last) : 0f;
                best = Mathf.Max(best, streak);
                last = Time.time;
                yield return null;
            }
            devWants.Remove(w);
            yield return new WaitForSeconds(0.3f);
            bool after = (anim != null && anim.speed < 0.01f) || boss.IsHitPaused;
            Report("dev-status-stun", best >= 0.3f && best <= cap + 0.15f && paused && !after,
                "longest freeze " + best.ToString("0.00") + " s (cap " + cap + "), IsHitPaused seen " + paused + ", frozen after " + after);
        }

        // 3: silence: projectiles the boss fires are duds (a DudShot; colliders stay on); parryable ones are left alone.
        // 12 s, since a boss slowed and stunned by the steps before is still working through its dilated waits.
        private static IEnumerator SilenceStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1.5f);
            var w = Hold(boss, silence: 12f);
            var seen = new HashSet<int>();
            int duds = 0, live = 0, far = 0;
            float nearest = float.PositiveInfinity;
            var col = boss.GetComponent<Collider2D>();
            var body = OwnerOf(boss).GetComponentsInChildren<SpriteRenderer>();
            float pad = Tune("silenceSpawnPad", 60f), t0 = Time.time;
            while (Time.time - t0 < 12f && Running(lv, boss))
            {
                ReMerge();
                yield return new WaitForEndOfFrame();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<AbstractProjectile>())
                {
                    if (p == null || !seen.Add(p.GetInstanceID()) || p.CanParry || !p.CompareTag("EnemyProjectile") || col == null) continue;
                    Vector3 at = p.transform.position;
                    // The game's rule (EnemyStatus.SpawnedBy): inside the padded hit area or the padded drawn body.
                    bool near = NearBounds(col.bounds, at, pad);
                    foreach (var r in body) if (!near && r != null && r.enabled && r.sprite != null && r.gameObject.activeInHierarchy) near = NearBounds(r.bounds, at, pad);
                    if (!near)
                    {
                        // Fired from outside both: the game would not dud it either; say how far.
                        far++;
                        nearest = Mathf.Min(nearest, Vector2.Distance(at, col.bounds.ClosestPoint(at)));
                        continue;
                    }
                    if (p.GetComponent("DudShot") != null) duds++; else live++;
                }
                yield return null;
            }
            devWants.Remove(w);
            string outside = far > 0 ? " (" + far + " more appeared outside its padded hit area and body, the nearest " + nearest.ToString("0") + " units from the hit area)" : "";
            if (duds + live == 0) Report("dev-status-silence", false, "inconclusive: the boss fired no projectile near its hit area or body in 12 s" + outside);
            else Report("dev-status-silence", live == 0, duds + " dud and " + live + " live projectiles fired by the silenced boss" + outside);
        }

        private static bool NearBounds(Bounds b, Vector3 p, float pad)
        {
            b.Expand(pad * 2f);
            return b.Contains(new Vector3(p.x, p.y, b.center.z));
        }

        // 8: tint: a slowed boss is tinted, and its colours come back exactly when the status ends
        private static IEnumerator TintStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();
            var rends = OwnerOf(boss).GetComponentsInChildren<SpriteRenderer>();
            var before = new Color[rends.Length];
            for (int i = 0; i < rends.Length; i++) before[i] = rends[i] != null ? rends[i].color : Color.clear;
            var w = Hold(boss, slow: 0.7f);
            int tinted = 0;
            float t0 = Time.time;
            while (Time.time - t0 < 1f && Running(lv, boss))
            {
                ReMerge();
                yield return new WaitForEndOfFrame();
                tinted = 0;
                for (int i = 0; i < rends.Length; i++) if (rends[i] != null && rends[i].color != before[i]) tinted++;
                yield return null;
            }
            devWants.Remove(w);
            yield return new WaitForSeconds(0.6f);
            yield return new WaitForEndOfFrame();
            int same = 0, n = 0;
            for (int i = 0; i < rends.Length; i++) if (rends[i] != null) { n++; if (rends[i].color == before[i]) same++; }
            Report("dev-status-tint", tinted > 0 && same >= 0.8f * n, tinted + " renderers tinted while slowed; " + same + " of " + n + " back to their exact colour");
        }

        // 6: stale: when nothing refreshes the status (ROUNDS stopped writing), it is cleared within statusStaleSeconds
        private static IEnumerator StaleStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1.5f);
            var anim = AnimOf(boss);
            yield return new WaitForEndOfFrame();
            float baseSpeed = anim != null ? anim.speed : 1f;
            var w = Hold(boss, slow: 0.7f);
            object s = w.status;
            float t0 = Time.time;
            while (Time.time - t0 < 1f && Running(lv, boss)) { ReMerge(); yield return null; }
            devWants.Remove(w);
            float stale = Tune("statusStaleSeconds", 0.5f), t1 = Time.realtimeSinceStartup;
            bool cleared = false;
            while (Time.realtimeSinceStartup - t1 < stale + 0.4f && !cleared)
            {
                yield return new WaitForEndOfFrame();
                cleared = !devApi.IsActive(s) && (anim == null || Mathf.Abs(anim.speed - baseSpeed) < 0.02f);
                yield return null;
            }
            Report("dev-status-stale", cleared, cleared ? "cleared " + (Time.realtimeSinceStartup - t1).ToString("0.00") + " s after the last refresh" : "still active " + (stale + 0.4f) + " s after the last refresh");
        }

        // 7 (boss): a push never moves a boss; a strong one is only a flinch
        private static IEnumerator BossPushStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1f);
            if (devApi.addPush == null) { Report("dev-status-push-boss", false, "EnemyStatus.AddPush not found"); yield break; }
            object s = devApi.Of(boss);
            devApi.addPush.Invoke(s, new object[] { new Vector2(1500f, 0f) });
            yield return null;
            var v = devApi.pushV != null ? (Vector2)devApi.pushV.GetValue(s) : Vector2.zero;
            Report("dev-status-push-boss", v == Vector2.zero, devApi.pushV == null ? "no pushV field to read (assumed unmoved)" : "boss push velocity " + v);
        }

        // 7 (minion): AddPush(1500, 0) on a platforming enemy moves it to the right and keeps it inside the level
        private static IEnumerator LightPushStep(DamageReceiver r, Level lv)
        {
            if (devApi.addPush == null) { Report("dev-status-push-light", false, "EnemyStatus.AddPush not found"); yield break; }
            var owner = OwnerOf(r);
            float x0 = owner.position.x;
            devApi.addPush.Invoke(devApi.Of(r), new object[] { new Vector2(1500f, 0f) });
            yield return new WaitForSeconds(0.4f);
            if (owner == null) { Report("dev-status-push-light", false, "the enemy is gone"); yield break; }
            float x1 = owner.position.x;
            bool inside = x1 >= lv.Left - 1f && x1 <= lv.Right + 1f;   // Level.Left/Right are x coordinates (Level.cs:555-557)
            Report("dev-status-push-light", x1 - x0 > 20f && inside, "moved " + (x1 - x0).ToString("0") + " units (inside the level " + inside + ")");
        }

        // 4: phase change: boss statuses are dropped at once and NoStatusNow holds for phaseChangeImmunity
        private static IEnumerator PhaseStep(DamageReceiver boss, Level lv)
        {
            yield return new WaitForSeconds(1.5f);
            if (devApi.phaseChanged == null) { Report("dev-status-phase", false, "EnemyStatus.PhaseChanged not found"); yield break; }
            var anim = AnimOf(boss);
            yield return new WaitForEndOfFrame();
            float baseSpeed = anim != null ? anim.speed : 1f;
            var w = Hold(boss, slow: 0.7f);
            float t0 = Time.time;
            while (Time.time - t0 < 0.6f && Running(lv, boss)) { ReMerge(); yield return null; }
            devWants.Remove(w);
            devApi.phaseChanged.Invoke(null, null);
            yield return new WaitForEndOfFrame();
            bool restored = anim == null || Mathf.Abs(anim.speed - baseSpeed) < 0.02f;
            bool immune = devApi.noStatusNow == null || (bool)devApi.noStatusNow.Invoke(null, new object[] { boss });
            Report("dev-status-phase", restored && immune, "speed restored " + restored + ", NoStatusNow " + immune);
        }

        public static void Tick()
        {
            if (!Active) return;
            if (!Application.runInBackground) Application.runInBackground = true;
            float now = Time.realtimeSinceStartup;
            switch (step)
            {
                case Step.WaitTitle:
                    if (Plugin.Engine.Connected && SceneLoader.SceneName == Scenes.scene_title.ToString() || now - startedAt > 40f && Plugin.Engine.Connected)
                    {
                        step = Step.LoadingData;
                        stepAt = now;
                        PlayerData.Init(ok =>
                        {
                            if (!ok) { step = Step.WaitTitle; return; }
                            PlayerData.CurrentSaveFileIndex = 0;
                            PlayerData.inGame = true;
                            // Couch co-op: switch on Cuphead's 2-player mode so player 2 is a blob with cards too.
                            PlayerManager.Multiplayer = true;
                            step = Step.PickCards;
                            stepAt = Time.realtimeSinceStartup;
                        });
                    }
                    else if (now - startedAt > 150f)
                    {
                        Report("engine-cards", false, "the ROUNDS card engine never answered");
                    }
                    break;
                case Step.PickCards:
                    if (Plugin.Catalog.Count == 0) { if (now - stepAt > 60f) Report("cards-applied", false, "no ROUNDS card catalog"); break; }
                    // Visit the world map first (the ROUNDS characters on it), then the test level with an empty deck: the
                    // first pick is the loss pick on the game-over card, as it is for players.
                    step = Step.OnMap;
                    stepAt = now;
                    SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Iris);
                    break;
                case Step.OnMap:
                    if (Map.Current != null && Map.Current.CurrentState == Map.State.Ready && (PickOverlay.I == null || !PickOverlay.I.Open) && mapDoneAt < 0f) mapDoneAt = now;
                    // The map's Ready state never came (an event on the loaded slot): the level is loaded all the same.
                    if ((mapDoneAt > 0f && now - mapDoneAt > 5f) || now - stepAt > 90f)
                    {
                        step = Step.InLevel;
                        stepAt = now;
                        LoadTestLevel();
                    }
                    break;
                case Step.InLevel:
                    TestRetry(now);
                    DevRetry(now);
                    DevRevive(now);
                    DevWin(now);
                    DevStatuses();
                    if (now - stepAt > 120f)
                    {
                        Report("coop-blobs", false, "player 2 did not appear as a ROUNDS blob");
                        Report("blob-spawned", false, "no ROUNDS blob appeared in the level");
                        Report("blob-hits-boss", false, "no ROUNDS bullet hit the boss within two minutes");
                    }
                    if (now - stepAt > 170f)
                    {
                        Report("cards-picked", false, "no loss pick was made on a game-over card within 170 s (the bots never lost, or the pick did not come up)");
                        Report("cards-applied", false, "ROUNDS's player never held the picked cards");
                    }
                    break;
            }
        }
    }
}
