#ifndef _DOH_H
#define _DOH_H
#include <windows.h>

#define DOH_DEFAULT_PROVIDERS "cloudflare,google"

/*
 * Start the built-in DNS-over-HTTPS resolver.
 *
 * providers: comma separated list of built-in provider names
 *            (cloudflare, google, quad9, adguard) and/or https:// URLs.
 *            NULL means DOH_DEFAULT_PROVIDERS.
 * verbose:   print one line per resolved query.
 * handle:    receives the WinDivert handle (so the caller can close it).
 *
 * Returns  1 when the resolver is running,
 *          0 when no DoH server is reachable (plain DNS stays untouched),
 *         -1 on a fatal error (bad arguments, WinDivert failure).
 */
int doh_start(const char *providers, int verbose, HANDLE *handle);

void doh_print_help(void);

#endif
