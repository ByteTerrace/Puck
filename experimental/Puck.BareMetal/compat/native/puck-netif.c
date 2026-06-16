/* lwIP glue: the netif driver, the NO_SYS platform hooks, and a kernel-side DHCP/DNS/TCP bring-up
 * test. The low_level_* functions bridge lwIP to our virtio-net driver (PuckNetTx / PuckNetRxPoll in
 * puck-efi.c). Part of Puck.BareMetal. */
#include "lwip/opt.h"
#include "lwip/def.h"
#include "lwip/mem.h"
#include "lwip/pbuf.h"
#include "lwip/etharp.h"
#include "lwip/netif.h"
#include "lwip/init.h"
#include "lwip/timeouts.h"
#include "lwip/dhcp.h"
#include "lwip/dns.h"
#include "lwip/tcp.h"
#include "lwip/ip4_addr.h"
#include "netif/ethernet.h"
#include "puck-tls.h"

/* --- bridges into puck-efi.c (virtio-net + serial + clock + RNG) --- */
extern int PuckVirtioNetInit(void);
extern void PuckNetMsixProve(void); /* puck-efi.c: MSI-X interrupt proof (then tears down) */
#define PUCK_PROVE_MSIX 0 /* 1 = re-run the live-NIC MSI-X regression proof during net bring-up */
extern void PuckNetTx(const unsigned char *frame, unsigned int len);
extern unsigned int PuckNetRxPoll(unsigned char *out, unsigned int maxLen);
extern unsigned char g_netMac[6];
extern void PuckSerialWriteByte(int b);
extern unsigned int PuckSysNowMs(void);
extern unsigned long long PuckRdRand64(void);

static void NetPuts(const char *s) { while (*s) PuckSerialWriteByte((unsigned char)*s++); }

/* --- NO_SYS platform hooks required by lwIP (declared in our arch/cc.h + lwip/sys.h) --- */
u32_t sys_now(void) { return PuckSysNowMs(); }
unsigned int PuckLwipRand(void) { return (unsigned int)PuckRdRand64(); }
void PuckLwipDiag(const char *msg) { NetPuts("[lwip] "); NetPuts(msg); NetPuts("\r\n"); }
void PuckLwipAssertFail(const char *msg)
{
    NetPuts("[lwip] ASSERT: ");
    NetPuts(msg);
    NetPuts("\r\n");
    for (;;) { }
}

/* --- netif driver: low_level_* bridge lwIP to virtio-net --- */
static struct netif g_netif;

static err_t low_level_output(struct netif *netif, struct pbuf *p)
{
    static unsigned char txbuf[1600];
    struct pbuf *q;
    unsigned int off = 0;
    LWIP_UNUSED_ARG(netif);

    for (q = p; q != NULL; q = q->next)
    {
        if (off + q->len > sizeof(txbuf)) break;
        MEMCPY(txbuf + off, q->payload, q->len);
        off += q->len;
    }
    PuckNetTx(txbuf, off);
    LINK_STATS_INC(link.xmit);
    return ERR_OK;
}

static void low_level_init(struct netif *netif)
{
    int i;
    netif->hwaddr_len = ETHARP_HWADDR_LEN;
    for (i = 0; i < 6; i++) netif->hwaddr[i] = g_netMac[i];
    netif->mtu = 1500;
    netif->flags = NETIF_FLAG_BROADCAST | NETIF_FLAG_ETHARP | NETIF_FLAG_ETHERNET | NETIF_FLAG_LINK_UP;
}

static err_t ethernetif_init(struct netif *netif)
{
    netif->name[0] = 'e';
    netif->name[1] = 'n';
#if LWIP_IPV4
    netif->output = etharp_output;
#endif
    netif->linkoutput = low_level_output;
    low_level_init(netif);
    return ERR_OK;
}

/* Drain any received frames into lwIP. Called from the poll loop. */
static void PuckNetRxDrain(void)
{
    static unsigned char rxbuf[1600];
    unsigned int len;

    while ((len = PuckNetRxPoll(rxbuf, sizeof(rxbuf))) != 0)
    {
        struct pbuf *p = pbuf_alloc(PBUF_RAW, (u16_t)len, PBUF_POOL);
        if (p != NULL)
        {
            pbuf_take(p, rxbuf, (u16_t)len);
            if (g_netif.input(p, &g_netif) != ERR_OK)
                pbuf_free(p);
            LINK_STATS_INC(link.recv);
        }
    }
}

