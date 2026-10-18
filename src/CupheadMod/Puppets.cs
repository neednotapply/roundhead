using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The real ROUNDS character for each player: Cuphead sends the blob's pose, the ROUNDS card engine animates
    /// ROUNDS's own player model with it and streams the rendered frames back here (sheets/ipc.json, pose and frames).
    /// </summary>
    public static class Puppets
    {
        private static readonly Texture2D[] tex = new Texture2D[2];
        private static readonly Sprite[] sprites = new Sprite[2];
        private static readonly float[] lastFrame = { -10f, -10f };
        private static readonly float[] lastSend = { -10f, -10f };
        private static FrameClient client;

        /// <summary>Cuphead units per ROUNDS unit: the ROUNDS body (1.4 units) is as wide as the blob.</summary>
        public static float CupheadPerRounds => Tuning.blobDiameter / Proto.BodyUnits;

        public static void Start()
        {
            if (client != null) return;
            client = new FrameClient();
            client.Start();
        }

        /// <summary>Sends a pose (ROUNDS units) at up to 60 per second.</summary>
        public static void Send(int slot, Request pose)
        {
            if (Plugin.Engine == null || !Plugin.Engine.Connected) return;
            bool urgent = pose.shoot || pose.block || pose.hit;
            if (!urgent && Time.unscaledTime - lastSend[slot] < 1f / 60f) return;
            lastSend[slot] = Time.unscaledTime;
            pose.op = Ops.Pose;
            pose.slot = slot;
            Plugin.Engine.Pose(pose);
        }

        /// <summary>Uploads any new frames; call once per frame.</summary>
        public static void Pump()
        {
            if (client == null) return;
            for (int slot = 0; slot < 2; slot++)
            {
                byte[] px = client.Take(slot);
                if (px == null) continue;
                if (tex[slot] == null)
                {
                    tex[slot] = new Texture2D(Proto.FrameSize, Proto.FrameSize, TextureFormat.RGBA32, false);
                    tex[slot].filterMode = FilterMode.Bilinear;
                    tex[slot].wrapMode = TextureWrapMode.Clamp;
                    float ppu = Proto.FrameSize / (Proto.FrameUnits * CupheadPerRounds);
                    // The ROUNDS camera sits 0.24 units below the body centre, so the pivot is the body centre.
                    float pivotY = 0.5f + 0.24f / Proto.FrameUnits;
                    sprites[slot] = Sprite.Create(tex[slot], new Rect(0, 0, Proto.FrameSize, Proto.FrameSize), new Vector2(0.5f, pivotY), ppu);
                }
                if (px.Length == Proto.FrameSize * Proto.FrameSize * 4)
                {
                    tex[slot].LoadRawTextureData(px);
                    tex[slot].Apply(false);
                    lastFrame[slot] = Time.unscaledTime;
                }
            }
        }

        public static bool Live(int slot) => Time.unscaledTime - lastFrame[slot] < 0.6f;

        public static void Stop() { client?.Stop(); }

        public static Sprite SpriteFor(int slot) => sprites[slot];
    }

    /// <summary>Reads the ROUNDS frame stream on its own thread and keeps only the newest frame per player.</summary>
    public class FrameClient
    {
        private readonly object gate = new object();
        private readonly byte[][] latest = new byte[2][];
        private Thread thread;
        private volatile TcpClient current;
        private volatile bool stopped;

        public void Stop()
        {
            stopped = true;
            try { current?.Close(); } catch { }
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Roundhead frames" };
            thread.Start();
        }

        public byte[] Take(int slot)
        {
            lock (gate)
            {
                byte[] px = latest[slot];
                latest[slot] = null;
                return px;
            }
        }

        private static int ReadPort()
        {
            try
            {
                string p = Path.Combine(Proto.DataDir(), Proto.FramesPortFileName);
                return File.Exists(p) ? int.Parse(File.ReadAllText(p).Trim()) : 0;
            }
            catch { return 0; }
        }

        private static bool ReadFully(NetworkStream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public void Run()
        {
            var head = new byte[4];
            while (true)
            {
                int port = ReadPort();
                if (port <= 0) { Thread.Sleep(1000); continue; }
                try
                {
                    if (stopped) return;
                    using (var tcp = new TcpClient())
                    {
                        current = tcp;
                        tcp.ReceiveBufferSize = 1 << 20;
                        tcp.Connect("127.0.0.1", port);
                        var s = tcp.GetStream();
                        while (true)
                        {
                            if (!ReadFully(s, head, 4)) break;
                            int len = BitConverter.ToInt32(head, 0);
                            if (len < 5 || len > 16 << 20) break;
                            var body = new byte[len];
                            if (!ReadFully(s, body, len)) break;
                            int slot = body[0];
                            if (slot > 1) continue;
                            var px = new byte[len - 5];
                            Buffer.BlockCopy(body, 5, px, 0, px.Length);
                            lock (gate) latest[slot] = px;
                        }
                    }
                }
                catch { }
                Thread.Sleep(1000);
            }
        }
    }
}
