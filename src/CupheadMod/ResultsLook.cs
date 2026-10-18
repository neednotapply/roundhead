using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The results screen after a won level (WinScreen) shows the players' real ROUNDS characters where Cuphead, Mugman or
    /// Ms. Chalice stand, without the hand-lettered name under them. WinScreen.Awake switches on one layout group for the 1- or
    /// 2-player screen (WinScreen.cs:245-356); it holds the board, the "THE RESULTS!" title and one object per character
    /// (named Cuphead, Mugman or Chalice...), each with its portrait, a blink layer and its NAME sprite. Only those character
    /// objects' sprites stay off (<see cref="PlayerSprites"/>); each player's ROUNDS puppet stands on its own portrait.
    /// </summary>
    public class ResultsLook : MonoBehaviour
    {
        private static readonly string[] Groups =
        {
            "OnePlayerCuphead", "OnePlayerMugman", "OnePlayerChalice",
            "TwoPlayerCupheadMugman", "TwoPlayerMugmanCuphead", "TwoPlayerChaliceCuphead",
            "TwoPlayerCupheadChalice", "TwoPlayerMugmanChalice", "TwoPlayerChaliceMugman",
        };

        private class Character
        {
            public Transform root;
            public SpriteRenderer portrait;
            public SpriteRenderer puppet;
            public int slot;
        }

        private readonly List<Character> chars = new List<Character>();
        private float t;

        public static void Attach(WinScreen screen)
        {
            if (screen == null || screen.GetComponent<ResultsLook>() != null) return;
            GameObject group = null;
            foreach (string f in Groups)
            {
                var g = Reflect.Get<GameObject>(screen, f);
                if (g != null && g.activeSelf) { group = g; break; }
            }
            if (group == null) { Plugin.L.LogWarning("results: no layout group is on; Cuphead's portraits stay"); return; }
            var look = screen.gameObject.AddComponent<ResultsLook>();
            var data = Level.ScoringData;
            bool p1Chalice = data != null && data.player1IsChalice;
            bool two = PlayerManager.Multiplayer;
            foreach (Transform child in group.GetComponentsInChildren<Transform>(true))
            {
                string kind = Kind(child.name);
                if (kind == null || (child.parent != null && Kind(child.parent.name) != null)) continue;
                var portrait = child.GetComponent<SpriteRenderer>();
                if (portrait == null) continue;
                var c = new Character { root = child, portrait = portrait };
                var go = new GameObject("RoundsResults" + kind); // a scene root: world scale, gone with the scene
                c.puppet = go.AddComponent<SpriteRenderer>();
                c.puppet.enabled = false;
                look.chars.Add(c);
                PlayerSprites.Track(child, null);
            }
            // Which player each portrait stands for: one player is always slot 0; with two, Ms. Chalice is player 1 when
            // player1IsChalice, and Cuphead is player 1 unless player 1 plays Mugman (PlayerManager.player1IsMugman).
            Character chalice = look.chars.Find(c => Kind(c.root.name) == "Chalice");
            foreach (var c in look.chars)
            {
                if (!two) { c.slot = 0; continue; }
                string kind = Kind(c.root.name);
                if (chalice != null) c.slot = (c == chalice) == p1Chalice ? 0 : 1;
                else c.slot = (kind == "Mugman") == PlayerManager.player1IsMugman ? 0 : 1;
            }
            if (TestMode.Active)
                foreach (var c in look.chars) Plugin.L.LogInfo("results: " + group.name + "/" + c.root.name + " -> player " + (c.slot + 1));
            TestMode.Report("results-rounds-look", look.chars.Count == (two ? 2 : 1),
                "results screen: " + look.chars.Count + " character portrait(s) with their names swapped for the ROUNDS character(s) in " + group.name);
        }

        /// <summary>"Cuphead", "Mugman" or "Chalice" for a character object's name, else null.</summary>
        private static string Kind(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.ToLowerInvariant();
            if (n.StartsWith("cuphead")) return "Cuphead";
            if (n.StartsWith("mugman")) return "Mugman";
            if (n.StartsWith("chalice") || n.StartsWith("ms_chalice") || n.StartsWith("mschalice")) return "Chalice";
            return null;
        }

        /// <summary>Where a switched-off portrait is drawn (its sprite's bounds in the world).</summary>
        private static float CentreX(SpriteRenderer r) => r.transform.TransformPoint(r.sprite.bounds.center).x;

        private void LateUpdate()
        {
            t += Time.deltaTime;
            // The board stands between two portraits, or right of a single one.
            float boardX = 0f;
            int n = 0;
            foreach (var c in chars) if (c.portrait != null && c.portrait.sprite != null) { boardX += CentreX(c.portrait); n++; }
            boardX = n > 1 ? boardX / n : float.PositiveInfinity;
            foreach (var c in chars)
            {
                if (c.root == null || c.portrait == null || c.puppet == null) continue;
                // Idle, facing the board; ROUNDS animates its own breathing, eyes and gun sway.
                float face = c.portrait.sprite == null || boardX > CentreX(c.portrait) ? 1f : -1f;
                Puppets.Send(c.slot, new Request { ax = face, ay = -0.1f + 0.08f * Mathf.Sin(t * 1.3f + c.slot), grounded = true });
                Sprite sp = c.portrait.sprite;
                bool live = Puppets.Live(c.slot) && Puppets.SpriteFor(c.slot) != null && sp != null && c.root.gameObject.activeInHierarchy;
                c.puppet.enabled = live;
                if (!live) continue;
                // The portrait's drawn area (its sprite, switched off, still has its bounds).
                Bounds sb = sp.bounds;
                Vector3 centre = c.portrait.transform.TransformPoint(sb.center);
                float h = sb.size.y * Mathf.Abs(c.portrait.transform.lossyScale.y);
                float d = h * Tuning.resultsSize;
                float k = d / Tuning.blobDiameter;
                c.puppet.sprite = Puppets.SpriteFor(c.slot);
                c.puppet.transform.position = new Vector3(centre.x, centre.y - h * 0.5f + d * 1.1f, c.portrait.transform.position.z);
                c.puppet.transform.localScale = new Vector3(k, k, 1f);
                c.puppet.sortingLayerID = c.portrait.sortingLayerID;
                c.puppet.sortingOrder = c.portrait.sortingOrder + 1;
            }
        }
    }
}
