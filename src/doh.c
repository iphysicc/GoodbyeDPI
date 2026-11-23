/*
 * Built-in DNS-over-HTTPS resolver for GoodbyeDPI.
 *
 * Plain DNS is filtered or poisoned in many countries, so the DPI tricks
 * alone are not enough: the browser never learns the real address.
 * This module captures every outgoing plain DNS query (UDP port 53) with
 * its own WinDivert handle, sends it over HTTPS (RFC 8484) with WinHTTP and
 * injects the answer back as if it came from the DNS server the system
 * asked. Applications and Windows settings need no change.
 *
 * Nothing extra to install: WinHTTP and Schannel ship with Windows.
 *
 * DoH servers are addressed by IP (https://1.1.1.1/dns-query), so
 *   - resolving the DoH server itself never needs DNS (no chicken-and-egg),
 *   - the TLS ClientHello carries no SNI to block.
 * A URL with a host name is allowed too; that host name is then resolved
 * through the other configured servers.
 *
 * Local names (single label, .local, .lan, .home.arpa, the adapters' DNS
 * suffixes, reverse lookups of private ranges) are passed to the original
 * DNS server untouched, so routers, printers and LAN shares keep working.
 *
 * If every DoH server fails for a query, the original query is let through
 * (fail open) so the connection is never left without DNS.
 */

/* SRW locks and condition variables need Vista; older MinGW defaults to XP. */
#if !defined(_WIN32_WINNT) || _WIN32_WINNT < 0x0600
#undef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winhttp.h>
#include <iphlpapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdint.h>
#include "windivert.h"
#include "goodbyedpi.h"
#include "doh.h"

#ifndef WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL
#define WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL 133
#endif
#ifndef WINHTTP_PROTOCOL_FLAG_HTTP2
#define WINHTTP_PROTOCOL_FLAG_HTTP2 0x1
#endif

#define DOH_MAX_PROVIDERS   16
#define DOH_MAX_SUFFIXES    32
#define DOH_MAX_ANSWER      65000   /* one UDP datagram incl. IPv6+UDP headers */
#define DOH_TEST_TIMEOUT_MS 10000 /* longer than the WinHTTP timeouts below */
#define DOH_FILTER "outbound and !loopback and !impostor and " \
                   "udp and udp.DstPort == 53 and udp.PayloadLength >= 17"

typedef struct {
    char        url[300];     /* as given, for messages */
    char        host[256];    /* lower case, for loop detection */
    wchar_t     whost[256];
    wchar_t     wpath[512];
    INTERNET_PORT port;
    int         host_is_ip;
    HINTERNET   connect;
    volatile LONG ok;         /* passed the startup self-test */
    volatile LONG test_ms;
} doh_provider_t;

typedef struct {
    const char *name;
    const char *urls[2];
} doh_builtin_t;

static const doh_builtin_t builtin_providers[] = {
    {"cloudflare", {"https://1.1.1.1/dns-query",  "https://1.0.0.1/dns-query"}},
    {"google",     {"https://8.8.8.8/dns-query",  "https://8.8.4.4/dns-query"}},
    {"quad9",      {"https://9.9.9.9/dns-query",  "https://149.112.112.112/dns-query"}},
    {"adguard",    {"https://94.140.14.14/dns-query", "https://94.140.15.15/dns-query"}},
};

static doh_provider_t providers[DOH_MAX_PROVIDERS];
static int provider_num = 0;
static volatile LONG preferred = 0;   /* index of the last provider that answered */

static char local_suffixes[DOH_MAX_SUFFIXES][256];
static int local_suffix_num = 0;

static HINTERNET session = NULL;
static HANDLE divert = NULL;
static int doh_verbose = 0;

typedef struct {
    WINDIVERT_ADDRESS addr;
    UINT len;
    char packet[];
} doh_job_t;

void doh_print_help(void) {
    printf("Built-in DoH providers:\n");
    for (size_t i = 0; i < sizeof(builtin_providers) / sizeof(builtin_providers[0]); i++)
        printf("  %-11s %s, %s\n", builtin_providers[i].name,
               builtin_providers[i].urls[0], builtin_providers[i].urls[1]);
}

/* ---------- small helpers ---------- */

static void str_lower(char *s) {
    for (; *s; s++)
        *s = (char)tolower((unsigned char)*s);
}

static int ends_with_label(const char *name, const char *suffix) {
    size_t nl = strlen(name), sl = strlen(suffix);
    if (nl == sl)
        return strcmp(name, suffix) == 0;
    if (nl > sl)
        return name[nl - sl - 1] == '.' && strcmp(name + nl - sl, suffix) == 0;
    return 0;
}