/* Pump the stack: feed RX, run timers. Call frequently. */
static void PuckNetPoll(void)
{
    PuckNetRxDrain();
    sys_check_timeouts();
}

/* --- shared bring-up: virtio + lwIP + DHCP (run once), then DNS resolve --- */
static volatile int g_dnsDone;
static ip_addr_t g_dnsResult;
static int g_netReady;

static void DnsCb(const char *name, const ip_addr_t *ipaddr, void *arg)
{
    LWIP_UNUSED_ARG(name); LWIP_UNUSED_ARG(arg);
    if (ipaddr) g_dnsResult = *ipaddr;
    g_dnsDone = ipaddr ? 1 : -1;
}

static int PuckNetEnsureUp(void)
{
    u32_t deadline;
    if (g_netReady) return 0;

    if (PuckVirtioNetInit() != 0) return -1;
#if PUCK_PROVE_MSIX
    /* Program MSI-X on the live NIC, fire a TX-completion interrupt through the LAPIC, then tear down
     * to the legacy layout before lwIP. Off by default: it cycles the NIC, so it should not run every
     * boot. Flip PUCK_PROVE_MSIX to re-run it. */
    PuckNetMsixProve();
#endif

    lwip_init();
    netif_add(&g_netif, IP4_ADDR_ANY4, IP4_ADDR_ANY4, IP4_ADDR_ANY4, NULL, ethernetif_init, ethernet_input);
    netif_set_default(&g_netif);
    netif_set_up(&g_netif);
    dhcp_start(&g_netif);

    NetPuts("[net] DHCP discover...\r\n");
    deadline = sys_now() + 8000;
    while (sys_now() < deadline && !dhcp_supplied_address(&g_netif)) PuckNetPoll();
    if (!dhcp_supplied_address(&g_netif)) { NetPuts("[net] DHCP timed out\r\n"); return -1; }
    NetPuts("[net] DHCP bound, IP ");
    NetPuts(ip4addr_ntoa(netif_ip4_addr(&g_netif)));
    NetPuts(" gw ");
    NetPuts(ip4addr_ntoa(netif_ip4_gw(&g_netif)));
    NetPuts(" dns ");
    NetPuts(ipaddr_ntoa(dns_getserver(0)));
    NetPuts("\r\n");

    g_netReady = 1;
    return 0;
}

static int PuckNetResolve(const char *host, ip_addr_t *out)
{
    u32_t deadline;
    g_dnsDone = 0;
    {
        err_t e = dns_gethostbyname(host, &g_dnsResult, DnsCb, NULL);
        if (e == ERR_OK) g_dnsDone = 1;                  /* already cached */
        else if (e != ERR_INPROGRESS) return -1;
    }
    deadline = sys_now() + 8000;
    while (sys_now() < deadline && g_dnsDone == 0) PuckNetPoll();
    if (g_dnsDone != 1) return -1;
    *out = g_dnsResult;
    NetPuts("[net] ");
    NetPuts(host);
    NetPuts(" -> ");
    NetPuts(ipaddr_ntoa(out));
    NetPuts("\r\n");
    return 0;
}

/* --- kernel bring-up test (plaintext): DHCP -> DNS -> TCP HTTP GET over the real (NAT'd) wire --- */
static volatile int g_httpDone;

static err_t HttpRecvCb(void *arg, struct tcp_pcb *pcb, struct pbuf *p, err_t err)
{
    LWIP_UNUSED_ARG(arg);
    if (err != ERR_OK) { g_httpDone = -1; return err; }
    if (p == NULL) { tcp_close(pcb); g_httpDone = 1; return ERR_OK; } /* peer closed */
    {
        struct pbuf *q;
        for (q = p; q != NULL; q = q->next)
        {
            unsigned int i;
            for (i = 0; i < q->len; i++) PuckSerialWriteByte(((unsigned char *)q->payload)[i]);
        }
    }
    tcp_recved(pcb, p->tot_len);
    pbuf_free(p);
    return ERR_OK;
}

static err_t HttpConnectedCb(void *arg, struct tcp_pcb *pcb, err_t err)
{
    static const char req[] = "GET / HTTP/1.0\r\nHost: example.com\r\nConnection: close\r\n\r\n";
    LWIP_UNUSED_ARG(arg);
    if (err != ERR_OK) { g_httpDone = -1; return err; }
    NetPuts("[net] TCP connected; sending HTTP GET\r\n");
    tcp_recv(pcb, HttpRecvCb);
    tcp_write(pcb, req, (u16_t)(sizeof(req) - 1), TCP_WRITE_FLAG_COPY);
    tcp_output(pcb);
    return ERR_OK;
}

