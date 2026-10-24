using HarmonyLib;
using SoundImplementation;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The hidden ROUNDS never takes the player's screen. Batch mode still makes a window (Unity.BatchModeWindow), blank, and
    /// ROUNDS's Optionshandler applies the player's saved screen mode at its start (Optionshandler.ApplyOptions:
    /// Screen.SetResolution with their resolution, usually full screen): that showed the blank window over the whole screen,
    /// in front of Cuphead, and it could take Cuphead's keyboard focus. The hidden engine (and the launcher's face drawing)
    /// skips only that call; vSync and ROUNDS's volumes are applied as ROUNDS does. ROUNDS started normally is untouched.
    /// <see cref="WindowHider"/> hides the window again should anything else show it.
    /// </summary>
    public static class ScreenQuiet
    {
        private static bool logged;

        public static void Apply()
        {
            try { new Harmony("gg.melty.roundhead.screenquiet").CreateClassProcessor(typeof(ScreenQuietPatch)).Patch(); }
            catch (System.Exception e) { EnginePlugin.L.LogWarning("window: could not keep ROUNDS's screen mode off: " + e.Message); }
        }

        [HarmonyPatch(typeof(Optionshandler), "ApplyOptions")]
        public static class ScreenQuietPatch
        {
            /// <summary>Optionshandler.ApplyOptions (Optionshandler.cs:91-96) without its Screen.SetResolution.</summary>
            [HarmonyPrefix]
            public static bool NoScreenMode()
            {
                if (!EnginePlugin.Hidden) return true;
                QualitySettings.vSyncCount = Optionshandler.vSync ? 1 : 0;
                if (SoundVolumeManager.Instance != null)
                    SoundVolumeManager.Instance.SetAudioMixerVolumes(Optionshandler.vol_Master, Optionshandler.vol_Music, Optionshandler.vol_Sfx);
                if (!logged) { logged = true; EnginePlugin.L.LogInfo("window: ROUNDS's saved screen mode is not applied (the hidden ROUNDS keeps its window out of sight)"); }
                return false;
            }
        }
    }
}
