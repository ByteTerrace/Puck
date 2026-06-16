/* The guest program embedded (as base64) in GuestElf.cs and run by the loader at ring 3.
 *
 * Build (produces a position-independent static binary that self-relocates wherever we load it):
 *
 *     musl-gcc -static-pie -Os -s -o hello hello.c
 *
 * Then regenerate GuestElf.cs from the bytes (PowerShell):
 *
 *     $b = [IO.File]::ReadAllBytes('hello'); [Convert]::ToBase64String($b)
 *
 * Static-pie + musl keeps the syscall surface tiny: arch_prctl(ARCH_SET_FS), set_tid_address,
 * ioctl(TIOCGWINSZ), writev, write, exit_group (see strace) - all serviced by PuckSyscallDispatch.
 *
 * Part of Puck.BareMetal.
 */
#include <unistd.h>
#include <stdio.h>

int main(int argc, char **argv)
{
    /* printf exercises musl's stdio (buffering -> writev), not just a raw write syscall. */
    printf("Hello from a REAL musl static Linux binary, hosted by Puck!\n");
    printf("argc=%d, running at ring 3 on our own page tables.\n", argc);
    write(1, "Goodbye from the guest.\n", 24);
    return 0;
}
