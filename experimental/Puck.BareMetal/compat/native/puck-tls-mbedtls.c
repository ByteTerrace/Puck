/* mbedTLS implementation of the engine-agnostic PuckTls interface (puck-tls.h). This is the
 * ONLY file that includes mbedTLS; everything above talks to PuckTls. A rustls implementation can
 * replace this file wholesale. Also supplies the freestanding platform hooks mbedTLS needs:
 * hardware entropy (RDRAND), wall-clock (CLOCK_REALTIME), a static heap, and a minimal snprintf.
 * Part of Puck.BareMetal. */
#include "mbedtls/ssl.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/entropy.h"
#include "mbedtls/x509_crt.h"
#include "mbedtls/platform.h"
#include "mbedtls/memory_buffer_alloc.h"
#include "mbedtls/platform_util.h"
#include "mbedtls/platform_time.h"
#include "puck-tls.h"
#include <stdarg.h> /* va_list/va_start/va_arg/va_end are MSVC intrinsics, no CRT dependency */
#include <time.h>   /* struct tm (declarations only; we link no CRT) */

extern unsigned long long PuckRdRand64(void);
extern long long PuckRealtimeEpoch(void);
extern const char g_puckCaBundle[]; /* puck-ca-bundle.c: PEM trust roots */
extern const unsigned int g_puckCaBundleLen;

/* ---- freestanding platform hooks ---- */
int mbedtls_hardware_poll(void *data, unsigned char *output, size_t len, size_t *olen)
{
    size_t i = 0;
    (void)data;
    while (i < len)
    {
        unsigned long long r = PuckRdRand64();
        int j;
        for (j = 0; j < 8 && i < len; j++) output[i++] = (unsigned char)(r >> (j * 8));
    }
    *olen = len;
    return 0;
}

static mbedtls_time_t PuckMbedTime(mbedtls_time_t *t)
{
    mbedtls_time_t now = (mbedtls_time_t)PuckRealtimeEpoch();
    if (t) *t = now;
    return now;
}

/* MBEDTLS_PLATFORM_GMTIME_R_ALT: Unix epoch seconds -> broken-down UTC. mbedTLS only reads the
 * date/time fields for X.509 validity comparison. Civil-from-days is Howard Hinnant's algorithm. */
extern unsigned int PuckSysNowMs(void);
struct tm *mbedtls_platform_gmtime_r(const mbedtls_time_t *tt, struct tm *tm_buf)
{
    long long t = (long long)*tt;
    long long days = t / 86400, rem = t % 86400, z, era, y;
    unsigned long long doe, yoe, doy, mp, d, m;
    if (rem < 0) { rem += 86400; days -= 1; }
    tm_buf->tm_hour = (int)(rem / 3600);
    tm_buf->tm_min  = (int)((rem % 3600) / 60);
    tm_buf->tm_sec  = (int)(rem % 60);
    tm_buf->tm_wday = (int)(((days % 7) + 4 + 7) % 7); /* 1970-01-01 was a Thursday (4) */
    z = days + 719468;
    era = (z >= 0 ? z : z - 146096) / 146097;
    doe = (unsigned long long)(z - era * 146097);
    yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    y = (long long)yoe + era * 400;
    doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    mp = (5 * doy + 2) / 153;
    d = doy - (153 * mp + 2) / 5 + 1;
    m = mp < 10 ? mp + 3 : mp - 9;
    y += (m <= 2);
    tm_buf->tm_year = (int)(y - 1900);
    tm_buf->tm_mon  = (int)(m - 1);
    tm_buf->tm_mday = (int)d;
    tm_buf->tm_yday = 0;
    tm_buf->tm_isdst = 0;
    return tm_buf;
}

/* MBEDTLS_PLATFORM_MS_TIME_ALT: monotonic milliseconds (used only for relative timing). */
mbedtls_ms_time_t mbedtls_ms_time(void)
{
    return (mbedtls_ms_time_t)PuckSysNowMs();
}

static int PuckMbedPrintf(const char *fmt, ...) { (void)fmt; return 0; } /* no console for mbedTLS */

/* Minimal snprintf: enough for the format specs mbedTLS uses (s,d,i,u,x,X,c,p with l/z/width/0).
 * Output correctness only affects (disabled) human-readable strings, so this need only be safe. */
