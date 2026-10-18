using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Win32;

namespace CupRounds.Launcher
{
    /// <summary>
    /// Play: start ROUNDS hidden as the card engine (with the mashup's own profile), wait for its cards, then start
    /// Cuphead with the Roundhead plugin. When Cuphead closes, close the ROUNDS this launcher started (only that one).
    /// </summary>
    public static class GameRunner
    {
        public const string RoundsAppId = "1557740";
        public const string CupheadAppId = "268910";
        public static Process RoundsProc;
        public static Process CupheadProc;
        public static event Action<string> Status;

        private static void Say(string s)
        {
            Log.Write(s);
            Status?.Invoke(s);
        }

        /// <summary>Copies the shipped BepInEx core and card engine into the profile (Thunderstore mods stay as they are).</summary>
        public static void SyncProfile()
        {
            string src = Path.Combine(Paths.Managed, "rounds", "BepInEx");
            if (!Directory.Exists(src)) throw new Exception("the Roundhead files are missing (" + src + "); reinstall it from Melty");
            CopyDir(Path.Combine(src, "core"), Path.Combine(Paths.ProfileBepInEx, "core"));
            string eng = Path.Combine(src, "plugins", "CupRounds.CardEngine");
            CopyDir(eng, Path.Combine(Paths.ProfileBepInEx, "plugins", "CupRounds.CardEngine"));
            Directory.CreateDirectory(Path.Combine(Paths.ProfileBepInEx, "config"));
        }

        private static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(to, f.Substring(from.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(f).Length || File.GetLastWriteTimeUtc(dest) < File.GetLastWriteTimeUtc(f))
                    File.Copy(f, dest, true);
            }
        }

        private static string Doorstop(string preloader)
        {
            return "--doorstop-enabled true --doorstop-target-assembly \"" + preloader + "\"";
        }

        private static Process[] Running(string exe)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            return Process.GetProcessesByName(name).Where(p =>
            {
                try { return string.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            }).ToArray();
        }

