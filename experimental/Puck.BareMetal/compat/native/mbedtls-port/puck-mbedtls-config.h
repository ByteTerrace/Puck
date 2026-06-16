/* mbedTLS config for Puck.BareMetal: a minimal freestanding TLS 1.2 *client* (no FS, no net, no
 * threads, no OS entropy/time). Crypto + memory + entropy + time are supplied by our port
 * (puck-tls-mbedtls.c). Configured, never edited in the vendored tree.
 *
 * NOTE: deliberately NOT named mbedtls_config.h. build_info.h does `#include MBEDTLS_CONFIG_FILE`,
 * and a quoted include is searched relative to build_info.h's own directory first -- where the
 * vendored default mbedtls_config.h lives. A unique name guarantees ours wins via the -I path. */
#ifndef MBEDTLS_CONFIG_H
#define MBEDTLS_CONFIG_H

/* --- platform: no libc/OS; we provide everything --- */
#define MBEDTLS_PLATFORM_C
#define MBEDTLS_PLATFORM_NO_STD_FUNCTIONS
#define MBEDTLS_PLATFORM_SNPRINTF_ALT
#define MBEDTLS_PLATFORM_PRINTF_ALT
#define MBEDTLS_MEMORY_BUFFER_ALLOC_C        /* mbedTLS's own allocator over a static buffer */
#define MBEDTLS_PLATFORM_MEMORY
#define MBEDTLS_NO_PLATFORM_ENTROPY          /* no /dev/urandom or getrandom */
#define MBEDTLS_ENTROPY_HARDWARE_ALT         /* -> our mbedtls_hardware_poll (RDRAND) */
#define MBEDTLS_HAVE_TIME
#define MBEDTLS_HAVE_TIME_DATE
#define MBEDTLS_PLATFORM_TIME_ALT            /* -> our CLOCK_REALTIME (cert validity "now") */
#define MBEDTLS_PLATFORM_GMTIME_R_ALT        /* -> our epoch->UTC tm (no CRT gmtime_s) */
#define MBEDTLS_PLATFORM_MS_TIME_ALT         /* -> our uptime ms (no Win32 GetSystemTimeAsFileTime) */
#define MBEDTLS_TEST_SW_INET_PTON            /* mbedTLS's built-in inet_pton; skip winsock ws2_32 */

/* --- RNG --- */
#define MBEDTLS_ENTROPY_C
#define MBEDTLS_CTR_DRBG_C

/* --- TLS 1.2 client only --- */
#define MBEDTLS_SSL_TLS_C
#define MBEDTLS_SSL_CLI_C
#define MBEDTLS_SSL_PROTO_TLS1_2
#define MBEDTLS_SSL_SERVER_NAME_INDICATION
#define MBEDTLS_SSL_KEEP_PEER_CERTIFICATE

/* --- key exchange (what Azure/Cloudflare offer) --- */
#define MBEDTLS_KEY_EXCHANGE_ECDHE_RSA_ENABLED
#define MBEDTLS_KEY_EXCHANGE_ECDHE_ECDSA_ENABLED

/* --- PK / asymmetric --- */
#define MBEDTLS_PK_C
#define MBEDTLS_PK_PARSE_C
#define MBEDTLS_RSA_C
#define MBEDTLS_PKCS1_V15
#define MBEDTLS_PKCS1_V21
#define MBEDTLS_ECP_C
#define MBEDTLS_ECDH_C
#define MBEDTLS_ECDSA_C
#define MBEDTLS_BIGNUM_C
#define MBEDTLS_ECP_DP_SECP256R1_ENABLED
#define MBEDTLS_ECP_DP_SECP384R1_ENABLED
#define MBEDTLS_ECP_DP_CURVE25519_ENABLED
#define MBEDTLS_ECP_NIST_OPTIM

/* --- symmetric + AEAD + hashes --- */
#define MBEDTLS_AES_C
#define MBEDTLS_GCM_C
#define MBEDTLS_CIPHER_C
#define MBEDTLS_MD_C
#define MBEDTLS_SHA224_C
#define MBEDTLS_SHA256_C
#define MBEDTLS_SHA384_C
#define MBEDTLS_SHA512_C
#define MBEDTLS_SHA1_C   /* legacy cert signatures */

/* --- X.509 certificate chain validation --- */
#define MBEDTLS_X509_USE_C
#define MBEDTLS_X509_CRT_PARSE_C
#define MBEDTLS_ASN1_PARSE_C
#define MBEDTLS_ASN1_WRITE_C
#define MBEDTLS_OID_C
#define MBEDTLS_PEM_PARSE_C
#define MBEDTLS_BASE64_C
#define MBEDTLS_X509_REMOVE_INFO  /* drop the human-readable cert-info strings (less snprintf) */

/* --- error strings off (less code, less snprintf) --- */

/* NB: do NOT #include "mbedtls/check_config.h" here. Since mbedTLS 3.0 build_info.h includes it
 * automatically once the config is finalized; including it manually trips its own guard warning. */
#endif /* MBEDTLS_CONFIG_H */
