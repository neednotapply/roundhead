using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>ROUNDS bullet motion on a Cuphead player projectile: speed, gravity, drag, bounces, size, falloff, ghost bullets.</summary>
    public class RoundsBullet : MonoBehaviour
    {
        private const int SurfaceMask = 262144 | 524288 | 1048576;
        private static readonly List<DamageReceiver> enemies = new List<DamageReceiver>();
        private static float enemiesAt = -10f;

        private BlobController owner;
        private BasicProjectile proj;
        private Vector2 velocity;
        private float gravity, drag, dragMin, life, maxLife;
        private int bounces, smart;
        private float baseDamage, bounceMult = 1f, distance;
        private bool ghost;
        private BuildStats b;

        public void Init(BlobController c, BasicProjectile p, Vector2 v, float dmg)
        {
            owner = c;
            proj = p;
            b = c.build;
            velocity = v;
            baseDamage = dmg;
            gravity = Tuning.baseBulletGravity * b.gravity;
            drag = b.drag;
            dragMin = b.dragMinSpeed * Tuning.dragScale;
            maxLife = b.destroyBulletAfter > 0f ? b.destroyBulletAfter : 6f;
            bounces = Mathf.Clamp(b.reflects + b.randomBounces, 0, 50);
            smart = Mathf.Clamp(b.smartBounce, 0, 50);
            ApplySize();
            ApplyWalls();
            ApplyLook();
        }

        public void ApplySize()
        {
            float s = Mathf.Clamp(1f + (b.size + b.projectileSize) * Tuning.bulletSizePerUnit, 0.5f, 4f);
            transform.localScale = new Vector3(transform.localScale.x * s, transform.localScale.y * s, 1f);
        }

        public void ApplyWalls()
        {
            ghost = b.ignoreWalls;
            bool surfacesKill = !ghost && bounces == 0;
            proj.CollisionDeath.Walls = surfacesKill;
            proj.CollisionDeath.Ground = surfacesKill;
            proj.CollisionDeath.Ceiling = surfacesKill;
        }

        private static Material trailMat;

        /// <summary>ROUNDS bullets: a round slug with a fading trail, tinted by card colours (team colour by default).</summary>
        public void ApplyLook()
        {
            Color team = owner.index == 0 ? new Color(1f, 0.62f, 0.25f) : new Color(0.4f, 0.65f, 1f);
            SkinData skin = Plugin.Skins[owner.index];
            if (skin != null) team = Color.Lerp(Gfx.Hex(skin.color, team), Color.white, 0.35f);
            Color tint = team;
            if (b.projectileColor != "000000" && b.projectileColor != "#000000") tint = Gfx.Hex(b.projectileColor, team);
            if (b.slow > 0f) tint = Color.Lerp(tint, new Color(0.45f, 0.75f, 1f), 0.5f);
            Texture2D tex = Plugin.BulletTex[owner.index];
            Sprite s = tex != null && tex.width > 8 ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width / 26f) : null;
            if (s == null) s = Gfx.Disc(22f);
            foreach (var a in GetComponentsInChildren<Animator>(true)) a.enabled = false;
            bool first = true;
            foreach (var r in GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (first)
                {
                    r.sprite = s;
                    r.color = tint;
                    first = false;
                }
                else r.enabled = false;
            }
            var trail = gameObject.AddComponent<TrailRenderer>();
            if (trailMat == null) trailMat = new Material(Shader.Find("Sprites/Default"));
            trail.material = trailMat;
            trail.time = 0.12f;
            trail.startWidth = 18f * transform.lossyScale.x;
            trail.endWidth = 0f;
            trail.startColor = new Color(tint.r, tint.g, tint.b, 0.8f);
            trail.endColor = new Color(tint.r, tint.g, tint.b, 0f);
            var first2 = GetComponentInChildren<SpriteRenderer>();
            if (first2 != null) { trail.sortingLayerID = first2.sortingLayerID; trail.sortingOrder = first2.sortingOrder - 1; }
        }

        private void FixedUpdate()
        {
            if (proj == null || proj.dead) { enabled = false; return; }
            float dt = CupheadTime.FixedDelta;
            if (dt <= 0f) return;
            life += dt;
            if (life > maxLife)
            {
                Reflect.Call(proj, "Die");
                enabled = false;
                return;
            }
            Move(dt);
            DistanceMultiplier();
        }

        public void Move(float dt)
        {
            velocity.y -= gravity * dt;
            if (drag > 0f)
            {
                float sp = velocity.magnitude;
                float target = Mathf.Max(dragMin, sp * Mathf.Exp(-drag * dt));
                if (sp > target && sp > 0.01f) velocity *= target / sp;
            }
            Vector2 pos = transform.position;
            Vector2 step = velocity * dt;
            if (bounces > 0 && !ghost)
            {
                RaycastHit2D hit = Physics2D.Raycast(pos, step.normalized, step.magnitude + Tuning.bounceProbe, SurfaceMask);
                if (hit.collider != null && hit.distance <= step.magnitude + 2f)
                {
                    pos = hit.point + hit.normal * 6f;
                    Bounce(hit.normal);
                    step = velocity * dt;
                }
            }
            Vector2 next = pos + step;
            if (bounces > 0)
            {
                Rect view = ViewRect();
                Vector2 n = Vector2.zero;
                if (next.x < view.xMin && velocity.x < 0f) n = Vector2.right;
                else if (next.x > view.xMax && velocity.x > 0f) n = Vector2.left;
                else if (next.y > view.yMax && velocity.y > 0f) n = Vector2.down;
                else if (next.y < view.yMin && velocity.y < 0f) n = Vector2.up;
                if (n != Vector2.zero) { Bounce(n); next = pos + velocity * dt; }
            }
            transform.position = new Vector3(next.x, next.y, transform.position.z);
            transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg);
            distance += step.magnitude;
        }

        private static Rect ViewRect()
        {
            Camera cam = Camera.main;
            if (cam == null || !cam.orthographic) return new Rect(-640f, -360f, 1280f, 720f);
            float h = cam.orthographicSize;
            float w = h * cam.aspect;
            Vector3 p = cam.transform.position;
            return new Rect(p.x - w, p.y - h, w * 2f, h * 2f);
        }

        public void Bounce(Vector2 normal)
        {
            velocity = Vector2.Reflect(velocity, normal) * b.speedMOnBounce;
            bounceMult *= b.dmgMOnBounce;
            bounces--;
            if (smart > 0)
            {
                smart--;
                DamageReceiver t = Nearest(transform.position);
                if (t != null)
                {
                    Vector2 to = ((Vector2)t.transform.position - (Vector2)transform.position).normalized;
                    velocity = to * velocity.magnitude;
                }
            }
            if (bounces <= 0) ApplyWalls();
            SetDamage(baseDamage * bounceMult);
        }

        public void DistanceMultiplier()
        {
            if (Mathf.Approximately(b.damageAfterDistanceMultiplier, 1f) && Mathf.Approximately(bounceMult, 1f)) return;
            float m = Mathf.Lerp(1f, b.damageAfterDistanceMultiplier, Mathf.Clamp01(distance / Tuning.falloffDistance));
            SetDamage(baseDamage * bounceMult * m);
        }

        private void SetDamage(float d)
        {
            proj.Damage = d;
            var dealer = Reflect.Get<DamageDealer>(proj, "damageDealer");
            if (dealer != null) dealer.SetDamage(d);
        }

        public static DamageReceiver Nearest(Vector2 from)
        {
            if (Time.time - enemiesAt > 0.5f)
            {
                enemiesAt = Time.time;
                enemies.Clear();
                foreach (var r in FindObjectsOfType<DamageReceiver>())
                    if (r.type == DamageReceiver.Type.Enemy && r.enabled && r.gameObject.activeInHierarchy) enemies.Add(r);
            }
            DamageReceiver best = null;
            float bd = float.MaxValue;
            foreach (var r in enemies)
            {
                if (r == null) continue;
                float d = ((Vector2)r.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = r; }
            }
            return best;
        }
    }
}
