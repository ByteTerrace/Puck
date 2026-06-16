/* Freestanding shims for the handful of MSVC/CRT symbols mbedTLS emits calls to but for which we
 * link no CRT. Deliberately header-free: including <stdlib.h>/<intrin.h> would mark _byteswap_* as
 * #pragma intrinsic and conflict with defining them here. Part of Puck.BareMetal. */

/* mbedTLS library/alignment.h maps MBEDTLS_BSWAP{16,32,64} to these under _MSC_VER. With /O1 the
 * compiler emits calls rather than inlining, so real definitions must exist to link. */
unsigned short _byteswap_ushort(unsigned short v)
{
    return (unsigned short)((v >> 8) | (v << 8));
}

unsigned long _byteswap_ulong(unsigned long v)
{
    return ((v & 0x000000FFul) << 24) | ((v & 0x0000FF00ul) << 8) |
           ((v & 0x00FF0000ul) >> 8)  | ((v & 0xFF000000ul) >> 24);
}

unsigned long long _byteswap_uint64(unsigned long long v)
{
    v = ((v & 0x00000000FFFFFFFFull) << 32) | ((v & 0xFFFFFFFF00000000ull) >> 32);
    v = ((v & 0x0000FFFF0000FFFFull) << 16) | ((v & 0xFFFF0000FFFF0000ull) >> 16);
    v = ((v & 0x00FF00FF00FF00FFull) << 8)  | ((v & 0xFF00FF00FF00FF00ull) >> 8);
    return v;
}

/* mbedTLS memory_buffer_alloc calls exit() only on detected heap corruption. We have no process to
 * exit; hang so the failure is observable rather than returning into corrupted state. */
void exit(int code)
{
    (void)code;
    for (;;) { }
}
