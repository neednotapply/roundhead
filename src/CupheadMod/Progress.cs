using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CupRounds
{
    /// <summary>A Cuphead save slot's ROUNDS decks (sheets/progression.json). Stored beside, never inside, Cuphead's own saves.</summary>
    [Serializable]
    public class SlotSave
    {
        /// <summary>The rules this save was written under; a save from older rules starts over (Json.Read leaves a missing
        /// field at 0).</summary>
        public int rules;
        /// <summary>Each player's current deck: the cards picked since their last won level, in pick order.</summary>
        public string[] owned1 = new string[0];
        public string[] owned2 = new string[0];
        /// <summary>Picks granted and not made yet (a loss pick left on the game-over card by quitting, a shop pack).</summary>
        public int pending1;
        public int pending2;
        /// <summary>Murder stacks picked and not spent yet (one per PCE Murder card; EnemyStatus spends them at the next level).</summary>
        public int murder1;
        public int murder2;
    }

    /// <summary>
    /// ROUNDS's loop on Cuphead's campaign (sheets/progression.json): a level is a match. Every game over gives each player a
    /// card pick on the game-over card (the loser picks), the deck is kept through retries and trips to the map, and winning
    /// the level wipes it: the next level starts with the plain gun again. Nothing is permanent.
    /// </summary>
    public static class Progress
    {
        public const int RulesVersion = 2;

        private static readonly Dictionary<string, SlotSave> slots = new Dictionary<string, SlotSave>();
        public static readonly BuildStats[] Builds = { new BuildStats(), new BuildStats() };
        public static readonly string[][] Partial = { new string[0], new string[0] };
        private static readonly string[] builtFor = { null, null };

        public static string SlotKey => TestMode.Active ? "test" : "slot" + PlayerData.CurrentSaveFileIndex;

        /// <summary>Test runs keep their cards in their own folder (emptied when a run starts), never in the player's saves.</summary>
        private static string Folder => Path.Combine(Proto.DataDir(), TestMode.Active ? "saves-test" : "saves");

        private static string PathFor(string key) => Path.Combine(Folder, key + ".json");

        public static void ClearTestSaves()
        {
            try
            {
                string d = Path.Combine(Proto.DataDir(), "saves-test");
                if (Directory.Exists(d)) foreach (string f in Directory.GetFiles(d, "*.json")) File.Delete(f);
            }
            catch (System.Exception e) { Plugin.L.LogWarning("could not clear test saves: " + e.Message); }
        }

        public static void Load()
        {
            slots.Clear();
            if (TestMode.Active)
                slots["test"] = new SlotSave { rules = RulesVersion };
        }

        public static SlotSave Current
        {
            get
            {
                string key = SlotKey;
                SlotSave s;
                if (slots.TryGetValue(key, out s)) return s;
                s = new SlotSave { rules = RulesVersion };
                try
                {
                    string p = PathFor(key);
                    if (File.Exists(p))
                    {
                        s = Json.Read<SlotSave>(File.ReadAllText(p)) ?? new SlotSave { rules = RulesVersion };
                        if (s.rules < RulesVersion)
                        {
                            // Cards earned under the old rules (kept for good) do not carry into a deck that is meant to be lost.
                            Plugin.L.LogInfo("ROUNDS save " + key + " is from older rules: starting over with an empty deck");
                            s = new SlotSave { rules = RulesVersion };
                        }
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("could not read " + key + ": " + e.Message); }
                slots[key] = s;
                return s;
            }
        }

        public static void Save()
        {
            if (TestMode.Active) return;
            try
            {
                string p = PathFor(SlotKey);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, Json.Write(Current));
            }
            catch (Exception e) { Plugin.L.LogWarning("could not save decks: " + e.Message); }
        }

        /// <summary>The player's current deck (the cards picked since their last won level).</summary>
        public static string[] Owned(int player) => player == 0 ? Current.owned1 : Current.owned2;

        public static int Pending(int player) => player == 0 ? Current.pending1 : Current.pending2;

        public static void AddPending(int player, int picks)
        {
            if (picks <= 0) return;
            if (player == 0) Current.pending1 += picks; else Current.pending2 += picks;
            Save();
        }

        public static void AddCard(int player, string id)
        {
            var list = new List<string>(Owned(player)) { id };
            if (player == 0) { Current.owned1 = list.ToArray(); Current.pending1 = Math.Max(0, Current.pending1 - 1); }
            else { Current.owned2 = list.ToArray(); Current.pending2 = Math.Max(0, Current.pending2 - 1); }
            if (id == MurderCard) { if (player == 0) Current.murder1++; else Current.murder2++; }
            Save();
            RefreshBuild(player);
            // ROUNDS's own pick already gave its player the card; a card from anywhere else (the fallback pick screen, a dev
            // deal) reaches it through the deck sync, which only applies what the player lacks.
            ArenaClient.SyncCards(player);
        }

        /// <summary>PCE's Murder: Unbound names a custom card's object "__" + mod + "__" + title (CustomCard.cs:169, 230), and
        /// the card id is that name (EnginePlugin.Id, EnginePlugin.cs:261; devdata cards-dump.json).</summary>
        public const string MurderCard = "__PCE__Murder";

        /// <summary>Unspent Murder stacks. Cuphead owns them: owning the card is not enough, since the deck is re-applied to
        /// every new ROUNDS player.</summary>
        public static int Murders(int player) => Math.Max(0, player == 0 ? Current.murder1 : Current.murder2);

        public static void SpendMurders(int player)
        {
            if (player == 0) Current.murder1 = 0; else Current.murder2 = 0;
            Save();
        }

        public static bool PlayerPresent(int player)
        {
            return player == 0 || PlayerManager.Multiplayer;
        }

        /// <summary>The deck's cap (tuning deckCap; 0 = none, ROUNDS's way): true when this player can take another card.</summary>
        public static bool RoomInDeck(int player)
        {
            int cap = (int)Tuning.deckCap;
            return cap <= 0 || Owned(player).Length + Pending(player) < cap;
        }

        /// <summary>The levels the loop plays on: ROUNDS's players are in it (ground levels and plane levels alike), and it is
        /// a level, not the tutorial or the Tower of Power gauntlet.</summary>
        private static bool TakesPart(Level level)
        {
            return level != null && !Level.IsTowerOfPower && level.LevelType != Level.Type.Tutorial && ArenaSeat.Seats.Count > 0;
        }

        /// <summary>A lost level (every player dead, or a Mausoleum urn lost): each player's loss pick, made on the game-over
        /// card before Retry or Exit (sheets/progression.json, loss).</summary>
        public static void OnLevelLost(Level level)
        {
            if (!TakesPart(level)) return;
            var given = new List<string>();
            for (int p = 0; p < 2; p++)
            {
                if (!PlayerPresent(p)) continue;
                if (!RoomInDeck(p)) { Plugin.L.LogInfo("player " + (p + 1) + "'s deck is full (" + (int)Tuning.deckCap + "): no loss pick"); continue; }
                AddPending(p, Rules.LossPicks);
                given.Add("player " + (p + 1));
            }
            if (given.Count > 0) Plugin.L.LogInfo("level " + level.CurrentLevel + " lost: " + Rules.LossPicks + " card pick(s) for " + string.Join(" and ", given.ToArray()) + " on the game-over card");
        }

        /// <summary>A won level ends the match: both decks are wiped and ROUNDS's players go back to the plain gun. A Dice
        /// Palace mini boss keeps them (Cuphead itself makes you redo the whole board on a loss); King Dice himself wipes.</summary>
        public static void OnLevelWon(Level level)
        {
            if (!TakesPart(level)) return;
            if (Level.IsDicePalace && !Level.IsDicePalaceMain) { Plugin.L.LogInfo("Dice Palace mini boss won: decks kept for the board"); return; }
            var had = new List<string>();
            for (int p = 0; p < 2; p++)
            {
                if (Owned(p).Length > 0) had.Add("player " + (p + 1) + " (" + Owned(p).Length + ")");
                // A loss pick never made (ROUNDS was away at the card) goes too: nothing outlives a win.
                if (p == 0) { Current.owned1 = new string[0]; Current.murder1 = 0; Current.pending1 = 0; } else { Current.owned2 = new string[0]; Current.murder2 = 0; Current.pending2 = 0; }
            }
            Save();
            // ROUNDS's players are reset when the level changes (ArenaClient.OnLevelChanged), not during the KO outro.
            for (int p = 0; p < 2; p++) RefreshBuild(p);
            Plugin.L.LogInfo("level " + level.CurrentLevel + " won: decks wiped" + (had.Count > 0 ? " for " + string.Join(", ", had.ToArray()) : " (they were empty)"));
        }

        /// <summary>Cuphead erased a save slot (PlayerData.ClearSlot): its ROUNDS decks go with it.</summary>
        public static void ClearSlot(int slot)
        {
            string key = "slot" + slot;
            slots.Remove(key);
            try
            {
                string p = Path.Combine(Path.Combine(Proto.DataDir(), "saves"), key + ".json");
                if (File.Exists(p)) File.Delete(p);
                Plugin.L.LogInfo("Cuphead save slot " + (slot + 1) + " erased: its ROUNDS decks too");
            }
            catch (Exception e) { Plugin.L.LogWarning("could not erase the ROUNDS decks of slot " + (slot + 1) + ": " + e.Message); }
        }

        public static void RefreshBuilds()
        {
            RefreshBuild(0);
            RefreshBuild(1);
        }

        public static void RefreshBuild(int player)
        {
            string[] owned = Owned(player);
            string key = SlotKey + ":" + string.Join("|", owned);
            if (builtFor[player] == key) return;
            Plugin.Engine.Build(owned, r =>
            {
                if (!r.ok) return;
                builtFor[player] = key;
                Builds[player] = r.stats ?? new BuildStats();
                Partial[player] = r.partial ?? new string[0];
                BlobController.RefreshAllBuilds();
                // cards-applied is ROUNDS's players holding the deck (ArenaClient.Follow), never this stand-in build.
            });
        }

        public static void ForgetBuilds()
        {
            builtFor[0] = builtFor[1] = null;
        }
    }
}
