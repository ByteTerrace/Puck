/* Bare-metal UEFI kernel + native runtime glue. Part of Puck.BareMetal.
 *
 * The UEFI counterpart of puck-rt.c, and the kernel bring-up shim. Beyond the OS-agnostic
 * JIT-transition / GC-poll / block-memory glue, this file:
 *   - owns a COM1 16550 UART as the output channel (works before AND after the firmware is gone);
 *   - backs the managed heap with EFI_BOOT_SERVICES->AllocatePool while boot services are up, and
 *     with a self-hosted bump allocator over real RAM after ExitBootServices;
 *   - in EfiEntry, takes the machine from the firmware (GetMemoryMap + ExitBootServices) and then
 *     runs the managed program self-hosted: no firmware, no OS, our memory, our console.
 *
 * Built freestanding: cl /c /GS- /O1 (see build/Puck.BareMetal.Efi.targets).
 */

/* ---- compiler intrinsics: port I/O (UART), MSRs (SYSCALL setup); no CRT needed ---- */
unsigned char __inbyte(unsigned short port);
void __outbyte(unsigned short port, unsigned char data);
unsigned short __inword(unsigned short port);
void __outword(unsigned short port, unsigned short data);
unsigned long __indword(unsigned short port);
void __outdword(unsigned short port, unsigned long data);
unsigned long long __readmsr(unsigned long reg);
void __writemsr(unsigned long reg, unsigned long long value);
unsigned long long __rdtsc(void);
void _disable(void); /* cli */
void _enable(void);  /* sti */
void __cpuid(int cpuInfo[4], int function);
#pragma intrinsic(__inbyte, __outbyte, __inword, __outword, __indword, __outdword, __readmsr, __writemsr, __rdtsc, _disable, _enable, __cpuid)

/* Never nonzero: the GC poll the JIT inlines as `if (RhpTrapThreads) RhpGcPoll()` is dead. */
unsigned int RhpTrapThreads = 0;

void RhpReversePInvoke(void *frame) { (void)frame; }
void RhpReversePInvokeReturn(void *frame) { (void)frame; }
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }
void RhpGcPoll(void) { }

static void PuckHang(void)
{
    for (;;)
        ;
}

void RhpFallbackFailFast(void) { PuckHang(); }

/* Reference-store write barriers: with no GC a barrier is just the pointer store. */
void RhpAssignRef(void **dst, void *reference)        { *dst = reference; }
void RhpCheckedAssignRef(void **dst, void *reference) { *dst = reference; }

/* ---------------------------------------------------------------------------------------
 * COM1 16550 serial console. We program the UART directly (port I/O), so it works identically
 * before and after ExitBootServices - unlike the firmware's ConOut, which stops at exit. QEMU
 * routes COM1 (0x3F8) to its -serial, so this is also how the boot is observed in a VM. */
#define COM1 0x3F8

static void SerialInit(void)
{
    __outbyte(COM1 + 1, 0x00); /* disable interrupts            */
    __outbyte(COM1 + 3, 0x80); /* enable DLAB (set baud divisor) */
    __outbyte(COM1 + 0, 0x01); /* divisor low  = 1 -> 115200     */
    __outbyte(COM1 + 1, 0x00); /* divisor high = 0               */
    __outbyte(COM1 + 3, 0x03); /* 8 bits, no parity, 1 stop      */
    __outbyte(COM1 + 2, 0xC7); /* enable+clear FIFO, 14B trigger */
    __outbyte(COM1 + 4, 0x0B); /* RTS/DSR set, OUT2              */
}

static void FbWriteByte(unsigned char b); /* the GOP framebuffer console (no-op until captured) */

static void SerialWriteByte(unsigned char b)
{
    while ((__inbyte(COM1 + 5) & 0x20) == 0) /* wait for THR empty */
        ;
    __outbyte(COM1, b);
    FbWriteByte(b); /* mirror to the framebuffer: the Steam Deck has no serial port */
}

/* Exported for managed code. A per-byte writer avoids needing a managed stack buffer (which the
 * JIT would protect with a /GS cookie -> __security_cookie, absent under /NODEFAULTLIB). */
void PuckSerialWriteByte(int b)
{
    SerialWriteByte((unsigned char)b);
}

static void SerialPuts(const char *s)
{
    while (*s)
        SerialWriteByte((unsigned char)*s++);
}

/* 16-digit hex, for register/state dumps (no leading-zero suppression - alignment aids reading). */
static void SerialPutHex64(unsigned long long v)
{
    int i;
    SerialPuts("0x");
    for (i = 60; i >= 0; i -= 4)
    {
        unsigned int nib = (unsigned int)((v >> i) & 0xF);
        SerialWriteByte((unsigned char)(nib < 10 ? ('0' + nib) : ('a' + nib - 10)));
    }
}

/* ---------------------------------------------------------------------------------------
 * EFI ABI: just the few service slots we call, by byte offset (so we need no EFI headers).
 * Verified against the UEFI spec table layouts. EFIAPI on x64 == this TU's default convention. */
#define EFI_ST_BOOTSERVICES_OFFSET   96   /* EFI_SYSTEM_TABLE.BootServices            */
#define EFI_BS_GETMEMORYMAP_OFFSET   56   /* EFI_BOOT_SERVICES.GetMemoryMap           */
#define EFI_BS_ALLOCATEPOOL_OFFSET   64   /* EFI_BOOT_SERVICES.AllocatePool           */
#define EFI_BS_EXITBOOTSERVICES_OFF  232  /* EFI_BOOT_SERVICES.ExitBootServices       */
#define EFI_BS_LOCATEPROTOCOL_OFFSET 320  /* EFI_BOOT_SERVICES.LocateProtocol         */
#define EFI_MEMTYPE_LOADER_DATA      2    /* EFI_MEMORY_TYPE.EfiLoaderData            */
#define EFI_MEMTYPE_CONVENTIONAL     7    /* EFI_MEMORY_TYPE.EfiConventionalMemory    */
#define EFI_PAGE_SIZE                4096

typedef unsigned long long efi_status_t;
typedef efi_status_t (*efi_allocate_pool_t)(int poolType, unsigned long long size, void **buffer);
typedef efi_status_t (*efi_get_memory_map_t)(unsigned long long *mapSize, void *map,
                                             unsigned long long *mapKey, unsigned long long *descSize,
                                             unsigned int *descVersion);
typedef efi_status_t (*efi_exit_boot_services_t)(void *imageHandle, unsigned long long mapKey);

static void *EfiField(void *base, unsigned int byteOffset) { return *(void **)((char *)base + byteOffset); }

/* ---------------------------------------------------------------------------------------
 * Heap. Before ExitBootServices the firmware's pool serves allocations; after it, a bump
 * allocator hands out real RAM from the largest EfiConventionalMemory region. RhpNewFast /
 * RhpNewArrayFast (the symbols the ILC emits for `newobj`/`newarr`) route through here. */
void *g_puckEfiSystemTable;
void *PuckGetEfiSystemTable(void) { return g_puckEfiSystemTable; }

static int            g_postExit = 0;
static unsigned char *g_heapStart = 0;
static unsigned char *g_heapPtr  = 0;
static unsigned char *g_heapEnd  = 0;

/* Bytes handed out by the bump allocator so far. Exposed so managed code can show that pooling
 * keeps the watermark bounded under a no-free allocator. */
unsigned long long PuckHeapUsed(void)
{
    return g_heapStart != 0 ? (unsigned long long)(g_heapPtr - g_heapStart) : 0;
}

static void *PuckAlloc(unsigned long long size)
{
    unsigned char *result;
    unsigned long long i;

    if (!g_postExit)
    {
        /* Firmware boot services up: allocate from the firmware pool (not zeroed -> we zero). */
        void *bs = EfiField(g_puckEfiSystemTable, EFI_ST_BOOTSERVICES_OFFSET);
        efi_allocate_pool_t allocPool = (efi_allocate_pool_t)EfiField(bs, EFI_BS_ALLOCATEPOOL_OFFSET);
        void *buf = 0;
        if (allocPool(EFI_MEMTYPE_LOADER_DATA, size, &buf) != 0 || buf == 0)
            PuckHang();
        result = (unsigned char *)buf;
    }
    else
    {
        /* Self-hosted: bump over our conventional-memory heap (16-byte aligned). */
        size = (size + 15ull) & ~15ull;
        if (g_heapPtr == 0 || g_heapPtr + size > g_heapEnd)
            PuckHang(); /* out of memory */
        result = g_heapPtr;
        g_heapPtr += size;
    }

    for (i = 0; i < size; i++) /* RhpNew* expects zeroed storage */
        result[i] = 0;
    return result;
}

/* MethodTable: only the first 8 bytes (ushort componentSize, ushort flags, uint baseSize) are
 * needed here - same view as puck-rt.c. */
typedef struct PuckMethodTable
{
    unsigned short ComponentSize;
    unsigned short Flags;
    unsigned int   BaseSize;
} PuckMethodTable;

void *RhpNewFast(PuckMethodTable *pMethodTable)
{
    void **result = (void **)PuckAlloc(pMethodTable->BaseSize);
    result[0] = pMethodTable;
    return result;
}

void *RhpNewArrayFast(PuckMethodTable *pMethodTable, int numElements)
{
    unsigned long long size;
    void **result;

    if (numElements < 0)
        PuckHang();

    size = (unsigned long long)pMethodTable->BaseSize
         + (unsigned long long)(unsigned int)numElements * pMethodTable->ComponentSize;

    result = (void **)PuckAlloc(size);
    result[0] = pMethodTable;
    *(int *)((unsigned char *)result + sizeof(void *)) = numElements;
    return result;
}

void *RhpNewPtrArrayFast(PuckMethodTable *pMethodTable, int numElements)
{
    return RhpNewArrayFast(pMethodTable, numElements);
}

/* ---------------------------------------------------------------------------------------
 * Lazy static constructors (single-threaded). The stock ILC routes a first static access of a
 * type with a real .cctor through ClassConstructorRunner -> PuckRunClassConstructor. UEFI
 * boot is single-threaded, so (unlike the hosted race-safe runner) this just runs the .cctor
 * once: zero the slot first so reentrant access from inside the .cctor sees "done" and does not
 * recurse, then invoke it. The managed side binds here via DllImport("puckrt") (a
 * DirectPInvoke; there is no such DLL). */
void PuckRunClassConstructor(long long volatile *pCctor)
{
    long long addr = *pCctor;
    if (addr != 0)
    {
        *pCctor = 0; /* run-once + reentrancy guard (single-threaded) */
        ((void (*)(void))addr)();
    }
}

/* ---------------------------------------------------------------------------------------
 * GC (reference-typed) static initialization. Same mechanism as the hosted build: the ILC emits
 * a per-type spine object for a type's reference statics and a GCStaticRegion table of relative
 * pointers to the base cells; the spine must be allocated and the base cell patched before any
 * managed code touches a reference static. The hosted bootstrapper does this in InitializeStatics
 * (StartupCodeHelpers.cs); we re-create the GC-static slice here and run it from EfiEntry, after
 * the bump heap is armed (so the spine allocations come from our own RAM). */
void *memcpy(void *dest, const void *src, unsigned long long count); /* defined below */

typedef struct PuckReadyToRunHeader
{
    unsigned int   Signature; /* 'RTR\0' == 0x00525452 */
    unsigned short MajorVersion;
    unsigned short MinorVersion;
    unsigned int   Flags;
    unsigned short NumberOfSections;
    unsigned char  EntrySize;
    unsigned char  EntryType;
} PuckReadyToRunHeader;

typedef struct PuckModuleInfoRow
{
    int   SectionId;
    int   Flags;
    void *Start;
    void *End;
} PuckModuleInfoRow;

extern PuckReadyToRunHeader __ReadyToRunHeader;

#define PUCK_RTR_GCSTATICREGION     201
#define PUCK_GCSTATIC_UNINITIALIZED 0x1u
#define PUCK_GCSTATIC_HASPREINIT    0x2u
#define PUCK_GCSTATIC_MASK          0x3u

static void *Puck_ReadRelPtr32(void *address) { return (char *)address + *(int *)address; }

static void PuckInitGCStatics(void)
{
    PuckReadyToRunHeader *header = &__ReadyToRunHeader;
    PuckModuleInfoRow *rows;
    unsigned int i;

    if (header->Signature != 0x00525452u)
        PuckHang();

    rows = (PuckModuleInfoRow *)((char *)header + sizeof(PuckReadyToRunHeader));

    for (i = 0; i < header->NumberOfSections; i++)
    {
        char *block, *end;

        if (rows[i].SectionId != PUCK_RTR_GCSTATICREGION)
            continue;

        block = (char *)rows[i].Start;
        end = (char *)rows[i].End;

        for (; block < end; block += sizeof(int))
        {
            void *pBlock = Puck_ReadRelPtr32(block);
            unsigned long long blockAddr = (unsigned long long)Puck_ReadRelPtr32(pBlock);

            if (blockAddr & PUCK_GCSTATIC_UNINITIALIZED)
            {
                PuckMethodTable *eeType = (PuckMethodTable *)(blockAddr & ~(unsigned long long)PUCK_GCSTATIC_MASK);
                void *spine = RhpNewFast(eeType);

                if (blockAddr & PUCK_GCSTATIC_HASPREINIT)
                {
                    void *preInit = Puck_ReadRelPtr32((char *)pBlock + sizeof(int));
                    memcpy((char *)spine + sizeof(void *), preInit, eeType->BaseSize - 2 * sizeof(void *));
                }

                *(void **)pBlock = spine;
            }
        }
    }
}

/* ---------------------------------------------------------------------------------------
 * Linux SYSCALL ABI. To run a Linux x86-64 binary we must service the `syscall` instruction:
 * set up our own GDT (so SYSCALL has known kernel selectors), point LSTAR at the assembly entry
 * trampoline, and dispatch the Linux syscall numbers. The guest runs in ring 3 (user/kernel split);
 * the trampoline returns to it via sysretq/iretq (see puck-efi-x64.asm). */
extern void PuckLoadGdt(void *gdtr);          /* puck-efi-x64.asm */
extern void PuckLoadIdt(void *idtr);          /* puck-efi-x64.asm */
extern void PuckLoadTr(unsigned long long selector); /* puck-efi-x64.asm */
extern void PuckSyscallEntry(void);           /* puck-efi-x64.asm (LSTAR target) */
extern unsigned long long g_puckIsrStubs[32]; /* puck-efi-x64.asm: the 32 exception ISR stubs */
extern unsigned long long PuckReadCr2(void);  /* puck-efi-x64.asm */
extern unsigned long long PuckReadCr3(void);  /* puck-efi-x64.asm */
extern void PuckWriteCr3(unsigned long long pml4); /* puck-efi-x64.asm */
extern void PuckWbinvd(void);                 /* puck-efi-x64.asm: cache writeback+invalidate */
extern void PuckClflush(void *addr);          /* puck-efi-x64.asm */
extern void PuckMfence(void);                 /* puck-efi-x64.asm */
extern unsigned long long PuckRdRand64(void); /* puck-efi-x64.asm: hardware RNG */

/* GDT laid out so SYSRET's selector math (STAR[63:48] = 0x10) lands on our user descriptors:
 *   0x00 null | 0x08 kernel code | 0x10 kernel data | 0x18 user data | 0x20 user code |
 *   0x28/0x30 TSS (16-byte system descriptor, filled at runtime).
 * SYSRET to 64-bit: CS = 0x10+16 = 0x20 (|RPL3 = 0x23), SS = 0x10+8 = 0x18 (|RPL3 = 0x1B). */
static unsigned long long g_puckGdt[7] = {
    0x0000000000000000ULL, /* 0x00 null                       */
    0x00AF9A000000FFFFULL, /* 0x08 kernel code (ring 0)        */
    0x00CF92000000FFFFULL, /* 0x10 kernel data (ring 0)        */
    0x00CFF2000000FFFFULL, /* 0x18 user data   (ring 3, DPL=3) */
    0x00AFFA000000FFFFULL, /* 0x20 user code   (ring 3, DPL=3) */
    0x0000000000000000ULL, /* 0x28 TSS low  (filled in PuckInitGdt) */
    0x0000000000000000ULL, /* 0x30 TSS high                            */
};

#pragma pack(push, 1)
typedef struct { unsigned short Limit; unsigned long long Base; } PuckGdtr;

/* 64-bit TSS. We only use rsp0 (the ring-0 stack the CPU switches to when a ring-3 interrupt or
 * exception fires); the rest stay zero. IoMapBase >= limit disables the I/O permission bitmap, so
 * ring-3 in/out faults (#GP), which is what we want. */
typedef struct PuckTss
{
    unsigned int       Reserved0;
    unsigned long long Rsp0;
    unsigned long long Rsp1;
    unsigned long long Rsp2;
    unsigned long long Reserved1;
    unsigned long long Ist[7];
    unsigned long long Reserved2;
    unsigned short     Reserved3;
    unsigned short     IoMapBase;
} PuckTss;
#pragma pack(pop)
static PuckGdtr g_puckGdtr;
static PuckTss g_puckTss;
static unsigned char g_puckKernelStack[16384]; /* rsp0: ring-0 stack for ring-3 faults */

/* Kernel scratch stack the syscall trampoline runs on. Single-CPU and cooperative: only one thread
 * is ever inside a syscall at a time, so one stack suffices. Referenced from the asm. */
static unsigned char g_puckSyscallStack[16384];
unsigned long long g_puckSyscallStackTop;

/* Saved user context. The asm trampoline writes RAX..RFLAGS (the first 16 fields) into
 * g_puckIncomingCtx on syscall entry and reloads them from the resumed thread's ctx on exit;
 * FSBASE is managed only on the C side. Field order/offsets MUST match the CTX_* equ's in
 * puck-efi-x64.asm. */
typedef struct PuckCtx
{
    unsigned long long rax, rbx, rcx, rdx, rsi, rdi, rbp, rsp, r8, r9, r10, r11, r12, r13, r14, r15, rip, rflags;
    unsigned long long fsbase;
} PuckCtx;
PuckCtx g_puckIncomingCtx;
#define PUCK_CTX_GPR_QWORDS 18 /* rax..rflags; the asm save/restore set (fsbase is C-only) */

/* Cooperative threads sharing the one address space. Each parks at a syscall-return point when not
 * running; a context switch is just resuming a different thread's ctx from the trampoline. */
#define PUCK_MAX_THREADS     64
#define PUCK_THREAD_FREE      0
#define PUCK_THREAD_RUNNABLE  1
#define PUCK_THREAD_BLOCKED   2
#define PUCK_THREAD_ZOMBIE    3

typedef struct PuckThread
{
    PuckCtx         ctx;          /* MUST be first: the asm resumes a thread via this pointer */
    int                tid;
    int                state;
    unsigned long long futexAddr;    /* uaddr a BLOCKED thread is waiting on (FUTEX_WAIT)        */
    unsigned long long clearTidAddr; /* CLONE_CHILD_CLEARTID: clear + futex-wake on thread exit  */
} PuckThread;

static PuckThread g_puckThreads[PUCK_MAX_THREADS];
static PuckThread *g_currentThread;
static int g_puckNextTid = 1;

/* Linux x86-64 syscall numbers we model. */
#define LINUX_SYS_WRITE            1
#define LINUX_SYS_MMAP             9
#define LINUX_SYS_MPROTECT        10
#define LINUX_SYS_MUNMAP          11
#define LINUX_SYS_BRK             12
#define LINUX_SYS_RT_SIGPROCMASK  14
#define LINUX_SYS_IOCTL           16
#define LINUX_SYS_WRITEV          20
#define LINUX_SYS_EXIT            60
#define LINUX_SYS_ARCH_PRCTL     158
#define LINUX_SYS_SET_TID_ADDRESS 218
#define LINUX_SYS_CLOCK_GETTIME  228
#define LINUX_SYS_EXIT_GROUP     231
#define LINUX_SYS_SET_ROBUST_LIST 273
#define LINUX_SYS_GETRANDOM      318
#define LINUX_ENOSYS             (-38)
#define LINUX_ENOTTY             (-25)
#define LINUX_EINVAL             (-22)
#define LINUX_ENOMEM             (-12)
#define LINUX_EAGAIN             (-11)

/* Threading syscalls + the clone/futex sub-encodings (intercepted in PuckHandleSyscall). */
#define LINUX_SYS_SCHED_YIELD     24
#define LINUX_SYS_CLONE           56
#define LINUX_SYS_GETTID         186
#define LINUX_SYS_FUTEX          202
#define CLONE_SETTLS         0x00080000ull
#define CLONE_PARENT_SETTID  0x00100000ull
#define CLONE_CHILD_CLEARTID 0x00200000ull
#define CLONE_CHILD_SETTID   0x01000000ull
#define FUTEX_CMD_MASK       (~(0x80 | 0x100)) /* strip FUTEX_PRIVATE_FLAG / FUTEX_CLOCK_REALTIME */

/* File / synthetic-device syscalls. */
#define LINUX_SYS_READ      0
#define LINUX_SYS_OPEN      2
#define LINUX_SYS_CLOSE     3
#define LINUX_SYS_LSEEK     8
#define LINUX_SYS_READV    19
#define LINUX_SYS_OPENAT  257
#define LINUX_EBADF      (-9)
#define LINUX_ENOENT     (-2)
#define LINUX_EMFILE     (-24)

