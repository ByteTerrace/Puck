/* A trivial shared library: one exported function. Built on Alpine as a musl .so:
 *   gcc -shared -fPIC -Wl,-soname,libfoo.so.1 -O2 libfoo.c -o /lib/libfoo.so.1
 * Used (with foouser.c) to prove the loader serves a NEEDED .so to ld-musl via file-backed mmap,
 * before RADV's full closure. Part of Puck.BareMetal. */
int foo_value(void) { return 0x37; }