static int host_is_ip_literal(const char *host) {
    struct in_addr a4;
    struct in6_addr a6;
    return inet_pton(AF_INET, host, &a4) == 1 || inet_pton(AF_INET6, host, &a6) == 1;
}

/* ---------- configuration ---------- */

static int add_provider_url(const char *url) {
    doh_provider_t *p;
    const char *h, *hend, *path;
    char portbuf[8];
    size_t hlen;
    long port = 443;

    if (provider_num >= DOH_MAX_PROVIDERS) {
        printf("DoH: too many servers, ignoring %s\n", url);
        return 1;
    }
    if (_strnicmp(url, "https://", 8) != 0) {
        printf("DoH: server URL must start with https:// (%s)\n", url);
        return 0;
    }

    p = &providers[provider_num];
    memset(p, 0, sizeof(*p));
    snprintf(p->url, sizeof(p->url), "%s", url);

    h = url + 8;
    if (*h == '[') {                     /* [IPv6]:port/path */
        hend = strchr(h, ']');
        if (!hend) return 0;
        hlen = (size_t)(hend - h - 1);
        if (hlen == 0 || hlen >= sizeof(p->host)) return 0;
        memcpy(p->host, h + 1, hlen);
        hend++;
    }
    else {
        hend = h + strcspn(h, ":/");
        hlen = (size_t)(hend - h);
        if (hlen == 0 || hlen >= sizeof(p->host)) return 0;
        memcpy(p->host, h, hlen);
    }
    p->host[hlen] = '\0';
    str_lower(p->host);

    if (*hend == ':') {
        size_t plen = strcspn(hend + 1, "/");
        if (plen == 0 || plen >= sizeof(portbuf)) return 0;
        memcpy(portbuf, hend + 1, plen);
        portbuf[plen] = '\0';
        port = strtol(portbuf, NULL, 10);
        if (port <= 0 || port > 65535) return 0;
        hend += 1 + plen;
    }
    path = (*hend == '/') ? hend : "/dns-query";

    p->port = (INTERNET_PORT)port;
    p->host_is_ip = host_is_ip_literal(p->host);
    MultiByteToWideChar(CP_UTF8, 0, p->host, -1, p->whost, 256);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, p->wpath, 512);
    provider_num++;
    return 1;
}

static int parse_providers(const char *list) {
    char *copy = strdup(list ? list : DOH_DEFAULT_PROVIDERS);
    char *ctx = NULL;
    int ok = 1;

    for (char *tok = strtok_s(copy, ", ", &ctx); tok; tok = strtok_s(NULL, ", ", &ctx)) {
        size_t i, found = 0;
        for (i = 0; i < sizeof(builtin_providers) / sizeof(builtin_providers[0]); i++) {
            if (_stricmp(tok, builtin_providers[i].name) == 0) {
                ok &= add_provider_url(builtin_providers[i].urls[0]);
                ok &= add_provider_url(builtin_providers[i].urls[1]);
                found = 1;
                break;
            }
        }
        if (found)
            continue;
        if (_strnicmp(tok, "https://", 8) == 0) {
            if (!add_provider_url(tok)) {
                printf("DoH: bad server URL: %s\n", tok);
                ok = 0;
            }
            continue;
        }
        printf("DoH: unknown provider \"%s\"\n", tok);
        doh_print_help();
        ok = 0;
    }
    free(copy);

    if (ok && provider_num == 0) {
        printf("DoH: no servers given\n");
        ok = 0;
    }
    return ok;
}

static void add_local_suffix(const char *suffix) {
    /* Never treat a public top-level zone as local, whatever DHCP says. */
    static const char *public_zones[] = {"com", "net", "org", "tr", "com.tr", "net.tr", "org.tr"};
    char s[256];
    size_t i;

    if (!suffix || !*suffix || local_suffix_num >= DOH_MAX_SUFFIXES)
        return;
    snprintf(s, sizeof(s), "%s", suffix);
    str_lower(s);
    while (*s && s[strlen(s) - 1] == '.')
        s[strlen(s) - 1] = '\0';
    if (!*s)
        return;
    for (i = 0; i < sizeof(public_zones) / sizeof(public_zones[0]); i++)
        if (strcmp(s, public_zones[i]) == 0)
            return;
    for (i = 0; i < (size_t)local_suffix_num; i++)
        if (strcmp(s, local_suffixes[i]) == 0)
            return;
    snprintf(local_suffixes[local_suffix_num++], 256, "%s", s);
}

