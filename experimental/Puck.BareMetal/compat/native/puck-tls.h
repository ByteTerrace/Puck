/* Puck.BareMetal — engine-agnostic TLS client interface.
 *
 * The rest of the system (the HTTPS client, the Azure-blob fetcher) talks ONLY to this interface,
 * never to a TLS library directly. mbedTLS implements it today (puck-tls-mbedtls.c); a future
 * rustls implementation (puck-tls-rustls.c) can replace it by providing the same six functions
 * and swapping which file the build compiles. No mbedtls/rustls type ever leaks past here.
 *
 * The byte transport (TCP) is injected via PuckTlsTransport, so the TLS engine is decoupled from
 * both lwIP (below) and the HTTP consumer (above). Part of Puck.BareMetal. */
#ifndef PUCK_TLS_H
#define PUCK_TLS_H

/* The raw byte transport the TLS record layer reads/writes through (e.g. a lwIP TCP connection).
 * Non-blocking, poll-driven semantics:
 *   send: returns bytes queued (>0), or 0 if it would block (send buffer full) -> caller polls+retries.
 *   recv: returns bytes read (>0), 0 if no data is available yet -> caller polls+retries, or <0 if the
 *         connection has closed or errored (EOF).
 * The TLS engine pumps the stack by calling poll() between retries while it waits on I/O. */
typedef struct PuckTlsTransport
{
    int (*send)(void *ctx, const unsigned char *buf, unsigned int len);
    int (*recv)(void *ctx, unsigned char *buf, unsigned int len);
    void (*poll)(void *ctx); /* pump the stack (e.g. PuckNetPoll) while blocking on I/O */
    void *ctx;
} PuckTlsTransport;

/* Opaque session handle; its layout belongs entirely to the active engine implementation. */
typedef struct PuckTls PuckTls;

/* Create a TLS client session that will validate `hostname` (SNI + certificate host check) against
 * the built-in CA roots and perform I/O over `transport`. Returns NULL on allocation/setup failure.
 * `transport` must outlive the session. */
PuckTls *PuckTlsNew(const char *hostname, const PuckTlsTransport *transport);

/* Drive the TLS handshake to completion (incl. certificate-chain + hostname verification).
 * Returns 0 on success, a negative engine-defined code on failure. */
int PuckTlsHandshake(PuckTls *tls);

/* Application-data I/O over the established session. Return bytes moved, 0 on clean close, <0 error. */
int PuckTlsWrite(PuckTls *tls, const void *buf, unsigned int len);
int PuckTlsRead(PuckTls *tls, void *buf, unsigned int len);

/* Close (best-effort TLS close-notify) and release the session. */
void PuckTlsFree(PuckTls *tls);

#endif /* PUCK_TLS_H */
