using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// ROUNDS's keys everywhere in Cuphead for player 1 (sheets/controls.json): WASD walk the world map and move through
    /// menus, Space accepts. Added on top of Rewired's answers at runtime, so Cuphead's saved key bindings never change.
    /// </summary>
    public static class InputPatches
    {
        private static bool patched;
        private static float nextTry;

        // CupheadButton ids (Cuphead's CupheadButton enum)
        private const int MoveH = 0, MoveV = 1, Jump = 2, Accept = 13, MenuUp = 16, MenuLeft = 18, MenuDown = 19, MenuRight = 20, MenuH = 22, MenuV = 23;

        public static void EnsurePatched()
        {
            if (patched || Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 1f;
            Rewired.Player p;
            try { p = PlayerManager.GetPlayerInput(PlayerId.PlayerOne); } catch { return; }
            if (p == null) return;
            patched = true;
            var h = new Harmony(Plugin.Guid + ".keys");
            Type t = p.GetType();
            Patch(h, t, "GetAxis", nameof(AxisPost));
            Patch(h, t, "GetAxisRaw", nameof(AxisPost));
            Patch(h, t, "GetButton", nameof(ButtonPost));
            Patch(h, t, "GetButtonDown", nameof(ButtonDownPost));
            Patch(h, t, "GetNegativeButtonDown", nameof(NegativeDownPost));
            Patch(h, t, "GetNegativeButton", nameof(NegativePost));
        }

        private static void Patch(Harmony h, Type t, string name, string post)
        {
            MethodInfo m = null;
            for (Type bt = t; bt != null && m == null; bt = bt.BaseType)
            {
                var c = bt.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(int) }, null);
                if (c != null && !c.IsAbstract) m = c;
            }
            if (m == null) { Plugin.L.LogWarning("keys: no Rewired " + name); return; }
            try { h.Patch(m, postfix: new HarmonyMethod(typeof(InputPatches).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic))); }
            catch (Exception e) { Plugin.L.LogWarning("keys: could not patch " + name + ": " + e.Message); }
        }

        private static bool Mine(Rewired.Player p) => p != null && p.id == 0 && Application.isFocused;

        private static float H => (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        private static float V => (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);

        private static void AxisPost(Rewired.Player __instance, int __0, ref float __result)
        {
            if (!Mine(__instance) || __result != 0f) return;
            if (__0 == MoveH || __0 == MenuH) __result = H;
            else if (__0 == MoveV || __0 == MenuV) __result = V;
        }

        private static void ButtonPost(Rewired.Player __instance, int __0, ref bool __result)
        {
            if (__result || !Mine(__instance)) return;
            switch (__0)
            {
                case MenuUp: __result = Input.GetKey(KeyCode.W); break;
                case MenuDown: __result = Input.GetKey(KeyCode.S); break;
                case MenuLeft: __result = Input.GetKey(KeyCode.A); break;
                case MenuRight: __result = Input.GetKey(KeyCode.D); break;
                case MoveH: case MenuH: __result = Input.GetKey(KeyCode.D); break;
                case MoveV: case MenuV: __result = Input.GetKey(KeyCode.W); break;
                case Accept: case Jump: __result = !InLevelArena && Input.GetKey(KeyCode.Space); break;
            }
        }

        private static void ButtonDownPost(Rewired.Player __instance, int __0, ref bool __result)
        {
            if (__result || !Mine(__instance)) return;
            switch (__0)
            {
                case MenuUp: __result = Input.GetKeyDown(KeyCode.W); break;
                case MenuDown: __result = Input.GetKeyDown(KeyCode.S); break;
                case MenuLeft: __result = Input.GetKeyDown(KeyCode.A); break;
                case MenuRight: __result = Input.GetKeyDown(KeyCode.D); break;
                case MoveH: case MenuH: __result = Input.GetKeyDown(KeyCode.D); break;
                case MoveV: case MenuV: __result = Input.GetKeyDown(KeyCode.W); break;
                case Accept: case Jump: __result = !InLevelArena && Input.GetKeyDown(KeyCode.Space); break;
            }
        }

        private static void NegativeDownPost(Rewired.Player __instance, int __0, ref bool __result)
        {
            if (__result || !Mine(__instance)) return;
            if (__0 == MoveH || __0 == MenuH) __result = Input.GetKeyDown(KeyCode.A);
            else if (__0 == MoveV || __0 == MenuV) __result = Input.GetKeyDown(KeyCode.S);
        }

        private static void NegativePost(Rewired.Player __instance, int __0, ref bool __result)
        {
            if (__result || !Mine(__instance)) return;
            if (__0 == MoveH || __0 == MenuH) __result = Input.GetKey(KeyCode.A);
            else if (__0 == MoveV || __0 == MenuV) __result = Input.GetKey(KeyCode.S);
        }

        /// <summary>In levels the ROUNDS player reads Space itself; Cuphead's own jump stays out of it.</summary>
        private static bool InLevelArena => ArenaClient.Mode == 1;
    }
}