static void load_local_suffixes(void) {
    static const char *defaults[] = {
        "local", "lan", "home", "home.arpa", "internal", "intranet",
        "localdomain", "localhost", "corp", "private",
    };
    ULONG size = 0;
    PIP_ADAPTER_ADDRESSES aa, cur;
    FIXED_INFO *fi;
    size_t i;

    for (i = 0; i < sizeof(defaults) / sizeof(defaults[0]); i++)
        add_local_suffix(defaults[i]);

    /* Primary DNS suffix of the machine (AD domain etc.) */
    size = 0;
    if (GetNetworkParams(NULL, &size) == ERROR_BUFFER_OVERFLOW) {
        fi = malloc(size);
        if (fi && GetNetworkParams(fi, &size) == NO_ERROR)
            add_local_suffix(fi->DomainName);
        free(fi);
    }

    /* Connection specific suffixes handed out by DHCP */
    size = 16384;
    aa = malloc(size);
    if (aa && GetAdaptersAddresses(AF_UNSPEC,
            GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_UNICAST,
            NULL, aa, &size) == ERROR_BUFFER_OVERFLOW) {
        free(aa);
        aa = malloc(size);
        if (aa && GetAdaptersAddresses(AF_UNSPEC,
                GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_UNICAST,
                NULL, aa, &size) != NO_ERROR) {
            free(aa);
            aa = NULL;
        }
    }
    for (cur = aa; cur; cur = cur->Next) {
        char buf[256];
        if (cur->OperStatus != IfOperStatusUp || !cur->DnsSuffix || !cur->DnsSuffix[0])
            continue;
        if (WideCharToMultiByte(CP_UTF8, 0, cur->DnsSuffix, -1, buf, sizeof(buf), NULL, NULL))
            add_local_suffix(buf);
    }
    free(aa);
}

/* ---------- DNS message helpers ---------- */

/* Reads the first question name as a lower case dotted string. */
static int dns_read_qname(const unsigned char *msg, UINT len, char *out, size_t outcap) {
    UINT pos = 12;
    size_t o = 0;

    if (len < 17 || ((msg[4] << 8) | msg[5]) != 1)   /* QDCOUNT must be 1 */
        return 0;
    while (pos < len) {
        unsigned int l = msg[pos++];
        if (l == 0) {
            out[o] = '\0';
            return 1;
        }
        if (l > 63 || pos + l > len || o + l + 2 > outcap)
            return 0;
        if (o)
            out[o++] = '.';
        for (unsigned int i = 0; i < l; i++)
            out[o++] = (char)tolower(msg[pos + i]);
        pos += l;
    }
    return 0;
}

static int reverse_is_private(const char *name) {
    /* x.x.168.192.in-addr.arpa style: the last labels are the first octets */
    char tmp[256];
    char *labels[8];
    int n = 0;
    size_t nl = strlen(name), sl = strlen(".in-addr.arpa");
    long a, b = -1;

    if (nl <= sl)
        return 0;
    memcpy(tmp, name, nl - sl);
    tmp[nl - sl] = '\0';
    for (char *ctx = NULL, *t = strtok_s(tmp, ".", &ctx); t && n < 8; t = strtok_s(NULL, ".", &ctx))
        labels[n++] = t;
    if (n == 0)
        return 0;
    a = strtol(labels[n - 1], NULL, 10);
    if (n >= 2)
        b = strtol(labels[n - 2], NULL, 10);
    return a == 10 || a == 127 || a == 0 ||
           (a == 192 && b == 168) || (a == 169 && b == 254) ||
           (a == 172 && b >= 16 && b <= 31) ||
           (a == 100 && b >= 64 && b <= 127);  /* CGNAT */
}

static int name_is_local(const char *name) {
    static const char *ip6_private[] = {
        "d.f.ip6.arpa", "c.f.ip6.arpa",
        "8.e.f.ip6.arpa", "9.e.f.ip6.arpa", "a.e.f.ip6.arpa", "b.e.f.ip6.arpa",
    };
    size_t i;

    if (!*name)
        return 0;                    /* root zone query: send it over DoH */
    if (!strchr(name, '.'))
        return 1;                    /* single label: NetBIOS style LAN name */
    if (ends_with_label(name, "in-addr.arpa"))
        return reverse_is_private(name);
    for (i = 0; i < sizeof(ip6_private) / sizeof(ip6_private[0]); i++)
        if (ends_with_label(name, ip6_private[i]))
            return 1;
    for (i = 0; i < (size_t)local_suffix_num; i++)
        if (ends_with_label(name, local_suffixes[i]))
            return 1;
    return 0;
}