/* The drmGetDevices2 surface (fstat -> rdev -> /sys PCI walk) + ACO/thread stubs. */
#define LINUX_SYS_PREAD64           17
#define LINUX_SYS_STAT               4
#define LINUX_SYS_FSTAT              5
#define LINUX_SYS_LSTAT              6
#define LINUX_SYS_ACCESS            21
#define LINUX_SYS_RT_SIGACTION      13
#define LINUX_SYS_MREMAP            25
#define LINUX_SYS_MADVISE           28
#define LINUX_SYS_GETPID            39
#define LINUX_SYS_FCNTL             72
#define LINUX_SYS_READLINK          89
#define LINUX_SYS_SYSINFO           99
#define LINUX_SYS_SCHED_GETAFFINITY 204
#define LINUX_SYS_GETDENTS64        217
#define LINUX_SYS_NEWFSTATAT        262
#define LINUX_SYS_READLINKAT        267
#define LINUX_SYS_STATX             332
#define LINUX_ENOTDIR    (-20)

/* Defined later (paging section); forward-declared so the arena helper below can use them. */
static unsigned long long PuckAllocPages(unsigned long long pages);
void PuckSetUserAccessible(unsigned long long addr, unsigned long long size);

/* A page-aligned, ring-3-accessible arena the guest's brk and anonymous mmap are served from. brk
 * grows up from the base; mmap grows down from the top; they meet in the middle (no real free - a
 * bump allocator, which is fine for a no-exit single guest). Lazily carved on first use. */
#define PUCK_GUEST_ARENA_BYTES (64ull * 1024 * 1024)
static unsigned long long g_guestArenaBase, g_guestBrk, g_guestMmapTop, g_guestArenaEnd;

static void PuckInitGuestArena(void)
{
    if (g_guestArenaBase != 0)
        return;
    g_guestArenaBase = PuckAllocPages(PUCK_GUEST_ARENA_BYTES / 4096);
    PuckSetUserAccessible(g_guestArenaBase, PUCK_GUEST_ARENA_BYTES);
    g_guestBrk = g_guestArenaBase;
    g_guestArenaEnd = g_guestArenaBase + PUCK_GUEST_ARENA_BYTES;
    g_guestMmapTop = g_guestArenaEnd;
}

/* ---------------------------------------------------------------------------------------
 * Time. The CMOS RTC (QEMU default = UTC) gives a one-time boot epoch; the TSC, calibrated against
 * PIT channel 2, advances it. CLOCK_REALTIME = epoch + uptime (needed for TLS cert validity / token
 * expiry); CLOCK_MONOTONIC = uptime. Hardware facilities only - no invented time source. */
static unsigned long long g_tscFreqHz = 1000000000ull; /* placeholder until PuckInitClock calibrates */
static unsigned long long g_bootTsc;
static unsigned long long g_bootEpoch; /* Unix seconds (UTC) sampled at boot */

static unsigned char PuckCmosRead(unsigned char reg)
{
    __outbyte(0x70, reg);
    return __inbyte(0x71);
}

/* Days since 1970-01-01 for a proleptic-Gregorian date (Howard Hinnant's civil-from-days inverse). */
static long long PuckDaysFromCivil(long long y, unsigned m, unsigned d)
{
    long long era;
    unsigned yoe, doy, doe;
    y -= (m <= 2);
    era = (y >= 0 ? y : y - 399) / 400;
    yoe = (unsigned)(y - era * 400);
    doy = (153u * (m + (m > 2 ? (unsigned)-3 : 9u)) + 2u) / 5u + d - 1u;
    doe = yoe * 365u + yoe / 4u - yoe / 100u + doy;
    return era * 146097ll + (long long)doe - 719468ll;
}

static unsigned long long PuckReadRtcEpoch(void)
{
    unsigned char sec, min, hour, day, mon, yr, regB;

    while (PuckCmosRead(0x0A) & 0x80) /* wait out an update-in-progress */
        ;
    sec = PuckCmosRead(0x00); min = PuckCmosRead(0x02); hour = PuckCmosRead(0x04);
    day = PuckCmosRead(0x07); mon = PuckCmosRead(0x08); yr = PuckCmosRead(0x09);
    regB = PuckCmosRead(0x0B);

    if (!(regB & 0x04)) /* register-B bit 2 clear => values are BCD; convert to binary */
    {
        sec = (unsigned char)((sec & 0x0F) + (sec >> 4) * 10);
        min = (unsigned char)((min & 0x0F) + (min >> 4) * 10);
        hour = (unsigned char)((hour & 0x0F) + (hour >> 4) * 10);
        day = (unsigned char)((day & 0x0F) + (day >> 4) * 10);
        mon = (unsigned char)((mon & 0x0F) + (mon >> 4) * 10);
        yr = (unsigned char)((yr & 0x0F) + (yr >> 4) * 10);
    }

    return (unsigned long long)(PuckDaysFromCivil(2000 + (long long)yr, mon, day) * 86400ll
        + (long long)hour * 3600 + (long long)min * 60 + sec);
}

void PuckInitClock(void)
{
    unsigned int count = 59659; /* 1193182 Hz * 0.05 s ~= 50 ms one-shot */
    unsigned long long t0, t1;
    unsigned char p61 = __inbyte(0x61);

    __outbyte(0x61, (unsigned char)((p61 & 0xFC) | 0x01)); /* PIT ch2 gate on, speaker off */
    __outbyte(0x43, 0xB0);                                 /* ch2, lo/hi, mode 0 (one-shot), binary */
    __outbyte(0x42, (unsigned char)(count & 0xFF));
    __outbyte(0x42, (unsigned char)((count >> 8) & 0xFF));
    t0 = __rdtsc();
    while ((__inbyte(0x61) & 0x20) == 0) /* wait for ch2 OUT to assert when the count reaches 0 */
        ;
    t1 = __rdtsc();
    if (t1 > t0)
        g_tscFreqHz = (t1 - t0) * 1193182ull / count;

    g_bootTsc = __rdtsc();
    g_bootEpoch = PuckReadRtcEpoch();
}

/* Monotonic seconds + nanoseconds since boot (overflow-safe). */
static void PuckUptime(unsigned long long *outSec, unsigned long long *outNsec)
{
    unsigned long long ticks = __rdtsc() - g_bootTsc;
    *outSec = ticks / g_tscFreqHz;
    *outNsec = (ticks % g_tscFreqHz) * 1000000000ull / g_tscFreqHz;
}

/* Milliseconds since boot - lwIP's sys_now() (puck-netif.c) wraps this. */
unsigned int PuckSysNowMs(void)
{
    return (unsigned int)((__rdtsc() - g_bootTsc) / (g_tscFreqHz / 1000ull));
}

/* Current wall-clock as a Unix epoch (UTC) - mbedTLS's cert-validity clock (puck-tls-mbedtls.c). */
long long PuckRealtimeEpoch(void)
{
    return (long long)g_bootEpoch + (long long)((__rdtsc() - g_bootTsc) / g_tscFreqHz);
}

/* ---------------------------------------------------------------------------------------
 * virtio-net (legacy/transitional, PIO virtqueue setup). The NIC under the network stack: discover
 * it over PCI, negotiate, build the RX/TX split virtqueues, and shuttle raw Ethernet frames. We
 * POLL the used rings (no IRQ wiring needed). lwIP rides on PuckNetTx / PuckNetRxPoll. */
static unsigned long PuckPciRead32(int bus, int dev, int fn, int off)
{
    __outdword(0xCF8, 0x80000000ul | ((unsigned long)bus << 16) | ((unsigned long)dev << 11)
                                   | ((unsigned long)fn << 8) | (unsigned long)(off & 0xFC));
    return __indword(0xCFC);
}
static void PuckPciWrite32(int bus, int dev, int fn, int off, unsigned long val)
{
    __outdword(0xCF8, 0x80000000ul | ((unsigned long)bus << 16) | ((unsigned long)dev << 11)
                                   | ((unsigned long)fn << 8) | (unsigned long)(off & 0xFC));
    __outdword(0xCFC, val);
}

/* Walk the PCI capability list and clear the MSI-X Enable bit, if present. A virtio device-status
 * reset does NOT touch PCI config space, so if a prior UEFI driver (e.g. iPXE when we PXE-boot) left
 * MSI-X enabled, the legacy virtio register window stays shifted +4 bytes and the device-specific
 * config (MAC at VIRTIO_PCI_CONFIG=0x14) would be misread. Forcing MSI-X off restores the layout our
 * legacy PIO driver hard-codes. No-op on a clean power-on device (bit already clear). */
static void PuckPciDisableMsix(int bus, int dev, int fn)
{
    unsigned long sr = PuckPciRead32(bus, dev, fn, 0x04); /* command(0x04) | status(0x06) */
    unsigned int cap;
    int guard = 0;
    if (!((sr >> 16) & (1u << 4))) return; /* Status reg bit 4: Capabilities List present */
    cap = PuckPciRead32(bus, dev, fn, 0x34) & 0xFC; /* capabilities pointer (dword-aligned) */
    while (cap >= 0x40 && guard++ < 48)
    {
        unsigned long c = PuckPciRead32(bus, dev, fn, (int)cap);
        unsigned int id = c & 0xFF; /* cap ID; next ptr is bits 8..15; MsgCtrl is bits 16..31 */
        if (id == 0x11) /* MSI-X: clear MsgCtrl bit 15 (Enable) == dword bit 31 */
        {
            PuckPciWrite32(bus, dev, fn, (int)cap, c & ~(1ul << 31));
            return;
        }
        cap = (c >> 8) & 0xFC; /* next capability pointer */
    }
}

/* ---------------------------------------------------------------------------------------
 * PCI BAR decode + sizing. RESERVED for the amdgpu port: a GPU exposes its registers + VRAM aperture
 * through large, often 64-bit prefetchable BARs (the aperture can sit above 4 GiB) that we decode +
 * size here and then map via PuckMapMmio. Sizing writes all-ones and reads back the implemented
 * bits, so it is destructive while in flight - only ever run it on a quiescent, driver-unbound
 * function with MEM/IO decode disabled, NEVER the live virtio NIC. (Not called yet; the cache-typed
 * MMIO map below is exercised on the LAPIC region from EfiEntry.) */
#define BAR_IO        0x1u
#define BAR_TYPE_MASK 0x6u
#define BAR_TYPE_64   0x4u
#define BAR_PREFETCH  0x8u

typedef struct PuckBar
{
    unsigned long long base, size;
    int index, isIo, is64, prefetch;
} PuckBar;

/* Decode the BAR at `index` (0..5). Returns config slots consumed (2 for 64-bit, else 1). */
static int PuckBarDecode(int bus, int dev, int fn, int index, PuckBar *out)
{
    int off = 0x10 + index * 4;
    unsigned long lo = PuckPciRead32(bus, dev, fn, off);
    out->index = index; out->base = 0; out->size = 0; out->is64 = 0; out->prefetch = 0;
    if (lo & BAR_IO) { out->isIo = 1; out->base = lo & ~0x3ul; return 1; }
    out->isIo = 0; out->prefetch = (lo & BAR_PREFETCH) ? 1 : 0;
    if ((lo & BAR_TYPE_MASK) == BAR_TYPE_64)
    {
        unsigned long hi = PuckPciRead32(bus, dev, fn, off + 4);
        out->is64 = 1;
        out->base = ((unsigned long long)hi << 32) | (unsigned long long)(lo & ~0xFul);
        return 2;
    }
    out->base = (unsigned long long)(lo & ~0xFul);
    return 1;
}

/* Size an already-decoded memory/IO BAR. CALLER must have cleared MEM+IO decode in the Command
 * register and must only call this on a quiescent function (never the live NIC). Restores the BAR. */
static void PuckBarSize(int bus, int dev, int fn, PuckBar *out)
{
    int off = 0x10 + out->index * 4;
    unsigned long save_lo = PuckPciRead32(bus, dev, fn, off);
    if (out->isIo)
    {
        unsigned long rb;
        PuckPciWrite32(bus, dev, fn, off, 0xFFFFFFFFul);
        rb = PuckPciRead32(bus, dev, fn, off) & 0xFFFFFFFCul;
        PuckPciWrite32(bus, dev, fn, off, save_lo);
        out->size = rb ? (unsigned long long)((~rb & 0xFFFFFFFFul) + 1ul) : 0;
        return;
    }
    if (out->is64)
    {
        unsigned long save_hi = PuckPciRead32(bus, dev, fn, off + 4);
        unsigned long long mask;
        PuckPciWrite32(bus, dev, fn, off, 0xFFFFFFFFul);
        PuckPciWrite32(bus, dev, fn, off + 4, 0xFFFFFFFFul);
        mask = ((unsigned long long)PuckPciRead32(bus, dev, fn, off + 4) << 32)
             | (unsigned long long)(PuckPciRead32(bus, dev, fn, off) & 0xFFFFFFF0ul);
        PuckPciWrite32(bus, dev, fn, off, save_lo);
        PuckPciWrite32(bus, dev, fn, off + 4, save_hi);
        out->size = mask ? (~mask + 1ull) : 0; /* 64-bit invert: correct for >= 4 GiB apertures */
        return;
    }
    {
        unsigned long rb;
        PuckPciWrite32(bus, dev, fn, off, 0xFFFFFFFFul);
        rb = PuckPciRead32(bus, dev, fn, off) & 0xFFFFFFF0ul;
        PuckPciWrite32(bus, dev, fn, off, save_lo);
        out->size = rb ? (unsigned long long)((~rb & 0xFFFFFFFFul) + 1ul) : 0;
    }
}

/* ---------------------------------------------------------------------------------------
 * ACPI + PCIe ECAM (memory-mapped configuration space). The legacy 0xCF8/0xCFC path above only
 * reaches the first 256 bytes of config space; a real PCIe GPU host needs the extended config
 * region (offset >= 0x100, PCIe capabilities) and clean multi-bus/multi-function enumeration.
 * ECAM's base comes from ACPI's MCFG table. We capture the RSDP pointer from the UEFI configuration
 * table BEFORE ExitBootServices (the ACPI tables themselves live in ACPI-reclaim RAM and persist,
 * inside our 0-4GiB identity map), then parse RSDP -> XSDT -> MCFG after paging is up. This is the
 * first piece of the host substrate needed to host a real GPU kernel driver (amdgpu) on hardware. */

/* EFI_SYSTEM_TABLE (x64): NumberOfTableEntries @104, ConfigurationTable* @112. */
#define EFI_ST_NUMTABLEENTRIES_OFF 104
#define EFI_ST_CONFIGTABLE_OFF     112

/* EFI_ACPI_TABLE_GUID (ACPI 2.0+, points at the XSDT-bearing RSDP): 8868e871-e4f1-11d3-bc22-0080c73c8881. */
static const unsigned char g_acpi20Guid[16] = {
    0x71, 0xe8, 0x68, 0x88, 0xf1, 0xe4, 0xd3, 0x11,
    0xbc, 0x22, 0x00, 0x80, 0xc7, 0x3c, 0x88, 0x81
};

static void              *g_acpiRsdp;       /* RSDP phys addr, captured pre-ExitBootServices */
static unsigned long long g_ecamBase;       /* ECAM MMIO base for PCI segment 0 */
static int                g_ecamStartBus, g_ecamEndBus;

/* Capture the ACPI 2.0 RSDP from the UEFI configuration table. MUST run before ExitBootServices. */
static void PuckAcpiCaptureRsdp(void *systemTable)
{
    unsigned long long n = *(unsigned long long *)((char *)systemTable + EFI_ST_NUMTABLEENTRIES_OFF);
    unsigned char *ct = (unsigned char *)EfiField(systemTable, EFI_ST_CONFIGTABLE_OFF);
    unsigned long long i;
    g_acpiRsdp = 0;
    if (!ct) return;
    for (i = 0; i < n; i++)
    {
        unsigned char *ent = ct + i * 24; /* {EFI_GUID guid[16]; void *table;} */
        int j, match = 1;
        for (j = 0; j < 16; j++)
            if (ent[j] != g_acpi20Guid[j]) { match = 0; break; }
        if (match) { g_acpiRsdp = *(void **)(ent + 16); return; }
    }
}

static int PuckAcpiSigIs(void *t, const char *sig)
{
    const unsigned char *p = (const unsigned char *)t;
    return p[0] == (unsigned char)sig[0] && p[1] == (unsigned char)sig[1]
        && p[2] == (unsigned char)sig[2] && p[3] == (unsigned char)sig[3];
}

/* Find an ACPI table by 4-char signature via RSDP -> XSDT. Returns the table pointer or 0. Run AFTER
 * paging (the identity map covers the ACPI tables). Reused for MCFG (ECAM) and IVRS/DMAR (IOMMU). */
static void *PuckAcpiFindTable(const char *sig)
{
    void *rsdp = g_acpiRsdp, *xsdt;
    unsigned int xlen, count, i;
    if (!rsdp) return 0;
    xsdt = (void *)(unsigned long long)(*(unsigned long long *)((char *)rsdp + 24)); /* RSDP.XsdtAddress */
    if (!xsdt || !PuckAcpiSigIs(xsdt, "XSDT")) return 0;
    xlen = *(unsigned int *)((char *)xsdt + 4); /* table Length */
    count = xlen >= 36 ? (xlen - 36) / 8 : 0;   /* 64-bit entry pointers follow the 36-byte header */
    for (i = 0; i < count; i++)
    {
        void *t = (void *)(unsigned long long)(*(unsigned long long *)((char *)xsdt + 36 + i * 8));
        if (t && PuckAcpiSigIs(t, sig)) return t;
    }
    return 0;
}

/* Record the ECAM base + bus range from MCFG. Returns 0 on success. */
static int PuckInitEcam(void)
{
    void *mcfg = PuckAcpiFindTable("MCFG");
    if (!mcfg) { SerialPuts("[pcie] no MCFG table (no ACPI ECAM)\r\n"); return -1; }
    /* First allocation structure @44: base(u64)@44, segment(u16)@52, startBus@54, endBus@55. We use
     * entry 0 and assume it is PCI segment 0 (true on QEMU q35 and every single-segment x86 target,
     * incl. the Steam Deck); a multi-segment firmware would need a scan over the structures. */
    g_ecamBase = *(unsigned long long *)((char *)mcfg + 44);
    g_ecamStartBus = *(unsigned char *)((char *)mcfg + 54);
    g_ecamEndBus = *(unsigned char *)((char *)mcfg + 55);
    return 0;
}

static volatile unsigned int *PuckEcamPtr(int bus, int dev, int fn, int off)
{
    return (volatile unsigned int *)(g_ecamBase
        + ((unsigned long long)(bus - g_ecamStartBus) << 20)
        + ((unsigned long long)dev << 15)
        + ((unsigned long long)fn << 12)
        + (unsigned long long)(off & ~3));
}
static unsigned int PuckEcamRead32(int bus, int dev, int fn, int off)
{
    return *PuckEcamPtr(bus, dev, fn, off);
}

static void EcamPutHex(unsigned long long v, int nibbles)
{
    const char *hx = "0123456789abcdef";
    int i;
    SerialPuts("0x");
    for (i = nibbles - 1; i >= 0; i--) SerialWriteByte(hx[(v >> (i * 4)) & 0xF]);
}
static void EcamPutDec(unsigned int v)
{
    char tmp[12]; int t = 0;
    if (v == 0) { SerialWriteByte('0'); return; }
    while (v) { tmp[t++] = (char)('0' + (v % 10)); v /= 10; }
    while (t > 0) SerialWriteByte(tmp[--t]);
}

/* Bring up PCIe ECAM (MMIO config space) from ACPI MCFG and log the window. PuckEcamRead32 then
 * reaches the extended config region (offset >= 0x100) the legacy 0xCF8 path can't, which the GPU
 * host needs on real hardware. */
void PuckEcamBringUp(void)
{
    if (PuckInitEcam() != 0) return;
    SerialPuts("[pcie] ECAM online, base="); EcamPutHex(g_ecamBase, 9);
    SerialPuts(" buses "); EcamPutDec((unsigned int)g_ecamStartBus);
    SerialPuts("-"); EcamPutDec((unsigned int)g_ecamEndBus); SerialPuts("\r\n");
}

/* ---------------------------------------------------------------------------------------
 * DMA-coherent memory + IOMMU detection. A GPU lives on DMA: command rings, GTT page tables, the
 * GSP/microcode upload regions, fence/seqno words - all system RAM the device reads/writes by
 * physical address (an APU like the Steam Deck has NO dedicated VRAM; it's all system memory via a
 * UMA carveout + GTT). PuckDmaAlloc hands out contiguous, zeroed, aligned regions and returns
 * BOTH the CPU pointer and the device-visible physical address. Under our identity map they are
 * equal today; the explicit phys keeps callers correct once an IOMMU or above-4GiB memory makes them
 * diverge. Permanent (no free), like the page allocator it sits on. */
typedef struct PuckDmaBuf
{
    void              *cpu;   /* CPU virtual address */
    unsigned long long phys;  /* device-visible (bus) physical address */
    unsigned long long size;  /* requested size in bytes */
} PuckDmaBuf;

static PuckDmaBuf PuckDmaAlloc(unsigned long long size, unsigned long long align)
{
    PuckDmaBuf b;
    unsigned long long pages, raw, aligned;
    if (align < 4096ull) align = 4096ull;                 /* page-granular floor */
    pages = (size + (align - 1) + 4095ull) / 4096ull;     /* room to align up by (align-1) plus size */
    raw = PuckAllocPages(pages);
    aligned = (raw + (align - 1)) & ~(align - 1);
    b.cpu = (void *)aligned;
    b.phys = aligned;                                     /* identity map: device phys == CPU virtual */
    b.size = size;
    return b;
}

