#include <stdio.h>
#include <time.h>
#include <sys/random.h>

int main(void)
{
    struct timespec rt, mono;
    clock_gettime(CLOCK_REALTIME, &rt);
    clock_gettime(CLOCK_MONOTONIC, &mono);
    printf("CLOCK_REALTIME : %ld (unix epoch, UTC)\n", (long)rt.tv_sec);
    printf("CLOCK_MONOTONIC: %ld.%03ld s since boot\n", (long)mono.tv_sec, (long)(mono.tv_nsec / 1000000));

    time_t t = rt.tv_sec;
    struct tm *g = gmtime(&t);
    if (g)
        printf("UTC decoded    : %04d-%02d-%02d %02d:%02d:%02d\n",
               g->tm_year + 1900, g->tm_mon + 1, g->tm_mday, g->tm_hour, g->tm_min, g->tm_sec);

    unsigned char buf[16];
    ssize_t n = getrandom(buf, sizeof(buf), 0);
    printf("getrandom %zd  :", n);
    for (int i = 0; i < (int)n; i++) printf(" %02x", buf[i]);
    printf("\n");
    return 0;
}