/* ---------- answer cache ----------
 *
 * Windows sends each query to every configured DNS server at once, and some
 * programs (Chrome's own resolver, WSL) skip the Windows cache entirely.
 * Answers are kept for their TTL (at most DOH_CACHE_MAX_TTL seconds), and a
 * query that arrives while the same question is being resolved waits for
 * that answer instead of sending another HTTPS request.
 */

#define DOH_CACHE_BUCKETS     1024
#define DOH_CACHE_MAX_ENTRIES 4096
#define DOH_CACHE_MIN_TTL     2
#define DOH_CACHE_MAX_TTL     300
#define DOH_CACHE_WAIT_MS     4000
#define DOH_KEY_MAX           (2 + 255 + 4)

typedef struct {
    UINT len;
    UINT qsection;                /* bytes of the question section */
    uint32_t hash;
    unsigned char b[DOH_KEY_MAX]; /* flags (RD/CD) + lower case question */
} doh_key_t;

typedef struct doh_cache_entry {
    struct doh_cache_entry *next;
    doh_key_t key;
    int pending;
    ULONGLONG expires;
    DWORD alen;
    char *answer;
} doh_cache_entry_t;

static doh_cache_entry_t *cache[DOH_CACHE_BUCKETS];
static int cache_count = 0;
static SRWLOCK cache_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE cache_cv = CONDITION_VARIABLE_INIT;

static int dns_skip_name(const unsigned char *m, UINT len, UINT *pos) {
    while (*pos < len) {
        unsigned int l = m[*pos];
        if ((l & 0xC0) == 0xC0) {
            *pos += 2;
            return *pos <= len;
        }
        if (l & 0xC0)
            return 0;
        *pos += 1;
        if (l == 0)
            return 1;
        *pos += l;
    }
    return 0;
}

static int make_key(const unsigned char *q, UINT qlen, doh_key_t *k) {
    UINT pos = 12;

    if (qlen < 17 || !dns_skip_name(q, qlen, &pos) || pos + 4 > qlen)
        return 0;
    pos += 4;                                     /* QTYPE + QCLASS */
    k->qsection = pos - 12;
    if (k->qsection + 2 > DOH_KEY_MAX)
        return 0;
    k->b[0] = q[2] & 0x01;                        /* RD */
    k->b[1] = q[3] & 0x10;                        /* CD */
    for (UINT i = 0; i < k->qsection; i++)
        k->b[2 + i] = (unsigned char)tolower(q[12 + i]);
    k->len = k->qsection + 2;
    k->hash = 2166136261u;                        /* FNV-1a */
    for (UINT i = 0; i < k->len; i++)
        k->hash = (k->hash ^ k->b[i]) * 16777619u;
    return 1;
}

/* Smallest TTL of the answer and authority records, clamped. */
static DWORD answer_ttl(const unsigned char *a, DWORD alen) {
    UINT pos = 12;
    unsigned int qd = (a[4] << 8) | a[5];
    unsigned int rr = (unsigned int)(((a[6] << 8) | a[7]) + ((a[8] << 8) | a[9]));
    DWORD ttl = DOH_CACHE_MAX_TTL;

    if ((a[3] & 0x0F) != 0 && (a[3] & 0x0F) != 3) /* only NOERROR / NXDOMAIN */
        return 0;
    while (qd--)
        if (!dns_skip_name(a, alen, &pos) || (pos += 4) > alen)
            return 0;
    if (rr == 0)
        return 30;                                /* empty answer (NODATA) */
    while (rr--) {
        DWORD t;
        unsigned int rdlen;
        if (!dns_skip_name(a, alen, &pos) || pos + 10 > alen)
            return 0;
        t = ((DWORD)a[pos + 4] << 24) | ((DWORD)a[pos + 5] << 16) |
            ((DWORD)a[pos + 6] << 8) | a[pos + 7];
        rdlen = (a[pos + 8] << 8) | a[pos + 9];
        pos += 10 + rdlen;
        if (pos > alen)
            return 0;
        if (t < ttl)
            ttl = t;
    }
    return ttl < DOH_CACHE_MIN_TTL ? DOH_CACHE_MIN_TTL : ttl;
}