/* Detect an IOMMU from ACPI (AMD-Vi = "IVRS", Intel VT-d = "DMAR"). An APU like the Steam Deck has
 * AMD-Vi; if firmware leaves it translating, it gates GPU DMA and we'll need a passthrough/identity
 * domain. QEMU q35 here has neither table -> direct DMA. Report presence + base for now. */
static void PuckDetectIommu(void)
{
    void *ivrs = PuckAcpiFindTable("IVRS");
    void *dmar = PuckAcpiFindTable("DMAR");
    if (ivrs)
    {
        /* First IVHD block @48; its IOMMU MMIO base address is at block offset +0x08 (u64). */
        unsigned long long mmio = *(unsigned long long *)((char *)ivrs + 48 + 0x08);
        SerialPuts("[dma] IOMMU: AMD-Vi (IVRS) present, MMIO base="); EcamPutHex(mmio, 9);
        SerialPuts(" -- needs a passthrough domain on real HW\r\n");
    }
    else if (dmar)
    {
        SerialPuts("[dma] IOMMU: Intel VT-d (DMAR) present -- needs a passthrough domain\r\n");
    }
    else
    {
        SerialPuts("[dma] no IOMMU (no IVRS/DMAR) -- direct/identity DMA\r\n");
    }
}

#define VIRTIO_PCI_DEVICE_FEATURES 0x00
#define VIRTIO_PCI_DRIVER_FEATURES 0x04
#define VIRTIO_PCI_QUEUE_PFN       0x08
#define VIRTIO_PCI_QUEUE_SIZE      0x0C
#define VIRTIO_PCI_QUEUE_SEL       0x0E
#define VIRTIO_PCI_QUEUE_NOTIFY    0x10
#define VIRTIO_PCI_STATUS          0x12
#define VIRTIO_PCI_ISR             0x13 /* r/o ISR status (read-to-ack), legacy INTx path        */
#define VIRTIO_PCI_CONFIG          0x14 /* device-specific config (MAC) with MSI-X disabled       */
#define VIRTIO_MSI_CONFIG_VECTOR   0x14 /* config-change MSI-X vector (present only when MSI-X on) */
#define VIRTIO_MSI_QUEUE_VECTOR    0x16 /* selected-queue MSI-X vector (present only when MSI-X on)*/
#define VIRTIO_MSI_NO_VECTOR       0xFFFF
#define VIRTIO_STATUS_ACK       1
#define VIRTIO_STATUS_DRIVER    2
#define VIRTIO_STATUS_DRIVER_OK 4
#define VIRTIO_NET_F_MAC        (1ul << 5)
#define VIRTIO_NET_HDR_LEN      10
#define VRING_DESC_F_NEXT  1
#define VRING_DESC_F_WRITE 2

#pragma pack(push, 1)
typedef struct { unsigned long long addr; unsigned int len; unsigned short flags; unsigned short next; } PuckVringDesc;
typedef struct { unsigned short flags; unsigned short idx; unsigned short ring[256]; } PuckVringAvail;
typedef struct { unsigned int id; unsigned int len; } PuckVringUsedElem;
typedef struct { unsigned short flags; unsigned short idx; PuckVringUsedElem ring[256]; } PuckVringUsed;
#pragma pack(pop)

typedef struct
{
    unsigned short     ioBase;
    unsigned short     qSize;
    PuckVringDesc  *desc;
    PuckVringAvail *avail;
    PuckVringUsed  *used;
    unsigned short     lastUsed;
    unsigned char     *buffers; /* qSize * 2048 contiguous buffers */
} PuckVirtq;

static unsigned short g_netIoBase;
unsigned char g_netMac[6];
static int g_netBus, g_netDev; /* stashed at init so the MSI-X proof needn't re-scan the bus */
static PuckVirtq g_netRx, g_netTx;

static void PuckVirtqSetup(PuckVirtq *vq, unsigned short ioBase, unsigned short sel)
{
    unsigned int descBytes, availBytes, usedOff, totalBytes;
    PuckDmaBuf ring, bufs;

    __outword((unsigned short)(ioBase + VIRTIO_PCI_QUEUE_SEL), sel);
    vq->ioBase = ioBase;
    vq->qSize = __inword((unsigned short)(ioBase + VIRTIO_PCI_QUEUE_SIZE));
    vq->lastUsed = 0;

    /* Legacy split vring: desc | avail | pad-to-4KiB | used, all in one page-aligned region. The
     * device DMAs the ring + buffers by physical address, so both come from the DMA allocator and the
     * device is handed ring.phys (== ring.cpu under our identity map; explicit for the IOMMU future). */
    descBytes = (unsigned int)vq->qSize * 16u;
    availBytes = 6u + (unsigned int)vq->qSize * 2u;
    usedOff = (descBytes + availBytes + 4095u) & ~4095u;
    totalBytes = usedOff + 6u + (unsigned int)vq->qSize * 8u;

    ring = PuckDmaAlloc(totalBytes, 4096);
    bufs = PuckDmaAlloc((unsigned long long)vq->qSize * 2048ull, 4096);
    vq->desc = (PuckVringDesc *)ring.cpu;
    vq->avail = (PuckVringAvail *)((unsigned char *)ring.cpu + descBytes);
    vq->used = (PuckVringUsed *)((unsigned char *)ring.cpu + usedOff);
    vq->buffers = (unsigned char *)bufs.cpu;

    __outdword((unsigned short)(ioBase + VIRTIO_PCI_QUEUE_PFN), (unsigned long)(ring.phys >> 12));
}

/* Post an Ethernet frame for transmit (prefixed with a zeroed virtio-net header), kick, and wait
 * for the device to consume it. Synchronous - fine for the low rates we drive here. */
void PuckNetTx(const unsigned char *frame, unsigned int len)
{
    PuckVirtq *vq = &g_netTx;
    unsigned char *buf = vq->buffers; /* slot 0 */
    unsigned int i;
    unsigned short head;

    for (i = 0; i < VIRTIO_NET_HDR_LEN; i++) buf[i] = 0;
    for (i = 0; i < len; i++) buf[VIRTIO_NET_HDR_LEN + i] = frame[i];

    vq->desc[0].addr = (unsigned long long)buf;
    vq->desc[0].len = VIRTIO_NET_HDR_LEN + len;
    vq->desc[0].flags = 0;
    vq->desc[0].next = 0;

    head = vq->avail->idx;
    vq->avail->ring[head % vq->qSize] = 0;
    vq->avail->idx = (unsigned short)(head + 1);
    __outword((unsigned short)(vq->ioBase + VIRTIO_PCI_QUEUE_NOTIFY), 1);

    while (vq->used->idx == vq->lastUsed) /* spin until consumed */
        ;
    vq->lastUsed = vq->used->idx;
}

/* Poll RX: if a frame arrived, copy it (sans the virtio-net header) into out and recycle the buffer.
 * Returns the Ethernet frame length, or 0 if nothing is ready. */
unsigned int PuckNetRxPoll(unsigned char *out, unsigned int maxLen)
{
    PuckVirtq *vq = &g_netRx;
    PuckVringUsedElem *e;
    unsigned int id, len, i;

    if (vq->used->idx == vq->lastUsed)
        return 0;

    e = &vq->used->ring[vq->lastUsed % vq->qSize];
    id = e->id;
    len = e->len;
    if (len > VIRTIO_NET_HDR_LEN)
    {
        len -= VIRTIO_NET_HDR_LEN;
        if (len > maxLen) len = maxLen;
        for (i = 0; i < len; i++) out[i] = vq->buffers[id * 2048u + VIRTIO_NET_HDR_LEN + i];
    }
    else
    {
        len = 0;
    }

    /* recycle the descriptor back into the avail ring */
    vq->avail->ring[vq->avail->idx % vq->qSize] = (unsigned short)id;
    vq->avail->idx = (unsigned short)(vq->avail->idx + 1);
    __outword((unsigned short)(vq->ioBase + VIRTIO_PCI_QUEUE_NOTIFY), 0);
    vq->lastUsed = (unsigned short)(vq->lastUsed + 1);
    return len;
}

int PuckVirtioNetInit(void)
{
    int bus, dev, found = -1, fbus = 0, fdev = 0;
    unsigned long bar0, features;
    unsigned short io;
    unsigned int i;

    for (bus = 0; bus < 256 && found < 0; bus++)
        for (dev = 0; dev < 32; dev++)
        {
            unsigned long id = PuckPciRead32(bus, dev, 0, 0x00);
            if ((id & 0xFFFF) == 0x1AF4 && ((id >> 16) & 0xFFFF) == 0x1000)
            {
                fbus = bus; fdev = dev; found = 0;
                g_netBus = bus; g_netDev = dev;
                break;
            }
        }
    if (found < 0)
    {
        SerialPuts("[net] no virtio-net device found\r\n");
        return -1;
    }

    /* Enable I/O space + bus mastering. */
    PuckPciWrite32(fbus, fdev, 0, 0x04, PuckPciRead32(fbus, fdev, 0, 0x04) | 0x05);

    /* Force MSI-X off so the legacy register layout matches VIRTIO_PCI_CONFIG (0x14). Critical when a
     * prior UEFI driver drove this NIC first - e.g. iPXE during a PXE network boot. */
    PuckPciDisableMsix(fbus, fdev, 0);

    bar0 = PuckPciRead32(fbus, fdev, 0, 0x10);
    if (!(bar0 & 0x1ul)) /* BAR0 must be an I/O-space BAR for the legacy PIO path */
    {
        SerialPuts("[net] virtio BAR0 is MMIO (modern-only); legacy PIO driver can't bind\r\n");
        return -1;
    }
    io = (unsigned short)(bar0 & ~0x3ul);
    g_netIoBase = io;

    /* Reset, then ACKNOWLEDGE + DRIVER. Spin until the device acknowledges the reset (status reads
     * back 0), honoring the virtio reset-completion contract before we reprogram it. */
    __outbyte((unsigned short)(io + VIRTIO_PCI_STATUS), 0);
    for (i = 0; i < 1000000u && __inbyte((unsigned short)(io + VIRTIO_PCI_STATUS)) != 0; i++)
        ;
    __outbyte((unsigned short)(io + VIRTIO_PCI_STATUS), VIRTIO_STATUS_ACK);
    __outbyte((unsigned short)(io + VIRTIO_PCI_STATUS), VIRTIO_STATUS_ACK | VIRTIO_STATUS_DRIVER);

    /* Negotiate: we only want the MAC. */
    features = __indword((unsigned short)(io + VIRTIO_PCI_DEVICE_FEATURES));
    __outdword((unsigned short)(io + VIRTIO_PCI_DRIVER_FEATURES), features & VIRTIO_NET_F_MAC);

    for (i = 0; i < 6; i++)
        g_netMac[i] = __inbyte((unsigned short)(io + VIRTIO_PCI_CONFIG + i));

    /* RX = queue 0, TX = queue 1. */
    PuckVirtqSetup(&g_netRx, io, 0);
    PuckVirtqSetup(&g_netTx, io, 1);

    /* Post RX buffers (device writes into them). */
    for (i = 0; i < g_netRx.qSize && i < 64; i++)
    {
        g_netRx.desc[i].addr = (unsigned long long)(g_netRx.buffers + i * 2048u);
        g_netRx.desc[i].len = 2048;
        g_netRx.desc[i].flags = VRING_DESC_F_WRITE;
        g_netRx.desc[i].next = 0;
        g_netRx.avail->ring[i] = (unsigned short)i;
    }
    g_netRx.avail->idx = (unsigned short)(i < g_netRx.qSize ? i : g_netRx.qSize);
    __outword((unsigned short)(io + VIRTIO_PCI_QUEUE_NOTIFY), 0);

    __outbyte((unsigned short)(io + VIRTIO_PCI_STATUS),
              VIRTIO_STATUS_ACK | VIRTIO_STATUS_DRIVER | VIRTIO_STATUS_DRIVER_OK);

    SerialPuts("[net] virtio-net up, MAC ");
    for (i = 0; i < 6; i++)
    {
        const char *hx = "0123456789abcdef";
        if (i) SerialWriteByte(':');
        SerialWriteByte(hx[g_netMac[i] >> 4]);
        SerialWriteByte(hx[g_netMac[i] & 0xF]);
    }
    SerialPuts("\r\n");
    return 0;
}

#define LINUX_ARCH_SET_GS 0x1001
#define LINUX_ARCH_SET_FS 0x1002
#define LINUX_ARCH_GET_FS 0x1003
#define LINUX_ARCH_GET_GS 0x1004
#define MSR_FS_BASE 0xC0000100u
#define MSR_GS_BASE 0xC0000101u

/* SysV iovec, as writev sees it. */
typedef struct { void *iov_base; unsigned long long iov_len; } PuckIoVec;

/* Signed decimal to serial (for the unknown-syscall log + exit code). */
static void SerialPutI64(long long v)
{
    unsigned char tmp[24];
    int t = 0;
    unsigned long long u;

    if (v < 0) { SerialWriteByte('-'); u = (unsigned long long)(-v); }
    else u = (unsigned long long)v;

    if (u == 0) { SerialWriteByte('0'); return; }
    while (u) { tmp[t++] = (unsigned char)('0' + (u % 10)); u /= 10; }
    while (t > 0) SerialWriteByte(tmp[--t]);
}

/* ---------------------------------------------------------------------------------------
 * Synthetic VFS. A tiny read-only namespace (a few /proc + /dev nodes) backing open/read/close/
 * lseek, so a real guest's file I/O works. This is the on-ramp to exposing real device nodes
 * (e.g. /dev/dri/cardN for DRM) once we piggyback on a Linux driver stack. */
#define PUCK_SF_STATIC 0
#define PUCK_SF_ZERO   1
#define PUCK_SF_NULL   2
#define PUCK_SF_RANDOM 3
#define PUCK_SF_DRM    4 /* /dev/dri/renderD128: ioctl-driven (DRM), the GPU userspace seam */
#define PUCK_SF_DIR    5 /* directory: enumerable via getdents64 (children derived from the table) */
#define PUCK_SF_LINK   6 /* symlink: content = target string, resolved via readlink(at) */

typedef struct { const char *path; const char *content; unsigned long long len; int kind; } PuckSynthFile;

static const char g_puckCpuinfo[] =
    "processor\t: 0\n"
    "vendor_id\t: PuckBareMetal\n"
    "model name\t: Puck UEFI ring-3 Linux host\n"
    "flags\t\t: fpu tsc msr\n\n";
static const char g_puckMeminfo[] =
    "MemTotal:         524288 kB\n"
    "MemFree:          393216 kB\n";
/* The PCI device's sysfs uevent, as libdrm's drmParsePciDeviceInfo expects (PCI_SLOT_NAME + PCI_ID). */
static const char g_puckPciUevent[] =
    "DRIVER=amdgpu\n"
    "PCI_CLASS=30000\n"
    "PCI_ID=1002:163F\n"
    "PCI_SUBSYS_ID=0000:0000\n"
    "PCI_SLOT_NAME=0000:00:01.0\n"
    "MODALIAS=pci:v00001002d0000163Fsv00000000sd00000000bc03sc00i00\n";

