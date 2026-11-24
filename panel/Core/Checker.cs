using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
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
        /// <summary>
        /// Not blocked, but with servers that reset a connection whose first packet cuts the
        /// TLS record header (GoodbyeDPI's classic split at byte 2). A method must keep them working.
        /// </summary>
        static readonly string[] MustKeepWorking = { "www.isbank.com.tr", "www.turkiye.gov.tr" };

        const string ControlSite = "www.cloudflare.com";   // not blocked: tells "no internet" apart

        public static int SiteCount => Sites.Length;

        public sealed class Result
        {
            public List<string> Working = new List<string>();
            public List<string> Problems = new List<string>();
            /// <summary>Normal sites the method breaks (connection refused/reset, not a timeout).</summary>
            public List<string> Broken = new List<string>();
            public bool Ok => Problems.Count == 0 && Broken.Count == 0;
            public int Score => Working.Count;
        }

        public static async Task<Result> Run(int timeoutSeconds = 10, CancellationToken ct = default)
        {
            var r = new Result();
            var checks = Sites.Select(s => CheckSite(s, timeoutSeconds, ct)).ToArray();
            var keep = MustKeepWorking.Select(s => CheckSite(s, timeoutSeconds, ct)).ToArray();
            foreach (var (site, problem, _) in await Task.WhenAll(checks))
            {
                if (problem == null) r.Working.Add(site);
                else r.Problems.Add(problem);
            }
            // a slow site is not the method's fault; a reset is
            foreach (var (site, problem, timedOut) in await Task.WhenAll(keep))
                if (problem != null && !timedOut) r.Broken.Add(site);
            ct.ThrowIfCancellationRequested();
            return r;
        }

        /// <summary>True when an unblocked site opens, i.e. the internet itself works.</summary>
        public static async Task<bool> InternetWorks(CancellationToken ct = default)
        {
            for (int i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (_, problem, _) = await CheckSite(ControlSite, 8, ct);
                if (problem == null) return true;
                await Task.Delay(1000, ct);
            }
            return false;
        }

        static async Task<(string site, string problem, bool timedOut)> CheckSite(string site, int timeoutSeconds, CancellationToken ct)
        {
            IPAddress[] system;
            try { system = await Dns.GetHostAddressesAsync(site); }
            catch { return (site, $"{site}: DNS cevap vermedi.", true); }

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                http.DefaultRequestHeaders.ConnectionClose = true;   // every check opens a fresh TLS handshake
                try
                {
                    using (var resp = await http.GetAsync("https://" + site + "/", HttpCompletionOption.ResponseHeadersRead, ct))
                        return (site, null, false);   // any HTTP answer with a valid certificate is the real site
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    return (site, $"{site}: cevap gelmedi.", true);   // HttpClient's timeout
                }
                catch (Exception)
                {
                    if (await LooksPoisoned(site, system))
                        return (site, $"{site}: DNS engel sayfasını gösteriyor ({system[0]}).", false);
                    return (site, $"{site}: bağlantı kurulamadı.", false);
                }
            }
        }

        /// <summary>
        /// Compares the system answer with a public DNS-over-HTTPS resolver (Cloudflare,
        /// falling back to Google). Big sites rotate addresses, so the same /16 network
        /// counts as a match.
        /// </summary>
        static async Task<bool> LooksPoisoned(string site, IPAddress[] system)
        {
            var ipv4 = system.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
            if (ipv4.Count == 0) return false;   // IPv6-only answer can't be compared against A records

            string[] endpoints =
            {
                $"https://1.1.1.1/dns-query?name={site}&type=A",
                $"https://8.8.8.8/resolve?name={site}&type=A",
            };
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
                foreach (var url in endpoints)
                {
                    try
                    {
                        string json = await http.GetStringAsync(url);
                        var real = System.Text.RegularExpressions.Regex
                            .Matches(json, "\"data\":\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\"")
                            .Cast<System.Text.RegularExpressions.Match>()
                            .Select(m => IPAddress.Parse(m.Groups[1].Value)).ToList();
                        if (real.Count == 0) continue;
                        return !ipv4.Any(a => real.Any(b => Same16(a, b)));
                    }
                    catch { /* try next resolver */ }
                }
            }
            return false;
        }

        static bool Same16(IPAddress a, IPAddress b)
        {
            var x = a.GetAddressBytes();
            var y = b.GetAddressBytes();
            return x[0] == y[0] && x[1] == y[1];
        }
    }
}
