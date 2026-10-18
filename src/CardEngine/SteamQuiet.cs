using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The hidden ROUNDS that Roundhead runs next to Cuphead never connects to Steam, so Steam only shows the player in Cuphead
    /// instead of flipping their friends' view between the two games. ROUNDS connects in one place, SteamManager.Awake
    /// (SteamAPI.Init); this skips that call and leaves ROUNDS's manager in place, uninitialised, as ROUNDS already handles a
    /// failed Init (it plays on and its Steam features check SteamManager.Initialized). The engine plays an offline sandbox
    /// and needs nothing from Steam. ROUNDS started normally is untouched: only the hidden engine (--roundhead-engine)
    /// applies this patch.
    /// </summary>
    public static class SteamQuiet
    {
        private static bool logged;

        public static void Apply()
        {
            try { new Harmony("gg.melty.roundhead.steamquiet").CreateClassProcessor(typeof(SteamQuietPatch)).Patch(); }
            catch (System.Exception e) { EnginePlugin.L.LogWarning("steam: could not keep the hidden ROUNDS off Steam: " + e.Message); }
        }

        [HarmonyPatch(typeof(SteamManager), "Awake")]
        public static class SteamQuietPatch
        {
            [HarmonyPrefix]
            public static bool SkipSteamInit(SteamManager __instance)
            {
                if (!EnginePlugin.Hidden) return true;
                if (Traverse.Create(typeof(SteamManager)).Field("s_instance").GetValue() != null)
                {
                    Object.Destroy(__instance.gameObject);
                    return false;
                }
                Traverse.Create(typeof(SteamManager)).Field("s_instance").SetValue(__instance);
                Object.DontDestroyOnLoad(__instance.gameObject);
                if (!logged) { logged = true; EnginePlugin.L.LogInfo("steam: the hidden ROUNDS stays off Steam (SteamAPI.Init skipped); Steam shows Cuphead only"); }
                return false;
            }
        }
    }
}
