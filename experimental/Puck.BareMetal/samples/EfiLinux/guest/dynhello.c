/* A trivial DYNAMIC musl guest (no -static): a position-independent executable with
 * PT_INTERP=/lib/ld-musl-x86_64.so.1 and DT_NEEDED libc.musl-x86_64.so.1. Because musl's libc IS
 * ld-musl, a libc-only binary makes the interpreter load nothing extra -- so this isolates and
 * proves the loader's dynamic handoff (PT_INTERP -> load ld-musl -> AT_BASE/AT_PHDR/AT_ENTRY auxv
 * -> ld-musl links us and jumps to main) before RADV's full .so closure. Built on Alpine so its
 * ld-musl matches the staged one; embedded via embed-dyn.ps1. */
#include <unistd.h>

int main(void)
{
    write(1, "dynamic musl hello via ld-musl!\n", 32);
    return 0;
}
