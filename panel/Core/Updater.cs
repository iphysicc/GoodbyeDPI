using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>Looks for a newer release of this fork on GitHub and runs its setup.</summary>
    public static class Updater
    {
        const string Repo = "iphysicc/GoodbyeDPI";
        const string SetupAsset = "GoodbyeDPI-Kurulum.exe";
        const string ChecksumsAsset = "SHA256SUMS.txt";

        public sealed class Release
        {
            public Version Version;
            public string SetupUrl;
            public string ExpectedSha256;
            public string ChecksumsUrl;
        }

        [DataContract]
        sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "assets")] public AssetDto[] Assets { get; set; }
        }

        [DataContract]
        sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string BrowserDownloadUrl { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
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
                    ReleaseDto dto;
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        dto = (ReleaseDto)new DataContractJsonSerializer(typeof(ReleaseDto)).ReadObject(ms);
                    if (dto == null || string.IsNullOrEmpty(dto.TagName) || dto.Assets == null) return null;

                    string tag = dto.TagName;
                    if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
                    if (!Version.TryParse(tag, out var v)) return null;
                    v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
                    if (v <= Current) return null;

                    string setupUrl = null, expectedSha = null, checksumsUrl = null;
                    string allowedPrefix = "https://github.com/" + Repo + "/";
                    foreach (var asset in dto.Assets)
                    {
                        if (asset == null || string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl) ||
                            !asset.BrowserDownloadUrl.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (string.Equals(asset.Name, SetupAsset, StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = asset.BrowserDownloadUrl;
                            if (asset.Digest != null && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            {
                                string hex = asset.Digest.Substring(7).Trim();
                                if (Regex.IsMatch(hex, "^[0-9a-fA-F]{64}$"))
                                    expectedSha = hex;
                            }
                        }
                        else if (string.Equals(asset.Name, ChecksumsAsset, StringComparison.OrdinalIgnoreCase))
                        {
                            checksumsUrl = asset.BrowserDownloadUrl;
                        }
                    }

                    if (setupUrl == null) return null;
                    return new Release
                    {
                        Version = v,
                        SetupUrl = setupUrl,
                        ExpectedSha256 = expectedSha,
                        ChecksumsUrl = checksumsUrl,
                    };
                }
            }
            catch { return null; }
        }

        /// <summary>Downloads the setup into an admin-owned folder, verifies its SHA-256, and starts it.</summary>
        public static async Task DownloadAndRunAsync(Release r)
        {
            string dir = Path.Combine(Installer.Dir, "updates");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"GoodbyeDPI-Kurulum-{r.Version.ToString(3)}.exe");

            using (var http = Client(TimeSpan.FromMinutes(5)))
            using (var resp = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(file))
                    await src.CopyToAsync(dst);
            }
            if (new FileInfo(file).Length < 100 * 1024)
            {
                TryDelete(file);
                throw new InvalidOperationException("Güncelleme indirilemedi.");
            }

            string expected = r.ExpectedSha256;
            if (string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(r.ChecksumsUrl))
                expected = await FetchExpectedSha256Async(r.ChecksumsUrl, SetupAsset);

            if (!string.IsNullOrEmpty(expected))
            {
                string actual = Sha256Of(file);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                    throw new InvalidOperationException("Güncelleme dosyası doğrulanamadı (SHA-256 uyuşmuyor).");
                }
            }

            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }

        static async Task<string> FetchExpectedSha256Async(string checksumsUrl, string assetName)
        {
            try
            {
                using (var http = Client(TimeSpan.FromSeconds(15)))
                {
                    string text = await http.GetStringAsync(checksumsUrl);
                    var m = Regex.Match(text,
                        "^\\s*([0-9a-fA-F]{64})\\s+\\*?" + Regex.Escape(assetName) + "\\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        static HttpClient Client(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GoodbyeDPI-Panel/" + Current.ToString(3));
            return http;
        }
    }
}
