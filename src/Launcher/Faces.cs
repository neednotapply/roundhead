using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CupRounds.Launcher
{
    /// <summary>
    /// Player 1's and player 2's faces. The launcher's face editor (FaceEditorForm) makes each player's face from ROUNDS's own
    /// face parts and keeps it in %LOCALAPPDATA%\CupRounds\faces.txt (FaceFile), which the card engine applies
    /// (src/CardEngine/Faces.cs); a player with no face there wears ROUNDS's own face for its player 1 or 2. ROUNDS's saved
    /// faces stay as they are. The pictures and parts are drawn by the player's own ROUNDS, started hidden for a few seconds
    /// with the engine's --roundhead-faces.
    /// </summary>
    public static class Faces
    {
        /// <summary>The faces ROUNDS shows and edits (its handler holds ten; the last two never appear in its menus).</summary>
        public const int Presets = 8;
        public static string ChoiceFile => Path.Combine(Paths.Data, "faces.txt");
        public static string SheetDir => Path.Combine(Paths.Data, "faces");
        public static string Picture(int seat, int preset) => Path.Combine(SheetDir, "seat" + seat + "_face" + preset + ".png");
        private static string SignatureFile => Path.Combine(SheetDir, "signature.txt");
        private const string RoundsPrefs = @"Software\Landfall Games\ROUNDS";
        public static Process Drawer;

        public static string PlayerName(int seat) => "Player " + (seat + 1);

        /// <summary>Each player's face: the editor's, else ROUNDS's own for its player 1 or 2 (from the parts' catalogue).</summary>
        public static FaceData Current(int seat, FaceParts parts)
        {
            FaceData[] saved;
            try { saved = FaceFile.Read(ChoiceFile); }
            catch (Exception e) { Log.Write("faces.txt unreadable: " + e.Message); saved = new FaceData[FaceFile.Seats.Length]; }
            if (seat < saved.Length && saved[seat] != null) return saved[seat];
            if (parts != null && parts.Selected[seat] >= 0 && parts.Selected[seat] < parts.Presets.Count) return parts.Presets[parts.Selected[seat]].Copy();
            return new FaceData();
        }

        public static void Save(int seat, FaceData face)
        {
            var saved = File.Exists(ChoiceFile) ? FaceFile.Read(ChoiceFile) : new FaceData[FaceFile.Seats.Length];
            saved[seat] = face.Copy();
            FaceFile.Write(ChoiceFile, saved);
            Log.Write(PlayerName(seat) + "'s face: " + face.Format());
        }

        /// <summary>All of ROUNDS's saved face parts and positions: when it changes, the pictures are drawn again.</summary>
        public static string Signature()
        {
            var sb = new StringBuilder();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RoundsPrefs))
                {
                    if (k == null) return "";
                    foreach (string n in k.GetValueNames().Where(n => n.StartsWith("eye") || n.StartsWith("mouth") || n.StartsWith("detail")).OrderBy(n => n, StringComparer.Ordinal))
                    {
                        object v = k.GetValue(n);
                        sb.Append(n).Append('=').Append(v is byte[] b ? BitConverter.ToString(b) : Convert.ToString(v)).Append(';');
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        public static bool PicturesCurrent()
        {
            try
            {
                for (int s = 0; s < FaceFile.Seats.Length; s++)
                    for (int p = 0; p < Presets; p++)
                        if (!File.Exists(Picture(s, p))) return false;
                return FaceParts.Ready && File.Exists(SignatureFile) && File.ReadAllText(SignatureFile) == Signature();
            }
            catch { return false; }
        }

        public static bool Drawing
        {
            get { try { return Drawer != null && !Drawer.HasExited; } catch { return false; } }
        }

        /// <summary>Starts ROUNDS hidden with the engine in its face-picture mode and waits (on this thread) until it is done.</summary>
        public static bool Draw()
        {
            string exe = Path.Combine(Paths.Rounds, "Rounds.exe");
            if (!File.Exists(exe)) { Log.Write("Cannot draw the faces: ROUNDS was not found at " + Paths.Rounds); return false; }
            if (GameRunner.Running(exe).Length > 0) { Log.Write("ROUNDS is open; the face pictures are drawn once it is closed"); return false; }
            // The profile gets the shipped card engine first (PLAY does the same), so the face-picture mode is the current one.
            try { GameRunner.SyncProfile(); }
            catch (Exception e) { Log.Write("Cannot draw the faces: " + e.Message); return false; }
            string preloader = Path.Combine(Paths.ProfileBepInEx, "core", "BepInEx.Preloader.dll");
            if (!File.Exists(preloader)) { Log.Write("Cannot draw the faces: the Roundhead files are missing (" + Paths.Profile + ")"); return false; }
            string sig = Signature();
            DateTime before = DateTime.Now;
            string args = "--doorstop-enabled true --doorstop-target-assembly \"" + preloader + "\" --roundhead-engine --roundhead-faces"
                + " -batchmode -screen-fullscreen 0 -screen-width 640 -screen-height 360";
            var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = Paths.Rounds, UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["SteamAppId"] = GameRunner.RoundsAppId;
            psi.EnvironmentVariables["SteamGameId"] = GameRunner.RoundsAppId;
            psi.EnvironmentVariables.Remove("DOORSTOP_DISABLE");
            psi.EnvironmentVariables.Remove("DOORSTOP_INITIALIZED");
            Log.Write("Drawing your ROUNDS faces (ROUNDS runs hidden for a few seconds)...");
            Drawer = Process.Start(psi);
            var p = Drawer;
            new Thread(() => GameRunner.KeepOutOfSight(p)) { IsBackground = true, Name = "faces window" }.Start();
            if (!p.WaitForExit(150000))
            {
                try { p.Kill(); } catch { }
                Log.Write("ROUNDS took too long to draw the faces");
                return false;
            }
            string done = Path.Combine(SheetDir, "done.txt");
            bool ok = File.Exists(done) && File.GetLastWriteTime(done) >= before;
            if (ok) File.WriteAllText(SignatureFile, sig);
            Log.Write(ok ? "Face pictures ready" : "ROUNDS did not draw the faces; see " + Path.Combine(Paths.ProfileBepInEx, "LogOutput.log"));
            return ok;
        }

        /// <summary>Ends a face-picture run (PLAY needs ROUNDS closed).</summary>
        public static void StopDrawing()
        {
            var p = Drawer;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); Log.Write("Stopped drawing the faces"); } } catch { }
        }
    }
}
