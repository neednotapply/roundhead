using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>On Cuphead's world map, each player walks around as their ROUNDS blob too (face and team colour from ROUNDS).</summary>
    public class MapBlob : MonoBehaviour
    {
        private MapPlayerController player;
        private int index;
        private readonly List<SpriteRenderer> cuphead = new List<SpriteRenderer>();
        private GameObject root;
        private SpriteRenderer outline, fill, faceR;
        private readonly SpriteRenderer[] feet = new SpriteRenderer[2];
        private float phase;
        private string skinKey;
        private float diameter = 50f;
        private SpriteRenderer puppetR;
        private Vector3 lastPos;
        private float facingX = 1f;

        public static void Attach(MapPlayerController p)
        {
            if (p == null || p.GetComponent<MapBlob>() != null) return;
            var b = p.gameObject.AddComponent<MapBlob>();
            b.player = p;
            b.index = p.id == PlayerId.PlayerTwo ? 1 : 0;
            foreach (var r in p.GetComponentsInChildren<SpriteRenderer>(true)) b.cuphead.Add(r);
            b.Build();
        }

        private SpriteRenderer Make(string name, Sprite s)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = s;
            return r;
        }

        private void Build()
        {
            root = new GameObject("RoundsMapBlob");
            root.transform.SetParent(transform, false);
            outline = Make("outline", Gfx.Disc());
            fill = Make("fill", Gfx.Disc());
            faceR = Make("face", null);
            for (int i = 0; i < 2; i++) feet[i] = Make("foot", Gfx.Disc());
            puppetR = Make("roundsCharacter", null);
            lastPos = transform.position;
        }

        private void ApplySkin()
        {
            SkinData skin = Plugin.Skins[index];
            string key = skin == null ? "none" : skin.color + skin.faceParts.Length;
            if (key == skinKey) return;
            skinKey = key;
            Color team = index == 0 ? new Color(1f, 0.55f, 0.1f) : new Color(0.2f, 0.5f, 1f);
            Color col = Gfx.Hex(skin != null ? skin.color : null, team);
            Color shade = Gfx.Hex(skin != null ? skin.shade : null, col * 0.55f);
            shade.a = 1f;
            fill.color = col;
            outline.color = shade;
            foreach (var f in feet) f.color = shade;
            faceR.sprite = skin != null && skin.faceParts.Length > 0 ? Gfx.ToSprite(Gfx.FromPng(skin.faceParts[0].png)) : null;
        }

        private void LateUpdate()
        {
            if (player == null || root == null) return;
            ApplySkin();
            SpriteRenderer reference = null;
            Bounds bounds = new Bounds(transform.position, Vector3.zero);
            foreach (var r in cuphead)
            {
                if (r == null) continue;
                if (reference == null && r.sprite != null)
                {
                    reference = r;
                    Bounds sb = r.sprite.bounds;
                    Vector3 size = Vector3.Scale(sb.size, new Vector3(Mathf.Abs(r.transform.lossyScale.x), Mathf.Abs(r.transform.lossyScale.y), 1f));
                    bounds = new Bounds(r.transform.TransformPoint(sb.center), size);
                }
                r.enabled = false;
            }
            if (reference == null) return;
            if (bounds.size.y > 1f) diameter = Mathf.Lerp(diameter, bounds.size.y * 0.55f, 0.2f);
            float d = diameter;
            Vector2 v = player.motor != null ? player.motor.velocity : Vector2.zero;
            bool moving = v.sqrMagnitude > 1f;
            phase += (moving ? 1f : 0f) * Time.deltaTime * 12f;
            float bob = moving ? Mathf.Abs(Mathf.Sin(phase)) * d * 0.06f : 0f;
            root.transform.position = new Vector3(bounds.center.x, bounds.min.y + d * 0.5f + d * 0.08f + bob, transform.position.z);
            float facing = v.x < -0.1f ? -1f : 1f;
            outline.transform.localScale = Vector3.one * d * 1.08f;
            fill.transform.localScale = Vector3.one * d;
            faceR.transform.localScale = new Vector3(d * facing, d, 1f);
            faceR.transform.localPosition = new Vector3(Mathf.Clamp(v.x, -1f, 1f) * d * 0.05f, Mathf.Clamp(v.y, -1f, 1f) * d * 0.04f, 0f);
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                float lift = moving ? Mathf.Max(0f, Mathf.Sin(phase + i * Mathf.PI)) * d * 0.1f : 0f;
                feet[i].transform.localPosition = new Vector3(side * d * 0.2f, -d * 0.5f + lift - bob, 0f);
                feet[i].transform.localScale = new Vector3(d * 0.22f, d * 0.13f, 1f);
            }
            int order = reference.sortingOrder;
            int layer = reference.sortingLayerID;
            SetSort(feet[0], layer, order - 1);
            SetSort(feet[1], layer, order - 1);
            SetSort(outline, layer, order);
            SetSort(fill, layer, order + 1);
            SetSort(faceR, layer, order + 2);
            // The real ROUNDS character, walking: ROUNDS animates it from the map movement.
            float dtm = Mathf.Max(0.001f, Time.deltaTime);
            Vector3 dp = (transform.position - lastPos) / dtm;
            lastPos = transform.position;
            float sp = new Vector2(dp.x, dp.y).magnitude;
            if (Mathf.Abs(dp.x) > 1f) facingX = Mathf.Sign(dp.x);
            float perRounds = d / Proto.BodyUnits;
            Puppets.Send(index, new Request { vx = sp > 1f ? facingX * sp / perRounds : 0f, vy = 0f, height = 0f, ax = facingX, ay = -0.15f, grounded = true });
            bool live = Puppets.Live(index) && Puppets.SpriteFor(index) != null;
            outline.gameObject.SetActive(!live);
            fill.gameObject.SetActive(!live);
            faceR.gameObject.SetActive(!live);
            foreach (var f in feet) f.gameObject.SetActive(!live);
            puppetR.gameObject.SetActive(live);
            if (live)
            {
                puppetR.sprite = Puppets.SpriteFor(index);
                float k = d / Tuning.blobDiameter;
                puppetR.transform.localScale = new Vector3(k, k, 1f);
                puppetR.transform.localPosition = new Vector3(0f, bob + d * 0.51f, 0f);
                SetSort(puppetR, layer, order + 1);
            }
            root.SetActive(player.gameObject.activeInHierarchy);
        }

        private static void SetSort(SpriteRenderer r, int layer, int order)
        {
            r.sortingLayerID = layer;
            r.sortingOrder = order;
        }

        private void OnDestroy()
        {
            if (root != null) Destroy(root);
        }
    }
}