static void MbedPutStr(char **p, char *end, const char *s)
{
    while (*s) { if (*p < end) **p = *s; (*p)++; s++; }
}
static void MbedPutNum(char **p, char *end, unsigned long long v, int base, int upper, int width, int zero, int neg)
{
    char tmp[24]; int t = 0; const char *dig = upper ? "0123456789ABCDEF" : "0123456789abcdef";
    if (v == 0) tmp[t++] = '0';
    while (v) { tmp[t++] = dig[v % base]; v /= base; }
    { int total = t + (neg ? 1 : 0); int pad = width - total;
      if (neg && zero) { if (*p < end) **p = '-'; (*p)++; }
      while (pad-- > 0) { if (*p < end) **p = zero ? '0' : ' '; (*p)++; }
      if (neg && !zero) { if (*p < end) **p = '-'; (*p)++; } }
    while (t > 0) { if (*p < end) **p = tmp[--t]; (*p)++; }
}
static int PuckMbedVsnprintf(char *buf, size_t size, const char *fmt, va_list ap)
{
    char *p = buf, *end = buf + (size ? size - 1 : 0);
    for (; *fmt; fmt++)
    {
        if (*fmt != '%') { if (p < end) *p = *fmt; p++; continue; }
        fmt++;
        { int zero = 0, width = 0, lng = 0;
          while (*fmt == '0' || *fmt == '-' || *fmt == '+' || *fmt == ' ') { if (*fmt == '0') zero = 1; fmt++; }
          while (*fmt >= '0' && *fmt <= '9') { width = width * 10 + (*fmt - '0'); fmt++; }
          while (*fmt == 'l' || *fmt == 'z' || *fmt == 'h') { if (*fmt == 'l' || *fmt == 'z') lng = 1; fmt++; }
          switch (*fmt)
          {
            case 's': MbedPutStr(&p, end, va_arg(ap, const char *)); break;
            case 'c': if (p < end) *p = (char)va_arg(ap, int); p++; break;
            case 'd': case 'i': { long long v = lng ? va_arg(ap, long long) : va_arg(ap, int);
                                  int neg = v < 0; unsigned long long u = neg ? (unsigned long long)(-v) : (unsigned long long)v;
                                  MbedPutNum(&p, end, u, 10, 0, width, zero, neg); break; }
            case 'u': MbedPutNum(&p, end, lng ? va_arg(ap, unsigned long long) : va_arg(ap, unsigned int), 10, 0, width, zero, 0); break;
            case 'x': MbedPutNum(&p, end, lng ? va_arg(ap, unsigned long long) : va_arg(ap, unsigned int), 16, 0, width, zero, 0); break;
            case 'X': MbedPutNum(&p, end, lng ? va_arg(ap, unsigned long long) : va_arg(ap, unsigned int), 16, 1, width, zero, 0); break;
            case 'p': MbedPutStr(&p, end, "0x"); MbedPutNum(&p, end, (unsigned long long)va_arg(ap, void *), 16, 0, 0, 0, 0); break;
            case '%': if (p < end) *p = '%'; p++; break;
            default:  if (p < end) *p = '%'; p++; if (*fmt) { if (p < end) *p = *fmt; p++; } break;
          }
        }
    }
    if (size) *p = 0;
    return (int)(p - buf);
}
static int PuckMbedSnprintf(char *buf, size_t size, const char *fmt, ...)
{
    int r; va_list ap; va_start(ap, fmt);
    r = PuckMbedVsnprintf(buf, size, fmt, ap); va_end(ap);
    return r;
}

static unsigned char g_tlsHeap[320 * 1024];
static int g_tlsPlatformReady;

void PuckTlsPlatformInit(void)
{
    if (g_tlsPlatformReady) return;
    mbedtls_memory_buffer_alloc_init(g_tlsHeap, sizeof(g_tlsHeap)); /* wires mbedtls_calloc/free */
    mbedtls_platform_set_snprintf(PuckMbedSnprintf);
    mbedtls_platform_set_printf(PuckMbedPrintf);
    mbedtls_platform_set_time(PuckMbedTime);
    g_tlsPlatformReady = 1;
}

/* ---- the PuckTls session (mbedTLS-backed) ---- */
struct PuckTls
{
    mbedtls_ssl_context ssl;
    mbedtls_ssl_config conf;
    mbedtls_x509_crt ca;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_entropy_context entropy;
    const PuckTlsTransport *transport;
};