        private static string SteamExe()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k?.GetValue("SteamExe") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Starts a Steam game directly with launch arguments. SteamAppId tells the game's Steam API which owned game
        /// it is (as Steam itself does when it starts a game), so it does not hand itself back to Steam and lose the arguments.
        /// Steam must be running and the game owned; nothing about ownership is bypassed.
        /// </summary>
        private static Process StartGame(string exe, string appId, string args, bool mustStay = true)
        {
            var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false };
            psi.EnvironmentVariables["SteamAppId"] = appId;
            psi.EnvironmentVariables["SteamGameId"] = appId;
            Process p = Process.Start(psi);
            Thread.Sleep(6000);
            if (mustStay && p.HasExited)
                throw new Exception(Path.GetFileName(exe) + " closed straight away (is Steam running and signed in?)");
            return p;
        }

        private static bool EngineReady()
        {
            try
            {
                if (!File.Exists(Paths.PortFile)) return false;
                int port = int.Parse(File.ReadAllText(Paths.PortFile).Trim());
                using (var c = new TcpClient()) { c.Connect("127.0.0.1", port); return true; }
            }
            catch { return false; }
        }

        public static void Play()
        {
            string roundsExe = Path.Combine(Paths.Rounds, "Rounds.exe");
            string cupheadExe = Path.Combine(Paths.Cuphead, "Cuphead.exe");
            if (!File.Exists(roundsExe)) throw new Exception("ROUNDS was not found at " + Paths.Rounds);
            if (!File.Exists(cupheadExe)) throw new Exception("Cuphead was not found at " + Paths.Cuphead);
            if (Running(cupheadExe).Length > 0) throw new Exception("Cuphead is already open. Close it, then press Play.");
            if (Running(roundsExe).Length > 0 && !EngineReady()) throw new Exception("ROUNDS is already open. Close it, then press Play (Roundhead starts its own ROUNDS in the background).");

            Say("Preparing the ROUNDS card engine...");
            SyncProfile();
            bool engineUp = EngineReady();
            bool ready = engineUp;
            if (!engineUp)
                try { if (File.Exists(Paths.PortFile)) File.Delete(Paths.PortFile); } catch { }

            // Cuphead starts first, so it is the game's window; the ROUNDS card engine joins in the background
            // and the Cuphead plugin connects as soon as its cards are ready.
            Say("Starting Cuphead...");
            string cupPreloader = Path.Combine(Paths.Managed, "cuphead", "BepInEx", "core", "BepInEx.Preloader.dll");
            // Melty test runs record the game's window, which needs Cuphead windowed; players keep their own display setting.
            string display = Program.AutoPlay ? " -screen-fullscreen 0 -screen-width 1280 -screen-height 720" : "";
            CupheadProc = StartGame(cupheadExe, CupheadAppId, Doorstop(cupPreloader) + display);

            string preloader = Path.Combine(Paths.ProfileBepInEx, "core", "BepInEx.Preloader.dll");
            string roundsArgs = Doorstop(preloader) + " --roundhead-engine -batchmode -screen-fullscreen 0 -screen-width 640 -screen-height 360";
            if (!engineUp)
            {
                Say("Starting the ROUNDS card engine in the background with " + ProfileManager.Mods.Count(m => m.Enabled) + " Thunderstore mod(s)...");
                RoundsProc = StartGame(roundsExe, RoundsAppId, roundsArgs, false);
                DateTime until = DateTime.Now.AddSeconds(180);
                while (!EngineReady() && !CupheadProc.HasExited)
                {
                    if (RoundsProc.HasExited) { Say("ROUNDS closed while loading its cards; see " + Path.Combine(Paths.ProfileBepInEx, "LogOutput.log")); break; }
                    if (DateTime.Now > until) { Say("ROUNDS is taking a long time to load its cards"); break; }
                    Thread.Sleep(500);
                }
                ready = EngineReady();
            }
            if (CupheadProc.HasExited) Say("Cuphead closed before the ROUNDS card engine was ready.");
            else if (ready) Say("Roundhead is running. Have fun!");
            else Say("Cuphead is running; Roundhead's ROUNDS players join as soon as the card engine is ready.");
            // The background ROUNDS stays for the whole Cuphead session: if it stops, it is started again (Cuphead holds the
            // players and picks them up when it is back).
            int restarts = 0;
            while (!CupheadProc.WaitForExit(1000))
            {
                Process r = RoundsProc;
                if (r == null || !r.HasExited) continue;
                Log.Write("The background ROUNDS stopped (exit code " + r.ExitCode + ")");
                if (++restarts > 3) { Say("ROUNDS keeps stopping; see " + Path.Combine(Paths.ProfileBepInEx, "LogOutput.log")); RoundsProc = null; continue; }
                try
                {
                    if (File.Exists(Paths.PortFile)) File.Delete(Paths.PortFile);
                }
                catch { }
                try
                {
                    RoundsProc = StartGame(roundsExe, RoundsAppId, roundsArgs, false);
                    if (!RoundsProc.HasExited) Say("Started the background ROUNDS again.");
                }
                catch (Exception e) { Say("Could not start ROUNDS again: " + e.Message); } // RoundsProc keeps the exited one: tried again next round
            }
            Say("Cuphead closed.");
            StopRounds();
        }

        public static void StopRounds()
        {
            Process p = RoundsProc;
            RoundsProc = null;
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                // The engine notices within 2 s that Cuphead is gone and ends itself, writing its log first.
                if (p.WaitForExit(6000)) { Log.Write("The background ROUNDS closed itself (pid " + p.Id + ")"); return; }
                // Still running: the background ROUNDS has no window and nothing to save; ending it directly skips ROUNDS's
                // shutdown, where its sound system throws and ROUNDS reports a crash.
                p.Kill();
                p.WaitForExit(5000);
                Log.Write("Closed the background ROUNDS (pid " + p.Id + ")");
            }
            catch (Exception e) { Log.Write("Could not close ROUNDS: " + e.Message); }
        }
    }
}
