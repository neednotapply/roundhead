using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Keeps Cuphead's own drawings of a ROUNDS-driven player off screen. The player object's sprites are switched off right
    /// before the cameras cull, every frame, so no Cuphead script, animation key or script order can show them for a frame
    /// (the hit animation, the invincibility flash, a revive). The separate objects Cuphead spawns with the character on them
    /// are hidden too: the death ghost (PlayerDeathEffect.cs:95-107, which also floats behind the game-over card), its
    /// explosion (LevelPlayerDeathEffect.cs:14-42) and the jump-in of a mid-level join (PlayerJoinEffect.cs:30-46). ROUNDS's
    /// own death burst (HealthHandler.cs:330) and revive pop-in (HealthHandler.cs:305) are the visuals; the co-op ghost the
    /// partner parries becomes a ROUNDS-style ghost (<see cref="RoundsGhost"/>) around Cuphead's own parry heart.
    /// </summary>
    public static class PlayerSprites
    {
        private struct Root { public Transform t, except; }

        private static readonly List<Root> roots = new List<Root>();
        private static readonly List<SpriteRenderer> extra = new List<SpriteRenderer>();
        private static readonly List<SpriteRenderer> buf = new List<SpriteRenderer>();
        private static bool hooked;
        private static int frame = -1;

        /// <summary>Every sprite under root stays off, except those under 'except' (the blob's own drawings).</summary>
        public static void Track(Transform root, Transform except)
        {
            if (root == null) return;
            Hook();
            Untrack(root);
            roots.Add(new Root { t = root, except = except });
            Apply();
        }

        public static void Untrack(Transform root)
        {
            for (int i = roots.Count - 1; i >= 0; i--) if (roots[i].t == root) roots.RemoveAt(i);
        }

        /// <summary>One sprite that stays off for as long as it exists.</summary>
        public static void Hide(SpriteRenderer r)
        {
            if (r == null) return;
            Hook();
            r.enabled = false;
            if (!extra.Contains(r)) extra.Add(r);
        }

        /// <summary>Every sprite under an effect object stays off.</summary>
        public static void HideAll(Component c)
        {
            if (c == null) return;
            c.GetComponentsInChildren(true, buf);
            for (int i = 0; i < buf.Count; i++) Hide(buf[i]);
            buf.Clear();
        }

        /// <summary>Cuphead's death ghost of a ROUNDS-driven player becomes the ROUNDS one: its character sprite and explosion
        /// puffs stay off (PlayerDeathEffect.cs:95-107) and a RoundsGhost rides on it. Once per effect; also for a ghost that
        /// was there before the blob (a player who starts a level dead, LevelPlayerController.StartDead) or one that never
        /// ran Init (a scene-placed TutorialPlayerDeathEffect), where the anchor may still be unset.</summary>
        public static void DressGhost(PlayerDeathEffect e, BlobController c)
        {
            if (c != null) DressGhost(e, c, c.index);
        }

        /// <summary>The same for a player drawn as the ROUNDS character without a blob (a plane level's <see cref="PlaneBlob"/>).</summary>
        public static void DressGhost(PlayerDeathEffect e, BlobController c, int index)
        {
            if (e == null || e.GetComponent<RoundsGhost>() != null) return;
            foreach (string f in new[] { "cuphead", "mugman", "chalice", "effect", "chaliceEffect" }) Hide(Reflect.Get<SpriteRenderer>(e, f));
            RoundsGhost.Attach(e, Reflect.Get<SpriteRenderer>(e, "spriteRenderer"), c, index);
        }

        /// <summary>The ghosts already in the level for this player (one that started the level dead).</summary>
        public static void DressGhosts(BlobController c)
        {
            if (c == null || c.player == null) return;
            foreach (var e in Object.FindObjectsOfType<PlayerDeathEffect>())
                if (e != null && Reflect.Get(e, "playerId") is PlayerId id && id == c.player.id) DressGhost(e, c);
        }

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Camera.onPreCull += OnPreCull;
        }

        /// <summary>Once per frame, before the first camera culls: after every Update, LateUpdate and animation.</summary>
        private static void OnPreCull(Camera cam)
        {
            if (Time.frameCount == frame) return;
            frame = Time.frameCount;
            Apply();
        }

        public static void Apply()
        {
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                var r = roots[i];
                if (r.t == null) { roots.RemoveAt(i); continue; }
                r.t.GetComponentsInChildren(true, buf);
                for (int k = 0; k < buf.Count; k++)
                {
                    var s = buf[k];
                    if (s.enabled && (r.except == null || !s.transform.IsChildOf(r.except))) s.enabled = false;
                }
                buf.Clear();
            }
            for (int i = extra.Count - 1; i >= 0; i--)
            {
                var s = extra[i];
                if (s == null) { extra.RemoveAt(i); continue; }
                if (s.enabled) s.enabled = false;
            }
        }
    }

    /// <summary>A UI Image that takes another Image's alpha every frame (the game-over timeline dot under Cuphead's own fade-in).</summary>
    public class UiAlphaFollower : MonoBehaviour
    {
        private UnityEngine.UI.Image src, dst;

        public void Init(UnityEngine.UI.Image source, UnityEngine.UI.Image target) { src = source; dst = target; LateUpdate(); }

        private void LateUpdate()
        {
            if (src == null || dst == null) return;
            dst.color = new Color(1f, 1f, 1f, src.color.a);
        }
    }

    /// <summary>
    /// A ROUNDS-style ghost in place of Cuphead's dead-player ghost: the player's blob (team or skin colour, ROUNDS face),
    /// see-through and bobbing where Cuphead's ghost sprite is, inside a pulsing ring in Cuphead's parry pink, so a co-op
    /// partner still sees what to parry; Cuphead's parry heart under it stays its own. It lives on the PlayerDeathEffect and
    /// goes with it (parry, steal, game over).
    /// </summary>
    public class RoundsGhost : MonoBehaviour
    {
        /// <summary>The ghost hovers this many blob diameters above Cuphead's ghost anchor (the sprite's pivot is at the feet).</summary>
        private const float Lift = 0.55f;

        /// <summary>Cuphead's parry pink (its pink objects are the parryable ones).</summary>
        private static readonly Color ParryPink = new Color(1f, 0.5f, 0.72f, 1f);

        private Transform anchor, holder, face;
        private SpriteRenderer anchorR, outline, fill, ring;
        private readonly List<SpriteRenderer> all = new List<SpriteRenderer>();
        private readonly List<Color> baseColors = new List<Color>();
        private float d, t;

        public static RoundsGhost Attach(Component effect, SpriteRenderer anchor, BlobController c, int index)
        {
            var g = effect.gameObject.AddComponent<RoundsGhost>();
            g.Init(anchor, c, index);
            return g;
        }

        private void Init(SpriteRenderer a, BlobController c, int index)
        {
            anchorR = a;
            anchor = a != null ? a.transform : transform;
            d = c != null ? c.body.Diameter : Tuning.blobDiameter;
            holder = new GameObject("RoundsGhost").transform;
            holder.SetParent(transform, false);
            SkinData skin = Plugin.Skins[index];
            Color col, shade;
            BlobBody.Colors(index, skin, out col, out shade);
            ring = Make("parryRing", ParryPink);
            ring.sprite = Gfx.Ring();
            outline = Make("outline", shade);
            fill = Make("fill", col);
            face = new GameObject("face").transform;
            face.SetParent(holder, false);
            BlobBody.BuildFace(face, skin, all);
            foreach (var r in all) baseColors.Add(r.color);
            LateUpdate();
        }

        private SpriteRenderer Make(string name, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(holder, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = Gfx.Disc();
            r.color = c;
            all.Add(r);
            return r;
        }

        private void LateUpdate()
        {
            if (holder == null) return;
            Transform a = anchor != null ? anchor : transform;
            // Its own clock: still while Cuphead's pause menu is up, running through the game-over card (as Cuphead's ghost does).
            if (PauseManager.state != PauseManager.State.Paused) t += Time.deltaTime;
            float bob = Mathf.Sin(t * 3f) * d * 0.06f, wob = 1f + 0.04f * Mathf.Sin(t * 5f);
            holder.position = a.position + new Vector3(0f, d * Lift + bob, 0f);
            ring.transform.localScale = Vector3.one * d * (1.35f + 0.08f * Mathf.Sin(t * 4f));
            outline.transform.localScale = new Vector3(d * 1.08f * wob, d * 1.08f / wob, 1f);
            fill.transform.localScale = new Vector3(d * wob, d / wob, 1f);
            face.localScale = new Vector3(d, d, 1f);
            face.localPosition = new Vector3(0f, d * 0.05f, 0f);
            float alpha = Tuning.ghostAlpha * (0.9f + 0.1f * Mathf.Sin(t * 4f));
            int layer = anchorR != null ? anchorR.sortingLayerID : 0, order = anchorR != null ? anchorR.sortingOrder : 0;
            for (int i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (r == null) continue;
                r.sortingLayerID = layer;
                r.sortingOrder = order + i;
                Color c = baseColors[i];
                c.a *= alpha;
                r.color = c;
            }
        }
    }
}
