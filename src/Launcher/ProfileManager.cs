using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CupRounds.Launcher
{
    public class InstalledMod
    {
        public string FullName = "";
        public string Version = "";
        public bool Enabled = true;
        public bool Dependency;
    }

    /// <summary>
    /// The mashup's own ROUNDS profile: Thunderstore mods installed the way r2modman lays them out
    /// (BepInEx/plugins/Owner-Name/...), with mods.json listing what is installed.
    /// </summary>
    public static class ProfileManager
    {
        private static string Manifest => Path.Combine(Paths.Profile, "mods.json");
        public static List<InstalledMod> Mods = new List<InstalledMod>();

        public static void Load()
        {
            Mods = new List<InstalledMod>();
            try
            {
                if (File.Exists(Manifest))
                    Mods = new JavaScriptSerializer().Deserialize<List<InstalledMod>>(File.ReadAllText(Manifest)) ?? new List<InstalledMod>();
            }
            catch (Exception e) { Log.Write("mods.json unreadable: " + e.Message); }
        }

        public static void Save()
        {
            Directory.CreateDirectory(Paths.Profile);
            File.WriteAllText(Manifest, new JavaScriptSerializer().Serialize(Mods));
        }

        public static InstalledMod Find(string fullName) => Mods.FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));

        public static bool IsProvided(string fullName) => Thunderstore.Provided.Any(p => string.Equals(p, fullName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Installs a mod and every dependency it lists (latest versions).</summary>
        public static void Install(string fullName, bool asDependency = false, HashSet<string> seen = null)
        {
            seen = seen ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(fullName) || IsProvided(fullName)) return;
            TsPackage p;
            if (!Thunderstore.ByName.TryGetValue(fullName, out p)) { Log.Write("Not on Thunderstore: " + fullName); return; }
            foreach (string dep in p.Latest.Dependencies)
                Install(Thunderstore.PackageOf(dep), true, seen);
            InstalledMod have = Find(fullName);
            if (have != null && have.Version == p.Latest.Number)
            {
                if (!asDependency) have.Dependency = false;
                Save();
                return;
            }
            Log.Write("Installing " + p.Latest.FullName + "...");
            string tmp = Path.Combine(Paths.Cache, p.Latest.FullName + ".zip");
            Directory.CreateDirectory(Paths.Cache);
            using (var w = Thunderstore.Web()) w.DownloadFile(p.Latest.DownloadUrl, tmp);
            RemoveFiles(fullName);
            Extract(tmp, fullName);
            try { File.Delete(tmp); } catch { }
            if (have == null) { have = new InstalledMod { FullName = fullName, Dependency = asDependency }; Mods.Add(have); }
            have.Version = p.Latest.Number;
            if (!asDependency) have.Dependency = false;
            if (!have.Enabled) SetEnabled(fullName, false);
            Save();
        }

        /// <summary>Thunderstore's BepInEx layout rules, as r2modman applies them.</summary>
        private static void Extract(string zipPath, string fullName)
        {
            string bep = Paths.ProfileBepInEx;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                bool hasBepInExRoot = zip.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase));
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string rel = e.FullName.Replace('\\', '/');
                    if (hasBepInExRoot && rel.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(8);
                    string top = rel.Contains("/") ? rel.Substring(0, rel.IndexOf('/')).ToLowerInvariant() : "";
                    string rest = rel.Contains("/") ? rel.Substring(rel.IndexOf('/') + 1) : rel;
                    string dest;
                    switch (top)
                    {
                        case "plugins": dest = Path.Combine(bep, "plugins", fullName, rest); break;
                        case "patchers": dest = Path.Combine(bep, "patchers", fullName, rest); break;
                        case "monomod": dest = Path.Combine(bep, "monomod", fullName, rest); break;
                        case "config": dest = Path.Combine(bep, "config", rest); break;
                        case "core": continue;
                        default: dest = Path.Combine(bep, "plugins", fullName, rel); break;
                    }
                    dest = Path.GetFullPath(dest);
                    if (!dest.StartsWith(Path.GetFullPath(Paths.Profile), StringComparison.OrdinalIgnoreCase)) continue;
                    if (top == "config" && File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
        }

        private static IEnumerable<string> ModFolders(string fullName)
        {
            foreach (string sub in new[] { "plugins", "patchers", "monomod" })
                yield return Path.Combine(Paths.ProfileBepInEx, sub, fullName);
        }

        private static void RemoveFiles(string fullName)
        {
            foreach (string d in ModFolders(fullName))
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }

        public static void Uninstall(string fullName)
        {
            var dependents = Mods.Where(m => m.FullName != fullName && Thunderstore.ByName.ContainsKey(m.FullName)
                && Thunderstore.ByName[m.FullName].Latest.Dependencies.Any(d => string.Equals(Thunderstore.PackageOf(d), fullName, StringComparison.OrdinalIgnoreCase))).Select(m => m.FullName).ToList();
            if (dependents.Count > 0) Log.Write("Note: " + string.Join(", ", dependents) + " need " + fullName);
            RemoveFiles(fullName);
            Mods.RemoveAll(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            Save();
            Log.Write("Removed " + fullName);
        }

        /// <summary>Disabling renames the mod's .dll files to .dll.old, like r2modman.</summary>
        public static void SetEnabled(string fullName, bool enabled)
        {
            foreach (string d in ModFolders(fullName))
            {
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d, enabled ? "*.dll.old" : "*.dll", SearchOption.AllDirectories))
                {
                    string to = enabled ? f.Substring(0, f.Length - 4) : f + ".old";
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(f, to);
                }
            }
            var m = Find(fullName);
            if (m != null) { m.Enabled = enabled; Save(); }
        }

        public static int UpdateAll()
        {
            int n = 0;
            foreach (var m in Mods.ToList())
            {
                TsPackage p;
                if (Thunderstore.ByName.TryGetValue(m.FullName, out p) && p.Latest.Number != m.Version)
                {
                    Install(m.FullName, m.Dependency);
                    n++;
                }
            }
            Log.Write(n == 0 ? "Everything is up to date" : "Updated " + n + " mod(s)");
            return n;
        }

        /// <summary>Imports an r2modman / Thunderstore Mod Manager profile code (mods reinstalled from Thunderstore, configs copied).</summary>
        public static int ImportCode(string code)
        {
            code = code.Trim();
            string body;
            using (var w = Thunderstore.Web()) body = w.DownloadString(string.Format(Thunderstore.ProfileApi, Uri.EscapeDataString(code)));
            const string prefix = "#r2modman";
            if (body.StartsWith(prefix)) body = body.Substring(prefix.Length);
            byte[] zipBytes = Convert.FromBase64String(body.Trim());
            int count = 0;
            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var r2x = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".r2x", StringComparison.OrdinalIgnoreCase));
                if (r2x == null) throw new Exception("that code has no mod list");
                string yaml;
                using (var sr = new StreamReader(r2x.Open(), Encoding.UTF8)) yaml = sr.ReadToEnd();
                count = InstallFromYaml(yaml);
                foreach (var e in zip.Entries)
                {
                    string rel = e.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(e.Name) || !rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.GetFullPath(Path.Combine(Paths.ProfileBepInEx, "config", rel.Substring("BepInEx/config/".Length)));
                    if (!dest.StartsWith(Path.GetFullPath(Paths.Profile), StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            return count;
        }

        public static List<string> LocalProfiles()
        {
            var found = new List<string>();
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string root in new[]
            {
                Path.Combine(appdata, "r2modmanPlus-local", "ROUNDS", "profiles"),
                Path.Combine(appdata, "Thunderstore Mod Manager", "DataFolder", "ROUNDS", "profiles")
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string d in Directory.GetDirectories(root))
                    if (File.Exists(Path.Combine(d, "mods.yml"))) found.Add(d);
            }
            return found;
        }

        /// <summary>Copies a local r2modman / TMM profile's mod list; the mods themselves are reinstalled fresh from Thunderstore.</summary>
        public static int ImportLocal(string profileDir)
        {
            return InstallFromYaml(File.ReadAllText(Path.Combine(profileDir, "mods.yml"), Encoding.UTF8));
        }

        private static int InstallFromYaml(string yaml)
        {
            int count = 0;
            var entries = Regex.Split(yaml, @"^\s*-\s+", RegexOptions.Multiline).Skip(1);
            foreach (string entry in entries)
            {
                Match name = Regex.Match(entry, @"^\s*name:\s*(\S+)", RegexOptions.Multiline);
                if (!name.Success) name = Regex.Match(entry, @"^(?:name:\s*)?(\S+)");
                if (!name.Success) continue;
                string full = name.Groups[1].Value.Trim().Trim('\'', '"');
                if (full.StartsWith("name:")) continue;
                Match enabled = Regex.Match(entry, @"^\s*enabled:\s*(true|false)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                bool on = !enabled.Success || enabled.Groups[1].Value.ToLowerInvariant() == "true";
                if (IsProvided(full) || !Thunderstore.ByName.ContainsKey(full)) continue;
                try
                {
                    Install(full);
                    if (!on) SetEnabled(full, false);
                    count++;
                }
                catch (Exception e) { Log.Write("Could not install " + full + ": " + e.Message); }
            }
            Log.Write("Imported " + count + " mod(s)");
            return count;
        }
    }
}
