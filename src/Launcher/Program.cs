using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace CupRounds.Launcher
{
    public static class Paths
    {
        public static string Cuphead = "";
        public static string Rounds = "";
        public static string Managed = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        public static string Data
        {
            get
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "CupRounds");
            }
        }

        /// <summary>The mashup's own ROUNDS mod profile (BepInEx root), separate from any r2modman profile.</summary>
        public static string Profile => Path.Combine(Data, "rounds-profile");
        public static string ProfileBepInEx => Path.Combine(Profile, "BepInEx");
        public static string Cache => Path.Combine(Data, "cache");
        public static string LogFile => Path.Combine(Data, "launcher.log");
        public static string PortFile => Path.Combine(Data, "engine.port");

        public static string MeltyTestFile
        {
            get
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
                return Path.Combine(Path.Combine(Path.Combine(local, "Melty"), "test"), "roundhead.json");
            }
        }
    }

    public static class Program
    {
        public static bool AutoPlay;
        /// <summary>--install Owner-Name (repeatable): install Thunderstore mods without the window, then exit.</summary>
        public static readonly System.Collections.Generic.List<string> Installs = new System.Collections.Generic.List<string>();
        /// <summary>--draw-faces: draw the face pictures and parts (Faces) without the window, then exit.</summary>
        public static bool DrawFaces;

        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : "";
                if (a == "--cuphead") { Paths.Cuphead = next; i++; }
                else if (a == "--rounds") { Paths.Rounds = next; i++; }
                else if (a == "--managed") { Paths.Managed = next.TrimEnd('\\', '/'); i++; }
                else if (a == "--autoplay") AutoPlay = true;
                else if (a == "--install") { Installs.Add(next); i++; }
                else if (a == "--draw-faces") DrawFaces = true;
            }
            if (File.Exists(Paths.MeltyTestFile) || Environment.GetEnvironmentVariable("MELTY_TEST") == "1") AutoPlay = true;
            // Started by hand (no folders given): find the games in the Steam libraries.
            if (string.IsNullOrEmpty(Paths.Cuphead)) Paths.Cuphead = SteamGames.Find("Cuphead", "Cuphead.exe") ?? "";
            if (string.IsNullOrEmpty(Paths.Rounds)) Paths.Rounds = SteamGames.Find("ROUNDS", "Rounds.exe") ?? "";
            Directory.CreateDirectory(Paths.Data);
            Log.Write("Roundhead launcher starting; Cuphead=" + Paths.Cuphead + " ROUNDS=" + Paths.Rounds + " managed=" + Paths.Managed + (AutoPlay ? " (autoplay)" : ""));
            if (Installs.Count > 0)
            {
                Thunderstore.LoadPackages();
                ProfileManager.Load();
                foreach (string m in Installs) ProfileManager.Install(m);
                Log.Write("Installed: " + string.Join(", ", ProfileManager.Mods.Select(x => x.FullName + " " + x.Version).ToArray()));
                return 0;
            }
            if (DrawFaces) return Faces.Draw() ? 0 : 1;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            Application.Run(form);
            return 0;
        }
    }

    /// <summary>Finds an installed Steam game folder through Steam's libraryfolders.vdf.</summary>
    public static class SteamGames
    {
        public static string Find(string folder, string exe)
        {
            var roots = new System.Collections.Generic.List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steam = k?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steam))
                    {
                        steam = steam.Replace('/', '\\');
                        roots.Add(steam);
                        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                                roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
            }
            catch { }
            foreach (string r in roots)
            {
                string dir = Path.Combine(r, "steamapps", "common", folder);
                if (File.Exists(Path.Combine(dir, exe))) return dir;
            }
            return null;
        }
    }

    public static class Log
    {
        public static event Action<string> Line;

        public static void Write(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            try { File.AppendAllText(Paths.LogFile, line + Environment.NewLine); } catch { }
            Line?.Invoke(line);
        }
    }
}
