using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Plane levels keep Cuphead's plane controls, hitbox, shots and super, but the player is drawn as their real ROUNDS
    /// character flying: the plane's own sprites stay off (<see cref="PlayerSprites"/>) and the ROUNDS puppet hovers where the
    /// plane is, its gun pointing forward and its legs dangling behind (Request.fly), sized by tuning flySize. It follows the
    /// plane's shrink move, fade (the invincibility blink) and visibility (a super hides it), and turns parry-pink while parrying.
    /// </summary>
    public class PlaneBlob : MonoBehaviour
    {
        private static readonly Color ParryPink = new Color(1f, 0.45f, 0.75f);

        private PlanePlayerController player;
        private int index;
        private GameObject root;
        private SpriteRenderer puppetR;
        private float height = -1f;
        private Vector3 lastPos;
        private Vector2 vel;
        private float lastShot = -10f;

        public static void Attach(PlanePlayerController p)
        {
            if (p == null || p.GetComponent<PlaneBlob>() != null) return;
            var b = p.gameObject.AddComponent<PlaneBlob>();
            b.player = p;
            b.index = p.id == PlayerId.PlayerTwo ? 1 : 0;
            b.root = new GameObject("RoundsPlaneBlob");
            b.root.transform.SetParent(p.transform, false);
            var go = new GameObject("roundsCharacter");
            go.transform.SetParent(b.root.transform, false);
            b.puppetR = go.AddComponent<SpriteRenderer>();
            b.lastPos = p.transform.position;
            PlayerSprites.Track(p.transform, b.root.transform);
            TestMode.Report("plane-rounds-look", true, "player " + (b.index + 1) + " flies as the ROUNDS character in a plane level");
        }

        private void LateUpdate()
        {
            if (player == null || root == null) return;
            SpriteRenderer plane = player.animationController != null ? player.animationController.spriteRenderer : null;
            float dt = Mathf.Max(0.001f, Time.deltaTime);
            Vector3 pos = transform.position;
            vel = Vector2.Lerp(vel, (pos - lastPos) / dt, Mathf.Clamp01(dt * 10f));
            lastPos = pos;
            bool live = Puppets.Live(index) && Puppets.SpriteFor(index) != null;
            bool shown = plane != null && plane.sprite != null && plane.gameObject.activeInHierarchy && !player.IsDead;
            float perRounds = Mathf.Max(1f, (height > 0f ? height : Tuning.blobDiameter) / Proto.BodyUnits);
            bool shoot = false;
            try
            {
                if (shown && player.input != null && player.input.actions.GetButton((int)CupheadButton.Shoot) && Time.time - lastShot > 0.18f)
                {
                    lastShot = Time.time;
                    shoot = true;
                }
            }
            catch { }
            Puppets.Send(index, new Request { vx = vel.x / perRounds, vy = vel.y / perRounds, ax = 1f, ay = 0f, grounded = false, fly = true, shoot = shoot });
            puppetR.gameObject.SetActive(live && shown);
            if (!live || !shown) return;
            // A fixed body size (the plane sprite's frames are mostly empty padding), halved while the plane is shrunk,
            // centred on the plane's hitbox.
            float target = Tuning.flySize * (player.Shrunk ? Tuning.flyShrunk : 1f);
            height = height < 0f ? target : Mathf.Lerp(height, target, Mathf.Clamp01(dt * 12f));
            puppetR.sprite = Puppets.SpriteFor(index);
            float k = height / Tuning.blobDiameter;
            Vector3 centre = player.center;
            puppetR.transform.position = new Vector3(centre.x, centre.y, plane.transform.position.z);
            puppetR.transform.localScale = new Vector3(k / Mathf.Abs(transform.lossyScale.x), k / Mathf.Abs(transform.lossyScale.y), 1f);
            Color c = player.Parrying ? ParryPink : Color.white;
            c.a = plane.color.a;
            puppetR.color = c;
            puppetR.sortingLayerID = plane.sortingLayerID;
            puppetR.sortingOrder = plane.sortingOrder;
        }

        private void OnDestroy()
        {
            if (player != null) PlayerSprites.Untrack(player.transform);
            if (root != null) Destroy(root);
        }
    }
}
