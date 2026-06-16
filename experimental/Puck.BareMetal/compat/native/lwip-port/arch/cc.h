/* lwIP arch/cc.h for Puck.BareMetal. The hooks that touch our hardware: no console printf
 * (DIAG/ERROR/ASSERT route to our COM1 serial), LWIP_RAND uses RDRAND, and LWIP_PROVIDE_ERRNO is
 * forced so no host <errno.h>/CRT is pulled. */
#ifndef LWIP_ARCH_CC_H
#define LWIP_ARCH_CC_H

#ifdef _MSC_VER
#pragma warning (disable: 4127) /* conditional expression is constant */
#pragma warning (disable: 4996) /* 'strncpy' was declared deprecated */
#pragma warning (disable: 4103) /* structure packing changed by including file */
#pragma warning (disable: 4820) /* padding added after data member */
#pragma warning (disable: 4711) /* function selected for automatic inline expansion */
#endif

/* lwIP provides its own errno (LWIP_SOCKET=0, so it is barely used); avoids the CRT <errno.h>. */
#define LWIP_PROVIDE_ERRNO

#ifndef BYTE_ORDER
#define BYTE_ORDER LITTLE_ENDIAN /* x86-64 */
#endif

typedef int sys_prot_t;

#ifdef _MSC_VER
#define _INTPTR 2            /* for MSVC <stdint.h> */
#define LWIP_NO_INTTYPES_H 1 /* no <inttypes.h>; supply the format strings below */
#define X8_F  "02x"
#define U16_F "hu"
#define U32_F "lu"
#define S32_F "ld"
#define X32_F "lx"
#define S16_F "hd"
#define X16_F "hx"
#define SZT_F "lu"
#endif /* _MSC_VER */

/* Compiler hints for packing structures (MSVC #pragma pack via arch/bpstruct.h + arch/epstruct.h). */
#define PACK_STRUCT_USE_INCLUDES

/* Platform hooks -> our serial / RDRAND (see puck-netif.c). No printf available. */
extern unsigned int PuckLwipRand(void);
extern void PuckLwipDiag(const char *msg);       /* print, no halt */
extern void PuckLwipAssertFail(const char *msg); /* print + halt   */

#define LWIP_PLATFORM_DIAG(x)               /* lwIP DIAG is printf-style; no console here */
#define LWIP_PLATFORM_ASSERT(x) PuckLwipAssertFail(x)
#define LWIP_ERROR(message, expression, handler) do { if (!(expression)) { \
    PuckLwipDiag(message); handler; } } while (0)

#ifndef LWIP_RAND
#define LWIP_RAND() (PuckLwipRand())
#endif

#endif /* LWIP_ARCH_CC_H */
