using System.Linq;

namespace GoodbyeDPI.Panel.Core
{
    public sealed class Option
    {
        public string Arg { get; }
        public string Label { get; }
        public Option(string arg, string label) { Arg = arg; Label = label; }
        public override string ToString() => Label;
    }

    public sealed class Settings
    {
        /// <summary>
        /// Methods the automatic search tries, in this order. Measured on Türk Telekom
        /// (2026-11): TTL based fakes get through reliably, the -6 … -9 fakes only part of
        /// the time. Every method splits right before the SNI (--frag-by-sni): the classic
        /// split at byte 2 cuts the TLS record header and some banks (İş Bankası) reset it.
        /// </summary>
        public static readonly string[] Candidates =
        {
            "-5 --frag-by-sni --set-ttl 3 -q",
            "-5 --frag-by-sni -q",
            "-5 --frag-by-sni --set-ttl 5 -q",
            "-8 --frag-by-sni -q",
            "-9 --frag-by-sni",
            "-7 --frag-by-sni -q",
            "-6 --frag-by-sni -q",
        };

        public static readonly Option[] DnsOptions =
        {
            new Option("cloudflare,google", "Cloudflare + Google"),
            new Option("quad9", "Quad9"),
            new Option("adguard", "AdGuard"),
            new Option("off", "Kapalı (sistem DNS'i)"),
        };

        /// <summary>The method that worked last time; empty until the first search.</summary>
        public string Mode { get; set; } = "";
        public string Dns { get; set; } = "cloudflare,google";

        public string EffectiveMode => string.IsNullOrEmpty(Mode) ? Candidates[0] : Mode;
        public string Arguments => ArgumentsFor(EffectiveMode);
        public string ArgumentsFor(string mode) => Dns == "off" ? mode : mode + " --doh " + Dns;

        public static bool IsKnownMode(string m) => Candidates.Contains(m);
    }
}
