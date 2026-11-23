using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>Looks for a newer release of this fork on GitHub and runs its setup.</summary>
    public static class Updater
    {
        const string Repo = "iphysicc/GoodbyeDPI";
        const string SetupAsset = "GoodbyeDPI-Kurulum.exe";

        public sealed class Release
        {
            public Version Version;
            public string SetupUrl;
        }

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            }
        }

        /// <summary>Returns the newer release, or null (also when offline or GitHub is unreachable).</summary>
        public static async Task<Release> CheckAsync()
        {
            try
            {
                using (var http = Client(TimeSpan.FromSeconds(10)))
                {
                    string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                    var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][^\"]*)\"");
                    if (!tagMatch.Success || !Version.TryParse(tagMatch.Groups[1].Value, out var v)) return null;
                    v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
                    if (v <= Current) return null;

                    var urlMatch = Regex.Match(json,
                        "\"browser_download_url\"\\s*:\\s*\"([^\"]*/" + Regex.Escape(SetupAsset) + ")\"",
                        RegexOptions.IgnoreCase);
                    string url = urlMatch.Success ? urlMatch.Groups[1].Value : null;
                    if (url == null || !url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return new Release { Version = v, SetupUrl = url };
                }
            }
            catch { return null; }
        }

        /// <summary>Downloads the setup and starts it (the user goes through its wizard).</summary>
        public static async Task DownloadAndRunAsync(Release r)
        {
            string file = Path.Combine(Path.GetTempPath(), $"GoodbyeDPI-Kurulum-{r.Version.ToString(3)}.exe");
            using (var http = Client(TimeSpan.FromMinutes(5)))
            using (var resp = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(file))
                    await src.CopyToAsync(dst);
            }
            if (new FileInfo(file).Length < 100 * 1024)
                throw new InvalidOperationException("Güncelleme indirilemedi.");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }

        static HttpClient Client(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GoodbyeDPI-Panel/" + Current.ToString(3));
            return http;
        }
    }
}