static doh_cache_entry_t **cache_find(const doh_key_t *k) {
    doh_cache_entry_t **e = &cache[k->hash % DOH_CACHE_BUCKETS];
    for (; *e; e = &(*e)->next)
        if ((*e)->key.len == k->len && memcmp((*e)->key.b, k->b, k->len) == 0)
            return e;
    return e;
}

static void cache_unlink(doh_cache_entry_t **e) {
    doh_cache_entry_t *dead = *e;
    *e = dead->next;
    free(dead->answer);
    free(dead);
    cache_count--;
}

static void cache_purge(ULONGLONG now) {
    for (int b = 0; b < DOH_CACHE_BUCKETS; b++) {
        doh_cache_entry_t **e = &cache[b];
        while (*e) {
            if (!(*e)->pending && ((*e)->expires <= now || cache_count > DOH_CACHE_MAX_ENTRIES / 2))
                cache_unlink(e);
            else
                e = &(*e)->next;
        }
    }
}

/* Returns 1 with a cached answer copied into answer (ID and question taken
 * from the query). Returns 0 when the caller has to resolve it. */
static int cache_get(const doh_key_t *k, const char *query, char *answer, DWORD *alen) {
    doh_cache_entry_t **e;
    ULONGLONG now, deadline = GetTickCount64() + DOH_CACHE_WAIT_MS;
    int hit = 0;

    AcquireSRWLockExclusive(&cache_lock);
    for (;;) {
        now = GetTickCount64();
        e = cache_find(k);
        if (!*e || !(*e)->pending || now >= deadline)
            break;
        SleepConditionVariableSRW(&cache_cv, &cache_lock, (DWORD)(deadline - now), 0);
    }
    if (*e && !(*e)->pending) {
        if ((*e)->expires > now) {
            memcpy(answer, (*e)->answer, (*e)->alen);
            *alen = (*e)->alen;
            hit = 1;
        }
        else {
            cache_unlink(e);
            e = cache_find(k);
        }
    }
    if (!hit && !*e) {                            /* reserve: others will wait for us */
        doh_cache_entry_t *n = calloc(1, sizeof(*n));
        if (n) {
            if (cache_count >= DOH_CACHE_MAX_ENTRIES)
                cache_purge(now);
            n->key = *k;
            n->pending = 1;
            n->next = cache[k->hash % DOH_CACHE_BUCKETS];
            cache[k->hash % DOH_CACHE_BUCKETS] = n;
            cache_count++;
        }
    }
    ReleaseSRWLockExclusive(&cache_lock);

    if (hit) {
        memcpy(answer, query, 2);                 /* this query's ID */
        if (*alen >= 12 + k->qsection)            /* and its exact name case */
            memcpy(answer + 12, query + 12, k->qsection);
    }
    return hit;
}

/* Stores the answer (answer == NULL: resolving failed) and wakes waiters. */
static void cache_put(const doh_key_t *k, const char *answer, DWORD alen) {
    doh_cache_entry_t **e;
    DWORD ttl = answer ? answer_ttl((const unsigned char *)answer, alen) : 0;
    char *copy = ttl ? malloc(alen) : NULL;

    AcquireSRWLockExclusive(&cache_lock);
    e = cache_find(k);
    if (copy && *e) {
        memcpy(copy, answer, alen);
        free((*e)->answer);
        (*e)->answer = copy;
        (*e)->alen = alen;
        (*e)->expires = GetTickCount64() + (ULONGLONG)ttl * 1000;
        (*e)->pending = 0;
        copy = NULL;
    }
    else if (*e && (*e)->pending) {
        cache_unlink(e);
    }
    ReleaseSRWLockExclusive(&cache_lock);
    WakeAllConditionVariable(&cache_cv);
    free(copy);
}

/* ---------- HTTPS exchange ---------- */

static int doh_exchange(doh_provider_t *p, const char *query, DWORD qlen,
                        char *answer, DWORD cap, DWORD *alen)
{
    static const wchar_t headers[] =
        L"Content-Type: application/dns-message\r\n"
        L"Accept: application/dns-message\r\n";
    HINTERNET req;
    DWORD status = 0, size = sizeof(status), got = 0, total = 0;
    int ok = 0;

    req = WinHttpOpenRequest(p->connect, L"POST", p->wpath, NULL,
                             WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                             WINHTTP_FLAG_SECURE);
    if (!req)
        return 0;

    if (!WinHttpSendRequest(req, headers, (DWORD)-1L, (LPVOID)query, qlen, qlen, 0) ||
        !WinHttpReceiveResponse(req, NULL))
        goto out;
    if (!WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX, &status, &size,
                             WINHTTP_NO_HEADER_INDEX) || status != 200)
        goto out;

    for (;;) {
        if (total >= cap)
            goto out;                /* answer does not fit a UDP datagram */
        if (!WinHttpReadData(req, answer + total, cap - total, &got))
            goto out;
        if (got == 0)
            break;
        total += got;
    }
    if (total >= 12) {
        *alen = total;
        ok = 1;
    }
