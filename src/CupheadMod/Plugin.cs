using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    [BepInPlugin(Guid, "Roundhead", PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gg.melty.roundhead.cuphead";
        public const string PluginVersion = "0.1.1";

        public static Plugin I;
        public static ManualLogSource L;
        public static EngineClient Engine;
        public static readonly Dictionary<string, CardData> Catalog = new Dictionary<string, CardData>();
        public static readonly SkinData[] Skins = new SkinData[2];
        public static readonly Texture2D[] BulletTex = new Texture2D[2];

        private float nextHello;
        private bool catalogLoaded;

        private void Awake()
        {
            I = this;
            L = Logger;
            DontDestroyOnLoad(gameObject);
            TestMode.Init();
            if (TestMode.Active) Progress.ClearTestSaves();
            Progress.Load();
            Engine = new EngineClient();
            Engine.Start();
            Puppets.Start();
            ArenaClient.Init();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            gameObject.AddComponent<ArenaLayer>();
            gameObject.AddComponent<PickOverlay>();
            gameObject.AddComponent<ShopPackOverlay>();
            L.LogInfo("Roundhead " + PluginVersion + " loaded; waiting for the ROUNDS card engine (data folder " + Proto.DataDir() + ")");
        }

        private float nextBeat;

        private void Update()
        {
            // Test runs: a heartbeat every 15 s, so a recording with no checks still says where the plugin stood.
            if (TestMode.Active && Time.unscaledTime >= nextBeat)
            {
                nextBeat = Time.unscaledTime + 15f;
                L.LogInfo("test: alive at " + Time.unscaledTime.ToString("0") + " s, scene " + SceneLoader.SceneName + ", engine " + (Engine.Connected ? "connected" : "not connected") + ", arena " + (ArenaClient.link == null ? "no memory" : ArenaClient.Live ? "live" : "quiet") + ", " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed"));
            }
            // The arena beat first and on its own: nothing that fails elsewhere may stop it.
            try { ArenaClient.Tick(); } catch (Exception e) { LogOnce("arena tick", e); }
            try { Engine.Pump(); } catch (Exception e) { LogOnce("engine", e); }
            try { Puppets.Pump(); } catch (Exception e) { LogOnce("puppets", e); }
            try { InputPatches.EnsurePatched(); } catch (Exception e) { LogOnce("keys", e); }
            if (Engine.Connected && !catalogLoaded && Time.unscaledTime >= nextHello)
            {
                nextHello = Time.unscaledTime + 5f;
                Engine.Hello(r =>
                {
                    if (!r.ok) return;
                    L.LogInfo("ROUNDS card engine: " + r.cardCount + " cards, ROUNDS " + r.roundsVersion + ", mods: " + string.Join(", ", r.mods));
                    TestMode.Report("engine-cards", r.cardCount > 0, r.cardCount + " ROUNDS cards from the running ROUNDS");
                    Engine.Catalog(c =>
                    {
                        if (!c.ok) return;
                        Catalog.Clear();
                        foreach (CardData d in c.cards) Catalog[d.id] = d;
                        catalogLoaded = true;
                        Progress.RefreshBuilds();
                    });
                    for (int slot = 0; slot < 2; slot++)
                    {
                        int s = slot;
                        Engine.Skin(s, k =>
                        {
                            if (!k.ok) return;
                            Skins[s] = k.skin;
                            BulletTex[s] = Gfx.FromPng(k.skin.bulletPng);
                            BlobController.RefreshAllLooks();
                        });
                    }
                });
            }
            if (!Engine.Connected) catalogLoaded = false;
            TestMode.Tick();
        }

        private readonly HashSet<string> errors = new HashSet<string>();

        private void LogOnce(string what, Exception e)
        {
            if (errors.Add(what + e.Message)) L.LogError(what + ": " + e);
        }

        private void OnApplicationQuit()
        {
            // Close every socket and the shared memory so no thread is left blocked while Cuphead shuts down.
            ArenaClient.Close();
            Engine.Stop();
            Puppets.Stop();
        }
    }
}
