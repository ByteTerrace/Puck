#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static long total = 0;

static void *worker(void *arg)
{
    long id = (long)arg;
    long local = 0;
    for (int i = 0; i < 200000; i++) local += i;
    pthread_mutex_lock(&lock);
    total += local;
    pthread_mutex_unlock(&lock);
    printf("worker %ld done (local=%ld)\n", id, local);
    return NULL;
}

int main(void)
{
    struct timespec t0;
    clock_gettime(CLOCK_MONOTONIC, &t0);

    unsigned char *m = malloc(8 * 1024 * 1024);
    memset(m, 7, 8 * 1024 * 1024);
    int probe = m[1234567];

    pthread_t th[4];
    for (long i = 0; i < 4; i++) pthread_create(&th[i], NULL, worker, (void *)i);
    for (int i = 0; i < 4; i++) pthread_join(th[i], NULL);

    free(m);
    printf("all joined: total=%ld, probe=%d\n", total, probe);
    return 0;
}
