using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// ROUNDS's gun is black. In ROUNDS itself the gun's sprites (Handle, Barrel) stay switched off: what you see is the
    /// gun's soft shadow (an SFPolygon under ROUNDS's soft-shadow renderer) over the dark screen, and the team-coloured
    /// skin particles that drift into its masks. Our frame has no shadow pass and sits on Cuphead's bright levels, so the
    /// gun sprites are drawn in gun black (sheets/tuning.json gunShade) with their masks off, for the arena's players
    /// (after ROUNDS colours a gun) and for the world-map puppet.
    /// </summary>
    public static class GunLook
    {
        public static void Blacken(Holdable gun, int? sortingOrder = null)
        {
            if (gun == null) return;
            var shade = new Color(Tuning.gunShade, Tuning.gunShade, Tuning.gunShade, 1f);
            foreach (var m in gun.GetComponentsInChildren<SpriteMask>(true))
            {
                var sr = m.GetComponent<SpriteRenderer>();
                if (sr == null) continue;
                m.enabled = false;   // no team-coloured skin particles inside the gun
                sr.enabled = true;
                sr.color = shade;
                if (sortingOrder.HasValue) sr.sortingOrder = sortingOrder.Value;
            }
        }
    }

    /// <summary>sheets/hooks.json e_gun_black.</summary>
    public static class GunLookPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Holdable), "SetTeamColors")]
        public static void GunBlack(Holdable __instance)
        {
            GunLook.Blacken(__instance);
        }
    }
}
