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

        /// <summary>A WebClient that gives up on a stalled server instead of waiting forever, and takes gzip (the mod list is 7 MB raw).</summary>
        private class TimedWebClient : WebClient
        {
            public int TimeoutMs = 30000;

            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                if (r is HttpWebRequest h)
                {
                    h.Timeout = TimeoutMs;
                    h.ReadWriteTimeout = TimeoutMs;
                    h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                }
                return r;
            }
        }

        public static WebClient Web(int timeoutMs = 30000)
        {
            var w = new TimedWebClient { Encoding = Encoding.UTF8, TimeoutMs = timeoutMs };
            w.Headers[HttpRequestHeader.UserAgent] = "Roundhead-Launcher/0.1 (+https://melty.gg/m/roundhead)";
            return w;
        }

        /// <summary>Downloads text, giving up after timeoutMs whatever stage it is stuck in: HttpWebRequest's own timeouts
        /// do not cover every stall (a proxy tunnel that never answers kept the old launcher waiting for good).</summary>
        public static string GetString(string url, int timeoutMs)
        {
            using (var w = Web(timeoutMs))
            {
                var t = w.DownloadStringTaskAsync(url);
                try
                {
                    if (t.Wait(timeoutMs)) return t.Result;
                }
                catch (AggregateException e) { throw e.InnerException ?? e; }
                w.CancelAsync();
                throw new TimeoutException("no answer in " + timeoutMs / 1000 + " s");
            }
        }

        /// <summary>True once a mod list (fresh or saved) is in Packages.</summary>
        public static bool Loaded;

        /// <summary>Loads every ROUNDS package (cached for an hour). Only browsing and installing mods need it; Play never waits for it.</summary>
        public static void LoadPackages(bool forceRefresh = false)
        {
            Directory.CreateDirectory(Paths.Cache);
            string cache = Path.Combine(Paths.Cache, "rounds-packages.json");
            string json = null;
            if (!forceRefresh && File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromHours(1))
                json = File.ReadAllText(cache, Encoding.UTF8);
            if (json == null && !forceRefresh && File.Exists(cache) && !Loaded)
            {
                // Show the saved list straight away while a fresh one downloads.
                try { Parse(File.ReadAllText(cache, Encoding.UTF8)); } catch (Exception e) { Log.Write("The saved mod list is unreadable (" + e.Message + ")"); }
            }
            if (json == null)
            {
                Log.Write("Downloading the ROUNDS mod list from Thunderstore...");
                try
                {
                    json = GetString(PackageApi, 60000);
                    File.WriteAllText(cache, json, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    Log.Write("Thunderstore is unreachable (" + e.Message + ")" + (File.Exists(cache) ? "; using the saved list" : ""));
                    if (Loaded) return;
                    if (File.Exists(cache)) json = File.ReadAllText(cache, Encoding.UTF8);
                    else return;
                }
            }
            Parse(json);
        }

        private static void Parse(string json)
        {
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
            var byName = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list) byName[p.FullName] = p;
            Packages = list;
            ByName = byName;
            Loaded = true;
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
