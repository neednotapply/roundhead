using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// A ROUNDS face in its own colours (sheets/hooks.json e_face_colours). On a player, ROUNDS draws the face's parts with
    /// its soft-shadow material (SFUnshadowed, shader Sprites/SFSoftShadow), lit by ROUNDS's own light pass; the engine's
    /// cameras (the arena's frames, the pick screen, the puppets, the face sent to Cuphead and the launcher's face pictures)
    /// have no such pass, so the parts came out dark grey, the colours of hats, eyes and details lost. They are drawn with
    /// Unity's plain sprite material instead, in the colours ROUNDS gives them (as ROUNDS's face menus show them).
    /// </summary>
    public static class FaceLook
    {
        private static Material unlit;

        public static Material Unlit
        {
            get
            {
                if (unlit == null)
                {
                    var shader = Shader.Find("Sprites/Default");
                    if (shader != null) unlit = new Material(shader) { name = "Roundhead face (unlit)" };
                }
                return unlit;
            }
        }

        public static void Apply(CharacterCreatorItemEquipper eq)
        {
            if (eq == null || eq.itemParent == null) return;
            Apply(eq.itemParent);
        }

        /// <summary>Every face part under root (ROUNDS's own items, or copies of them) on the plain sprite material.</summary>
        public static void Apply(GameObject root)
        {
            if (root == null || Unlit == null) return;
            foreach (var r in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var m = r.sharedMaterial;
                if (m != null && m != unlit && m.shader != null && m.shader.name.Contains("SFSoftShadow")) r.sharedMaterial = unlit;
            }
        }
    }

    /// <summary>Every face ROUNDS equips in the hidden engine (the arena's players, the pick screen's face).</summary>
    public static class FaceLookPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(CharacterCreatorItemEquipper), "Equip")]
        public static void Equipped(CharacterCreatorItemEquipper __instance) => FaceLook.Apply(__instance);
    }
}