out:
    if (!ok && doh_verbose)
        printf("DoH: %s failed (error %lu, HTTP %lu, %lu bytes)\n",
               p->url, GetLastError(), status, total);
    WinHttpCloseHandle(req);
    return ok;
}

/* Sends the query to the configured servers, starting with the last one that
 * worked. A server whose own host name is being asked is skipped. */
static int doh_resolve(char *query, DWORD qlen, const char *qname,
                       char *answer, DWORD cap, DWORD *alen, int *used)
{
    uint16_t id;
    int start = (int)preferred;

    memcpy(&id, query, 2);
    memset(query, 0, 2);             /* RFC 8484 4.1: ID 0 is cache friendly */

    for (int n = 0; n < provider_num; n++) {
        int i = (start + n) % provider_num;
        doh_provider_t *p = &providers[i];
        if (!p->host_is_ip && strcmp(qname, p->host) == 0)
            continue;
        if (doh_exchange(p, query, qlen, answer, cap, alen)) {
            if (i != start)
                InterlockedExchange(&preferred, i);
            memcpy(answer, &id, 2);
            memcpy(query, &id, 2);
            *used = i;
            return 1;
        }
    }
    memcpy(query, &id, 2);
    return 0;
}

/* ---------- packet handling ---------- */

static void inject_answer(doh_job_t *job, PWINDIVERT_IPHDR ip, PWINDIVERT_IPV6HDR ip6,
                          PWINDIVERT_UDPHDR udp, const char *answer, DWORD alen)
{
    char *pkt;
    UINT hdr = ip ? sizeof(WINDIVERT_IPHDR) : sizeof(WINDIVERT_IPV6HDR);
    UINT total = hdr + (UINT)sizeof(WINDIVERT_UDPHDR) + alen;
    WINDIVERT_ADDRESS addr = job->addr;
    PWINDIVERT_UDPHDR rudp;

    pkt = calloc(1, total);
    if (!pkt)
        return;

    if (ip) {
        PWINDIVERT_IPHDR r = (PWINDIVERT_IPHDR)pkt;
        r->Version = 4;
        r->HdrLength = 5;
        r->Length = htons((u_short)total);
        r->TTL = 64;
        r->Protocol = IPPROTO_UDP;
        r->SrcAddr = ip->DstAddr;
        r->DstAddr = ip->SrcAddr;
    }
    else {
        PWINDIVERT_IPV6HDR r = (PWINDIVERT_IPV6HDR)pkt;
        r->Version = 6;
        r->Length = htons((u_short)(sizeof(WINDIVERT_UDPHDR) + alen));
        r->NextHdr = IPPROTO_UDP;
        r->HopLimit = 64;
        memcpy(r->SrcAddr, ip6->DstAddr, sizeof(r->SrcAddr));
        memcpy(r->DstAddr, ip6->SrcAddr, sizeof(r->DstAddr));
    }

    rudp = (PWINDIVERT_UDPHDR)(pkt + hdr);
    rudp->SrcPort = udp->DstPort;
    rudp->DstPort = udp->SrcPort;
    rudp->Length = htons((u_short)(sizeof(WINDIVERT_UDPHDR) + alen));
    memcpy(pkt + hdr + sizeof(WINDIVERT_UDPHDR), answer, alen);

    addr.Outbound = 0;
    addr.Impostor = 0;
    addr.IPChecksum = 0;
    addr.TCPChecksum = 0;
    addr.UDPChecksum = 0;
    WinDivertHelperCalcChecksums(pkt, total, &addr, 0);
    if (!WinDivertSend(divert, pkt, total, NULL, &addr) && doh_verbose)
        printf("DoH: can't inject answer (error %lu)\n", GetLastError());
    free(pkt);
}

static void pass_through(doh_job_t *job) {
    WinDivertSend(divert, job->packet, job->len, NULL, &job->addr);
}

