/* lwIP options for Puck.BareMetal: NO_SYS (single-threaded, polled, callback API), IPv4 only, with
 * DHCP + DNS + TCP/UDP. Drives the vendored stack on our virtio-net netif. */
#ifndef LWIPOPTS_H
#define LWIPOPTS_H

#define NO_SYS                          1   /* no OS threads; we poll + drive timers */
#define SYS_LIGHTWEIGHT_PROT            0
#define LWIP_NETCONN                    0   /* no sequential API */
#define LWIP_SOCKET                     0   /* we implement the socket syscalls ourselves */
#define LWIP_NETIF_API                  0

/* memory: lwIP's own heap + pools (no libc malloc) */
#define MEM_LIBC_MALLOC                 0
#define MEMP_MEM_MALLOC                 0
#define MEM_ALIGNMENT                   8
#define MEM_SIZE                        (512 * 1024)
#define MEMP_NUM_PBUF                   64
#define MEMP_NUM_UDP_PCB                8
#define MEMP_NUM_TCP_PCB                16
#define MEMP_NUM_TCP_PCB_LISTEN         4
#define MEMP_NUM_TCP_SEG                64
#define MEMP_NUM_REASSDATA              8
#define MEMP_NUM_ARP_QUEUE              16
#define PBUF_POOL_SIZE                  64
#define PBUF_POOL_BUFSIZE               1536

/* protocols */
#define LWIP_IPV4                       1
#define LWIP_IPV6                       0
#define LWIP_ARP                        1
#define LWIP_ETHERNET                   1
#define LWIP_ICMP                       1
#define LWIP_RAW                        1
#define LWIP_UDP                        1
#define LWIP_TCP                        1
#define LWIP_DHCP                       1
#define LWIP_DNS                        1
#define LWIP_DNS_SECURE                 0
#define DNS_TABLE_SIZE                  4
#define DNS_MAX_NAME_LENGTH             256

/* netif */
#define LWIP_SINGLE_NETIF               1
#define LWIP_NETIF_STATUS_CALLBACK      1
#define LWIP_NETIF_LINK_CALLBACK        0
#define LWIP_NETIF_HOSTNAME             0
#define LWIP_NETIF_TX_SINGLE_PBUF       1

/* checksums computed in software (no NIC offload) */
#define CHECKSUM_GEN_IP                 1
#define CHECKSUM_GEN_UDP                1
#define CHECKSUM_GEN_TCP                1
#define CHECKSUM_GEN_ICMP               1
#define CHECKSUM_CHECK_IP               1
#define CHECKSUM_CHECK_UDP              1
#define CHECKSUM_CHECK_TCP              1
#define CHECKSUM_CHECK_ICMP             1

/* TCP sizing */
#define TCP_MSS                         1460
#define TCP_WND                         (8 * TCP_MSS)
#define TCP_SND_BUF                     (8 * TCP_MSS)
#define TCP_SND_QUEUELEN                ((4 * (TCP_SND_BUF) + (TCP_MSS - 1)) / (TCP_MSS))
#define LWIP_TCP_KEEPALIVE              1

/* random + errno: hardware RNG, lwIP-provided errno (no host <errno.h>) */
#define LWIP_RAND()                     ((u32_t)PuckLwipRand())
#define LWIP_PROVIDE_ERRNO              1

/* trim: no stats/debug/assert-info (size + no printf dependency) */
#define LWIP_STATS                      0
#define LWIP_DEBUG                      0
#define LWIP_NOASSERT                   0

#endif /* LWIPOPTS_H */
