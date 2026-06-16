#include <stdio.h>
#include <string.h>

int main(void)
{
    FILE *f = fopen("/proc/cpuinfo", "r");
    if (!f) { printf("fopen /proc/cpuinfo FAILED\n"); return 1; }
    char buf[512];
    size_t n = fread(buf, 1, sizeof(buf) - 1, f);
    buf[n] = 0;
    printf("read %zu bytes from /proc/cpuinfo:\n%s", n, buf);
    fclose(f);

    FILE *z = fopen("/dev/zero", "r");
    if (!z) { printf("fopen /dev/zero FAILED\n"); return 1; }
    unsigned char zb[16];
    memset(zb, 0xFF, sizeof(zb));
    size_t zn = fread(zb, 1, sizeof(zb), z);
    int allzero = 1;
    for (size_t i = 0; i < zn; i++) if (zb[i]) allzero = 0;
    printf("/dev/zero: read %zu bytes, all-zero=%d\n", zn, allzero);
    fclose(z);
    return 0;
}