static PuckSynthFile g_puckSynthFiles[] = {
    { "/proc/cpuinfo", g_puckCpuinfo, sizeof(g_puckCpuinfo) - 1, PUCK_SF_STATIC },
    { "/proc/meminfo", g_puckMeminfo, sizeof(g_puckMeminfo) - 1, PUCK_SF_STATIC },
    { "/dev/zero",     0, 0, PUCK_SF_ZERO },
    { "/dev/null",     0, 0, PUCK_SF_NULL },
    { "/dev/urandom",  0, 0, PUCK_SF_RANDOM },
    { "/dev/dri",            0, 0, PUCK_SF_DIR }, /* enumerated by drmGetDevices2 via getdents64 */
    { "/dev/dri/renderD128", 0, 0, PUCK_SF_DRM }, /* GPU render node -> amdgpu (RADV's seam) */
    /* The /sys walk drmGetDevices2 does: render node -> char-dev symlink -> PCI device -> ids. The
     * synthetic PCI device 0000:00:01.0 reports the Van Gogh ids (vendor 0x1002, device 0x163F). */
    { "/sys/dev/char/226:128", "../../devices/pci0000:00/0000:00:01.0/drm/renderD128", 0, PUCK_SF_LINK },
    /* Intermediate directories the libdrm walk stats/opendirs after symlink resolution. */
    { "/sys/devices/pci0000:00/0000:00:01.0",                    0, 0, PUCK_SF_DIR },
    { "/sys/devices/pci0000:00/0000:00:01.0/drm",                0, 0, PUCK_SF_DIR },
    { "/sys/devices/pci0000:00/0000:00:01.0/drm/renderD128",     0, 0, PUCK_SF_DIR },
    { "/sys/devices/pci0000:00/0000:00:01.0/drm/renderD128/device", "../../../0000:00:01.0", 0, PUCK_SF_LINK },
    { "/sys/devices/pci0000:00/0000:00:01.0/drm/renderD128/dev", "226:128\n", 8, PUCK_SF_STATIC },
    /* The bus classifier: drmParseSubsystemType readlinks this; only its basename ("pci") matters. */
    { "/sys/devices/pci0000:00/0000:00:01.0/subsystem", "../../../bus/pci", 0, PUCK_SF_LINK },
    /* PCI identity files libdrm's drmParsePciDeviceInfo reads (uevent first, then the id files). */
    { "/sys/devices/pci0000:00/0000:00:01.0/uevent", g_puckPciUevent, sizeof(g_puckPciUevent) - 1, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/class",    "0x030000\n", 9, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/vendor",   "0x1002\n", 7, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/device",   "0x163f\n", 7, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/revision", "0xae\n",   5, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/subsystem_vendor", "0x0000\n", 7, PUCK_SF_STATIC },
    { "/sys/devices/pci0000:00/0000:00:01.0/subsystem_device", "0x0000\n", 7, PUCK_SF_STATIC },
};

#define PUCK_MAX_FDS 64
typedef struct { int used; int fileIndex; unsigned long long offset; } PuckFd;
static PuckFd g_puckFds[PUCK_MAX_FDS]; /* fds 0-2 are stdio; synthetic files use 3+ */

static int PuckStrEq(const char *a, const char *b)
{
    while (*a && *b) { if (*a != *b) return 0; a++; b++; }
    return *a == *b;
}

static int PuckStrLen(const char *s) { int n = 0; while (s[n]) n++; return n; }

/* First n chars of a equal b[0..n)? (b may be shorter only if it has a NUL inside n, treated unequal) */
static int PuckStrNEq(const char *a, const char *b, int n)
{
    int i;
    for (i = 0; i < n; i++) { if (a[i] != b[i]) return 0; if (a[i] == 0) return 0; }
    return 1;
}

#define PUCK_SYNTH_COUNT ((int)(sizeof(g_puckSynthFiles) / sizeof(g_puckSynthFiles[0])))

/* Dynamically-registered files. The managed loader adds a dynamic guest's shared-library closure
 * here (so ld-musl can open/read/mmap each NEEDED .so). They occupy the index range above the static
 * table in one unified VFS index space, so all the VFS code below works unchanged via PuckSynthGet. */
#define PUCK_MAX_DYNFILES 16
static PuckSynthFile g_puckDynFiles[PUCK_MAX_DYNFILES];
static char g_puckDynPaths[PUCK_MAX_DYNFILES][96];
static int g_puckDynFileCount;

/* Register a file in the VFS. Called from the loader (DllImport) before entering a dynamic guest.
 * The path is copied (the marshalled string is transient); `content` is a permanent bump-heap blob. */
void PuckVfsAddFile(const char *path, const void *content, unsigned long long len)
{
    int i, j;
    if (g_puckDynFileCount >= PUCK_MAX_DYNFILES) return;
    i = g_puckDynFileCount++;
    for (j = 0; path[j] && j < 95; j++) g_puckDynPaths[i][j] = path[j];
    g_puckDynPaths[i][j] = 0;
    g_puckDynFiles[i].path = g_puckDynPaths[i];
    g_puckDynFiles[i].content = (const char *)content;
    g_puckDynFiles[i].len = len;
    g_puckDynFiles[i].kind = PUCK_SF_STATIC;
}

static int PuckSynthTotal(void) { return PUCK_SYNTH_COUNT + g_puckDynFileCount; }
static PuckSynthFile *PuckSynthGet(int idx)
{
    return (idx < PUCK_SYNTH_COUNT) ? &g_puckSynthFiles[idx]
                                       : &g_puckDynFiles[idx - PUCK_SYNTH_COUNT];
}

/* Look up a file by exact path (dynamic entries take precedence); -1 if absent. */
static int PuckSynthFind(const char *path)
{
    int i;
    for (i = 0; i < PuckSynthTotal(); i++)
        if (PuckStrEq(path, PuckSynthGet(i)->path)) return i;
    return -1;
}

/* Append the path fragment `frag` onto the absolute path being built in out[0..*outlen), resolving
 * "." and ".." against the component stack already there (kernel-style normalization). */
static void PuckPathNormInto(char *out, int *outlen, const char *frag, int cap)
{
    const char *p = frag;
    while (*p)
    {
        const char *start;
        int clen, i;
        while (*p == '/') p++;
        if (!*p) break;
        start = p;
        while (*p && *p != '/') p++;
        clen = (int)(p - start);
        if (clen == 1 && start[0] == '.')
            continue;                                  /* "." -> no-op */
        if (clen == 2 && start[0] == '.' && start[1] == '.')
        {                                              /* ".." -> pop last component */
            while (*outlen > 1 && out[*outlen - 1] != '/') (*outlen)--;
            if (*outlen > 1) (*outlen)--;
            continue;
        }
        if (*outlen == 0 || out[*outlen - 1] != '/')
            { if (*outlen < cap - 1) out[(*outlen)++] = '/'; }
        for (i = 0; i < clen && *outlen < cap - 1; i++) out[(*outlen)++] = start[i];
    }
    out[*outlen < cap ? *outlen : cap - 1] = 0;
}

/* Canonicalize `path` into `buf`, resolving symlink components against the synthetic VFS the way the
 * kernel would (libdrm/RADV stat paths *through* the /sys/dev/char/MAJ:MIN symlinks and expect the
 * resolution to happen for them). followFinal=0 leaves a trailing symlink unresolved (lstat/readlink
 * semantics); =1 resolves it too (stat/open). Bounded iterations guard against link cycles. */
static const char *PuckResolvePath(const char *path, char *buf, int cap, int followFinal)
{
    int len = 0, iter;
    buf[0] = 0;
    PuckPathNormInto(buf, &len, path, cap);
    for (iter = 0; iter < 40; iter++)
    {
        int bestIdx = -1, bestLen = -1, i;
        for (i = 0; i < PuckSynthTotal(); i++)
        {
            PuckSynthFile *sf = PuckSynthGet(i);
            int pl;
            if (sf->kind != PUCK_SF_LINK) continue;
            pl = PuckStrLen(sf->path);
            if (PuckStrNEq(buf, sf->path, pl) && (buf[pl] == 0 || buf[pl] == '/'))
            {
                if (buf[pl] == 0 && !followFinal) continue; /* don't resolve a trailing symlink */
                if (pl > bestLen) { bestLen = pl; bestIdx = i; }
            }
        }
        if (bestIdx < 0) break;
        {
            PuckSynthFile *link = PuckSynthGet(bestIdx);
            char tmp[256], parent[256];
            int tl = 0, pj = 0, lastSlash = 0, j, lp = PuckStrLen(link->path);
            for (j = 0; j < lp; j++) if (link->path[j] == '/') lastSlash = j;
            for (j = 0; j < lastSlash; j++) parent[pj++] = link->path[j];
            parent[pj] = 0;
            PuckPathNormInto(tmp, &tl, parent, (int)sizeof tmp);       /* link's parent dir */
            PuckPathNormInto(tmp, &tl, link->content, (int)sizeof tmp); /* + (relative) target */
            PuckPathNormInto(tmp, &tl, buf + bestLen, (int)sizeof tmp); /* + remainder after link */
            len = 0; buf[0] = 0;
            PuckPathNormInto(buf, &len, tmp, cap);
        }
    }
    return buf;
}

/* True if `path` is a proper ancestor directory of some table entry (so it behaves like a directory
 * even without an explicit entry), e.g. ".../0000:00:01.0/drm" above ".../drm/renderD128/dev". */
static int PuckIsImplicitDir(const char *path)
{
    int pl = PuckStrLen(path), i;
    if (pl == 0) return 0;
    for (i = 0; i < PuckSynthTotal(); i++)
    {
        const char *e = PuckSynthGet(i)->path;
        if (PuckStrNEq(e, path, pl) && e[pl] == '/') return 1;
    }
    return 0;
}

/* Resolve symlinks in `path`, then find it. Returns the table index, or -1. When -1, *isDir reports
 * whether the resolved path is (at least) an implicit directory. */
static int PuckSynthFindPath(const char *path, int followFinal, int *isDir)
{
    char buf[256];
    const char *r = PuckResolvePath(path, buf, (int)sizeof buf, followFinal);
    int fi = PuckSynthFind(r);
    if (isDir) *isDir = (fi < 0) ? PuckIsImplicitDir(r) : (PuckSynthGet(fi)->kind == PUCK_SF_DIR);
    return fi;
}

static long long PuckSynthOpen(const char *path)
{
    int fi = PuckSynthFindPath(path, 1, 0), fd; /* open() follows symlinks */
    if (fi < 0)
    {
        SerialPuts("[vfs] ENOENT open(");
        SerialPuts(path);
        SerialPuts(")\r\n");
        return LINUX_ENOENT;
    }
    for (fd = 3; fd < PUCK_MAX_FDS; fd++)
        if (!g_puckFds[fd].used)
        {
            g_puckFds[fd].used = 1;
            g_puckFds[fd].fileIndex = fi;
            g_puckFds[fd].offset = 0;
            return fd;
        }
    return LINUX_EMFILE;
}

static long long PuckSynthRead(int fd, unsigned char *buf, unsigned long long count)
{
    PuckSynthFile *sf;
    unsigned long long i, avail;
    unsigned long long r;

    if (fd == 0) return 0; /* stdin: EOF */
    if (fd < 3 || fd >= PUCK_MAX_FDS || !g_puckFds[fd].used) return LINUX_EBADF;
    sf = PuckSynthGet(g_puckFds[fd].fileIndex);

    switch (sf->kind)
    {
        case PUCK_SF_ZERO:
            for (i = 0; i < count; i++) buf[i] = 0;
            return (long long)count;
        case PUCK_SF_NULL:
            return 0;
        case PUCK_SF_RANDOM:
            for (i = 0; i < count;)
            {
                int j;
                r = PuckRdRand64();
                for (j = 0; j < 8 && i < count; j++) buf[i++] = (unsigned char)(r >> (j * 8));
            }
            return (long long)count;
        default: /* static content */
            avail = sf->len - g_puckFds[fd].offset;
            if (avail > count) avail = count;
            for (i = 0; i < avail; i++) buf[i] = ((const unsigned char *)sf->content)[g_puckFds[fd].offset + i];
            g_puckFds[fd].offset += avail;
            return (long long)avail;
    }
}

static long long PuckSynthLseek(int fd, long long offset, int whence)
{
    long long len, neu;
    if (fd < 3 || fd >= PUCK_MAX_FDS || !g_puckFds[fd].used) return LINUX_EBADF;
    len = (long long)PuckSynthGet(g_puckFds[fd].fileIndex)->len;
    if (whence == 0) neu = offset;                                  /* SEEK_SET */
    else if (whence == 1) neu = (long long)g_puckFds[fd].offset + offset; /* SEEK_CUR */
    else if (whence == 2) neu = len + offset;                       /* SEEK_END */
    else return LINUX_EINVAL;
    if (neu < 0) neu = 0;
    if (PuckSynthGet(g_puckFds[fd].fileIndex)->kind == PUCK_SF_STATIC && neu > len) neu = len;
    g_puckFds[fd].offset = (unsigned long long)neu;
    return neu;
}

static long long PuckSynthClose(int fd)
{
    if (fd >= 3 && fd < PUCK_MAX_FDS && g_puckFds[fd].used) g_puckFds[fd].used = 0;
    return 0;
}

static int PuckFdFileIndex(int fd)
{
    if (fd >= 3 && fd < PUCK_MAX_FDS && g_puckFds[fd].used) return g_puckFds[fd].fileIndex;
    return -1;
}

/* Map a synthetic file's kind to a Linux st_mode (+ rdev/size), the way drmGetDevices2's fstat needs:
 * the render node is a char device with rdev 226:128, sysfs entries are regular files, /dev/dri is a
 * directory, the /sys cross-links are symlinks. */
static int PuckKindMode(int fi, unsigned long long *rdev, unsigned long long *size)
{
    int kind = (fi >= 0) ? PuckSynthGet(fi)->kind : -1;
    *rdev = 0; *size = 0;
    switch (kind)
    {
        case PUCK_SF_DRM:    *rdev = (226u << 8) | 128u; return 0020666; /* S_IFCHR | 0666 */
        case PUCK_SF_ZERO:
        case PUCK_SF_NULL:
        case PUCK_SF_RANDOM: return 0020666;                            /* S_IFCHR */
        case PUCK_SF_DIR:    return 0040755;                            /* S_IFDIR */
        case PUCK_SF_LINK:   *size = PuckStrLen(PuckSynthGet(fi)->content); return 0120777; /* S_IFLNK */
        default:                *size = (fi >= 0) ? PuckSynthGet(fi)->len : 0; return 0100444; /* S_IFREG */
    }
}

/* A fixed, nonzero synthetic block-device id shared by every file in the VFS. */
#define PUCK_SYNTH_DEV 0x1Aull

/* x86-64 struct stat: st_dev@0, st_ino@8, st_nlink@16, st_mode@24, st_rdev@40, st_size@48,
 * st_blksize@56 (144 bytes). st_dev/st_ino MUST be unique-and-stable per file: ld-musl's map_library
 * fstat()s each shared library and dedups by (st_dev, st_ino) -- if they all read back zero, it folds
 * every dependency into the first-loaded DSO and never maps the rest (their symbols vanish). */
static void PuckFillStat(int fi, unsigned char *st)
{
    unsigned long long rdev, size;
    int mode = PuckKindMode(fi, &rdev, &size), i;
    for (i = 0; i < 144; i++) st[i] = 0;
    *(unsigned long long *)(st + 0)  = PUCK_SYNTH_DEV;
    *(unsigned long long *)(st + 8)  = (fi >= 0) ? (unsigned long long)(fi + 1) : 0; /* st_ino */
    *(unsigned long long *)(st + 16) = 1;
    *(unsigned int *)(st + 24) = (unsigned int)mode;
    *(unsigned long long *)(st + 40) = rdev;
    *(unsigned long long *)(st + 48) = size;
    *(unsigned long long *)(st + 56) = 4096;
}

/* struct statx: stx_mask@0, stx_blksize@4, stx_nlink@16, stx_mode@28(u16), stx_ino@32, stx_size@40,
 * stx_rdev_major@128, stx_rdev_minor@132, stx_dev_major@152, stx_dev_minor@156 (256 bytes). The
 * (dev, ino) identity mirrors PuckFillStat for the same dedup reason. */
static void PuckFillStatx(int fi, unsigned char *sx)
{
    unsigned long long rdev, size;
    int mode = PuckKindMode(fi, &rdev, &size), i;
    for (i = 0; i < 256; i++) sx[i] = 0;
    *(unsigned int *)(sx + 0) = 0x000007FFu;   /* STATX_BASIC_STATS */
    *(unsigned int *)(sx + 4) = 4096;
    *(unsigned int *)(sx + 16) = 1;
    *(unsigned short *)(sx + 28) = (unsigned short)mode;
    *(unsigned long long *)(sx + 32) = (fi >= 0) ? (unsigned long long)(fi + 1) : 0; /* stx_ino */
    *(unsigned long long *)(sx + 40) = size;
    *(unsigned int *)(sx + 128) = (unsigned int)(rdev >> 8);
    *(unsigned int *)(sx + 132) = (unsigned int)(rdev & 0xFF);
    *(unsigned int *)(sx + 152) = 0;                          /* stx_dev_major */
    *(unsigned int *)(sx + 156) = (unsigned int)PUCK_SYNTH_DEV; /* stx_dev_minor */
}

/* Fill a struct stat for an implicit directory (an ancestor path with no explicit table entry). */
static void PuckFillStatDir(unsigned char *st)
{
    int i;
    for (i = 0; i < 144; i++) st[i] = 0;
    *(unsigned long long *)(st + 0)  = PUCK_SYNTH_DEV;
    *(unsigned long long *)(st + 8)  = 0xD17;          /* a stable nonzero ino for synthetic dirs */
    *(unsigned long long *)(st + 16) = 2;              /* st_nlink */
    *(unsigned int *)(st + 24) = 0040755;              /* S_IFDIR | 0755 */
    *(unsigned long long *)(st + 56) = 4096;
}

/* If `path` is an immediate child of `dir` (one path component below it), return the child name. */
static const char *PuckImmediateChildName(const char *dir, const char *path)
{
    int dl = PuckStrLen(dir), i;
    const char *name, *s;
    for (i = 0; i < dl; i++) if (path[i] != dir[i]) return 0;
    if (path[dl] != '/') return 0;
    name = path + dl + 1;
    if (!*name) return 0;
    for (s = name; *s; s++) if (*s == '/') return 0;
    return name;
}

/* getdents64 over a synthetic directory: emit one linux_dirent64 per immediate child. Single-shot
 * (offset!=0 -> EOF), which suffices for the small synthetic dirs drmGetDevices2 scans. */
static long long PuckGetdents64(int fd, unsigned char *buf, unsigned long long count)
{
    int dirfi, i;
    const char *dirpath;
    unsigned long long off = 0;
    if (fd < 3 || fd >= PUCK_MAX_FDS || !g_puckFds[fd].used) return LINUX_EBADF;
    dirfi = g_puckFds[fd].fileIndex;
    if (PuckSynthGet(dirfi)->kind != PUCK_SF_DIR) return LINUX_ENOTDIR;
    if (g_puckFds[fd].offset != 0) return 0; /* already enumerated */
    dirpath = PuckSynthGet(dirfi)->path;
    for (i = 0; i < PuckSynthTotal(); i++)
    {
        const char *name = PuckImmediateChildName(dirpath, PuckSynthGet(i)->path);
        int namelen, reclen, j, kind;
        unsigned char *d;
        if (!name) continue;
        namelen = PuckStrLen(name);
        reclen = (19 + namelen + 1 + 7) & ~7; /* linux_dirent64 header is 19 bytes; align to 8 */
        if (off + (unsigned long long)reclen > count) break;
        d = buf + off;
        *(unsigned long long *)(d + 0) = (unsigned long long)(i + 1);  /* d_ino */
        *(long long *)(d + 8) = (long long)(off + reclen);             /* d_off */
        *(unsigned short *)(d + 16) = (unsigned short)reclen;          /* d_reclen */
        kind = PuckSynthGet(i)->kind;
        d[18] = (unsigned char)((kind == PUCK_SF_DIR) ? 4 : (kind == PUCK_SF_LINK) ? 10
                              : (kind == PUCK_SF_STATIC) ? 8 : 2);  /* d_type DT_DIR/LNK/REG/CHR */
        for (j = 0; j < namelen; j++) d[19 + j] = (unsigned char)name[j];
        d[19 + namelen] = 0;
        off += reclen;
    }
    g_puckFds[fd].offset = 1; /* mark enumerated -> next call returns EOF */
    return (long long)off;
}

/* readlink(at): copy a symlink's target (NOT null-terminated, per the syscall contract). */
static long long PuckReadlinkPath(const char *path, unsigned char *buf, unsigned long long bufsize)
{
    int isDir = 0, fi = PuckSynthFindPath(path, 0, &isDir), tl, i; /* readlink: don't follow final */
    const char *t;
    if (fi < 0) return isDir ? LINUX_EINVAL : LINUX_ENOENT; /* existing non-symlink -> EINVAL, not ENOENT */
    if (PuckSynthGet(fi)->kind != PUCK_SF_LINK) return LINUX_EINVAL;
    t = PuckSynthGet(fi)->content;
    tl = PuckStrLen(t);
    if ((unsigned long long)tl > bufsize) tl = (int)bufsize;
    for (i = 0; i < tl; i++) buf[i] = (unsigned char)t[i];
    return tl;
}

/* DRM ioctl seam for /dev/dri/renderD128 -> amdgpu. The Linux _IOC request packs dir[31:30],
 * size[29:16], type[15:8], nr[7:0]. RADV's first call is DRM_IOCTL_VERSION (type 'd', nr 0) to match
 * the driver name, which must read back "amdgpu". The two-call protocol (query lengths, then fill
 * provided buffers) is honored. The amdgpu AMDGPU_INFO surface (chip/PCI/CU for
 * vkEnumeratePhysicalDevices) is served below; unknown ioctls are logged so we can see what RADV asks. */
struct PuckDrmVersion
{
    int major, minor, patch;
    unsigned long long name_len; char *name;
    unsigned long long date_len; char *date;
    unsigned long long desc_len; char *desc;
};

/* amdgpu buffer objects. With no real GPU, a BO is just host RAM: GEM_CREATE allocates user-accessible
 * pages, GEM_MMAP hands the BO's CPU address back as the mmap "offset", and an mmap on the render-node
 * fd at that offset returns the page directly (PuckBoLookup, used by LINUX_SYS_MMAP). GEM_VA is a
 * no-op -- RADV tracks the GPU virtual address itself; we model no GPUVM. Enough for resource creation
 * and shader compilation; real GPU access waits on the GFX-ring port. */
#define PUCK_MAX_BOS 8192
typedef struct { unsigned int handle; unsigned long long cpu, size; } PuckBo;
static PuckBo g_puckBos[PUCK_MAX_BOS];
static int g_puckBoCount;
static unsigned int g_puckBoNext = 1;

/* If `off` (a GEM_MMAP offset == the BO's CPU address) names a live BO, return its base; else 0. */
static unsigned long long PuckBoLookup(unsigned long long off)
{
    int i;
    for (i = 0; i < g_puckBoCount; i++)
        if (g_puckBos[i].cpu == off) return g_puckBos[i].cpu;
    return 0;
}

static long long PuckDrmIoctl(unsigned int req, void *arg)
{
    unsigned int nr = req & 0xFF;
    unsigned int type = (req >> 8) & 0xFF;

    if (type == 'd' && nr == 0x00) /* DRM_IOCTL_VERSION (_IOWR('d',0,struct drm_version)) */
    {
        struct PuckDrmVersion *v = (struct PuckDrmVersion *)arg;
        static const char nm[] = "amdgpu";
        int i;
        v->major = 3; v->minor = 59; v->patch = 0; /* >= 3.54: Mesa 26.1 gates amdgpu on kernel 6.6+ */
        if (v->name && v->name_len >= 6) for (i = 0; i < 6; i++) v->name[i] = nm[i];
        v->name_len = 6;
        v->date_len = 0; /* empty date/desc: RADV matches on the name only */
        v->desc_len = 0;
        return 0;
    }

    if (type == 'd' && nr == 0x05) /* DRM_IOCTL_GET_CLIENT: amdgpu_get_auth checks we're authenticated */
    {
        /* struct drm_client { int idx; int auth; unsigned long pid, uid, magic, iocs; } */
        struct { int idx, auth; unsigned long pid, uid, magic, iocs; } *c = arg;
        c->auth = 1; c->pid = 1; c->uid = 0; c->magic = 0; c->iocs = 0;
        return 0;
    }

    if (type == 'd' && nr == 0x0c) /* DRM_IOCTL_GET_CAP: report all queried caps as supported */
    {
        /* struct drm_get_cap { __u64 capability; __u64 value; } -- RADV asks PRIME/SYNCOBJ/timeline. */
        struct { unsigned long long capability, value; } *cap = arg;
        cap->value = 1;
        if (cap->capability == 0x01) cap->value = 3; /* DRM_CAP_PRIME: import|export */
        return 0;
    }

    if (type == 'd' && nr == 0x0d) /* DRM_IOCTL_SET_CLIENT_CAP: accept (atomic/etc. are no-ops here) */
        return 0;

    if (type == 'd' && nr == 0x09) /* DRM_IOCTL_GEM_CLOSE: free a BO handle; bump allocator keeps it */
        return 0;

    /* Core DRM sync-object ioctls (0xBF..0xCD). RADV creates syncobjs for its fences during physical-
     * device setup; with no real GPU we hand out monotonic handles and treat every wait as already
     * signalled. Enough for enumeration; real fence semantics arrive with the GFX-ring port. */
    if (type == 'd' && nr == 0xBF) /* DRM_IOCTL_SYNCOBJ_CREATE { u32 handle; u32 flags; } */
    {
        static unsigned int s_syncobjNext = 1;
        struct { unsigned int handle, flags; } *s = arg;
        s->handle = s_syncobjNext++;
        return 0;
    }
    if (type == 'd' && nr == 0xC1) /* DRM_IOCTL_SYNCOBJ_HANDLE_TO_FD { u32 handle,flags; s32 fd; u32 pad; } */
    {
        struct { unsigned int handle, flags; int fd; unsigned int pad; } *s = arg;
        s->fd = 0x100 + (int)(s->handle & 0xFF); /* a synthetic, never-read fd */
        return 0;
    }
    if (type == 'd' && nr == 0xC2) /* DRM_IOCTL_SYNCOBJ_FD_TO_HANDLE { u32 handle,flags; s32 fd; u32 pad; } */
    {
        static unsigned int s_importNext = 0x4000;
        struct { unsigned int handle, flags; int fd; unsigned int pad; } *s = arg;
        s->handle = s_importNext++;
        return 0;
    }
    if (type == 'd' && (nr == 0xC0 || /* DESTROY */ (nr >= 0xC3 && nr <= 0xCD)))
        return 0; /* RESET/SIGNAL/WAIT/TIMELINE_WAIT/QUERY/TRANSFER/TIMELINE_SIGNAL: treat as signalled */

    if (type == 'd' && nr == 0x40) /* DRM_IOCTL_AMDGPU_GEM_CREATE: allocate a buffer object */
    {
        /* in {u64 bo_size, alignment, domains, domain_flags}; out {u32 handle, _pad} */
        unsigned long long *in = arg;
        unsigned long long size = (in[0] + 4095ull) & ~4095ull, cpu, k;
        if (size == 0 || g_puckBoCount >= PUCK_MAX_BOS) return LINUX_ENOMEM;
        cpu = PuckAllocPages(size / 4096);
        if (!cpu) return LINUX_ENOMEM;
        PuckSetUserAccessible(cpu, size);
        for (k = 0; k < size; k++) ((unsigned char *)cpu)[k] = 0; /* zerovram semantics */
        g_puckBos[g_puckBoCount].handle = g_puckBoNext;
        g_puckBos[g_puckBoCount].cpu = cpu;
        g_puckBos[g_puckBoCount].size = size;
        g_puckBoCount++;
        *(unsigned int *)arg = g_puckBoNext++; /* out.handle */
        ((unsigned int *)arg)[1] = 0;
        return 0;
    }

    if (type == 'd' && nr == 0x41) /* DRM_IOCTL_AMDGPU_GEM_MMAP: return the BO's CPU addr as the offset */
    {
        /* in {u32 handle, _pad}; out {u64 addr_ptr} */
        unsigned int handle = *(unsigned int *)arg;
        int i;
        for (i = 0; i < g_puckBoCount; i++)
            if (g_puckBos[i].handle == handle) { *(unsigned long long *)arg = g_puckBos[i].cpu; return 0; }
        return LINUX_EINVAL;
    }

    if (type == 'd' && nr == 0x48) /* DRM_IOCTL_AMDGPU_GEM_VA: map BO -> GPU VA; no GPUVM modelled */
        return 0;

    if (type == 'd' && nr == 0x42) /* DRM_IOCTL_AMDGPU_CTX: GPU submission context (vkCreateDevice) */
    {
        /* union drm_amdgpu_ctx { in{u32 op,flags,ctx_id,priority}; out{...ctx_id@0...}; } */
        unsigned int *u = arg;
        unsigned int op = u[0]; /* AMDGPU_CTX_OP_ALLOC=1 FREE=2 QUERY_STATE=3 QUERY_STATE2=4 */
        static unsigned int s_ctxNext = 1;
        if (op == 1) { u[0] = s_ctxNext++; u[1] = 0; }      /* out.alloc.ctx_id */
        else { u[0] = 0; u[1] = 0; u[2] = 0; u[3] = 0; }    /* free / query-state: no hangs/resets */
        return 0;
    }

    if (type == 'd' && nr == 0x45) /* DRM_IOCTL_AMDGPU_INFO (nr = DRM_COMMAND_BASE 0x40 + 0x05) */
    {
        /* struct drm_amdgpu_info { __u64 return_pointer; __u32 return_size; __u32 query; union ...; } */
        struct { unsigned long long return_pointer; unsigned int return_size, query, u[4]; } *q = arg;
        unsigned char *out = (unsigned char *)(unsigned long long)q->return_pointer;
        unsigned int sz = q->return_size, i;
        if (!out) return LINUX_EINVAL;

        switch (q->query)
        {
            case 0x00: /* AMDGPU_INFO_ACCEL_WORKING */
                if (sz >= 4) *(unsigned int *)out = 1;
                return 0;

            case 0x16: /* AMDGPU_INFO_DEV_INFO -> struct drm_amdgpu_info_device (fields by offset) */
            {
                unsigned char dev[320];
                for (i = 0; i < sizeof dev; i++) dev[i] = 0;
                *(unsigned int *)(dev + 0)   = 0x163F;            /* device_id: Van Gogh (Steam Deck) */
                *(unsigned int *)(dev + 8)   = 0x01;              /* external_rev: VANGOGH_A0 (chip id) */
                *(unsigned int *)(dev + 16)  = 144;              /* family = AMDGPU_FAMILY_VGH (0x90, not 0x8B) */
                *(unsigned int *)(dev + 20)  = 1;                 /* num_shader_engines */
                *(unsigned int *)(dev + 24)  = 1;                 /* num_shader_arrays_per_engine */
                *(unsigned int *)(dev + 28)  = 100000;            /* gpu_counter_freq (kHz): valid timestamps */
                *(unsigned long long *)(dev + 32) = 1600000;      /* max_engine_clock (kHz) */
                *(unsigned long long *)(dev + 40) = 1600000;      /* max_memory_clock (kHz) */
                *(unsigned int *)(dev + 48)  = 8;                 /* cu_active_number (8 CU) */
                *(unsigned int *)(dev + 56)  = 0xFF;              /* cu_bitmap[0][0]: 8 CUs in SE0/SA0 */
                *(unsigned int *)(dev + 128) = 8;                 /* num_hw_gfx_contexts */
                *(unsigned long long *)(dev + 136) = 1;           /* ids_flags: AMDGPU_IDS_FLAGS_FUSION (APU) */
                *(unsigned long long *)(dev + 152) = 0x7FFFFFFFFFFFull; /* virtual_address_max */
                *(unsigned int *)(dev + 160) = 0x1000;           /* virtual_address_alignment */
                *(unsigned int *)(dev + 168) = 0x1000;           /* gart_page_size */
                *(unsigned int *)(dev + 180) = 128;              /* vram_bit_width */
                *(unsigned int *)(dev + 240) = 32;               /* wave_front_size (gfx10.3 wave32) */
                for (i = 0; i < (sz < sizeof dev ? sz : sizeof dev); i++) out[i] = dev[i];
                return 0;
            }

            case 0x19: /* AMDGPU_INFO_MEMORY: 3x drm_amdgpu_heap_info {total,usable,usage,max_alloc} */
            {
                unsigned char mi[96];
                unsigned long long vram = 256ull << 20, gtt = 3072ull << 20; /* APU: 256 MB carveout + 3 GB GTT */
                for (i = 0; i < sizeof mi; i++) mi[i] = 0;
                *(unsigned long long *)(mi + 0)  = vram; *(unsigned long long *)(mi + 8)  = vram;
                *(unsigned long long *)(mi + 24) = vram;                              /* vram.max_allocation */
                *(unsigned long long *)(mi + 32) = vram; *(unsigned long long *)(mi + 40) = vram;
                *(unsigned long long *)(mi + 56) = vram;                              /* cpu_accessible_vram */
                *(unsigned long long *)(mi + 64) = gtt;  *(unsigned long long *)(mi + 72) = gtt;
                *(unsigned long long *)(mi + 88) = gtt;                               /* gtt.max_allocation */
                for (i = 0; i < (sz < sizeof mi ? sz : sizeof mi); i++) out[i] = mi[i];
                return 0;
            }

            case 0x02: /* AMDGPU_INFO_HW_IP_INFO: GFX/COMPUTE present, gfx10.3, one ring */
            {
                unsigned char ip[96];
                for (i = 0; i < sizeof ip; i++) ip[i] = 0;
                *(unsigned int *)(ip + 0)  = 10;                 /* hw_ip_version_major */
                *(unsigned int *)(ip + 4)  = 3;                  /* hw_ip_version_minor */
                *(unsigned int *)(ip + 24) = 1;                  /* available_rings (1) */
                for (i = 0; i < (sz < sizeof ip ? sz : sizeof ip); i++) out[i] = ip[i];
                return 0;
            }

            default: /* unknown query: benign zeros (RADV tolerates), never -EINVAL */
                for (i = 0; i < sz && i < 512; i++) out[i] = 0;
                return 0;
        }
    }

    {
        const char *hx = "0123456789abcdef";
        SerialPuts("[drm] unhandled ioctl type='"); SerialWriteByte((int)type);
        SerialPuts("' nr=0x"); SerialWriteByte(hx[(nr >> 4) & 0xF]); SerialWriteByte(hx[nr & 0xF]);
        SerialPuts("\r\n");
    }
    return LINUX_EINVAL;
}

/* Called from the asm trampoline with the syscall number + 6 args in the Win64 convention. */
long long PuckSyscallDispatch(long long num, long long a1, long long a2, long long a3,
                                 long long a4, long long a5, long long a6)
{
    (void)a6;

    switch (num)
    {
        case LINUX_SYS_WRITE: /* write(fd, buf, count) -> our serial */
        {
            const unsigned char *buf = (const unsigned char *)a2;
            long long i;
            for (i = 0; i < a3; i++)
                SerialWriteByte(buf[i]);
            return a3;
        }

        case LINUX_SYS_WRITEV: /* writev(fd, iov, iovcnt) -> our serial */
        {
            const PuckIoVec *iov = (const PuckIoVec *)a2;
            long long count = a3, total = 0, k;
            for (k = 0; k < count; k++)
            {
                const unsigned char *b = (const unsigned char *)iov[k].iov_base;
                unsigned long long j;
                for (j = 0; j < iov[k].iov_len; j++)
                    SerialWriteByte(b[j]);
                total += (long long)iov[k].iov_len;
            }
            return total;
        }

        case LINUX_SYS_READ: /* synthetic VFS (fd >= 3) or stdin EOF (fd 0) */
            return PuckSynthRead((int)a1, (unsigned char *)a2, (unsigned long long)a3);

        case LINUX_SYS_READV: /* musl stdio reads via readv; gather into each iovec from the VFS */
        {
            const PuckIoVec *iov = (const PuckIoVec *)a2;
            long long count = a3, total = 0, k, n;
            for (k = 0; k < count; k++)
            {
                n = PuckSynthRead((int)a1, (unsigned char *)iov[k].iov_base, iov[k].iov_len);
                if (n < 0) return total > 0 ? total : n;
                total += n;
                if ((unsigned long long)n < iov[k].iov_len) break; /* short read / EOF */
            }
            return total;
        }

        case LINUX_SYS_OPEN: /* open(path, flags, mode) */
            return PuckSynthOpen((const char *)a1);

        case LINUX_SYS_OPENAT: /* openat(dirfd, path, flags, mode) */
            return PuckSynthOpen((const char *)a2);

        case LINUX_SYS_CLOSE:
            return PuckSynthClose((int)a1);

        case LINUX_SYS_LSEEK:
            return PuckSynthLseek((int)a1, a2, (int)a3);

        case LINUX_SYS_IOCTL: /* DRM ioctls on the render node; else musl's TIOCGWINSZ tty probe */
        {
            int fd = (int)a1;
            if (fd >= 3 && fd < PUCK_MAX_FDS && g_puckFds[fd].used
                && PuckSynthGet(g_puckFds[fd].fileIndex)->kind == PUCK_SF_DRM)
                return PuckDrmIoctl((unsigned int)a2, (void *)a3);
            return LINUX_ENOTTY;
        }

        case LINUX_SYS_FSTAT: /* fstat(fd, statbuf) */
            if (a2) PuckFillStat(PuckFdFileIndex((int)a1), (unsigned char *)a2);
            return 0;

        case LINUX_SYS_STAT:  /* stat(path, statbuf): follows symlinks; RADV scans /dev/dri with it */
        case LINUX_SYS_LSTAT: /* lstat(path, statbuf): does not follow a trailing symlink */
        {
            const char *path = (const char *)a1;
            int isDir = 0, fi = PuckSynthFindPath(path, num == LINUX_SYS_STAT ? 1 : 0, &isDir);
            if (fi < 0 && !isDir) return LINUX_ENOENT;
            if (a2) { if (fi >= 0) PuckFillStat(fi, (unsigned char *)a2); else PuckFillStatDir((unsigned char *)a2); }
            return 0;
        }

        case LINUX_SYS_NEWFSTATAT: /* newfstatat(dirfd, path, statbuf, flags); AT_SYMLINK_NOFOLLOW=0x100 */
        {
            const char *path = (const char *)a2;
            int isDir = 0, fi;
            if (path && path[0])
            {
                fi = PuckSynthFindPath(path, (a4 & 0x100) ? 0 : 1, &isDir);
                if (fi < 0 && !isDir) return LINUX_ENOENT;
            }
            else fi = PuckFdFileIndex((int)a1);
            if (a3) { if (fi >= 0) PuckFillStat(fi, (unsigned char *)a3); else PuckFillStatDir((unsigned char *)a3); }
            return 0;
        }

        case LINUX_SYS_STATX: /* statx(dirfd, path, flags, mask, statxbuf) */
        {
            const char *path = (const char *)a2;
            int isDir = 0, fi;
            if (path && path[0])
            {
                fi = PuckSynthFindPath(path, (a3 & 0x100) ? 0 : 1, &isDir); /* a3 = flags */
                if (fi < 0 && !isDir) return LINUX_ENOENT;
            }
            else fi = PuckFdFileIndex((int)a1);
            if (a5) { if (fi >= 0) PuckFillStatx(fi, (unsigned char *)a5); else { /* dir */
                int i; unsigned char *sx = (unsigned char *)a5;
                for (i = 0; i < 256; i++) sx[i] = 0;
                *(unsigned int *)(sx + 0) = 0x000007FFu; *(unsigned int *)(sx + 4) = 4096;
                *(unsigned int *)(sx + 16) = 2; *(unsigned short *)(sx + 28) = 0040755;
                *(unsigned int *)(sx + 156) = (unsigned int)PUCK_SYNTH_DEV; } }
            return 0;
        }

        case LINUX_SYS_ACCESS: /* access(path, mode): RADV probes for sysfs/debugfs files */
        {
            const char *path = (const char *)a1;
            int isDir = 0, fi = PuckSynthFindPath(path, 1, &isDir);
            return (fi >= 0 || isDir) ? 0 : LINUX_ENOENT;
        }

        case LINUX_SYS_GETDENTS64: /* getdents64(fd, buf, count) */
            return PuckGetdents64((int)a1, (unsigned char *)a2, (unsigned long long)a3);

        case LINUX_SYS_PREAD64: /* pread64(fd, buf, count, offset): positioned read (ld-musl reads .so hdrs) */
        {
            int fi = PuckFdFileIndex((int)a1);
            unsigned char *buf = (unsigned char *)a2;
            unsigned long long count = (unsigned long long)a3, offset = (unsigned long long)a4, i, n = 0;
            const unsigned char *content;
            unsigned long long flen;
            if (fi < 0) return LINUX_EBADF;
            content = (const unsigned char *)PuckSynthGet(fi)->content;
            flen = PuckSynthGet(fi)->len;
            if (content && offset < flen)
            {
                n = flen - offset;
                if (n > count) n = count;
                for (i = 0; i < n; i++) buf[i] = content[offset + i];
            }
            return (long long)n;
        }

        case LINUX_SYS_READLINK: /* readlink(path, buf, bufsize) */
            return PuckReadlinkPath((const char *)a1, (unsigned char *)a2, (unsigned long long)a3);

        case LINUX_SYS_READLINKAT: /* readlinkat(dirfd, path, buf, bufsize) */
            return PuckReadlinkPath((const char *)a2, (unsigned char *)a3, (unsigned long long)a4);

        case LINUX_SYS_FCNTL: /* F_DUPFD(_CLOEXEC) -> a real dup; F_GET/SETFD/FL -> success */
        {
            int cmd = (int)a2;
            if (cmd == 0 || cmd == 1030) /* F_DUPFD=0, F_DUPFD_CLOEXEC=1030 */
            {
                int src = (int)a1, lo = (int)a3 > 3 ? (int)a3 : 3, fd;
                if (PuckFdFileIndex(src) < 0) return LINUX_EBADF;
                for (fd = lo; fd < PUCK_MAX_FDS; fd++)
                    if (!g_puckFds[fd].used) { g_puckFds[fd] = g_puckFds[src]; return fd; }
                return LINUX_EMFILE;
            }
            return 0;
        }

        case LINUX_SYS_SCHED_GETAFFINITY: /* 1-CPU mask -> ACO sizes its thread pool to one worker */
        {
            unsigned char *mask = (unsigned char *)a3;
            unsigned long long n = (unsigned long long)a2, i;
            if (mask && n >= 1) { mask[0] = 1; for (i = 1; i < n && i < 128; i++) mask[i] = 0; }
            return 8; /* bytes of the affinity mask written */
        }

        case LINUX_SYS_SYSINFO: /* zero-filled struct sysinfo (non-gating) */
        {
            unsigned char *si = (unsigned char *)a1;
            int i;
            if (si) for (i = 0; i < 112; i++) si[i] = 0;
            return 0;
        }

        case LINUX_SYS_MADVISE:      /* advisory only */
        case LINUX_SYS_RT_SIGACTION: /* no signal delivery modelled */
            return 0;

        case LINUX_SYS_MREMAP: /* the bump mmap arena can't relocate a mapping */
            return LINUX_ENOMEM;

        case LINUX_SYS_GETPID:
            return 1;

        case 102: /* getuid  */
        case 104: /* getgid  */
        case 107: /* geteuid */
        case 108: /* getegid */
            return 0; /* run as root; RADV calls these via getpwuid for the device name */

        case LINUX_SYS_ARCH_PRCTL: /* arch_prctl(code, addr): set/get FS/GS base via MSR */
        {
            switch (a1)
            {
                case LINUX_ARCH_SET_FS:
                    __writemsr(MSR_FS_BASE, (unsigned long long)a2);
                    g_currentThread->ctx.fsbase = (unsigned long long)a2; /* restore on every resume */
                    return 0;
                case LINUX_ARCH_SET_GS: __writemsr(MSR_GS_BASE, (unsigned long long)a2); return 0;
                case LINUX_ARCH_GET_FS: *(unsigned long long *)a2 = __readmsr(MSR_FS_BASE); return 0;
                case LINUX_ARCH_GET_GS: *(unsigned long long *)a2 = __readmsr(MSR_GS_BASE); return 0;
                default: return LINUX_EINVAL;
            }
        }

        case LINUX_SYS_SET_TID_ADDRESS: /* record this thread's clear-tid addr; return its tid */
            g_currentThread->clearTidAddr = (unsigned long long)a1;
            return (long long)g_currentThread->tid;

        case LINUX_SYS_SET_ROBUST_LIST: /* no robust-list support; pretend success */
        case LINUX_SYS_MUNMAP:         /* bump allocator: nothing to free */
        case LINUX_SYS_MPROTECT:       /* guest pages are already RWX+US */
        case LINUX_SYS_RT_SIGPROCMASK: /* no signal delivery modelled */
            return 0;

        case LINUX_SYS_BRK: /* query (a1==0) or move the program break within the arena */
            PuckInitGuestArena();
            if (a1 == 0)
                return (long long)g_guestBrk;
            if ((unsigned long long)a1 >= g_guestArenaBase && (unsigned long long)a1 <= g_guestMmapTop)
                g_guestBrk = (unsigned long long)a1;
            return (long long)g_guestBrk;

        case LINUX_SYS_MMAP: /* mmap(addr,len,prot,flags,fd,off): anon (zeroed), or file-backed (copy
                              * the file's bytes). MAP_FIXED honors the caller's addr (ld-musl refines
                              * a segment inside its blanket reservation); else bump down the arena. */
        {
            unsigned long long len = ((unsigned long long)a2 + 4095ull) & ~4095ull;
            int flags = (int)a4, fd = (int)a5;
            unsigned long long fileOff = (unsigned long long)a6, target, i;
            PuckInitGuestArena();
            if (len == 0)
                return LINUX_EINVAL;

            /* mmap of the render node at a GEM_MMAP offset -> the BO's host page, mapped in place. */
            if (fd >= 3 && fd < PUCK_MAX_FDS && g_puckFds[fd].used
                && PuckSynthGet(g_puckFds[fd].fileIndex)->kind == PUCK_SF_DRM)
            {
                unsigned long long bo = PuckBoLookup(fileOff);
                return bo ? (long long)bo : LINUX_EINVAL;
            }

            if (flags & 0x10) /* MAP_FIXED: addr lies within a region we already handed out */
                target = (unsigned long long)a1;
            else
            {
                if (g_guestMmapTop - len < g_guestBrk)
                    return LINUX_ENOMEM;
                g_guestMmapTop -= len;
                target = g_guestMmapTop;
            }

            if ((flags & 0x20) || fd < 0) /* MAP_ANONYMOUS / no fd -> zero-filled */
            {
                for (i = 0; i < len; i++) ((unsigned char *)target)[i] = 0;
            }
            else /* file-backed: copy the file's bytes at fileOff (clamped to its size), zero the rest */
            {
                int fi = PuckFdFileIndex(fd);
                const unsigned char *content = (fi >= 0) ? (const unsigned char *)PuckSynthGet(fi)->content : 0;
                unsigned long long flen = (fi >= 0) ? PuckSynthGet(fi)->len : 0;
                unsigned long long n = 0;
                if (content && fileOff < flen)
                {
                    n = flen - fileOff;
                    if (n > len) n = len;
                    for (i = 0; i < n; i++) ((unsigned char *)target)[i] = content[fileOff + i];
                }
                for (i = n; i < len; i++) ((unsigned char *)target)[i] = 0; /* .bss / beyond EOF */
            }
            return (long long)target;
        }

        case LINUX_SYS_CLOCK_GETTIME: /* CLOCK_REALTIME(0) = boot epoch + uptime; else monotonic uptime */
        {
            unsigned long long sec, nsec;
            long long *ts = (long long *)a2; /* struct timespec { long tv_sec; long tv_nsec; } */
            PuckUptime(&sec, &nsec);
            if (a1 == 0)
                sec += g_bootEpoch;
            if (ts) { ts[0] = (long long)sec; ts[1] = (long long)nsec; }
            return 0;
        }

        case LINUX_SYS_GETRANDOM: /* hardware entropy (RDRAND) for TLS key/nonce generation */
        {
            unsigned char *buf = (unsigned char *)a1;
            unsigned long long want = (unsigned long long)a2, i = 0;
            while (i < want)
            {
                unsigned long long r = PuckRdRand64();
                int j;
                for (j = 0; j < 8 && i < want; j++) buf[i++] = (unsigned char)(r >> (j * 8));
            }
            return (long long)want;
        }

        case LINUX_SYS_GETTID: /* single process; report the running thread's tid */
            return (long long)g_currentThread->tid;

        case LINUX_SYS_EXIT_GROUP: /* whole process exits (thread exit(60) is handled by the scheduler) */
            SerialPuts("[syscall] guest called exit_group(");
            SerialPutI64(a1);
            SerialPuts(") - process terminated.\r\n");
            PuckHang();
            return 0;

        default:
            SerialPuts("[syscall] ENOSYS: unhandled syscall ");
            SerialPutI64(num);
            SerialPuts(" (a1=");
            SerialPutI64(a1);
            SerialPuts(")\r\n");
            return LINUX_ENOSYS;
    }
}

/* Round-robin: the next RUNNABLE thread after the current one, or NULL if none is runnable. */
static PuckThread *PuckPickNext(void)
{
    int start = (int)(g_currentThread - g_puckThreads);
    int i;
    for (i = 1; i <= PUCK_MAX_THREADS; i++)
    {
        PuckThread *t = &g_puckThreads[(start + i) % PUCK_MAX_THREADS];
        if (t->state == PUCK_THREAD_RUNNABLE)
            return t;
    }
    return 0;
}

/* Make up to maxWake threads blocked on uaddr runnable; returns how many were woken. */
static int PuckFutexWake(unsigned long long uaddr, int maxWake)
{
    int woken = 0, i;
    for (i = 0; i < PUCK_MAX_THREADS && woken < maxWake; i++)
    {
        if (g_puckThreads[i].state == PUCK_THREAD_BLOCKED && g_puckThreads[i].futexAddr == uaddr)
        {
            g_puckThreads[i].state = PUCK_THREAD_RUNNABLE;
            g_puckThreads[i].futexAddr = 0;
            woken++;
        }
    }
    return woken;
}

/* clone() for a thread: a child sharing our address space, starting at the post-syscall RIP with
 * rax=0 on its own stack/TLS. The parent keeps running and gets the child's tid. */
static void PuckSysClone(PuckThread *caller, long long flags, long long stack,
                            long long ptid, long long ctid, long long tls)
{
    PuckThread *child = 0;
    int i;
    for (i = 0; i < PUCK_MAX_THREADS; i++)
        if (g_puckThreads[i].state == PUCK_THREAD_FREE) { child = &g_puckThreads[i]; break; }
    if (child == 0) { caller->ctx.rax = (unsigned long long)(long long)LINUX_EAGAIN; return; }

    child->ctx = caller->ctx;        /* inherit regs + the post-syscall RIP (musl's clone tail) */
    child->ctx.rax = 0;              /* the child observes clone() == 0 */
    child->ctx.rsp = (unsigned long long)stack;
    child->ctx.fsbase = (flags & CLONE_SETTLS) ? (unsigned long long)tls : caller->ctx.fsbase;
    child->tid = g_puckNextTid++;
    child->state = PUCK_THREAD_RUNNABLE;
    child->futexAddr = 0;
    child->clearTidAddr = (flags & CLONE_CHILD_CLEARTID) ? (unsigned long long)ctid : 0;

    if (flags & CLONE_PARENT_SETTID) *(int *)ptid = child->tid;
    if (flags & CLONE_CHILD_SETTID)  *(int *)ctid = child->tid;

    caller->ctx.rax = (unsigned long long)child->tid; /* parent gets the tid and keeps running */
}

/* futex(uaddr, op, val): FUTEX_WAIT parks the caller (switching away) unless *uaddr already moved;
 * FUTEX_WAKE makes up to val waiters runnable. The futex word lives in shared (ring-3) memory. */
static void PuckSysFutex(PuckThread *caller, long long uaddr, long long op, long long val)
{
    int cmd = (int)(op & FUTEX_CMD_MASK);

    if (cmd == 0) /* FUTEX_WAIT */
    {
        if (*(volatile int *)uaddr != (int)val) { caller->ctx.rax = (unsigned long long)(long long)LINUX_EAGAIN; return; }
        caller->ctx.rax = 0; /* returns 0 once woken */
        caller->state = PUCK_THREAD_BLOCKED;
        caller->futexAddr = (unsigned long long)uaddr;
        g_currentThread = PuckPickNext();
        if (g_currentThread == 0) { SerialPuts("[sched] PANIC: futex deadlock (no runnable thread)\r\n"); PuckHang(); }
    }
    else if (cmd == 1) /* FUTEX_WAKE */
    {
        caller->ctx.rax = (unsigned long long)PuckFutexWake((unsigned long long)uaddr, (int)val);
    }
    else
    {
        caller->ctx.rax = (unsigned long long)(long long)LINUX_ENOSYS; /* requeue/PI/etc. not modelled */
    }
}

/* A thread terminated (SYS_exit). Honour CLONE_CHILD_CLEARTID (clear the tid + wake pthread_join),
 * mark it a zombie, and switch to the next runnable thread. */
static void PuckSysThreadExit(PuckThread *caller)
{
    if (caller->clearTidAddr)
    {
        *(int *)caller->clearTidAddr = 0;
        PuckFutexWake(caller->clearTidAddr, 0x7FFFFFFF);
    }
    caller->state = PUCK_THREAD_ZOMBIE;
    g_currentThread = PuckPickNext();
    if (g_currentThread == 0) { SerialPuts("[sched] last thread exited; halting.\r\n"); PuckHang(); }
}

/* The trampoline's C entry point: the caller's register snapshot is already in g_puckIncomingCtx.
 * Copy it into the calling thread's saved context, then either service a scheduling syscall here
 * (which may switch g_currentThread) or delegate to PuckSyscallDispatch. Finally set the FS base
 * for whoever runs next and return that thread so the asm resumes it. */
PuckThread *PuckHandleSyscall(void)
{
    PuckThread *caller = g_currentThread;
    unsigned long long *d = (unsigned long long *)&caller->ctx;
    unsigned long long *s = (unsigned long long *)&g_puckIncomingCtx;
    long long num, a1, a2, a3, a4, a5, a6;
    int i;

    for (i = 0; i < PUCK_CTX_GPR_QWORDS; i++) /* rax..rflags; the C-managed fsbase is preserved */
        d[i] = s[i];

    num = (long long)g_puckIncomingCtx.rax;
    a1 = (long long)g_puckIncomingCtx.rdi;
    a2 = (long long)g_puckIncomingCtx.rsi;
    a3 = (long long)g_puckIncomingCtx.rdx;
    a4 = (long long)g_puckIncomingCtx.r10;
    a5 = (long long)g_puckIncomingCtx.r8;
    a6 = (long long)g_puckIncomingCtx.r9;

    switch (num)
    {
        case LINUX_SYS_CLONE:       PuckSysClone(caller, a1, a2, a3, a4, a5); break;
        case LINUX_SYS_FUTEX:       PuckSysFutex(caller, a1, a2, a3); break;
        case LINUX_SYS_EXIT:        PuckSysThreadExit(caller); break;
        case LINUX_SYS_SCHED_YIELD: caller->ctx.rax = 0; g_currentThread = PuckPickNext(); break;
        default:                    caller->ctx.rax = (unsigned long long)PuckSyscallDispatch(num, a1, a2, a3, a4, a5, a6); break;
    }

    __writemsr(MSR_FS_BASE, g_currentThread->ctx.fsbase);
    return g_currentThread;
}

/* Timer-IRQ entry from the asm (PuckTimerIsr): the interrupted thread's full register file is
 * already in g_puckIncomingCtx. Snapshot it, round-robin to the next runnable thread, EOI the
 * PIC, and return whoever runs next for the shared iretq resume. This is what makes the scheduler
 * preemptive - a CPU-bound thread that never syscalls still gets switched out here. */
PuckThread *PuckHandleTimer(void)
{
    PuckThread *caller = g_currentThread;
    unsigned long long *d = (unsigned long long *)&caller->ctx;
    unsigned long long *s = (unsigned long long *)&g_puckIncomingCtx;
    PuckThread *next;
    int i;

    for (i = 0; i < PUCK_CTX_GPR_QWORDS; i++) /* snapshot the interrupted thread (preserve fsbase) */
        d[i] = s[i];

    next = PuckPickNext();
    if (next != 0)
        g_currentThread = next;

    __outbyte(0x20, 0x20); /* non-specific EOI to the master PIC (timer is IRQ0) */

    __writemsr(MSR_FS_BASE, g_currentThread->ctx.fsbase);
    return g_currentThread;
}

/* Load our own GDT (replacing the firmware's) and TSS, and reload every segment register. Done
 * early so the IDT's gate selector (0x08) and any fault resolve against our descriptors. The GDT
 * carries the ring-0 + ring-3 code/data descriptors and a TSS; rsp0 is the ring-0 stack the CPU
 * switches to on a ring-3 interrupt/exception. */
void PuckInitGdt(void)
{
    unsigned long long base = (unsigned long long)&g_puckTss;
    unsigned long long limit = sizeof(g_puckTss) - 1;

    g_puckTss.Rsp0 =
        ((unsigned long long)&g_puckKernelStack[sizeof(g_puckKernelStack)]) & ~15ull;
    g_puckTss.IoMapBase = (unsigned short)sizeof(g_puckTss); /* >= limit: no I/O bitmap */

    /* 64-bit TSS system descriptor (selector 0x28), spanning g_puckGdt[5..6]. */
    g_puckGdt[5] = (limit & 0xFFFFull)
                    | ((base & 0xFFFFFFull) << 16)
                    | (0x89ull << 40)                       /* present, type=9 (available 64-bit TSS) */
                    | (((limit >> 16) & 0xFull) << 48)
                    | (((base >> 24) & 0xFFull) << 56);
    g_puckGdt[6] = (base >> 32) & 0xFFFFFFFFull;

    g_puckGdtr.Limit = (unsigned short)(sizeof(g_puckGdt) - 1);
    g_puckGdtr.Base = (unsigned long long)&g_puckGdt[0];
    PuckLoadGdt(&g_puckGdtr);
    PuckLoadTr(0x28); /* TSS selector */
}

void PuckInitSyscalls(void)
{
    g_puckSyscallStackTop =
        ((unsigned long long)&g_puckSyscallStack[sizeof(g_puckSyscallStack)]) & ~15ull;

    __writemsr(0xC0000080u, __readmsr(0xC0000080u) | 1ull);            /* EFER.SCE = 1 */
    __writemsr(0xC0000081u, (0x10ull << 48) | (0x08ull << 32));        /* STAR: SYSCALL CS=0x08 */
    __writemsr(0xC0000082u, (unsigned long long)PuckSyscallEntry);  /* LSTAR */
    __writemsr(0xC0000084u, 0x200ull);                                 /* SFMASK: clear IF */

    /* Mask every legacy-PIC IRQ before threads run with IF=1 (the iretq resume sets IF), so a stray
     * firmware-left timer tick can't vector into an exception handler. PuckInitTimer remaps the
     * PIC and unmasks just the timer. */
    __outbyte(0x21, 0xFF); /* master PIC: mask IRQ0-7 */
    __outbyte(0xA1, 0xFF); /* slave PIC:  mask IRQ8-15 */

    /* The guest launched by managed Main is thread 0; its syscalls snapshot into this thread's ctx. */
    g_puckThreads[0].tid = g_puckNextTid++;
    g_puckThreads[0].state = PUCK_THREAD_RUNNABLE;
    g_puckThreads[0].ctx.fsbase = 0;
    g_currentThread = &g_puckThreads[0];
}

/* ---------------------------------------------------------------------------------------
 * IDT + panic handler. Each of the 32 CPU-exception vectors points at an asm stub (g_puckIsrStubs)
 * that builds a uniform trap frame and calls PuckTrapHandler, which dumps the CPU state to serial.
 * This converts an otherwise silent triple-fault/reboot (a guest fault, a bad page table, a wrong
 * ring transition) into a diagnosable panic line - essential before paging and ring-3 work. */
typedef struct PuckTrapFrame
{
    unsigned long long r15, r14, r13, r12, r11, r10, r9, r8;
    unsigned long long rbp, rdi, rsi, rdx, rcx, rbx, rax;
    unsigned long long vector, errorCode;
    unsigned long long rip, cs, rflags, rsp, ss;
} PuckTrapFrame;

static const char *PuckVectorName(unsigned long long v)
{
    switch (v)
    {
        case 0:  return "#DE divide";
        case 3:  return "#BP breakpoint";
        case 6:  return "#UD invalid-opcode";
        case 8:  return "#DF double-fault";
        case 13: return "#GP general-protection";
        case 14: return "#PF page-fault";
        default: return "exception";
    }
}

void PuckTrapHandler(PuckTrapFrame *f)
{
    SerialPuts("\r\n[PANIC] ");
    SerialPuts(PuckVectorName(f->vector));
    SerialPuts(" vector=");
    SerialPutHex64(f->vector);
    SerialPuts(" err=");
    SerialPutHex64(f->errorCode);
    SerialPuts("\r\n  rip=");
    SerialPutHex64(f->rip);
    SerialPuts(" cs=");
    SerialPutHex64(f->cs);
    SerialPuts(" rflags=");
    SerialPutHex64(f->rflags);
    SerialPuts("\r\n  rsp=");
    SerialPutHex64(f->rsp);
    SerialPuts(" ss=");
    SerialPutHex64(f->ss);
    SerialPuts(" cr2=");
    SerialPutHex64(PuckReadCr2());
    SerialPuts(" cr3=");
    SerialPutHex64(PuckReadCr3());
    SerialPuts("\r\n  rax=");
    SerialPutHex64(f->rax);
    SerialPuts(" rbx=");
    SerialPutHex64(f->rbx);
    SerialPuts(" rcx=");
    SerialPutHex64(f->rcx);
    SerialPuts(" rdx=");
    SerialPutHex64(f->rdx);
    SerialPuts("\r\n  rsi=");
    SerialPutHex64(f->rsi);
    SerialPuts(" rdi=");
    SerialPutHex64(f->rdi);
    SerialPuts(" rbp=");
    SerialPutHex64(f->rbp);
    SerialPuts(" r8=");
    SerialPutHex64(f->r8);
    SerialPuts("\r\n  r9=");
    SerialPutHex64(f->r9);
    SerialPuts(" r10=");
    SerialPutHex64(f->r10);
    SerialPuts(" r11=");
    SerialPutHex64(f->r11);
    SerialPuts(" r12=");
    SerialPutHex64(f->r12);
    SerialPuts("\r\n  r13=");
    SerialPutHex64(f->r13);
    SerialPuts(" r14=");
    SerialPutHex64(f->r14);
    SerialPuts(" r15=");
    SerialPutHex64(f->r15);
    SerialPuts("\r\n[PANIC] halted.\r\n");
    PuckHang();
}

#pragma pack(push, 1)
typedef struct PuckIdtEntry
{
    unsigned short OffsetLow;
    unsigned short Selector;
    unsigned char  Ist;
    unsigned char  TypeAttr;
    unsigned short OffsetMid;
    unsigned int   OffsetHigh;
    unsigned int   Reserved;
} PuckIdtEntry;
typedef struct { unsigned short Limit; unsigned long long Base; } PuckIdtr;
#pragma pack(pop)

static PuckIdtEntry g_puckIdt[256];
static PuckIdtr g_puckIdtr;

static void PuckSetIdtEntry(int vec, unsigned long long handler)
{
    g_puckIdt[vec].OffsetLow = (unsigned short)handler;
    g_puckIdt[vec].Selector = 0x08;          /* ring-0 code in our GDT */
    g_puckIdt[vec].Ist = 0;
    g_puckIdt[vec].TypeAttr = 0x8E;          /* present, DPL0, 64-bit interrupt gate */
    g_puckIdt[vec].OffsetMid = (unsigned short)(handler >> 16);
    g_puckIdt[vec].OffsetHigh = (unsigned int)(handler >> 32);
    g_puckIdt[vec].Reserved = 0;
}

void PuckInitIdt(void)
{
    int i;
    for (i = 0; i < 32; i++)
        PuckSetIdtEntry(i, g_puckIsrStubs[i]);
    /* Vectors 32..255 are left not-present: with IF cleared and no PIC/APIC, no device IRQ fires;
     * a spurious one would itself fault (#GP/#DF), which is vectored and dumped. */
    g_puckIdtr.Limit = (unsigned short)(sizeof(g_puckIdt) - 1);
    g_puckIdtr.Base = (unsigned long long)&g_puckIdt[0];
    PuckLoadIdt(&g_puckIdtr);
}

/* ---------------------------------------------------------------------------------------
 * Local APIC (x2APIC) + interrupt delivery. The legacy 8259 PIC carries only our PIT timer; a real
 * device (virtio-net MSI-X next, and eventually amdgpu's interrupt ring) delivers through the LAPIC.
 * x2APIC reaches LAPIC registers via MSRs - no MMIO/UC mapping - and is what modern hardware (incl.
 * the Steam Deck's Zen2) uses. We keep the existing PIC timer alive by leaving LINT0 in virtual-wire
 * (ExtINT) mode, then prove the whole delivery path - vector -> IDT gate -> ISR -> EOI - with a
 * self-IPI, before any device is wired. First half of the IRQ stack the GPU host needs. */
#define MSR_IA32_APIC_BASE 0x1Bu
#define X2APIC_TPR     0x808u
#define X2APIC_EOI     0x80Bu
#define X2APIC_LVT_LINT0 0x835u
#define X2APIC_LVT_LINT1 0x836u
#define X2APIC_SVR     0x80Fu

extern void PuckSpuriousIsr(void); /* puck-efi-x64.asm */

static void PuckLapicEoi(void) { __writemsr(X2APIC_EOI, 0); }

/* Bring up the LAPIC in x2APIC mode (MSR-based, no MMIO/UC mapping; what the Steam Deck's Zen2 uses).
 * LINT0 stays in virtual-wire (ExtINT) mode so the existing 8259 PIT keeps passing through after the
 * LAPIC is enabled. Installs the spurious-vector handler so a stray spurious interrupt is harmless.
 * Returns 0 on success, -1 if the CPU lacks x2APIC. Must run before PuckInitTimer. */
static int PuckInitLapic(void)
{
    int regs[4];
    unsigned long long base;
    __cpuid(regs, 1);
    if (!((unsigned int)regs[2] & (1u << 21))) { SerialPuts("[apic] CPU lacks x2APIC\r\n"); return -1; }
    base = __readmsr(MSR_IA32_APIC_BASE);
    base |= (1ull << 11);                       /* EN: global LAPIC enable (xAPIC) */
    __writemsr(MSR_IA32_APIC_BASE, base);
    base |= (1ull << 10);                       /* EXTD: x2APIC mode (legal xAPIC->x2APIC step) */
    __writemsr(MSR_IA32_APIC_BASE, base);
    __writemsr(X2APIC_LVT_LINT0, 0x700u);       /* LINT0 = ExtINT: 8259 PIC IRQs (our PIT) pass through */
    __writemsr(X2APIC_LVT_LINT1, 0x400u);       /* LINT1 = NMI */
    __writemsr(X2APIC_TPR, 0);                  /* task priority 0: accept every vector */
    __writemsr(X2APIC_SVR, 0x100u | 0xFFu);     /* SVR bit 8 = APIC software-enable; spurious vec 0xFF */
    PuckSetIdtEntry(0xFF, (unsigned long long)PuckSpuriousIsr);
    return 0;
}

/* ---------------------------------------------------------------------------------------
 * MSI-X on virtio-net: prove a REAL device interrupt (not polling) goes MSI-X table -> LAPIC ->
 * our IDT vector -> EOI. We route the TX queue (TX completion is deterministic - we post the frame)
 * to MSI-X entry 0 -> vector 0x42, fire one frame in a brief IF=1 window, and confirm the ISR ran.
 * Then we tear MSI-X back down to the legacy layout BEFORE lwIP starts, so the proven polling
 * DHCP/TLS path sees exactly the device it sees today. This is the device-side half of the IRQ stack
 * the GPU needs (amdgpu's interrupt ring reuses this exact stub + table-programming pattern). */
extern void PuckNetMsixIsr(void); /* puck-efi-x64.asm */
static volatile int g_netMsixHit;
void PuckNetMsixHandler(void) { g_netMsixHit = 1; PuckLapicEoi(); } /* called from the asm stub */

void PuckNetMsixProve(void)
{
    int b = g_netBus, d = g_netDev, f = 0, i;
    unsigned short io = g_netIoBase;
    unsigned int cap = 0, guard = 0, e = 0;          /* MSI-X table entry 0 */
    unsigned long c0 = 0, tbl, barReg;
    unsigned int bir, toff, found = 0;
    unsigned long long tableBar;
    volatile unsigned int *msixTable;
    unsigned char m1, m2;
    const unsigned int vector = 0x42;

    /* 1. Walk the capability list for MSI-X (id 0x11). */
    {
        unsigned long sr = PuckPciRead32(b, d, f, 0x04);
        if (!((sr >> 16) & (1u << 4))) { SerialPuts("[net] no cap list; MSI-X proof skipped\r\n"); return; }
        cap = PuckPciRead32(b, d, f, 0x34) & 0xFC;
        while (cap >= 0x40 && guard++ < 48)
        {
            unsigned long c = PuckPciRead32(b, d, f, (int)cap);
            if ((c & 0xFF) == 0x11) { c0 = c; found = 1; break; }
            cap = (c >> 8) & 0xFC;
        }
    }
    if (!found) { SerialPuts("[net] no MSI-X cap; proof skipped\r\n"); return; }

    /* 2. Decode + validate the table BAR (must be 32-bit memory, programmed; <4GiB => identity-mapped). */
    tbl = PuckPciRead32(b, d, f, (int)cap + 4);
    bir = (unsigned int)(tbl & 0x7);
    toff = (unsigned int)(tbl & ~0x7u);
    barReg = PuckPciRead32(b, d, f, 0x10 + (int)bir * 4);
    if ((barReg & 0x1) || ((barReg >> 1) & 0x3) || ((barReg & ~0xFul) == 0))
    { SerialPuts("[net] MSI-X table BAR not 32-bit mem / unprogrammed; abort\r\n"); return; }
    tableBar = (unsigned long long)(barReg & ~0xFul);
    msixTable = (volatile unsigned int *)(tableBar + toff);

    /* 3. Program entry 0 MASKED first, with the x2APIC physical-delivery message to the BSP (APIC 0). */
    msixTable[e * 4 + 3] = 1;             /* vector control: masked */
    msixTable[e * 4 + 0] = 0xFEE00000u;   /* addr lo: physical mode, dest APIC 0, no redirect */
    msixTable[e * 4 + 1] = 0x00000000u;   /* addr hi */
    msixTable[e * 4 + 2] = vector;        /* data: fixed delivery, edge, vector 0x42 */
    PuckMfence();
    if (msixTable[e * 4 + 2] != vector)   /* read-back: confirms the BAR is MMIO-backed */
    { SerialPuts("[net] MSI-X table BAR unbacked (read-back mismatch); abort\r\n"); return; }

    /* 4. Install the 0x42 IDT gate BEFORE enabling/routing (gate present before any message). */
    PuckSetIdtEntry(0x42, (unsigned long long)PuckNetMsixIsr);

    /* 5. Enable MSI-X, FORCING Function-Mask (bit 14) clear (firmware/iPXE may have set it). */
    PuckPciWrite32(b, d, f, (int)cap, (c0 | (1u << 31)) & ~(1u << 30));

    /* 6. Route TX queue 1 -> entry 0. Offsets 0x14/0x16 exist only now that MSI-X is on. Read-back
     *    0xFFFF == device refused. */
    __outword((unsigned short)(io + VIRTIO_PCI_QUEUE_SEL), 1);
    __outword((unsigned short)(io + VIRTIO_MSI_QUEUE_VECTOR), (unsigned short)e);
    if (__inword((unsigned short)(io + VIRTIO_MSI_QUEUE_VECTOR)) != (unsigned short)e)
    { SerialPuts("[net] device refused queue MSI-X vector; abort\r\n"); PuckPciDisableMsix(b, d, f); return; }
    __outword((unsigned short)(io + VIRTIO_MSI_CONFIG_VECTOR), VIRTIO_MSI_NO_VECTOR);

    /* 7. Used-buffer interrupts not suppressed, then UNMASK the table entry LAST. */
    g_netTx.avail->flags = 0; /* VRING_AVAIL_F_NO_INTERRUPT clear */
    msixTable[e * 4 + 3] = 0;
    PuckMfence();

    /* 8. Fire: post a TX frame in a brief IF=1 window. The PIT is already armed, so SAVE/RESTORE the
     *    8259 mask (masking without restore would kill preemption for the rest of boot). */
    {
        static unsigned char frame[42]; /* minimal ARP-shaped probe; QEMU consumes the TX regardless */
        for (i = 0; i < 6; i++) frame[i] = 0xFF;            /* dst broadcast */
        for (i = 0; i < 6; i++) frame[6 + i] = g_netMac[i]; /* src = our MAC */
        frame[12] = 0x08; frame[13] = 0x06;                 /* ethertype ARP */
        frame[14] = 0x00; frame[15] = 0x01;                 /* HTYPE ethernet */
        frame[16] = 0x08; frame[17] = 0x00;                 /* PTYPE IPv4 */
        frame[18] = 6; frame[19] = 4; frame[21] = 0x01;     /* HLEN/PLEN, op=request */
        for (i = 0; i < 6; i++) frame[22 + i] = g_netMac[i];/* sender HW */

        g_netMsixHit = 0;
        m1 = __inbyte(0x21); m2 = __inbyte(0xA1);
        __outbyte(0x21, 0xFF); __outbyte(0xA1, 0xFF);       /* mask PIC for the window */
        _enable();                                          /* IF = 1 */
        PuckNetTx(frame, sizeof frame);                  /* posts + notifies + polls used ring */
        for (i = 0; i < 2000000 && !g_netMsixHit; i++)
            ;
        _disable();                                         /* IF = 0 */
        __outbyte(0x21, m1); __outbyte(0xA1, m2);           /* RESTORE: IRQ0 re-unmasked, PIT keeps preempting */
    }

    SerialPuts(g_netMsixHit
        ? "[net] virtio-net TX MSI-X -> vector 0x42 -> ISR -> EOI verified\r\n"
        : "[net] virtio-net MSI-X FAILED (no delivery)\r\n");

    /* 9. Teardown BEFORE lwIP: unroute, re-mask, disable MSI-X -> legacy layout (0x14 = MAC) restored. */
    __outword((unsigned short)(io + VIRTIO_PCI_QUEUE_SEL), 1);
    __outword((unsigned short)(io + VIRTIO_MSI_QUEUE_VECTOR), VIRTIO_MSI_NO_VECTOR);
    msixTable[e * 4 + 3] = 1;
    PuckPciDisableMsix(b, d, f);
}

/* Preemptive timer. Remap the 8259 PIC (IRQ0-15 -> vectors 0x20-0x2F; the default 0x08-0x0F collide
 * with CPU exceptions), point vector 0x20 at the asm timer ISR, program PIT channel 0 to ~100 Hz,
 * and unmask IRQ0. From here a thread running at ring 3 (IF=1) is preempted ~100x/s; the kernel runs
 * with IF=0 (we _disable() after ExitBootServices) so it is never itself interrupted. */
extern void PuckTimerIsr(void); /* puck-efi-x64.asm */

void PuckInitTimer(void)
{
    __outbyte(0x20, 0x11); /* ICW1: init, expect ICW4 (master) */
    __outbyte(0xA0, 0x11); /* ICW1 (slave) */
    __outbyte(0x21, 0x20); /* ICW2: master vector base 0x20 */
    __outbyte(0xA1, 0x28); /* ICW2: slave  vector base 0x28 */
    __outbyte(0x21, 0x04); /* ICW3: master has a slave on IRQ2 */
    __outbyte(0xA1, 0x02); /* ICW3: slave cascade identity */
    __outbyte(0x21, 0x01); /* ICW4: 8086 mode */
    __outbyte(0xA1, 0x01);
    __outbyte(0x21, 0xFE); /* OCW1: master unmasks IRQ0 (timer) only */
    __outbyte(0xA1, 0xFF); /* OCW1: slave all masked */

    PuckSetIdtEntry(0x20, (unsigned long long)PuckTimerIsr);

    /* PIT channel 0, access lo/hi, mode 2 (rate generator), binary; divisor ~11932 -> ~100 Hz. */
    {
        unsigned int divisor = 1193182u / 100u;
        __outbyte(0x43, 0x34);
        __outbyte(0x40, (unsigned char)(divisor & 0xFF));
        __outbyte(0x40, (unsigned char)((divisor >> 8) & 0xFF));
    }
}

/* ---------------------------------------------------------------------------------------
 * Our own page tables. Stop depending on the firmware's mappings (and pre-empt firmware
 * that NX-maps conventional memory) by building a fresh 4-level identity map with 2 MiB RWX large
 * pages and switching CR3 onto it. Page-table pages come from our own RAM, so they must be
 * allocated page-aligned (the managed heap is only 16-byte aligned) - hence PuckAllocPages. */
#define PUCK_PTE_P  0x01ull   /* present                                  */
#define PUCK_PTE_RW 0x02ull   /* writable                                 */
#define PUCK_PTE_US 0x04ull   /* user-accessible (set on guest pages, G3) */
#define PUCK_PTE_PS 0x80ull   /* page size: 2 MiB leaf at the PD level     */
#define PUCK_PTE_PWT   0x08ull   /* bit 3                                          */
#define PUCK_PTE_PCD   0x10ull   /* bit 4                                          */
#define PUCK_PTE_PAT4K 0x80ull   /* bit 7  -- 4 KiB PTE PAT bit (== PS at PDE level) */
#define PUCK_PDE_PAT2M 0x1000ull /* bit 12 -- 2 MiB PDE PAT bit (NOT bit 7)          */
#define PUCK_IA32_PAT  0x277
/* PA0=WB, PA1=WT, PA2=UC-, PA3=UC, PA4=WB, PA5=WT, PA6=WC, PA7=UC. Only PA6 differs from the
 * power-on default (UC- -> WC); PA0=WB keeps every existing identity leaf unchanged. */
#define PUCK_PAT_VALUE 0x0001040600070406ull

typedef enum { PUCK_MT_WB = 0, PUCK_MT_WC = 1, PUCK_MT_UC = 2 } PuckMemType;

/* 4 KiB-aligned bump over the conventional heap; pages are permanent (no GC, never freed). Must run
 * before managed allocation churns g_heapPtr - we call it right after the IDT, before GC statics. */
static unsigned long long PuckAllocPages(unsigned long long pages)
{
    unsigned long long addr = ((unsigned long long)g_heapPtr + 4095ull) & ~4095ull;
    unsigned long long bytes = pages * 4096ull;
    unsigned long long i;

    if (addr == 0 || (unsigned char *)addr + bytes > g_heapEnd)
        PuckHang(); /* out of memory for page tables */

    for (i = 0; i < bytes; i++)
        ((unsigned char *)addr)[i] = 0;

    g_heapPtr = (unsigned char *)addr + bytes;
    return addr;
}

/* The PML4 we build, kept so PuckSetUserAccessible can flip US on the guest's pages. */
static unsigned long long *g_puckPml4;

void PuckInitPaging(void)
{
    /* Identity-map the low 4 GiB with 2 MiB RWX pages: covers all QEMU RAM (our image, heap, stacks,
     * guest) plus low MMIO for the -m sizes we run. 1 PML4 + 1 PDPT + 4 PD = 6 pages (24 KiB). A
     * machine with >4 GiB of RAM placed high would need this range widened. */
    unsigned long long *pml4 = (unsigned long long *)PuckAllocPages(1);
    unsigned long long *pdpt = (unsigned long long *)PuckAllocPages(1);
    int g, i;

    /* US is set on the upper levels (PML4/PDPT) so they never gate user access; the per-2 MiB PD
     * leaves stay supervisor (US=0) by default. PuckSetUserAccessible then flips US on exactly
     * the guest's PD entries, making only those 2 MiB pages reachable from ring 3. */
    pml4[0] = (unsigned long long)pdpt | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_US;

    for (g = 0; g < 4; g++) /* four 1 GiB slots = 4 GiB */
    {
        unsigned long long *pd = (unsigned long long *)PuckAllocPages(1);
        pdpt[g] = (unsigned long long)pd | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_US;
        for (i = 0; i < 512; i++)
        {
            unsigned long long phys = (unsigned long long)g * 0x40000000ull
                                    + (unsigned long long)i * 0x200000ull;
            pd[i] = phys | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_PS; /* 2 MiB, RWX, supervisor */
        }
    }

    g_puckPml4 = pml4;
    PuckWriteCr3((unsigned long long)pml4); /* identity map => physical == virtual */
}

/* Flip US on every 2 MiB PD leaf covering [addr, addr+size), making those pages reachable from
 * ring 3 (the guest image + its user stack). Then reload CR3 to flush stale TLB entries. Exported
 * so the managed loader can mark the guest's pages before dropping to ring 3. The 2 MiB granularity
 * means neighbouring heap in the same large page also becomes user-readable - acceptable until the
 * loader places the guest in its own page-aligned region. */
void PuckSetUserAccessible(unsigned long long addr, unsigned long long size)
{
    unsigned long long start = addr & ~0x1FFFFFull;
    unsigned long long end = (addr + size + 0x1FFFFFull) & ~0x1FFFFFull;
    unsigned long long a;

    for (a = start; a < end; a += 0x200000ull)
    {
        unsigned long long pdpti = (a >> 30) & 0x1FF;
        unsigned long long pdi = (a >> 21) & 0x1FF;
        unsigned long long *pdpt = (unsigned long long *)(g_puckPml4[(a >> 39) & 0x1FF] & ~0xFFFull);
        unsigned long long *pd = (unsigned long long *)(pdpt[pdpti] & ~0xFFFull);
        pd[pdi] |= PUCK_PTE_US;
    }

    PuckWriteCr3((unsigned long long)g_puckPml4); /* flush the TLB */
}

/* ---------------------------------------------------------------------------------------
 * PAT (Page Attribute Table) + MMIO region typing. A GPU needs its register BARs mapped UC and its
 * VRAM aperture/framebuffer mapped WC; our identity map is all WB. PAT lets a page select a cache
 * type via the PWT/PCD/PAT bits without touching MTRRs. We set IA32_PAT once (PA6=WC, PA7=UC; the
 * low slots keep their power-on types so every existing WB leaf is unchanged). NOTE: a green QEMU
 * run only proves the page-table edits + MSR + flush don't fault - QEMU guest RAM is host RAM, so
 * WC ~= UC ~= WB there. Real WC/UC semantics are Steam Deck (Van Gogh) only. */
void PuckInitPat(void)
{
    int cpu[4];
    __cpuid(cpu, 1);
    if (!((unsigned int)cpu[3] & (1u << 16))) /* CPUID.01H:EDX.PAT[16] */
        PuckHang();                        /* every QEMU/Van Gogh target has PAT */
    __writemsr(PUCK_IA32_PAT, PUCK_PAT_VALUE);
    /* No flush needed: PA0..PA3 keep their power-on types and no live leaf references PA4..PA7 yet. */
}

static unsigned long long PuckPteCacheBits(PuckMemType t)
{
    if (t == PUCK_MT_WC) return PUCK_PTE_PAT4K | PUCK_PTE_PCD;                   /* 0x90 */
    if (t == PUCK_MT_UC) return PUCK_PTE_PAT4K | PUCK_PTE_PCD | PUCK_PTE_PWT; /* 0x98 */
    return 0;
}
static unsigned long long PuckPdeCacheBits(PuckMemType t)
{
    if (t == PUCK_MT_WC) return PUCK_PDE_PAT2M | PUCK_PTE_PCD;                   /* 0x1010 */
    if (t == PUCK_MT_UC) return PUCK_PDE_PAT2M | PUCK_PTE_PCD | PUCK_PTE_PWT; /* 0x1018 */
    return 0;
}

/* Map [phys, phys+size) at virtual==physical with the given cache type. Two paths:
 *  - phys+size <= 4 GiB: the identity map already covers it with a present 2 MiB PS leaf, so we
 *    RETUNE that PDE's PAT bits in place (no split). QEMU-verifiable.
 *  - phys+size  > 4 GiB: no PML4[1]/PDPT/PD exists (Deck 64-bit BAR); build the chain from
 *    PuckAllocPages with 4 KiB leaves. Deck-only; logged, never entered on default q35.
 * Identity-extend keeps the phys==virt invariant PuckDmaAlloc relies on. */
void *PuckMapMmio(unsigned long long phys, unsigned long long size, PuckMemType type)
{
    unsigned long long start, end, a;

    if (phys + size < phys) PuckHang(); /* 64-bit overflow guard */

    if (phys + size <= 0x100000000ull)
    {
        unsigned long long bits = PuckPdeCacheBits(type);
        start = phys & ~0x1FFFFFull;
        end = (phys + size + 0x1FFFFFull) & ~0x1FFFFFull;
        for (a = start; a < end; a += 0x200000ull)
        {
            unsigned long long *pdpt = (unsigned long long *)(g_puckPml4[(a >> 39) & 0x1FF] & ~0xFFFull);
            unsigned long long *pd = (unsigned long long *)(pdpt[(a >> 30) & 0x1FF] & ~0xFFFull);
            unsigned long long i2 = (a >> 21) & 0x1FF;
            pd[i2] = (pd[i2] & ~(PUCK_PDE_PAT2M | PUCK_PTE_PCD | PUCK_PTE_PWT)) | bits;
        }
    }
    else
    {
        unsigned long long bits = PuckPteCacheBits(type);
        SerialPuts("[mmio] high BAR (>4GiB): building page-table chain with 4KiB leaves\r\n");
        start = phys & ~0xFFFull;
        end = (phys + size + 0xFFFull) & ~0xFFFull;
        for (a = start; a < end; a += 0x1000ull)
        {
            unsigned long long i4 = (a >> 39) & 0x1FF, i3 = (a >> 30) & 0x1FF;
            unsigned long long i2 = (a >> 21) & 0x1FF, i1 = (a >> 12) & 0x1FF;
            unsigned long long *pdpt, *pd, *pt;
            if (!(g_puckPml4[i4] & PUCK_PTE_P))
                g_puckPml4[i4] = PuckAllocPages(1) | PUCK_PTE_P | PUCK_PTE_RW;
            pdpt = (unsigned long long *)(g_puckPml4[i4] & ~0xFFFull);
            if (!(pdpt[i3] & PUCK_PTE_P))
                pdpt[i3] = PuckAllocPages(1) | PUCK_PTE_P | PUCK_PTE_RW;
            pd = (unsigned long long *)(pdpt[i3] & ~0xFFFull);
            if (pd[i2] & PUCK_PTE_PS) PuckHang(); /* a PS leaf >4GiB == real RAM overlap: refuse */
            if (!(pd[i2] & PUCK_PTE_P))
                pd[i2] = PuckAllocPages(1) | PUCK_PTE_P | PUCK_PTE_RW;
            pt = (unsigned long long *)(pd[i2] & ~0xFFFull);
            pt[i1] = a | PUCK_PTE_P | PUCK_PTE_RW | bits;
        }
    }

    /* SDM region-retype order: flush the TLB FIRST (old type no longer translatable), THEN write
     * back + invalidate caches so no stale line of the old type can be refilled. */
    PuckWriteCr3((unsigned long long)g_puckPml4);
    PuckWbinvd();
    return (void *)phys;
}

/* ---------------------------------------------------------------------------------------
 * GOP framebuffer text console. The Steam Deck has no serial port, so the boot is read off the
 * panel: capture the firmware's framebuffer (EFI_GRAPHICS_OUTPUT_PROTOCOL) before ExitBootServices,
 * map it write-combining once our paging is up, and mirror the serial byte stream onto it with an
 * 8x16 font. PUCK_FB_ROTATION compensates for a panel mounted in portrait. White-on-black is
 * pixel-format agnostic: 0xFFFFFFFF and 0 read identically whether the firmware chose RGB or BGR. */
extern const unsigned char g_puckFont8x16[95][16]; /* puck-font.c (regen-font.ps1) */

#ifndef PUCK_FB_ROTATION
#define PUCK_FB_ROTATION 0   /* 0/90/180/270; QEMU std-VGA is landscape, the Deck panel may differ */
#endif

static struct {
    unsigned int *base;          /* mapped framebuffer; 0 until PuckFbInit (FbWriteByte no-ops) */
    unsigned long long phys;     /* framebuffer physical base, captured pre-ExitBootServices */
    unsigned int width, height;  /* framebuffer pixel dimensions */
    unsigned int pitch;          /* pixels per scan line (>= width) */
    unsigned int cols, rows;     /* text grid, logical (post-rotation) */
    unsigned int cx, cy;         /* cursor, in character cells */
} g_fb;

typedef efi_status_t (*efi_locate_protocol_t)(const void *guid, void *reg, void **iface);

/* Read the framebuffer base + geometry while boot services live (the WC mapping waits for our page
 * tables). A no-GOP environment (headless VM) just leaves the console off. */
static void PuckGopCapture(void *bs)
{
    static const unsigned char gopGuid[16] =
        { 0xde, 0xa9, 0x42, 0x90, 0xdc, 0x23, 0x38, 0x4a, 0x96, 0xfb, 0x7a, 0xde, 0xd0, 0x80, 0x51, 0x6a };
    efi_locate_protocol_t locate = (efi_locate_protocol_t)EfiField(bs, EFI_BS_LOCATEPROTOCOL_OFFSET);
    void *gop = 0, *mode, *info;
    if (locate(gopGuid, 0, &gop) != 0 || !gop)
    { SerialPuts("[fb] no GOP found (headless)\r\n"); return; }
    mode = *(void **)((unsigned char *)gop + 24);   /* EFI_GRAPHICS_OUTPUT_PROTOCOL.Mode */
    info = *(void **)((unsigned char *)mode + 8);    /* Mode.Info */
    g_fb.phys   = *(unsigned long long *)((unsigned char *)mode + 24); /* Mode.FrameBufferBase */
    g_fb.width  = *(unsigned int *)((unsigned char *)info + 4);        /* HorizontalResolution */
    g_fb.height = *(unsigned int *)((unsigned char *)info + 8);        /* VerticalResolution   */
    g_fb.pitch  = *(unsigned int *)((unsigned char *)info + 32);       /* PixelsPerScanLine    */
    SerialPuts("[fb] GOP framebuffer base="); SerialPutHex64(g_fb.phys);
    SerialPuts(" w="); SerialPutHex64(g_fb.width); SerialPuts(" h="); SerialPutHex64(g_fb.height);
    SerialPuts(" pitch="); SerialPutHex64(g_fb.pitch); SerialPuts("\r\n");
}

static void PuckFbInit(void)
{
    unsigned long long i, n;
    if (g_fb.phys == 0 || g_fb.pitch == 0) return;
    g_fb.base = (unsigned int *)PuckMapMmio(g_fb.phys,
        (unsigned long long)g_fb.pitch * g_fb.height * 4ull, PUCK_MT_WC);
    if ((PUCK_FB_ROTATION % 180) == 0) { g_fb.cols = g_fb.width / 8; g_fb.rows = g_fb.height / 16; }
    else { g_fb.cols = g_fb.height / 8; g_fb.rows = g_fb.width / 16; }
    n = (unsigned long long)g_fb.pitch * g_fb.height;
    for (i = 0; i < n; i++) g_fb.base[i] = 0;
    g_fb.cx = g_fb.cy = 0;
    SerialPuts("[fb] framebuffer console online\r\n");
}

static void FbPutPixel(unsigned int lx, unsigned int ly, unsigned int color)
{
    unsigned int W = g_fb.width, H = g_fb.height, px, py;
    switch (PUCK_FB_ROTATION)
    {
        case 90:  px = W - 1 - ly; py = lx;         break;
        case 180: px = W - 1 - lx; py = H - 1 - ly; break;
        case 270: px = ly;         py = H - 1 - lx; break;
        default:  px = lx;         py = ly;         break;
    }
    if (px >= W || py >= H) return;
    g_fb.base[py * g_fb.pitch + px] = color;
}

static void FbDrawGlyph(unsigned int cx, unsigned int cy, unsigned char ch)
{
    const unsigned char *gl = (ch >= 0x20 && ch <= 0x7E) ? g_puckFont8x16[ch - 0x20] : g_puckFont8x16[0];
    unsigned int row, col, bx = cx * 8, by = cy * 16;
    for (row = 0; row < 16; row++)
        for (col = 0; col < 8; col++)
            FbPutPixel(bx + col, by + row, (gl[row] & (0x80u >> col)) ? 0xFFFFFFFFu : 0u);
}

/* Mirror of the serial byte stream. Wraps to the top on overflow (no scrollback yet). */
static void FbWriteByte(unsigned char b)
{
    if (g_fb.base == 0) return;
    if (b == '\n') { g_fb.cx = 0; g_fb.cy++; }
    else if (b == '\r') { g_fb.cx = 0; }
    else if (b == '\t') { g_fb.cx = (g_fb.cx + 4u) & ~3u; }
    else { FbDrawGlyph(g_fb.cx, g_fb.cy, b); g_fb.cx++; }
    if (g_fb.cx >= g_fb.cols) { g_fb.cx = 0; g_fb.cy++; }
    if (g_fb.cy >= g_fb.rows)
    {
        unsigned long long i, n = (unsigned long long)g_fb.pitch * g_fb.height;
        for (i = 0; i < n; i++) g_fb.base[i] = 0;
        g_fb.cx = g_fb.cy = 0;
    }
}

/* ---------------------------------------------------------------------------------------
 * UEFI image entry. Take the machine from the firmware, then run the managed program
 * self-hosted. The firmware calls this with EFIAPI EfiEntry(EFI_HANDLE, EFI_SYSTEM_TABLE*). */
extern int __managed__Main(void);

/* Find the base+size of the largest EfiConventionalMemory region in a memory map. */
static void FindLargestConventional(unsigned char *map, unsigned long long mapSize,
                                    unsigned long long descSize,
                                    unsigned long long *outBase, unsigned long long *outSize)
{
    unsigned long long best = 0, bestBase = 0;
    unsigned char *p;

    for (p = map; p + descSize <= map + mapSize; p += descSize)
    {
        unsigned int type = *(unsigned int *)p;
        if (type == EFI_MEMTYPE_CONVENTIONAL)
        {
            unsigned long long physStart = *(unsigned long long *)(p + 8);
            unsigned long long pages = *(unsigned long long *)(p + 24);
            unsigned long long bytes = pages * EFI_PAGE_SIZE;
            if (bytes > best)
            {
                best = bytes;
                bestBase = physStart;
            }
        }
    }

    *outBase = bestBase;
    *outSize = best;
}

/* ---------------------------------------------------------------------------------------
 * Preload a dynamic guest's shared-library closure from the ESP (the FAT boot volume) into RAM,
 * BEFORE ExitBootServices (the firmware filesystem is gone afterward; EfiLoaderData buffers persist
 * and are skipped by FindLargestConventional, so they survive into the running kernel). A big closure
 * (RADV is ~24 MiB) would bloat the embedded GuestElf, so we stage it on the ESP and read it here via
 * the EFI Simple File System protocol, registering each file in the VFS for ld-musl to mmap in-guest. */
#define EFI_BS_HANDLEPROTOCOL_OFFSET   152
#define EFI_LOADED_IMAGE_DEVICEHANDLE  24
#define EFI_SFS_OPENVOLUME_OFF         8
#define EFI_FILE_OPEN_OFF              8
#define EFI_FILE_CLOSE_OFF             16
#define EFI_FILE_READ_OFF             32
#define EFI_FILE_GETPOS_OFF           48
#define EFI_FILE_SETPOS_OFF           56

static const unsigned char g_efiLoadedImageGuid[16] =
    { 0xA1, 0x31, 0x1B, 0x5B, 0x62, 0x95, 0xD2, 0x11, 0x8E, 0x3F, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B };
static const unsigned char g_efiSfsGuid[16] =
    { 0x22, 0x5B, 0x4E, 0x96, 0x59, 0x64, 0xD2, 0x11, 0x8E, 0x39, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B };

typedef efi_status_t (*efi_handle_protocol_t)(void *handle, const void *guid, void **iface);
typedef efi_status_t (*efi_open_volume_t)(void *sfs, void **root);
typedef efi_status_t (*efi_file_open_t)(void *file, void **newHandle, const unsigned short *name,
                                        unsigned long long mode, unsigned long long attr);
typedef efi_status_t (*efi_file_read_t)(void *file, unsigned long long *size, void *buf);
typedef efi_status_t (*efi_file_pos_t)(void *file, unsigned long long *pos);
typedef efi_status_t (*efi_file_setpos_t)(void *file, unsigned long long pos);
typedef efi_status_t (*efi_file_close_t)(void *file);

extern void PuckVfsAddFile(const char *path, const void *content, unsigned long long len);

/* The dynamic guest's shared-library closure, staged on the ESP under \radv\. Each name is the exact
 * NEEDED soname so ld-musl resolves it at /lib/<name> in the synthetic VFS. ASCII here; the CHAR16 ESP
 * path (for the EFI file API) and the /lib guest path are built per-entry at load time. This is the
 * lean LLVM-free/WSI-free RADV closure (see radv/build-radv-musl.sh) -- 9 .so's; libc is ld-musl
 * itself (embedded as the guest interp), so it is not preloaded here. */
static const char *const g_puckPreloadNames[] = {
    "libvulkan_radeon.so",   /* the RADV ICD (NEEDED directly by the vktest guest) */
    "libdrm_amdgpu.so.1",
    "libdrm.so.2",
    "libelf.so.1",
    "libz.so.1",
    "libzstd.so.1",          /* NEEDED by libelf */
    "libSPIRV-Tools.so",
    "libstdc++.so.6",
    "libgcc_s.so.1",
};

/* Build "\radv\<name>" as CHAR16 (UTF-16) for EFI_FILE.Open. out must hold >= 6 + strlen(name) + 1. */
static void PuckBuildEspPath(const char *name, unsigned short *out)
{
    static const char prefix[] = "\\radv\\";
    int j = 0, k;
    for (k = 0; prefix[k]; k++) out[j++] = (unsigned short)(unsigned char)prefix[k];
    for (k = 0; name[k]; k++) out[j++] = (unsigned short)(unsigned char)name[k];
    out[j] = 0;
}
/* Build "/lib/<name>" as ASCII for the VFS. out must hold >= 5 + strlen(name) + 1. */
static void PuckBuildLibPath(const char *name, char *out)
{
    static const char prefix[] = "/lib/";
    int j = 0, k;
    for (k = 0; prefix[k]; k++) out[j++] = prefix[k];
    for (k = 0; name[k]; k++) out[j++] = name[k];
    out[j] = 0;
}

static void PuckEfiPreloadSos(void *imageHandle, void *bs)
{
    efi_handle_protocol_t handleProtocol = (efi_handle_protocol_t)EfiField(bs, EFI_BS_HANDLEPROTOCOL_OFFSET);
    efi_allocate_pool_t allocPool = (efi_allocate_pool_t)EfiField(bs, EFI_BS_ALLOCATEPOOL_OFFSET);
    void *li = 0, *sfs = 0, *root = 0, *deviceHandle;
    efi_open_volume_t openVolume;
    unsigned int n = (unsigned int)(sizeof(g_puckPreloadNames) / sizeof(g_puckPreloadNames[0])), i;

    if (handleProtocol(imageHandle, g_efiLoadedImageGuid, &li) != 0 || !li)
    { SerialPuts("[esp] no LoadedImage protocol\r\n"); return; }
    deviceHandle = EfiField(li, EFI_LOADED_IMAGE_DEVICEHANDLE);
    if (handleProtocol(deviceHandle, g_efiSfsGuid, &sfs) != 0 || !sfs)
    { SerialPuts("[esp] no SimpleFileSystem protocol\r\n"); return; }
    openVolume = (efi_open_volume_t)EfiField(sfs, EFI_SFS_OPENVOLUME_OFF);
    if (openVolume(sfs, &root) != 0 || !root)
    { SerialPuts("[esp] OpenVolume failed\r\n"); return; }

    for (i = 0; i < n; i++)
    {
        efi_file_open_t fopen = (efi_file_open_t)EfiField(root, EFI_FILE_OPEN_OFF);
        const char *name = g_puckPreloadNames[i];
        unsigned short espPath[128];
        char libPath[128];
        void *file = 0, *buf = 0;
        unsigned long long size = 0;
        efi_file_setpos_t setpos;
        efi_file_pos_t getpos;
        efi_file_read_t fread;
        efi_file_close_t fclose;
        PuckBuildEspPath(name, espPath);
        PuckBuildLibPath(name, libPath);
        if (fopen(root, &file, espPath, 1, 0) != 0 || !file)
        { SerialPuts("[esp] open failed: "); SerialPuts(libPath); SerialPuts("\r\n"); continue; }
        setpos = (efi_file_setpos_t)EfiField(file, EFI_FILE_SETPOS_OFF);
        getpos = (efi_file_pos_t)EfiField(file, EFI_FILE_GETPOS_OFF);
        fread = (efi_file_read_t)EfiField(file, EFI_FILE_READ_OFF);
        fclose = (efi_file_close_t)EfiField(file, EFI_FILE_CLOSE_OFF);
        setpos(file, 0xFFFFFFFFFFFFFFFFull); /* seek to EOF to get the size */
        getpos(file, &size);
        setpos(file, 0);
        if (size == 0 || allocPool(EFI_MEMTYPE_LOADER_DATA, size, &buf) != 0 || !buf)
        { SerialPuts("[esp] alloc failed: "); SerialPuts(libPath); SerialPuts("\r\n"); fclose(file); continue; }
        if (fread(file, &size, buf) != 0) { SerialPuts("[esp] read failed: "); SerialPuts(libPath); SerialPuts("\r\n"); fclose(file); continue; }
        fclose(file);
        PuckVfsAddFile(libPath, buf, size);
        SerialPuts("[esp] preloaded "); SerialPuts(libPath); SerialPuts("\r\n");
    }
}

unsigned long long EfiEntry(void *imageHandle, void *systemTable)
{
    void *bs;
    efi_allocate_pool_t allocPool;
    efi_get_memory_map_t getMap;
    efi_exit_boot_services_t exitBS;
    unsigned long long mapSize = 0, mapKey = 0, descSize = 0, finalMapSize;
    unsigned int descVersion = 0;
    void *mapBuf = 0;
    unsigned long long heapBase = 0, heapSize = 0;

    g_puckEfiSystemTable = systemTable;
    SerialInit();
    SerialPuts("[boot] Puck.BareMetal UEFI image entered; firmware boot services are up.\r\n");

    /* Capture the ACPI RSDP from the UEFI configuration table while it is still valid (the ECAM base
     * is parsed out of MCFG later, after paging). Must happen before ExitBootServices. */
    PuckAcpiCaptureRsdp(systemTable);

    bs = EfiField(systemTable, EFI_ST_BOOTSERVICES_OFFSET);

    /* Preload the dynamic guest's .so closure from the ESP into RAM + register it in the VFS, while
     * the firmware filesystem is still available. */
    PuckEfiPreloadSos(imageHandle, bs);

    /* Capture the GOP framebuffer while boot services are up (mapped after our paging is online).
     * On the Steam Deck this panel is the only way to observe the boot -- there is no serial port. */
    PuckGopCapture(bs);

    allocPool = (efi_allocate_pool_t)EfiField(bs, EFI_BS_ALLOCATEPOOL_OFFSET);
    getMap = (efi_get_memory_map_t)EfiField(bs, EFI_BS_GETMEMORYMAP_OFFSET);
    exitBS = (efi_exit_boot_services_t)EfiField(bs, EFI_BS_EXITBOOTSERVICES_OFF);

    /* Size the memory map, allocate a buffer with slack (AllocatePool itself perturbs the map),
     * then loop GetMemoryMap + ExitBootServices until the key is accepted (no allocation inside
     * the loop, so it converges immediately). */
    getMap(&mapSize, 0, &mapKey, &descSize, &descVersion); /* returns BUFFER_TOO_SMALL; sets sizes */
    mapSize += 8 * descSize;
    if (allocPool(EFI_MEMTYPE_LOADER_DATA, mapSize, &mapBuf) != 0 || mapBuf == 0)
        PuckHang();

    SerialPuts("[boot] memory map acquired; calling ExitBootServices...\r\n");

    for (;;)
    {
        finalMapSize = mapSize;
        if (getMap(&finalMapSize, mapBuf, &mapKey, &descSize, &descVersion) != 0)
            PuckHang();
        if (exitBS(imageHandle, mapKey) == 0)
            break; /* success: the firmware is now gone */
        /* stale key (rare): retry with a fresh map; buffer is pre-sized so no allocation here */
    }

    /* From here on there is no firmware: no boot services, no ConOut. We own the machine. */
    FindLargestConventional((unsigned char *)mapBuf, finalMapSize, descSize, &heapBase, &heapSize);
    if (heapSize == 0)
        PuckHang();

    g_heapStart = (unsigned char *)heapBase;
    g_heapPtr = (unsigned char *)heapBase;
    g_heapEnd = (unsigned char *)heapBase + heapSize;
    g_postExit = 1; /* RhpNewFast now bump-allocates from real RAM */

    SerialPuts("[kernel] ExitBootServices OK. Firmware gone - self-hosted on conventional RAM.\r\n");

    /* Run the whole kernel with interrupts disabled; only ring-3 threads get IF=1 (set by the iretq
     * resume), so the preemptive timer fires solely while a guest thread runs, never in the kernel. */
    _disable();

    /* Load our own GDT (so selector 0x08 = our ring-0 code), then an IDT so any subsequent fault
     * dumps the CPU state to serial instead of triple-faulting QEMU into a silent reboot loop. */
    PuckInitGdt();
    SerialPuts("[kernel] GDT loaded.\r\n");
    PuckInitIdt();
    SerialPuts("[kernel] IDT loaded; CPU exceptions now panic to serial.\r\n");

    /* Build our own identity-mapped page tables and switch CR3 onto them (the IDT above
     * makes a bad map a diagnosable #PF instead of a silent triple-fault). */
    PuckInitPaging();
    SerialPuts("[kernel] CR3 switched to our identity map (0-4GiB, 2MiB RWX).\r\n");

    /* GPU-host substrate (all QEMU-verified; see docs/amd-vulkan-plan.md). Each subsystem brings up
     * the real init a GPU driver needs and logs one "online" line. */
    PuckInitPat();
    /* Exercise the PAT/MapMmio path once: type the LAPIC/high-MMIO 2 MiB leaf UC (correct for that
     * region; we drive the LAPIC via x2APIC MSRs, so the retune is benign). */
    PuckMapMmio(0xFEE00000ull, 0x1000ull, PUCK_MT_UC);
    SerialPuts("[kernel] PAT armed (PA6=WC, PA7=UC); LAPIC/MMIO leaf typed UC.\r\n");

    /* Map the captured framebuffer WC and bring up the on-panel console; from here the serial log is
     * also rendered to the screen (the only console on the Deck). */
    PuckFbInit();

    PuckEcamBringUp();      /* PCIe ECAM (MMIO config) from ACPI MCFG: enumerate + reach ext config */

    if (PuckInitLapic() == 0) /* x2APIC: the path a GPU delivers MSI/MSI-X + its IH ring through */
        SerialPuts("[apic] x2APIC online (LAPIC interrupt delivery ready).\r\n");

    /* DMA-coherent allocator (where a GPU's rings/GTT/firmware live) + IOMMU detection. virtio-net's
     * vrings allocate through it, so the later TLS test is the real device-DMA proof. */
    PuckDetectIommu();
    SerialPuts("[dma] DMA-coherent allocator online (virtio-net rings DMA through it).\r\n");

    /* Initialize reference-typed statics now that the heap is live, so managed code (including
     * lazy .cctors such as ArrayPool<T>.Shared) can use them. */
    PuckInitGCStatics();

    /* Bring up the Linux SYSCALL ABI. The trampoline returns via sysretq (ring 3); the ring-3 guest
     * launched by managed Main is what exercises it. */
    PuckInitSyscalls();
    SerialPuts("[kernel] SYSCALL ABI online (LSTAR/STAR/SFMASK armed).\r\n");

    PuckInitClock();
    SerialPuts("[kernel] clock calibrated (RTC boot epoch + TSC freq); CLOCK_REALTIME live.\r\n");

    PuckInitTimer();
    SerialPuts("[kernel] preemptive timer armed (PIT ~100 Hz, IRQ0 -> vector 0x20).\r\n");

    {
        /* DHCP -> DNS -> TCP -> TLS handshake (cert-chain + hostname verified against the embedded
         * CA roots) -> HTTPS GET. The Let's Encrypt endpoint serves a cert chaining to ISRG Root X1,
         * one of the roots we embed, so a successful handshake proves real certificate validation. */
        extern void PuckNetTlsTest(const char *host, const char *path); /* puck-netif.c */
        PuckNetTlsTest("valid-isrgrootx1.letsencrypt.org", "/");
    }

    __managed__Main(); /* the managed program now runs on our memory + our console */

    SerialPuts("[kernel] managed main returned; halting.\r\n");
    PuckHang();
    return 0; /* EFI_SUCCESS (unreachable) */
}

/* ---------------------------------------------------------------------------------------
 * Block-memory intrinsics (no CRT). #pragma function stops MSVC turning the loops back into
 * calls to these. Prototypes must precede the pragma. */
typedef unsigned long long bm_size_t;

void *memset(void *dest, int value, bm_size_t count);
void *memcpy(void *dest, const void *src, bm_size_t count);
void *memmove(void *dest, const void *src, bm_size_t count);

#pragma function(memset, memcpy, memmove)

void *memset(void *dest, int value, bm_size_t count)
{
    unsigned char *d = (unsigned char *)dest;
    while (count--)
        *d++ = (unsigned char)value;
    return dest;
}

void *memcpy(void *dest, const void *src, bm_size_t count)
{
    unsigned char *d = (unsigned char *)dest;
    const unsigned char *s = (const unsigned char *)src;
    while (count--)
        *d++ = *s++;
    return dest;
}

void *memmove(void *dest, const void *src, bm_size_t count)
{
    unsigned char *d = (unsigned char *)dest;
    const unsigned char *s = (const unsigned char *)src;
    if (d < s)
        while (count--)
            *d++ = *s++;
    else
    {
        d += count;
        s += count;
        while (count--)
            *--d = *--s;
    }
    return dest;
}

/* String/mem helpers the vendored lwIP calls (no CRT under /NODEFAULTLIB). */
int memcmp(const void *a, const void *b, bm_size_t n)
{
    const unsigned char *p = (const unsigned char *)a, *q = (const unsigned char *)b;
    while (n--) { if (*p != *q) return (int)*p - (int)*q; p++; q++; }
    return 0;
}
bm_size_t strlen(const char *s)
{
    const char *p = s;
    while (*p) p++;
    return (bm_size_t)(p - s);
}
int strcmp(const char *a, const char *b)
{
    while (*a && (*a == *b)) { a++; b++; }
    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}
int strncmp(const char *a, const char *b, bm_size_t n)
{
    while (n && *a && (*a == *b)) { a++; b++; n--; }
    return (n == 0) ? 0 : (int)(unsigned char)*a - (int)(unsigned char)*b;
}
char *strchr(const char *s, int c)
{
    while (*s) { if (*s == (char)c) return (char *)s; s++; }
    return (c == 0) ? (char *)s : (char *)0;
}
char *strstr(const char *h, const char *n)
{
    if (!*n) return (char *)h;
    for (; *h; h++)
    {
        const char *a = h, *b = n;
        while (*a && *b && (*a == *b)) { a++; b++; }
        if (!*b) return (char *)h;
    }
    return (char *)0;
}
char *strcpy(char *d, const char *s)
{
    char *r = d;
    while ((*d++ = *s++) != 0) { }
    return r;
}
char *strncpy(char *d, const char *s, bm_size_t n)
{
    char *r = d;
    while (n && (*d = *s) != 0) { d++; s++; n--; }
    while (n--) *d++ = 0;
    return r;
}

/* ctype + atoi shims the vendored lwIP calls (ASCII; no locale, no CRT). */
int isdigit(int c)  { return c >= '0' && c <= '9'; }
int isxdigit(int c) { return isdigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
int islower(int c)  { return c >= 'a' && c <= 'z'; }
int isupper(int c)  { return c >= 'A' && c <= 'Z'; }
int isspace(int c)  { return c == ' ' || (c >= '\t' && c <= '\r'); }
int tolower(int c)  { return isupper(c) ? c + 32 : c; }
int toupper(int c)  { return islower(c) ? c - 32 : c; }
int atoi(const char *s)
{
    int sign = 1, v = 0;
    while (*s == ' ' || *s == '\t') s++;
    if (*s == '-') { sign = -1; s++; } else if (*s == '+') s++;
    while (*s >= '0' && *s <= '9') { v = v * 10 + (*s - '0'); s++; }
    return sign * v;
}