static DWORD WINAPI doh_worker(LPVOID arg) {
    doh_job_t *job = arg;
    PWINDIVERT_IPHDR ip = NULL;
    PWINDIVERT_IPV6HDR ip6 = NULL;
    PWINDIVERT_UDPHDR udp = NULL;
    PVOID data = NULL;
    UINT dlen = 0;
    char qname[256];
    char *answer = NULL;
    DWORD alen = 0;
    int used = -1, have_key;
    doh_key_t key;
    ULONGLONG t0;

    if (!WinDivertHelperParsePacket(job->packet, job->len, &ip, &ip6, NULL, NULL, NULL,
                                    NULL, &udp, &data, &dlen, NULL, NULL) ||
        !udp || !data || (!ip && !ip6) ||
        !dns_read_qname(data, dlen, qname, sizeof(qname)) ||
        (((unsigned char *)data)[2] & 0x80))       /* QR set: not a query */
    {
        pass_through(job);
        goto done;
    }

    if (name_is_local(qname)) {
        if (doh_verbose)
            printf("DoH: %s -> local, sent to the original DNS server\n", qname);
        pass_through(job);
        goto done;
    }

    answer = malloc(DOH_MAX_ANSWER);
    if (!answer) {
        pass_through(job);
        goto done;
    }

    t0 = GetTickCount64();
    have_key = make_key(data, dlen, &key);
    if (have_key && cache_get(&key, data, answer, &alen)) {
        inject_answer(job, ip, ip6, udp, answer, alen);
        if (doh_verbose)
            printf("DoH: %s -> cache, %llu ms\n", qname, GetTickCount64() - t0);
    }
    else if (doh_resolve(data, dlen, qname, answer, DOH_MAX_ANSWER, &alen, &used)) {
        if (have_key)
            cache_put(&key, answer, alen);
        inject_answer(job, ip, ip6, udp, answer, alen);
        if (doh_verbose)
            printf("DoH: %s -> %s, %llu ms\n", qname, providers[used].url,
                   GetTickCount64() - t0);
    }
    else {
        if (have_key)
            cache_put(&key, NULL, 0);
        printf("DoH: no server answered for %s, falling back to plain DNS\n", qname);
        pass_through(job);
    }

done:
    free(answer);
    free(job);
    return 0;
}

static DWORD WINAPI doh_recv_thread(LPVOID arg __attribute__((unused))) {
    char packet[MAX_PACKET_SIZE];
    UINT len;
    WINDIVERT_ADDRESS addr;

    for (;;) {
        doh_job_t *job;
        if (!WinDivertRecv(divert, packet, sizeof(packet), &len, &addr)) {
            DWORD err = GetLastError();
            if (err == ERROR_INSUFFICIENT_BUFFER)
                continue;
            /* handle closed or shut down: GoodbyeDPI is exiting */
            return 0;
        }
        job = malloc(sizeof(doh_job_t) + len);
        if (!job)
            continue;
        job->addr = addr;
        job->len = len;
        memcpy(job->packet, packet, len);
        if (!QueueUserWorkItem(doh_worker, job, WT_EXECUTELONGFUNCTION)) {
            WinDivertSend(divert, packet, len, NULL, &addr);
            free(job);
        }
    }
}

/* ---------- startup ---------- */

/* Builds a minimal A query for name into buf, returns its length. */
static DWORD build_query(const char *name, unsigned char *buf, size_t cap) {
    size_t pos = 12;
    const char *s = name;

    memset(buf, 0, 12);
    buf[2] = 0x01;                   /* RD */
    buf[5] = 0x01;                   /* QDCOUNT = 1 */
    while (*s) {
        const char *dot = strchr(s, '.');
        size_t l = dot ? (size_t)(dot - s) : strlen(s);
        if (pos + l + 6 > cap)
            return 0;
        buf[pos++] = (unsigned char)l;
        memcpy(buf + pos, s, l);
        pos += l;
        s += l;
        if (*s == '.')
            s++;
    }
    buf[pos++] = 0;
    buf[pos++] = 0; buf[pos++] = 1;  /* QTYPE A */
    buf[pos++] = 0; buf[pos++] = 1;  /* QCLASS IN */
    return (DWORD)pos;
}

static DWORD WINAPI doh_test_thread(LPVOID arg) {
    doh_provider_t *p = arg;
    unsigned char q[64];
    char *a = malloc(DOH_MAX_ANSWER);
    DWORD qlen = build_query("example.com", q, sizeof(q)), alen = 0;
    ULONGLONG t0 = GetTickCount64();

    if (a && doh_exchange(p, (char *)q, qlen, a, DOH_MAX_ANSWER, &alen) &&
        (a[3] & 0x0F) == 0)          /* RCODE NOERROR */
    {
        InterlockedExchange(&p->test_ms, (LONG)(GetTickCount64() - t0));
        InterlockedExchange(&p->ok, 1);
    }
    free(a);
    return 0;
}