void PuckNetLwipTest(void)
{
    u32_t deadline;

    if (PuckNetEnsureUp() != 0) return;
    if (PuckNetResolve("example.com", &g_dnsResult) != 0) { NetPuts("[net] DNS failed\r\n"); return; }

    {
        struct tcp_pcb *pcb = tcp_new();
        g_httpDone = 0;
        tcp_connect(pcb, &g_dnsResult, 80, HttpConnectedCb);
    }
    deadline = sys_now() + 12000;
    while (sys_now() < deadline && g_httpDone == 0) PuckNetPoll();
    NetPuts(g_httpDone == 1 ? "\r\n[net] HTTP done -- THE STACK SINGS!\r\n" : "\r\n[net] HTTP failed\r\n");
}

/* ============================ HTTPS: TLS over a lwIP TCP connection ============================ */
/* A PuckTlsTransport backed by one non-blocking lwIP TCP pcb. The TLS engine (mbedTLS today) is
 * given send/recv/poll; received segments are queued as a pbuf chain and consumed on demand, so the
 * TCP window naturally backpressures while the record layer drains. */
typedef struct PuckTcpConn
{
    struct tcp_pcb *pcb;
    struct pbuf *rxq;       /* unconsumed received data (pbuf chain) */
    unsigned int rxOff;     /* bytes already consumed from rxq's head pbuf */
    volatile int connected; /* 0 pending, 1 up, -1 failed */
    volatile int closed;    /* peer sent FIN */
    volatile int err;       /* fatal pcb error (pcb already freed by lwIP) */
    u32_t deadline;         /* abort recv() once sys_now() passes this */
} PuckTcpConn;

static err_t TlsConnectedCb(void *arg, struct tcp_pcb *pcb, err_t err)
{
    PuckTcpConn *c = (PuckTcpConn *)arg;
    LWIP_UNUSED_ARG(pcb);
    c->connected = (err == ERR_OK) ? 1 : -1;
    return ERR_OK;
}

static err_t TlsRecvCb(void *arg, struct tcp_pcb *pcb, struct pbuf *p, err_t err)
{
    PuckTcpConn *c = (PuckTcpConn *)arg;
    LWIP_UNUSED_ARG(pcb);
    if (err != ERR_OK) { if (p) pbuf_free(p); c->err = 1; return ERR_OK; }
    if (p == NULL) { c->closed = 1; return ERR_OK; } /* peer FIN */
    /* Queue; window is reopened via tcp_recved only as TlsTransportRecv consumes bytes. */
    if (c->rxq == NULL) c->rxq = p; else pbuf_cat(c->rxq, p);
    return ERR_OK;
}

static void TlsErrCb(void *arg, err_t err)
{
    PuckTcpConn *c = (PuckTcpConn *)arg;
    LWIP_UNUSED_ARG(err);
    c->pcb = NULL;   /* lwIP has already freed the pcb */
    c->err = 1;
}

static int TlsTransportSend(void *ctx, const unsigned char *buf, unsigned int len)
{
    PuckTcpConn *c = (PuckTcpConn *)ctx;
    u16_t avail;
    err_t e;
    if (c->err || c->pcb == NULL) return -1;
    avail = tcp_sndbuf(c->pcb);
    if (avail == 0) return 0;                 /* would block -> WANT_WRITE */
    if (len > avail) len = avail;
    e = tcp_write(c->pcb, buf, (u16_t)len, TCP_WRITE_FLAG_COPY);
    if (e == ERR_MEM) return 0;               /* retry later */
    if (e != ERR_OK) return -1;
    tcp_output(c->pcb);
    return (int)len;
}

