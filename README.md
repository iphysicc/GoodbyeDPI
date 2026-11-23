# GoodbyeDPI, Türkiye için kurulumlu sürüm

Bu repo [ValdikSS/GoodbyeDPI](https://github.com/ValdikSS/GoodbyeDPI)'ın çatalıdır. Türkiye'de erişim engeli hem DNS'te hem DPI'da olduğu için GoodbyeDPI'yi tek başına çalıştırmak çoğu zaman yetmiyor. Bu sürüm ikisini birden çözüyor ve kurup tek düğmeyle kullanılacak hâle getiriyor.

**[Son sürümü indir](https://github.com/iphysicc/GoodbyeDPI/releases/latest)**: `GoodbyeDPI-Kurulum.exe` dosyasını çalıştır, açılan panelde **Başlat**'a bas.

Orijinal GoodbyeDPI'ye eklenenler:

- **Şifreli DNS içinde.** Düz DNS sorguları yakalanıp HTTPS üzerinden (Cloudflare, Google, Quad9 ya da AdGuard) çözülüyor. Ayrı bir DNSCrypt ya da DNS programı gerekmiyor, Windows'un DNS ayarına dokunulmuyor. Türk Telekom'da engel sayfasına (195.175.254.2) giden discord.com, roblox.com ve pastebin.com gerçek adreslerine çözülüyor.
- **Yöntem otomatik seçiliyor.** Hangi DPI hilesinin çalıştığı ISS'ye göre değişiyor. Panel yöntemleri sırayla dener ve DPI ile engellenen 8 adresi (Discord sitesi ve uygulama adresleri, Roblox, Wattpad, Pastebin) açan ilkini seçer. Türk Telekom'da ölçüldü: TTL tabanlı yöntemler (`-5`) 8 adresin 8'ini açtı, sahte paket yöntemleri (`-6` … `-9`) her denemede farklı olarak 3–7'sini.
- **Ağ değişince yeniden kontrol.** Başka bir Wi-Fi'ya ya da telefonun hotspot'una geçince panel korumayı sessizce test eder, gerekirse yeni yöntem bulur.
- **Kurulum ve panel.** Türkçe kurulum sihirbazı, Ayarlar > Uygulamalar'dan kaldırma, Windows açılışında başlama, bildirim alanı simgesi (açıkken renkli, kapalıyken gri), güncelleme kontrolü.

Windows "Windows kişisel bilgisayarınızı korudu" uyarısı verirse **Ek bilgi > Yine de çalıştır**'a bas: dosya imzasız olduğu için çıkıyor.

Kaynaktan derlemek için `build.ps1` (MinGW-w64, .NET SDK ve Inno Setup gerekir, ayrıntı aşağıda). Motora eklenen DNS-over-HTTPS'in ayrıntıları aşağıdaki *Built-in DNS over HTTPS* bölümünde.

---

GoodbyeDPI — Deep Packet Inspection circumvention utility
=========================

This software designed to bypass Deep Packet Inspection systems found in many Internet Service Providers which block access to certain websites.

It handles DPI connected using optical splitter or port mirroring (**Passive DPI**) which do not block any data but just replying faster than requested destination, and **Active DPI** connected in sequence.

**Windows 7, 8, 8.1, 10 or 11** with administrator privileges required.

# Quick start

* **For Russia**: Download [latest version from Releases page](https://github.com/ValdikSS/GoodbyeDPI/releases), unpack the file and run **1_russia_blacklist_dnsredir.cmd** script.
* For other countries: Download [latest version from Releases page](https://github.com/ValdikSS/GoodbyeDPI/releases), unpack the file and run **2_any_country_dnsredir.cmd**.

These scripts launch GoodbyeDPI in recommended mode with DNS resolver redirection to Yandex DNS on non-standard port (to prevent DNS poisoning).  
If it works — congratulations! You can use it as-is or configure further.

# How to use

Download [latest version from Releases page](https://github.com/ValdikSS/GoodbyeDPI/releases) and run.

## Supported arguments
To get relevant information about your version of the program, use the -h (--help) argument at startup.
```
Usage: goodbyedpi.exe [OPTION...]
 -p          block passive DPI
 -q          block QUIC/HTTP3
 -r          replace Host with hoSt
 -s          remove space between host header and its value
 -m          mix Host header case (test.com -> tEsT.cOm)
 -f <value>  set HTTP fragmentation to value
 -k <value>  enable HTTP persistent (keep-alive) fragmentation and set it to value
 -n          do not wait for first segment ACK when -k is enabled
 -e <value>  set HTTPS fragmentation to value
 -a          additional space between Method and Request-URI (enables -s, may break sites)
 -w          try to find and parse HTTP traffic on all processed ports (not only on port 80)
 --port        <value>    additional TCP port to perform fragmentation on (and HTTP tricks with -w)
 --ip-id       <value>    handle additional IP ID (decimal, drop redirects and TCP RSTs with this ID).
                          This option can be supplied multiple times.
 --dns-addr    <value>    redirect UDP DNS requests to the supplied IP address (experimental)
 --dns-port    <value>    redirect UDP DNS requests to the supplied port (53 by default)
 --dnsv6-addr  <value>    redirect UDPv6 DNS requests to the supplied IPv6 address (experimental)
 --dnsv6-port  <value>    redirect UDPv6 DNS requests to the supplied port (53 by default)
 --dns-verb               print verbose DNS redirection messages
 --blacklist   <txtfile>  perform circumvention tricks only to host names and subdomains from
                          supplied text file (HTTP Host/TLS SNI).
                          This option can be supplied multiple times.
 --allow-no-sni           perform circumvention if TLS SNI can't be detected with --blacklist enabled.
 --frag-by-sni            if SNI is detected in TLS packet, fragment the packet right before SNI value.
 --set-ttl     <value>    activate Fake Request Mode and send it with supplied TTL value.
                          DANGEROUS! May break websites in unexpected ways. Use with care (or --blacklist).
 --auto-ttl    [a1-a2-m]  activate Fake Request Mode, automatically detect TTL and decrease
                          it based on a distance. If the distance is shorter than a2, TTL is decreased
                          by a2. If it's longer, (a1; a2) scale is used with the distance as a weight.
                          If the resulting TTL is more than m(ax), set it to m.
                          Default (if set): --auto-ttl 1-4-10. Also sets --min-ttl 3.
                          DANGEROUS! May break websites in unexpected ways. Use with care (or --blacklist).
 --min-ttl     <value>    minimum TTL distance (128/64 - TTL) for which to send Fake Request
                          in --set-ttl and --auto-ttl modes.
 --wrong-chksum           activate Fake Request Mode and send it with incorrect TCP checksum.
                          May not work in a VM or with some routers, but is safer than set-ttl.
 --wrong-seq              activate Fake Request Mode and send it with TCP SEQ/ACK in the past.
 --native-frag            fragment (split) the packets by sending them in smaller packets, without
                          shrinking the Window Size. Works faster (does not slow down the connection)
                          and better.
 --reverse-frag           fragment (split) the packets just as --native-frag, but send them in the
                          reversed order. Works with the websites which could not handle segmented
                          HTTPS TLS ClientHello (because they receive the TCP flow "combined").
 --fake-from-hex <value>  Load fake packets for Fake Request Mode from HEX values (like 1234abcDEF).
                          This option can be supplied multiple times, in this case each fake packet
                          would be sent on every request in the command line argument order.
 --fake-with-sni <value>  Generate fake packets for Fake Request Mode with given SNI domain name.
                          The packets mimic Mozilla Firefox 130 TLS ClientHello packet
                          (with random generated fake SessionID, key shares and ECH grease).
                          Can be supplied multiple times for multiple fake packets.
 --fake-gen <value>       Generate random-filled fake packets for Fake Request Mode, value of them
                          (up to 30).
 --fake-resend <value>    Send each fake packet value number of times.
                          Default: 1 (send each packet once).
 --max-payload [value]    packets with TCP payload data more than [value] won't be processed.
                          Use this option to reduce CPU usage by skipping huge amount of data
                          (like file transfers) in already established sessions.
                          May skip some huge HTTP requests from being processed.
                          Default (if set): --max-payload 1200.


LEGACY modesets:
 -1          -p -r -s -f 2 -k 2 -n -e 2 (most compatible mode)
 -2          -p -r -s -f 2 -k 2 -n -e 40 (better speed for HTTPS yet still compatible)
 -3          -p -r -s -e 40 (better speed for HTTP and HTTPS)
 -4          -p -r -s (best speed)

Modern modesets (more stable, more compatible, faster):
 -5          -f 2 -e 2 --auto-ttl --reverse-frag --max-payload
 -6          -f 2 -e 2 --wrong-seq --reverse-frag --max-payload
 -7          -f 2 -e 2 --wrong-chksum --reverse-frag --max-payload
 -8          -f 2 -e 2 --wrong-seq --wrong-chksum --reverse-frag --max-payload
 -9          -f 2 -e 2 --wrong-seq --wrong-chksum --reverse-frag --max-payload -q (this is the default)

 Note: combination of --wrong-seq and --wrong-chksum generates two different fake packets.
```
## How to check
To check if your ISP's DPI could be circumvented, first make sure that your provider does not poison DNS answers by enabling "Secure DNS (DNS over HTTPS)" option in your browser.

* **Chrome**: Settings → [Privacy and security](chrome://settings/security) → Use secure DNS → With: NextDNS
* **Firefox**: [Settings](about:preferences) → Network Settings → Enable DNS over HTTPS → Use provider: NextDNS

Then run the `goodbyedpi.exe` executable without any options. If it works — congratulations! You can use it as-is or configure further, for example by using `--blacklist` option if the list of blocked websites is known and available for your country.

If your provider intercepts DNS requests, you may want to use `--dns-addr` option to a public DNS resolver running on non-standard port (such as Yandex DNS `77.88.8.8:1253`) or configure DNS over HTTPS/TLS using third-party applications.

Check the .cmd scripts and modify it according to your preference and network conditions.

## Built-in DNS over HTTPS

Where the ISP poisons or intercepts plain DNS (Turkey, for example, answers blocked names with a block page address), GoodbyeDPI can resolve every DNS query over HTTPS by itself. Nothing else has to be installed or configured: it uses WinHTTP, which ships with Windows.

```
goodbyedpi.exe                                    # default mode -9, DNS over HTTPS on
goodbyedpi.exe -9 --doh                           # Cloudflare, then Google
goodbyedpi.exe -9 --doh quad9,adguard             # other providers, in this order
goodbyedpi.exe -9 --doh https://9.9.9.9/dns-query # any RFC 8484 server
goodbyedpi.exe -9 --doh --dns-verb                # print every resolved name
```

How it works:

* Outgoing plain DNS queries (UDP port 53, any server) are captured and sent as RFC 8484 POST requests; the answer is injected back as if the original DNS server sent it. Browsers, games and Windows itself keep using their usual DNS settings.
* DoH servers are reached by IP address (`https://1.1.1.1/dns-query`): no DNS lookup is needed for the DoH server itself and the TLS handshake carries no SNI to block. A URL with a host name works too; that name is resolved through the other servers in the list.
* Servers are checked at startup. Working servers are used first, with automatic failover to the next one. If no server is reachable, DNS is left untouched and GoodbyeDPI keeps running.
* Answers are cached for their TTL (5 minutes at most), and identical queries arriving at the same time share one HTTPS request.
* Local names stay on the system DNS server: single-label names, `.local`, `.lan`, `.home.arpa`, the adapters' DNS suffixes and reverse lookups of private ranges.
* If every server fails for a query, that query falls back to plain DNS so the connection never stays without DNS.

`--doh` can't be combined with `--dns-addr`; use `--no-doh` to turn it off in the default mode.

## GoodbyeDPI Panel

`GoodbyeDPI-Kurulum.exe` is the setup (Turkish wizard, Inno Setup): it installs GoodbyeDPI, WinDivert and the panel to `C:\Program Files\GoodbyeDPI`, adds Start menu (and optionally desktop) shortcuts and an entry in Settings > Apps for uninstalling. `GoodbyeDPI-Panel.exe` also works on its own as a portable single file (.NET Framework 4.8, nothing to install) and installs itself on first start.

The panel registers the `GoodbyeDPI` Windows service and turns it on and off. There is no mode to choose: on Start it tries the methods in turn and keeps the first one that opens 8 addresses blocked by DPI in Turkey (Discord site and app endpoints, Roblox, Wattpad, Pastebin); the method is saved and only re-checked on later starts. Measured on Türk Telekom, TTL based methods (`-5`, `-5 --set-ttl 5`) opened 8/8 addresses every time while the fake-packet ones (`-6` … `-9`) opened 3–7 of 8, differently on every try, so the TTL ones are tried first. When the network changes (another Wi-Fi, a hotspot, waking up) the panel quietly re-tests the running method and searches again only if it stopped working. The panel also picks the DNS-over-HTTPS provider, starts protection with Windows (the service) and shows its notification area icon at logon (a scheduled task, since the panel needs administrator rights), checks GitHub for a newer release and uninstalls everything. Closing the window keeps it in the notification area (colored icon: protection on, gray: off); protection keeps running even after Exit. When running as a service, GoodbyeDPI writes its messages to `goodbyedpi.log` next to the executable.

# How does it work

### Passive DPI

Most Passive DPI send HTTP 302 Redirect if you try to access blocked website over HTTP and TCP Reset in case of HTTPS, faster than destination website. Packets sent by DPI usually have IP Identification field equal to `0x0000` or `0x0001`, as seen with Russian providers. These packets, if they redirect you to another website (censorship page), are blocked by GoodbyeDPI.

### Active DPI

Active DPI is more tricky to fool. Currently the software uses 7 methods to circumvent Active DPI:

* TCP-level fragmentation for first data packet
* TCP-level fragmentation for persistent (keep-alive) HTTP sessions
* Replacing `Host` header with `hoSt`
* Removing space between header name and value in `Host` header
* Adding additional space between HTTP Method (GET, POST etc) and URI
* Mixing case of Host header value
* Sending fake HTTP/HTTPS packets with low Time-To-Live value, incorrect checksum or incorrect TCP Sequence/Acknowledgement numbers to fool DPI and prevent delivering them to the destination

These methods should not break any website as they're fully compatible with TCP and HTTP standards, yet it's sufficient to prevent DPI data classification and to circumvent censorship. Additional space may break some websites, although it's acceptable by HTTP/1.1 specification (see 19.3 Tolerant Applications).

The program loads WinDivert driver which uses Windows Filtering Platform to set filters and redirect packets to the userspace. It's running as long as console window is visible and terminates when you close the window.

# How to build from source

This project can be built using **GNU Make** and [**mingw**](https://mingw-w64.org). The only dependency is [WinDivert](https://github.com/basil00/Divert).

To build x86 exe run:

`make CPREFIX=i686-w64-mingw32- WINDIVERTHEADERS=/path/to/windivert/include WINDIVERTLIBS=/path/to/windivert/x86`

And for x86_64:

`make CPREFIX=x86_64-w64-mingw32- BIT64=1 WINDIVERTHEADERS=/path/to/windivert/include WINDIVERTLIBS=/path/to/windivert/amd64`

On Windows, `build.ps1` does everything: it uses MinGW-w64 from winget (`winget install BrechtSanders.WinLibs.POSIX.UCRT`), downloads and verifies WinDivert, builds and puts `goodbyedpi.exe` with the WinDivert files into `dist\x86_64`.

# How to install as Windows Service

Check examples in `service_install_russia_blacklist.cmd`, `service_install_russia_blacklist_dnsredir.cmd` and `service_remove.cmd` scripts.

Modify them according to your own needs.

# Known issues

* Horribly outdated Windows 7 installations are not able to load WinDivert driver due to missing support for SHA256 digital signatures. Install KB3033929 [x86](https://www.microsoft.com/en-us/download/details.aspx?id=46078)/[x64](https://www.microsoft.com/en-us/download/details.aspx?id=46148), or better, update the whole system using Windows Update.
* Intel/Qualcomm Killer network cards: `Advanced Stream Detect` in Killer Control Center is incompatible with GoodbyeDPI, [disable it](https://github.com/ValdikSS/GoodbyeDPI/issues/541#issuecomment-2296038239).
* QUIK trading software [may interfere with GoodbyeDPI](https://github.com/ValdikSS/GoodbyeDPI/issues/677#issuecomment-2390595606). First start QUIK, then GoodbyeDPI.
* ~~Some SSL/TLS stacks unable to process fragmented ClientHello packets, and HTTPS websites won't open. Bug: [#4](https://github.com/ValdikSS/GoodbyeDPI/issues/4), [#64](https://github.com/ValdikSS/GoodbyeDPI/issues/64).~~ Fragmentation issues are fixed in v0.1.7.
* ~~ESET Antivirus is incompatible with WinDivert driver [#91](https://github.com/ValdikSS/GoodbyeDPI/issues/91). This is most probably antivirus bug, not WinDivert.~~


# Similar projects

- **[zapret](https://github.com/bol-van/zapret)** by @bol-van (for MacOS, Linux and Windows)
- **[Green Tunnel](https://github.com/SadeghHayeri/GreenTunnel)** by @SadeghHayeri (for MacOS, Linux and Windows)
- **[DPI Tunnel CLI](https://github.com/nomoresat/DPITunnel-cli)** by @zhenyolka (for Linux and routers)
- **[DPI Tunnel for Android](https://github.com/nomoresat/DPITunnel-android)** by @zhenyolka (for Android)
- **[PowerTunnel](https://github.com/krlvm/PowerTunnel)** by @krlvm (for Windows, MacOS and Linux)
- **[PowerTunnel for Android](https://github.com/krlvm/PowerTunnel-Android)** by @krlvm (for Android)
- **[SpoofDPI](https://github.com/xvzc/SpoofDPI)** by @xvzc (for macOS and Linux)
- **[SpoofDPI-Platform](https://github.com/r3pr3ss10n/SpoofDPI-Platform)** by @r3pr3ss10n (for Android, macOS, Windows)
- **[GhosTCP](https://github.com/macronut/ghostcp)** by @macronut (for Windows)
- **[ByeDPI](https://github.com/hufrea/byedpi)** for Linux/Windows + **[ByeDPIAndroid](https://github.com/dovecoteescapee/ByeDPIAndroid/)** / **[ByeByeDPI](https://github.com/romanvht/ByeByeDPI/)** for Android (no root)
- **[youtubeUnblock](https://github.com/Waujito/youtubeUnblock/)** by @Waujito (for OpenWRT/Entware routers and Linux)

# Kudos

Thanks @basil00 for [WinDivert](https://github.com/basil00/Divert). That's the main part of this program.

Thanks for every [BlockCheck](https://github.com/ValdikSS/blockcheck) contributor. It would be impossible to understand DPI behaviour without this utility.