static int BioSend(void *ctx, const unsigned char *buf, size_t len)
{
    PuckTls *t = (PuckTls *)ctx;
    int n = t->transport->send(t->transport->ctx, buf, (unsigned int)len);
    if (n < 0) return MBEDTLS_ERR_SSL_INTERNAL_ERROR; /* net module disabled; generic fatal */
    if (n == 0) return MBEDTLS_ERR_SSL_WANT_WRITE;
    return n;
}
static int BioRecv(void *ctx, unsigned char *buf, size_t len)
{
    PuckTls *t = (PuckTls *)ctx;
    int n = t->transport->recv(t->transport->ctx, buf, (unsigned int)len);
    if (n < 0) return MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY;
    if (n == 0) return MBEDTLS_ERR_SSL_WANT_READ;
    return n;
}

PuckTls *PuckTlsNew(const char *hostname, const PuckTlsTransport *transport)
{
    PuckTls *t;
    PuckTlsPlatformInit();
    t = (PuckTls *)mbedtls_calloc(1, sizeof(*t));
    if (!t) return 0;
    t->transport = transport;
    mbedtls_ssl_init(&t->ssl);
    mbedtls_ssl_config_init(&t->conf);
    mbedtls_x509_crt_init(&t->ca);
    mbedtls_ctr_drbg_init(&t->drbg);
    mbedtls_entropy_init(&t->entropy);

    if (mbedtls_ctr_drbg_seed(&t->drbg, mbedtls_entropy_func, &t->entropy, 0, 0) != 0) goto fail;
    if (mbedtls_x509_crt_parse(&t->ca, (const unsigned char *)g_puckCaBundle, g_puckCaBundleLen) < 0) goto fail;
    if (mbedtls_ssl_config_defaults(&t->conf, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) != 0) goto fail;
    mbedtls_ssl_conf_authmode(&t->conf, MBEDTLS_SSL_VERIFY_REQUIRED);
    mbedtls_ssl_conf_ca_chain(&t->conf, &t->ca, 0);
    mbedtls_ssl_conf_rng(&t->conf, mbedtls_ctr_drbg_random, &t->drbg);
    if (mbedtls_ssl_setup(&t->ssl, &t->conf) != 0) goto fail;
    if (mbedtls_ssl_set_hostname(&t->ssl, hostname) != 0) goto fail;
    mbedtls_ssl_set_bio(&t->ssl, t, BioSend, BioRecv, 0);
    return t;
fail:
    PuckTlsFree(t);
    return 0;
}

int PuckTlsHandshake(PuckTls *t)
{
    int r;
    while ((r = mbedtls_ssl_handshake(&t->ssl)) != 0)
    {
        if (r == MBEDTLS_ERR_SSL_WANT_READ || r == MBEDTLS_ERR_SSL_WANT_WRITE) { t->transport->poll(t->transport->ctx); continue; }
        return r;
    }
    return 0;
}

int PuckTlsWrite(PuckTls *t, const void *buf, unsigned int len)
{
    int r;
    while ((r = mbedtls_ssl_write(&t->ssl, (const unsigned char *)buf, len)) <= 0)
    {
        if (r == MBEDTLS_ERR_SSL_WANT_READ || r == MBEDTLS_ERR_SSL_WANT_WRITE) { t->transport->poll(t->transport->ctx); continue; }
        return r;
    }
    return r;
}

int PuckTlsRead(PuckTls *t, void *buf, unsigned int len)
{
    int r;
    while ((r = mbedtls_ssl_read(&t->ssl, (unsigned char *)buf, len)) == MBEDTLS_ERR_SSL_WANT_READ
           || r == MBEDTLS_ERR_SSL_WANT_WRITE)
        t->transport->poll(t->transport->ctx);
    if (r == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return 0;
    return r;
}

void PuckTlsFree(PuckTls *t)
{
    if (!t) return;
    mbedtls_ssl_close_notify(&t->ssl);
    mbedtls_ssl_free(&t->ssl);
    mbedtls_ssl_config_free(&t->conf);
    mbedtls_x509_crt_free(&t->ca);
    mbedtls_ctr_drbg_free(&t->drbg);
    mbedtls_entropy_free(&t->entropy);
    mbedtls_free(t);
}