static int TlsTransportRecv(void *ctx, unsigned char *buf, unsigned int len)
{
    PuckTcpConn *c = (PuckTcpConn *)ctx;
    unsigned int copied = 0;
    if (c->rxq == NULL)
    {
        if (c->err) return -1;
        if (c->closed) return -1;             /* EOF */
        if (sys_now() > c->deadline) return -1;
        return 0;                             /* no data yet -> WANT_READ */
    }
    while (len > 0 && c->rxq != NULL)
    {
        struct pbuf *head = c->rxq;
        unsigned int avail = head->len - c->rxOff;
        unsigned int take = (avail < len) ? avail : len;
        MEMCPY(buf + copied, (unsigned char *)head->payload + c->rxOff, take);
        copied += take; len -= take; c->rxOff += take;
        if (c->rxOff == head->len)            /* head fully consumed: detach + free just it */
        {
            c->rxq = head->next;
            if (c->rxq) pbuf_ref(c->rxq);
            head->next = NULL;
            pbuf_free(head);
            c->rxOff = 0;
        }
    }
    if (copied && c->pcb) tcp_recved(c->pcb, (u16_t)copied); /* reopen the receive window */
    return (int)copied;
}

static void TlsTransportPoll(void *ctx) { LWIP_UNUSED_ARG(ctx); PuckNetPoll(); }

/* HTTPS GET over TLS. Validates the server cert against the embedded CA roots (SNI = host). */
void PuckNetTlsTest(const char *host, const char *path)
{
    PuckTcpConn conn;
    PuckTlsTransport transport;
    PuckTls *tls;
    ip_addr_t ip;
    u32_t deadline;
    int rc;

    if (PuckNetEnsureUp() != 0) return;
    if (PuckNetResolve(host, &ip) != 0) { NetPuts("[tls] DNS failed\r\n"); return; }

    /* one TCP connection */
    conn.pcb = tcp_new();
    conn.rxq = NULL; conn.rxOff = 0;
    conn.connected = 0; conn.closed = 0; conn.err = 0;
    conn.deadline = sys_now() + 15000;
    if (conn.pcb == NULL) { NetPuts("[tls] tcp_new failed\r\n"); return; }
    tcp_arg(conn.pcb, &conn);
    tcp_recv(conn.pcb, TlsRecvCb);
    tcp_err(conn.pcb, TlsErrCb);
    tcp_connect(conn.pcb, &ip, 443, TlsConnectedCb);

    NetPuts("[tls] TCP connecting :443...\r\n");
    deadline = sys_now() + 12000;
    while (sys_now() < deadline && conn.connected == 0) PuckNetPoll();
    if (conn.connected != 1) { NetPuts("[tls] TCP connect failed\r\n"); return; }

    /* TLS session over that connection */
    transport.send = TlsTransportSend;
    transport.recv = TlsTransportRecv;
    transport.poll = TlsTransportPoll;
    transport.ctx = &conn;

    tls = PuckTlsNew(host, &transport);
    if (tls == NULL) { NetPuts("[tls] PuckTlsNew failed (CA parse / setup)\r\n"); return; }

    NetPuts("[tls] handshaking (cert chain + hostname verify)...\r\n");
    conn.deadline = sys_now() + 15000;
    rc = PuckTlsHandshake(tls);
    if (rc != 0)
    {
        NetPuts("[tls] handshake FAILED\r\n");
        PuckTlsFree(tls);
        return;
    }
    NetPuts("[tls] handshake OK -- secure channel established\r\n");

    /* HTTP/1.0 GET inside the TLS tunnel */
    {
        char req[256];
        const char *h = host; char *w = req; int n;
        const char *p1 = "GET "; const char *p2 = " HTTP/1.0\r\nHost: ";
        const char *p3 = "\r\nConnection: close\r\n\r\n";
        while (*p1) *w++ = *p1++;
        { const char *pp = path; while (*pp) *w++ = *pp++; }
        while (*p2) *w++ = *p2++;
        while (*h) *w++ = *h++;
        while (*p3) *w++ = *p3++;
        n = (int)(w - req);
        conn.deadline = sys_now() + 15000;
        PuckTlsWrite(tls, req, (unsigned int)n);
    }

    /* read the response and echo it to serial until the peer closes */
    NetPuts("[tls] response:\r\n");
    for (;;)
    {
        unsigned char buf[1024];
        int got;
        conn.deadline = sys_now() + 15000;
        got = PuckTlsRead(tls, buf, sizeof(buf));
        if (got > 0) { int i; for (i = 0; i < got; i++) PuckSerialWriteByte(buf[i]); continue; }
        break; /* 0 = clean close, <0 = error/timeout */
    }

    PuckTlsFree(tls);
    if (conn.pcb) tcp_close(conn.pcb);
    NetPuts("\r\n[tls] HTTPS done -- TLS SINGS!\r\n");
}
