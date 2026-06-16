#include <stdio.h>
#include <pthread.h>

static volatile long counter = 0;

static void *spinner(void *arg)
{
    (void)arg;
    /* Pure compute, NO syscalls: under a cooperative scheduler this thread, once it never yields,
     * would starve everyone else. Only preemption (the timer) lets the main thread run again. */
    for (long i = 0; i < 4000000L; i++)
        counter++;
    return NULL;
}

int main(void)
{
    pthread_t t;
    pthread_create(&t, NULL, spinner, NULL);

    long target = 1000000L;
    while (counter < target) { } /* spin, no syscall; advances only if the spinner is preempted in */

    printf("main saw counter reach %ld while the spinner ran -> PREEMPTION WORKS\n", target);
    pthread_join(t, NULL);
    printf("joined: counter=%ld\n", counter);
    return 0;
}
