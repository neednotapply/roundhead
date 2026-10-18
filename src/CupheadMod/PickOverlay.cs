using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The ROUNDS card pick: on the game-over card after a lost level (the loser picks, ROUNDS's way), in the shop after
    /// buying a pack, and on the world map for a pick left over (a game-over card quit before picking). With the ROUNDS
    /// arena up it is ROUNDS's own pick screen (its cards, its face, its rules, mod cards included), shown full screen
    /// and played with the Cuphead player's keys, stick or mouse; without it, a fallback screen of ROUNDS card faces.
    /// </summary>
    public class PickOverlay : MonoBehaviour
    {
        private enum Phase { Idle, Offering, Showing, Rounds }

        public static PickOverlay I;
        private Phase phase = Phase.Idle;
        private int player;
        private string[] ids = new string[0];
        private readonly Dictionary<string, Texture2D> faces = new Dictionary<string, Texture2D>();
        private int selected;
        private float shownAt;
        private bool mapLocked;
        private int offerToken;
        private GUIStyle title, small, panelName, panelText;
        private float waitingNoteUntil;
        private bool gameOverPick;
        private float gameOverAt = -1f;
        private int offerFailures;
        private float nextOfferAt, offerStartedAt, menuHoldUntil;
        private int menuHoldFrame = -1;
        private const int MaxOfferFailures = 3;

        public bool Open => phase != Phase.Idle;
        public bool GameOverReady => OnGameOver;

        private void Awake() { I = this; }

        private bool OnMap => Map.Current != null && SceneLoader.CurrentlyLoading == false;
        private bool InShop => ShopScene.Current != null;

        /// <summary>The game-over card is up and ready (LevelGameOverGUI.State.Ready) after a lost level ROUNDS's players
        /// were in (the tutorial takes no part).</summary>
        private static bool OnGameOver
        {
            get
            {
                var gui = LevelGameOverGUI.Current;
                if (gui == null || !gui.gameObject.activeInHierarchy || Level.Current == null || !Level.Current.Ending || Level.Won || ArenaSeat.Seats.Count == 0) return false;
                object state = Reflect.Get(gui, "state");
                return state != null && System.Convert.ToInt32(state) == 1;
            }
        }

        /// <summary>
        /// The game-over card's menu (Retry, Exit to Map) waits while the loss picks are made on it: while a pick is open,
        /// for a moment after one closes (the press that took the card must not also answer the menu), and while picks are
        /// pending and ROUNDS is expected to offer them within a few seconds. Without ROUNDS, or after repeated failed
        /// offers, the card goes on and the picks wait for the map.
        /// </summary>
        public bool HoldsGameOver()
        {
            if (!OnGameOver) { gameOverAt = -1f; offerFailures = 0; return false; }
            if (gameOverAt < 0f) gameOverAt = Time.unscaledTime;
            if (Open) return true;
            if (Time.frameCount <= menuHoldFrame || Time.unscaledTime < menuHoldUntil) return true;
            if (!AnyPending() || offerFailures >= MaxOfferFailures) return false;
            return Time.unscaledTime - gameOverAt < 15f;
        }

        private void Update()
        {
            if (phase == Phase.Idle)
            {
                if (!(OnMap && (Map.Current.CurrentState == Map.State.Ready || mapLocked)) && !InShop && !OnGameOver) return;
                if (Time.unscaledTime < nextOfferAt || offerFailures >= MaxOfferFailures) return;
                for (int p = 0; p < 2; p++)
                {
                    if (!Progress.PlayerPresent(p) || Progress.Pending(p) <= 0) continue;
                    if (InShop && !ShopPackOverlay.AllowsPick(p)) continue;
                    // ROUNDS is up but its sandbox is not ready yet: wait for it, so the pick is ROUNDS's own screen.
                    if (!Plugin.Engine.Connected || (ArenaClient.RoundsRunning && !ArenaClient.Live)) { waitingNoteUntil = Time.unscaledTime + 0.5f; return; }
                    StartOffer(p);
                    return;
                }
                return;
            }
            if (OnMap && !mapLocked && Map.Current.CurrentState == Map.State.Ready)
            {
                Map.Current.CurrentState = Map.State.Event;
                mapLocked = true;
            }
            // The card engine's offer never came (its main thread is stuck): give the offer up, the menu goes on.
            if (phase == Phase.Offering && Time.unscaledTime - offerStartedAt > 8f) { offerToken++; OfferFailed("the card engine did not answer the offer in 8 s"); return; }
            if (phase == Phase.Showing) ReadInput();
            if (phase == Phase.Rounds) WatchRoundsPick();
        }

        private void WatchRoundsPick()
        {
            string[] got = ArenaClient.PickResult();
            if (got == null)
            {
                // ROUNDS went away, or never took the pick up (its player for this slot is missing): the fallback screen instead.
                bool gone = !ArenaClient.Live && ArenaClient.PickRunningFor > 3f;
                bool ignored = ArenaClient.Live && !ArenaClient.EnginePicking && ArenaClient.PickRunningFor > 10f;
                if (gone || ignored)
                {
                    Plugin.L.LogWarning("ROUNDS's pick screen did not come up (" + (gone ? "ROUNDS stopped answering" : "ROUNDS did not start the pick") + "): offering the cards here");
                    ArenaClient.CancelPick();
                    FallbackOffer(player);
                }
                return;
            }
            if (got.Length == 0)
            {
                // ROUNDS's pick ended with nothing (it threw or stalled): once more, then the fallback screen, then give up.
                offerFailures++;
                Plugin.L.LogWarning("ROUNDS's pick for player " + (player + 1) + " gave no card (" + offerFailures + ")");
                if (offerFailures >= MaxOfferFailures) { Close(); return; }
                if (offerFailures >= 2) { FallbackOffer(player); return; }
                phase = Phase.Idle;
                nextOfferAt = Time.unscaledTime + 1f;
                return;
            }
            foreach (string id in got)
            {
                CardData d;
                Plugin.Catalog.TryGetValue(id, out d);
                Plugin.L.LogInfo("player " + (player + 1) + " picked ROUNDS card " + (d != null ? d.name : id) + " on ROUNDS's pick screen");
                Progress.AddCard(player, id);
                Picked(id);
            }
            offerFailures = 0;
            AudioManager.Play("menu_equipment_equip");
            PickClosed();
            if (!AnyPending()) Close();
        }

        /// <summary>A pick just ended: the menu under it stays held for a moment, so the press that took the card is not read again.</summary>
        private void PickClosed()
        {
            phase = Phase.Idle;
            ids = new string[0];
            menuHoldFrame = Time.frameCount + 1;
            menuHoldUntil = Time.unscaledTime + 0.3f;
        }

        private void OfferFailed(string why)
        {
            offerFailures++;
            Plugin.L.LogWarning("no card offer for player " + (player + 1) + ": " + why + " (" + offerFailures + ")");
            nextOfferAt = Time.unscaledTime + 2f;
            Close();
        }

        private void Picked(string id)
        {
            if (!gameOverPick || !TestMode.Active) return;
            CardData d;
            Plugin.Catalog.TryGetValue(id, out d);
            TestMode.Report("cards-picked", true, "player " + (player + 1) + " picked " + (d != null ? d.name : id) + " on the game-over card after losing " + (Level.Current != null ? Level.Current.CurrentLevel.ToString() : "the level") + " (deck now " + string.Join(", ", Progress.Owned(player)) + ")");
        }

        public void ReadInput()
        {
            Rewired.Player rp = PlayerManager.GetPlayerInput(player == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
            if (Time.unscaledTime - shownAt < 0.35f) return;
            if (TestMode.Active && Time.unscaledTime - shownAt > 3.5f && faces.Count >= ids.Length) { Take(0); return; }
            if (TestMode.Active && Time.unscaledTime - shownAt > 12f) { Take(0); return; }
            bool left = (rp != null && (rp.GetButtonDown(Buttons.PickLeft) || rp.GetNegativeButtonDown(0))) || Input.GetKeyDown(KeyCode.LeftArrow) || (player == 0 && Input.GetKeyDown(KeyCode.A));
            bool right = (rp != null && (rp.GetButtonDown(Buttons.PickRight) || rp.GetButtonDown(0))) || Input.GetKeyDown(KeyCode.RightArrow) || (player == 0 && Input.GetKeyDown(KeyCode.D));
            bool accept = (rp != null && (rp.GetButtonDown(Buttons.PickAccept) || rp.GetButtonDown(Buttons.Jump))) || Input.GetKeyDown(KeyCode.Return);
            if (left) selected = (selected + ids.Length - 1) % ids.Length;
            if (right) selected = (selected + 1) % ids.Length;
            if (accept) Take(selected);
        }

        private void StartOffer(int p)
        {
            player = p;
            gameOverPick = OnGameOver;
            if (ArenaClient.Live)
            {
                phase = Phase.Rounds;
                ArenaClient.RequestPick(p, Mathf.Clamp(Progress.Pending(p), 1, ArenaLink.MaxPicked));
                AudioManager.Play("menu_card_up");
                return;
            }
            FallbackOffer(p);
        }

        /// <summary>The pick without ROUNDS's screen: the card engine's offer, drawn here as ROUNDS card faces.</summary>
        private void FallbackOffer(int p)
        {
            phase = Phase.Offering;
            offerStartedAt = Time.unscaledTime;
            int token = ++offerToken;
            int seed = TestMode.Active ? TestMode.Seed + 1000 * p + Progress.Owned(p).Length : Random.Range(1, int.MaxValue);
            Plugin.Engine.Offer(Progress.Owned(p), Rules.LossOffer, seed, r =>
            {
                if (token != offerToken) return;
                if (!r.ok || r.ids == null || r.ids.Length == 0)
                {
                    OfferFailed(r.error ?? "empty offer");
                    return;
                }
                ids = r.ids;
                selected = 0;
                faces.Clear();
                foreach (string id in ids)
                {
                    string cid = id;
                    Plugin.Engine.Render(cid, rr => { if (rr.ok) faces[cid] = Gfx.FromPng(rr.png); });
                }
                phase = Phase.Showing;
                shownAt = Time.unscaledTime;
                AudioManager.Play("menu_card_up");
            });
        }

        private void Take(int i)
        {
            if (i < 0 || i >= ids.Length) return;
            string id = ids[i];
            CardData d;
            Plugin.Catalog.TryGetValue(id, out d);
            Plugin.L.LogInfo("player " + (player + 1) + " picked ROUNDS card " + (d != null ? d.name : id));
            Progress.AddCard(player, id);
            Picked(id);
            offerFailures = 0;
            AudioManager.Play("menu_equipment_equip");
            PickClosed();
            if (!AnyPending()) Close();
        }

        private bool AnyPending()
        {
            for (int p = 0; p < 2; p++)
                if (Progress.PlayerPresent(p) && Progress.Pending(p) > 0 && (!InShop || ShopPackOverlay.AllowsPick(p))) return true;
            return false;
        }

        private void Close()
        {
            if (phase != Phase.Idle) PickClosed();
            phase = Phase.Idle;
            ids = new string[0];
            if (mapLocked && Map.Current != null && Map.Current.CurrentState == Map.State.Event)
                Map.Current.CurrentState = Map.State.Ready;
            mapLocked = false;
            ShopPackOverlay.PickDone();
        }

        private void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            title.normal.textColor = Color.white;
            small = new GUIStyle(title) { fontSize = 18, fontStyle = FontStyle.Normal };
            panelName = new GUIStyle(title) { fontSize = 20, wordWrap = true };
            panelText = new GUIStyle(small) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperCenter };
        }

        private void OnGUI()
        {
            Styles();
            float sw = Screen.width, sh = Screen.height;
            float k = sh / 1080f;
            if (phase == Phase.Rounds) return;
            if (phase == Phase.Idle)
            {
                if (Time.unscaledTime < waitingNoteUntil)
                {
                    small.fontSize = Mathf.RoundToInt(20 * k);
                    GUI.Label(new Rect(0, sh - 60 * k, sw, 40 * k), "ROUNDS cards are waiting for you... (starting ROUNDS)", small);
                }
                return;
            }
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), Gfx.White);
            GUI.color = Color.white;
            title.fontSize = Mathf.RoundToInt(44 * k);
            small.fontSize = Mathf.RoundToInt(22 * k);
            string who = Progress.PlayerPresent(1) ? "PLAYER " + (player + 1) + " - " : "";
            int left = Progress.Pending(player);
            GUI.Label(new Rect(0, 60 * k, sw, 60 * k), who + (gameOverPick ? "YOU LOST - PICK A CARD" : "PICK A CARD"), title);
            GUI.Label(new Rect(0, 120 * k, sw, 34 * k), left > 1 ? left + " picks left" : "last pick", small);
            if (phase == Phase.Offering)
            {
                GUI.Label(new Rect(0, sh / 2f, sw, 40 * k), "Shuffling ROUNDS cards...", small);
                return;
            }
            int n = ids.Length;
            float cw = Mathf.Min(300f * k, (sw - 80f * k) / n - 20f * k);
            float ch = cw * 1.4f;
            float total = n * cw + (n - 1) * 24f * k;
            float x0 = (sw - total) / 2f;
            float y0 = sh / 2f - ch / 2f + 30f * k;
            for (int i = 0; i < n; i++)
            {
                bool sel = i == selected;
                float grow = sel ? 1.1f : 1f;
                var r = new Rect(x0 + i * (cw + 24f * k) - cw * (grow - 1f) / 2f, y0 - ch * (grow - 1f) / 2f - (sel ? 20f * k : 0f), cw * grow, ch * grow);
                if (r.Contains(Event.current.mousePosition))
                {
                    if (Event.current.type == EventType.MouseMove || Event.current.type == EventType.Repaint) selected = i;
                    if (Event.current.type == EventType.MouseDown && Time.unscaledTime - shownAt > 0.35f) { Take(i); Event.current.Use(); return; }
                }
                DrawCard(ids[i], r, sel);
            }
            GUI.Label(new Rect(0, sh - 90 * k, sw, 40 * k), "Left / Right to choose, Jump or Enter (or click) to take it", small);
        }

        private void DrawCard(string id, Rect r, bool sel)
        {
            Texture2D face;
            CardData d;
            Plugin.Catalog.TryGetValue(id, out d);
            GUI.color = new Color(0.07f, 0.06f, 0.06f, 0.95f);
            GUI.DrawTexture(r, Gfx.White);
            if (sel)
            {
                GUI.color = new Color(1f, 0.8f, 0.2f, 1f);
                float t = Mathf.Max(3f, r.width / 70f);
                GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), Gfx.White);
                GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), Gfx.White);
                GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), Gfx.White);
                GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), Gfx.White);
            }
            GUI.color = Color.white;
            if (faces.TryGetValue(id, out face) && face != null)
            {
                GUI.DrawTexture(r, face, ScaleMode.ScaleToFit, true);
                return;
            }
            Color c = d != null ? Gfx.Hex(d.colorHex, new Color(0.15f, 0.15f, 0.15f)) : new Color(0.15f, 0.15f, 0.15f);
            GUI.color = c;
            GUI.DrawTexture(r, Gfx.White);
            GUI.color = Color.white;
            panelName.fontSize = Mathf.RoundToInt(r.width / 12f);
            panelText.fontSize = Mathf.RoundToInt(r.width / 19f);
            GUI.Label(new Rect(r.x + 8, r.y + r.height * 0.08f, r.width - 16, r.height * 0.15f), d != null ? d.name.ToUpperInvariant() : id, panelName);
            string body = d != null ? d.description + "\n\n" + string.Join("\n", d.stats) : "";
            GUI.Label(new Rect(r.x + 10, r.y + r.height * 0.28f, r.width - 20, r.height * 0.7f), body, panelText);
        }
    }

    /// <summary>Porkrind's card pack: coins for a pick of 5 ROUNDS cards into the current deck, a head start on the next
    /// attempt (wiped at the win like the rest of the deck).</summary>
    public class ShopPackOverlay : MonoBehaviour
    {
        private static readonly bool[] buying = new bool[2];
        private GUIStyle style;
        private string message = "";
        private float messageUntil;

        public static void ShopOpened()
        {
            buying[0] = buying[1] = false;
        }

        public static bool AllowsPick(int p) => buying[p];

        public static void PickDone()
        {
            buying[0] = buying[1] = false;
            SetShopInput(true);
        }

        private static void SetShopInput(bool on)
        {
            foreach (var sp in FindObjectsOfType<ShopScenePlayer>()) sp.enabled = on;
        }

        private void Update()
        {
            if (ShopScene.Current == null || PickOverlay.I == null || PickOverlay.I.Open) return;
            for (int p = 0; p < 2; p++)
                if (Progress.PlayerPresent(p) && ReadInput(p)) Buy(p);
        }

        public bool ReadInput(int p)
        {
            Rewired.Player rp = PlayerManager.GetPlayerInput(p == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
            if (rp != null && rp.GetButtonDown(Buttons.ShopPack)) return true;
            return p == 0 && Input.GetKeyDown(KeyCode.P);
        }

        public void Buy(int p)
        {
            PlayerId id = p == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo;
            int coins = PlayerData.Data.GetCurrency(id);
            if (coins < Rules.ShopPackCost)
            {
                Say("Not enough coins for a card pack (" + Rules.ShopPackCost + " needed)");
                AudioManager.Play("shop_cantpurchase");
                return;
            }
            if (!Progress.RoomInDeck(p))
            {
                Say("Your deck is full (" + (int)Tuning.deckCap + " cards): win a level first");
                AudioManager.Play("shop_cantpurchase");
                return;
            }
            if (!Plugin.Engine.Connected || (ArenaClient.RoundsRunning && !ArenaClient.Live))
            {
                Say("ROUNDS is still starting, try again in a moment");
                return;
            }
            PlayerData.Data.AddCurrency(id, -Rules.ShopPackCost);
            PlayerData.SaveCurrentFile();
            AudioManager.Play("shop_purchase");
            buying[p] = true;
            SetShopInput(false);
            Progress.AddPending(p, Rules.ShopPackPicks);
            Say("Card pack bought!");
        }

        private void Say(string m)
        {
            message = m;
            messageUntil = Time.unscaledTime + 2.5f;
        }

        private void OnGUI()
        {
            if (ShopScene.Current == null || (PickOverlay.I != null && PickOverlay.I.Open)) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                style.normal.textColor = Color.white;
            }
            float k = Screen.height / 1080f;
            style.fontSize = Mathf.RoundToInt(22 * k);
            string text = Time.unscaledTime < messageUntil ? message : "ROUNDS CARD PACK - " + Rules.ShopPackCost + " coins - press Lock (or P): pick 1 of 5 cards for your deck (yours until you beat a level)";
            GUI.Box(new Rect(Screen.width * 0.2f, 18 * k, Screen.width * 0.6f, 52 * k), text, style);
        }
    }
}
