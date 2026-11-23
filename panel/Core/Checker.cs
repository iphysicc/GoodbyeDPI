using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>
    /// Opens a few sites that are commonly blocked in Turkey, the way a browser would
    /// (system DNS + HTTPS), and explains what failed.
    /// </summary>
    public static class Checker
    {
        /// <summary>
        /// Addresses blocked by DPI (SNI) in Turkey, including the ones the Discord app
        /// itself uses. Some methods get through only part of the time, so one site is not
        /// enough to tell a reliable method from a lucky one.
        /// </summary>
        static readonly string[] Sites =
        {
            "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "discordapp.com",
            "roblox.com", "www.roblox.com", "wattpad.com", "pastebin.com",
        };
        const string ControlSite = "www.cloudflare.com";   // not blocked: tells "no internet" apart

        public static int SiteCount => Sites.Length;

        public sealed class Result
        {
            public List<string> Working = new List<string>();
            public List<string> Problems = new List<string>();
            public bool Ok => Problems.Count == 0;
            public int Score => Working.Count;
        }

        public static async Task<Result> Run(int timeoutSeconds = 10)
        {
            var r = new Result();
            var checks = Sites.Select(s => CheckSite(s, timeoutSeconds)).ToArray();
            foreach (var (site, problem) in await Task.WhenAll(checks))
            {
                if (problem == null) r.Working.Add(site);
                else r.Problems.Add(problem);
            }
            return r;
        }

        /// <summary>True when an unblocked site opens, i.e. the internet itself works.</summary>
        public static async Task<bool> InternetWorks()
        {
            for (int i = 0; i < 2; i++)
            {
                var (_, problem) = await CheckSite(ControlSite, 8);
                if (problem == null) return true;
                await Task.Delay(1000);
            }
            return false;
        }

        static async Task<(string site, string problem)> CheckSite(string site, int timeoutSeconds)
        {
            IPAddress[] system;
            try { system = await Dns.GetHostAddressesAsync(site); }
            catch { return (site, $"{site}: DNS cevap vermedi."); }

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                http.DefaultRequestHeaders.ConnectionClose = true;   // every check opens a fresh TLS handshake
                try
                {
                    using (var resp = await http.GetAsync("https://" + site + "/", HttpCompletionOption.ResponseHeadersRead))
                        return (site, null);   // any HTTP answer with a valid certificate is the real site
                }
                catch (Exception)
                {
                    if (await LooksPoisoned(site, system))
                        return (site, $"{site}: DNS engel sayfasını gösteriyor ({system[0]}).");
                    return (site, $"{site}: bağlantı kurulamadı.");
                }
            }
        }

        /// <summary>
        /// Compares the system answer with Cloudflare's DNS-over-HTTPS answer.
        /// Big sites rotate addresses, so the same /16 network counts as a match.
        /// </summary>
        static async Task<bool> LooksPoisoned(string site, IPAddress[] system)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) })
                {
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
                    string json = await http.GetStringAsync($"https://1.1.1.1/dns-query?name={site}&type=A");
                    var real = System.Text.RegularExpressions.Regex
                        .Matches(json, "\"data\":\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\"")
                        .Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => IPAddress.Parse(m.Groups[1].Value)).ToList();
                    if (real.Count == 0) return false;
                    return !system.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                  .Any(a => real.Any(b => Same16(a, b)));
                }
            }
            catch { return false; }
        }

        static bool Same16(IPAddress a, IPAddress b)
        {
            var x = a.GetAddressBytes();
            var y = b.GetAddressBytes();
            return x[0] == y[0] && x[1] == y[1];
        }
    }
}
