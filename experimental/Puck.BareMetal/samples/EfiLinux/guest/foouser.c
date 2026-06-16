/* A dynamic musl guest that NEEDs libfoo.so.1 and calls into it -- so ld-musl must open/mmap an
 * EXTERNAL .so (not just libc) from the synthetic VFS. Proves the loader's file-backed mmap + .so
 * serving before RADV. Built on Alpine:
 *   gcc -O2 foouser.c -o foouser /lib/libfoo.so.1
 * Part of Puck.BareMetal. */
#include <unistd.h>

extern int foo_value(void);

int main(void)
{
    int x = foo_value();
    char ok = (x == 0x37) ? 'Y' : 'N';
    write(1, "libfoo via ld-musl: ", 20);
    write(1, &ok, 1);
    write(1, "\n", 1);
    return 0;
}
