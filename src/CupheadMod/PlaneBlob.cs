using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// A plane level's player. Cuphead's plane controls, hitbox, shrink and parry stay; the plane's own sprites stay off
    /// (<see cref="PlayerSprites"/>). With the ROUNDS arena live it is a Flying <see cref="ArenaSeat"/>: ROUNDS's real player
    /// is held where the plane flies (ArenaLink.U_Fly) and is the player, drawn in ROUNDS's picture with its gun, bullets,
    /// block, health and cards, while the plane's own shots and super stay off (Hooks.PlaneWeapons). The plane's parry
    /// refills the ROUNDS block. Without the arena the plane plays as Cuphead's, drawn as the ROUNDS puppet hovering where
    /// the plane is, its gun pointing forward and its legs dangling behind (Request.fly), sized by tuning flySize; it
    /// follows the plane's shrink move, fade (the invincibility blink) and visibility (a super hides it), and turns
    /// parry-pink while parrying.
    /// </summary>
    public class PlaneBlob : ArenaSeat
    {
        private static readonly Color ParryPink = new Color(1f, 0.45f, 0.75f);

        private PlanePlayerController player;
        private GameObject root;
        private SpriteRenderer puppetR;
        private GameObject crosshair;
        private SpriteRenderer crossRing, crossDot;
        private float height = -1f;
        private Vector3 lastPos;
        private Vector2 vel;
        private float lastShot = -10f;

        public override AbstractPlayerController Body => player;
        public override bool Flying => true;

        public override bool CanAct
        {
            get
            {
                Level lv = Level.Current;
                if (player == null || player.IsDead || lv == null || lv.Ending || !lv.Started) return false;
                return player.motor != null && player.motor.enabled && PauseManager.state != PauseManager.State.Paused;
            }
        }

        public static void Attach(PlanePlayerController p)
        {
            if (p == null || p.GetComponent<PlaneBlob>() != null) return;
            var b = p.gameObject.AddComponent<PlaneBlob>();
            b.player = p;
            b.index = p.id == PlayerId.PlayerTwo ? 1 : 0;
            b.TakeSeat();
            b.root = new GameObject("RoundsPlaneBlob");
            b.root.transform.SetParent(p.transform, false);
            var go = new GameObject("roundsCharacter");
            go.transform.SetParent(b.root.transform, false);
            b.puppetR = go.AddComponent<SpriteRenderer>();
            if (b.index == 0)
            {
                b.crosshair = new GameObject("RoundsCrosshair");
                b.crossRing = b.crosshair.AddComponent<SpriteRenderer>();
                b.crossRing.sprite = Gfx.Ring();
                var dot = new GameObject("dot");
                dot.transform.SetParent(b.crosshair.transform, false);
                dot.transform.localScale = Vector3.one * 0.22f;
                b.crossDot = dot.AddComponent<SpriteRenderer>();
                b.crossDot.sprite = Gfx.Disc();
            }
            b.lastPos = p.transform.position;
            PlayerSprites.Track(p.transform, b.root.transform);
            if (p.parryController != null) p.parryController.OnParrySuccessEvent += () => ArenaClient.OnParrySuccess(b);
            Plugin.L.LogInfo("plane level: player " + (b.index + 1) + " flies as their ROUNDS player in " + (Level.Current != null ? Level.Current.CurrentLevel.ToString() : "?"));
            TestMode.Report("plane-rounds-look", true, "player " + (b.index + 1) + " flies as the ROUNDS character in a plane level");
        }

        private void Update()
        {
            if (player == null) return;
            input.Update();
            Announce();
            if (Arena) ArenaClient.Lead(this);
        }

        private void LateUpdate()
        {
            if (player == null || root == null) return;
            SpriteRenderer plane = player.animationController != null ? player.animationController.spriteRenderer : null;
            float dt = Mathf.Max(0.001f, Time.deltaTime);
            Vector3 pos = transform.position;
            vel = Vector2.Lerp(vel, (pos - lastPos) / dt, Mathf.Clamp01(dt * 10f));
            lastPos = pos;
            bool shown = plane != null && plane.sprite != null && plane.gameObject.activeInHierarchy && !player.IsDead;
            UpdateCrosshair(plane, shown);
            // ROUNDS's real player is drawn in ROUNDS's picture: the puppet stand-in stays off.
            if (Arena)
            {
                puppetR.gameObject.SetActive(false);
                return;
            }
            bool live = Puppets.Live(index) && Puppets.SpriteFor(index) != null;
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

        /// <summary>ROUNDS-style crosshair at the mouse for player 1 while the ROUNDS gun aims with it (Cuphead hides the
        /// system cursor), as on the ground (BlobBody.UpdateCrosshair).</summary>
        private void UpdateCrosshair(SpriteRenderer plane, bool shown)
        {
            if (crosshair == null) return;
            bool on = Arena && shown && input.MouseActive;
            crosshair.SetActive(on);
            if (!on) return;
            Vector2 w = input.MouseWorld();
            crosshair.transform.position = new Vector3(w.x, w.y, transform.position.z);
            crosshair.transform.localScale = Vector3.one * 46f;
            crossRing.color = crossDot.color = new Color(1f, 1f, 1f, 0.9f);
            crossRing.sortingLayerID = crossDot.sortingLayerID = plane.sortingLayerID;
            crossRing.sortingOrder = plane.sortingOrder + 32200;
            crossDot.sortingOrder = plane.sortingOrder + 32201;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (player != null) PlayerSprites.Untrack(player.transform);
            if (crosshair != null) Destroy(crosshair);
            if (root != null) Destroy(root);
        }
    }
}
