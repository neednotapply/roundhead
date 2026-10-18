using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace CupRounds
{
    /// <summary>Renders a real ROUNDS card (frame, art, title, stat lines) with its own camera, in memory.</summary>
    public static class CardRenderer
    {
        private const int Layer = 31;
        private static Camera cam;

        /// <summary>Spawns every card at once (side by side, far from the menu), waits for their art to settle, then captures each.</summary>
        public static IEnumerator RenderMany(CardInfo[] cards, string[] results)
        {
            var spawned = new GameObject[cards.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                try
                {
                    spawned[i] = CardChoice.instance.AddCardVisual(cards[i], new Vector3(4000f + i * 60f, 4000f, 0f));
                    foreach (var col in spawned[i].GetComponentsInChildren<Collider2D>(true)) col.enabled = false;
                }
                catch (Exception e) { EnginePlugin.L.LogWarning("could not spawn card " + cards[i].cardName + ": " + e.Message); }
            }
            yield return null;
            yield return new WaitForSecondsRealtime(0.45f);
            for (int i = 0; i < cards.Length; i++)
            {
                if (spawned[i] == null) continue;
                try { results[i] = Capture(spawned[i], cards[i]); }
                catch (Exception e) { EnginePlugin.L.LogWarning("render " + cards[i].cardName + ": " + e.Message); }
                finally { UnityEngine.Object.Destroy(spawned[i]); }
            }
        }

        private static string Capture(GameObject go, CardInfo card)
        {
            RenderTexture rt = null;
            try
            {
                SetLayer(go.transform, Layer);
                Transform front = go.transform.Find("Canvas/Front") ?? FindDeep(go.transform, "Front");
                RectTransform rect = front != null ? front.GetComponent<RectTransform>() : go.GetComponentInChildren<Canvas>(true)?.GetComponent<RectTransform>();
                if (rect == null) { EnginePlugin.L.LogWarning("card " + card.cardName + " has no canvas"); return null; }
                if (cam == null)
                {
                    var cgo = new GameObject("Roundhead card camera");
                    UnityEngine.Object.DontDestroyOnLoad(cgo);
                    cam = cgo.AddComponent<Camera>();
                    cam.enabled = false;
                    cam.orthographic = true;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                    cam.cullingMask = 1 << Layer;
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 1000f;
                }
                foreach (var canvas in go.GetComponentsInChildren<Canvas>(true))
                    if (canvas.renderMode != RenderMode.WorldSpace) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 10f; }
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Vector3 right = (corners[3] - corners[0]).normalized, up = (corners[1] - corners[0]).normalized;
                float minX = 0f, maxX = Vector3.Distance(corners[0], corners[3]), minY = 0f, maxY = Vector3.Distance(corners[0], corners[1]);
                var vis = go.GetComponentInChildren<CardVisuals>(true);
                if (vis != null && vis.nameText != null)
                {
                    var tc = new Vector3[4];
                    vis.nameText.rectTransform.GetWorldCorners(tc);
                    foreach (var p in tc)
                    {
                        Vector3 d = p - corners[0];
                        minY = Mathf.Min(minY, Vector3.Dot(d, up));
                        maxY = Mathf.Max(maxY, Vector3.Dot(d, up));
                    }
                }
                float w = maxX - minX, h = maxY - minY;
                if (w < 0.001f || h < 0.001f) { w = 1f; h = 1.4f; }
                Vector3 center = corners[0] + right * (minX + maxX) / 2f + up * (minY + maxY) / 2f;
                int ph = 560;
                int pw = Mathf.Clamp(Mathf.RoundToInt(ph * w / h), 200, 900);
                rt = RenderTexture.GetTemporary(pw, ph, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt;
                cam.transform.position = center - rect.forward * 50f;
                cam.transform.rotation = Quaternion.LookRotation(rect.forward, rect.up);
                cam.orthographicSize = h / 2f * 1.06f;
                cam.aspect = pw / (float)ph;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string png = Convert.ToBase64String(tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                return png;
            }
            finally
            {
                if (cam != null) cam.targetTexture = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }

        private static void SetLayer(Transform t, int layer)
        {
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = layer;
        }
    }

    public struct FaceItem
    {
        public CharacterItem item;
        public Vector2 offset;
        public FaceItem(CharacterItem item, Vector2 offset) { this.item = item; this.offset = offset; }
    }

    /// <summary>
    /// Builds the player's saved ROUNDS face the way ROUNDS's CharacterCreatorItemEquipper does (same items, offsets
    /// and scales) and renders it with its own camera to a transparent square one blob across.
    /// </summary>
    public static class FaceRenderer
    {
        private const int Layer = 31;

        public static string Render(FaceItem[] items, float eqScale, float scaleM, Vector3 eqOffset, float diameter, Color dark)
        {
            var root = new GameObject("Roundhead face");
            root.transform.position = new Vector3(-8000f, 8000f, 0f);
            root.transform.localScale = Vector3.one * eqScale;
            RenderTexture rt = null;
            Camera cam = null;
            try
            {
                int order = 0;
                foreach (var fi in items)
                {
                    if (fi.item == null) continue;
                    GameObject obj = UnityEngine.Object.Instantiate(fi.item.gameObject, root.transform);
                    foreach (var mb in obj.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
                    obj.transform.localRotation = Quaternion.identity;
                    obj.transform.localScale = fi.item.scale * Vector3.one * scaleM;
                    obj.transform.localPosition = (fi.item.offset + fi.offset) * scaleM;
                    foreach (var r in obj.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        if (r.sprite == null && r.gameObject == obj) r.sprite = fi.item.sprite;
                        Color c = r.color;
                        if (c.r > 0.9f && c.g > 0.9f && c.b > 0.9f) r.color = new Color(dark.r, dark.g, dark.b, c.a);
                        r.sortingOrder = order++;
                    }
                    foreach (var t in obj.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
                }
                var cgo = new GameObject("Roundhead face camera");
                cam = cgo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = diameter / 2f;
                cam.aspect = 1f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << Layer;
                cam.transform.position = root.transform.position + eqOffset - Vector3.forward * 50f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 200f;
                rt = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string png = Convert.ToBase64String(tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                return png;
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; UnityEngine.Object.Destroy(cam.gameObject); }
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(root);
            }
        }
    }

    /// <summary>The player's ROUNDS look: team colour, saved face, gun and bullet, read from the running ROUNDS.</summary>
    public static class SkinExport
    {
        public static SkinData Export(int slot)
        {
            var data = new SkinData();
            try
            {
                PlayerSkin skin = PlayerSkinBank.GetPlayerSkinColors(slot);
                if (skin != null)
                {
                    data.color = ColorUtility.ToHtmlStringRGB(skin.color);
                    data.shade = ColorUtility.ToHtmlStringRGB(skin.backgroundColor);
                }
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("skin colours: " + e.Message); }

            GameObject prefab = PlayerPrefab();
            float diameter = 1f;
            Vector3 eqOffset = Vector3.zero;
            float eqScale = 1f, scaleM = 1f;
            if (prefab != null)
            {
                var circle = prefab.GetComponentInChildren<CircleCollider2D>(true);
                if (circle != null) diameter = circle.radius * 2f * Mathf.Abs(circle.transform.lossyScale.x);
                // The blob you see is the skin mask, a little bigger than the collider; size the face against it.
                float best = 0f;
                foreach (var m in prefab.GetComponentsInChildren<SpriteMask>(true))
                {
                    if (m.sprite == null) continue;
                    float sz = m.sprite.bounds.size.x * Mathf.Abs(m.transform.lossyScale.x);
                    EnginePlugin.L.LogInfo("  mask " + m.name + " sprite " + m.sprite.name + " size " + sz);
                    if (sz > best) best = sz;
                }
                foreach (var r in prefab.GetComponentsInChildren<SpriteRenderer>(true))
                    if (r.sprite != null) EnginePlugin.L.LogInfo("  sprite " + r.name + " " + r.sprite.name + " size " + (r.sprite.bounds.size.x * Mathf.Abs(r.transform.lossyScale.x)));
                if (best > diameter * 0.8f && best < diameter * 4f) diameter = best;
                var eq = prefab.GetComponentInChildren<CharacterCreatorItemEquipper>(true);
                if (eq != null)
                {
                    eqScale = Mathf.Abs(eq.transform.lossyScale.x);
                    scaleM = eq.scaleM;
                    eqOffset = eq.transform.position - prefab.transform.position;
                }
                EnginePlugin.L.LogInfo("player prefab: diameter " + diameter + ", face scale " + eqScale + " x " + scaleM + ", face offset " + eqOffset);
            }
            if (diameter <= 0.0001f) diameter = 1f;

            try
            {
                PlayerFace face = Face(slot);
                var loader = CharacterCreatorItemLoader.instance ?? Resources.FindObjectsOfTypeAll<CharacterCreatorItemLoader>().FirstOrDefault();
                if (face != null && loader != null)
                {
                    Color dark = Color.black;
                    ColorUtility.TryParseHtmlString("#" + data.shade, out dark);
                    dark = new Color(dark.r * 0.35f, dark.g * 0.35f, dark.b * 0.35f, 1f);
                    string png = FaceRenderer.Render(new[]
                    {
                        new FaceItem(Pick(loader.eyes, face.eyeID), face.eyeOffset),
                        new FaceItem(Pick(loader.mouths, face.mouthID), face.mouthOffset),
                        new FaceItem(Pick(loader.accessories, face.detailID), face.detailOffset),
                        new FaceItem(Pick(loader.accessories, face.detail2ID), face.detail2Offset)
                    }, eqScale, scaleM, eqOffset, diameter, dark);
                    if (png != null)
                        data.faceParts = new[] { new SkinPart { png = png, x = 0f, y = 0f, w = 1f, h = 1f, color = "FFFFFFFF" } };
                }
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("face: " + e); }

            try
            {
                if (prefab != null) data.limbs = Limbs(prefab, diameter);
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("limbs: " + e); }

            try
            {
                Holding holding = prefab != null ? prefab.GetComponent<Holding>() : null;
                Holdable holdable = holding != null ? holding.holdable : null;
                if (holdable != null)
                {
                    SpriteRenderer best = holdable.GetComponentsInChildren<SpriteRenderer>(true)
                        .Where(r => r.sprite != null)
                        .OrderByDescending(r => r.sprite.bounds.size.x * r.sprite.bounds.size.y * Mathf.Abs(r.transform.lossyScale.x * r.transform.lossyScale.y))
                        .FirstOrDefault();
                    if (best != null)
                    {
                        Vector3 local = best.bounds.center - holdable.transform.position;
                        data.gun = new SkinPart
                        {
                            png = Png(best.sprite),
                            x = local.x / diameter,
                            y = local.y / diameter,
                            w = best.bounds.size.x / diameter,
                            h = best.bounds.size.y / diameter,
                            color = ColorUtility.ToHtmlStringRGBA(best.color)
                        };
                    }
                    Gun gun = holdable.GetComponent<Gun>();
                    GameObject bullet = gun != null && gun.projectiles != null && gun.projectiles.Length > 0 ? gun.projectiles[0].objectToSpawn : null;
                    if (bullet != null)
                    {
                        SpriteRenderer br = bullet.GetComponentsInChildren<SpriteRenderer>(true).FirstOrDefault(r => r.sprite != null);
                        if (br != null) data.bulletPng = Png(br.sprite);
                    }
                }
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("gun: " + e); }
            return data;
        }

        /// <summary>ROUNDS's arms and legs: every LegRenderer on the player prefab, with its curve points and segment look.</summary>
        private static SkinLimb[] Limbs(GameObject prefab, float diameter)
        {
            var list = new System.Collections.Generic.List<SkinLimb>();
            Vector3 root = prefab.transform.position;
            foreach (var lr in prefab.GetComponentsInChildren<LegRenderer>(true))
            {
                if (lr.start == null || lr.mid == null || lr.end == null) continue;
                string path = lr.transform.name;
                for (Transform t = lr.transform.parent; t != null && t != prefab.transform; t = t.parent) path = t.name + "/" + path;
                var limb = new SkinLimb
                {
                    name = path,
                    arm = path.ToLowerInvariant().Contains("arm") || path.ToLowerInvariant().Contains("hand"),
                    sx = (lr.start.position.x - root.x) / diameter,
                    sy = (lr.start.position.y - root.y) / diameter,
                    mx = (lr.mid.position.x - root.x) / diameter,
                    my = (lr.mid.position.y - root.y) / diameter,
                    ex = (lr.end.position.x - root.x) / diameter,
                    ey = (lr.end.position.y - root.y) / diameter,
                    segments = Mathf.Clamp(lr.segmentCount, 3, 30)
                };
                SpriteRenderer seg = lr.segment != null ? lr.segment.GetComponentInChildren<SpriteRenderer>(true) : null;
                if (seg != null && seg.sprite != null)
                {
                    Vector3 sc = seg.transform.lossyScale;
                    limb.width = seg.sprite.bounds.size.x * Mathf.Abs(sc.x) / diameter;
                    limb.length = seg.sprite.bounds.size.y * Mathf.Abs(sc.y) / diameter;
                    limb.color = ColorUtility.ToHtmlStringRGBA(seg.color);
                    limb.png = Png(seg.sprite);
                }
                EnginePlugin.L.LogInfo("  limb " + path + " start " + limb.sx.ToString("0.00") + "," + limb.sy.ToString("0.00") + " end " + limb.ex.ToString("0.00") + "," + limb.ey.ToString("0.00") + " width " + limb.width.ToString("0.000") + " colour " + limb.color);
                list.Add(limb);
            }
            return list.ToArray();
        }

        private static GameObject PlayerPrefab()
        {
            var pa = PlayerAssigner.instance ?? Resources.FindObjectsOfTypeAll<PlayerAssigner>().FirstOrDefault();
            return pa != null ? pa.playerPrefab : null;
        }

        private static PlayerFace Face(int slot)
        {
            var h = CharacterCreatorHandler.instance ?? Resources.FindObjectsOfTypeAll<CharacterCreatorHandler>().FirstOrDefault();
            if (h != null && h.selectedPlayerFaces != null && slot < h.selectedPlayerFaces.Length && h.selectedPlayerFaces[slot] != null)
                return h.selectedPlayerFaces[slot];
            var f = new PlayerFace();
            f.LoadFace(PlayerPrefs.GetInt("SelectedFace" + slot).ToString());
            return f;
        }

        private static CharacterItem Pick(CharacterItem[] items, int id)
        {
            return items != null && id >= 0 && id < items.Length ? items[id] : null;
        }

        private static SkinPart Part(CharacterItem item, Vector2 offset, float eqScale, float scaleM, Vector3 eqOffset, float diameter)
        {
            if (item == null) return null;
            Sprite s = item.sprite;
            var sr = item.GetComponent<SpriteRenderer>();
            if (s == null) s = sr != null ? sr.sprite : null;
            if (s == null) return null;
            float scale = item.scale * scaleM * eqScale;
            Vector2 pos = (item.offset + offset) * scaleM * eqScale + (Vector2)eqOffset;
            return new SkinPart
            {
                png = Png(s),
                x = pos.x / diameter,
                y = pos.y / diameter,
                w = s.bounds.size.x * scale / diameter,
                h = s.bounds.size.y * scale / diameter,
                color = sr != null ? ColorUtility.ToHtmlStringRGBA(sr.color) : "FFFFFFFF"
            };
        }

        /// <summary>A sprite's pixels as PNG, copied through the GPU since ROUNDS's textures are not CPU-readable.</summary>
        public static string Png(Sprite s)
        {
            Texture2D src = s.texture;
            Rect r = s.textureRect;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(Mathf.Max(1, (int)r.width), Mathf.Max(1, (int)r.height), TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            string b64 = Convert.ToBase64String(t.EncodeToPNG());
            UnityEngine.Object.Destroy(t);
            return b64;
        }
    }
}
