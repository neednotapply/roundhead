using System;
using HarmonyLib;

namespace CupRounds
{
    /// <summary>
    /// How Roundhead runs Cuphead's campaign around the levels (sheets/hooks.json, h_single_slot, h_drop_in, h_skip_cutscene):
    /// one save slot (the title's Start goes straight into slot 1; slots 2 and 3 are left untouched on disk), drop-in
    /// two-player play (player 2 joins the moment they press a button, with no "join?" prompt pausing the game), and no
    /// storybook cutscenes: each one is replaced by the scene it leads to, and a new game starts on the world map.
    /// </summary>
    public static class GameFlow
    {
        private const int SingleSlot = 0;

        /// <summary>After SlotSelectScreen.UpdateMainMenu: Start opened the slot list (SlotSelectScreen.cs:316-321, which also
        /// set up the three slots); slot 1 is entered at once instead (EnterGame, SlotSelectScreen.cs:475), with the
        /// character choice the slot was saved with.</summary>
        public static void EnterSingleSlot(SlotSelectScreen screen)
        {
            if (PlayerData.inGame || Reflect.EnumName(screen, "state") != "SlotSelect") return;
            Reflect.Set(screen, "_slotSelection", SingleSlot);
            Plugin.L.LogInfo("title: Start goes straight into save slot " + (SingleSlot + 1));
            Reflect.Call(screen, "EnterGame");
        }

        /// <summary>Prefix on PlayerManager.SetPlayerCanJoin: wherever Cuphead lets player 2 join (the map, a level under
        /// way), they join on their first press (PlayerManager.Update, PlayerManager.cs:255-340) instead of first getting
        /// the interrupting "join?" prompt.</summary>
        public static void DropIn(ref bool promptBeforeJoin)
        {
            promptBeforeJoin = false;
        }

        /// <summary>
        /// Prefix on SceneLoader.LoadScene (every scene and level load ends there, SceneLoader.cs:164-193): a storybook
        /// cutscene is swapped for where it would lead. Cutscene.Load stores that target before loading the cutscene
        /// (Cutscene.cs:31-50, played out in OnCutsceneOver); the two loaded directly have their outcome here. The opening
        /// storybook leads to the world map, not Elder Kettle's house (the tutorial stays reachable from the house on the
        /// map); the Devil's offer goes straight to his fight (the refusal, DevilCutscene.refuse_devil_cr); King Dice's
        /// welcome goes to his palace when every contract is in, else back to the map (KingDiceCutscene.Awake).
        /// </summary>
        public static void SkipCutscene(ref Scenes scene)
        {
            string name = scene.ToString();
            if (!name.StartsWith("scene_cutscene_")) return;
            Scenes from = scene;
            try
            {
                switch (scene)
                {
                    case Scenes.scene_cutscene_intro:
                        scene = Scenes.scene_map_world_1;
                        break;
                    case Scenes.scene_cutscene_devil:
                        scene = LevelScene(Levels.Devil);
                        break;
                    case Scenes.scene_cutscene_kingdice:
                        bool contracts = PlayerData.Data.CheckLevelsHaveMinDifficulty(Level.world1BossLevels, Level.Mode.Normal)
                            && PlayerData.Data.CheckLevelsHaveMinDifficulty(Level.world2BossLevels, Level.Mode.Normal)
                            && PlayerData.Data.CheckLevelsHaveMinDifficulty(Level.world3BossLevels, Level.Mode.Normal);
                        scene = contracts ? Scenes.scene_level_dice_palace_main : PlayerData.Data.CurrentMap;
                        break;
                    default:
                        var t = Traverse.Create(typeof(Cutscene));
                        if (t.Field("mode").GetValue<Cutscene.Mode>() == Cutscene.Mode.Level) scene = LevelScene(t.Field("levelAfterCutscene").GetValue<Levels>());
                        else scene = t.Field("sceneAfterCutscene").GetValue<Scenes>();
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("cutscene " + name + " could not be skipped: " + e.Message);
                scene = from;
                return;
            }
            Plugin.L.LogInfo("cutscene " + name + " skipped: " + scene);
        }

        /// <summary>A level's scene, with SceneLoader.CurrentLevel set as SceneLoader.LoadLevel does (SceneLoader.cs:189-193).</summary>
        private static Scenes LevelScene(Levels level)
        {
            AccessTools.Property(typeof(SceneLoader), "CurrentLevel").SetValue(null, level, null);
            return (Scenes)Enum.Parse(typeof(Scenes), LevelProperties.GetLevelScene(level));
        }
    }
}
