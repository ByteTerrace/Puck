#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>

int main(void)
{
    struct timespec t0;
    clock_gettime(CLOCK_MONOTONIC, &t0);

    size_t msz = 4 * 1024 * 1024;
    unsigned char *m = mmap(NULL, msz, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (m == MAP_FAILED) { printf("mmap FAILED\n"); return 1; }
    memset(m, 0x5A, msz);

    size_t n = 8 * 1024 * 1024;
    unsigned char *buf = malloc(n);
    if (!buf) { printf("malloc FAILED\n"); return 1; }
    memset(buf, 0xAB, n);

    unsigned long sum = 0;
    for (size_t i = 0; i < n; i += 4096) sum += buf[i];
    for (size_t i = 0; i < msz; i += 4096) sum += m[i];

    struct timespec t1;
    clock_gettime(CLOCK_MONOTONIC, &t1);
    printf("mmap+malloc+touch OK: sum=%lu, dt_ok=%d\n", sum, (t1.tv_sec >= t0.tv_sec));

    free(buf);
    munmap(m, msz);
    printf("clean exit\n");
    return 0;
}
