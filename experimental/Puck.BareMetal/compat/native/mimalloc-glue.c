/* Part of Puck.BareMetal. Freestanding glue bridging mimalloc into the no-CRT, no-GC puck link.
 *
 * mimalloc is a full C allocator that normally sits on top of a C runtime. Our link is
 * freestanding (kernel32 + ucrt import lib only; the SDK NativeAOT runtime is stripped),
 * so three things the CRT would otherwise provide are supplied here:
 *
 *   1. A minimal TLS directory (_tls_used). mimalloc keeps its per-thread heap pointer in
 *      a __declspec(thread) variable; the linker only emits the PE TLS directory that makes
 *      static TLS work if the magic symbol `_tls_used` is defined (normally by the CRT's
 *      tlssup.obj, which we do not link). The Windows loader then allocates the TLS block
 *      and runs the TLS callbacks for every thread, with no CRT involvement.
 *   2. A handful of libc shims (abort/atexit/getenv/strtol/fputs) that mimalloc references
 *      but that, taken from the real ucrt, would touch uninitialised CRT state (the env
 *      block, the atexit table, stdio) because CRT startup never runs. Ours are freestanding
 *      and self-contained. memset/memcpy/memmove live in puck-rt.c and are not redefined.
 *   3. Explicit process/thread init. mimalloc self-initialises from a `.CRT$XIU` constructor,
 *      which only the CRT runs; we call mi_process_init()/mi_thread_init() once instead.
 *
 * Built freestanding (cl /c /GS- /O1; see build/Puck.BareMetal.targets). The single export
 * PuckAllocZeroed is what the object allocator calls.
 */

#include <windows.h>

#include "mimalloc.h"

/* --------------------------------------------------------------------------------------
 * 1. Minimal TLS support so __declspec(thread) resolves without the CRT.
 *
 * Layout mirrors the MSVC CRT's tlssup.c: _tls_start / _tls_end bracket the .tls section
 * that holds all thread-local data; __xl_a / __xl_z bracket the TLS callback array; and
 * `_tls_used` is the IMAGE_TLS_DIRECTORY the linker stamps into the PE for the loader.
 * ------------------------------------------------------------------------------------ */
#pragma section(".tls",     long, read, write)
#pragma section(".tls$ZZZ", long, read, write)
#pragma section(".CRT$XLA", long, read)
#pragma section(".CRT$XLZ", long, read)
#pragma section(".rdata$T", long, read)

__declspec(allocate(".tls"))     char _tls_start = 0;
__declspec(allocate(".tls$ZZZ")) char _tls_end   = 0;
__declspec(allocate(".CRT$XLA")) PIMAGE_TLS_CALLBACK __xl_a = 0;
__declspec(allocate(".CRT$XLZ")) PIMAGE_TLS_CALLBACK __xl_z = 0;

ULONG _tls_index = 0;

__declspec(allocate(".rdata$T")) const IMAGE_TLS_DIRECTORY _tls_used = {
    (ULONGLONG)&_tls_start,     /* StartAddressOfRawData */
    (ULONGLONG)&_tls_end,       /* EndAddressOfRawData   */
    (ULONGLONG)&_tls_index,     /* AddressOfIndex        */
    (ULONGLONG)(&__xl_a + 1),   /* AddressOfCallBacks (first slot after __xl_a) */
    (ULONG)0,                   /* SizeOfZeroFill        */
    (ULONG)0                    /* Characteristics       */
};

/* --------------------------------------------------------------------------------------
 * 2. Freestanding libc shims mimalloc references.
 *
 * These intentionally shadow the ucrt versions: because they are defined in this object,
 * the linker resolves mimalloc's references here and never pulls the ucrt copies (which
 * assume CRT startup ran). memset/memcpy/memmove are provided by puck-rt.c.
 * ------------------------------------------------------------------------------------ */
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int uExitCode);

/* mimalloc calls abort() only on unrecoverable corruption / OOM-in-new. Terminate hard. */
__declspec(noreturn) void abort(void)
{
    ExitProcess(0xFFu);
}

