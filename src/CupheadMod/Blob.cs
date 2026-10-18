using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>The ROUNDS blob that replaces a Cuphead player on ground levels (sheets/effects.json).</summary>
    public class BlobController : MonoBehaviour
    {
        public static readonly List<BlobController> All = new List<BlobController>();

        public LevelPlayerController player;
        public int index;
        public BuildStats build = new BuildStats();
        public BlobInput input;
        public BlobMotor motor;
        public BlobBody body;
        public BlobHealth health;
        public BlobBlock block;
        public RoundsGun gun;

        public static BlobController For(AbstractPlayerController p)
        {
            if (p == null) return null;
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].player == p) return All[i];
            return null;
        }

        public static BlobController For(Component c)
        {
            return c == null ? null : For(c.GetComponent<AbstractPlayerController>());
        }

        public static void Attach(LevelPlayerController p)
        {
            if (p == null || For(p) != null) return;
            var c = p.gameObject.AddComponent<BlobController>();
            c.player = p;
            c.index = p.id == PlayerId.PlayerTwo ? 1 : 0;
            c.input = new BlobInput(c);
            c.motor = new BlobMotor(c);
            c.health = new BlobHealth(c);
            c.block = new BlobBlock(c);
            c.gun = new RoundsGun(c);
            c.body = new BlobBody(c);
            All.Add(c);
            c.ApplyBuild();
            PlayerSprites.DressGhosts(c);
            Plugin.L.LogInfo("ROUNDS blob spawned for player " + (c.index + 1) + " in " + (Level.Current != null ? Level.Current.CurrentLevel.ToString() : "?"));
            TestMode.PlaceBot(c);
            TestMode.OnBlobSpawned();
            TestMode.Report("blob-spawned", true, "player " + (c.index + 1) + " is a ROUNDS blob in " + (Level.Current != null ? Level.Current.CurrentLevel.ToString() : "a level"));
        }

        public static void RefreshAllBuilds()
        {
            foreach (var c in All) if (c != null) c.ApplyBuild();
        }

        public static void RefreshAllLooks()
        {
            foreach (var c in All) if (c != null) c.body.ApplyLook();
        }

        public void ApplyBuild()
        {
            build = Progress.Builds[index] ?? new BuildStats();
            motor.ApplyMove();
            motor.ApplyJump();
            health.ApplyBuild();
            gun.ApplyBuild();
            body.ApplyLook();
        }

        /// <summary>The player may act: alive, the level under way (a boss or run-and-gun level after its intro; the house and
        /// the tutorial have no intro and never set Started, Level.cs:1885-1961), not ending, not paused, and Cuphead's own
        /// motor taking input (dialogue and doors switch it off, DialogueInteractionPoint.cs:141; the house keeps the motor on
        /// and only the weapon off, so walking there must not wait on the weapon).</summary>
        public bool CanAct
        {
            get
            {
                Level lv = Level.Current;
                if (player == null || player.IsDead || lv == null || lv.Ending || player.motor == null) return false;
                bool underWay = lv.Started || lv.LevelType == Level.Type.Tutorial || lv.CurrentLevel == Levels.House;
                return underWay && Reflect.Get<bool>(player.motor, "allowInput") && PauseManager.state != PauseManager.State.Paused;
            }
        }

        /// <summary>ROUNDS's real player stands in for this one (the arena link is live).</summary>
        public bool Arena => ArenaClient.Drives(this);
        private int announcedFor;

        private void Update()
        {
            float dt = CupheadTime.Delta;
            input.Update();
            // ROUNDS's player arrived (level start, ROUNDS ready late, or ROUNDS started again): it comes here.
            int rounds = ArenaClient.RoundsPid;
            if (ArenaClient.Live && announcedFor != rounds && ArenaClient.link.I(ArenaLink.RSlot(index) + ArenaLink.Q_Present) != 0)
            {
                announcedFor = rounds;
                ArenaClient.OnPlayerSpawned(this);
            }
            if (Arena)
            {
                ArenaClient.Follow(this);
                return;
            }
            // ROUNDS is still on its way: stay put and hidden (BlobBody) instead of playing as the stand-in.
            if (ArenaClient.Waiting(this)) return;
            motor.Update(dt);
            if (CanAct && dt > 0f)
            {
                block.Update(dt);
                gun.Update(dt);
                health.Update(dt);
            }
        }

        private void LateUpdate()
        {
            body.LateUpdate();
        }

        private void OnDestroy()
        {
            All.Remove(this);
            body.Destroy();
        }
    }

    /// <summary>
    /// ROUNDS controls (sheets/controls.json). Player 1 gets ROUNDS's keyboard and mouse layout in levels, read directly
    /// so Cuphead's saved bindings never change: A/D move, W or Space jump, S drops, the mouse aims, left click shoots,
    /// right click blocks. Cuphead's own bindings and gamepads (right stick aims) keep working for both players.
    /// </summary>
    public class BlobInput
    {
        private readonly BlobController c;
        private Vector3 lastMouse;
        private float lastMouseTime = -100f;
        private float lastStickTime = -100f;
        private bool wasLeftTrigger;
        private Rewired.Joystick stick;
        private int rx = -1, ry = -1, rt = -1, lt = -1;
        private float jumpPressedAt = -10f;
        private bool botWasHeld;

        public BlobInput(BlobController c) { this.c = c; }

        private Rewired.Player Rp => c.player.input.actions;

        /// <summary>The mouse and the ROUNDS keys belong to player 1.</summary>
        private bool KeyboardPlayer => c.index == 0;

        private static bool RoundsKeyHeld => Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.Space);

        /// <summary>Mouse aim: on for player 1 whenever the mouse or ROUNDS keys were used lately and the right stick wasn't.</summary>
        public bool MouseActive => KeyboardPlayer && lastMouseTime > lastStickTime && Time.unscaledTime - lastMouseTime < 20f;

        public void Update()
        {
            if (KeyboardPlayer)
            {
                if ((Input.mousePosition - lastMouse).sqrMagnitude > 4f || Input.GetMouseButton(0) || Input.GetMouseButton(1) || RoundsKeyHeld)
                    lastMouseTime = Time.unscaledTime;
                lastMouse = Input.mousePosition;
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W)) jumpPressedAt = Time.unscaledTime;
            }
            if (Rp.GetButtonDown(Buttons.Jump)) jumpPressedAt = Time.unscaledTime;
            bool botHeld;
            if (TestMode.BotJump(c.index, out botHeld))
            {
                if (botHeld && !botWasHeld) jumpPressedAt = Time.unscaledTime;
                botWasHeld = botHeld;
            }
            FindStick();
            if (new Vector2(StickAxis(rx), StickAxis(ry)).sqrMagnitude > 0.2f) lastStickTime = Time.unscaledTime;
        }

        private void FindStick()
        {
            Rewired.Joystick j = null;
            try
            {
                foreach (var js in Rp.controllers.Joysticks) { j = js; break; }
            }
            catch { }
            if (j == stick) return;
            stick = j;
            rx = ry = rt = lt = -1;
            if (j == null) return;
            var ids = j.AxisElementIdentifiers;
            for (int i = 0; i < ids.Count; i++)
            {
                string n = ids[i].name.ToLowerInvariant();
                if (n.Contains("right stick x")) rx = ids[i].id;
                else if (n.Contains("right stick y")) ry = ids[i].id;
                else if (n.Contains("right trigger")) rt = ids[i].id;
                else if (n.Contains("left trigger")) lt = ids[i].id;
            }
        }

        private float StickAxis(int id) => stick != null && id >= 0 ? stick.GetAxisById(id) : 0f;

        public Vector2 Move()
        {
            float bot;
            if (TestMode.BotMove(c.index, out bot)) return new Vector2(bot, 0f);
            float x = Rp.GetAxis(0), y = Rp.GetAxis(1);
            if (KeyboardPlayer)
            {
                float kx = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
                if (kx != 0f) x = kx;
                if (Input.GetKey(KeyCode.S)) y = -1f;
            }
            return new Vector2(x, y);
        }

        public bool DownHeld() => Move().y < -0.5f;

        private bool botJumpWas;

        public bool JumpDown()
        {
            bool held;
            if (TestMode.BotJump(c.index, out held))
            {
                bool down = held && !botJumpWas;
                botJumpWas = held;
                return down;
            }
            return Rp.GetButtonDown(Buttons.Jump) || (KeyboardPlayer && (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W)));
        }

        /// <summary>A jump press from the last 0.15 s (presses land between fixed steps), used once.</summary>
        public bool ConsumeJump()
        {
            if (Time.unscaledTime - jumpPressedAt > 0.15f) return false;
            jumpPressedAt = -10f;
            return true;
        }

        public bool JumpHeld()
        {
            bool held;
            if (TestMode.BotJump(c.index, out held)) return held;
            return Rp.GetButton(Buttons.Jump) || (KeyboardPlayer && (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W)));
        }

        public bool Locked() => Rp.GetButton(Buttons.Lock);

        private bool wasShootHeld;
        private float botClickAt;

        /// <summary>A new shot press this frame (ROUNDS guns fire per press unless cards make them fast).</summary>
        public bool ShootDown()
        {
            if (TestMode.BotShoot(c.index))
            {
                if (Time.time - botClickAt < 0.32f) return false;
                botClickAt = Time.time;
                return true;
            }
            bool held = ShootHeld();
            bool down = held && !wasShootHeld;
            wasShootHeld = held;
            if (KeyboardPlayer && Input.GetMouseButtonDown(0)) down = true;
            return down;
        }

        public bool ShootHeld()
        {
            if (TestMode.BotShoot(c.index)) return true;
            if (Rp.GetButton(Buttons.Shoot)) return true;
            if (KeyboardPlayer && Input.GetMouseButton(0)) return true;
            return StickAxis(rt) > 0.5f;
        }

        public bool BlockDown()
        {
            if (TestMode.BotBlock(c.index)) return true;
            if (Rp.GetButtonDown(Buttons.Block) || Rp.GetButtonDown(Buttons.BlockAlt)) return true;
            if (KeyboardPlayer && Input.GetMouseButtonDown(1)) return true;
            bool trig = StickAxis(lt) > 0.5f;
            bool down = trig && !wasLeftTrigger;
            wasLeftTrigger = trig;
            return down;
        }

        /// <summary>The mouse in the level, through the camera's viewport (works with Cuphead's scaled screen).</summary>
        public Vector2 MouseWorld()
        {
            Camera cam = CupheadLevelCamera.Current != null ? CupheadLevelCamera.Current.camera : Camera.main;
            if (cam == null) return c.player.center;
            Vector3 vp = new Vector3(Input.mousePosition.x / Mathf.Max(1, Screen.width), Input.mousePosition.y / Mathf.Max(1, Screen.height), 0f);
            Rect r = cam.rect;
            vp.x = (vp.x - r.x) / Mathf.Max(0.01f, r.width);
            vp.y = (vp.y - r.y) / Mathf.Max(0.01f, r.height);
            vp.z = Mathf.Abs(cam.transform.position.z);
            return cam.ViewportToWorldPoint(vp);
        }

        /// <summary>Free 360 aim: test bot, then the mouse (player 1), then the right stick, then the 8-way direction held.</summary>
        public Vector2 Aim(Vector2 origin)
        {
            Vector2 bot;
            if (TestMode.BotAim(c.index, origin, out bot)) return bot;
            if (MouseActive)
            {
                Vector2 d = MouseWorld() - origin;
                if (d.sqrMagnitude > 1f) return d.normalized;
            }
            Vector2 s = new Vector2(StickAxis(rx), StickAxis(ry));
            if (s.sqrMagnitude > 0.2f) return s.normalized;
            int lx = (int)c.player.motor.LookDirection.x;
            int ly = (int)c.player.motor.LookDirection.y;
            if (c.player.motor.Grounded && !Locked() && ly < 0) ly = 0;
            if (lx == 0 && ly == 0) lx = (int)c.player.motor.TrueLookDirection.x;
            if (lx == 0 && ly == 0) lx = 1;
            return new Vector2(lx, ly).normalized;
        }
    }

    /// <summary>
    /// ROUNDS movement inside Cuphead's motor: ROUNDS's run push and drag, jump impulse and hold lift, gravity that grows
    /// with time in the air, air control, extra jumps, wall jump and slide (values from the ROUNDS player prefab, sheets/tuning.json),
    /// scaled to Cuphead's screen. Cuphead keeps collisions, platforms, knockbacks and parry bounces.
    /// </summary>
    public class BlobMotor
    {
        private readonly BlobController c;
        private const int WallMask = 262144;
        private float vx, vy;              // Cuphead units/s, +y up
        private float sinceGrounded, sinceJump = 10f;
        private int jumpsLeft;
        private float lastSetY = 999f;

        public BlobMotor(BlobController c) { this.c = c; }

        private LevelPlayerMotor M => c.player.motor;
        private LevelPlayerMotor.Properties Props => Reflect.Get<LevelPlayerMotor.Properties>(M, "properties");
        private LevelPlayerMotor.VelocityManager Vel => Reflect.Get<LevelPlayerMotor.VelocityManager>(M, "velocityManager");
        private static float S => Tuning.moveWorldScale;
        private float MaxY { get { var p = Props; return p != null ? p.maxSpeedY : 1620f; } }

        /// <summary>Speed cards act through the push each step (nothing to cache).</summary>
        public void ApplyMove() { }

        /// <summary>Jump and gravity cards act through the impulse and gravity each step.</summary>
        public void ApplyJump()
        {
            jumpsLeft = Mathf.Max(0, c.build.jumps - 1);
        }

        private float JumpImpulse => 0.01f * Tuning.roundsUpForce * Mathf.Clamp(c.build.jump, 0.3f, 3f);

        /// <summary>After Cuphead's HandleWalking: ROUNDS run momentum (push minus drag, weaker in the air).</summary>
        public void Walk()
        {
            float dt = CupheadTime.FixedDelta;
            var v = Vel;
            if (dt <= 0f || v == null) return;
            if (M.IsHit) { vx = 0f; return; }
            bool g = M.Grounded;
            float mult = g ? 1f : Tuning.roundsAirControl;
            float dir = c.CanAct ? Mathf.Clamp(c.input.Move().x, -1f, 1f) : 0f;
            if (Mathf.Abs(dir) < 0.2f) dir = 0f;
            if (g && (c.input.Locked() || M.Ducking)) dir = 0f;
            float push = Tuning.roundsMoveForce * 0.01f * Mathf.Clamp(c.build.movementSpeed, 0.2f, 4f) * mult;
            float drag = 0.001f * Tuning.roundsExtraDrag * mult / 0.02f;
            float r = vx / S;
            r += (dir * push - drag * r) * dt;
            vx = r * S;
            int wall = WallSide();
            if (wall != 0 && Mathf.Sign(vx) == wall) vx = 0f;
            v.move = vx;
        }

        /// <summary>Replaces Cuphead's HandleJumping: ROUNDS jumps from the ground, extra air jumps and wall jumps.</summary>
        public bool Jump()
        {
            if (!c.input.ConsumeJump()) return false;
            if (M.Grounded && ((int)M.LookDirection.y < 0 || c.input.DownHeld()) && c.player.transform.parent != null) return true; // drop through a platform, Cuphead's way
            if (M.IsHit || M.Parrying || !c.CanAct) return false;
            int wall = M.Grounded ? 0 : WallSide();
            if (M.Grounded || sinceGrounded < 0.1f) DoJump(new Vector2(0f, 1f));
            else if (wall != 0) { DoJump(new Vector2(-wall * 0.4f, 0.8f)); jumpsLeft = Mathf.Max(0, c.build.jumps - 1); }
            else if (jumpsLeft > 0) { jumpsLeft--; DoJump(new Vector2(0f, 1f)); }
            return false;
        }

        private void DoJump(Vector2 dir)
        {
            float imp = JumpImpulse;
            float r = vy / S;
            if (r < 0f) r = 0f;
            r += dir.y * imp;
            vy = r * S;
            vx += dir.x * imp * S;
            sinceJump = 0f;
            sinceGrounded = 0f;
            Reflect.Call(M, "LeaveGround", false);
            Reflect.Set(Reflect.Get(M, "jumpManager"), "state", "Used");
            SetY();
            AudioManager.Play("player_jump");
        }

        private void SetY()
        {
            var v = Vel;
            if (v == null) return;
            v.y = Mathf.Clamp(-vy / MaxY, -10f, 1f);
            lastSetY = v.y;
        }

        /// <summary>Replaces Cuphead's HandleFalling while airborne: ROUNDS gravity, hold lift, air drag, wall slide.</summary>
        public bool Fall()
        {
            var v = Vel;
            float dt = CupheadTime.FixedDelta;
            if (v == null || dt <= 0f) return true;
            // Cuphead changed the velocity itself (hit knock-up, parry bounce, pit): follow it.
            if (lastSetY < 900f && Mathf.Abs(v.y - lastSetY) > 0.0005f) vy = -v.y * MaxY;
            if (M.Grounded)
            {
                sinceGrounded = 0f;
                jumpsLeft = Mathf.Max(0, c.build.jumps - 1);
                vy = 0f;
                lastSetY = 999f;
                return true;
            }
            if (M.IsHit) { lastSetY = 999f; return true; }
            sinceGrounded += dt;
            sinceJump += dt;
            float r = vy / S;
            float jump = Mathf.Clamp(c.build.jump, 0.3f, 3f);
            if (c.input.JumpHeld() && sinceJump < Tuning.roundsJumpHold) r += 2f * jump * Tuning.roundsUpForce * 0.02f * dt;
            float g = Tuning.roundsBaseGravity + Mathf.Pow(sinceGrounded, Tuning.roundsGravityExponent) * Tuning.roundsGravityForce;
            r -= g * Mathf.Clamp(c.build.bodyGravity, 0.2f, 4f) * dt;
            r -= r * (0.001f * Tuning.roundsExtraDrag * Tuning.roundsAirControl / 0.02f) * dt;
            int wall = WallSide();
            float mx = c.input.Move().x;
            if (wall != 0 && Mathf.Sign(mx) == wall && Mathf.Abs(mx) > 0.3f && r < -3f) r = -3f;
            object dm = Reflect.Get(M, "directionManager");
            object up = Reflect.Get(dm, "up");
            if (r > 0f && up != null && !Reflect.Get<bool>(up, "able")) r = 0f;
            vy = r * S;
            SetY();
            Reflect.Set(Reflect.Get(M, "jumpManager"), "ableToLand", v.y > 0f);
            return false;
        }

        /// <summary>-1 wall on the left, +1 on the right, 0 none.</summary>
        public int WallSide()
        {
            Vector2 center = c.player.center;
            float reach = 25f + 14f;
            if (Physics2D.Raycast(center, Vector2.left, reach, WallMask).collider != null) return -1;
            if (Physics2D.Raycast(center, Vector2.right, reach, WallMask).collider != null) return 1;
            return 0;
        }

        /// <summary>A sideways shove (block dash, recoil) added to the ROUNDS momentum.</summary>
        public void Push(float x, float seconds)
        {
            vx += x;
        }

        public void Update(float dt) { }
    }

    /// <summary>ROUNDS health on Cuphead hearts: 34 hp per heart, regeneration, life steal, decay, respawns.</summary>
    public class BlobHealth
    {
        private readonly BlobController c;
        private float healBank;
        private int respawnsLeft;
        public float lastHitTime = -100f;

        public BlobHealth(BlobController c) { this.c = c; }

        private PlayerStatsManager S => c.player.stats;

        public int Hearts => Mathf.Clamp(Mathf.RoundToInt(c.build.maxHealth / Tuning.healthPerHeart), 1, (int)Tuning.maxHearts);

        public void ApplyBuild()
        {
            respawnsLeft = c.build.respawns;
            if (S == null) return;
            Reflect.Call(S, "CalculateHealthMax");
            if (Level.Current == null || Level.Current.LevelTime < 2f || S.Health > S.HealthMax)
                S.SetHealth(S.HealthMax);
        }

        public void Update(float dt)
        {
            if (c.build.regen > 0f) Heal(c.build.regen * dt);
        }

        public void OnDealtDamage(float cupheadDamage)
        {
            if (c.build.lifeSteal > 0f) Heal(cupheadDamage / Tuning.damageScale * c.build.lifeSteal);
        }

        /// <summary>Heals in ROUNDS hp; every 34 hp banked is one heart.</summary>
        public void Heal(float roundsHp)
        {
            if (S == null || roundsHp <= 0f || c.player.IsDead) return;
            healBank += roundsHp;
            while (healBank >= Tuning.healthPerHeart && S.Health < S.HealthMax)
            {
                healBank -= Tuning.healthPerHeart;
                S.SetHealth(S.Health + 1);
            }
            if (S.Health >= S.HealthMax) healBank = Mathf.Min(healBank, Tuning.healthPerHeart);
        }

        public bool InDecayGrace => c.build.secondsToTakeDamageOver > 0f && Time.time - lastHitTime < c.build.secondsToTakeDamageOver * 0.25f;

        public bool TryRespawn()
        {
            if (respawnsLeft <= 0) return false;
            respawnsLeft--;
            S.SetHealth(S.HealthMax);
            var cr = Reflect.Call(S, "hit_cr") as IEnumerator;
            if (cr != null) S.StartCoroutine(cr);
            c.body.FlashBlock(true);
            Plugin.L.LogInfo("respawn card used, " + respawnsLeft + " left");
            return true;
        }
    }

    /// <summary>ROUNDS block: invulnerability window, parry, cooldown, heal and dash on block, auto block.</summary>
    public class BlobBlock
    {
        private readonly BlobController c;
        public float cooldownLeft;
        private float windowLeft;
        private float parryWantedUntil = -1f;

        public BlobBlock(BlobController c)
        {
            this.c = c;
            c.player.motor.OnParrySuccess += () => cooldownLeft = 0f;
        }

        public bool IsBlocking => windowLeft > 0f;
        public bool WantsParry => Time.time < parryWantedUntil;
        public float CooldownTotal => Cooldown();
        public void ConsumeParry() { parryWantedUntil = -1f; }

        public float Cooldown()
        {
            return Mathf.Max(0.25f, Tuning.baseBlockCooldown * c.build.blockCdMultiplier + c.build.blockCdAdd);
        }

        public void Update(float dt)
        {
            cooldownLeft -= dt;
            windowLeft -= dt;
            if (c.input.BlockDown() && cooldownLeft <= 0f) DoBlock();
        }

        public void DoBlock()
        {
            windowLeft = Tuning.blockWindow + 0.12f * Mathf.Max(0, c.build.additionalBlocks);
            cooldownLeft = Cooldown();
            parryWantedUntil = Time.time + 0.12f;
            Heal();
            Dash();
            c.body.FlashBlock(false);
            c.body.MarkBlock();
            AudioManager.Play("player_parry_power_up");
        }

        public void Heal()
        {
            if (c.build.blockHealing > 0f) c.health.Heal(c.build.blockHealing);
        }

        public void Dash()
        {
            if (c.build.blockForce <= 0f) return;
            float dir = Mathf.Sign(c.input.Move().x);
            if (Mathf.Abs(c.input.Move().x) < 0.2f) dir = (int)c.player.motor.TrueLookDirection.x;
            c.motor.Push(dir * c.build.blockForce * Tuning.blockForceScale, 0.3f);
        }

        /// <summary>Auto-block cards: block an incoming hit by themselves when the block is ready.</summary>
        public bool AutoBlock()
        {
            if (!c.build.autoBlock || cooldownLeft > 0f) return false;
            DoBlock();
            return true;
        }
    }

    /// <summary>The blob's look: your ROUNDS face, team colour and ROUNDS gun drawn over the hidden Cuphead sprite.</summary>
    public class BlobBody
    {
        private readonly BlobController c;
        private readonly List<SpriteRenderer> cupheadRenderers = new List<SpriteRenderer>();
        private readonly GameObject root;
        private readonly SpriteRenderer outline, fill, ring, flash, gunR;
        private readonly Transform face, gunPivot;
        private readonly List<SpriteRenderer> faceR = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> pips = new List<SpriteRenderer>();
        private readonly SpriteRenderer[] feet = new SpriteRenderer[2];
        private float flashT = 10f;
        private bool flashGold;
        private float walkPhase;
        private Vector2 aim = Vector2.right;
        private readonly SpriteRenderer puppetR;
        private GameObject crosshair;
        private SpriteRenderer crossRing, crossDot;
        private Vector3 lastPos;
        private Vector2 smoothVel;
        private float groundY;
        private bool shotFlag, blockFlag, hitFlag;

        public void MarkShot() { shotFlag = true; }
        public void MarkBlock() { blockFlag = true; }
        public void MarkHit() { hitFlag = true; }

        /// <summary>True while the real ROUNDS character (animated by ROUNDS) is arriving for this player.</summary>
        public bool PuppetLive => Puppets.Live(c.index) && Puppets.SpriteFor(c.index) != null;

        public Vector2 AimDir => aim;
        public float Diameter => Tuning.blobDiameter * Mathf.Clamp(Mathf.Sqrt(c.build.sizeMultiplier), 0.7f, 1.8f);
        public Vector2 Center => c.player.center;

        public BlobBody(BlobController c)
        {
            this.c = c;
            foreach (var r in c.player.GetComponentsInChildren<SpriteRenderer>(true)) cupheadRenderers.Add(r);
            root = new GameObject("RoundsBlob");
            root.transform.SetParent(c.player.transform, false);
            // Cuphead's drawings of this player (hit and death poses, flashes, anything it adds later) stay off from right
            // before the cameras cull, whatever Cuphead's scripts and animations do to them in between.
            PlayerSprites.Track(c.player.transform, root.transform);
            outline = Make("outline", Gfx.Disc(), 0);
            fill = Make("fill", Gfx.Disc(), 1);
            face = new GameObject("face").transform;
            face.SetParent(root.transform, false);
            gunPivot = new GameObject("gun").transform;
            gunPivot.SetParent(root.transform, false);
            gunR = new GameObject("gunSprite").AddComponent<SpriteRenderer>();
            gunR.transform.SetParent(gunPivot, false);
            ring = Make("blockRing", Gfx.Ring(), 4);
            flash = Make("blockFlash", Gfx.Ring(), 5);
            for (int i = 0; i < 2; i++) feet[i] = Make("foot", Gfx.Disc(), -1);
            puppetR = Make("roundsCharacter", null, 2);
            if (c.index == 0)
            {
                crosshair = new GameObject("RoundsCrosshair");
                crossRing = crosshair.AddComponent<SpriteRenderer>();
                crossRing.sprite = Gfx.Ring();
                var dot = new GameObject("dot");
                dot.transform.SetParent(crosshair.transform, false);
                dot.transform.localScale = Vector3.one * 0.22f;
                crossDot = dot.AddComponent<SpriteRenderer>();
                crossDot.sprite = Gfx.Disc();
            }
            lastPos = c.player.transform.position;
            groundY = lastPos.y;
            ApplyLook();
        }

        private SpriteRenderer Make(string name, Sprite s, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = s;
            r.sortingOrder = order;
            return r;
        }

        private SpriteRenderer Ref
        {
            get
            {
                foreach (var r in cupheadRenderers) if (r != null) return r;
                return null;
            }
        }

        /// <summary>The player's blob colour (its ROUNDS skin, else the team colour) and the darker shade for its outline.</summary>
        public static void Colors(int index, SkinData skin, out Color col, out Color shade)
        {
            Color team = index == 0 ? new Color(1f, 0.55f, 0.1f) : new Color(0.2f, 0.5f, 1f);
            col = Gfx.Hex(skin != null ? skin.color : null, team);
            shade = Gfx.Hex(skin != null ? skin.shade : null, col * 0.55f);
            shade.a = 1f;
        }

        /// <summary>The ROUNDS face (its skin's parts, else two eyes) as sprites under 'face', added to faceR.</summary>
        public static void BuildFace(Transform face, SkinData skin, Color shade, List<SpriteRenderer> faceR)
        {
            int before = faceR.Count;
            if (skin != null && skin.faceParts != null)
            {
                int order = 2;
                // Keep the face inside the blob even if ROUNDS's measurements are off.
                float widest = 0f;
                foreach (SkinPart part in skin.faceParts)
                    widest = Mathf.Max(widest, part.w);
                float fit = widest > 1.05f ? 1f / widest : 1f;
                foreach (SkinPart part in skin.faceParts)
                {
                    Color tint = Gfx.Hex(part.color, Color.white);
                    if (tint.a < 0.05f) continue;
                    Texture2D tex = Gfx.FromPng(part.png);
                    Sprite sp = Gfx.ToSprite(tex);
                    if (sp == null) continue;
                    var go = new GameObject("facePart");
                    go.transform.SetParent(face, false);
                    go.transform.localPosition = new Vector3(part.x * fit, part.y * fit, 0f);
                    go.transform.localScale = new Vector3(part.w * fit, part.h * fit * tex.width / Mathf.Max(1f, tex.height), 1f);
                    var r = go.AddComponent<SpriteRenderer>();
                    r.sprite = sp;
                    // ROUNDS draws face features dark on the blob; white sprites take the blob's dark tone.
                    r.color = (tint.r + tint.g + tint.b) > 2.7f ? new Color(shade.r * 0.35f, shade.g * 0.35f, shade.b * 0.35f, 1f) : tint;
                    r.sortingOrder = order++;
                    faceR.Add(r);
                }
            }
            if (faceR.Count == before) DefaultFace(face, faceR);
        }

        public void ApplyLook()
        {
            if (root == null) return;
            SkinData skin = Plugin.Skins[c.index];
            Color col, shade;
            Colors(c.index, skin, out col, out shade);
            fill.color = col;
            outline.color = shade;
            foreach (var f in feet) f.color = shade;
            foreach (var r in faceR) Object.Destroy(r.gameObject);
            faceR.Clear();
            BuildFace(face, skin, shade, faceR);
            Texture2D gt = skin != null ? Gfx.FromPng(skin.gun.png) : null;
            Sprite gs = Gfx.ToSprite(gt);
            if (gs != null && skin.gun.w > 0.01f)
            {
                Color gc = Gfx.Hex(skin.gun.color, Color.white);
                gunR.sprite = gs;
                gunR.color = (gc.r + gc.g + gc.b) > 2.7f ? new Color(0.16f, 0.16f, 0.16f) : gc;
                gunR.transform.localScale = new Vector3(skin.gun.w, Mathf.Max(0.06f, skin.gun.h) * gt.width / Mathf.Max(1f, gt.height), 1f);
                gunR.transform.localPosition = new Vector3(Mathf.Max(0.1f, skin.gun.x), 0f, 0f);
            }
            else
            {
                gunR.sprite = Gfx.ToSprite(Gfx.White);
                gunR.color = new Color(0.16f, 0.16f, 0.16f);
                gunR.transform.localScale = new Vector3(0.45f, 0.14f, 1f);
                gunR.transform.localPosition = new Vector3(0.22f, 0f, 0f);
            }
            gunR.sortingOrder = 3;
            RebuildPips();
            baseColors.Clear();
            baseColors[fill] = fill.color;
            baseColors[outline] = outline.color;
            baseColors[gunR] = gunR.color;
            foreach (var f in feet) baseColors[f] = f.color;
            foreach (var r in faceR) baseColors[r] = r.color;
        }

        private readonly Dictionary<SpriteRenderer, Color> baseColors = new Dictionary<SpriteRenderer, Color>();

        private static void DefaultFace(Transform face, List<SpriteRenderer> faceR)
        {
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("eye");
                go.transform.SetParent(face, false);
                go.transform.localPosition = new Vector3(i == 0 ? -0.12f : 0.14f, 0.1f, 0f);
                go.transform.localScale = new Vector3(0.11f, 0.16f, 1f);
                var r = go.AddComponent<SpriteRenderer>();
                r.sprite = Gfx.Disc();
                r.color = new Color(0.1f, 0.1f, 0.1f);
                r.sortingOrder = 2;
                faceR.Add(r);
            }
        }

        private void RebuildPips()
        {
            int n = Mathf.Clamp(c.build.maxAmmo, 1, 30);
            while (pips.Count < n) pips.Add(Make("ammo", Gfx.Disc(), 6));
            while (pips.Count > n)
            {
                Object.Destroy(pips[pips.Count - 1].gameObject);
                pips.RemoveAt(pips.Count - 1);
            }
        }

        public void FlashBlock(bool gold)
        {
            flashT = 0f;
            flashGold = gold;
        }

        public void ApplyRecoil()
        {
            float push = -aim.x * Tuning.recoilPush * c.build.recoilMuiltiplier;
            c.motor.Push(push, 0.1f);
        }

        /// <summary>ROUNDS legs reach 1.09 body widths below the body; Cuphead's centre is 0.74 widths above its feet.</summary>
        public const float PuppetLift = 0.35f;

        public Vector2 Muzzle => PuppetLive
            ? Center + Vector2.up * Diameter * PuppetLift + aim * (Diameter * 1.15f)
            : Center + aim * (Diameter * 0.62f);

        /// <summary>The blob's pose in ROUNDS units, sent to ROUNDS to animate its player character.</summary>
        private void SendPose(Vector2 center)
        {
            Vector3 pos = c.player.transform.position;
            float dt = Mathf.Max(0.001f, Time.deltaTime);
            Vector2 v = (pos - lastPos) / dt;
            lastPos = pos;
            if (v.sqrMagnitude > 4000f * 4000f) v = Vector2.zero;
            smoothVel = Vector2.Lerp(smoothVel, v, Mathf.Clamp01(dt * 20f));
            bool grounded = c.player.motor.Grounded;
            if (grounded) groundY = pos.y;
            float k = Puppets.CupheadPerRounds;
            Puppets.Send(c.index, new Request
            {
                vx = smoothVel.x / k,
                vy = smoothVel.y / k,
                height = Mathf.Clamp((pos.y - groundY) / k, 0f, 8f),
                ax = aim.x,
                ay = aim.y,
                grounded = grounded,
                shoot = shotFlag,
                block = blockFlag,
                hit = hitFlag,
                dead = c.player.IsDead
            });
            shotFlag = blockFlag = hitFlag = false;
        }

        public void LateUpdate()
        {
            foreach (var r in cupheadRenderers) if (r != null) r.enabled = false;
            if (c.Arena || ArenaClient.Waiting(c))
            {
                root.SetActive(false);
                SpriteRenderer rr = Ref;
                UpdateCrosshair(rr != null ? rr.sortingLayerID : 0, (rr != null ? rr.sortingOrder : 0) + 32000);
                return;
            }
            bool show = !c.player.IsDead && c.player.gameObject.activeInHierarchy;
            root.SetActive(show);
            if (!show) { if (crosshair != null) crosshair.SetActive(false); return; }
            SpriteRenderer reference = Ref;
            int layer = reference != null ? reference.sortingLayerID : 0;
            int baseOrder = reference != null ? reference.sortingOrder : 0;
            float alpha = reference != null ? reference.color.a : 1f;
            float d = Diameter;
            bool duck = c.player.motor.Ducking;
            Vector2 center = Center;
            if (duck) center.y -= d * 0.2f;
            root.transform.position = new Vector3(center.x, center.y, c.player.transform.position.z);
            root.transform.localScale = Vector3.one;
            float squash = duck ? 0.62f : 1f;
            aim = c.input.Aim(center);
            float facing = aim.x < 0f ? -1f : 1f;
            SendPose(center);
            bool live = PuppetLive;
            outline.gameObject.SetActive(!live);
            fill.gameObject.SetActive(!live);
            face.gameObject.SetActive(!live);
            gunPivot.gameObject.SetActive(!live);
            foreach (var f in feet) f.gameObject.SetActive(!live);
            ring.gameObject.SetActive(!live);
            puppetR.gameObject.SetActive(live);
            if (live)
            {
                puppetR.sprite = Puppets.SpriteFor(c.index);
                float size = d / Tuning.blobDiameter;
                puppetR.transform.localPosition = new Vector3(0f, d * PuppetLift + (duck ? d * 0.2f : 0f), 0f);
                puppetR.transform.localScale = new Vector3(size, size * squash, 1f);
                puppetR.color = new Color(1f, 1f, 1f, alpha <= 0.01f ? 0.35f : 1f);
            }

            outline.transform.localScale = new Vector3(d * 1.08f, d * 1.08f * squash, 1f);
            fill.transform.localScale = new Vector3(d, d * squash, 1f);
            face.localScale = new Vector3(d * facing, d * squash, 1f);
            face.localPosition = new Vector3(aim.x * d * 0.06f, aim.y * d * 0.05f, 0f);

            float ang = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;
            gunPivot.localPosition = new Vector3(aim.x * d * 0.45f, aim.y * d * 0.45f * squash, 0f);
            gunPivot.localRotation = Quaternion.Euler(0f, 0f, ang);
            gunPivot.localScale = new Vector3(d, d * (facing < 0f ? -1f : 1f), 1f);

            float speed = Mathf.Abs(c.input.Move().x);
            walkPhase += (c.player.motor.Grounded ? speed : 0f) * Time.deltaTime * 14f;
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                float lift = c.player.motor.Grounded ? Mathf.Max(0f, Mathf.Sin(walkPhase + i * Mathf.PI)) * d * 0.08f : d * 0.05f;
                feet[i].transform.localPosition = new Vector3(side * d * 0.2f, -d * 0.5f * squash + lift, 0f);
                feet[i].transform.localScale = new Vector3(d * 0.2f, d * 0.12f, 1f);
            }

            float cdTotal = c.block.CooldownTotal;
            float ready = Mathf.Clamp01(1f - c.block.cooldownLeft / Mathf.Max(0.01f, cdTotal));
            ring.transform.localScale = Vector3.one * d * 1.3f;
            ring.color = new Color(1f, 1f, 1f, ready >= 1f ? 0f : 0.12f + 0.25f * ready);
            flashT += Time.deltaTime;
            float ft = Mathf.Clamp01(flashT / 0.3f);
            flash.transform.localScale = Vector3.one * d * Mathf.Lerp(1.1f, 1.9f, ft);
            flash.color = flashGold ? new Color(1f, 0.85f, 0.2f, 1f - ft) : new Color(1f, 1f, 1f, 1f - ft);

            int ammo = c.gun.Ammo;
            float reload = c.gun.ReloadProgress;
            for (int i = 0; i < pips.Count; i++)
            {
                float a = (i - (pips.Count - 1) * 0.5f) * 0.16f;
                pips[i].transform.localPosition = new Vector3(Mathf.Sin(a) * d * 0.75f, -Mathf.Cos(a) * d * 0.75f * squash - d * 0.05f, 0f);
                pips[i].transform.localScale = Vector3.one * d * 0.09f;
                bool full = i < ammo || (reload >= 0f && i < Mathf.FloorToInt(reload * pips.Count));
                pips[i].color = full ? new Color(1f, 1f, 1f, 0.95f) : new Color(0f, 0f, 0f, 0.45f);
            }

            float k = alpha <= 0.01f ? 0.35f : 1f;
            foreach (var r in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                r.sortingLayerID = layer;
                Color col;
                if (!baseColors.TryGetValue(r, out col)) col = r.color;
                col.a *= k;
                r.color = col;
            }
            SetOrder(puppetR, baseOrder, 1);
            SetOrder(outline, baseOrder, 0);
            SetOrder(fill, baseOrder, 1);
            for (int i = 0; i < faceR.Count; i++) SetOrder(faceR[i], baseOrder, 2 + i);
            SetOrder(gunR, baseOrder, 2 + faceR.Count);
            foreach (var f in feet) SetOrder(f, baseOrder, -1);
            SetOrder(ring, baseOrder, 3 + faceR.Count);
            SetOrder(flash, baseOrder, 4 + faceR.Count);
            foreach (var p in pips) SetOrder(p, baseOrder, 5 + faceR.Count);
            UpdateCrosshair(layer, baseOrder);
        }

        /// <summary>ROUNDS-style crosshair at the mouse for player 1 (Cuphead hides the system cursor).</summary>
        public void UpdateCrosshair(int layer, int baseOrder)
        {
            if (crosshair == null) return;
            bool on = c.input.MouseActive && !c.player.IsDead && c.player.gameObject.activeInHierarchy;
            crosshair.SetActive(on);
            if (!on) return;
            Vector2 w = c.input.MouseWorld();
            crosshair.transform.position = new Vector3(w.x, w.y, c.player.transform.position.z);
            crosshair.transform.localScale = Vector3.one * 46f;
            crossRing.color = new Color(1f, 1f, 1f, 0.9f);
            crossDot.color = new Color(1f, 1f, 1f, 0.9f);
            crossRing.sortingLayerID = crossDot.sortingLayerID = layer;
            crossRing.sortingOrder = baseOrder + 200;
            crossDot.sortingOrder = baseOrder + 201;
        }

        private static void SetOrder(SpriteRenderer r, int baseOrder, int offset)
        {
            r.sortingOrder = baseOrder + offset;
        }

        public void Destroy()
        {
            if (c != null && c.player != null) PlayerSprites.Untrack(c.player.transform);
            if (crosshair != null) Object.Destroy(crosshair);
            if (root != null) Object.Destroy(root);
        }
    }
}
