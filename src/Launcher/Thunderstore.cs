using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace CupRounds.Launcher
{
    public class TsVersion
    {
        public string FullName = "";
        public string Number = "";
        public string Description = "";
        public string Icon = "";
        public string DownloadUrl = "";
        public long Downloads;
        public string[] Dependencies = new string[0];
    }

    public class TsPackage
    {
        public string FullName = "";
        public string Name = "";
        public string Owner = "";
        public string Url = "";
        public int Rating;
        public bool Deprecated;
        public bool Nsfw;
        public string[] Categories = new string[0];
        public List<TsVersion> Versions = new List<TsVersion>();
        public TsVersion Latest => Versions.Count > 0 ? Versions[0] : null;
        public long TotalDownloads => Versions.Sum(v => v.Downloads);
        public bool IsCards => Categories.Any(c => c.IndexOf("card", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>The ROUNDS community on Thunderstore (thunderstore.io/c/rounds).</summary>
    public static class Thunderstore
    {
        public const string PackageApi = "https://thunderstore.io/c/rounds/api/v1/package/";
        public const string ProfileApi = "https://thunderstore.io/api/experimental/legacyprofile/get/{0}/";
        public static readonly string[] Provided = { "BepInEx-BepInExPack_ROUNDS", "BepInEx-BepInExPack" };

        public static List<TsPackage> Packages = new List<TsPackage>();
        public static Dictionary<string, TsPackage> ByName = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);

        public static WebClient Web()
        {
            var w = new WebClient { Encoding = Encoding.UTF8 };
            w.Headers[HttpRequestHeader.UserAgent] = "Roundhead-Launcher/0.1 (+https://melty.gg/m/roundhead)";
            return w;
        }

        /// <summary>Loads every ROUNDS package (cached for an hour).</summary>
        public static void LoadPackages(bool forceRefresh = false)
        {
            Directory.CreateDirectory(Paths.Cache);
            string cache = Path.Combine(Paths.Cache, "rounds-packages.json");
            string json = null;
            if (!forceRefresh && File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromHours(1))
                json = File.ReadAllText(cache, Encoding.UTF8);
            if (json == null)
            {
                Log.Write("Downloading the ROUNDS mod list from Thunderstore...");
                try
                {
                    using (var w = Web()) json = w.DownloadString(PackageApi);
                    File.WriteAllText(cache, json, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    Log.Write("Thunderstore is unreachable (" + e.Message + ")" + (File.Exists(cache) ? "; using the saved list" : ""));
                    if (File.Exists(cache)) json = File.ReadAllText(cache, Encoding.UTF8);
                    else return;
                }
            }
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
            var raw = ser.DeserializeObject(json) as object[];
            var list = new List<TsPackage>();
            if (raw != null)
                foreach (var o in raw)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var p = new TsPackage
                    {
                        FullName = S(d, "full_name"),
                        Name = S(d, "name"),
                        Owner = S(d, "owner"),
                        Url = S(d, "package_url"),
                        Rating = (int)L(d, "rating_score"),
                        Deprecated = B(d, "is_deprecated"),
                        Nsfw = B(d, "has_nsfw_content"),
                        Categories = Arr(d, "categories")
                    };
                    if (d.TryGetValue("versions", out object vs) && vs is object[] varr)
                        foreach (var vo in varr)
                        {
                            var vd = vo as Dictionary<string, object>;
                            if (vd == null) continue;
                            p.Versions.Add(new TsVersion
                            {
                                FullName = S(vd, "full_name"),
                                Number = S(vd, "version_number"),
                                Description = S(vd, "description"),
                                Icon = S(vd, "icon"),
                                DownloadUrl = S(vd, "download_url"),
                                Downloads = L(vd, "downloads"),
                                Dependencies = Arr(vd, "dependencies")
                            });
                        }
                    if (p.Versions.Count > 0 && !p.Nsfw) list.Add(p);
                }
            Packages = list;
            ByName = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list) ByName[p.FullName] = p;
            Log.Write("Thunderstore: " + list.Count + " ROUNDS mods, " + list.Count(p => p.IsCards) + " of them card mods");
        }

        /// <summary>"Owner-Name-1.2.3" -> "Owner-Name".</summary>
        public static string PackageOf(string dependency)
        {
            int i = dependency.LastIndexOf('-');
            return i > 0 ? dependency.Substring(0, i) : dependency;
        }

        private static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out object v) && v != null ? v.ToString() : "";
        private static long L(Dictionary<string, object> d, string k) => d.TryGetValue(k, out object v) && v != null ? Convert.ToInt64(v) : 0;
        private static bool B(Dictionary<string, object> d, string k) => d.TryGetValue(k, out object v) && v is bool b && b;
        private static string[] Arr(Dictionary<string, object> d, string k) => d.TryGetValue(k, out object v) && v is IEnumerable e && !(v is string) ? e.Cast<object>().Select(x => x?.ToString() ?? "").ToArray() : new string[0];
    }
}