static int self_test(void) {
    HANDLE threads[DOH_MAX_PROVIDERS];
    doh_provider_t sorted[DOH_MAX_PROVIDERS];
    int n = 0, working = 0, i;

    for (i = 0; i < provider_num; i++) {
        if (!providers[i].host_is_ip)
            continue;                /* needs DNS, tested implicitly later */
        threads[n] = CreateThread(NULL, 0, doh_test_thread, &providers[i], 0, NULL);
        if (threads[n])
            n++;
    }
    if (n)
        WaitForMultipleObjects((DWORD)n, threads, TRUE, DOH_TEST_TIMEOUT_MS);
    for (i = 0; i < n; i++)
        CloseHandle(threads[i]);

    /* Working servers first, keep the user's order otherwise. */
    for (i = 0; i < provider_num; i++)
        if (providers[i].ok)
            sorted[working++] = providers[i];
    n = working;
    for (i = 0; i < provider_num; i++)
        if (!providers[i].ok)
            sorted[n++] = providers[i];
    memcpy(providers, sorted, sizeof(doh_provider_t) * (size_t)provider_num);

    for (i = 0; i < provider_num; i++) {
        if (providers[i].ok)
            printf("  %s: OK, %ld ms\n", providers[i].url, providers[i].test_ms);
        else if (!providers[i].host_is_ip)
            printf("  %s: host name, resolved through the servers above\n", providers[i].url);
        else
            printf("  %s: not reachable\n", providers[i].url);
    }
    return working;
}

int doh_start(const char *list, int verbose, HANDLE *handle) {
    DWORD opt;
    int i, has_ip = 0;

    doh_verbose = verbose;
    *handle = NULL;

    if (!parse_providers(list))
        return -1;

    /* A host name URL alone could never be resolved. Bootstrap it. */
    for (i = 0; i < provider_num; i++)
        has_ip |= providers[i].host_is_ip;
    if (!has_ip) {
        add_provider_url(builtin_providers[0].urls[0]);
        add_provider_url(builtin_providers[0].urls[1]);
    }

    session = WinHttpOpen(L"GoodbyeDPI-DoH", WINHTTP_ACCESS_TYPE_NO_PROXY,
                          WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) {
        printf("DoH: WinHTTP is not available (error %lu)\n", GetLastError());
        return -1;
    }
    opt = WINHTTP_PROTOCOL_FLAG_HTTP2;    /* silently ignored before Windows 10 1607 */
    WinHttpSetOption(session, WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL, &opt, sizeof(opt));
    opt = WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_REDIRECTS | WINHTTP_DISABLE_AUTHENTICATION;
    WinHttpSetOption(session, WINHTTP_OPTION_DISABLE_FEATURE, &opt, sizeof(opt));
    WinHttpSetTimeouts(session, 2000, 2500, 2500, 3500);

    for (i = 0; i < provider_num; i++) {
        providers[i].connect = WinHttpConnect(session, providers[i].whost, providers[i].port, 0);
        if (!providers[i].connect) {
            printf("DoH: can't use %s (error %lu)\n", providers[i].url, GetLastError());
            return -1;
        }
    }

    load_local_suffixes();

    printf("\nChecking DNS-over-HTTPS servers:\n");
    if (!self_test()) {
        printf("WARNING: no DNS-over-HTTPS server is reachable, "
               "DNS queries are left untouched.\n");
        return 0;
    }
    preferred = 0;

    printf("Local names kept on the original DNS server: single label, ");
    for (i = 0; i < local_suffix_num; i++)
        printf("%s%s", local_suffixes[i], i + 1 < local_suffix_num ? ", " : "\n");

    divert = WinDivertOpen(DOH_FILTER, WINDIVERT_LAYER_NETWORK, 0, 0);
    if (divert == INVALID_HANDLE_VALUE) {
        printf("DoH: can't open WinDivert filter (error %lu)\n", GetLastError());
        divert = NULL;
        return -1;
    }
    WinDivertSetParam(divert, WINDIVERT_PARAM_QUEUE_LENGTH, 4096);

    if (!CreateThread(NULL, 0, doh_recv_thread, NULL, 0, NULL)) {
        printf("DoH: can't start the resolver thread\n");
        WinDivertClose(divert);
        divert = NULL;
        return -1;
    }

    *handle = divert;
    return 1;
}
