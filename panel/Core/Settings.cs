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
        /// Methods the automatic search tries, in this order. On Turkish ISPs the TTL based
        /// ones are reliable, the fake-packet ones (-6 … -9) get through only part of the time.
        /// </summary>
        public static readonly string[] Candidates =
        {
            "-5",
            "-5 --set-ttl 5",
            "-5 --set-ttl 3 --frag-by-sni",
            "-8",
            "-9",
            "-7",
            "-6",
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
