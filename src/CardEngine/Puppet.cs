using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The real ROUNDS player character, spawned from ROUNDS's own player prefab far away from the menu and animated by
    /// ROUNDS's own scripts (particle skin, face, IK legs and arms, gun sway, block orb). Gameplay scripts are switched
    /// off; the pose comes from the blob in Cuphead. Each frame it is rendered with its own camera and streamed to Cuphead.
    /// </summary>
    public class Puppet
    {
        private const int Layer = 30;
        private static readonly HashSet<string> Animate = new HashSet<string>
        {
            "SetTeamColor", "PlayerSkinHandler", "SetPlayerSpriteLayer", "PlayerSkinParticle", "CharacterCreatorItemEquipper",
            "CharacterItem", "CharacterItemMirror", "IKArmMove", "LimbIK", "LegRenderer", "IkLeg", "LegRotator", "ArmRotator",
            "FollowInactiveHand", "Unparent", "FollowTransform", "FollowScale", "PlayerWobblePosition", "CodeAnimation",
            "RightLeftMirrorSpring", "HandPos", "Holdable", "Rotate"
        };
        private static readonly FieldInfo VelField = typeof(PlayerVelocity).GetField("velocity", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public readonly int slot;
        private GameObject root;
        private CharacterData data;
        private PlayerVelocity vel;
        private Holding holding;
        private Transform holdingObject;
        private GameObject ground;
        private Camera cam;
        private RenderTexture rt;
        private Texture2D read;
        private Vector3 basePos;
        private float scale = 1.2f;
        private float x;
        private float shownHeight;
        private bool wasGrounded = true;
        private float layerRefresh;
        private Request pose = new Request { grounded = true, ax = 1f };
        private float lastPose = -10f;
        private readonly List<Transform> owned = new List<Transform>();
        private Vector3 gunVel;
        private Transform handPos;
        public bool Ok { get; private set; }

        public Puppet(int slot) { this.slot = slot; }

        public void SetPose(Request p)
        {
            pose = p;
            lastPose = Time.realtimeSinceStartup;
        }

        public bool Active => Ok && frames > 30 && Time.realtimeSinceStartup - lastPose < 3f;

        public bool Create()
        {
            try
            {
                var pa = PlayerAssigner.instance ?? Resources.FindObjectsOfTypeAll<PlayerAssigner>().FirstOrDefault();
                if (pa == null || pa.playerPrefab == null) { EnginePlugin.L.LogWarning("puppet: no ROUNDS player prefab"); return false; }
                basePos = new Vector3(-7000f - slot * 400f, 7000f, 0f);
                root = UnityEngine.Object.Instantiate(pa.playerPrefab, basePos, Quaternion.identity);
                root.name = "Roundhead puppet " + (slot + 1);
                scale = Mathf.Abs(root.transform.localScale.x);
                data = root.GetComponent<CharacterData>();
                vel = root.GetComponent<PlayerVelocity>();
                holding = root.GetComponent<Holding>();
                var player = root.GetComponent<Player>();
                if (player != null) { player.playerID = slot; player.teamID = slot; }
                if (data != null) { data.isPlaying = false; data.isGrounded = true; }
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true)) Keep(mb);
                if (holding != null && holding.holdable != null)
                {
                    foreach (var mb in holding.holdable.GetComponentsInChildren<MonoBehaviour>(true)) Keep(mb);
                    var rig = holding.holdable.GetComponent<Rigidbody2D>();
                    if (rig != null) { rig.gravityScale = 0f; rig.isKinematic = true; }
                    holding.holdable.transform.position = basePos;
                    owned.Add(holding.holdable.transform);
                }
                foreach (string off in new[] { "WobbleObjects/Healthbar", "WobbleObjects/Canvas_Chat", "LevelMapper", "Effects", "ObjectCollider" })
                {
                    var t = root.transform.Find(off);
                    if (t != null) t.gameObject.SetActive(false);
                }
                foreach (var col in root.GetComponentsInChildren<Collider2D>(true)) col.enabled = false;
                holdingObject = root.GetComponentInChildren<HoldingObject>(true)?.transform;
                handPos = root.GetComponentInChildren<HandPos>(true)?.transform;
                owned.Add(root.transform);
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (t.parent == root.transform) owned.Add(t);
                SetLayers();

                // Ground under the puppet's feet, on a layer the IK legs look for.
                int groundLayer = 0;
                var leg = root.GetComponentInChildren<IkLeg>(true);
                if (leg != null && leg.mask.value != 0)
                    for (int i = 0; i < 32; i++) if ((leg.mask.value & (1 << i)) != 0) { groundLayer = i; break; }
                ground = new GameObject("Roundhead puppet ground " + (slot + 1));
                ground.layer = groundLayer;
                var box = ground.AddComponent<BoxCollider2D>();
                box.size = new Vector2(200000f, 2f);
                ground.transform.position = new Vector3(basePos.x, GroundTop - 1f, 0f);

                var cgo = new GameObject("Roundhead puppet camera " + (slot + 1));
                UnityEngine.Object.DontDestroyOnLoad(cgo);
                cam = cgo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = Proto.FrameUnits / 2f;
                cam.aspect = 1f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << Layer;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 200f;
                rt = new RenderTexture(Proto.FrameSize, Proto.FrameSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt;
                read = new Texture2D(Proto.FrameSize, Proto.FrameSize, TextureFormat.RGBA32, false);
                UnityEngine.Object.DontDestroyOnLoad(root);
                UnityEngine.Object.DontDestroyOnLoad(ground);
                Ok = true;
                EnginePlugin.L.LogInfo("puppet " + (slot + 1) + " ready (ROUNDS player scale " + scale + ")");
                return true;
            }
            catch (Exception e)
            {
                EnginePlugin.L.LogError("puppet " + (slot + 1) + " failed: " + e);
                return false;
            }
        }

        private float GroundTop => basePos.y - 1.27f * scale;

        private static void Keep(MonoBehaviour mb)
        {
            if (mb == null) return;
            if (!Animate.Contains(mb.GetType().Name)) mb.enabled = false;
        }

        private void SetLayers()
        {
            foreach (var t in owned)
                if (t != null)
                    foreach (var c in t.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = Layer;
        }

        /// <summary>The first frames: give it its team colours and the player's saved face.</summary>
        private bool dressed;
        private int frames;

        private void Dress()
        {
            dressed = true;
            try
            {
                PlayerSkin colors = PlayerSkinBank.GetPlayerSkinColors(slot);
                SetTeamColor.TeamColorThis(root, colors);
                // The gun in gun black over the body (Holding is off here, so ROUNDS never colours it itself).
                if (holding != null && holding.holdable != null) GunLook.Blacken(holding.holdable, 50);
                var eq = root.GetComponentInChildren<CharacterCreatorItemEquipper>(true);
                var handler = CharacterCreatorHandler.instance ?? Resources.FindObjectsOfTypeAll<CharacterCreatorHandler>().FirstOrDefault();
                PlayerFace face = handler != null && handler.selectedPlayerFaces != null && slot < handler.selectedPlayerFaces.Length ? handler.selectedPlayerFaces[slot] : null;
                if (eq != null && face != null) eq.EquipFace(face);
            }
            catch (Exception e) { EnginePlugin.L.LogWarning("puppet dress: " + e.Message); }
        }

        public void Tick(float dt)
        {
            if (!Ok || root == null) return;
            frames++;
            if (!dressed && frames > 2) Dress();
            if (Time.realtimeSinceStartup > layerRefresh)
            {
                layerRefresh = Time.realtimeSinceStartup + 1f;
                // Objects ROUNDS spawns or unparents at runtime (skin particles, legs, gun) join the puppet layer.
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
                foreach (var u in UnityEngine.Object.FindObjectsOfType<Unparent>())
                    if (u.parent == root.transform) foreach (var c in u.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = Layer;
                if (holding != null && holding.holdable != null) foreach (var c in holding.holdable.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = Layer;
            }
            Request p = pose;
            // Flying stays in place (the plane moves on Cuphead's side) high above the puppet's floor, and the body velocity
            // ROUNDS's legs read is pointed back and down: in the air ROUNDS puts each foot at its rest spot plus that
            // velocity x 0.08 (IkLeg.AirFootMovement), so the legs dangle behind as the character flies.
            if (!p.fly) x += p.vx * dt;
            float target = p.fly ? Mathf.Max(Tuning.flyHover, p.height) : Mathf.Max(0f, p.height);
            shownHeight = Mathf.Lerp(shownHeight, target, Mathf.Clamp01(dt * 30f));
            root.transform.position = new Vector3(basePos.x + x, basePos.y + shownHeight, 0f);
            Vector2 legVel = p.fly ? new Vector2(p.vx * 0.3f - Tuning.flyLegTrail, p.vy * 0.3f - Tuning.flyLegDrop) : new Vector2(p.vx, p.vy);
            if (vel != null && VelField != null) VelField.SetValue(vel, legVel);
            if (data != null)
            {
                data.isGrounded = p.grounded && !p.fly;
                data.sinceGrounded = p.grounded ? 0f : data.sinceGrounded + dt;
                data.isWallGrab = false;
                var aim = new Vector3(p.ax, p.ay, 0f);
                if (aim.sqrMagnitude < 0.01f) aim = Vector3.right;
                data.aimDirection = aim.normalized;
            }
            if (holdingObject != null)
            {
                var aim = new Vector3(p.ax, p.ay, 0f);
                if (aim.sqrMagnitude > 0.01f) holdingObject.rotation = Quaternion.LookRotation(aim.normalized, Vector3.back);
            }
            MoveGun(dt, p);
            bool grounded = p.grounded && !p.fly;
            if (grounded && !wasGrounded) Play("LandSmoke");
            if (!grounded && wasGrounded && p.vy > 0.5f && !p.fly) { Play("JumpSmoke"); Play("Jump"); }
            wasGrounded = grounded;
            if (p.shoot) gunVel -= data.aimDirection * 9f;
            if (p.block) Play("Block");
            if (p.hit) root.GetComponentInChildren<PlayerSkinHandler>(true)?.BlinkColor(Color.white * 0.95f);
            pose.shoot = pose.block = pose.hit = false;
        }

        /// <summary>ROUNDS's Holding spring (same force and drag), done by hand: the gun trails the hand and points along the aim.</summary>
        private void MoveGun(float dt, Request p)
        {
            if (holding == null || holding.holdable == null || handPos == null) return;
            Transform gun = holding.holdable.transform;
            Vector3 target = handPos.position + new Vector3(p.vx, p.vy, 0f) * 0.04f;
            float step = Mathf.Min(dt, 0.033f);
            gunVel += (target - gun.position) * holding.force * step;
            gunVel -= gunVel * Mathf.Clamp01(holding.drag * step);
            gun.position += gunVel * step;
            if ((gun.position - target).sqrMagnitude > 4f) { gun.position = target; gunVel = Vector3.zero; }
            gun.rotation = Quaternion.LookRotation(Vector3.forward, handPos.forward);
        }

        private void Play(string name)
        {
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
                if (ps.name == name) { ps.gameObject.SetActive(true); ps.Play(); }
            foreach (var u in UnityEngine.Object.FindObjectsOfType<Unparent>())
                if (u.parent == root.transform)
                    foreach (var ps in u.GetComponentsInChildren<ParticleSystem>(true))
                        if (ps.name == name) { ps.transform.position = root.transform.position + Vector3.down * 1.2f * scale; ps.Play(); }
        }

        /// <summary>Renders the character and returns its RGBA pixels (bottom row first), or null.</summary>
        public byte[] Render()
        {
            if (!Ok || root == null) return null;
            cam.transform.position = root.transform.position + new Vector3(0f, -0.2f * scale, -50f);
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, Proto.FrameSize, Proto.FrameSize), 0, 0);
            read.Apply(false);
            RenderTexture.active = prev;
            return read.GetRawTextureData();
        }
    }

    /// <summary>Streams puppet frames to Cuphead on a second localhost socket (sheets/ipc.json, frames).</summary>
    public class FrameServer
    {
        private TcpListener listener;
        private readonly List<NetworkStream> clients = new List<NetworkStream>();
        private readonly object gate = new object();
        private Thread thread;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(Path.Combine(Proto.DataDir(), Proto.FramesPortFileName), port.ToString());
            thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var c = listener.AcceptTcpClient();
                        c.NoDelay = true;
                        c.SendBufferSize = 1 << 20;
                        lock (gate) clients.Add(c.GetStream());
                    }
                    catch { break; }
                }
            }) { IsBackground = true, Name = "Roundhead frames" };
            thread.Start();
            EnginePlugin.L.LogInfo("Roundhead frame stream on 127.0.0.1:" + port);
        }

        public bool HasClients { get { lock (gate) return clients.Count > 0; } }

        private readonly byte[][] latest = new byte[2][];
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread sender;

        /// <summary>Hands the newest frame of a slot to the sender thread (the game thread never waits on the socket).</summary>
        public void Broadcast(int slot, byte[] pixels)
        {
            if (pixels == null || slot < 0 || slot > 1) return;
            lock (gate) latest[slot] = pixels;
            if (sender == null)
            {
                sender = new Thread(SendLoop) { IsBackground = true, Name = "Roundhead frame sender" };
                sender.Start();
            }
            wake.Set();
        }

        private void SendLoop()
        {
            var header = new byte[9];
            while (true)
            {
                wake.WaitOne(200);
                for (int slot = 0; slot < 2; slot++)
                {
                    byte[] px;
                    NetworkStream[] targets;
                    lock (gate)
                    {
                        px = latest[slot];
                        latest[slot] = null;
                        targets = clients.ToArray();
                    }
                    if (px == null) continue;
                    BitConverter.GetBytes(5 + px.Length).CopyTo(header, 0);
                    header[4] = (byte)slot;
                    BitConverter.GetBytes((ushort)Proto.FrameSize).CopyTo(header, 5);
                    BitConverter.GetBytes((ushort)Proto.FrameSize).CopyTo(header, 7);
                    foreach (var c in targets)
                    {
                        try
                        {
                            c.Write(header, 0, header.Length);
                            c.Write(px, 0, px.Length);
                        }
                        catch { lock (gate) clients.Remove(c); }
                    }
                }
            }
        }

        public void Stop()
        {
            try { listener?.Stop(); } catch { }
            try { File.Delete(Path.Combine(Proto.DataDir(), Proto.FramesPortFileName)); } catch { }
        }
    }
}
