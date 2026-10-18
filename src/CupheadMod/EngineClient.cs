using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Talks to the ROUNDS card engine running in the ROUNDS process next to Cuphead.
    /// A background thread owns the socket; JSON is parsed and callbacks run on Unity's main thread in Pump().
    /// </summary>
    public class EngineClient
    {
        private readonly object gate = new object();
        private readonly Queue<string> outbox = new Queue<string>();
        private readonly Queue<string> inbox = new Queue<string>();
        private readonly Dictionary<int, Action<Response>> waiting = new Dictionary<int, Action<Response>>();
        private Thread thread;
        private volatile bool running;
        private volatile bool connected;
        private int nextRid = 1;

        public bool Connected => connected;

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "Roundhead engine link" };
            thread.Start();
        }

        private volatile TcpClient current;

        public void Stop()
        {
            running = false;
            try { current?.Close(); } catch { }
        }

        private static int ReadPort()
        {
            try
            {
                string p = Path.Combine(Proto.DataDir(), Proto.PortFileName);
                if (!File.Exists(p)) return 0;
                return int.Parse(File.ReadAllText(p).Trim());
            }
            catch { return 0; }
        }

        private int triedPort;
        private float lastFailLog = -100f;

        private void Run()
        {
            while (running)
            {
                int port = ReadPort();
                if (port <= 0) { Thread.Sleep(1000); continue; }
                TcpClient tcp = null;
                try
                {
                    tcp = new TcpClient();
                    current = tcp;
                    tcp.NoDelay = true;
                    if (port != triedPort) { triedPort = port; Plugin.L.LogInfo("engine link: connecting to 127.0.0.1:" + port + " (" + Path.Combine(Proto.DataDir(), Proto.PortFileName) + ")"); }
                    tcp.Connect("127.0.0.1", port);
                    NetworkStream ns = tcp.GetStream();
                    var reader = new StreamReader(ns, new UTF8Encoding(false));
                    var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };
                    connected = true;
                    var readThread = new Thread(() =>
                    {
                        try
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                                lock (gate) inbox.Enqueue(line);
                        }
                        catch { }
                        connected = false;
                    }) { IsBackground = true };
                    readThread.Start();
                    while (running && connected)
                    {
                        string send = null;
                        lock (gate) if (outbox.Count > 0) send = outbox.Dequeue();
                        if (send != null) writer.WriteLine(send);
                        else Thread.Sleep(5);
                    }
                }
                catch (Exception e)
                {
                    // Rate-limited: a connection refused repeats every second while the engine starts.
                    float t = Environment.TickCount / 1000f;
                    if (t - lastFailLog > 10f) { lastFailLog = t; Plugin.L.LogInfo("engine link: " + e.GetType().Name + ": " + e.Message); }
                }
                connected = false;
                try { tcp?.Close(); } catch { }
                lock (gate)
                {
                    outbox.Clear();
                }
                Thread.Sleep(1000);
            }
        }

        /// <summary>Runs callbacks for answers that arrived; call every frame from the main thread.</summary>
        public void Pump()
        {
            while (true)
            {
                string line = null;
                lock (gate) if (inbox.Count > 0) line = inbox.Dequeue();
                if (line == null) break;
                Response r;
                try { r = Json.Read<Response>(line); }
                catch (Exception e) { Plugin.L.LogWarning("bad engine line: " + e.Message); continue; }
                Action<Response> cb;
                if (waiting.TryGetValue(r.rid, out cb))
                {
                    waiting.Remove(r.rid);
                    try { cb(r); }
                    catch (Exception e) { Plugin.L.LogError("engine callback: " + e); }
                }
            }
            if (!connected && waiting.Count > 0)
            {
                var failed = new List<Action<Response>>(waiting.Values);
                waiting.Clear();
                foreach (var cb in failed)
                {
                    try { cb(new Response { ok = false, error = "ROUNDS card engine is not running" }); } catch { }
                }
            }
        }

        private void Send(Request req, Action<Response> cb)
        {
            if (!connected)
            {
                cb(new Response { ok = false, error = "ROUNDS card engine is not running" });
                return;
            }
            req.rid = nextRid++;
            waiting[req.rid] = cb;
            string json = Json.Write(req);
            lock (gate) outbox.Enqueue(json);
        }

        /// <summary>Fire-and-forget request (no answer comes back).</summary>
        public void SendNoReply(Request req)
        {
            if (!connected) return;
            req.rid = 0;
            string json = Json.Write(req);
            lock (gate) { if (outbox.Count < 240) outbox.Enqueue(json); }
        }

        public void Pose(Request pose) => SendNoReply(pose);

        public void Hello(Action<Response> cb) => Send(new Request { op = Ops.Hello, version = Proto.Version }, cb);
        public void Catalog(Action<Response> cb) => Send(new Request { op = Ops.Catalog }, cb);
        public void Offer(string[] owned, int count, int seed, Action<Response> cb) => Send(new Request { op = Ops.Offer, owned = owned, count = count, seed = seed }, cb);
        public void Render(string id, Action<Response> cb) => Send(new Request { op = Ops.Render, id = id }, cb);
        public void Build(string[] owned, Action<Response> cb) => Send(new Request { op = Ops.Build, owned = owned }, cb);
        public void Skin(int slot, Action<Response> cb) => Send(new Request { op = Ops.Skin, slot = slot }, cb);
        public void Ping(Action<Response> cb) => Send(new Request { op = Ops.Ping }, cb);
        public void ArenaCards(int slot, string[] owned, Action<Response> cb) => Send(new Request { op = Ops.Arena_cards, slot = slot, owned = owned }, cb);
    }
}