/* No CRT atexit table exists; process teardown is the OS reclaiming the address space.
 * Returning 0 (success) makes mimalloc believe its cleanup hook is registered. */
int atexit(void (*func)(void))
{
    (void)func;
    return 0;
}

/* No CRT environment block: option overrides via MIMALLOC_* env vars are simply absent. */
char *getenv(const char *name)
{
    (void)name;
    return (char *)0;
}

/* Minimal base-10/16 signed parse. Only reached if an option string is parsed; with getenv
 * returning NULL the runtime path is effectively dead, but the symbol must still resolve. */
long strtol(const char *nptr, char **endptr, int base)
{
    const char *s = nptr;
    long value = 0;
    int negative = 0;

    while (*s == ' ' || (*s >= '\t' && *s <= '\r'))
        s++;

    if (*s == '+' || *s == '-')
        negative = (*s++ == '-');

    if ((base == 0 || base == 16) && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
    {
        s += 2;
        base = 16;
    }
    else if (base == 0)
    {
        base = (s[0] == '0') ? 8 : 10;
    }

    for (;;)
    {
        int digit;
        char c = *s;

        if (c >= '0' && c <= '9')
            digit = c - '0';
        else if (c >= 'a' && c <= 'z')
            digit = c - 'a' + 10;
        else if (c >= 'A' && c <= 'Z')
            digit = c - 'A' + 10;
        else
            break;

        if (digit >= base)
            break;

        value = value * base + digit;
        s++;
    }

    if (endptr != (char **)0)
        *endptr = (char *)s;

    return negative ? -value : value;
}

/* mimalloc's diagnostic output already flows through its Windows prim (WriteConsole); this
 * secondary path is not exercised at boot, so swallow it rather than wire up a FILE*. */
int fputs(const char *str, void *stream)
{
    (void)str;
    (void)stream;
    return 0;
}

/* --------------------------------------------------------------------------------------
 * 3. The allocation entry point the object allocator calls.
 *
 * Zero-initialised (RhpNew* expects zeroed storage). mimalloc is brought up lazily on first use.
 *
 * THREAD SAFETY: the eventual target (an EFI host for multi-threaded GPU drivers) allocates
 * from many threads, so init must be correct under concurrency:
 *   - mi_process_init() must run EXACTLY ONCE for the whole process. A plain check-then-set
 *     flag races (two threads both init -> corruption), so claim init with an interlocked CAS
 *     and have late arrivals wait until it has published.
 *   - mi_thread_init() is PER THREAD (each thread gets its own heap), so it must run on every
 *     thread's first allocation — a single process-wide flag would skip it for all but the
 *     first thread. A thread-local guard handles that.
 *
 * (On a custom OS we also own thread creation, so the per-thread TLS that backs t_threadInit —
 * and mimalloc's own thread var — must be set up per thread by our threading layer, the same
 * way the Windows loader does it for the hosted build via the TLS directory above.)
 * ------------------------------------------------------------------------------------ */
#define PUCK_PROC_UNINIT 0L
#define PUCK_PROC_INITING 1L
#define PUCK_PROC_READY 2L

static volatile long s_processState = PUCK_PROC_UNINIT;
static __declspec(thread) int t_threadInit = 0;

static void PuckEnsureProcessInit(void)
{
    if (s_processState == PUCK_PROC_READY)
        return;

    if (InterlockedCompareExchange(&s_processState, PUCK_PROC_INITING, PUCK_PROC_UNINIT) == PUCK_PROC_UNINIT)
    {
        mi_process_init();
        /* Publish: the interlocked store releases the writes mi_process_init made. */
        InterlockedExchange(&s_processState, PUCK_PROC_READY);
    }
    else
    {
        /* Another thread is initializing (or just finished); wait for it to publish. */
        while (s_processState != PUCK_PROC_READY)
            YieldProcessor();
    }
}

void *PuckAllocZeroed(size_t size)
{
    PuckEnsureProcessInit();

    if (!t_threadInit)
    {
        mi_thread_init();
        t_threadInit = 1;
    }

    return mi_zalloc(size);
}
