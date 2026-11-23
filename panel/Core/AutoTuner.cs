using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>
    /// Which DPI trick works depends on the ISP (and even the city), so the user never
    /// picks one: the panel tries them in turn and keeps the one that opens the test sites.
    /// Measured on Türk Telekom (2026-11): TTL based methods opened 8/8 addresses,
    /// fake-packet ones (-6 … -9) only 3–7/8 and differently on every try.
    /// </summary>
    public static class AutoTuner
    {
        public sealed class Outcome
        {
            public string Mode;            // method in use, null when none worked well enough
            public Checker.Result Last;    // the test of that method
        }

        static int Total => Checker.SiteCount;
        // a single unlucky connection must not throw away a working method
        static int GoodEnough => Total - 1;
        // below this protection is kept on, but the user is told it is unreliable
        static int Usable => (Total * 3 + 3) / 4;

        /// <summary>True when the running method still opens (nearly) every test address.</summary>
        public static bool IsGood(Checker.Result r) => r.Score >= GoodEnough;

        /// <summary>Starts with the saved method and checks it; searches only if it fails.</summary>
        public static async Task<Outcome> StartAndVerify(Settings s, bool autostart, Action<int, int> progress)
        {
            if (!string.IsNullOrEmpty(s.Mode))
            {
                bool active = await Task.Run(() => Installer.Restart(s, autostart));
                if (active)
                {
                    var r = await Checker.Run(8);
                    if (r.Score >= GoodEnough) return new Outcome { Mode = s.Mode, Last = r };
                }
            }
            return await Search(s, autostart, progress);
        }

        /// <summary>Tries the methods; leaves the service running with the best one.</summary>
        public static async Task<Outcome> Search(Settings s, bool autostart, Action<int, int> progress)
        {
            if (!await Checker.InternetWorks())
                throw new InvalidOperationException("İnternet bağlantısı yok gibi görünüyor. Bağlantını kontrol edip tekrar dene.");

            var order = new List<string>();
            if (Settings.IsKnownMode(s.Mode)) order.Add(s.Mode);
            order.AddRange(Settings.Candidates.Where(c => c != s.Mode));

            string best = null, current = null;
            Checker.Result bestResult = null;
            for (int i = 0; i < order.Count; i++)
            {
                current = order[i];
                progress?.Invoke(i + 1, order.Count);
                var trial = new Settings { Mode = order[i], Dns = s.Dns };
                bool active = await Task.Run(() => Installer.Restart(trial, autostart));
                if (!active)
                {
                    // the engine itself fails (driver blocked, another DPI tool): no method will help
                    string why = await Task.Run(() => Installer.Diagnose(trial));
                    throw new InvalidOperationException("GoodbyeDPI başlatılamadı. " + (why ?? "Bilgisayarı yeniden başlatıp tekrar dene."));
                }
                var r = await Checker.Run(6);
                if (bestResult == null || r.Score > bestResult.Score)
                {
                    best = order[i];
                    bestResult = r;
                }
                if (r.Ok) break;   // everything opened: no need to try the rest
            }

            bool usable = bestResult.Score >= Usable;
            var chosen = new Settings { Mode = best, Dns = s.Dns };
            if (best != current)   // the service still runs the last method tried
                await Task.Run(() => Installer.Restart(chosen, autostart));
            else
                await Task.Run(() => Installer.Configure(chosen, autostart));
            if (usable)
            {
                s.Mode = best;
                Installer.SaveSettings(s);
            }
            // when nothing is usable protection stays on with the best method:
            // DNS over HTTPS still helps where only DNS is blocked
            return new Outcome { Mode = usable ? best : null, Last = bestResult };
        }
    }
}
