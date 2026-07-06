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

/* 1 once a 16550 is confirmed at COM1, 0 if the port is absent (the Steam Deck has no UART). When
 * absent, SerialWriteByte must NOT poll the line-status register: a floating LSR can leave the
 * THR-empty bit clear forever and hang the very first putchar -- which is exactly what wedged the
 * Deck at the firmware logo. With no UART, output goes to the panel console only. */
static int g_serialPresent;

static void SerialInit(void)
{
    /* Probe via the scratch register (offset 7): a real 16550 round-trips whatever we write; floating
     * ISA I/O space does not. Two patterns guard against a bus that happens to echo one value. */
    int ok;
    __outbyte(COM1 + 7, 0xA5);
    ok = (__inbyte(COM1 + 7) == 0xA5);
    __outbyte(COM1 + 7, 0x5A);
    ok = ok && (__inbyte(COM1 + 7) == 0x5A);
    g_serialPresent = ok;
    if (!g_serialPresent)
        return; /* no UART: SerialWriteByte skips the port and mirrors to the framebuffer only */

    __outbyte(COM1 + 1, 0x00); /* disable interrupts            */
    __outbyte(COM1 + 3, 0x80); /* enable DLAB (set baud divisor) */
    __outbyte(COM1 + 0, 0x01); /* divisor low  = 1 -> 115200     */
    __outbyte(COM1 + 1, 0x00); /* divisor high = 0               */
    __outbyte(COM1 + 3, 0x03); /* 8 bits, no parity, 1 stop      */
    __outbyte(COM1 + 2, 0xC7); /* enable+clear FIFO, 14B trigger */
    __outbyte(COM1 + 4, 0x0B); /* RTS/DSR set, OUT2              */
}

static void FbWriteByte(unsigned char b); /* the GOP framebuffer console (no-op until captured) */

/* Serial/panel output is also teed into this RAM buffer so the whole boot log can be persisted to a
 * UEFI variable at the end and written to \PuckLog.txt on the ESP at the NEXT boot (see PuckLog*).
 * The Deck has no serial port and its panel scrolls past one screen, so this is how the log reaches
 * a dev box as exact text instead of photographs. */
static char g_logBuf[24576];
static unsigned int g_logLen;

static void SerialWriteByte(unsigned char b)
{
    if (g_serialPresent)
    {
        unsigned int spin = 100000; /* bounded so a wedged/absent UART can never hang the boot */
        while (spin-- && (__inbyte(COM1 + 5) & 0x20) == 0) /* wait for THR empty */
            ;
        __outbyte(COM1, b);
    }
    if (g_logLen < sizeof(g_logBuf)) g_logBuf[g_logLen++] = (char)b; /* tee to the RAM log */
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

/* GPU health-set dump (defined with the amdgpu probe, far below); the exit_group syscall calls it
 * so the readings land on the parked final screen. No-op until the probe has mapped the BAR. */
static void PuckGpuHealthDump(void);
/* Persist the boot-log RAM buffer to a UEFI variable (defined far below); the exit_group syscall
 * calls it so the next boot can write \PuckLog.txt. */
static void PuckLogPersist(void);

/* One-line summary of how far stages (c)/(d) got, latched by PuckGpuBringUp and restated at the
 * end of the parked health dump (the per-step progress scrolls off the Deck panel). */
static const char *g_gpuBringUpNote = "not reached";

/* Stage-(e) ring-test diagnostics, latched during the test and reprinted on the parked health
 * screen (the failure dump at the point of failure scrolls off the Deck panel). g_rtRan gates it. */
static int g_rtRan;
static unsigned int g_rtScratch, g_rtRptr, g_rtWptr, g_rtCpStat, g_rtActive, g_rtMeCntl;
/* Boot-5 two-stage poll verdict (RingAttemptHqd "(j)"): reg = SET_UCONFIG_REG->SCRATCH_REG0 path,
 * mem = WRITE_DATA->memory path. Latched by the LAST attempt that ran, for g_gpuBringUpNote wording. */
static int g_rtRegPass, g_rtMemPass;
/* Ring/doorbell setup readbacks, latched after programming: is the doorbell path armed (DbPhys!=0,
 * DbCtrl==0x40000458) and did the ring registers take (RbCntl, RbBase)? Distinguishes "doorbell not
 * wired" from "CP won't fetch despite an armed doorbell" (⇒ clear-state init needed). */
static unsigned long long g_rtDbPhys;
static unsigned int g_rtDbCtrl, g_rtRbCntl, g_rtRbBase, g_rtAperEn;
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
            /* The kernel parks here (managed Main never resumes), so this IS the final screen:
             * re-read the GPU health set onto it -- the early dump scrolls past the Deck panel. */
            PuckGpuHealthDump();
            PuckLogPersist(); /* stash the whole log to NVRAM; next boot writes it to \PuckLog.txt */
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

/* Bytes covered by the identity map (numGiB << 30), set by PuckInitPaging. PuckMapMmio uses it to
 * decide "already covered by a 2 MiB PS leaf -> retune in place" vs "high BAR beyond RAM -> build
 * 4 KiB leaves", so a framebuffer or BAR living in high RAM is retuned, not refused. */
static unsigned long long g_puckMappedBytes;

void PuckInitPaging(unsigned long long numGiB)
{
    /* Identity-map [0, numGiB) with 2 MiB RWX pages. This MUST cover all physical RAM (our image,
     * heap, these very page tables, stacks, guest) plus the MMIO we touch: the instant CR3 switches,
     * any access to an unmapped address faults. QEMU fits in 4 GiB, but a 16 GiB Steam Deck places
     * the heap -- and thus these page tables -- high, so the caller derives numGiB from the firmware
     * memory map + the framebuffer and clamps it to [4, 512] (one PDPT spans 512 x 1 GiB). Layout:
     * 1 PML4 + 1 PDPT + numGiB PDs. */
    unsigned long long *pml4 = (unsigned long long *)PuckAllocPages(1);
    unsigned long long *pdpt = (unsigned long long *)PuckAllocPages(1);
    unsigned long long g;
    int i;

    /* US is set on the upper levels (PML4/PDPT) so they never gate user access; the per-2 MiB PD
     * leaves stay supervisor (US=0) by default. PuckSetUserAccessible then flips US on exactly
     * the guest's PD entries, making only those 2 MiB pages reachable from ring 3. */
    pml4[0] = (unsigned long long)pdpt | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_US;

    for (g = 0; g < numGiB; g++) /* one PD (1 GiB of 2 MiB leaves) per slot */
    {
        unsigned long long *pd = (unsigned long long *)PuckAllocPages(1);
        pdpt[g] = (unsigned long long)pd | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_US;
        for (i = 0; i < 512; i++)
        {
            unsigned long long phys = g * 0x40000000ull + (unsigned long long)i * 0x200000ull;
            pd[i] = phys | PUCK_PTE_P | PUCK_PTE_RW | PUCK_PTE_PS; /* 2 MiB, RWX, supervisor */
        }
    }

    g_puckPml4 = pml4;
    g_puckMappedBytes = numGiB << 30; /* PuckMapMmio: "inside the map -> retune in place" boundary */
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
 *  - inside the identity map (phys+size <= g_puckMappedBytes): already covered by a present 2 MiB PS
 *    leaf, so we RETUNE that PDE's PAT bits in place (no split). QEMU-verifiable.
 *  - above it (a 64-bit BAR beyond mapped RAM): no PDPT/PD exists there; build the chain from
 *    PuckAllocPages with 4 KiB leaves. Only for a genuinely high BAR; never entered on default q35.
 * Identity-extend keeps the phys==virt invariant PuckDmaAlloc relies on. */
void *PuckMapMmio(unsigned long long phys, unsigned long long size, PuckMemType type)
{
    unsigned long long start, end, a;

    if (phys + size < phys) PuckHang(); /* 64-bit overflow guard */

    if (phys + size <= g_puckMappedBytes)
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

    /* Flush the TLB so the new type is used. NO wbinvd: for a UC/WC MMIO aperture there are no old
     * WB lines of ours to evict (Linux efifb issues none for the EFI framebuffer), and a GLOBAL
     * wbinvd here would write every dirty line in the cache -- including any that alias a scanout
     * aperture -- straight into the live display BAR as garbage. That global writeback WAS the
     * framebuffer noise. WC write ordering is drained by an sfence at the write sites instead. */
    PuckWriteCr3((unsigned long long)g_puckPml4);
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
#define PUCK_FB_ROTATION 90  /* 0/90/180/270. The Deck LCD GOP scans out portrait; 90 makes text
                              * upright when the Deck is held in landscape (derived from a real boot:
                              * rot=0 rendered text flowing toward the d-pad edge). QEMU std-VGA is
                              * landscape, so QEMU renders rotated -- harmless, it runs headless. */
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
    PuckMfence(); /* drain the WC clear so the panel actually goes black */
    g_fb.cx = g_fb.cy = 0;
    SerialPuts("[fb] framebuffer console online\r\n");
}

/* Bring the on-panel console up on the firmware's identity map, BEFORE our own paging, so the whole
 * takeover is visible on the Deck panel (its only console). The firmware maps the GOP framebuffer
 * 1:1, so g_fb.phys is directly writable here; PuckFbInit later retunes it write-combining once our
 * page tables are online (g_fb.phys is inside the identity map, so that retune is an in-place PDE
 * edit, not a refused high-BAR split). Clearing the panel doubles as the first "we are running"
 * beacon: the firmware boot logo vanishing means our image took the machine. */
static void PuckFbInitEarly(void)
{
    if (g_fb.phys == 0 || g_fb.pitch == 0) return;
    /* Do NOT touch the aperture here. Pre-paging it is only reachable through the firmware's
     * write-back map, and the AMD display controller does not snoop the CPU cache -- writing it WB
     * leaves dirty lines that later spray into the live scanout as noise. We only stash the text-grid
     * geometry and log it to serial; the panel console comes up WRITE-COMBINING after our paging, in
     * PuckFbInit (g_fb.base stays 0 until then, so FbWriteByte no-ops -- serial-only until WC). */
    if ((PUCK_FB_ROTATION % 180) == 0) { g_fb.cols = g_fb.width / 8; g_fb.rows = g_fb.height / 16; }
    else { g_fb.cols = g_fb.height / 8; g_fb.rows = g_fb.width / 16; }
    g_fb.cx = g_fb.cy = 0;
    SerialPuts("[fb] geometry: base="); SerialPutHex64(g_fb.phys);
    SerialPuts(" w="); SerialPutHex64(g_fb.width);
    SerialPuts(" h="); SerialPutHex64(g_fb.height);
    SerialPuts(" pitch="); SerialPutHex64(g_fb.pitch);
    SerialPuts(" rot="); SerialPutHex64(PUCK_FB_ROTATION); SerialPuts("\r\n");
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
    PuckMfence(); /* WC is weakly ordered; drain each write so the panel updates promptly */
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

/* Highest end address over actual-RAM descriptors only (EFI types 1..10: Loader/BootServices/Runtime
 * code & data, Conventional, ACPI reclaim/NVS). Reserved (0), MMIO (11), memory-mapped I/O port space
 * (12), PAL (13), and Persistent (14) are EXCLUDED, so a high device BAR -- e.g. the Deck's framebuffer
 * aperture at ~995 GiB -- does NOT balloon the identity map (and get lost past the 512 GiB clamp). Such
 * apertures are mapped on demand by PuckMapMmio's high-BAR path instead. */
static unsigned long long MemMapTopPhys(unsigned char *map, unsigned long long mapSize,
                                        unsigned long long descSize)
{
    unsigned long long top = 0;
    unsigned char *p;

    for (p = map; p + descSize <= map + mapSize; p += descSize)
    {
        unsigned int type = *(unsigned int *)p;
        unsigned long long physStart, pages, end;
        if (type < 1 || type > 10) continue; /* RAM only; skip Reserved/MMIO/IOPort/PAL/Persistent */
        physStart = *(unsigned long long *)(p + 8);
        pages = *(unsigned long long *)(p + 24);
        end = physStart + pages * EFI_PAGE_SIZE;
        if (end > top) top = end;
    }
    return top;
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
typedef efi_status_t (*efi_file_delete_t)(void *file); /* also closes the handle, per the EFI spec */

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

/* Build "\<prefix><name>" as CHAR16 (UTF-16) for EFI_FILE.Open. out must hold the prefix + name + NUL. */
static void PuckBuildEspPath(const char *prefix, const char *name, unsigned short *out)
{
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

/* Open the ESP volume root via LoadedImage -> SimpleFileSystem -> OpenVolume. Returns 0 (after one
 * log line) when a protocol is missing; shared by the .so preload and the GPU-microcode preload. */
static void *PuckEspOpenRoot(void *imageHandle, void *bs)
{
    efi_handle_protocol_t handleProtocol = (efi_handle_protocol_t)EfiField(bs, EFI_BS_HANDLEPROTOCOL_OFFSET);
    void *li = 0, *sfs = 0, *root = 0, *deviceHandle;
    efi_open_volume_t openVolume;

    if (handleProtocol(imageHandle, g_efiLoadedImageGuid, &li) != 0 || !li)
    { SerialPuts("[esp] no LoadedImage protocol\r\n"); return 0; }
    deviceHandle = EfiField(li, EFI_LOADED_IMAGE_DEVICEHANDLE);
    if (handleProtocol(deviceHandle, g_efiSfsGuid, &sfs) != 0 || !sfs)
    { SerialPuts("[esp] no SimpleFileSystem protocol\r\n"); return 0; }
    openVolume = (efi_open_volume_t)EfiField(sfs, EFI_SFS_OPENVOLUME_OFF);
    if (openVolume(sfs, &root) != 0 || !root)
    { SerialPuts("[esp] OpenVolume failed\r\n"); return 0; }
    return root;
}

/* REAL-HARDWARE GOTCHA -- probe a directory ONCE before opening files under it: on the Deck's
 * USB/FAT stack, a run of EFI_FILE.Open calls for missing files wedged the firmware -- it froze
 * mid-loop on-device (QEMU tolerates it, so this only bit on real hardware). If the directory is
 * absent, the caller skips its whole preload, avoiding the failing-open storm entirely. */
static int PuckEspDirProbe(void *root, const unsigned short *dirPath)
{
    efi_file_open_t rootOpen = (efi_file_open_t)EfiField(root, EFI_FILE_OPEN_OFF);
    void *dir = 0;
    if (rootOpen(root, &dir, dirPath, 1, 0) != 0 || !dir) return 0;
    ((efi_file_close_t)EfiField(dir, EFI_FILE_CLOSE_OFF))(dir);
    return 1;
}

/* Read one ESP file into a fresh EfiLoaderData buffer (persists across ExitBootServices and is
 * skipped by FindLargestConventional, so it survives into the running kernel). 0 = success. */
static int PuckEspReadFile(void *root, efi_allocate_pool_t allocPool, const unsigned short *espPath,
                           void **outBuf, unsigned long long *outSize)
{
    efi_file_open_t fopen = (efi_file_open_t)EfiField(root, EFI_FILE_OPEN_OFF);
    void *file = 0, *buf = 0;
    unsigned long long size = 0;
    efi_file_setpos_t setpos;
    efi_file_pos_t getpos;
    efi_file_read_t fread;
    efi_file_close_t fclose;

    if (fopen(root, &file, espPath, 1, 0) != 0 || !file) return -1;
    setpos = (efi_file_setpos_t)EfiField(file, EFI_FILE_SETPOS_OFF);
    getpos = (efi_file_pos_t)EfiField(file, EFI_FILE_GETPOS_OFF);
    fread = (efi_file_read_t)EfiField(file, EFI_FILE_READ_OFF);
    fclose = (efi_file_close_t)EfiField(file, EFI_FILE_CLOSE_OFF);
    setpos(file, 0xFFFFFFFFFFFFFFFFull); /* seek to EOF to get the size */
    getpos(file, &size);
    setpos(file, 0);
    if (size == 0 || allocPool(EFI_MEMTYPE_LOADER_DATA, size, &buf) != 0 || !buf)
    { fclose(file); return -2; }
    if (fread(file, &size, buf) != 0) { fclose(file); return -3; }
    fclose(file);
    *outBuf = buf;
    *outSize = size;
    return 0;
}

/* ---------------------------------------------------------------------------------------
 * Boot-log persistence. The bring-up log (post-ExitBootServices) can't reach a file directly -- the
 * firmware filesystem is gone by then. So: at the end of a boot we stash g_logBuf into a UEFI
 * variable via Runtime Services (SetVariable works post-exit); at the START of the NEXT boot, while
 * boot services + the ESP are still alive, we read it back and write \PuckLog.txt, then clear the
 * variable. A dev box reads exact text off the stick -- no photographing the panel. */
#define EFI_ST_RUNTIMESERVICES_OFFSET 88   /* EFI_SYSTEM_TABLE.RuntimeServices */
#define EFI_RT_GETVARIABLE_OFFSET     72   /* EFI_RUNTIME_SERVICES.GetVariable */
#define EFI_RT_SETVARIABLE_OFFSET     88   /* EFI_RUNTIME_SERVICES.SetVariable */
#define EFI_FILE_WRITE_OFF            40   /* EFI_FILE_PROTOCOL.Write */
#define EFI_FILE_DELETE_OFF           24   /* EFI_FILE_PROTOCOL.Delete */
#define EFI_VAR_NV_BS_RT              0x7  /* NON_VOLATILE | BOOTSERVICE_ACCESS | RUNTIME_ACCESS */
#define EFI_FILE_MODE_CREATE_RW       0x8000000000000003ull /* CREATE | WRITE | READ */

typedef efi_status_t (*efi_get_variable_t)(const unsigned short *name, const void *guid,
                                           unsigned int *attrs, unsigned long long *size, void *data);
typedef efi_status_t (*efi_set_variable_t)(const unsigned short *name, const void *guid,
                                           unsigned int attrs, unsigned long long size, const void *data);
typedef efi_status_t (*efi_file_write_t)(void *file, unsigned long long *size, const void *buf);

/* Puck vendor GUID for the log variable (arbitrary but stable; set/get must agree). */
static const unsigned char g_puckLogGuid[16] =
    { 0x9c, 0x6b, 0x50, 0x55, 0x43, 0x4b, 0x10, 0x00, 0x80, 0x21, 0xb7, 0x33, 0x0c, 0x0f, 0xef, 0x1a };
static const unsigned short g_puckLogVar[] = { 'P','u','c','k','L','o','g',0 };

/* Persist g_logBuf to the UEFI variable (post-ExitBootServices, via Runtime Services). Best-effort:
 * the panel already showed everything, so a failure here loses nothing. */
static void PuckLogPersist(void)
{
    void *rt = EfiField(g_puckEfiSystemTable, EFI_ST_RUNTIMESERVICES_OFFSET);
    efi_set_variable_t setVar;
    if (!rt || g_logLen == 0) return;
    setVar = (efi_set_variable_t)EfiField(rt, EFI_RT_SETVARIABLE_OFFSET);
    setVar(g_puckLogVar, g_puckLogGuid, EFI_VAR_NV_BS_RT, (unsigned long long)g_logLen, g_logBuf);
}

/* At the next boot (boot services + ESP alive): if the log variable exists, write it to \PuckLog.txt
 * and clear the variable. Reuses the proven SFS path; runs from the GPU-fw preload.
 * TRUNCATION FIX (found from a real flush leaving the PREVIOUS boot's tail on the stick beyond the new
 * log's length): EFI_FILE_MODE_CREATE_RW on an EXISTING file only grows it, never shrinks it -- a
 * shorter new log left stale bytes past the new EOF. Delete the file first (best-effort; a NOT_FOUND
 * on the first-ever flush is expected and ignored) so the subsequent Open(..., CREATE_RW) always starts
 * from a zero-length file, then write. Delete() closes the handle itself (EFI spec), so no separate
 * Close call on that path. */
static void PuckLogFlush(void *root, efi_allocate_pool_t allocPool)
{
    void *rt = EfiField(g_puckEfiSystemTable, EFI_ST_RUNTIMESERVICES_OFFSET);
    efi_get_variable_t getVar;
    efi_set_variable_t setVar;
    efi_file_open_t fopen;
    void *buf = 0, *file = 0;
    unsigned long long size = 24576, wlen;
    unsigned int attrs = 0;
    const unsigned short path[] = { '\\','P','u','c','k','L','o','g','.','t','x','t',0 };

    if (!rt || !root) return;
    getVar = (efi_get_variable_t)EfiField(rt, EFI_RT_GETVARIABLE_OFFSET);
    setVar = (efi_set_variable_t)EfiField(rt, EFI_RT_SETVARIABLE_OFFSET);
    if (allocPool(EFI_MEMTYPE_LOADER_DATA, size, &buf) != 0 || !buf) return;
    if (getVar(g_puckLogVar, g_puckLogGuid, &attrs, &size, buf) != 0 || size == 0)
        return; /* no prior log (NOT_FOUND) -- nothing to flush */

    fopen = (efi_file_open_t)EfiField(root, EFI_FILE_OPEN_OFF);

    /* Delete any existing \PuckLog.txt first so the write below starts from zero length -- otherwise a
     * new log shorter than the old one leaves the old tail dangling past the new EOF. Best-effort: a
     * missing file (first-ever flush) fails here and that is fine, nothing to delete. */
    if (fopen(root, &file, path, EFI_FILE_MODE_CREATE_RW, 0) == 0 && file)
        ((efi_file_delete_t)EfiField(file, EFI_FILE_DELETE_OFF))(file);

    file = 0;
    if (fopen(root, &file, path, EFI_FILE_MODE_CREATE_RW, 0) == 0 && file)
    {
        efi_file_write_t fwrite = (efi_file_write_t)EfiField(file, EFI_FILE_WRITE_OFF);
        wlen = size;
        fwrite(file, &wlen, buf);
        ((efi_file_close_t)EfiField(file, EFI_FILE_CLOSE_OFF))(file);
        SerialPuts("[log] wrote \\PuckLog.txt from the previous boot (truncated first)\r\n");
    }
    /* Clear the variable (SetVariable with size 0 deletes) so the next flush only sees a fresh log. */
    setVar(g_puckLogVar, g_puckLogGuid, EFI_VAR_NV_BS_RT, 0, buf);
}

static void PuckEfiPreloadSos(void *imageHandle, void *bs)
{
    efi_allocate_pool_t allocPool = (efi_allocate_pool_t)EfiField(bs, EFI_BS_ALLOCATEPOOL_OFFSET);
    static const unsigned short radvDir[] = { '\\', 'r', 'a', 'd', 'v', 0 };
    void *root = PuckEspOpenRoot(imageHandle, bs);
    unsigned int n = (unsigned int)(sizeof(g_puckPreloadNames) / sizeof(g_puckPreloadNames[0])), i;

    if (!root) return;
    if (!PuckEspDirProbe(root, radvDir)) /* once, per the wedge-avoidance note on PuckEspDirProbe */
    { SerialPuts("[esp] no \\radv on ESP; skipping .so preload\r\n"); return; }

    for (i = 0; i < n; i++)
    {
        const char *name = g_puckPreloadNames[i];
        unsigned short espPath[128];
        char libPath[128];
        void *buf = 0;
        unsigned long long size = 0;
        PuckBuildEspPath("\\radv\\", name, espPath);
        PuckBuildLibPath(name, libPath);
        if (PuckEspReadFile(root, allocPool, espPath, &buf, &size) != 0)
        { SerialPuts("[esp] load failed: "); SerialPuts(libPath); SerialPuts("\r\n"); continue; }
        PuckVfsAddFile(libPath, buf, size);
        SerialPuts("[esp] preloaded "); SerialPuts(libPath); SerialPuts("\r\n");
    }
}

/* ---------------------------------------------------------------------------------------
 * GPU microcode preload -- the Van Gogh blobs (amdgpu/firmware in-tree, staged on the ESP at
 * \amdgpu\ by stage-deck.ps1 / run-qemu.ps1). Read into EfiLoaderData like the .so closure, but
 * recorded in a KERNEL-side table (not the guest VFS): PuckGpuBringUp -- stage (d) of
 * docs/gfx103-bringup-spec.md -- feeds them to the PSP itself. Same probe-the-directory-once
 * wedge-avoidance as \radv. */
int strcmp(const char *a, const char *b); /* defined with the CRT shims at the bottom of this file */

typedef struct PuckGpuFw
{
    const char          *name;
    const unsigned char *data;
    unsigned long long   size;
} PuckGpuFw;

static const char *const g_puckGpuFwNames[] = {
    "vangogh_toc.bin",  /* PSP table of contents (LOAD_TOC -> TMR size) */
    "vangogh_asd.bin",  /* app security daemon -- preloaded for later TA work; not in the (d) load list */
    "vangogh_pfp.bin",
    "vangogh_me.bin",
    "vangogh_ce.bin",
    "vangogh_mec.bin",
    "vangogh_mec2.bin",
    "vangogh_rlc.bin",
    "vangogh_sdma.bin",
};

static PuckGpuFw g_gpuFw[sizeof(g_puckGpuFwNames) / sizeof(g_puckGpuFwNames[0])];
static unsigned int g_gpuFwCount;

static void PuckEfiPreloadGpuFw(void *imageHandle, void *bs)
{
    efi_allocate_pool_t allocPool = (efi_allocate_pool_t)EfiField(bs, EFI_BS_ALLOCATEPOOL_OFFSET);
    static const unsigned short fwDir[] = { '\\', 'a', 'm', 'd', 'g', 'p', 'u', 0 };
    void *root = PuckEspOpenRoot(imageHandle, bs);
    unsigned int n = (unsigned int)(sizeof(g_puckGpuFwNames) / sizeof(g_puckGpuFwNames[0])), i;

    if (!root) return;
    /* Flush the previous boot's log to \PuckLog.txt while the ESP is still writable (independent of
     * \amdgpu). Do it before the dir probe so it runs even on an image without GPU microcode. */
    PuckLogFlush(root, allocPool);
    if (!PuckEspDirProbe(root, fwDir)) /* once, per the wedge-avoidance note on PuckEspDirProbe */
    { SerialPuts("[esp] no \\amdgpu on ESP; skipping GPU microcode preload\r\n"); return; }

    for (i = 0; i < n; i++)
    {
        const char *name = g_puckGpuFwNames[i];
        unsigned short espPath[128];
        void *buf = 0;
        unsigned long long size = 0;
        PuckBuildEspPath("\\amdgpu\\", name, espPath);
        if (PuckEspReadFile(root, allocPool, espPath, &buf, &size) != 0)
        { SerialPuts("[esp] gpu fw load failed: "); SerialPuts(name); SerialPuts("\r\n"); continue; }
        g_gpuFw[g_gpuFwCount].name = name;
        g_gpuFw[g_gpuFwCount].data = (const unsigned char *)buf;
        g_gpuFw[g_gpuFwCount].size = size;
        g_gpuFwCount++;
        SerialPuts("[esp] gpu fw "); SerialPuts(name);
        SerialPuts(" ("); EcamPutDec((unsigned int)size); SerialPuts(" bytes)\r\n");
    }
}

static const PuckGpuFw *PuckGpuFwFind(const char *name)
{
    unsigned int i;
    for (i = 0; i < g_gpuFwCount; i++)
        if (strcmp(g_gpuFw[i].name, name) == 0)
            return &g_gpuFw[i];
    return 0;
}

/* ---------------------------------------------------------------------------------------
 * amdgpu GPU probe -- stage (b) of docs/amd-vulkan-plan.md. Find the Van Gogh APU (1002:163F) over
 * PCI config space, decode + size its BARs, map the 512 KiB register aperture UC, and read the
 * day-one health set: GRBM/CP/RLC status (is the GFX block alive after the warm GOP post),
 * GCMC_VM_FB_LOCATION (where the UMA carveout sits in the GPU's address space), RCC_CONFIG_MEMSIZE
 * (carveout size in MiB), and the MP0 C2PMSG mailbox residue (PSP sOS state -- stage (d) talks to
 * it). Read-only by design: the only config-space writes are BAR-sizing all-ones (restored), done
 * with the console quiet because the panel framebuffer scans out of THIS device's FB BAR (CPU
 * writes to it are dropped while MEM decode is off). Register dword addresses are IP-base segment +
 * offset from the vendored MIT headers (amdgpu/include: vangogh_ip_offset.h, gc_10_3_0_offset.h,
 * mp_11_0_offset.h, nbio_7_2_0_offset.h). QEMU has no RDNA model -> one "not found" line; the
 * readings themselves are Deck-only. */

/* SOC15 dword addresses for Van Gogh: IP_BASE__INST0_SEGn + register offset. Everything below fits
 * in the 512 KiB register BAR (0x20000 dwords), so plain aperture reads reach them all. */
#define VGH_GC_SEG0                    0x1260u
#define VGH_GC_SEG1                    0xA000u
#define VGH_MP0_SEG0                   0x16000u
#define VGH_MP1_SEG0                   0x16000u
#define VGH_NBIO_SEG2                  0x0D20u
#define VGH_GRBM_STATUS                (VGH_GC_SEG0 + 0x0da4u)
#define VGH_GRBM_STATUS2               (VGH_GC_SEG0 + 0x0da2u)
#define VGH_CP_STAT                    (VGH_GC_SEG0 + 0x0f40u)
#define VGH_CP_MEC_CNTL                (VGH_GC_SEG0 + 0x0f55u)
#define VGH_CP_ME_CNTL                 (VGH_GC_SEG0 + 0x0f56u)
#define VGH_GCMC_VM_FB_LOCATION_BASE   (VGH_GC_SEG0 + 0x16fcu)
#define VGH_GCMC_VM_FB_LOCATION_TOP    (VGH_GC_SEG0 + 0x16fdu)
#define VGH_RLC_GPM_STAT               (VGH_GC_SEG1 + 0x4e6eu)
#define VGH_MP0_C2PMSG_33              (VGH_MP0_SEG0 + 0x0061u) /* PSP bootloader status */
#define VGH_MP0_C2PMSG_35              (VGH_MP0_SEG0 + 0x0063u) /* PSP mailbox cmd/resp */
#define VGH_MP0_C2PMSG_58              (VGH_MP0_SEG0 + 0x007au)
#define VGH_MP0_C2PMSG_59              (VGH_MP0_SEG0 + 0x007bu)
#define VGH_MP0_C2PMSG_64              (VGH_MP0_SEG0 + 0x0080u) /* sOS ready flags */
#define VGH_MP0_C2PMSG_81              (VGH_MP0_SEG0 + 0x0091u)
#define VGH_MP1_C2PMSG_90              (VGH_MP1_SEG0 + 0x029au) /* SMU alive if non-zero */
#define VGH_RLC_BOOTLOAD_STATUS        (VGH_GC_SEG1 + 0x4e8du)  /* bit31 = RLC boot complete */
#define VGH_GCMC_VM_FB_OFFSET          (VGH_GC_SEG0 + 0x16e7u)  /* carveout DRAM base >> 24 */
#define VGH_GCVM_L2_FAULT_STATUS       (VGH_GC_SEG0 + 0x15c8u)  /* 0 = no GPUVM faults */
#define VGH_RCC_CONFIG_MEMSIZE         (VGH_NBIO_SEG2 + 0x00c3u) /* UMA carveout, MiB */

/* Stage (c) GMC/GART -- GC hub (GFXHUB) only; offsets from gc_10_3_0_offset.h. */
#define VGH_GCVM_CTX0_PT_BASE_LO       (VGH_GC_SEG0 + 0x1667u)  /* GCVM_CONTEXT0_PAGE_TABLE_BASE_ADDR_LO32 */
#define VGH_GCVM_CTX0_PT_BASE_HI       (VGH_GC_SEG0 + 0x1668u)  /* GCVM_CONTEXT0_PAGE_TABLE_BASE_ADDR_HI32 */
#define VGH_GCVM_CTX0_PT_START_LO      (VGH_GC_SEG0 + 0x1687u)  /* GCVM_CONTEXT0_PAGE_TABLE_START_ADDR_LO32 */
#define VGH_GCVM_CTX0_PT_START_HI      (VGH_GC_SEG0 + 0x1688u)  /* GCVM_CONTEXT0_PAGE_TABLE_START_ADDR_HI32 */
#define VGH_GCVM_CTX0_PT_END_LO        (VGH_GC_SEG0 + 0x16a7u)  /* GCVM_CONTEXT0_PAGE_TABLE_END_ADDR_LO32 */
#define VGH_GCVM_CTX0_PT_END_HI        (VGH_GC_SEG0 + 0x16a8u)  /* GCVM_CONTEXT0_PAGE_TABLE_END_ADDR_HI32 */
#define VGH_GCMC_VM_AGP_TOP            (VGH_GC_SEG0 + 0x16feu)  /* GCMC_VM_AGP_TOP (empty aperture = 0) */
#define VGH_GCMC_VM_AGP_BOT            (VGH_GC_SEG0 + 0x16ffu)  /* GCMC_VM_AGP_BOT (empty = 0xFFFFFF) */
#define VGH_GCMC_VM_AGP_BASE           (VGH_GC_SEG0 + 0x1700u)  /* GCMC_VM_AGP_BASE */
#define VGH_GCMC_VM_SYS_AP_LOW         (VGH_GC_SEG0 + 0x1701u)  /* GCMC_VM_SYSTEM_APERTURE_LOW_ADDR */
#define VGH_GCMC_VM_SYS_AP_HIGH        (VGH_GC_SEG0 + 0x1702u)  /* GCMC_VM_SYSTEM_APERTURE_HIGH_ADDR */
#define VGH_GCMC_VM_SYS_DEFAULT_LSB    (VGH_GC_SEG0 + 0x16e8u)  /* GCMC_VM_SYSTEM_APERTURE_DEFAULT_ADDR_LSB */
#define VGH_GCMC_VM_SYS_DEFAULT_MSB    (VGH_GC_SEG0 + 0x16e9u)  /* GCMC_VM_SYSTEM_APERTURE_DEFAULT_ADDR_MSB */
#define VGH_GCMC_VM_MX_L1_TLB_CNTL     (VGH_GC_SEG0 + 0x1703u)  /* GCMC_VM_MX_L1_TLB_CNTL */
#define VGH_GCVM_L2_CNTL               (VGH_GC_SEG0 + 0x15bcu)  /* GCVM_L2_CNTL */
#define VGH_GCVM_L2_CNTL2              (VGH_GC_SEG0 + 0x15bdu)  /* GCVM_L2_CNTL2 */
#define VGH_GCVM_L2_CNTL3              (VGH_GC_SEG0 + 0x15beu)  /* GCVM_L2_CNTL3 */
#define VGH_GCVM_L2_CNTL4              (VGH_GC_SEG0 + 0x15d4u)  /* GCVM_L2_CNTL4 */
#define VGH_GCVM_L2_CNTL5              (VGH_GC_SEG0 + 0x15dcu)  /* GCVM_L2_CNTL5 */
#define VGH_GCVM_CONTEXT0_CNTL         (VGH_GC_SEG0 + 0x15fcu)  /* GCVM_CONTEXT0_CNTL */
#define VGH_GCVM_L2_FAULT_DEFAULT_LO   (VGH_GC_SEG0 + 0x15cbu)  /* GCVM_L2_PROTECTION_FAULT_DEFAULT_ADDR_LO32 */
#define VGH_GCVM_L2_FAULT_DEFAULT_HI   (VGH_GC_SEG0 + 0x15ccu)  /* GCVM_L2_PROTECTION_FAULT_DEFAULT_ADDR_HI32 */
#define VGH_GCVM_L2_ID_AP_LOW_LO       (VGH_GC_SEG0 + 0x15ceu)  /* GCVM_L2_CONTEXT1_IDENTITY_APERTURE_LOW_ADDR_LO32 */
#define VGH_GCVM_L2_ID_AP_LOW_HI       (VGH_GC_SEG0 + 0x15cfu)  /* GCVM_L2_CONTEXT1_IDENTITY_APERTURE_LOW_ADDR_HI32 */
#define VGH_GCVM_L2_ID_AP_HIGH_LO      (VGH_GC_SEG0 + 0x15d0u)  /* GCVM_L2_CONTEXT1_IDENTITY_APERTURE_HIGH_ADDR_LO32 */
#define VGH_GCVM_L2_ID_AP_HIGH_HI      (VGH_GC_SEG0 + 0x15d1u)  /* GCVM_L2_CONTEXT1_IDENTITY_APERTURE_HIGH_ADDR_HI32 */
#define VGH_GCVM_L2_ID_PHYS_LO         (VGH_GC_SEG0 + 0x15d2u)  /* GCVM_L2_CONTEXT_IDENTITY_PHYSICAL_OFFSET_LO32 */
#define VGH_GCVM_L2_ID_PHYS_HI         (VGH_GC_SEG0 + 0x15d3u)  /* GCVM_L2_CONTEXT_IDENTITY_PHYSICAL_OFFSET_HI32 */
#define VGH_GCVM_INV_ENG17_REQ         (VGH_GC_SEG0 + 0x1630u)  /* GCVM_INVALIDATE_ENG17_REQ */
#define VGH_GCVM_INV_ENG17_ACK         (VGH_GC_SEG0 + 0x1642u)  /* GCVM_INVALIDATE_ENG17_ACK */
#define VGH_HDP_MEM_FLUSH              (VGH_NBIO_SEG2 + 0x00f7u) /* BIF_BX_PF0_HDP_MEM_COHERENCY_FLUSH_CNTL
                                                                  * (nbio_7_2_0_offset.h): write = HDP flush */

/* Stage (d) PSP v11 KM ring -- MP0 mailbox regs from mp_11_0_offset.h (C2PMSG_N dword = 0x40 + N). */
#define VGH_MP0_C2PMSG_67              (VGH_MP0_SEG0 + 0x0083u) /* KM ring write pointer (dwords) */
#define VGH_MP0_C2PMSG_69              (VGH_MP0_SEG0 + 0x0085u) /* ring create: mem addr low32 */
#define VGH_MP0_C2PMSG_70              (VGH_MP0_SEG0 + 0x0086u) /* ring create: mem addr high32 */
#define VGH_MP0_C2PMSG_71              (VGH_MP0_SEG0 + 0x0087u) /* ring create: mem size (bytes) */

/* Stage (e) GFX CP ring -- all GC/seg0 (dword 0x1260) except SCRATCH_REG0 (GC/seg1, dword 0xA000).
 * dword offsets + BASE_IDX grep-verified against gc_10_3_0_offset.h; each byte offset (macro*4)
 * cross-checked against spec 5.2/5.4 -- header and spec agree on every one. */
#define VGH_CP_RB0_BASE                (VGH_GC_SEG0 + 0x1de0u)  /* mmCP_RB0_BASE     (idx0) -> byte 0x0C100; rb_mc>>8 */
#define VGH_CP_RB0_BASE_HI             (VGH_GC_SEG0 + 0x1e51u)  /* mmCP_RB0_BASE_HI  (idx0) -> byte 0x0C2C4 */
#define VGH_CP_RB0_CNTL                (VGH_GC_SEG0 + 0x1de1u)  /* mmCP_RB0_CNTL     (idx0) -> byte 0x0C104 */
#define VGH_CP_RB0_RPTR                (VGH_GC_SEG0 + 0x0f60u)  /* mmCP_RB0_RPTR     (idx0) -> byte 0x08700 (read) */
#define VGH_CP_RB0_RPTR_ADDR           (VGH_GC_SEG0 + 0x1de3u)  /* mmCP_RB0_RPTR_ADDR    (idx0) -> byte 0x0C10C */
#define VGH_CP_RB0_RPTR_ADDR_HI        (VGH_GC_SEG0 + 0x1de4u)  /* mmCP_RB0_RPTR_ADDR_HI (idx0) -> byte 0x0C110 */
#define VGH_CP_RB0_WPTR                (VGH_GC_SEG0 + 0x1df4u)  /* mmCP_RB0_WPTR     (idx0) -> byte 0x0C150 */
#define VGH_CP_RB0_WPTR_HI             (VGH_GC_SEG0 + 0x1df5u)  /* mmCP_RB0_WPTR_HI  (idx0) -> byte 0x0C154 */
#define VGH_CP_RB_WPTR_POLL_ADDR_LO    (VGH_GC_SEG0 + 0x1e8bu)  /* mmCP_RB_WPTR_POLL_ADDR_LO (idx0) -> byte 0x0C3AC */
#define VGH_CP_RB_WPTR_POLL_ADDR_HI    (VGH_GC_SEG0 + 0x1e8cu)  /* mmCP_RB_WPTR_POLL_ADDR_HI (idx0) -> byte 0x0C3B0 */
#define VGH_CP_RB_VMID                 (VGH_GC_SEG0 + 0x1df1u)  /* mmCP_RB_VMID      (idx0) -> byte 0x0C144 */
#define VGH_CP_RB_ACTIVE               (VGH_GC_SEG0 + 0x1f40u)  /* mmCP_RB_ACTIVE    (idx0) -> byte 0x0C680 */
#define VGH_CP_RB_WPTR_DELAY           (VGH_GC_SEG0 + 0x0f61u)  /* mmCP_RB_WPTR_DELAY(idx0) -> byte 0x08704 */
#define VGH_CP_MAX_CONTEXT             (VGH_GC_SEG0 + 0x1e4eu)  /* mmCP_MAX_CONTEXT  (idx0) -> byte 0x0C2B8 */
#define VGH_CP_DEVICE_ID               (VGH_GC_SEG0 + 0x1debu)  /* mmCP_DEVICE_ID    (idx0) -> byte 0x0C12C */
#define VGH_GRBM_GFX_CNTL              (VGH_GC_SEG0 + 0x0dc2u)  /* mmGRBM_GFX_CNTL (idx0): PIPEID[1:0]/MEID[3:2] select the CP pipe subsequent CP_RB* writes target */
#define VGH_CP_RB_DOORBELL_CONTROL     (VGH_GC_SEG0 + 0x1e8du)  /* mmCP_RB_DOORBELL_CONTROL     (idx0) */
#define VGH_CP_RB_DOORBELL_RANGE_LOWER (VGH_GC_SEG0 + 0x1dfau)  /* mmCP_RB_DOORBELL_RANGE_LOWER (idx0) */
#define VGH_CP_RB_DOORBELL_RANGE_UPPER (VGH_GC_SEG0 + 0x1dfbu)  /* mmCP_RB_DOORBELL_RANGE_UPPER (idx0) */
#define VGH_SCRATCH_REG0               (VGH_GC_SEG1 + 0x2040u)  /* mmSCRATCH_REG0    (idx1) -> byte 0x30100 */
/* GFX ring0 doorbell: AMDGPU_NAVI10_DOORBELL_GFX_RING0 (0x08B) << 1 on gfx10 (gfx_v10 sw_init). The
 * CP_RB_DOORBELL_CONTROL.DOORBELL_OFFSET field is bits[27:2] = index, so the register value and the
 * aperture byte offset are both index<<2 = 0x458. DOORBELL_EN = bit30. */
#define VGH_GFX_DOORBELL_INDEX         0x116u
#define VGH_CP_DOORBELL_EN             0x40000000u
/* NBIO global doorbell-aperture gate: unless BIF_DOORBELL_APER_EN (bit0) is set, writes to the
 * doorbell BAR never reach any engine (nbio_v7_2_enable_doorbell_aperture). NBIO seg2, same segment
 * as RCC_CONFIG_MEMSIZE (hardware-verified). GOP leaves it off on the render path. */
#define VGH_RCC_DOORBELL_APER_EN       (VGH_NBIO_SEG2 + 0x00c0u)

/* CP_ME_CNTL halt masks (gc_10_3_0_sh_mask.h): CE_HALT|PFP_HALT|ME_HALT = 0x15000000, matching the
 * warm-boot readback. Halting before programming CP_RB0 / doorbell regs and un-halting only after
 * (per amdgpu gfx_v10_0_cp_gfx_start ordering) is the stage (e) 2026-07-03 hypothesis under test. */
#define VGH_CP_ME_CNTL_CE_HALT_MASK    0x01000000u  /* CP_ME_CNTL__CE_HALT_MASK */
#define VGH_CP_ME_CNTL_PFP_HALT_MASK   0x04000000u  /* CP_ME_CNTL__PFP_HALT_MASK */
#define VGH_CP_ME_CNTL_ME_HALT_MASK    0x10000000u  /* CP_ME_CNTL__ME_HALT_MASK */
#define VGH_CP_ME_CNTL_ALL_HALT        (VGH_CP_ME_CNTL_CE_HALT_MASK | VGH_CP_ME_CNTL_PFP_HALT_MASK \
                                        | VGH_CP_ME_CNTL_ME_HALT_MASK) /* = 0x15000000 */

/* Stage (e) failure-dump diagnostics (gc_10_3_0_offset.h; all BASE_IDX 0 -> GC/seg0). PFP/ME header
 * dump: each read pops the next entry of the CP's own packet-header FIFO -- all-zero readings across
 * 8 reads mean the PFP never fetched anything from the ring; our packet headers (TYPE3 opcodes)
 * appearing means fetch worked but execution stalled downstream (CPF/ROQ/L2). */
#define VGH_CP_PFP_HEADER_DUMP         (VGH_GC_SEG0 + 0x0f42u)  /* mmCP_PFP_HEADER_DUMP (idx0) */
#define VGH_CP_ME_HEADER_DUMP          (VGH_GC_SEG0 + 0x0f41u)  /* mmCP_ME_HEADER_DUMP  (idx0) */
#define VGH_CP_ROQ_RB_STAT             (VGH_GC_SEG0 + 0x0f80u)  /* mmCP_ROQ_RB_STAT     (idx0) */
#define VGH_CP_CPF_STATUS              (VGH_GC_SEG0 + 0x0e27u)  /* mmCP_CPF_STATUS      (idx0) */
#define VGH_CP_CPF_BUSY_STAT           (VGH_GC_SEG0 + 0x0e28u)  /* mmCP_CPF_BUSY_STAT   (idx0) */
#define VGH_GCVM_L2_FAULT_CNTL         (VGH_GC_SEG0 + 0x15c4u)  /* mmGCVM_L2_PROTECTION_FAULT_CNTL (idx0) */

/* Stage (e) boot-3 stall diagnostics (2026-07-03): CP_STALLED_STAT1/2/3 + CP_CPF_STALLED_STAT1 +
 * CP_CPC_STALLED_STAT1 (gc_10_3_0_offset.h, all BASE_IDX 0 -> GC/seg0) enumerate BY FIELD exactly what
 * the CP front end / CPF / CPC is blocked waiting on -- see gc_10_3_0_sh_mask.h for the field names,
 * which make the next log self-decoding; this file deliberately does not try to decode them. RLC_STAT/
 * RLC_SAFE_MODE/RLC_CP_SCHEDULERS are BASE_IDX 1 -> GC/seg1, same segment as VGH_RLC_BOOTLOAD_STATUS. */
#define VGH_CP_STALLED_STAT1           (VGH_GC_SEG0 + 0x0f3du)  /* mmCP_STALLED_STAT1     (idx0) */
#define VGH_CP_STALLED_STAT2           (VGH_GC_SEG0 + 0x0f3eu)  /* mmCP_STALLED_STAT2     (idx0) */
#define VGH_CP_STALLED_STAT3           (VGH_GC_SEG0 + 0x0f3cu)  /* mmCP_STALLED_STAT3     (idx0) */
#define VGH_CP_CPF_STALLED_STAT1       (VGH_GC_SEG0 + 0x0e29u)  /* mmCP_CPF_STALLED_STAT1 (idx0) */
#define VGH_CP_CPC_STALLED_STAT1       (VGH_GC_SEG0 + 0x0e26u)  /* mmCP_CPC_STALLED_STAT1 (idx0) */
#define VGH_RLC_STAT                   (VGH_GC_SEG1 + 0x4c04u)  /* mmRLC_STAT             (idx1) */
#define VGH_RLC_SAFE_MODE              (VGH_GC_SEG1 + 0x4ca0u)  /* mmRLC_SAFE_MODE        (idx1) */
#define VGH_RLC_CP_SCHEDULERS          (VGH_GC_SEG1 + 0x4ca1u)  /* mmRLC_CP_SCHEDULERS    (idx1) */

/* Stage (e) 2026-07-03: modern gfx10.3 firmware does not serve the legacy CP_RB0_* ring interface
 * at all -- amdgpu ships amdgpu_async_gfx_ring=1, so the gfx ring is ALWAYS a queue (an MQD +
 * CP_GFX_HQD_* register file), normally committed by a KIQ MAP_QUEUES packet. AMD's own bring-up
 * path (#ifdef BRING_UP_DEBUG, gfx_v10_0_gfx_queue_init_register) instead programs the GFX HQD
 * registers directly via MMIO with no KIQ -- that is the flow implemented below. All GC/seg0,
 * BASE_IDX 0; dword offsets grep-verified against gc_10_3_0_offset.h. */
#define VGH_CP_GFX_HQD_ACTIVE          (VGH_GC_SEG0 + 0x1e80u)  /* mmCP_GFX_HQD_ACTIVE          (idx0) */
#define VGH_CP_GFX_HQD_VMID            (VGH_GC_SEG0 + 0x1e81u)  /* mmCP_GFX_HQD_VMID            (idx0) */
#define VGH_CP_GFX_HQD_QUEUE_PRIORITY  (VGH_GC_SEG0 + 0x1e84u)  /* mmCP_GFX_HQD_QUEUE_PRIORITY  (idx0) */
#define VGH_CP_GFX_HQD_QUANTUM         (VGH_GC_SEG0 + 0x1e85u)  /* mmCP_GFX_HQD_QUANTUM         (idx0) */
#define VGH_CP_GFX_HQD_BASE            (VGH_GC_SEG0 + 0x1e86u)  /* mmCP_GFX_HQD_BASE            (idx0) */
#define VGH_CP_GFX_HQD_BASE_HI         (VGH_GC_SEG0 + 0x1e87u)  /* mmCP_GFX_HQD_BASE_HI         (idx0) */
#define VGH_CP_GFX_HQD_RPTR            (VGH_GC_SEG0 + 0x1e88u)  /* mmCP_GFX_HQD_RPTR            (idx0) */
#define VGH_CP_GFX_HQD_RPTR_ADDR       (VGH_GC_SEG0 + 0x1e89u)  /* mmCP_GFX_HQD_RPTR_ADDR       (idx0) */
#define VGH_CP_GFX_HQD_RPTR_ADDR_HI    (VGH_GC_SEG0 + 0x1e8au)  /* mmCP_GFX_HQD_RPTR_ADDR_HI    (idx0) */
#define VGH_CP_GFX_HQD_CNTL            (VGH_GC_SEG0 + 0x1e8fu)  /* mmCP_GFX_HQD_CNTL            (idx0) */
#define VGH_CP_GFX_HQD_WPTR            (VGH_GC_SEG0 + 0x1e91u)  /* mmCP_GFX_HQD_WPTR            (idx0) */
#define VGH_CP_GFX_HQD_WPTR_HI         (VGH_GC_SEG0 + 0x1e92u)  /* mmCP_GFX_HQD_WPTR_HI         (idx0) */
#define VGH_CP_GFX_MQD_CONTROL         (VGH_GC_SEG0 + 0x1e9au)  /* mmCP_GFX_MQD_CONTROL         (idx0) */
#define VGH_CP_MQD_BASE_ADDR           (VGH_GC_SEG0 + 0x1fa9u)  /* mmCP_MQD_BASE_ADDR           (idx0) */
#define VGH_CP_MQD_BASE_ADDR_HI        (VGH_GC_SEG0 + 0x1faau)  /* mmCP_MQD_BASE_ADDR_HI        (idx0) */
#define VGH_CP_PFP_INSTR_PNTR          (VGH_GC_SEG0 + 0x0f45u)  /* mmCP_PFP_INSTR_PNTR          (idx0) */
#define VGH_CP_ME_INSTR_PNTR           (VGH_GC_SEG0 + 0x0f46u)  /* mmCP_ME_INSTR_PNTR           (idx0) */
#define VGH_CP_CE_INSTR_PNTR           (VGH_GC_SEG0 + 0x0f47u)  /* mmCP_CE_INSTR_PNTR           (idx0) */

/* Boot-12 (2026-07-04) icache diagnostics + CE icache fix. Root-cause hypothesis: Vangogh's RLC
 * backdoor-autoload path (AMDGPU_FW_LOAD_RLC_BACKDOOR_AUTO) programs each CP front end's instruction-
 * cache base register itself (gfx_v10_0_rlc_backdoor_autoload_config_{me,ce,pfp,mec}_cache, scratchpad
 * gfx_v10_0.c ~5495-5615) as part of gfx_v10_0_wait_for_rlc_autoload_complete -- our PSP-hybrid LOAD_IP_FW
 * flow never runs that helper, so it never touches ANY *_IC_BASE/_IC_OP_CNTL register. PFP/ME evidently
 * got wired some other way (their PCs move, they execute per every boot since boot 5); the CE has never
 * moved off its counter-underflow wedge across 11 boots -- exactly what a microcode-less CE would do.
 * IC_BASE_LO/HI/BASE_CNTL/OP_CNTL all BASE_IDX 1 -> VGH_GC_SEG1; grep-verified against
 * gc_10_3_0_offset.h lines 9975-9998 (mmCP_PFP_IC_BASE_LO=0x5840 .. mmCP_CE_IC_OP_CNTL=0x584b, each
 * PFP/ME/CE quad laid out identically: BASE_LO, BASE_HI, BASE_CNTL, OP_CNTL). */
#define VGH_CP_PFP_IC_BASE_LO          (VGH_GC_SEG1 + 0x5840u)  /* mmCP_PFP_IC_BASE_LO   (idx1) */
#define VGH_CP_PFP_IC_BASE_HI          (VGH_GC_SEG1 + 0x5841u)  /* mmCP_PFP_IC_BASE_HI   (idx1) */
#define VGH_CP_PFP_IC_BASE_CNTL        (VGH_GC_SEG1 + 0x5842u)  /* mmCP_PFP_IC_BASE_CNTL (idx1) */
#define VGH_CP_PFP_IC_OP_CNTL          (VGH_GC_SEG1 + 0x5843u)  /* mmCP_PFP_IC_OP_CNTL   (idx1) */
#define VGH_CP_ME_IC_BASE_LO           (VGH_GC_SEG1 + 0x5844u)  /* mmCP_ME_IC_BASE_LO    (idx1) */
#define VGH_CP_ME_IC_BASE_HI           (VGH_GC_SEG1 + 0x5845u)  /* mmCP_ME_IC_BASE_HI    (idx1) */
#define VGH_CP_ME_IC_BASE_CNTL         (VGH_GC_SEG1 + 0x5846u)  /* mmCP_ME_IC_BASE_CNTL  (idx1) */
#define VGH_CP_ME_IC_OP_CNTL           (VGH_GC_SEG1 + 0x5847u)  /* mmCP_ME_IC_OP_CNTL    (idx1) */
#define VGH_CP_CE_IC_BASE_LO           (VGH_GC_SEG1 + 0x5848u)  /* mmCP_CE_IC_BASE_LO    (idx1) */
#define VGH_CP_CE_IC_BASE_HI           (VGH_GC_SEG1 + 0x5849u)  /* mmCP_CE_IC_BASE_HI    (idx1) */
#define VGH_CP_CE_IC_BASE_CNTL         (VGH_GC_SEG1 + 0x584au)  /* mmCP_CE_IC_BASE_CNTL  (idx1) */
#define VGH_CP_CE_IC_OP_CNTL           (VGH_GC_SEG1 + 0x584bu)  /* mmCP_CE_IC_OP_CNTL    (idx1) */

/* CP_CE_IC_OP_CNTL fields (gc_10_3_0_sh_mask.h, CP_CE_IC_OP_CNTL__* -- PFP/ME share the identical
 * bit layout, quoted once here and reused): INVALIDATE_CACHE bit0, INVALIDATE_CACHE_COMPLETE bit1,
 * PRIME_ICACHE bit4, ICACHE_PRIMED bit5. amdgpu's autoload-config helpers only ever set
 * INVALIDATE_CACHE and poll INVALIDATE_CACHE_COMPLETE -- PRIME_ICACHE/ICACHE_PRIMED are read/written
 * by neither gfx_v10_0_rlc_backdoor_autoload_config_ce_cache nor its PFP/ME siblings, so this port
 * does not touch them either (no PRIME_ICACHE step exists in the reference to mirror). */
#define VGH_CP_CE_IC_OP_CNTL_INVALIDATE_CACHE_MASK          0x00000001u
#define VGH_CP_CE_IC_OP_CNTL_INVALIDATE_CACHE_COMPLETE_MASK 0x00000002u
/* CP_CE_IC_BASE_CNTL fields (gc_10_3_0_sh_mask.h, CP_CE_IC_BASE_CNTL__* -- again identical layout
 * across PFP/ME/CE): VMID[3:0], ADDRESS_CLAMP bit4, EXE_DISABLE bit23, CACHE_POLICY[25:24]. amdgpu's
 * autoload-config helpers never touch IC_BASE_CNTL at all (only IC_BASE_LO/HI + IC_OP_CNTL) -- the
 * VMID/cache-policy fields are left at their post-reset default and only VMID=0/CACHE_POLICY=0 (a
 * masked no-op RMW) is asserted here for documentation; this mirrors the reference's silence on the
 * register exactly (present in the header, unused by the sequence being ported). */
#define VGH_CP_CE_IC_BASE_CNTL_VMID_MASK           0x0000000Fu
#define VGH_CP_CE_IC_BASE_CNTL_CACHE_POLICY_MASK   0x03000000u

/* CP_GFX_MQD_CONTROL field masks (gc_10_3_0_sh_mask.h): VMID (RMW cleared to 0), PRIV_STATE (set to
 * 1 -- "privileged" ring, matching gfx_v10_0_gfx_mqd_init), CACHE_POLICY (RMW cleared to 0). */
#define VGH_CP_GFX_MQD_CONTROL_VMID_MASK        0x0000000Fu
#define VGH_CP_GFX_MQD_CONTROL_PRIV_STATE_MASK  0x00000100u
#define VGH_CP_GFX_MQD_CONTROL_CACHE_POLICY_MASK 0x03000000u
/* CP_GFX_HQD_QUANTUM: only QUANTUM_EN is set (=1); QUANTUM_SCALE/QUANTUM_DURATION/QUANTUM_ACTIVE are
 * left as read from the live register (RMW), matching gfx_v10_0_gfx_mqd_init. */
#define VGH_CP_GFX_HQD_QUANTUM_EN_MASK           0x00000001u

/* Boot-4 (2026-07-03) stage (e) additions: gfx_v10_0_constants_init golden/SH_MEM/GDS init, plus the
 * A0 (scheduler poke) / A1 (full direct-MMIO KIQ bring-up) experiments. All offsets grep-verified
 * against gc_10_3_0_offset.h / gc_10_3_0_sh_mask.h (all BASE_IDX 0 -> VGH_GC_SEG0 unless noted). */
#define VGH_GRBM_CNTL                  (VGH_GC_SEG0 + 0x0da0u)  /* mmGRBM_CNTL           (idx0) */
#define VGH_SH_MEM_BASES               (VGH_GC_SEG0 + 0x10aau)  /* mmSH_MEM_BASES        (idx0) */
#define VGH_SH_MEM_CONFIG              (VGH_GC_SEG0 + 0x10adu)  /* mmSH_MEM_CONFIG       (idx0) */
#define VGH_GDS_VMID0_BASE             (VGH_GC_SEG0 + 0x20a0u)  /* mmGDS_VMID0_BASE      (idx0); VMIDn = +2n */
#define VGH_GDS_VMID0_SIZE             (VGH_GC_SEG0 + 0x20a1u)  /* mmGDS_VMID0_SIZE      (idx0); VMIDn = +2n */

/* GRBM_CNTL.READ_TIMEOUT field (gc_10_3_0_sh_mask.h). */
#define VGH_GRBM_CNTL_READ_TIMEOUT_SHIFT         0x0u
#define VGH_GRBM_CNTL_READ_TIMEOUT_MASK          0x000000FFu

/* SH_MEM_CONFIG fields actually present in the vendored gfx10.3 header: ADDRESS_MODE, ALIGNMENT_MODE,
 * DEFAULT_MTYPE, INITIAL_INST_PREFETCH, ICACHE_USE_GL1. There is NO RETRY_MODE field on gfx10.3 (the
 * kernel's DEFAULT_SH_MEM_CONFIG macro ORs in a RETRY_MODE shift that gfx9-era headers define but this
 * gfx10.3 header does not -- the field does not exist on this hardware generation, so it is omitted
 * here; the resolved constant below reflects only the fields the vendored header actually has). */
#define VGH_SH_MEM_CONFIG_ADDRESS_MODE_SHIFT      0x0u
#define VGH_SH_MEM_CONFIG_ALIGNMENT_MODE_SHIFT    0x2u
#define VGH_SH_MEM_CONFIG_INITIAL_INST_PREFETCH_SHIFT 0xeu
/* SH_MEM_ADDRESS_MODE_64 = 1, SH_MEM_ALIGNMENT_MODE_UNALIGNED = 3 (well-known gfx9/gfx10 UAPI enum
 * values, amdgpu_ring.h -- not vendored here, so cited as behavior fact from the kernel reference
 * rather than a header quote). INITIAL_INST_PREFETCH left at the kernel's literal `3`. Resolved:
 * DEFAULT_SH_MEM_CONFIG = (1<<0) | (3<<2) | (3<<14) = 0x0000C00D. */
#define VGH_DEFAULT_SH_MEM_CONFIG      0x0000C00Du

/* SH_MEM_BASES fields (gc_10_3_0_sh_mask.h). amdgpu sets PRIVATE_BASE/SHARED_BASE from
 * gmc.private_aperture_start/shared_aperture_start >> 48; those apertures are 0x1000000000000ULL and
 * 0x2000000000000ULL (bit 48 / bit 49), so >>48 gives 0x1 and 0x2 respectively -- reconstructed here
 * as literal field values rather than derived from a vendored GMC aperture constant (none is
 * vendored). */
#define VGH_SH_MEM_BASES_PRIVATE_BASE_SHIFT       0x0u
#define VGH_SH_MEM_BASES_SHARED_BASE_SHIFT        0x10u
#define VGH_SH_MEM_BASES_PRIVATE_BASE_VALUE       0x1u
#define VGH_SH_MEM_BASES_SHARED_BASE_VALUE        0x2u

/* GRBM_GFX_CNTL fields (gc_10_3_0_sh_mask.h): PIPEID[1:0] shift0, MEID[1:0] shift2, VMID[3:0] shift4,
 * QUEUEID[2:0] shift8. amdgpu's nv_grbm_select(adev, me, pipe, queue, vmid) call order matches this
 * field layout (constants_init calls it (0,0,0,vmid) per VMID; the KIQ path calls it
 * (ring->me, ring->pipe, ring->queue, 0)). */
#define VGH_GRBM_GFX_CNTL_PIPEID_SHIFT  0x0u
#define VGH_GRBM_GFX_CNTL_MEID_SHIFT    0x2u
#define VGH_GRBM_GFX_CNTL_VMID_SHIFT    0x4u
#define VGH_GRBM_GFX_CNTL_QUEUEID_SHIFT 0x8u

/* Vangogh golden-register set (golden_settings_gc_10_3_vangogh, gfx_v10_0.c 3245-3273): masked RMW,
 * reg = (RREG(reg) & ~mask) | (value & mask). Two kernel entries use _Vangogh-suffixed register names
 * (mmGCR_GENERAL_CNTL_Vangogh, mmSPI_CONFIG_CNTL_1_Vangogh); the vendored gc_10_3_0_offset.h has no
 * _Vangogh-suffixed defines at all, only the plain mmGCR_GENERAL_CNTL/mmSPI_CONFIG_CNTL_1 (same
 * register, gfx10.3-generic name in this header revision) -- used here. FOUR kernel entries
 * (mmCGTT_SPI_RA0_CLK_CTRL, mmCGTT_SPI_RA1_CLK_CTRL, mmGL2C_CTRL3, mmTA_CNTL_AUX) have NO matching
 * define anywhere in the vendored header (grep-checked; only unrelated mmCGTT_SPI_ names and a plain
 * mmTA_CNTL exist, no mmGL2C_CTRL3 at all) -- SKIPPED, logged as skipped at runtime;
 * PuckGpuConstantsInit does not apply these four. mmLDS_CONFIG is included per-task (kernel comment
 * paraphrased: fixes a GPU hang on VanGogh -- behavior fact, not copied text). */
#define VGH_CH_PIPE_STEER              (VGH_GC_SEG1 + 0x2d90u)  /* mmCH_PIPE_STEER              (idx1) */
#define VGH_DB_DEBUG3                  (VGH_GC_SEG0 + 0x13aeu)  /* mmDB_DEBUG3                  (idx0) */
#define VGH_DB_DEBUG4                  (VGH_GC_SEG0 + 0x13afu)  /* mmDB_DEBUG4                  (idx0) */
#define VGH_DB_EXCEPTION_CONTROL       (VGH_GC_SEG0 + 0x13bfu)  /* mmDB_EXCEPTION_CONTROL       (idx0) */
#define VGH_GB_ADDR_CONFIG             (VGH_GC_SEG0 + 0x13deu)  /* mmGB_ADDR_CONFIG             (idx0) */
#define VGH_GCR_GENERAL_CNTL           (VGH_GC_SEG0 + 0x1580u)  /* mmGCR_GENERAL_CNTL           (idx0); kernel: mmGCR_GENERAL_CNTL_Vangogh, no such define vendored -- using the plain name at the same 10.3 offset */
#define VGH_GL1_PIPE_STEER             (VGH_GC_SEG1 + 0x2d10u)  /* mmGL1_PIPE_STEER             (idx1) */
#define VGH_GL2_PIPE_STEER_0           (VGH_GC_SEG1 + 0x2e25u)  /* mmGL2_PIPE_STEER_0           (idx1) */
#define VGH_GL2_PIPE_STEER_1           (VGH_GC_SEG1 + 0x2e26u)  /* mmGL2_PIPE_STEER_1           (idx1) */
#define VGH_GL2A_ADDR_MATCH_MASK       (VGH_GC_SEG1 + 0x2e21u)  /* mmGL2A_ADDR_MATCH_MASK       (idx1) */
#define VGH_GL2C_ADDR_MATCH_MASK       (VGH_GC_SEG1 + 0x2e03u)  /* mmGL2C_ADDR_MATCH_MASK       (idx1) */
#define VGH_GL2C_CM_CTRL1              (VGH_GC_SEG1 + 0x2e08u)  /* mmGL2C_CM_CTRL1              (idx1) */
#define VGH_PA_CL_ENHANCE              (VGH_GC_SEG0 + 0x1025u)  /* mmPA_CL_ENHANCE              (idx0) */
#define VGH_PA_SC_BINNER_TIMEOUT_CNT   (VGH_GC_SEG0 + 0x1070u)  /* mmPA_SC_BINNER_TIMEOUT_COUNTER (idx0) */
#define VGH_PA_SC_ENHANCE_2            (VGH_GC_SEG0 + 0x107cu)  /* mmPA_SC_ENHANCE_2            (idx0) */
#define VGH_SPI_CONFIG_CNTL_1          (VGH_GC_SEG0 + 0x11efu)  /* mmSPI_CONFIG_CNTL_1          (idx0); kernel: mmSPI_CONFIG_CNTL_1_Vangogh, no such define vendored -- using the plain name at the same 10.3 offset */
#define VGH_SQG_CONFIG                 (VGH_GC_SEG0 + 0x10bau)  /* mmSQG_CONFIG                 (idx0) */
#define VGH_SX_DEBUG_1                 (VGH_GC_SEG0 + 0x11b8u)  /* mmSX_DEBUG_1                 (idx0) */
#define VGH_UTCL1_CTRL                 (VGH_GC_SEG0 + 0x1588u)  /* mmUTCL1_CTRL                 (idx0) */
#define VGH_VGT_GS_MAX_WAVE_ID         (VGH_GC_SEG0 + 0x1009u)  /* mmVGT_GS_MAX_WAVE_ID         (idx0) */
#define VGH_LDS_CONFIG                 (VGH_GC_SEG0 + 0x10a2u)  /* mmLDS_CONFIG                 (idx0) */

/* Boot 10: full-CSB emission needs a live readback of PA_SC_TILE_STEERING_OVERRIDE (gfx10_cs_data's
 * SECT_CONTEXT blocks do not cover it -- it is filled from hardware at submit time, not a static
 * recorded value, per gfx_v10_0_get_csb_buffer's own PA_SC_TILE_STEERING_OVERRIDE special-case).
 * mmPA_SC_TILE_STEERING_OVERRIDE = 0x00d7, BASE_IDX 1 (gc_10_3_0_offset.h lines 5606-5607). */
#define VGH_PA_SC_TILE_STEERING_OVERRIDE (VGH_GC_SEG1 + 0x00d7u) /* mmPA_SC_TILE_STEERING_OVERRIDE (idx1) */

/* Stage (e:kiq) MEC un-halt: CP_MEC_CNTL halt masks (gc_10_3_0_sh_mask.h). MEC un-halt clears both
 * ME1/ME2 halt bits; A1 leaves the PIPEn_RESET bits alone (0, i.e. not asserted). */
#define VGH_CP_MEC_CNTL_MEC_ME1_HALT_MASK  0x40000000u  /* CP_MEC_CNTL__MEC_ME1_HALT_MASK */
#define VGH_CP_MEC_CNTL_MEC_ME2_HALT_MASK  0x10000000u  /* CP_MEC_CNTL__MEC_ME2_HALT_MASK */

/* KIQ ring identity on Vangogh (gfx10.3.1), per amdgpu_gfx_kiq_acquire (amdgpu_gfx.c, v6.6): walks the
 * mec/pipe/queue bitmap top-down, skips MEC2 pipes 2/3 and any queue!=0, and sets ring->me = mec + 1.
 * For Vangogh's mec.num_mec=2/num_pipe_per_mec=4/num_queue_per_pipe=4 (gfx_v10_0.c 4507-4520) this
 * lands on me=2 (MEC1, 1-based), pipe=1, queue=0 -- matching the observed RLC_CP_SCHEDULERS reset
 * slots (0x40/0x48/0x50/0x58 = me2<<5|pipe<<3 for pipe 0-3): pipe=1 is slot 0x48. */
#define VGH_KIQ_ME    2u
#define VGH_KIQ_PIPE  1u
#define VGH_KIQ_QUEUE 0u
/* RLC_CP_SCHEDULERS low byte the kiq_setting two-step write produces: (me<<5)|(pipe<<3)|queue, then
 * OR 0x80 (valid/enable) on the second write. */
#define VGH_KIQ_SCHED_BYTE        ((VGH_KIQ_ME << 5) | (VGH_KIQ_PIPE << 3) | VGH_KIQ_QUEUE) /* = 0x48 */
#define VGH_KIQ_SCHED_VALID_MASK  0x80u

/* KIQ doorbell index: AMDGPU_NAVI10_DOORBELL_KIQ = 0x000 (amdgpu_doorbell.h, not vendored here --
 * well-known gfx10 Navi constant, cited as a behavior fact, not a header quote). Byte offset in the
 * doorbell BAR = index << 1 << 2 (kiq_init_register: (adev->doorbell_index.kiq * 2) << 2) = 0. */
#define VGH_KIQ_DOORBELL_INDEX 0x000u

/* Boot 10 relayout: the gfx ring grew 4 KiB -> 8 KiB (full-CSB preamble is ~953 dwords, more than a
 * 4 KiB/1024-dword ring can hold with any headroom), pushing every following buffer back by 4 KiB.
 * KIQ MQD/EOP/RING now sit at +0x105000/+0x106000/+0x107000 (was +0x104000/+0x105000/+0x106000); the
 * KIQ rptr-report/wptr-poll singleton dwords move INSIDE the KIQ ring's own 4 KiB page, at its last
 * two dwords (+0x107F80/+0x107FC0) instead of a separate page, so the whole VGH_RT_* block still ends
 * at exactly +0x108000 as laid out below (see the gfx-ring-block comment for the full table). The KIQ
 * PQ is only ever used to carry MAP_QUEUES-class traffic (never exercised yet, see PuckGpuKiqBringUp),
 * nowhere near the ring's last 8 bytes, so this is safe. */
#define VGH_RT_KIQ_MQD_OFF       0x00105000ull /* KIQ MQD image, 4 KiB, at +1 MiB + 20 KiB */
#define VGH_RT_KIQ_EOP_OFF       0x00106000ull /* KIQ EOP ring, 4 KiB, at +1 MiB + 24 KiB */
#define VGH_RT_KIQ_RING_OFF      0x00107000ull /* KIQ PQ (HQD ring), 4 KiB, at +1 MiB + 28 KiB */
#define VGH_RT_KIQ_RPTR_OFF      0x00107F80ull /* KIQ rptr-report writeback dword (tail of the KIQ ring page) */
#define VGH_RT_KIQ_WPTR_OFF      0x00107FC0ull /* KIQ wptr-poll mirror dword (tail of the KIQ ring page) */

/* A1 KIQ HQD register file (CP_HQD_*, distinct dword range from CP_GFX_HQD_*): all BASE_IDX 0 ->
 * VGH_GC_SEG0, grep-verified against gc_10_3_0_offset.h. */
#define VGH_CP_HQD_ACTIVE                    (VGH_GC_SEG0 + 0x1fabu)  /* mmCP_HQD_ACTIVE                    (idx0) */
#define VGH_CP_HQD_VMID                      (VGH_GC_SEG0 + 0x1facu)  /* mmCP_HQD_VMID                      (idx0) */
#define VGH_CP_HQD_PERSISTENT_STATE          (VGH_GC_SEG0 + 0x1fadu)  /* mmCP_HQD_PERSISTENT_STATE          (idx0) */
#define VGH_CP_HQD_PQ_BASE                   (VGH_GC_SEG0 + 0x1fb1u)  /* mmCP_HQD_PQ_BASE                   (idx0) */
#define VGH_CP_HQD_PQ_BASE_HI                (VGH_GC_SEG0 + 0x1fb2u)  /* mmCP_HQD_PQ_BASE_HI                (idx0) */
#define VGH_CP_HQD_PQ_RPTR                   (VGH_GC_SEG0 + 0x1fb3u)  /* mmCP_HQD_PQ_RPTR                   (idx0) */
#define VGH_CP_HQD_PQ_RPTR_REPORT_ADDR       (VGH_GC_SEG0 + 0x1fb4u)  /* mmCP_HQD_PQ_RPTR_REPORT_ADDR       (idx0) */
#define VGH_CP_HQD_PQ_RPTR_REPORT_ADDR_HI    (VGH_GC_SEG0 + 0x1fb5u)  /* mmCP_HQD_PQ_RPTR_REPORT_ADDR_HI    (idx0) */
#define VGH_CP_HQD_PQ_WPTR_POLL_ADDR         (VGH_GC_SEG0 + 0x1fb6u)  /* mmCP_HQD_PQ_WPTR_POLL_ADDR         (idx0) */
#define VGH_CP_HQD_PQ_WPTR_POLL_ADDR_HI      (VGH_GC_SEG0 + 0x1fb7u)  /* mmCP_HQD_PQ_WPTR_POLL_ADDR_HI      (idx0) */
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL       (VGH_GC_SEG0 + 0x1fb8u)  /* mmCP_HQD_PQ_DOORBELL_CONTROL       (idx0) */
#define VGH_CP_HQD_PQ_CONTROL                (VGH_GC_SEG0 + 0x1fbau)  /* mmCP_HQD_PQ_CONTROL                (idx0) */
#define VGH_CP_HQD_DEQUEUE_REQUEST           (VGH_GC_SEG0 + 0x1fc1u)  /* mmCP_HQD_DEQUEUE_REQUEST           (idx0) */
#define VGH_CP_MQD_CONTROL                   (VGH_GC_SEG0 + 0x1fcbu)  /* mmCP_MQD_CONTROL                   (idx0) */
#define VGH_CP_HQD_EOP_BASE_ADDR             (VGH_GC_SEG0 + 0x1fceu)  /* mmCP_HQD_EOP_BASE_ADDR             (idx0) */
#define VGH_CP_HQD_EOP_BASE_ADDR_HI          (VGH_GC_SEG0 + 0x1fcfu)  /* mmCP_HQD_EOP_BASE_ADDR_HI          (idx0) */
#define VGH_CP_HQD_EOP_CONTROL                (VGH_GC_SEG0 + 0x1fd0u)  /* mmCP_HQD_EOP_CONTROL               (idx0) */
#define VGH_CP_HQD_PQ_WPTR_LO                (VGH_GC_SEG0 + 0x1fdfu)  /* mmCP_HQD_PQ_WPTR_LO                (idx0) */
#define VGH_CP_HQD_PQ_WPTR_HI                (VGH_GC_SEG0 + 0x1fe0u)  /* mmCP_HQD_PQ_WPTR_HI                (idx0) */
/* MQD_BASE_ADDR/_HI and MEC_DOORBELL_RANGE_LOWER/_UPPER are shared with the gfx HQD path already
 * defined above (VGH_CP_MQD_BASE_ADDR/_HI); the MEC ones are distinct registers. */
#define VGH_CP_MEC_DOORBELL_RANGE_LOWER       (VGH_GC_SEG0 + 0x1dfcu)  /* mmCP_MEC_DOORBELL_RANGE_LOWER      (idx0) */
#define VGH_CP_MEC_DOORBELL_RANGE_UPPER       (VGH_GC_SEG0 + 0x1dfdu)  /* mmCP_MEC_DOORBELL_RANGE_UPPER      (idx0) */

/* CP_HQD_* field masks/shifts (gc_10_3_0_sh_mask.h) needed to build the compute MQD image faithfully
 * to gfx_v10_0_compute_mqd_init. */
#define VGH_CP_HQD_EOP_CONTROL_EOP_SIZE_SHIFT             0x0u
#define VGH_CP_HQD_EOP_CONTROL_EOP_SIZE_MASK              0x0000003Fu
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_OFFSET_SHIFT 0x2u
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_OFFSET_MASK  0x0FFFFFFCu
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_SOURCE_MASK  0x10000000u
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_EN_MASK      0x40000000u
#define VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_HIT_MASK     0x80000000u
#define VGH_CP_HQD_PQ_CONTROL_QUEUE_SIZE_SHIFT            0x0u
#define VGH_CP_HQD_PQ_CONTROL_QUEUE_SIZE_MASK             0x0000003Fu
#define VGH_CP_HQD_PQ_CONTROL_RPTR_BLOCK_SIZE_SHIFT       0x8u
#define VGH_CP_HQD_PQ_CONTROL_RPTR_BLOCK_SIZE_MASK        0x00003F00u
#define VGH_CP_HQD_PQ_CONTROL_UNORD_DISPATCH_MASK         0x10000000u
#define VGH_CP_HQD_PQ_CONTROL_TUNNEL_DISPATCH_MASK        0x20000000u
#define VGH_CP_HQD_PQ_CONTROL_PRIV_STATE_MASK             0x40000000u
#define VGH_CP_HQD_PQ_CONTROL_KMD_QUEUE_MASK              0x80000000u
#define VGH_CP_HQD_PERSISTENT_STATE_PRELOAD_SIZE_SHIFT    0x8u
#define VGH_CP_HQD_PERSISTENT_STATE_PRELOAD_SIZE_MASK     0x0003FF00u

static volatile unsigned int *g_gpuRegs;      /* register BAR, mapped UC; dword-indexed */
static int g_gpuBus, g_gpuDev, g_gpuFn;       /* PCI location of the found GPU */
static unsigned long long g_gpuDoorbellPhys, g_gpuDoorbellSize; /* doorbell BAR (prefetchable, non-fb) */
static volatile unsigned char *g_gpuDoorbell;                   /* mapped doorbell page (UC), or 0 */

/* Config read that prefers ECAM (multi-bus correct, reaches ext config) and falls back to legacy
 * 0xCF8 when MCFG was absent. Offsets used here are all < 0x100, so both paths agree. */
static unsigned int GpuCfg32(int bus, int dev, int fn, int off)
{
    if (g_ecamBase) return PuckEcamRead32(bus, dev, fn, off);
    return (unsigned int)PuckPciRead32(bus, dev, fn, off);
}

static void GpuPutReg(const char *name, unsigned int dw)
{
    SerialPuts("[gpu]   "); SerialPuts(name); SerialPuts("=");
    EcamPutHex((unsigned long long)g_gpuRegs[dw], 8); SerialPuts("\r\n");
}

static void PuckGpuHealthDump(void);

static void PuckGpuProbe(void)
{
    int bus, dev, fn, endBus = g_ecamBase ? g_ecamEndBus : 255;
    int found = 0;
    PuckBar bars[6];
    int barCount = 0, regBar = -1, fbBar = -1, assignBar = -1, i;

    /* Type the ECAM window UC before leaning on it: it sits inside the sub-4GiB identity map, whose
     * 2 MiB leaves default to WB -- harmless under QEMU (ECAM is guest RAM there), but real config
     * space must not be read through the cache. */
    if (g_ecamBase)
        PuckMapMmio(g_ecamBase, ((unsigned long long)(g_ecamEndBus - g_ecamStartBus + 1)) << 20, PUCK_MT_UC);

    for (bus = g_ecamBase ? g_ecamStartBus : 0; bus <= endBus && !found; bus++)
        for (dev = 0; dev < 32 && !found; dev++)
            for (fn = 0; fn < 8 && !found; fn++)
            {
                unsigned int id = GpuCfg32(bus, dev, fn, 0x00);
                if (id == 0xFFFFFFFFu) { if (fn == 0) break; else continue; }
                /* AMD display-class function (base class 0x03). The Deck LCD reports 1002:163F. */
                if ((id & 0xFFFFu) == 0x1002u && ((GpuCfg32(bus, dev, fn, 0x08) >> 24) == 0x03u))
                {
                    SerialPuts("[gpu] AMD GPU at "); EcamPutDec((unsigned int)bus);
                    SerialPuts(":"); EcamPutDec((unsigned int)dev);
                    SerialPuts("."); EcamPutDec((unsigned int)fn);
                    SerialPuts(" id="); EcamPutHex(id, 8);
                    SerialPuts(" rev="); EcamPutHex(GpuCfg32(bus, dev, fn, 0x08) & 0xFFu, 2);
                    SerialPuts("\r\n");
                    g_gpuBus = bus; g_gpuDev = dev; g_gpuFn = fn;
                    found = 1;
                }
                /* Only scan fn 1..7 on multi-function devices. */
                if (fn == 0 && !((GpuCfg32(bus, dev, 0, 0x0C) >> 16) & 0x80u)) break;
            }
    if (!found) { SerialPuts("[gpu] no AMD display device (expected under QEMU)\r\n"); return; }

    /* Raw BAR dwords 0x10..0x24: undecoded ground truth (panel photos of the decoded lines have
     * been ambiguous). */
    SerialPuts("[gpu]  cfg10:");
    for (i = 0; i < 6; i++) { SerialPuts(" "); EcamPutHex(GpuCfg32(g_gpuBus, g_gpuDev, g_gpuFn, 0x10 + i * 4), 8); }
    SerialPuts("\r\n");

    /* Decode + size all six BARs. Sizing writes all-ones with MEM+IO decode off; the panel console
     * writes through this device's FB BAR, so the window between the two command writes stays
     * print-free (CPU framebuffer stores are dropped meanwhile -- scanout itself, DCN reading the
     * carveout, does not traverse the config decode and keeps displaying). */
    {
        unsigned long cmd = PuckPciRead32(g_gpuBus, g_gpuDev, g_gpuFn, 0x04);
        PuckMfence();                                  /* drain pending WC console stores first */
        PuckPciWrite32(g_gpuBus, g_gpuDev, g_gpuFn, 0x04, cmd & ~0x3ul);
        /* Decode FIRST, then size: doing the decode in a for-loop increment runs it after the body
         * and sized an undecoded entry (first Deck boot: a garbage "BAR2147683634" line, and BAR5 --
         * decoded but past barCount -- silently dropped). */
        i = 0;
        while (i < 6)
        {
            i += PuckBarDecode(g_gpuBus, g_gpuDev, g_gpuFn, i, &bars[barCount]);
            PuckBarSize(g_gpuBus, g_gpuDev, g_gpuFn, &bars[barCount]);
            barCount++;
        }
        PuckPciWrite32(g_gpuBus, g_gpuDev, g_gpuFn, 0x04, cmd);
    }
    for (i = 0; i < barCount; i++)
    {
        if (bars[i].base == 0 && bars[i].size == 0) continue; /* unimplemented */
        SerialPuts("[gpu]  BAR"); EcamPutDec((unsigned int)bars[i].index);
        SerialPuts(bars[i].isIo ? " io  base=" : (bars[i].prefetch ? " pf  base=" : " mem base="));
        EcamPutHex(bars[i].base, 10); SerialPuts(" size="); EcamPutHex(bars[i].size, 8);
        if (!bars[i].isIo && bars[i].base && g_fb.phys >= bars[i].base && g_fb.phys < bars[i].base + bars[i].size)
        { SerialPuts(" <-fb"); fbBar = i; }
        /* Doorbell aperture: the prefetchable memory BAR that is NOT the framebuffer (2 MiB on Van
         * Gogh/Sephiroth). Stage (e) rings the GFX doorbell through it -- gfx10 ignores MMIO wptr. */
        else if (!bars[i].isIo && bars[i].prefetch && bars[i].base && g_gpuDoorbellPhys == 0)
        { SerialPuts(" <-db"); g_gpuDoorbellPhys = bars[i].base; g_gpuDoorbellSize = bars[i].size; }
        /* The register aperture: smallest ASSIGNED non-prefetchable memory BAR (512 KiB on Van
         * Gogh/Sephiroth). An implemented-but-unassigned one (base 0, real size mask) is remembered
         * for assignment below -- firmware GOP never touches the register BAR, so it arrives
         * unprogrammed on the Deck. */
        if (!bars[i].isIo && !bars[i].prefetch && bars[i].size >= 0x80000ull)
        {
            if (bars[i].base && (regBar < 0 || bars[i].size < bars[regBar].size)) regBar = i;
            if (!bars[i].base && assignBar < 0) assignBar = i;
        }
        SerialPuts("\r\n");
    }
    (void)fbBar;

    if (regBar < 0 && assignBar >= 0)
    {
        /* Standard PCI resource assignment, kernel-style: put the register BAR at the base of the
         * parent bridge's non-prefetchable memory window (the GPU is the only device on its bus and
         * its other memory BARs are prefetchable/high, so the window base is free). If firmware left
         * the window closed, open a 1 MiB window just below ECAM and route it. All config writes go
         * through the legacy 0xCF8 port -- uncached by construction. */
        unsigned long long cand = 0;
        int bbus = -1, bdev = 0, bfn = 0;
        for (bus = 0; bus <= endBus && bbus < 0; bus++)
            for (dev = 0; dev < 32 && bbus < 0; dev++)
                for (fn = 0; fn < 8; fn++)
                {
                    unsigned int id = GpuCfg32(bus, dev, fn, 0x00);
                    unsigned int hdr;
                    if (id == 0xFFFFFFFFu) { if (fn == 0) break; else continue; }
                    hdr = (GpuCfg32(bus, dev, fn, 0x0C) >> 16) & 0xFFu;
                    if ((hdr & 0x7Fu) == 0x01u
                        && (int)((GpuCfg32(bus, dev, fn, 0x18) >> 8) & 0xFFu) == g_gpuBus)
                    { bbus = bus; bdev = dev; bfn = fn; break; }
                    if (fn == 0 && !(hdr & 0x80u)) break;
                }
        if (bbus >= 0)
        {
            unsigned int win = GpuCfg32(bbus, bdev, bfn, 0x20);
            unsigned long long wbase = ((unsigned long long)(win & 0xFFF0u)) << 16;
            unsigned long long wlimit = ((unsigned long long)(win & 0xFFF00000u)) | 0xFFFFFull;
            SerialPuts("[gpu]  bridge "); EcamPutDec((unsigned int)bbus); SerialPuts(":");
            EcamPutDec((unsigned int)bdev); SerialPuts("."); EcamPutDec((unsigned int)bfn);
            SerialPuts(" memwin="); EcamPutHex(wbase, 8); SerialPuts("-"); EcamPutHex(wlimit, 8);
            SerialPuts("\r\n");
            if (wbase && wbase <= wlimit && wlimit - wbase >= 0x7FFFFull)
                cand = wbase;
            else
            {
                cand = (g_ecamBase ? g_ecamBase : 0xF8000000ull) - 0x100000ull; /* 1 MiB below ECAM */
                PuckPciWrite32(bbus, bdev, bfn, 0x20,
                    (unsigned long)(((cand >> 16) & 0xFFF0ull) | ((((cand + 0xFFFFFull) >> 16) & 0xFFF0ull) << 16)));
                SerialPuts("[gpu]  bridge memwin was closed; opened at "); EcamPutHex(cand, 8); SerialPuts("\r\n");
            }
            /* The bridge must forward memory transactions (it already does if any BAR downstream
             * is live, but firmware may have left MEM decode off along with the window). */
            PuckPciWrite32(bbus, bdev, bfn, 0x04, PuckPciRead32(bbus, bdev, bfn, 0x04) | 0x6ul);
        }
        else
            SerialPuts("[gpu]  no parent bridge found; cannot place register BAR\r\n");
        if (cand)
        {
            PuckPciWrite32(g_gpuBus, g_gpuDev, g_gpuFn, 0x10 + bars[assignBar].index * 4, (unsigned long)cand);
            PuckPciWrite32(g_gpuBus, g_gpuDev, g_gpuFn, 0x04,
                PuckPciRead32(g_gpuBus, g_gpuDev, g_gpuFn, 0x04) | 0x2ul);
            bars[assignBar].base =
                (unsigned long long)(GpuCfg32(g_gpuBus, g_gpuDev, g_gpuFn, 0x10 + bars[assignBar].index * 4) & ~0xFu);
            SerialPuts("[gpu]  BAR"); EcamPutDec((unsigned int)bars[assignBar].index);
            SerialPuts(" assigned base="); EcamPutHex(bars[assignBar].base, 8); SerialPuts("\r\n");
            if (bars[assignBar].base == cand) regBar = assignBar;
        }
    }
    if (regBar < 0)
    { SerialPuts("[gpu] no register BAR (see raw cfg dump above); not mapping\r\n"); return; }

    g_gpuRegs = (volatile unsigned int *)PuckMapMmio(bars[regBar].base, bars[regBar].size, PUCK_MT_UC);
    SerialPuts("[gpu] register BAR"); EcamPutDec((unsigned int)bars[regBar].index);
    SerialPuts(" mapped UC; health set:\r\n");
    PuckGpuHealthDump();
}

/* The stage-(b) observable, re-runnable: sane, non-FF values here mean config decode, the BAR map,
 * PAT, and the SOC15 offset math all hold on real silicon -- the foundation for stages (c)-(h).
 * EfiEntry calls it a second time right before the final halt so the values sit on the panel's
 * LAST screen (the full boot log scrolls past one screen on the Deck; a photo can't catch it
 * mid-scroll). */
static void PuckGpuHealthDump(void)
{
    if (!g_gpuRegs) return;
    GpuPutReg("GRBM_STATUS ", VGH_GRBM_STATUS);
    GpuPutReg("GRBM_STATUS2", VGH_GRBM_STATUS2);
    GpuPutReg("CP_STAT     ", VGH_CP_STAT);
    GpuPutReg("CP_ME_CNTL  ", VGH_CP_ME_CNTL);
    GpuPutReg("CP_MEC_CNTL ", VGH_CP_MEC_CNTL);
    GpuPutReg("RLC_GPM_STAT", VGH_RLC_GPM_STAT);
    GpuPutReg("RLC_BOOTLOAD", VGH_RLC_BOOTLOAD_STATUS);
    GpuPutReg("FB_LOC_BASE ", VGH_GCMC_VM_FB_LOCATION_BASE);
    GpuPutReg("FB_LOC_TOP  ", VGH_GCMC_VM_FB_LOCATION_TOP);
    GpuPutReg("FB_OFFSET   ", VGH_GCMC_VM_FB_OFFSET);
    GpuPutReg("VM_L2_FAULT ", VGH_GCVM_L2_FAULT_STATUS);
    GpuPutReg("PSP_C2P_33  ", VGH_MP0_C2PMSG_33);
    GpuPutReg("PSP_C2P_35  ", VGH_MP0_C2PMSG_35);
    GpuPutReg("PSP_C2P_58  ", VGH_MP0_C2PMSG_58);
    GpuPutReg("PSP_C2P_59  ", VGH_MP0_C2PMSG_59);
    GpuPutReg("PSP_C2P_64  ", VGH_MP0_C2PMSG_64);
    GpuPutReg("PSP_C2P_81  ", VGH_MP0_C2PMSG_81);
    GpuPutReg("SMU_C2P_90  ", VGH_MP1_C2PMSG_90);
    SerialPuts("[gpu]   carveout=");
    EcamPutDec(g_gpuRegs[VGH_RCC_CONFIG_MEMSIZE]);
    SerialPuts(" MiB (RCC_CONFIG_MEMSIZE)\r\n");
    /* Stage-(e) ring diagnostics reprinted here (they scroll off at the point of failure). RPTR
     * advancing past 0 ⇒ the CP fetched the ring; RPTR==0 ⇒ the CP never started. */
    if (g_rtRan)
    {
        SerialPuts("[gpu]   (e) SCRATCH="); EcamPutHex(g_rtScratch, 8);
        SerialPuts(" RB_RPTR="); EcamPutHex(g_rtRptr, 8);
        SerialPuts(" RB_WPTR="); EcamPutHex(g_rtWptr, 8); SerialPuts("\r\n");
        SerialPuts("[gpu]   (e) CP_STAT="); EcamPutHex(g_rtCpStat, 8);
        SerialPuts(" RB_ACTIVE="); EcamPutHex(g_rtActive, 8);
        SerialPuts(" CP_ME_CNTL="); EcamPutHex(g_rtMeCntl, 8); SerialPuts("\r\n");
        SerialPuts("[gpu]   (e) DB_PHYS="); EcamPutHex(g_rtDbPhys, 10);
        SerialPuts(" DB_CTRL="); EcamPutHex(g_rtDbCtrl, 8); SerialPuts("\r\n");
        SerialPuts("[gpu]   (e) RB_CNTL="); EcamPutHex(g_rtRbCntl, 8);
        SerialPuts(" RB_BASE="); EcamPutHex(g_rtRbBase, 8);
        SerialPuts(" APER_EN="); EcamPutHex(g_rtAperEn, 8); SerialPuts("\r\n");
    }
    /* The bring-up outcome, latched by PuckGpuBringUp, restated on this parked final screen so a
     * single photo tells the whole story (the (c)/(d) progress lines scroll off the Deck panel). */
    SerialPuts("[gpu]   bring-up: "); SerialPuts(g_gpuBringUpNote); SerialPuts("\r\n");
}

/* ---------------------------------------------------------------------------------------
 * amdgpu bring-up -- stages (c) GMC/GART and (d) PSP v11 firmware load of
 * docs/gfx103-bringup-spec.md. Called from EfiEntry AFTER PuckInitClock (every poll below is
 * TSC-deadlined; a timeout prints the relevant status registers and RETURNS -- never hang, never
 * reset: the panel scans out of this device and the console must survive a failed step). GC hub
 * (GFXHUB) only -- MMHUB, DCN and GCMC_VM_FB_LOCATION are inherited from the GOP post untouched.
 *
 * Memory the PSP and the VM walker read lives in the UMA carveout, like amdgpu (which places both
 * the GART table and the PSP buffers in "VRAM"): we take a 32 MiB window at carveout + 64 MiB --
 * above the GOP scanout framebuffer at the carveout base, below any top-of-carveout firmware
 * structures -- and map it UC, so CPU writes land in DRAM immediately (the GPU side does not snoop;
 * PuckWbinvd is forbidden near a live scanout). UNVERIFIED: nothing pins carveout+[64,96) MiB as
 * free on a warm GOP; every consumer of it below is defensive (timeout + print + return), so a
 * collision surfaces as a failed, logged step rather than a wedge. */
#define VGH_PSPWIN_CARVEOUT_OFF 0x04000000ull /* window = carveout + 64 MiB */
#define VGH_PSPWIN_SIZE         0x02000000ull /* 32 MiB */
#define VGH_PSPWIN_TMR          0x00000000ull /* TMR (PSP-authenticated fw staging); size from LOAD_TOC */
#define VGH_PSPWIN_TMR_MAX      0x01300000ull /* spec 4.4: TMR size cap */
#define VGH_PSPWIN_FW           0x01400000ull /* ucode staging buffer, 1 MiB, 1 MiB-aligned (PSP_1_MEG) */
#define VGH_PSPWIN_RING         0x01500000ull /* PSP KM ring, 0x1000 bytes (spec 4.2) */
#define VGH_PSPWIN_CMD          0x01501000ull /* psp_gfx_cmd_resp buffer, 0x1000 */
#define VGH_PSPWIN_FENCE        0x01502000ull /* PSP fence page (PSP writes the fence value at +0) */
#define VGH_PSPWIN_GARTPT       0x01600000ull /* VMID0 GART page table, 512 KiB */
/* Boot-12 CE icache staging (2026-07-04): the KIQ/gfx-ring VGH_RT_* block, based at window+0x1800000
 * (the carveout "vram" attempt's base), ends at +0x1908000 (see the VGH_RT_RING_OFF block-layout
 * comment) -- +0x1A00000 is clear of it with headroom, and clear of everything below +0x1800000 too.
 * The CE ucode blob (vangogh_ce.bin) is small (tens of KiB); 1 MiB of window is ample. */
#define VGH_PSPWIN_CE_IC         0x01A00000ull /* CE icache ucode staging window, 1 MiB */
#define VGH_PSP_RING_BYTES      0x1000u

#define VGH_GART_SIZE           0x10000000ull /* 256 MiB of GPU VA at [0, 256 MiB) (VMID0/system domain) */
#define VGH_GART_PTE_FLAGS      0x77ull       /* VALID|SYSTEM|SNOOPED|EXECUTABLE|READABLE|WRITEABLE,
                                               * MTYPE_NV10=UC(0) -- spec 3.2 GART leaf for a DMA page */

/* PSP GFX command ids + fw types (spec 4.4; psp_gfx_if.h). */
#define VGH_PSP_CMD_SETUP_TMR    0x05u
#define VGH_PSP_CMD_LOAD_IP_FW   0x06u
#define VGH_PSP_CMD_LOAD_TOC     0x20u
#define VGH_PSP_CMD_AUTOLOAD_RLC 0x21u
#define VGH_FW_TYPE_CP_ME        1u
#define VGH_FW_TYPE_CP_PFP       2u
#define VGH_FW_TYPE_CP_CE        3u
#define VGH_FW_TYPE_CP_MEC       4u
#define VGH_FW_TYPE_RLC_G        8u
#define VGH_FW_TYPE_SDMA0        9u
#define VGH_FW_TYPE_RLC_GPM_MEM  20u /* verified vs psp_gfx_if.h (kernel v6.6):
                                      * GFX_FW_TYPE_RLC_RESTORE_LIST_GPM_MEM = 20 */
#define VGH_FW_TYPE_RLC_SRM_MEM  21u /* verified vs psp_gfx_if.h (kernel v6.6):
                                      * GFX_FW_TYPE_RLC_RESTORE_LIST_SRM_MEM = 21 */
#define VGH_FW_TYPE_RLC_SRM_CNTL 22u /* verified vs psp_gfx_if.h (kernel v6.6):
                                      * GFX_FW_TYPE_RLC_RESTORE_LIST_SRM_CNTL = 22 */

/* Stage (e) GFX ring test working set, carved from the stage-(c) 1:1 GART test buffer (GPU VA ==
 * phys == CPU addr in [0, 2 MiB); see g_gartTestReady). Non-overlapping byte offsets into that
 * buffer -- kept in its upper half, clear of the CPU pattern dword at +0 that (c) wrote.
 *
 * Boot 10 (2026-07-04) relayout: the ring grew 4 KiB -> 8 KiB to hold amdgpu's FULL clear-state
 * preamble (gfx10_cs_data, ~953 dwords -- see RingAttemptHqd) instead of the old empty-stub preamble;
 * every following offset shifts back by 4 KiB. Full table (bytes from the block base):
 *
 *   ring    @ +0x100000, 8 KiB, ends +0x102000  (RB_BUFSZ math below; 2048 dwords)
 *   rptr    @ +0x102000  (rptr writeback dword)
 *   wptr    @ +0x102040  (wptr-poll mirror dword)
 *   fence   @ +0x102080  (RELEASE_MEM fence dword)
 *   scratch @ +0x1020C0  (WRITE_DATA target dword)
 *   IB      @ +0x103000, 4 KiB, ends +0x104000
 *   gfx MQD @ +0x104000, 4 KiB, ends +0x105000
 *   KIQ MQD @ +0x105000, 4 KiB, ends +0x106000
 *   KIQ EOP @ +0x106000, 4 KiB, ends +0x107000
 *   KIQ ring@ +0x107000, 4 KiB, ends +0x108000  (block end)
 *
 * Nothing here is in the carveout PSP window -- it is all DMA/GTT memory the CP reaches through
 * VMID0/GART. Block-fit check, GART attempt: the GART test buffer is a 2 MiB window ([0, 0x200000)),
 * and the block ends at +0x108000 (1,081,344 bytes), comfortably < 0x200000 (2,097,152 bytes) -- fits.
 * Block-fit check, carveout "vram" attempt: base window+0x1800000, block ends
 * window+0x1800000+0x108000 = window+0x1908000, still < window+0x2000000 (the 32 MiB window end) --
 * fits. */
#define VGH_RT_RING_OFF          0x00100000ull /* GFX ring buffer, 8 KiB, at +1 MiB */
#define VGH_RT_RING_BYTES        0x2000u       /* 8 KiB = 2048 dwords */
#define VGH_RT_RPTR_OFF          0x00102000ull /* rptr writeback dword */
#define VGH_RT_WPTR_OFF          0x00102040ull /* wptr-poll mirror dword */
#define VGH_RT_FENCE_OFF         0x00102080ull /* RELEASE_MEM fence dword */
#define VGH_RT_SCRATCH_OFF       0x001020C0ull /* WRITE_DATA target dword (IB writes 0xDEADBEEF here) */
#define VGH_RT_IB_OFF            0x00103000ull /* IB buffer, 4 KiB, at +1 MiB + 12 KiB */
#define VGH_RT_MQD_OFF           0x00104000ull /* GFX HQD MQD image, 4 KiB, at +1 MiB + 16 KiB */

/* PM4 (nvd.h / soc15d.h are NOT vendored -> these opcodes/encodings are from the task + spec 5.4/5.5;
 * anything not confirmable against a vendored header is tagged UNVERIFIED). */
#define VGH_PM4_TYPE3(op, n)     ((3u << 30) | (((n) & 0x3FFFu) << 16) | (((op) & 0xFFu) << 8))
#define VGH_PM4_SET_UCONFIG_REG  0x79u   /* PACKET3_SET_UCONFIG_REG */
#define VGH_PM4_WRITE_DATA       0x37u   /* PACKET3_WRITE_DATA */
#define VGH_PM4_INDIRECT_BUFFER  0x3Fu   /* PACKET3_INDIRECT_BUFFER */
#define VGH_PM4_RELEASE_MEM      0x49u   /* PACKET3_RELEASE_MEM */
/* CP-start preamble ops (amdgpu cp_gfx_start). The CP won't process a bare register write as its
 * first-ever packet after autoload; CONTEXT_CONTROL primes its state tracking. UNVERIFIED opcodes
 * (nvd.h not vendored). */
#define VGH_PM4_PREAMBLE_CNTL    0x4Au   /* PACKET3_PREAMBLE_CNTL */
#define VGH_PM4_CONTEXT_CONTROL  0x28u   /* PACKET3_CONTEXT_CONTROL */
#define VGH_PM4_CLEAR_STATE      0x12u   /* PACKET3_CLEAR_STATE */
#define VGH_PM4_PREAMBLE_BEGIN   (2u << 28) /* PACKET3_PREAMBLE_BEGIN_CLEAR_STATE */
#define VGH_PM4_PREAMBLE_END     (3u << 28) /* PACKET3_PREAMBLE_END_CLEAR_STATE */
#define VGH_PM4_UCONFIG_START    0xC000u /* nvd.h-VERIFIED PACKET3_SET_UCONFIG_REG_START: uconfig regs
                                          * indexed from this dword; SCRATCH_REG0 dword 0x2040 fits */

/* Boot-5 (2026-07-03) attempt B2 "hqd-cepart": amdgpu's cp_gfx_start CE tail, emitted right after
 * PREAMBLE_END and before SET_UCONFIG_REG (gfx_v10_0_cp_gfx_start, behavior-only reference --
 * scratchpad gfx_v10_0.c line ~6006): PACKET3(PACKET3_SET_BASE, 2) / PACKET3_BASE_INDEX(CE_PARTITION_BASE)
 * / 0x8000 / 0x8000. Neither PACKET3_SET_BASE's opcode value nor CE_PARTITION_BASE's index value are
 * resolvable from any vendored header (nvd.h/soc15d.h, where amdgpu actually defines them, are not
 * vendored here, same gap as the other PM4 opcodes above) -- UNVERIFIED, taken from the task brief. */
/* nvd.h is NOW VENDORED (../amdgpu/include/nvd.h, MIT -- the amdgpu IP-block sources carry the X11
 * permission notice, not GPL; discovered 2026-07-04, ending the UNVERIFIED-PM4-encoding era). */
#define VGH_PM4_CLEAR_STATE         0x12u /* nvd.h PACKET3_CLEAR_STATE */
#define VGH_PM4_SET_BASE            0x11u /* nvd.h PACKET3_SET_BASE */
#define VGH_PM4_SET_BASE_CE_PARTITION 3u  /* nvd.h CE_PARTITION_BASE = 3 -- boots 6/7 wrongly used 2
                                           * (a different partition); prime suspect for the CE wedging
                                           * at the first INDIRECT_BUFFER (boot 7 IB/fence stall) */
#define VGH_PM4_SET_CONTEXT_REG       0x69u     /* nvd.h PACKET3_SET_CONTEXT_REG (line 321) */
#define VGH_PM4_SET_CONTEXT_REG_START 0x0000a000u /* nvd.h PACKET3_SET_CONTEXT_REG_START (line 322) */

/* Boot 10 (2026-07-04) full-CSB pivot: amdgpu's recorded clear-state preamble (gfx10_cs_data, the
 * SECT_CONTEXT register blocks gfx_v10_0_get_csb_buffer emits between CONTEXT_CONTROL and
 * PREAMBLE_END) is now vendored verbatim instead of re-derived -- clearstate_gfx10.h/clearstate_defs.h
 * (amdgpu/include/, both MIT, verified against the X11 permission notice in each file's own header
 * comment). Both are pure C89 data tables with zero #includes of their own (clearstate_gfx10.h only
 * needs the two tiny struct/enum defs from clearstate_defs.h), so -- unlike v10_structs.h, which this
 * file transcribes by hand into VGH_MQD_DW_* defines because it has no natural array form -- these are
 * large data tables better included verbatim than hand-copied. This is the file's first real
 * #include; syntax-checked clean under /TC (C mode) as part of this change. */
#include "../../amdgpu/include/clearstate_defs.h"
#include "../../amdgpu/include/clearstate_gfx10.h"

/* struct v10_gfx_mqd (amdgpu/include/v10_structs.h, MIT) field dword offsets, for building the MQD
 * IMAGE at VGH_RT_MQD_OFF. v10_structs.h has no #include of its own (bare uint32_t, no stdint.h in
 * this freestanding TU) -- rather than pull in a header, these are the exact dword indices read off
 * the vendored struct (each `// offset: N (0xHH)` comment in the struct matches the value below).
 * Only the fields gfx_v10_0_gfx_mqd_init (v6.6) sets are needed; the MQD is otherwise zeroed. */
#define VGH_MQD_DW_CP_MQD_BASE_ADDR           128u /* 0x80 */
#define VGH_MQD_DW_CP_MQD_BASE_ADDR_HI        129u /* 0x81 */
#define VGH_MQD_DW_CP_GFX_HQD_ACTIVE          130u /* 0x82 */
#define VGH_MQD_DW_CP_GFX_HQD_VMID            131u /* 0x83 */
#define VGH_MQD_DW_CP_GFX_HQD_QUEUE_PRIORITY  134u /* 0x86 */
#define VGH_MQD_DW_CP_GFX_HQD_QUANTUM         135u /* 0x87 */
#define VGH_MQD_DW_CP_GFX_HQD_BASE            136u /* 0x88 */
#define VGH_MQD_DW_CP_GFX_HQD_BASE_HI         137u /* 0x89 */
#define VGH_MQD_DW_CP_GFX_HQD_RPTR            138u /* 0x8A */
#define VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR       139u /* 0x8B */
#define VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR_HI    140u /* 0x8C */
#define VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_LO    141u /* 0x8D */
#define VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_HI    142u /* 0x8E */
#define VGH_MQD_DW_CP_RB_DOORBELL_CONTROL     143u /* 0x8F */
#define VGH_MQD_DW_CP_GFX_HQD_CNTL            145u /* 0x91 */
#define VGH_MQD_DW_CP_GFX_HQD_WPTR            149u /* 0x95 */
#define VGH_MQD_DW_CP_GFX_HQD_WPTR_HI         150u /* 0x96 */
#define VGH_MQD_DW_CP_GFX_MQD_CONTROL         162u /* 0xA2 */

static volatile unsigned char *g_pspWin;   /* CPU pointer to the UC-mapped carveout window */
static unsigned long long g_pspWinMc;      /* the same window as a GPU/MC address (FB_LOCATION-based) */
static unsigned int g_pspFenceSeq;         /* monotonically increasing PSP command fence value */

/* Stage (c) publishes its GART test buffer so stage (e) can carve the CP ring/IB/fence/scratch out
 * of it without touching the page table again: the buffer's first 2 MiB is GART-mapped 1:1 at GPU
 * VA [0, 2 MiB) (PuckGpuGartBringUp), so for any byte in it GPU-VA == phys == CPU addr. WC-typed, so
 * CPU stores post to DRAM promptly (the CP does not snoop; an HDP flush is still needed so the GPU's
 * read side sees them). g_gartTestBusy left set once (c) succeeds. */
static volatile unsigned char *g_gartTestCpu;   /* CPU pointer to the 2 MiB GART test buffer */
static unsigned long long      g_gartTestVa;     /* its GPU-VA base = 0 (GART maps VA [0,2MiB)->buffer) */
static int                     g_gartTestReady;  /* 1 after stage (c) mapped + verified the buffer */

static void GpuDelayMs(unsigned int ms)
{
    unsigned long long end = __rdtsc() + (g_tscFreqHz / 1000ull) * ms;
    while (__rdtsc() < end) { }
}

/* Poll (reg & mask) == want with a TSC deadline. On timeout: print + -1 (caller returns, no hang). */
static int GpuWaitReg(unsigned int dw, unsigned int mask, unsigned int want, unsigned int timeoutMs,
                      const char *what)
{
    unsigned long long end = __rdtsc() + (g_tscFreqHz / 1000ull) * timeoutMs;
    unsigned int v = 0;
    for (;;)
    {
        v = g_gpuRegs[dw];
        if ((v & mask) == want) return 0;
        if (__rdtsc() > end) break;
    }
    SerialPuts("[gpu]  TIMEOUT "); SerialPuts(what);
    SerialPuts(" last="); EcamPutHex((unsigned long long)v, 8); SerialPuts("\r\n");
    return -1;
}

/* Little-endian u32 from a byte buffer (firmware container fields; unaligned-safe). */
static unsigned int FwLe32(const unsigned char *p, unsigned long long off)
{
    return (unsigned int)p[off] | ((unsigned int)p[off + 1] << 8)
         | ((unsigned int)p[off + 2] << 16) | ((unsigned int)p[off + 3] << 24);
}

/* Copy a blob into the UC window (dword stores; a byte tail for odd sizes), then drain. */
static void GpuUcWrite(unsigned long long winOff, const unsigned char *src, unsigned long long n)
{
    volatile unsigned char *d = g_pspWin + winOff;
    unsigned long long i = 0;
    for (; i + 4 <= n; i += 4)
        *(volatile unsigned int *)(d + i) = FwLe32(src, i);
    for (; i < n; i++)
        d[i] = src[i];
    PuckMfence();
}

static void GpuUcZero(unsigned long long winOff, unsigned long long n)
{
    volatile unsigned int *d = (volatile unsigned int *)(g_pspWin + winOff);
    unsigned long long i;
    for (i = 0; i < n / 4; i++) d[i] = 0;
    PuckMfence();
}

/* Common ucode container header (spec 4.7): payload at +ucode_array_offset_bytes(+24), length
 * ucode_size_bytes(+20). Returns 0 on a malformed header (defensive: log + skip at the caller). */
static const unsigned char *FwPayload(const PuckGpuFw *fw, unsigned int *outSize)
{
    unsigned int off, size;
    if (!fw || fw->size < 32) return 0;
    size = FwLe32(fw->data, 20);
    off = FwLe32(fw->data, 24);
    if (off < 32 || size == 0 || (unsigned long long)off + size > fw->size) return 0;
    *outSize = size;
    return fw->data + off;
}

/* Submit one PSP command through the KM ring and wait for its fence (spec 4.3).
 * uni = the command-specific union dwords (written at cmd+28); resp[0]=status(+864),
 * resp[1]=tmr_size(+880). Returns 0 = fence arrived (status may still be non-zero). */
static int PuckPspSubmit(unsigned int cmdId, const unsigned int *uni, unsigned int uniCount,
                         unsigned int *resp, const char *what)
{
    volatile unsigned int *cmd = (volatile unsigned int *)(g_pspWin + VGH_PSPWIN_CMD);
    volatile unsigned int *fenceBuf = (volatile unsigned int *)(g_pspWin + VGH_PSPWIN_FENCE);
    volatile unsigned int *frame;
    unsigned long long cmdMc = g_pspWinMc + VGH_PSPWIN_CMD;
    unsigned long long fenceMc = g_pspWinMc + VGH_PSPWIN_FENCE;
    unsigned long long end;
    unsigned int i, wptr, fence;

    /* psp_gfx_cmd_resp (1024 B): buf_size(+0), buf_version(+4), cmd_id(+8), union(+28), resp(+864). */
    for (i = 0; i < 1024u / 4u; i++) cmd[i] = 0;
    cmd[0] = 1024u;
    cmd[1] = 1u; /* PSP_GFX_CMD_BUF_VERSION */
    cmd[2] = cmdId;
    for (i = 0; i < uniCount; i++) cmd[7 + i] = uni[i];

    fence = ++g_pspFenceSeq;
    wptr = g_gpuRegs[VGH_MP0_C2PMSG_67]; /* KM ring wptr, in dwords */
    if (wptr >= VGH_PSP_RING_BYTES / 4u || (wptr & 15u) != 0)
    {
        SerialPuts("[gpu]  PSP wptr bogus: "); EcamPutHex((unsigned long long)wptr, 8);
        SerialPuts(" ("); SerialPuts(what); SerialPuts(")\r\n");
        return -1;
    }

    /* psp_gfx_rb_frame (64 B): cmd_buf_addr_lo/hi(+0/+4), cmd_buf_size(+8), fence_addr_lo/hi
     * (+12/+16), fence_value(+20), rest 0. */
    frame = (volatile unsigned int *)(g_pspWin + VGH_PSPWIN_RING + (unsigned long long)wptr * 4ull);
    for (i = 0; i < 16; i++) frame[i] = 0;
    frame[0] = (unsigned int)(cmdMc & 0xFFFFFFFFull);
    frame[1] = (unsigned int)(cmdMc >> 32);
    frame[2] = 1024u;
    frame[3] = (unsigned int)(fenceMc & 0xFFFFFFFFull);
    frame[4] = (unsigned int)(fenceMc >> 32);
    frame[5] = fence;
    PuckMfence();
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0; /* spec 4.3 step 5: HDP flush so the PSP sees frame + cmd */

    g_gpuRegs[VGH_MP0_C2PMSG_67] = (wptr + 16u) % (VGH_PSP_RING_BYTES / 4u); /* consume trigger */

    end = __rdtsc() + (g_tscFreqHz / 1000ull) * 1000ull; /* 1 s (spec: 20000 x ~10 us) */
    for (;;)
    {
        if (fenceBuf[0] == fence) break;
        if (__rdtsc() > end)
        {
            SerialPuts("[gpu]  TIMEOUT PSP fence: "); SerialPuts(what);
            SerialPuts(" fence="); EcamPutHex((unsigned long long)fenceBuf[0], 8);
            SerialPuts(" want="); EcamPutHex((unsigned long long)fence, 8);
            SerialPuts(" C2P64="); EcamPutHex((unsigned long long)g_gpuRegs[VGH_MP0_C2PMSG_64], 8);
            SerialPuts("\r\n");
            return -1;
        }
    }
    if (resp)
    {
        resp[0] = cmd[216]; /* resp.status  (+864) */
        resp[1] = cmd[220]; /* resp.tmr_size(+880) */
    }
    return 0;
}

/* One LOAD_IP_FW: stage the payload in the fw buffer, submit, report. 0 = accepted (status 0). */
static int PuckPspLoadIpFw(unsigned int fwType, const unsigned char *payload, unsigned int size,
                           const char *what)
{
    unsigned int uni[4], resp[2];
    unsigned long long fwMc = g_pspWinMc + VGH_PSPWIN_FW;

    if (!payload || size == 0 || size > 0x100000u)
    {
        SerialPuts("[gpu]  skip "); SerialPuts(what); SerialPuts(": bad container/payload\r\n");
        return -1;
    }
    GpuUcWrite(VGH_PSPWIN_FW, payload, size);
    uni[0] = (unsigned int)(fwMc & 0xFFFFFFFFull);
    uni[1] = (unsigned int)(fwMc >> 32);
    uni[2] = size;
    uni[3] = fwType;
    if (PuckPspSubmit(VGH_PSP_CMD_LOAD_IP_FW, uni, 4, resp, what) != 0)
        return -1;
    SerialPuts("[gpu]  LOAD_IP_FW "); SerialPuts(what);
    SerialPuts(" type="); EcamPutDec(fwType);
    SerialPuts(" size="); EcamPutDec(size);
    if (resp[0] != 0) { SerialPuts(" STATUS="); EcamPutHex((unsigned long long)resp[0], 8); }
    SerialPuts(resp[0] == 0 ? " ok\r\n" : " (rejected)\r\n");
    return resp[0] == 0 ? 0 : -1;
}

/* Stage (c): GMC v10 / GPUVM, GC hub only (spec 3). Places the VMID0 GART page table in the
 * carveout window, points the first 2 MiB of GPU VA at a WC-typed DMA test buffer, enables L1/L2 +
 * context 0, TLB-invalidates through engine 17, and asserts the no-fault register state. Also maps
 * the carveout window itself (the PSP buffers ride on it, so stage (d) requires (c)'s mapping). */
static int PuckGpuGartBringUp(void)
{
    unsigned long long fbBase, fbTop, carvePhys, winPhys, pt, dummy, i;
    volatile unsigned long long *ptes;
    PuckDmaBuf test;

    fbBase = ((unsigned long long)(g_gpuRegs[VGH_GCMC_VM_FB_LOCATION_BASE] & 0xFFFFFFu)) << 24;
    fbTop = (((unsigned long long)(g_gpuRegs[VGH_GCMC_VM_FB_LOCATION_TOP] & 0xFFFFFFu)) << 24) | 0xFFFFFFull;
    carvePhys = ((unsigned long long)(g_gpuRegs[VGH_GCMC_VM_FB_OFFSET] & 0xFFFFFFu)) << 24;
    if (fbBase == 0 || fbTop <= fbBase || carvePhys == 0)
    { SerialPuts("[gpu] (c) FB_LOCATION not posted; skipping GMC bring-up\r\n"); return -1; }
    if (fbTop - fbBase + 1ull < VGH_PSPWIN_CARVEOUT_OFF + VGH_PSPWIN_SIZE)
    { SerialPuts("[gpu] (c) carveout too small for the PSP window; skipping\r\n"); return -1; }

    winPhys = carvePhys + VGH_PSPWIN_CARVEOUT_OFF;
    if (winPhys < g_puckMappedBytes && winPhys + VGH_PSPWIN_SIZE > g_puckMappedBytes)
    { SerialPuts("[gpu] (c) carveout window straddles the identity map; skipping\r\n"); return -1; }
    g_pspWin = (volatile unsigned char *)PuckMapMmio(winPhys, VGH_PSPWIN_SIZE, PUCK_MT_UC);
    g_pspWinMc = fbBase + VGH_PSPWIN_CARVEOUT_OFF;
    SerialPuts("[gpu] (c) carveout window phys="); EcamPutHex(winPhys, 10);
    SerialPuts(" mc="); EcamPutHex(g_pspWinMc, 10); SerialPuts(" (32 MiB, UC)\r\n");

    /* GART backing: one 2 MiB DMA buffer, retyped WC so CPU pattern writes land in DRAM (the VM
     * walker and GPU clients do not snoop); its last 4 KiB doubles as the fault/unmapped dummy. */
    test = PuckDmaAlloc(0x200000ull, 0x200000ull);
    PuckMapMmio(test.phys, 0x200000ull, PUCK_MT_WC); /* exactly one 2 MiB leaf: no neighbours retyped */
    dummy = test.phys + 0x200000ull - 0x1000ull;

    /* Single-level VMID0 table (spec 3.2): 8-byte PTEs, one per 4 KiB GPU page; every entry valid
     * (dummy-backed) so a stray translation faults into the dummy, not the walker.
     * The PAGE_TABLE_BASE register takes the table's PHYSICAL DRAM address, not its MC address --
     * amdgpu runs amdgpu_gmc_vram_mc2pa() on it (gmc_v10_0_get_vm_pde). Handing the walker the MC
     * address (g_pspWinMc-based) made it read garbage -> the WALKER_ERROR+MAPPING_ERROR fault
     * (VM_L2_FAULT=0xd1a) the CP hit on the first real translation. winPhys is the window's DRAM
     * address; the PTEs already hold physical addresses, so only the root pointer was wrong. */
    pt = winPhys + VGH_PSPWIN_GARTPT;                    /* PHYSICAL DRAM address of the page table */
    ptes = (volatile unsigned long long *)(g_pspWin + VGH_PSPWIN_GARTPT);
    for (i = 0; i < VGH_GART_SIZE / 4096ull; i++)
        ptes[i] = (dummy & 0x0000FFFFFFFFF000ull) | VGH_GART_PTE_FLAGS;
    for (i = 0; i < 0x200000ull / 4096ull; i++) /* GPU VA [0, 2 MiB) -> the test buffer, 1:1 */
        ptes[i] = ((test.phys + i * 4096ull) & 0x0000FFFFFFFFF000ull) | VGH_GART_PTE_FLAGS;
    PuckMfence();

    /* (a) context-0 page-table root + VA range (spec 3.3a). GART VA = [0, 256 MiB). */
    g_gpuRegs[VGH_GCVM_CTX0_PT_BASE_LO] = (unsigned int)((pt | 1ull) & 0xFFFFFFFFull); /* |1 = PDE VALID */
    g_gpuRegs[VGH_GCVM_CTX0_PT_BASE_HI] = (unsigned int)(pt >> 32);
    g_gpuRegs[VGH_GCVM_CTX0_PT_START_LO] = 0;
    g_gpuRegs[VGH_GCVM_CTX0_PT_START_HI] = 0;
    g_gpuRegs[VGH_GCVM_CTX0_PT_END_LO] = (unsigned int)((VGH_GART_SIZE - 1ull) >> 12);
    g_gpuRegs[VGH_GCVM_CTX0_PT_END_HI] = 0;

    /* (b) system aperture = the inherited FB carveout; AGP empty; dummy as the unmapped/fault
     * default (spec 3.3b). FB_LOCATION itself is NOT rewritten. */
    g_gpuRegs[VGH_GCMC_VM_AGP_BASE] = 0;
    g_gpuRegs[VGH_GCMC_VM_AGP_BOT] = 0x00FFFFFFu; /* empty: bot > top */
    g_gpuRegs[VGH_GCMC_VM_AGP_TOP] = 0;
    g_gpuRegs[VGH_GCMC_VM_SYS_AP_LOW] = (unsigned int)(fbBase >> 18);
    g_gpuRegs[VGH_GCMC_VM_SYS_AP_HIGH] = (unsigned int)(fbTop >> 18);
    g_gpuRegs[VGH_GCMC_VM_SYS_DEFAULT_LSB] = (unsigned int)(dummy >> 12);
    g_gpuRegs[VGH_GCMC_VM_SYS_DEFAULT_MSB] = (unsigned int)(dummy >> 44);
    g_gpuRegs[VGH_GCVM_L2_FAULT_DEFAULT_LO] = (unsigned int)(dummy >> 12);
    g_gpuRegs[VGH_GCVM_L2_FAULT_DEFAULT_HI] = (unsigned int)(dummy >> 44);

    /* (c) L1 TLB: ENABLE_L1_TLB | SYSTEM_ACCESS_MODE=3 | ENABLE_ADVANCED_DRIVER_MODEL, MTYPE=UC
     * (spec 3.3c; masks from gc_10_3_0_sh_mask.h). */
    g_gpuRegs[VGH_GCMC_VM_MX_L1_TLB_CNTL] = 0x00000059u;

    /* (d) L2 cache, from the gc_10_3_0_default.h reset values (spec 3.3d):
     * L2_CNTL  = default 0x00080602 - fragment processing + ENABLE_L2_CACHE + DEFAULT_PAGE_OUT
     * L2_CNTL3 = default 0x80100007 with BANK_SELECT=9, BIGK_FRAGMENT_SIZE=6
     * L2_CNTL4 = default 0x000000c1 minus the two VMC_TAP_*_REQUEST_PHYSICAL bits */
    g_gpuRegs[VGH_GCVM_L2_CNTL] = 0x00080E01u;
    g_gpuRegs[VGH_GCVM_L2_CNTL2] = 0x00000003u; /* INVALIDATE_ALL_L1_TLBS | INVALIDATE_L2_CACHE */
    g_gpuRegs[VGH_GCVM_L2_CNTL3] = 0x80130009u;
    g_gpuRegs[VGH_GCVM_L2_CNTL4] = 0x00000001u;
    g_gpuRegs[VGH_GCVM_L2_CNTL5] = 0x00003FE0u;

    /* (f) context-1 identity aperture disabled: LOW > HIGH so any context-1 access faults. */
    g_gpuRegs[VGH_GCVM_L2_ID_AP_LOW_LO] = 0xFFFFFFFFu;
    g_gpuRegs[VGH_GCVM_L2_ID_AP_LOW_HI] = 0x0000000Fu;
    g_gpuRegs[VGH_GCVM_L2_ID_AP_HIGH_LO] = 0;
    g_gpuRegs[VGH_GCVM_L2_ID_AP_HIGH_HI] = 0;
    g_gpuRegs[VGH_GCVM_L2_ID_PHYS_LO] = 0;
    g_gpuRegs[VGH_GCVM_L2_ID_PHYS_HI] = 0;

    /* (e) enable VMID0: single-level (PAGE_TABLE_DEPTH=0), no retry faults (spec 3.3e). */
    g_gpuRegs[VGH_GCVM_CONTEXT0_CNTL] = 0x00000001u;

    /* TLB invalidate through engine 17, VMID0, all PTE/PDE/L1 bits (spec 3.4; GFXHUB early path
     * uses no semaphore). HDP-flush first so the walker sees the CPU-written PTEs. */
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;
    (void)g_gpuRegs[VGH_GCVM_L2_FAULT_STATUS]; /* clear any stale fault latch */
    g_gpuRegs[VGH_GCVM_INV_ENG17_REQ] = 0x00F80001u;
    if (GpuWaitReg(VGH_GCVM_INV_ENG17_ACK, 0x1u, 0x1u, 1000, "GCVM_INVALIDATE_ENG17_ACK") != 0)
        return -1;

    /* CPU-side pattern through the GART-backed page + the no-fault assertion. (A GPU-side readback
     * needs an engine -- that is stage (e); here the observable is register state.) */
    {
        volatile unsigned int *tp = (volatile unsigned int *)test.cpu;
        unsigned int fault;
        tp[0] = 0x50554B21u; /* 'PUK!' */
        PuckMfence();
        fault = g_gpuRegs[VGH_GCVM_L2_FAULT_STATUS];
        if (fault == 0)
        {
            /* Publish the test region for stage (e) to carve the CP ring/IB/fence out of. The GART
             * maps GPU-VA [0,2MiB) -> phys [test.phys, ...) (PT_START=0, PTE[i]=test.phys+i*4096), so
             * the region's GPU-VA BASE IS 0 -- NOT test.phys. (First Deck stage-(e) boots set
             * CP_RB0_BASE from test.phys ~6 GiB, outside the 256 MiB GART; the CP could never fetch
             * the ring. GPU VA and CPU addr are independent: CPU uses test.cpu+off, GPU uses 0+off.) */
            g_gartTestCpu = (volatile unsigned char *)test.cpu;
            g_gartTestVa = 0;
            g_gartTestReady = 1;
            SerialPuts("[gpu] (c) GMC/GART PASS: VMID0 256 MiB @ VA 0, PT pa=");
            EcamPutHex(pt, 10); SerialPuts(", no VM faults\r\n");
        }
        else
        {
            SerialPuts("[gpu] (c) GMC/GART FAIL: GCVM_L2_PROTECTION_FAULT_STATUS=");
            EcamPutHex((unsigned long long)fault, 8); SerialPuts("\r\n");
            return -1;
        }
    }
    return 0;
}

/* Stage (d): create the PSP KM ring (spec 4.2), LOAD_TOC -> SETUP_TMR -> LOAD_IP_FW each Van Gogh
 * blob -> AUTOLOAD_RLC (spec 4.4), then poll the autoload-complete pair (spec 4.6). */
static int PuckGpuPspLoad(void)
{
    const PuckGpuFw *fw;
    const unsigned char *payload;
    unsigned int size, uni[6], resp[2], tmrSize;
    unsigned long long ringMc = g_pspWinMc + VGH_PSPWIN_RING;
    unsigned long long tmrMc = g_pspWinMc + VGH_PSPWIN_TMR;
    unsigned long long tmrPhys;

    /* -- KM ring create (spec 4.2; sOS alive, so no bootloader steps) -- */
    GpuUcZero(VGH_PSPWIN_RING, 0x3000ull); /* ring + cmd + fence pages */
    if (GpuWaitReg(VGH_MP0_C2PMSG_64, 0x80000000u, 0x80000000u, 1000, "C2PMSG_64 sOS ready") != 0)
        return -1;
    g_gpuRegs[VGH_MP0_C2PMSG_69] = (unsigned int)(ringMc & 0xFFFFFFFFull);
    g_gpuRegs[VGH_MP0_C2PMSG_70] = (unsigned int)(ringMc >> 32);
    g_gpuRegs[VGH_MP0_C2PMSG_71] = VGH_PSP_RING_BYTES;
    g_gpuRegs[VGH_MP0_C2PMSG_64] = 0x00020000u; /* PSP_RING_TYPE__KM << 16 */
    GpuDelayMs(20);                             /* documented HW handshake delay */
    if (GpuWaitReg(VGH_MP0_C2PMSG_64, 0x8000FFFFu, 0x80000000u, 1000, "C2PMSG_64 ring create") != 0)
        return -1;
    SerialPuts("[gpu] (d) PSP KM ring created @ mc="); EcamPutHex(ringMc, 10); SerialPuts("\r\n");

    /* -- LOAD_TOC: the response's tmr_size sizes the TMR (spec 4.4 step 1) -- */
    fw = PuckGpuFwFind("vangogh_toc.bin");
    payload = FwPayload(fw, &size);
    if (!payload)
    { SerialPuts("[gpu] (d) vangogh_toc.bin missing/malformed; aborting PSP load\r\n"); return -1; }
    GpuUcWrite(VGH_PSPWIN_FW, payload, size);
    uni[0] = (unsigned int)((g_pspWinMc + VGH_PSPWIN_FW) & 0xFFFFFFFFull);
    uni[1] = (unsigned int)((g_pspWinMc + VGH_PSPWIN_FW) >> 32);
    uni[2] = size;
    if (PuckPspSubmit(VGH_PSP_CMD_LOAD_TOC, uni, 3, resp, "LOAD_TOC") != 0)
        return -1;
    tmrSize = resp[1];
    SerialPuts("[gpu]  LOAD_TOC status="); EcamPutHex((unsigned long long)resp[0], 8);
    SerialPuts(" tmr_size="); EcamPutHex((unsigned long long)tmrSize, 8); SerialPuts("\r\n");
    if (tmrSize == 0 || tmrSize > (unsigned int)VGH_PSPWIN_TMR_MAX)
    {
        SerialPuts("[gpu]  implausible tmr_size; using 0x400000\r\n"); /* amdgpu PSP_TMR_SIZE default */
        tmrSize = 0x400000u;
    }
    tmrSize = (tmrSize + 0xFFFFFu) & ~0xFFFFFu; /* PSP_TMR_ALIGNMENT = 1 MiB */

    /* -- SETUP_TMR (spec 4.4 steps 2-3): TMR at the window base, MC + system-physical both given -- */
    tmrPhys = (g_pspWinMc + VGH_PSPWIN_TMR) - ((unsigned long long)(g_gpuRegs[VGH_GCMC_VM_FB_LOCATION_BASE] & 0xFFFFFFu) << 24)
            + (((unsigned long long)(g_gpuRegs[VGH_GCMC_VM_FB_OFFSET] & 0xFFFFFFu)) << 24);
    uni[0] = (unsigned int)(tmrMc & 0xFFFFFFFFull);
    uni[1] = (unsigned int)(tmrMc >> 32);
    uni[2] = tmrSize;
    uni[3] = 0x2u; /* UNVERIFIED: bitfield.virt_phy_addr = bit1 (bit0 = sriov_enabled) per psp_gfx_if.h */
    uni[4] = (unsigned int)(tmrPhys & 0xFFFFFFFFull);
    uni[5] = (unsigned int)(tmrPhys >> 32);
    if (PuckPspSubmit(VGH_PSP_CMD_SETUP_TMR, uni, 6, resp, "SETUP_TMR") != 0)
        return -1;
    SerialPuts("[gpu]  SETUP_TMR status="); EcamPutHex((unsigned long long)resp[0], 8);
    SerialPuts(" mc="); EcamPutHex(tmrMc, 10); SerialPuts(" size="); EcamPutHex((unsigned long long)tmrSize, 8);
    SerialPuts("\r\n");

    /* -- LOAD_IP_FW, spec 4.4 order: sdma, ce, pfp, me, mec, mec2, RLC sublists, RLC_G -- */
    {
        static const struct { const char *blob; unsigned int fwType; unsigned int mecJt; } list[] = {
            { "vangogh_sdma.bin", VGH_FW_TYPE_SDMA0,  0 },
            { "vangogh_ce.bin",   VGH_FW_TYPE_CP_CE,  0 },
            { "vangogh_pfp.bin",  VGH_FW_TYPE_CP_PFP, 0 },
            { "vangogh_me.bin",   VGH_FW_TYPE_CP_ME,  0 },
            { "vangogh_mec.bin",  VGH_FW_TYPE_CP_MEC, 1 },
            { "vangogh_mec2.bin", VGH_FW_TYPE_CP_MEC, 1 }, /* UNVERIFIED: spec 4.4 maps mec2 to CP_MEC too */
        };
        unsigned int i;
        for (i = 0; i < sizeof(list) / sizeof(list[0]); i++)
        {
            fw = PuckGpuFwFind(list[i].blob);
            payload = FwPayload(fw, &size);
            if (payload && list[i].mecJt && fw->size >= 44)
            {
                unsigned int jtSize = FwLe32(fw->data, 40) * 4u; /* gfx v1_0 hdr: jt_size dwords @ +40 */
                if (jtSize < size) size -= jtSize; /* JT tail skipped on the autoload path (spec 4.4) */
            }
            (void)PuckPspLoadIpFw(list[i].fwType, payload, payload ? size : 0, list[i].blob);
        }
    }

    /* RLC v2.1 sub-blobs (spec 4.4 step 4 + 4.7): save/restore list cntl + gpm + srm, offsets from
     * the rlc_firmware_header_v2_1 fields (byte layout verified against the vendored blob), then
     * RLC_G itself last. vangogh_rlc.bin is header v2.1 -- no IRAM/DRAM sub-blobs exist in it. */
    fw = PuckGpuFwFind("vangogh_rlc.bin");
    if (fw && fw->size >= 156 && FwLe32(fw->data, 8) == 0x00010002u) /* hdr ver minor=1, major=2 @ +8/+10 */
    {
        static const struct { unsigned int sizeOff, dataOff, fwType; const char *what; } sub[] = {
            { 116, 120, VGH_FW_TYPE_RLC_SRM_CNTL, "rlc.srlc_cntl" },
            { 132, 136, VGH_FW_TYPE_RLC_GPM_MEM,  "rlc.srlc_gpm" },
            { 148, 152, VGH_FW_TYPE_RLC_SRM_MEM,  "rlc.srlc_srm" },
        };
        unsigned int i;
        for (i = 0; i < sizeof(sub) / sizeof(sub[0]); i++)
        {
            unsigned int sSize = FwLe32(fw->data, sub[i].sizeOff);
            unsigned int sOff = FwLe32(fw->data, sub[i].dataOff);
            if (sSize == 0 || (unsigned long long)sOff + sSize > fw->size)
            { SerialPuts("[gpu]  skip "); SerialPuts(sub[i].what); SerialPuts(": empty/out of range\r\n"); continue; }
            (void)PuckPspLoadIpFw(sub[i].fwType, fw->data + sOff, sSize, sub[i].what);
        }
    }
    else
        SerialPuts("[gpu]  rlc header not v2.1; loading RLC_G only\r\n");
    payload = FwPayload(fw, &size);
    if (PuckPspLoadIpFw(VGH_FW_TYPE_RLC_G, payload, payload ? size : 0, "rlc.rlc_g") != 0)
        SerialPuts("[gpu]  RLC_G rejected -- autoload will likely fail\r\n");

    /* -- AUTOLOAD_RLC: all graphics fw is in; start the RLC (spec 4.4 step 5) -- */
    if (PuckPspSubmit(VGH_PSP_CMD_AUTOLOAD_RLC, uni, 0, resp, "AUTOLOAD_RLC") != 0)
        return -1;
    SerialPuts("[gpu]  AUTOLOAD_RLC status="); EcamPutHex((unsigned long long)resp[0], 8); SerialPuts("\r\n");

    /* -- autoload completion (spec 4.6): BOOTLOAD_COMPLETE (bit31) then CP_STAT == 0 -- */
    if (GpuWaitReg(VGH_RLC_BOOTLOAD_STATUS, 0x80000000u, 0x80000000u, 2000, "RLC BOOTLOAD_COMPLETE") != 0)
    {
        GpuPutReg("CP_STAT     ", VGH_CP_STAT);
        GpuPutReg("GRBM_STATUS ", VGH_GRBM_STATUS);
        GpuPutReg("PSP_C2P_64  ", VGH_MP0_C2PMSG_64);
        return -1;
    }
    if (GpuWaitReg(VGH_CP_STAT, 0xFFFFFFFFu, 0, 1000, "CP_STAT idle after autoload") != 0)
    {
        GpuPutReg("GRBM_STATUS ", VGH_GRBM_STATUS);
        return -1;
    }
    return 0;
}

/* Small helpers over a ring-test attempt's working buffer: a CPU dword pointer and the matching GPU
 * VA for a byte offset, both relative to the CURRENT attempt's base (set by RingAttempt just before
 * use). GPU-VA == phys == MC for the "gart" attempt (1:1 GART); for "vram" the MC base is the
 * carveout window's MC address, which is not GPU-VA==MC identity but is still the correct PM4/BASE
 * operand (CP_RB0_BASE etc. take MC addresses, not GPU VAs, once VMID0 is bypassed -- see the "vram"
 * attempt comment in RingAttempt). g_rtCurCpu/g_rtCurMc are set once per attempt, not per-call. */
static volatile unsigned char *g_rtCurCpu;   /* CPU base for the in-flight attempt */
static unsigned long long      g_rtCurMc;    /* MC base for the in-flight attempt */
static unsigned int            g_rtRingW;    /* ring dword index after the last kicked packet */

/* Which MMIO wptr register(s) RtKick writes before the doorbell (or instead of it, for "rb0-mmio").
 * 0 = legacy CP_RB0_WPTR/_WPTR_HI (the original, only-ever ring interface before 2026-07-03).
 * 1 = CP_GFX_HQD_WPTR/_WPTR_HI (the HQD/MQD interface, attempts A/B). Set once per attempt, by the
 * attempt function, before any RtKick call in that attempt. */
#define VGH_RT_KICK_LEGACY 0
#define VGH_RT_KICK_HQD    1
static int g_rtKickMode = VGH_RT_KICK_LEGACY;
/* 1 = skip the doorbell write entirely and rely on the MMIO wptr alone (attempt C "rb0-mmio": two
 * hardware boots proved the legacy+doorbell combination dead, so this attempt isolates the pure MMIO
 * path with DOORBELL_EN=0, matching a non-doorbell amdgpu ring). */
static int g_rtKickNoDoorbell = 0;

static volatile unsigned int *RtCpu(unsigned long long off)
{
    return (volatile unsigned int *)(g_rtCurCpu + off);
}
static unsigned long long RtVa(unsigned long long off)
{
    return g_rtCurMc + off;
}

/* Advance the ring write pointer to `wptrDwords`, against the CURRENT attempt's base. On gfx10 the
 * legacy GFX ring wptr does NOT come from the MMIO CP_RB0_WPTR register alone -- a Deck boot proved a
 * write there reads back 0 and the CP (alive, PFP busy, RB_ACTIVE=1) never fetched; our own earlier
 * kick never wrote CP_RB0_WPTR_HI, which is also why the pure-MMIO fallback (now attempt C) never got
 * a fair trial -- amdgpu's non-doorbell path writes both halves. Order (spec 5.3/6): mirror the wptr
 * to memory, HDP-flush so the CP sees the new ring bytes + mirror, write the MMIO wptr (legacy: both
 * CP_RB0_WPTR/_HI; HQD: both CP_GFX_HQD_WPTR/_HI), then ring the 64-bit doorbell unless
 * g_rtKickNoDoorbell -- that write is what made the CP consume the ring on the legacy interface. */
static void RtKick(unsigned int wptrDwords)
{
    *RtCpu(VGH_RT_WPTR_OFF) = wptrDwords; PuckMfence();
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;
    if (g_rtKickMode == VGH_RT_KICK_HQD)
    {
        g_gpuRegs[VGH_CP_GFX_HQD_WPTR] = wptrDwords;
        g_gpuRegs[VGH_CP_GFX_HQD_WPTR_HI] = 0u;
    }
    else
    {
        g_gpuRegs[VGH_CP_RB0_WPTR] = wptrDwords;
        g_gpuRegs[VGH_CP_RB0_WPTR_HI] = 0u; /* amdgpu's non-doorbell path writes both halves */
    }
    if (g_gpuDoorbell && !g_rtKickNoDoorbell)
    {
        *(volatile unsigned long long *)(g_gpuDoorbell + (VGH_GFX_DOORBELL_INDEX << 2)) =
            (unsigned long long)wptrDwords;
        PuckMfence();
    }
}

/* Log2 of a power-of-two (for RB_BUFSZ = log2(ring_bytes/8)). */
static unsigned int RtLog2(unsigned long long v)
{
    unsigned int n = 0;
    while (v > 1ull) { v >>= 1; n++; }
    return n;
}

/* Boot-3 stall dump (2026-07-03): CP_STALLED_STAT1/2/3 + CP_CPF_STALLED_STAT1 + CP_CPC_STALLED_STAT1
 * enumerate BY FIELD what the CP front end / CPF / CPC pipeline is currently blocked waiting on -- the
 * sh_mask field names (gc_10_3_0_sh_mask.h) make the log line self-decoding; this function deliberately
 * does not try to interpret the bits itself. RLC_STAT/RLC_SAFE_MODE/RLC_CP_SCHEDULERS/RLC_GPM_STAT/
 * RLC_BOOTLOAD_STATUS ride along so an un-provisioned RLC (stage-(d) LOAD_IP_FW rejects) is visible in
 * the same dump as the ring stall. `tag` distinguishes the post-un-halt call (still busy after the
 * poll) from the at-fail call (RingDiagDump, end of the attempt) in the log. */
static void RingStallDump(const char *tag)
{
    SerialPuts("[gpu] [stall:"); SerialPuts(tag); SerialPuts("]\r\n");
    GpuPutReg("CP_STALLED_STAT1", VGH_CP_STALLED_STAT1);
    GpuPutReg("CP_STALLED_STAT2", VGH_CP_STALLED_STAT2);
    GpuPutReg("CP_STALLED_STAT3", VGH_CP_STALLED_STAT3);
    GpuPutReg("CP_CPF_STALLED1 ", VGH_CP_CPF_STALLED_STAT1);
    GpuPutReg("CP_CPC_STALLED1 ", VGH_CP_CPC_STALLED_STAT1);
    GpuPutReg("RLC_STAT        ", VGH_RLC_STAT);
    GpuPutReg("RLC_SAFE_MODE   ", VGH_RLC_SAFE_MODE);
    GpuPutReg("RLC_CP_SCHED    ", VGH_RLC_CP_SCHEDULERS);
    GpuPutReg("RLC_GPM_STAT    ", VGH_RLC_GPM_STAT);
    GpuPutReg("RLC_BOOTLOAD    ", VGH_RLC_BOOTLOAD_STATUS);
}

/* Boot-12 (2026-07-04) icache diagnostic: CP_{PFP,ME,CE}_IC_BASE_LO/HI (+ BASE_CNTL) and 4x
 * CP_CE_INSTR_PNTR (mirroring the existing PFP/ME PC dumps below -- a moving value = CE microcode
 * alive and spinning, frozen = genuinely wedged). `tag` labels the call site in the log ("diag" from
 * RingDiagDump's at-fail path, "icache" from PuckGpuRingTest right after KIQ bring-up). If CE_IC_BASE
 * reads 0 while PFP/ME bases are non-zero, that is the smoking gun for the microcode-less-CE
 * hypothesis (see the VGH_CP_CE_INSTR_PNTR comment) -- this dump makes it visible either way, it does
 * not itself decide the verdict. */
static void GpuIcacheDump(const char *tag)
{
    unsigned int i;
    SerialPuts("[gpu] ["); SerialPuts(tag); SerialPuts("] icache bases\r\n");
    GpuPutReg("PFP_IC_BASE_LO  ", VGH_CP_PFP_IC_BASE_LO);
    GpuPutReg("PFP_IC_BASE_HI  ", VGH_CP_PFP_IC_BASE_HI);
    GpuPutReg("PFP_IC_BASE_CNTL", VGH_CP_PFP_IC_BASE_CNTL);
    GpuPutReg("ME_IC_BASE_LO   ", VGH_CP_ME_IC_BASE_LO);
    GpuPutReg("ME_IC_BASE_HI   ", VGH_CP_ME_IC_BASE_HI);
    GpuPutReg("ME_IC_BASE_CNTL ", VGH_CP_ME_IC_BASE_CNTL);
    GpuPutReg("CE_IC_BASE_LO   ", VGH_CP_CE_IC_BASE_LO);
    GpuPutReg("CE_IC_BASE_HI   ", VGH_CP_CE_IC_BASE_HI);
    GpuPutReg("CE_IC_BASE_CNTL ", VGH_CP_CE_IC_BASE_CNTL);
    for (i = 0; i < 4u; i++)
    {
        SerialPuts("[gpu]   CE_PC["); EcamPutDec(i); SerialPuts("] =");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_CE_INSTR_PNTR], 8); SerialPuts("\r\n");
    }
}

/* Failure-side diagnostic dump (spec: what did the CP's own fetch/execute pipeline see?). Called
 * only when an attempt's SCRATCH poll times out. CP_PFP/ME_HEADER_DUMP are FIFOs: each read pops the
 * next queued packet header the PFP/ME fetched, so 8 back-to-back reads either drain real fetched
 * headers (fetch worked, execution stalled downstream) or come back all-zero (the PFP never fetched
 * anything from the ring at all -- indicts ring/doorbell/VMID wiring, not CP execution). Extended
 * 2026-07-03 with the HQD readbacks (attempts A/B program CP_GFX_HQD_* instead of CP_RB0_*, so the
 * legacy RPTR/WPTR dump above reads the wrong registers for those attempts) and the PFP/ME program
 * counters: 4 reads each of CP_PFP_INSTR_PNTR/CP_ME_INSTR_PNTR -- a moving value means the microcode
 * is alive and spinning (e.g. stuck in a wait-loop), a frozen value means it is truly wedged (halted
 * or fetch-stalled). CP_CPF_STATUS is read twice ~1 ms apart for the same reason: frozen vs toggling
 * distinguishes a genuinely stuck CPF pipeline from one still making (slow) progress. Boot-12: also
 * calls GpuIcacheDump("diag") for the CE icache-base/PC readback (see that function's comment). */
static void RingDiagDump(volatile unsigned char *cpuBase)
{
    unsigned int i, rptrWb, cpfA, cpfB;

    GpuPutReg("CP_STAT     ", VGH_CP_STAT);
    GpuPutReg("GRBM_STATUS ", VGH_GRBM_STATUS);
    GpuPutReg("GRBM_STATUS2", VGH_GRBM_STATUS2);
    GpuPutReg("CP_RB0_RPTR ", VGH_CP_RB0_RPTR);

    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0; PuckMfence();
    rptrWb = *(volatile unsigned int *)(cpuBase + VGH_RT_RPTR_OFF);
    SerialPuts("[gpu]   RPTR_WB     ="); EcamPutHex((unsigned long long)rptrWb, 8); SerialPuts("\r\n");

    GpuPutReg("CP_RB0_WPTR ", VGH_CP_RB0_WPTR);
    GpuPutReg("CP_GFX_HQD_ACTIVE", VGH_CP_GFX_HQD_ACTIVE);
    GpuPutReg("CP_GFX_HQD_RPTR  ", VGH_CP_GFX_HQD_RPTR);
    GpuPutReg("CP_GFX_HQD_WPTR  ", VGH_CP_GFX_HQD_WPTR);
    GpuPutReg("VM_L2_FLTST ", VGH_GCVM_L2_FAULT_STATUS);
    GpuPutReg("VM_L2_FLTCTL", VGH_GCVM_L2_FAULT_CNTL);
    GpuPutReg("CP_ROQ_RBST ", VGH_CP_ROQ_RB_STAT);
    GpuPutReg("CP_CPF_STAT ", VGH_CP_CPF_STATUS);
    GpuPutReg("CP_CPF_BUSY ", VGH_CP_CPF_BUSY_STAT);

    /* PFP/ME header-dump FIFOs: 8 reads each, all-zero => never fetched; non-zero (our TYPE3 opcodes,
     * e.g. 0x4A/0x28/0x79 in bits[15:8]) => fetch worked, stall is downstream of the front end. */
    for (i = 0; i < 8u; i++)
    {
        SerialPuts("[gpu]   PFP_HDR["); EcamPutDec(i); SerialPuts("]=");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_PFP_HEADER_DUMP], 8); SerialPuts("\r\n");
    }
    for (i = 0; i < 8u; i++)
    {
        SerialPuts("[gpu]   ME_HDR["); EcamPutDec(i); SerialPuts("] =");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_ME_HEADER_DUMP], 8); SerialPuts("\r\n");
    }

    /* PFP/ME program counters: a moving value across the 4 reads = microcode alive and spinning; a
     * frozen value = wedged (matches whatever CP_STAT/CP_CPF_STATUS already said, corroborating it). */
    for (i = 0; i < 4u; i++)
    {
        SerialPuts("[gpu]   PFP_PC["); EcamPutDec(i); SerialPuts("]=");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_PFP_INSTR_PNTR], 8); SerialPuts("\r\n");
    }
    for (i = 0; i < 4u; i++)
    {
        SerialPuts("[gpu]   ME_PC["); EcamPutDec(i); SerialPuts("] =");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_ME_INSTR_PNTR], 8); SerialPuts("\r\n");
    }

    /* CP_CPF_STATUS twice, ~1 ms apart: frozen (same value) vs toggling (different value) -- toggling
     * would mean the CPF pipeline is still making some progress, not fully wedged. */
    cpfA = g_gpuRegs[VGH_CP_CPF_STATUS];
    GpuDelayMs(1);
    cpfB = g_gpuRegs[VGH_CP_CPF_STATUS];
    SerialPuts("[gpu]   CP_CPF_STAT (t0)="); EcamPutHex((unsigned long long)cpfA, 8);
    SerialPuts(" (t0+1ms)="); EcamPutHex((unsigned long long)cpfB, 8);
    SerialPuts(cpfA == cpfB ? " frozen\r\n" : " toggling\r\n");

    GpuIcacheDump("diag");
    RingStallDump("at-fail");
}

/* One ring-test attempt against a given (cpuBase, mcBase) pair, mirroring amdgpu gfx_v10_0_cp_gfx_start
 * ordering: halt the CP front ends, program EVERY CP_RB0 / doorbell register while halted, THEN
 * un-halt and poll CP_STAT for idle, THEN prime + kick the ring. Every poll is TSC-deadlined; a
 * timeout prints diagnostics and returns 0 (never hang, never reset -- the panel scans out of this
 * device). Returns 1 on SCRATCH_REG0 PASS. `label` is only for log lines ("gart" / "vram" / "rb0-mmio").
 * `noDoorbell` (attempt C "rb0-mmio" only): DOORBELL_EN=0 in CP_RB_DOORBELL_CONTROL and RtKick skips
 * the doorbell write entirely -- pure MMIO CP_RB0_WPTR/_WPTR_HI, the fallback amdgpu takes when a ring
 * has no doorbell; two hardware boots proved the legacy MMIO+doorbell COMBINATION dead, so this
 * isolates the pure-MMIO path (which our old kick never gave a fair trial: it never wrote WPTR_HI).
 * NOT CALLED as of boot 4 (2026-07-03): the legacy "rb0-mmio" attempt is dropped from PuckGpuRingTest's
 * matrix (three hardware boots already proved the legacy CP_RB0_* interface uninformative -- see
 * docs/deck-bringup-handoff.md's boot-3 section). Left in the tree, unused, as a reference for the
 * halt->program->un-halt discipline RingAttemptHqd mirrors; not deleted outright since the legacy
 * interface could still become relevant again if the HQD/KIQ line of investigation dead-ends. */
static int RingAttempt(const char *label, volatile unsigned char *cpuBase, unsigned long long mcBase,
                        int noDoorbell)
{
    unsigned long long rbMc, rptrMc, wptrMc;
    volatile unsigned int *ring, *ib;
    unsigned int bufsz, blksz, w, got;

    g_rtCurCpu = cpuBase;
    g_rtCurMc = mcBase;
    g_rtKickMode = VGH_RT_KICK_LEGACY;
    g_rtKickNoDoorbell = noDoorbell;

    /* (a) Halt the CP front ends before touching any CP_RB0 / doorbell register (amdgpu programs the
     * whole ring while halted, then un-halts once at the end -- gfx_v10_0_cp_gfx_start). Also drop
     * RB_ACTIVE so a stale attempt's ring is not "active" while we reprogram it. */
    g_gpuRegs[VGH_CP_ME_CNTL] = VGH_CP_ME_CNTL_ALL_HALT;
    PuckMfence();
    GpuDelayMs(1);
    g_gpuRegs[VGH_CP_RB_ACTIVE] = 0u;

    /* Buffer addresses, relative to THIS attempt's base (RtVa/RtCpu read g_rtCurCpu/g_rtCurMc). The
     * fence/scratch/IB addresses are computed later, in RingIbFence, once this attempt has PASSed.
     * Boot 10: ring grew 4 KiB->8 KiB, see the VGH_RT_RING_OFF block-layout comment for the full table.
     *   ring   : cpuBase+0x100000  RtVa(0x100000)  (8 KiB)
     *   rptr   : ... +0x102000     RtVa(0x102000)  (CP writes read pointer)
     *   wptr   : ... +0x102040     RtVa(0x102040)  (wptr-poll mirror) */
    rbMc      = RtVa(VGH_RT_RING_OFF);
    rptrMc    = RtVa(VGH_RT_RPTR_OFF);
    wptrMc    = RtVa(VGH_RT_WPTR_OFF);
    ring      = RtCpu(VGH_RT_RING_OFF);
    ib        = RtCpu(VGH_RT_IB_OFF);

    /* (b) Zero the working dwords + the ring/IB for THIS attempt's base. WC memory: PuckMfence
     * drains, then an HDP flush later (before the kick) makes the writes visible to the CP. */
    {
        unsigned int i;
        for (i = 0; i < VGH_RT_RING_BYTES / 4u; i++) ring[i] = 0;
        for (i = 0; i < 0x1000u / 4u; i++) ib[i] = 0;
        *RtCpu(VGH_RT_RPTR_OFF) = 0;
        *RtCpu(VGH_RT_WPTR_OFF) = 0;
        *RtCpu(VGH_RT_FENCE_OFF) = 0;
        *RtCpu(VGH_RT_SCRATCH_OFF) = 0;
        PuckMfence();
    }

    /* (c) Pipe select (ME0/PIPE0) FIRST: GRBM_GFX_CNTL routes subsequent CP_RB* register access to a
     * specific pipe. Without it our ring registers land on whatever pipe the RLC left selected.
     * amdgpu's cp_gfx_switch_pipe does exactly this, still while halted. */
    g_gpuRegs[VGH_GRBM_GFX_CNTL] = 0u;       /* PIPEID=0, MEID=0 -> graphics ME0 pipe0 */
    g_gpuRegs[VGH_CP_RB_WPTR_DELAY] = 0u;
    g_gpuRegs[VGH_CP_RB_VMID] = 0u;          /* ring fetches through VMID0/GART */

    /* (d) CP_RB0_CNTL + addr regs, faithful to amdgpu gfx_v10_0_cp_gfx_resume, still while halted. */
    bufsz = RtLog2((unsigned long long)VGH_RT_RING_BYTES / 8ull); /* log2(8192/8)=log2(1024)=10 (boot 10:
                                                                   * ring grew 4 KiB->8 KiB) */
    blksz = bufsz - 2u;                                           /* = 8 */
    g_gpuRegs[VGH_CP_RB0_CNTL] = bufsz | (blksz << 8);            /* RB_BUFSZ | RB_BLKSZ<<8 */

    g_gpuRegs[VGH_CP_RB0_WPTR] = 0u;
    g_gpuRegs[VGH_CP_RB0_WPTR_HI] = 0u;
    g_gpuRegs[VGH_CP_RB0_RPTR_ADDR] = (unsigned int)(rptrMc & 0xFFFFFFFCu);
    g_gpuRegs[VGH_CP_RB0_RPTR_ADDR_HI] = (unsigned int)((rptrMc >> 32) & 0xFFu);
    g_gpuRegs[VGH_CP_RB_WPTR_POLL_ADDR_LO] = (unsigned int)(wptrMc & 0xFFFFFFFCu);
    g_gpuRegs[VGH_CP_RB_WPTR_POLL_ADDR_HI] = (unsigned int)((wptrMc >> 32) & 0xFFu);
    PuckMfence();
    GpuDelayMs(1);                           /* amdgpu mdelay(1) between the two CNTL writes */
    g_gpuRegs[VGH_CP_RB0_CNTL] = bufsz | (blksz << 8); /* re-arm after the addr writes (amdgpu does) */

    /* (e) Base + active. */
    g_gpuRegs[VGH_CP_RB0_BASE] = (unsigned int)((rbMc >> 8) & 0xFFFFFFFFu);
    g_gpuRegs[VGH_CP_RB0_BASE_HI] = (unsigned int)(rbMc >> 40);
    g_gpuRegs[VGH_CP_RB_ACTIVE] = 1u;

    /* (f) Doorbell: NBIO aperture-enable + PuckMapMmio are idempotent (only done once, globally --
     * g_gpuDoorbell stays mapped across attempts); the CP_RB_DOORBELL_* register writes are re-done
     * per attempt since VGH_CP_ME_CNTL_ALL_HALT above does not clear them, but a fresh attempt after a
     * failed one should still see them re-armed defensively. */
    if (g_gpuDoorbellPhys && !noDoorbell)
    {
        g_gpuRegs[VGH_RCC_DOORBELL_APER_EN] = g_gpuRegs[VGH_RCC_DOORBELL_APER_EN] | 0x1u;
        if (!g_gpuDoorbell)
            g_gpuDoorbell = (volatile unsigned char *)PuckMapMmio(g_gpuDoorbellPhys, 0x1000ull, PUCK_MT_UC);
        g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL] = (VGH_GFX_DOORBELL_INDEX << 2) | VGH_CP_DOORBELL_EN;
        g_gpuRegs[VGH_CP_RB_DOORBELL_RANGE_LOWER] = (VGH_GFX_DOORBELL_INDEX << 2) & 0xFFCu;
        g_gpuRegs[VGH_CP_RB_DOORBELL_RANGE_UPPER] = 0xFFCu;
        PuckMfence();
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") doorbell mapped @ ");
        EcamPutHex(g_gpuDoorbellPhys + (VGH_GFX_DOORBELL_INDEX << 2), 10);
        SerialPuts(" (gfx ring0 index 0x116)\r\n");
    }
    else if (noDoorbell)
    {
        /* Attempt C: force DOORBELL_EN=0 -- pure MMIO wptr, no doorbell in the mix at all. */
        g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL] = (VGH_GFX_DOORBELL_INDEX << 2);
        PuckMfence();
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") DOORBELL_EN=0: pure MMIO CP_RB0_WPTR/_HI path\r\n");
    }
    else
        SerialPuts("[gpu] (e) WARNING: no doorbell BAR found; MMIO wptr fallback (gfx10 ignores it)\r\n");

    /* (g) CP_MAX_CONTEXT + CP_DEVICE_ID: the "init the CP" pair from cp_gfx_start, still while
     * halted, immediately before the un-halt. */
    g_gpuRegs[VGH_CP_MAX_CONTEXT] = 7u;      /* max_hw_contexts - 1 */
    g_gpuRegs[VGH_CP_DEVICE_ID] = 1u;
    PuckMfence();

    /* Latch the ring/doorbell setup for the parked screen: DB_PHYS!=0 => doorbell BAR found;
     * DB_CTRL==0x40000458 => doorbell armed; RB_CNTL/RB_BASE => ring registers took. */
    g_rtDbPhys = g_gpuDoorbellPhys;
    g_rtDbCtrl = g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL];
    g_rtRbCntl = g_gpuRegs[VGH_CP_RB0_CNTL];
    g_rtRbBase = g_gpuRegs[VGH_CP_RB0_BASE];
    g_rtAperEn = g_gpuRegs[VGH_RCC_DOORBELL_APER_EN];

    /* (h) UN-HALT: everything above is now fully programmed while the front ends could not fetch.
     * Clear CE/PFP/ME_HALT and poll CP_STAT for idle -- amdgpu's cp_gfx_enable does exactly this
     * poll after un-halting. Log the result either way and CONTINUE regardless: this line is itself
     * the key diagnostic distinguishing "CP idles cleanly with a real ring" from "still wedged". */
    g_gpuRegs[VGH_CP_ME_CNTL] = 0u;
    if (GpuWaitReg(VGH_CP_STAT, 0xFFFFFFFFu, 0, 100, "CP_STAT after un-halt") == 0)
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") CP_STAT after un-halt: ");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_STAT], 8); SerialPuts(" idle\r\n");
    }
    else
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") CP_STAT after un-halt: ");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_STAT], 8); SerialPuts(" still busy after 100ms\r\n");
        RingStallDump("post-unhalt");
    }

    /* (i) Prime SCRATCH_REG0 + the clear-state preamble (amdgpu cp_gfx_start) as the FIRST ring
     * packets: the CP won't fetch/execute a bare SET_UCONFIG_REG as its first-ever packet after
     * autoload -- it needs the PREAMBLE + CONTEXT_CONTROL prime to enter ring-processing. w = running
     * dword index. */
    g_gpuRegs[VGH_SCRATCH_REG0] = 0xCAFEDEADu;
    w = 0u;
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_PREAMBLE_CNTL, 0);
    ring[w++] = VGH_PM4_PREAMBLE_BEGIN;
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_CONTEXT_CONTROL, 1);
    ring[w++] = 0x80000000u;
    ring[w++] = 0x80000000u;
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_PREAMBLE_CNTL, 0);
    ring[w++] = VGH_PM4_PREAMBLE_END;
    /* Ring test (spec 5.4): SET_UCONFIG_REG writes 0xDEADBEEF to SCRATCH_REG0 (primed to 0xCAFEDEAD).
     * Operand = ABSOLUTE SOC15 index - uconfig start = 0xC040 - 0xC000 = 0x40 (SCRATCH is BASE_IDX 1). */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_SET_UCONFIG_REG, 1);
    ring[w++] = VGH_SCRATCH_REG0 - VGH_PM4_UCONFIG_START;
    ring[w++] = 0xDEADBEEFu;
    PuckMfence();
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;            /* spec 6: HDP flush before the CP reads the ring */

    RtKick(w);                                    /* ring the GFX doorbell (gfx10 live wptr path) */

    /* Latch the CP state for the parked health screen (the fail dump below scrolls off). */
    g_rtRan = 1;
    g_rtScratch = g_gpuRegs[VGH_SCRATCH_REG0];
    g_rtRptr = g_gpuRegs[VGH_CP_RB0_RPTR];
    g_rtWptr = g_gpuRegs[VGH_CP_RB0_WPTR];
    g_rtCpStat = g_gpuRegs[VGH_CP_STAT];
    g_rtActive = g_gpuRegs[VGH_CP_RB_ACTIVE];
    g_rtMeCntl = g_gpuRegs[VGH_CP_ME_CNTL];

    /* (j) Poll SCRATCH_REG0 for 0xDEADBEEF, 1 s deadline. */
    if (GpuWaitReg(VGH_SCRATCH_REG0, 0xFFFFFFFFu, 0xDEADBEEFu, 1000, "SCRATCH_REG0 ring exec") != 0)
    {
        got = g_gpuRegs[VGH_SCRATCH_REG0];
        g_rtScratch = got; g_rtRptr = g_gpuRegs[VGH_CP_RB0_RPTR]; g_rtCpStat = g_gpuRegs[VGH_CP_STAT];
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") ring test FAIL: SCRATCH=");
        EcamPutHex((unsigned long long)got, 8); SerialPuts("\r\n");
        RingDiagDump(cpuBase);
        return 0;
    }
    g_rtScratch = g_gpuRegs[VGH_SCRATCH_REG0]; g_rtRptr = g_gpuRegs[VGH_CP_RB0_RPTR];
    g_rtRingW = w;                            /* RingIbFence continues from here -- pointers run on */
    SerialPuts("[gpu] (e) RING TEST PASS ("); SerialPuts(label);
    SerialPuts("): CP executed SET_UCONFIG_REG (SCRATCH_REG0=0xdeadbeef)\r\n");
    return 1;
}

/* One ring-test attempt through the GFX HQD/MQD interface instead of legacy CP_RB0_* -- the flow
 * modern gfx10.3 firmware actually expects (see the VGH_CP_GFX_HQD_* comment above): build an MQD
 * IMAGE in memory (struct v10_gfx_mqd layout, MIT-licensed amdgpu/include/v10_structs.h; only the
 * fields gfx_v10_0_gfx_mqd_init sets are filled, rest zeroed), THEN commit it to the live CP_GFX_HQD_*
 * register file via direct MMIO in the exact order amdgpu's own #ifdef BRING_UP_DEBUG helper
 * (gfx_v10_0_gfx_queue_init_register, v5.10) uses -- no KIQ MAP_QUEUES anywhere in this path. Same
 * halt->program->un-halt->prime->kick discipline and TSC-deadlined polling as RingAttempt (never
 * hang, never reset). `label` is "hqd-ce"/"hqd-ce-vram" (the one surviving attempt, boot 6 onward).
 * `unhaltValue`: the value CP_ME_CNTL is un-halted TO, instead of a hardcoded 0 -- always 0 (full
 * un-halt, CE included) since boot 6 proved the CE must run on gfx10.3 (boot-5's CE-halted "hqd-noce"
 * experiment wedged the PFP at a new PC and is retired); kept as a parameter for any future experiment
 * that wants to hold a front end back.
 *
 * Boot 10 (2026-07-04) full-CSB pivot: the minimal-preamble era is over. Boots 8a/8b/9 proved
 * everything STRUCTURAL is amdgpu-faithful (registers, ordering, KIQ, CE_PARTITION_BASE=3) -- the
 * remaining delta was CONTENT: amdgpu's recorded preamble is the FULL clear-state block (gfx10_cs_data,
 * hundreds of SET_CONTEXT_REG dwords), ours was an empty stub. `emitCePartitionTail` is GONE (dropped
 * outright, both call sites updated): the full stream below is now unconditional and always includes
 * the CE_PARTITION SET_BASE tail as part of it -- an empty recorded preamble + CLEAR_STATE + live CE is
 * a combination no real driver ever runs, so there is no longer a "no full CSB" variant worth trying.
 * Exact packet order (gfx_v10_0_cp_gfx_start, scratchpad gfx_v10_0.c line 5951, behavior-only reference
 * -- opcodes/offsets are all from vendored MIT headers, the CSB data table itself is now vendored
 * verbatim, MIT, see the clearstate_gfx10.h include above):
 *   (a) PREAMBLE_CNTL(0) / PREAMBLE_BEGIN
 *   (b) CONTEXT_CONTROL(1) / 0x80000000 / 0x80000000
 *   (c) for each gfx10_cs_data section with id==SECT_CONTEXT, for each extent: SET_CONTEXT_REG(reg_count)
 *       / (reg_index - SET_CONTEXT_REG_START) / the reg_count extent values
 *   (d) SET_CONTEXT_REG(1) / (mmPA_SC_TILE_STEERING_OVERRIDE_abs - SET_CONTEXT_REG_START) / live readback
 *   (e) PREAMBLE_CNTL(0) / PREAMBLE_END
 *   (f) CLEAR_STATE(0) / 0
 *   (g) SET_BASE(2) / CE_PARTITION(3) / 0x8000 / 0x8000
 *   (h) the existing SET_UCONFIG_REG + WRITE_DATA test packets, unchanged
 * Every ring-test attempt also appends a WRITE_DATA-to-memory packet after SET_UCONFIG_REG and does a
 * two-stage reg/mem poll -- see the "(j)" comment below. Returns 1 on the memory-write PASS (mem=Y; see
 * (j)); reg=Y/N is logged regardless. */
static int RingAttemptHqd(const char *label, volatile unsigned char *cpuBase, unsigned long long mcBase,
                           unsigned int unhaltValue)
{
    unsigned long long rbMc, rptrMc, wptrMc, mqdMc, scratchMc;
    volatile unsigned int *ring, *ib, *mqd;
    unsigned int bufsz, blksz, w, got, mqdControl, quantum, i;
    unsigned int steeringAbs, ceReg;
    const struct cs_section_def *sect;
    const struct cs_extent_def *ext;
    int regPass, memPass;

    g_rtCurCpu = cpuBase;
    g_rtCurMc = mcBase;
    g_rtKickMode = VGH_RT_KICK_HQD;
    g_rtKickNoDoorbell = 0;

    /* (a) Halt the CP front ends and drop CP_GFX_HQD_ACTIVE before touching anything else -- the HQD
     * analogue of RingAttempt's step (a); amdgpu's #ifdef BRING_UP_DEBUG path runs under nv_grbm_select
     * (still ME0/PIPE0/QUEUE0 -- see (c) below) with the queue inactive while it is (re)programmed. */
    g_gpuRegs[VGH_CP_ME_CNTL] = VGH_CP_ME_CNTL_ALL_HALT;
    PuckMfence();
    GpuDelayMs(1);
    g_gpuRegs[VGH_CP_GFX_HQD_ACTIVE] = 0u;

    /* Buffer addresses, relative to THIS attempt's base. */
    rbMc      = RtVa(VGH_RT_RING_OFF);
    rptrMc    = RtVa(VGH_RT_RPTR_OFF);
    wptrMc    = RtVa(VGH_RT_WPTR_OFF);
    mqdMc     = RtVa(VGH_RT_MQD_OFF);
    scratchMc = RtVa(VGH_RT_SCRATCH_OFF);
    ring      = RtCpu(VGH_RT_RING_OFF);
    ib        = RtCpu(VGH_RT_IB_OFF);
    mqd       = RtCpu(VGH_RT_MQD_OFF);

    /* (b) Zero the working set (ring, IB, MQD page) + the singleton dwords for THIS attempt's base. */
    {
        for (i = 0; i < VGH_RT_RING_BYTES / 4u; i++) ring[i] = 0;
        for (i = 0; i < 0x1000u / 4u; i++) ib[i] = 0;
        for (i = 0; i < 0x1000u / 4u; i++) mqd[i] = 0;
        *RtCpu(VGH_RT_RPTR_OFF) = 0;
        *RtCpu(VGH_RT_WPTR_OFF) = 0;
        *RtCpu(VGH_RT_FENCE_OFF) = 0;
        *RtCpu(VGH_RT_SCRATCH_OFF) = 0;
        PuckMfence();
    }

    /* Build the MQD IMAGE (gfx_v10_0_gfx_mqd_init, v6.6): exactly the fields it sets, RMW-ing the
     * two RMW'd control registers off their LIVE values (mqd_control / quantum) the same way amdgpu
     * does (RREG32_SOC15 then REG_SET_FIELD), so any reset-default bits we don't know about survive. */
    bufsz = RtLog2((unsigned long long)VGH_RT_RING_BYTES / 8ull); /* log2(8192/8)=log2(1024)=10 (boot 10:
        ring grew 4 KiB->8 KiB); amdgpu's HQD path computes rb_bufsz = order_base_2(queue_size/4)-1 =
        order_base_2(2048)-1 = 11-1 = 10 -- the same value by construction (queue_size/4 == ring_bytes/8
        dwords when queue_size == ring_bytes), so RtLog2 here is equivalent to amdgpu's
        order_base_2()-1 formula, just phrased differently. */
    blksz = bufsz - 2u;

    mqdControl = g_gpuRegs[VGH_CP_GFX_MQD_CONTROL];
    mqdControl &= ~(VGH_CP_GFX_MQD_CONTROL_VMID_MASK | VGH_CP_GFX_MQD_CONTROL_PRIV_STATE_MASK
                  | VGH_CP_GFX_MQD_CONTROL_CACHE_POLICY_MASK);
    mqdControl |= VGH_CP_GFX_MQD_CONTROL_PRIV_STATE_MASK; /* VMID=0, PRIV_STATE=1, CACHE_POLICY=0 */

    quantum = g_gpuRegs[VGH_CP_GFX_HQD_QUANTUM];
    quantum |= VGH_CP_GFX_HQD_QUANTUM_EN_MASK; /* QUANTUM_EN=1; QUANTUM_SCALE/DURATION left as read */

    mqd[VGH_MQD_DW_CP_GFX_HQD_WPTR] = 0u;
    mqd[VGH_MQD_DW_CP_GFX_HQD_WPTR_HI] = 0u;
    mqd[VGH_MQD_DW_CP_MQD_BASE_ADDR] = (unsigned int)(mqdMc & 0xFFFFFFFCu);
    mqd[VGH_MQD_DW_CP_MQD_BASE_ADDR_HI] = (unsigned int)(mqdMc >> 32);
    mqd[VGH_MQD_DW_CP_GFX_MQD_CONTROL] = mqdControl;
    mqd[VGH_MQD_DW_CP_GFX_HQD_VMID] = 0u;
    mqd[VGH_MQD_DW_CP_GFX_HQD_QUEUE_PRIORITY] = 0u;
    mqd[VGH_MQD_DW_CP_GFX_HQD_QUANTUM] = quantum;
    mqd[VGH_MQD_DW_CP_GFX_HQD_BASE] = (unsigned int)((rbMc >> 8) & 0xFFFFFFFFu);
    mqd[VGH_MQD_DW_CP_GFX_HQD_BASE_HI] = (unsigned int)(rbMc >> 40);
    mqd[VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR] = (unsigned int)(rptrMc & 0xFFFFFFFCu);
    mqd[VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR_HI] = (unsigned int)((rptrMc >> 32) & 0xFFFFu);
    mqd[VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_LO] = (unsigned int)(wptrMc & 0xFFFFFFFCu);
    mqd[VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_HI] = (unsigned int)((wptrMc >> 32) & 0xFFFFu);
    mqd[VGH_MQD_DW_CP_GFX_HQD_CNTL] = bufsz | (blksz << 8);
    mqd[VGH_MQD_DW_CP_RB_DOORBELL_CONTROL] = (VGH_GFX_DOORBELL_INDEX << 2) | VGH_CP_DOORBELL_EN;
    mqd[VGH_MQD_DW_CP_GFX_HQD_RPTR] = g_gpuRegs[VGH_CP_GFX_HQD_RPTR]; /* snapshot of the live register */
    mqd[VGH_MQD_DW_CP_GFX_HQD_ACTIVE] = 1u;
    PuckMfence();

    /* (c) Pipe select (ME0/PIPE0), same as the legacy path -- amdgpu's nv_grbm_select wraps the whole
     * mqd_init + queue_init_register pair under one pipe/queue selection; we do the same, still while
     * the front ends are halted and the queue inactive. */
    g_gpuRegs[VGH_GRBM_GFX_CNTL] = 0u;

    /* Commit the MQD image to the LIVE CP_GFX_HQD_* registers, in the v5.10 BRING_UP_DEBUG
     * gfx_v10_0_gfx_queue_init_register order (behavior-only reference -- register offsets are all
     * from the vendored gc_10_3_0_offset.h, not copied from the GPL source). */
    g_gpuRegs[VGH_CP_GFX_HQD_WPTR] = mqd[VGH_MQD_DW_CP_GFX_HQD_WPTR];
    g_gpuRegs[VGH_CP_GFX_HQD_WPTR_HI] = mqd[VGH_MQD_DW_CP_GFX_HQD_WPTR_HI];
    g_gpuRegs[VGH_CP_MQD_BASE_ADDR] = mqd[VGH_MQD_DW_CP_MQD_BASE_ADDR];
    g_gpuRegs[VGH_CP_MQD_BASE_ADDR_HI] = mqd[VGH_MQD_DW_CP_MQD_BASE_ADDR_HI];
    g_gpuRegs[VGH_CP_GFX_MQD_CONTROL] = mqd[VGH_MQD_DW_CP_GFX_MQD_CONTROL];
    g_gpuRegs[VGH_CP_GFX_HQD_VMID] = mqd[VGH_MQD_DW_CP_GFX_HQD_VMID];
    g_gpuRegs[VGH_CP_GFX_HQD_QUEUE_PRIORITY] = mqd[VGH_MQD_DW_CP_GFX_HQD_QUEUE_PRIORITY];
    g_gpuRegs[VGH_CP_GFX_HQD_QUANTUM] = mqd[VGH_MQD_DW_CP_GFX_HQD_QUANTUM];
    g_gpuRegs[VGH_CP_GFX_HQD_BASE] = mqd[VGH_MQD_DW_CP_GFX_HQD_BASE];
    g_gpuRegs[VGH_CP_GFX_HQD_BASE_HI] = mqd[VGH_MQD_DW_CP_GFX_HQD_BASE_HI];
    g_gpuRegs[VGH_CP_GFX_HQD_RPTR_ADDR] = mqd[VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR];
    g_gpuRegs[VGH_CP_GFX_HQD_RPTR_ADDR_HI] = mqd[VGH_MQD_DW_CP_GFX_HQD_RPTR_ADDR_HI];
    g_gpuRegs[VGH_CP_GFX_HQD_CNTL] = mqd[VGH_MQD_DW_CP_GFX_HQD_CNTL];
    g_gpuRegs[VGH_CP_RB_WPTR_POLL_ADDR_LO] = mqd[VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_LO];
    g_gpuRegs[VGH_CP_RB_WPTR_POLL_ADDR_HI] = mqd[VGH_MQD_DW_CP_RB_WPTR_POLL_ADDR_HI];
    g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL] = mqd[VGH_MQD_DW_CP_RB_DOORBELL_CONTROL];
    g_gpuRegs[VGH_CP_GFX_HQD_ACTIVE] = mqd[VGH_MQD_DW_CP_GFX_HQD_ACTIVE];
    PuckMfence();

    /* (d) Doorbell aperture/CONTROL/RANGE + NBIO enable (idempotent across attempts, same as
     * RingAttempt), then CP_MAX_CONTEXT/CP_DEVICE_ID, then un-halt + poll. */
    if (g_gpuDoorbellPhys)
    {
        g_gpuRegs[VGH_RCC_DOORBELL_APER_EN] = g_gpuRegs[VGH_RCC_DOORBELL_APER_EN] | 0x1u;
        if (!g_gpuDoorbell)
            g_gpuDoorbell = (volatile unsigned char *)PuckMapMmio(g_gpuDoorbellPhys, 0x1000ull, PUCK_MT_UC);
        g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL] = (VGH_GFX_DOORBELL_INDEX << 2) | VGH_CP_DOORBELL_EN;
        g_gpuRegs[VGH_CP_RB_DOORBELL_RANGE_LOWER] = (VGH_GFX_DOORBELL_INDEX << 2) & 0xFFCu;
        g_gpuRegs[VGH_CP_RB_DOORBELL_RANGE_UPPER] = 0xFFCu;
        PuckMfence();
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") doorbell mapped @ ");
        EcamPutHex(g_gpuDoorbellPhys + (VGH_GFX_DOORBELL_INDEX << 2), 10);
        SerialPuts(" (gfx ring0 index 0x116, HQD path)\r\n");
    }
    else
        SerialPuts("[gpu] (e) WARNING: no doorbell BAR found; MMIO wptr fallback (gfx10 ignores it)\r\n");

    g_gpuRegs[VGH_CP_MAX_CONTEXT] = 7u;
    g_gpuRegs[VGH_CP_DEVICE_ID] = 1u;
    PuckMfence();

    g_rtDbPhys = g_gpuDoorbellPhys;
    g_rtDbCtrl = g_gpuRegs[VGH_CP_RB_DOORBELL_CONTROL];
    g_rtRbCntl = g_gpuRegs[VGH_CP_GFX_HQD_CNTL];
    g_rtRbBase = g_gpuRegs[VGH_CP_GFX_HQD_BASE];
    g_rtAperEn = g_gpuRegs[VGH_RCC_DOORBELL_APER_EN];

    /* UN-HALT (to `unhaltValue`, NOT hardcoded 0 -- boot-5 B1 "hqd-noce" passes CE_HALT_MASK to leave
     * the Constant Engine halted while ME/PFP run; B2 "hqd-cepart" passes 0, full un-halt) + poll
     * CP_STAT for idle -- identical diagnostic discipline to RingAttempt otherwise. */
    g_gpuRegs[VGH_CP_ME_CNTL] = unhaltValue;
    if (GpuWaitReg(VGH_CP_STAT, 0xFFFFFFFFu, 0, 100, "CP_STAT after un-halt") == 0)
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") CP_STAT after un-halt: ");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_STAT], 8); SerialPuts(" idle\r\n");
    }
    else
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") CP_STAT after un-halt: ");
        EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_STAT], 8); SerialPuts(" still busy after 100ms\r\n");
        RingStallDump("post-unhalt");
    }

    /* Prime SCRATCH_REG0, then emit the FULL cp_gfx_start stream (boot 10 full-CSB pivot -- see the
     * function-header comment for the packet-order citation), then the existing SET_UCONFIG_REG +
     * WRITE_DATA-to-memory test packets, targeting the SAME scratch dword's MC address -- proves
     * execution independent of whether SET_UCONFIG_REG's register-write path works (see the two-stage
     * poll below). */
    g_gpuRegs[VGH_SCRATCH_REG0] = 0xCAFEDEADu;
    w = 0u;
    /* (a) */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_PREAMBLE_CNTL, 0);
    ring[w++] = VGH_PM4_PREAMBLE_BEGIN;
    /* (b) */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_CONTEXT_CONTROL, 1);
    ring[w++] = 0x80000000u;
    ring[w++] = 0x80000000u;
    /* (c) the vendored gfx10_cs_data SECT_CONTEXT blocks, verbatim (clearstate_gfx10.h, MIT) --
     * gfx_v10_0_get_csb_buffer/_cp_gfx_start's own emission loop, transcribed exactly. */
    for (sect = gfx10_cs_data; sect->section != 0; ++sect)
    {
        if (sect->id != SECT_CONTEXT) continue;
        for (ext = sect->section; ext->extent != 0; ++ext)
        {
            ring[w++] = VGH_PM4_TYPE3(VGH_PM4_SET_CONTEXT_REG, ext->reg_count);
            ring[w++] = ext->reg_index - VGH_PM4_SET_CONTEXT_REG_START;
            for (i = 0; i < ext->reg_count; i++)
                ring[w++] = ext->extent[i];
        }
    }
    /* (d) PA_SC_TILE_STEERING_OVERRIDE: not part of the static gfx10_cs_data table (amdgpu fills it at
     * submit time from adev->gfx.config.pa_sc_tile_steering_override, a value computed from live SE/SA/
     * RB topology, not a recorded constant) -- read the live register instead. The packet operand is
     * the absolute SOC15 index (VGH_PA_SC_TILE_STEERING_OVERRIDE = VGH_GC_SEG1 + 0x00d7, exactly like
     * every other VGH_* register define in this file) minus SET_CONTEXT_REG_START, the same
     * base-then-subtract-the-packet-start derivation VGH_SCRATCH_REG0's SET_UCONFIG_REG operand already
     * uses against VGH_PM4_UCONFIG_START for its own packet family. */
    steeringAbs = VGH_PA_SC_TILE_STEERING_OVERRIDE;
    ceReg = g_gpuRegs[VGH_PA_SC_TILE_STEERING_OVERRIDE];
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_SET_CONTEXT_REG, 1);
    ring[w++] = steeringAbs - VGH_PM4_SET_CONTEXT_REG_START;
    ring[w++] = ceReg;
    /* (e) */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_PREAMBLE_CNTL, 0);
    ring[w++] = VGH_PM4_PREAMBLE_END;
    /* (f) CLEAR_STATE(0) -- replays the recorded PREAMBLE BEGIN..END block (now the full CSB, not an
     * empty stub) and initializes the context/CE state machinery (boot 8b: without it the first DE
     * INDIRECT_BUFFER deadlocks -- PFP never launches the IB fetch, CE waits on the DE counter). */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_CLEAR_STATE, 0);
    ring[w++] = 0u;
    /* (g) SET_BASE CE_PARTITION (nvd.h-verified opcode/index, boot 7's CE_PARTITION_BASE=3 fix). */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_SET_BASE, 2);
    ring[w++] = VGH_PM4_SET_BASE_CE_PARTITION;
    ring[w++] = 0x00008000u;
    ring[w++] = 0x00008000u;

    /* (h) the existing SET_UCONFIG_REG + WRITE_DATA test packets, unchanged. */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_SET_UCONFIG_REG, 1);
    ring[w++] = VGH_SCRATCH_REG0 - VGH_PM4_UCONFIG_START;
    ring[w++] = 0xDEADBEEFu;
    /* WRITE_DATA -> memory (same UNVERIFIED control-dword encoding RingIbFence's IB already uses:
     * DST_SEL(5)<<8 | WR_CONFIRM(bit20)), targeting VGH_RT_SCRATCH_OFF's MC address. Boot 6: the write
     * retired (WR_CONFIRM honored, CP idle) but the CPU read zeros -- GL2 residency; CACHE_POLICY[26:25]
     * = 3 (BYPASS, nvd.h-VERIFIED WRITE_DATA_CACHE_POLICY(x)=(x)<<25) forces the write to memory. */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_WRITE_DATA, 3);
    ring[w++] = (5u << 8) | (1u << 20) | (3u << 25); /* nvd.h-VERIFIED: WRITE_DATA_DST_SEL(5) | WR_CONFIRM
                                                      * | WRITE_DATA_CACHE_POLICY(3=bypass) */
    ring[w++] = (unsigned int)(scratchMc & 0xFFFFFFFFu);
    ring[w++] = (unsigned int)(scratchMc >> 32);
    ring[w++] = 0x600DF00Du;

    /* Headroom guard: gfx10's recorded CSB is ~953 dwords (937 SECT_CONTEXT + 16 fixed packets, this
     * build's computed total) + 8 test-packet dwords = 961, comfortably under the 2048-dword ring; this
     * check exists so a future header update (or a bug in the emission loop above) fails loudly instead
     * of silently overrunning the ring into the rptr/wptr/fence/scratch dwords that follow it. Headroom
     * of 16 dwords covers IB/fence-stage bookkeeping (RingIbFence continues writing from g_rtRingW). */
    SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") preamble+test stream = ");
    EcamPutHex((unsigned long long)w, 4); SerialPuts(" dwords\r\n");
    if (w > (VGH_RT_RING_BYTES / 4u) - 16u)
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label);
        SerialPuts(") ERROR: preamble+test stream overflows the ring (would need ");
        EcamPutHex((unsigned long long)w, 4);
        SerialPuts(" dwords, capacity-headroom is ");
        EcamPutHex((unsigned long long)((VGH_RT_RING_BYTES / 4u) - 16u), 4);
        SerialPuts(") -- aborting this attempt\r\n");
        return 0;
    }

    PuckMfence();
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;

    /* PRE-kick ring readback (boot-8: a cold boot read back ALL ZEROS post-kick from the very memory
     * the CPU had just written -- this split decides whether the packets ever reached DRAM (pre=zeros
     * -> CPU write path/mapping broken) or were destroyed afterwards (pre=packets, post=zeros -> a GPU
     * agent, prime suspect the CE partition dump, overwrote them). Boot 10: with a stream now hundreds
     * of dwords long, both the head (preamble) and the tail (the actual test packets) are logged, so a
     * scribble confined to one end of the ring isn't missed by only ever checking dword 0. */
    SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") RING-pre[0..4]=");
    for (i = 0; i < 5u; i++) { EcamPutHex((unsigned long long)ring[i], 8); SerialPuts(" "); }
    SerialPuts("\r\n[gpu] (e:"); SerialPuts(label); SerialPuts(") RING-pre-tail[");
    EcamPutHex((unsigned long long)(w - 10u), 4); SerialPuts("..");
    EcamPutHex((unsigned long long)(w - 1u), 4); SerialPuts("]=");
    for (i = w - 10u; i < w; i++) { EcamPutHex((unsigned long long)ring[i], 8); SerialPuts(" "); }
    SerialPuts("\r\n");

    RtKick(w); /* HQD mode: writes CP_GFX_HQD_WPTR/_HI, then the doorbell */

    g_rtRan = 1;
    g_rtScratch = g_gpuRegs[VGH_SCRATCH_REG0];
    g_rtRptr = g_gpuRegs[VGH_CP_GFX_HQD_RPTR];
    g_rtWptr = g_gpuRegs[VGH_CP_GFX_HQD_WPTR];
    g_rtCpStat = g_gpuRegs[VGH_CP_STAT];
    g_rtActive = g_gpuRegs[VGH_CP_GFX_HQD_ACTIVE];
    g_rtMeCntl = g_gpuRegs[VGH_CP_ME_CNTL];

    /* Post-kick diagnostics (boot-5): CPU-read the first 10 ring dwords back (HDP flush first) --
     * rules out ring-memory corruption / wrong-page-fetch theories independent of whether the CP
     * executed anything. */
    g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0; PuckMfence();
    SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") RING-post[0..4]=");
    for (i = 0; i < 5u; i++) { EcamPutHex((unsigned long long)ring[i], 8); SerialPuts(" "); }
    SerialPuts("\r\n[gpu] (e:"); SerialPuts(label); SerialPuts(") RING-post[5..9]=");
    for (i = 5; i < 10u; i++) { EcamPutHex((unsigned long long)ring[i], 8); SerialPuts(" "); }
    SerialPuts("\r\n");

    /* Live CPU write/read probe (boot-8): prove the CPU's mapping of this block works AT THIS MOMENT
     * by writing a magic to an unused ring tail dword and reading it straight back. probe=N means the
     * CPU-side mapping/claim of the block is broken on this boot (memory-map nondeterminism), not
     * anything GPU-side. */
    ring[255] = 0x50524F42u; PuckMfence();
    SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") CPU-probe=");
    SerialPuts(ring[255] == 0x50524F42u ? "Y" : "N");
    SerialPuts(" block cpu="); EcamPutHex((unsigned long long)cpuBase, 10);
    SerialPuts(" mc="); EcamPutHex(mcBase, 10); SerialPuts("\r\n");

    /* (j) Two-stage poll verdict (boot-5): SCRATCH_REG0 (the SET_UCONFIG_REG register path) and the
     * memory dword (the WRITE_DATA path) are polled INDEPENDENTLY -- a broken SET_UCONFIG_REG path must
     * not mask a working memory path, and vice versa. PASS for this attempt is EITHER landing (boot 6
     * proved reg=Y/mem=N: the CP executed the whole stream -- our headers in the PFP/ME FIFOs, RB0_RPTR
     * fully advanced, CP_STAT idle -- while the memory write stayed CPU-invisible, most plausibly GL2
     * cache residency; the WRITE_DATA now carries CACHE_POLICY=BYPASS for that). reg/mem disagreeing is
     * still logged explicitly: it localizes which write-back path needs work. */
    regPass = (GpuWaitReg(VGH_SCRATCH_REG0, 0xFFFFFFFFu, 0xDEADBEEFu, 1000, "SCRATCH_REG0 ring exec") == 0);
    g_rtScratch = g_gpuRegs[VGH_SCRATCH_REG0]; g_rtRptr = g_gpuRegs[VGH_CP_GFX_HQD_RPTR];
    g_rtCpStat = g_gpuRegs[VGH_CP_STAT];

    {
        unsigned long long end = __rdtsc() + (g_tscFreqHz / 1000ull) * 1000ull; /* 1 s, independent of regPass */
        unsigned int mv = 0;
        for (;;)
        {
            g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;
            PuckMfence();
            mv = *RtCpu(VGH_RT_SCRATCH_OFF);
            if (mv == 0x600DF00Du) { memPass = 1; break; }
            if (__rdtsc() > end) { memPass = 0; break; }
        }
        got = mv;
    }

    g_rtRegPass = regPass;
    g_rtMemPass = memPass;

    SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") verdict: reg=");
    SerialPuts(regPass ? "Y" : "N"); SerialPuts(" mem="); SerialPuts(memPass ? "Y" : "N");
    SerialPuts(" (SCRATCH_REG0="); EcamPutHex((unsigned long long)g_rtScratch, 8);
    SerialPuts(" mem_dword="); EcamPutHex((unsigned long long)got, 8); SerialPuts(")\r\n");
    if (regPass != memPass)
        SerialPuts(memPass
            ? "[gpu] (e) NOTE: reg/mem DISAGREE -- memory write executed, SET_UCONFIG_REG path needs fixing\r\n"
            : "[gpu] (e) NOTE: reg/mem DISAGREE -- register write executed, WRITE_DATA/memory path needs fixing\r\n");

    if (!regPass && !memPass)
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") ring test FAIL: mem dword=");
        EcamPutHex((unsigned long long)got, 8); SerialPuts("\r\n");
        RingDiagDump(cpuBase);
        return 0;
    }
    g_rtRingW = w;
    SerialPuts("[gpu] (e) RING TEST PASS ("); SerialPuts(label);
    SerialPuts(memPass ? "): CP executed (mem write landed)\r\n"
                       : "): CP executed (SET_UCONFIG_REG landed; mem write pending GL2 visibility)\r\n");
    return 1;
}

/* IB + fence stage (spec 5.5), run once after a passing attempt, against that attempt's base
 * (g_rtCurCpu/g_rtCurMc, still set from RingAttempt). IB: WRITE_DATA(dst_sel=5 memory, WR_CONFIRM)
 * -> the scratch target. Ring: INDIRECT_BUFFER -> the IB, then RELEASE_MEM -> the fence dword
 * (DATA_SEL=1 32-bit, INT_SEL=0 no irq / poll). Bit encodings are from spec 5.5 (nvd.h not vendored
 * -> UNVERIFIED). Returns 1 on fence + WRITE_DATA landing, 0 on either failing. */
static int RingIbFence(const char *label, volatile unsigned char *cpuBase)
{
    unsigned long long ibMc, fenceMc, scratchMc;
    volatile unsigned int *ring, *ib;
    unsigned int w, seq, got, ibLen;

    ibMc      = RtVa(VGH_RT_IB_OFF);
    fenceMc   = RtVa(VGH_RT_FENCE_OFF);
    scratchMc = RtVa(VGH_RT_SCRATCH_OFF);
    ring      = RtCpu(VGH_RT_RING_OFF);
    ib        = RtCpu(VGH_RT_IB_OFF);

    /* IB body. WRITE_DATA control: DST_SEL(5)<<8 (memory-async) | WR_CONFIRM(bit20) | CACHE_POLICY
     * BYPASS (boot 6: LRU-policy writes retire into GL2 and stay CPU-invisible). nvd.h-VERIFIED
     * encoding (nvd.h vendored 2026-07-04). */
    ib[0] = VGH_PM4_TYPE3(VGH_PM4_WRITE_DATA, 3);
    ib[1] = (5u << 8) | (1u << 20) | (3u << 25); /* nvd.h-VERIFIED: WRITE_DATA_DST_SEL(5) | WR_CONFIRM
                                                  * | WRITE_DATA_CACHE_POLICY(3=bypass) */
    ib[2] = (unsigned int)(scratchMc & 0xFFFFFFFFu);
    ib[3] = (unsigned int)(scratchMc >> 32);
    ib[4] = 0xDEADBEEFu;
    ibLen = 5u;                               /* IB length in dwords */
    PuckMfence();

    /* Ring: INDIRECT_BUFFER (op 0x3F, count 2) then RELEASE_MEM (op 0x49, count 6). The ring's
     * rptr/wptr are CUMULATIVE -- after the PASS they both sit at g_rtRingW, so the new packets must
     * be written there and the doorbell advanced past them. Restarting at 0 would leave the CP
     * fetching [old wptr, new wptr), i.e. the middle dwords of the rewritten packets, as headers. */
    w = g_rtRingW;
    seq = 0xF00D0001u;                        /* fence sequence value to poll for */
    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_INDIRECT_BUFFER, 2);
    ring[w++] = (unsigned int)(ibMc & 0xFFFFFFFCu);   /* dword-aligned IB GPU-VA lo */
    ring[w++] = (unsigned int)(ibMc >> 32);
    ring[w++] = ibLen | (0u << 24);           /* control: ib_length_dw | (vmid=0 << 24) */

    ring[w++] = VGH_PM4_TYPE3(VGH_PM4_RELEASE_MEM, 6);
    /* dw1: GCR_SEQ|GCR_GL2_WB|GCR_GLM_INV|GCR_GLM_WB | CACHE_POLICY(3)
     *      | EVENT_TYPE(CACHE_FLUSH_AND_INV_TS_EVENT=0x14) | EVENT_INDEX(5). Field positions
     *      nvd.h-VERIFIED (GLM_WB=12,GLM_INV=13,GL2_WB=21,SEQ=22,CACHE_POLICY<<25,EVENT_INDEX<<8);
     *      the 0x14 event value is per amdgpu gfx_v10_0_ring_emit_fence (behavior). */
    ring[w++] = (1u << 22) | (1u << 21) | (1u << 13) | (1u << 12) /* GCR_SEQ|GL2_WB|GLM_INV|GLM_WB */
            | (3u << 25)                                        /* CACHE_POLICY(3) */
            | (0x14u << 0)                                      /* EVENT_TYPE = CACHE_FLUSH_AND_INV_TS */
            | (5u << 8);                                        /* EVENT_INDEX(5) */
    ring[w++] = (1u << 29) | (0u << 24);      /* dw2: DATA_SEL(1)<<29 32-bit | INT_SEL(0)<<24 poll
                                               * (nvd.h-VERIFIED field positions) */
    ring[w++] = (unsigned int)(fenceMc & 0xFFFFFFFFu); /* dw3: fence addr lo */
    ring[w++] = (unsigned int)(fenceMc >> 32);         /* dw4: fence addr hi */
    ring[w++] = seq;                          /* dw5: seq lo */
    ring[w++] = 0u;                           /* dw6: seq hi (32-bit fence) */
    ring[w++] = 0u;                           /* dw7: padding (count 6 -> body dw1..dw7 = 7 dwords) */
    PuckMfence();

    RtKick(w);                                /* ring the doorbell for the IB + fence packets */

    /* Poll the fence dword (HDP flush/invalidate between reads so the CPU sees the GPU write; spec 6.
     * The NBIO HDP flush register doubles as invalidate here -- there is no separate remap constant
     * vendored, and it is the same physical HDP path stage (c)/(d) used to read PSP fences). */
    {
        unsigned long long end = __rdtsc() + (g_tscFreqHz / 1000ull) * 1000ull; /* 1 s */
        unsigned int fv = 0;
        for (;;)
        {
            g_gpuRegs[VGH_HDP_MEM_FLUSH] = 0;
            PuckMfence();
            fv = *RtCpu(VGH_RT_FENCE_OFF);
            if (fv == seq) break;
            if (__rdtsc() > end)
            {
                SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") IB/FENCE FAIL: fence=");
                EcamPutHex((unsigned long long)fv, 8);
                SerialPuts(" want="); EcamPutHex((unsigned long long)seq, 8); SerialPuts("\r\n");
                RingDiagDump(cpuBase);
                return 0;
            }
        }
    }

    /* Fence signalled -> read back the IB's WRITE_DATA target (HDP flush already current above). */
    got = *RtCpu(VGH_RT_SCRATCH_OFF);
    if (got != 0xDEADBEEFu)
    {
        SerialPuts("[gpu] (e:"); SerialPuts(label); SerialPuts(") IB target MISMATCH: ");
        EcamPutHex((unsigned long long)got, 8); SerialPuts(" (fence signalled but WRITE_DATA did not land)\r\n");
        return 0;
    }

    SerialPuts("[gpu] (e:"); SerialPuts(label);
    SerialPuts(") MILESTONE: FIRST SILICON EXECUTES -- CP ran IB, RELEASE_MEM fence signalled\r\n");
    GpuPutReg("CP_RB0_RPTR ", VGH_CP_RB0_RPTR);
    GpuPutReg("CP_STAT     ", VGH_CP_STAT);
    return 1;
}

/* Boot-4 (2026-07-03) stage (e) constants+golden init: gfx_v10_0_constants_init (GRBM_CNTL read
 * timeout, per-VMID SH_MEM_CONFIG/SH_MEM_BASES, GDS VMID base/size) plus golden_settings_gc_10_3_vangogh
 * (masked RMW). Called once, at the very start of PuckGpuRingTest, before any ring attempt -- neither
 * amdgpu_async_gfx_ring nor the RLC handshake depend on these registers being programmed before
 * autoload, but amdgpu always runs constants_init this early in its own gfx_v10_0_hw_init, so Puck now
 * matches that ordering rather than omitting it. Never touches CP_ME_CNTL/CP_MEC_CNTL halt state. */
static void PuckGpuConstantsInit(void)
{
    unsigned int tmp, vmid;

    SerialPuts("[gpu] (e) constants+golden init\r\n");

    /* GRBM_CNTL.READ_TIMEOUT = 0xff (mmGRBM_CNTL 0x0da0; gc_10_3_0_sh_mask.h
     * GRBM_CNTL__READ_TIMEOUT_MASK = 0x000000FF, shift 0). */
    tmp = g_gpuRegs[VGH_GRBM_CNTL];
    tmp = (tmp & ~VGH_GRBM_CNTL_READ_TIMEOUT_MASK)
        | ((0xffu << VGH_GRBM_CNTL_READ_TIMEOUT_SHIFT) & VGH_GRBM_CNTL_READ_TIMEOUT_MASK);
    g_gpuRegs[VGH_GRBM_CNTL] = tmp;

    /* SH_MEM per VMID (gfx_v10_0_constants_init): for vmid 0..15, select the VMID via GRBM_GFX_CNTL
     * (PIPEID/MEID/QUEUEID left 0, matching nv_grbm_select(adev,0,0,0,vmid)), program SH_MEM_CONFIG =
     * VGH_DEFAULT_SH_MEM_CONFIG for every VMID, and for vmid!=0 also SH_MEM_BASES with PRIVATE_BASE=1,
     * SHARED_BASE=2 (aperture start >> 48). VMID0 keeps its own reset SH_MEM_BASES untouched, matching
     * amdgpu's `if (i != 0)` guard. GRBM_GFX_CNTL is restored to 0 (VMID0/PIPE0/ME0/QUEUE0) after. */
    for (vmid = 0; vmid < 16u; vmid++)
    {
        g_gpuRegs[VGH_GRBM_GFX_CNTL] = (vmid << VGH_GRBM_GFX_CNTL_VMID_SHIFT);
        g_gpuRegs[VGH_SH_MEM_CONFIG] = VGH_DEFAULT_SH_MEM_CONFIG;
        if (vmid != 0)
        {
            unsigned int bases = (VGH_SH_MEM_BASES_PRIVATE_BASE_VALUE << VGH_SH_MEM_BASES_PRIVATE_BASE_SHIFT)
                                | (VGH_SH_MEM_BASES_SHARED_BASE_VALUE  << VGH_SH_MEM_BASES_SHARED_BASE_SHIFT);
            g_gpuRegs[VGH_SH_MEM_BASES] = bases;
        }
    }
    g_gpuRegs[VGH_GRBM_GFX_CNTL] = 0u;
    PuckMfence();

    /* GDS VMID0..15 BASE/SIZE (gfx_v10_0_init_gds_vmid, gfx_v10_0.c 4872): loop vmid 1..15 (VMID0 is
     * deliberately left untouched -- amdgpu's comment: "VMID0 should maintain access so that HWS
     * firmware can save/restore entries"), writing 0 to both BASE and SIZE. Stride is 2 dwords per
     * VMID (mmGDS_VMID0_BASE=0x20a0, mmGDS_VMID1_BASE=0x20a2, ... verified directly against the header
     * -- every VMIDn slot is separately enumerated there, not just the first two). */
    for (vmid = 1; vmid < 16u; vmid++)
    {
        g_gpuRegs[VGH_GDS_VMID0_BASE + 2u * vmid] = 0u;
        g_gpuRegs[VGH_GDS_VMID0_SIZE + 2u * vmid] = 0u;
    }
    PuckMfence();

    /* Vangogh golden registers (golden_settings_gc_10_3_vangogh): masked RMW. Four kernel entries have
     * no vendored define (see the skip comment on the VGH_CH_PIPE_STEER block above) -- logged, not
     * applied. */
    {
        static const struct { unsigned int reg; unsigned int mask, value; const char *name; } golden[] = {
            { VGH_CH_PIPE_STEER,          0x000000ffu, 0x000000e4u, "CH_PIPE_STEER" },
            { VGH_DB_DEBUG3,               0xffffffffu, 0x00000200u, "DB_DEBUG3" },
            { VGH_DB_DEBUG4,               0xffffffffu, 0x00800000u, "DB_DEBUG4" },
            { VGH_DB_EXCEPTION_CONTROL,    0x7fff0f1fu, 0x00b80000u, "DB_EXCEPTION_CONTROL" },
            { VGH_GB_ADDR_CONFIG,          0x0c1807ffu, 0x00000142u, "GB_ADDR_CONFIG" },
            { VGH_GCR_GENERAL_CNTL,        0x1ff1ffffu, 0x00000500u, "GCR_GENERAL_CNTL" },
            { VGH_GL1_PIPE_STEER,          0x000000ffu, 0x000000e4u, "GL1_PIPE_STEER" },
            { VGH_GL2_PIPE_STEER_0,        0x77777777u, 0x32103210u, "GL2_PIPE_STEER_0" },
            { VGH_GL2_PIPE_STEER_1,        0x77777777u, 0x32103210u, "GL2_PIPE_STEER_1" },
            { VGH_GL2A_ADDR_MATCH_MASK,    0xffffffffu, 0xfffffff3u, "GL2A_ADDR_MATCH_MASK" },
            { VGH_GL2C_ADDR_MATCH_MASK,    0xffffffffu, 0xfffffff3u, "GL2C_ADDR_MATCH_MASK" },
            { VGH_GL2C_CM_CTRL1,           0xff8fff0fu, 0x580f1008u, "GL2C_CM_CTRL1" },
            { VGH_PA_CL_ENHANCE,           0xf17fffffu, 0x01200007u, "PA_CL_ENHANCE" },
            { VGH_PA_SC_BINNER_TIMEOUT_CNT,0xffffffffu, 0x00000800u, "PA_SC_BINNER_TIMEOUT_COUNTER" },
            { VGH_PA_SC_ENHANCE_2,         0xffffffbfu, 0x00000020u, "PA_SC_ENHANCE_2" },
            { VGH_SPI_CONFIG_CNTL_1,       0xffffffffu, 0x00070103u, "SPI_CONFIG_CNTL_1" },
            { VGH_SQG_CONFIG,              0x000017ffu, 0x00001000u, "SQG_CONFIG" },
            { VGH_SX_DEBUG_1,              0x00010000u, 0x00010020u, "SX_DEBUG_1" },
            { VGH_UTCL1_CTRL,              0xffffffffu, 0x00400000u, "UTCL1_CTRL" },
            { VGH_VGT_GS_MAX_WAVE_ID,      0x00000fffu, 0x000000ffu, "VGT_GS_MAX_WAVE_ID" },
            { VGH_LDS_CONFIG,              0x00000020u, 0x00000020u, "LDS_CONFIG" }, /* kernel: fixes a GPU hang on VanGogh */
        };
        unsigned int i;
        for (i = 0; i < sizeof(golden) / sizeof(golden[0]); i++)
        {
            unsigned int v = g_gpuRegs[golden[i].reg];
            v = (v & ~golden[i].mask) | (golden[i].value & golden[i].mask);
            g_gpuRegs[golden[i].reg] = v;
        }
        PuckMfence();
        SerialPuts("[gpu] (e) golden: applied 21/25 vangogh entries (skipped: CGTT_SPI_RA0_CLK_CTRL, "
                   "CGTT_SPI_RA1_CLK_CTRL, GL2C_CTRL3, TA_CNTL_AUX -- no vendored define)\r\n");
    }
}

/* Stage (e): GFX CP ring bring-up + IB submit + fence (spec 5). 2026-07-03 root-cause conclusion (see
 * docs/deck-bringup-handoff.md): modern gfx10.3 firmware does not serve the legacy CP_RB0_* interface
 * at all -- amdgpu ships amdgpu_async_gfx_ring=1, so on Vangogh the gfx ring is ALWAYS an MQD-backed
 * HQD queue, normally committed by a KIQ MAP_QUEUES packet. Boot 3 found + fixed the RLC save/restore-
 * list fw-type-ID bug (all three now load ok). Boot 4 graded two experiments -- A0 "hqd-poke" (scheduler
 * designation only, no live MEC/KIQ) and A1 "hqd-kiq" (full direct-MMIO KIQ bring-up) -- and PROVED A0
 * insufficient: the poke alone still stalled identically. Boot 5's A1 (full KIQ bring-up: MEC un-halt +
 * kiq_setting + a real v10_compute_mqd committed to CP_HQD_*) UNBLOCKED THE GFX PFP for the first time
 * ever -- CP_GFX_HQD_RPTR advanced to 0xa == WPTR (all 10 ring dwords consumed), CP_ROQ_RB_STAT=
 * 0x00140014 (data flowed), PFP/ME PCs moved to new loops. CONCLUSION: the gfx10.3 PFP firmware will
 * not process any ring until the MEC is running with a designated KIQ -- A0's poke-only gradation is
 * proven insufficient and is DROPPED; the full KIQ bring-up is now UNCONDITIONAL, run once before any
 * ring attempt (PuckGpuKiqPoke remains, called from inside PuckGpuKiqBringUp as a helper step, not as
 * its own graded experiment anymore).
 *
 * Boot 5's remaining failure: SCRATCH_REG0 stayed 0xCAFEDEAD (SET_UCONFIG_REG did not observably
 * execute) and CP_STALLED_STAT3=0x00002000 decodes to CE_WAITING_ON_DE_COUNTER_UNDERFLOW
 * (gc_10_3_0_sh_mask.h) with CP_STAT=0x94008200 (CP_BUSY|ROQ_CE_RING_BUSY|CE_BUSY|PFP_BUSY|
 * ROQ_RING_BUSY): the Constant Engine, un-halted alongside ME/PFP by CP_ME_CNTL=0 (which clears
 * CE_HALT too), is consuming the ring in parallel and is wedged on the CE/DE sync counter. Two graded
 * experiments now follow the unconditional KIQ bring-up, in order:
 *   B1 "hqd-noce"   : RingAttemptHqd on the GART block, un-halting CP_ME_CNTL to CE_HALT_MASK instead
 *                     of 0 -- ME+PFP run, CE stays HALTED. gfx10 drivers never use the CE (vestigial on
 *                     RDNA); if the CE was the only wedge, this alone should pass.
 *   B2 "hqd-cepart" : only if B1 fails. CE un-halted (CP_ME_CNTL=0) AND the ring gains amdgpu's
 *                     cp_gfx_start CE tail (SET_BASE CE_PARTITION, right after PREAMBLE_END and before
 *                     SET_UCONFIG_REG) -- see the VGH_PM4_SET_BASE comment for the UNVERIFIED-encoding
 *                     note (nvd.h not vendored, same gap as the other PM4 opcodes in this file).
 * Both B1 and B2 append a WRITE_DATA-to-memory packet after SET_UCONFIG_REG and use a two-stage
 * reg/mem poll verdict (see RingAttemptHqd's "(j)" comment): PASS = the memory write landed, which
 * proves CP execution independent of whether the SET_UCONFIG_REG register path works.
 * The old "hqd-vram" (GART-vs-carveout) and "rb0-mmio" (legacy CP_RB0, no doorbell) attempts remain
 * DROPPED (boot-3 proved that axis uninformative). Called from PuckGpuBringUp only after stage (d)
 * leaves the microcode resident and the CP HALTED, and only if stage (c) published its GART buffer.
 * Every poll is TSC-deadlined; a timeout logs and returns/continues, never hangs, never resets -- the
 * panel scans out of this device. */
/* Scheduler poke helper (formerly graded experiment A0, now just a step inside the unconditional KIQ
 * bring-up -- boot 4 proved the poke ALONE insufficient, so it is never run standalone anymore). Reads
 * RLC_CP_SCHEDULERS, writes the KIQ designation into its low byte (kiq_setting's two-step write:
 * designation only, then OR the valid bit 0x80), leaving the other three MEC-pipe slot bytes untouched.
 * Logs before/after values. */
static void PuckGpuKiqPoke(void)
{
    unsigned int before, afterStep1, afterStep2;

    before = g_gpuRegs[VGH_RLC_CP_SCHEDULERS];
    SerialPuts("[gpu] (e:kiq) RLC_CP_SCHEDULERS before="); EcamPutHex((unsigned long long)before, 8);
    SerialPuts("\r\n");

    /* Step 1: low byte = designation, valid bit NOT yet set (kiq_setting). */
    afterStep1 = (before & 0xFFFFFF00u) | VGH_KIQ_SCHED_BYTE;
    g_gpuRegs[VGH_RLC_CP_SCHEDULERS] = afterStep1;
    PuckMfence();

    /* Step 2: OR in the valid/enable bit (0x80) -- the second kiq_setting write. */
    afterStep2 = afterStep1 | VGH_KIQ_SCHED_VALID_MASK;
    g_gpuRegs[VGH_RLC_CP_SCHEDULERS] = afterStep2;
    PuckMfence();

    SerialPuts("[gpu] (e:kiq) RLC_CP_SCHEDULERS after step1="); EcamPutHex((unsigned long long)afterStep1, 8);
    SerialPuts(" after step2="); EcamPutHex((unsigned long long)g_gpuRegs[VGH_RLC_CP_SCHEDULERS], 8);
    SerialPuts(" (designation me="); EcamPutDec(VGH_KIQ_ME); SerialPuts(" pipe="); EcamPutDec(VGH_KIQ_PIPE);
    SerialPuts(" queue="); EcamPutDec(VGH_KIQ_QUEUE); SerialPuts(")\r\n");
}

/* Full direct-MMIO KIQ bring-up, faithful to gfx_v10_0_kiq_resume (kiq_setting + compute_mqd_init +
 * kiq_init_register), WITHOUT submitting any PM4 packet through the KIQ ring itself (no MAP_QUEUES --
 * that would need the ring test's PM4 plumbing again). UNCONDITIONAL as of boot 5: boot 4 proved the
 * scheduler-poke-alone gradation (formerly "A0") insufficient, and boot 5 proved this full bring-up
 * (formerly "A1") is what actually unblocks the gfx PFP (RPTR advanced 0->0xa for the first time ever)
 * -- so it is no longer gated behind a failed poke-only attempt; PuckGpuRingTest now calls it once,
 * before any ring attempt. Never polls the new MEC ring (there IS no MEC ring traffic yet) -- un-halt
 * MEC and proceed unconditionally; a CP_HQD_ACTIVE readback of 0 after the commit is logged and
 * treated as a diagnostic, not a hang condition. */
static void PuckGpuKiqBringUp(void)
{
    volatile unsigned char *cpuBase = g_gartTestCpu;
    unsigned long long mcBase = g_gartTestVa;
    unsigned long long eopMc, mqdMc, pqMc, rptrMc, wptrMc;
    volatile unsigned int *mqd;
    unsigned int tmp, pqControl, eopControl, persistentState, doorbellControl;

    SerialPuts("[gpu] (e:kiq) starting direct-MMIO KIQ bring-up (unconditional)\r\n");

    /* (a) Un-halt MEC. CP_MEC_CNTL was left at its post-autoload halted value; clear both ME1/ME2 halt
     * bits. This is new territory: there is no MEC ring traffic to poll for afterward, so just clear
     * the bits and proceed -- no GpuWaitReg call here (nothing to wait for yet). */
    tmp = g_gpuRegs[VGH_CP_MEC_CNTL];
    tmp &= ~(VGH_CP_MEC_CNTL_MEC_ME1_HALT_MASK | VGH_CP_MEC_CNTL_MEC_ME2_HALT_MASK);
    g_gpuRegs[VGH_CP_MEC_CNTL] = tmp;
    PuckMfence();
    SerialPuts("[gpu] (e:kiq) CP_MEC_CNTL un-halted: "); EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_MEC_CNTL], 8);
    SerialPuts("\r\n");

    /* (b) kiq_setting: the two-step RLC_CP_SCHEDULERS write (PuckGpuKiqPoke), called as a step here. */
    PuckGpuKiqPoke();

    /* (c) Zero the KIQ buffers (MQD, EOP, PQ/ring) + the rptr/wptr singleton dwords, relative to the
     * GART test buffer base (same base RingAttemptHqd's gart attempt uses). */
    eopMc  = mcBase + VGH_RT_KIQ_EOP_OFF;
    mqdMc  = mcBase + VGH_RT_KIQ_MQD_OFF;
    pqMc   = mcBase + VGH_RT_KIQ_RING_OFF;
    rptrMc = mcBase + VGH_RT_KIQ_RPTR_OFF;
    wptrMc = mcBase + VGH_RT_KIQ_WPTR_OFF;
    mqd    = (volatile unsigned int *)(cpuBase + VGH_RT_KIQ_MQD_OFF);
    {
        volatile unsigned int *eop = (volatile unsigned int *)(cpuBase + VGH_RT_KIQ_EOP_OFF);
        volatile unsigned int *pq  = (volatile unsigned int *)(cpuBase + VGH_RT_KIQ_RING_OFF);
        unsigned int i;
        for (i = 0; i < 0x1000u / 4u; i++) { eop[i] = 0u; pq[i] = 0u; mqd[i] = 0u; }
        *(volatile unsigned int *)(cpuBase + VGH_RT_KIQ_RPTR_OFF) = 0u;
        *(volatile unsigned int *)(cpuBase + VGH_RT_KIQ_WPTR_OFF) = 0u;
        PuckMfence();
    }

    /* Build the v10_compute_mqd image (gfx_v10_0_compute_mqd_init, gfx_v10_0.c 6504): only the fields
     * it sets. v10_structs.h's struct v10_compute_mqd has no per-field "// offset" comments (unlike
     * v10_gfx_mqd), so the dword indices below were counted directly off the vendored struct's field
     * list (cp_mqd_base_addr_lo at dw128, matching VGH_MQD_DW_CP_MQD_BASE_ADDR used for the gfx MQD --
     * the two structs share that one field at the same index, but diverge immediately after: the
     * compute struct's cp_hqd_pq_base_lo/_hi sit at dw136/137, its cp_hqd_eop_base_addr_lo/_hi/_control
     * at dw165/166/167 -- distinct slots, not reused from the gfx MQD's dword constants). */
    mqd[0]  = 0xC0310800u;                              /* header */
    mqd[23] = 0xffffffffu;                              /* compute_static_thread_mgmt_se0 (dw23) */
    mqd[24] = 0xffffffffu;                              /* compute_static_thread_mgmt_se1 (dw24) */
    mqd[26] = 0xffffffffu;                              /* compute_static_thread_mgmt_se2 (dw26) */
    mqd[27] = 0xffffffffu;                              /* compute_static_thread_mgmt_se3 (dw27) */

    /* EOP base addr + EOP_CONTROL.EOP_SIZE = order_base_2(GFX10_MEC_HPD_SIZE/4)-1. GFX10_MEC_HPD_SIZE
     * is 2048 (gfx_v10_0.c line 57, NOT 4096) -> order_base_2(512)-1 = 9-1 = 8. */
    mqd[165] = (unsigned int)((eopMc >> 8) & 0xFFFFFFFFu); /* cp_hqd_eop_base_addr_lo (dw165) */
    mqd[166] = (unsigned int)(eopMc >> 40);                /* cp_hqd_eop_base_addr_hi (dw166) */
    eopControl = g_gpuRegs[VGH_CP_HQD_EOP_CONTROL];
    eopControl = (eopControl & ~VGH_CP_HQD_EOP_CONTROL_EOP_SIZE_MASK) | (8u << VGH_CP_HQD_EOP_CONTROL_EOP_SIZE_SHIFT);
    mqd[167] = eopControl;                                 /* cp_hqd_eop_control (dw167) */

    /* Doorbell control: KIQ doorbell index 0 (AMDGPU_NAVI10_DOORBELL_KIQ, well-known gfx10 Navi
     * constant -- amdgpu_doorbell.h is not vendored here). */
    doorbellControl = g_gpuRegs[VGH_CP_HQD_PQ_DOORBELL_CONTROL];
    doorbellControl = (doorbellControl & ~VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_OFFSET_MASK)
                     | ((VGH_KIQ_DOORBELL_INDEX << 1) << VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_OFFSET_SHIFT);
    doorbellControl |= VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_EN_MASK;
    doorbellControl &= ~(VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_SOURCE_MASK | VGH_CP_HQD_PQ_DOORBELL_CONTROL_DOORBELL_HIT_MASK);
    mqd[143] = doorbellControl;                            /* cp_hqd_pq_doorbell_control (dw143) */

    mqd[152] = 0u; /* cp_hqd_dequeue_request (dw152) */
    mqd[182] = 0u; /* cp_hqd_pq_wptr_lo (dw182) */
    mqd[183] = 0u; /* cp_hqd_pq_wptr_hi (dw183) */

    mqd[128] = (unsigned int)(mqdMc & 0xFFFFFFFCu); /* cp_mqd_base_addr_lo (dw128) */
    mqd[129] = (unsigned int)(mqdMc >> 32);          /* cp_mqd_base_addr_hi (dw129) */

    tmp = g_gpuRegs[VGH_CP_MQD_CONTROL];
    tmp &= ~0x0000000Fu; /* CP_MQD_CONTROL__VMID_MASK: VMID=0 */
    mqd[162] = tmp;      /* cp_mqd_control (dw162) */

    mqd[136] = (unsigned int)((pqMc >> 8) & 0xFFFFFFFFu); /* cp_hqd_pq_base_lo (dw136) */
    mqd[137] = (unsigned int)(pqMc >> 40);                /* cp_hqd_pq_base_hi (dw137) */

    pqControl = g_gpuRegs[VGH_CP_HQD_PQ_CONTROL];
    pqControl = (pqControl & ~VGH_CP_HQD_PQ_CONTROL_QUEUE_SIZE_MASK)
              | ((RtLog2((unsigned long long)0x1000u / 4ull) << VGH_CP_HQD_PQ_CONTROL_QUEUE_SIZE_SHIFT) & VGH_CP_HQD_PQ_CONTROL_QUEUE_SIZE_MASK);
    pqControl = (pqControl & ~VGH_CP_HQD_PQ_CONTROL_RPTR_BLOCK_SIZE_MASK)
              | ((RtLog2(4096ull / 4ull) << VGH_CP_HQD_PQ_CONTROL_RPTR_BLOCK_SIZE_SHIFT) & VGH_CP_HQD_PQ_CONTROL_RPTR_BLOCK_SIZE_MASK);
    pqControl &= ~(VGH_CP_HQD_PQ_CONTROL_UNORD_DISPATCH_MASK | VGH_CP_HQD_PQ_CONTROL_TUNNEL_DISPATCH_MASK);
    pqControl |= (VGH_CP_HQD_PQ_CONTROL_PRIV_STATE_MASK | VGH_CP_HQD_PQ_CONTROL_KMD_QUEUE_MASK);
    mqd[145] = pqControl; /* cp_hqd_pq_control (dw145) */

    mqd[139] = (unsigned int)(rptrMc & 0xFFFFFFFCu);        /* cp_hqd_pq_rptr_report_addr_lo (dw139) */
    mqd[140] = (unsigned int)((rptrMc >> 32) & 0xFFFFu);    /* cp_hqd_pq_rptr_report_addr_hi (dw140) */
    mqd[141] = (unsigned int)(wptrMc & 0xFFFFFFFCu);        /* cp_hqd_pq_wptr_poll_addr_lo (dw141) */
    mqd[142] = (unsigned int)((wptrMc >> 32) & 0xFFFFu);    /* cp_hqd_pq_wptr_poll_addr_hi (dw142) */

    mqd[138] = g_gpuRegs[VGH_CP_HQD_PQ_RPTR]; /* cp_hqd_pq_rptr (dw138): snapshot of the live register */
    mqd[131] = 0u;                             /* cp_hqd_vmid (dw131) */

    persistentState = g_gpuRegs[VGH_CP_HQD_PERSISTENT_STATE];
    persistentState = (persistentState & ~VGH_CP_HQD_PERSISTENT_STATE_PRELOAD_SIZE_MASK)
                     | ((0x53u << VGH_CP_HQD_PERSISTENT_STATE_PRELOAD_SIZE_SHIFT) & VGH_CP_HQD_PERSISTENT_STATE_PRELOAD_SIZE_MASK);
    mqd[132] = persistentState; /* cp_hqd_persistent_state (dw132) */

    mqd[133] = 0u; /* cp_hqd_pipe_priority (dw133) */
    mqd[134] = 0u; /* cp_hqd_queue_priority (dw134) */
    mqd[130] = 1u; /* cp_hqd_active (dw130) -- set active LAST, per compute_mqd_init/kiq_init_register */
    PuckMfence();

    /* Commit to the LIVE CP_HQD_* register file, in gfx_v10_0_kiq_init_register order, under a
     * GRBM_GFX_CNTL select for the KIQ ring (MEID=2, PIPEID=1, QUEUEID=0 -- VGH_KIQ_ME/_PIPE/_QUEUE). */
    g_gpuRegs[VGH_GRBM_GFX_CNTL] = (VGH_KIQ_ME << VGH_GRBM_GFX_CNTL_MEID_SHIFT)
                                  | (VGH_KIQ_PIPE << VGH_GRBM_GFX_CNTL_PIPEID_SHIFT)
                                  | (VGH_KIQ_QUEUE << VGH_GRBM_GFX_CNTL_QUEUEID_SHIFT);
    PuckMfence();

    g_gpuRegs[VGH_CP_HQD_PQ_DOORBELL_CONTROL] = 0u; /* disable doorbells while (re)programming */

    g_gpuRegs[VGH_CP_HQD_EOP_BASE_ADDR]    = mqd[165];
    g_gpuRegs[VGH_CP_HQD_EOP_BASE_ADDR_HI] = mqd[166];
    g_gpuRegs[VGH_CP_HQD_EOP_CONTROL]      = eopControl;

    g_gpuRegs[VGH_CP_MQD_BASE_ADDR]    = mqd[128];
    g_gpuRegs[VGH_CP_MQD_BASE_ADDR_HI] = mqd[129];
    g_gpuRegs[VGH_CP_MQD_CONTROL]      = mqd[162];

    g_gpuRegs[VGH_CP_HQD_PQ_BASE]    = mqd[136];
    g_gpuRegs[VGH_CP_HQD_PQ_BASE_HI] = mqd[137];
    g_gpuRegs[VGH_CP_HQD_PQ_CONTROL] = pqControl;

    g_gpuRegs[VGH_CP_HQD_PQ_RPTR_REPORT_ADDR]    = mqd[139];
    g_gpuRegs[VGH_CP_HQD_PQ_RPTR_REPORT_ADDR_HI] = mqd[140];
    g_gpuRegs[VGH_CP_HQD_PQ_WPTR_POLL_ADDR]      = mqd[141];
    g_gpuRegs[VGH_CP_HQD_PQ_WPTR_POLL_ADDR_HI]   = mqd[142];

    g_gpuRegs[VGH_CP_MEC_DOORBELL_RANGE_LOWER] = (VGH_KIQ_DOORBELL_INDEX * 2u) << 2;
    g_gpuRegs[VGH_CP_MEC_DOORBELL_RANGE_UPPER] = (VGH_KIQ_DOORBELL_INDEX * 2u) << 2; /* single-queue range */

    g_gpuRegs[VGH_CP_HQD_PQ_DOORBELL_CONTROL] = doorbellControl;

    g_gpuRegs[VGH_CP_HQD_PQ_WPTR_LO] = mqd[182];
    g_gpuRegs[VGH_CP_HQD_PQ_WPTR_HI] = mqd[183];

    g_gpuRegs[VGH_CP_HQD_VMID] = mqd[131];
    g_gpuRegs[VGH_CP_HQD_PERSISTENT_STATE] = persistentState;

    g_gpuRegs[VGH_CP_HQD_ACTIVE] = mqd[130]; /* activate LAST */
    PuckMfence();

    /* Read CP_HQD_ACTIVE back while the KIQ queue is STILL selected -- the HQD register file is
     * banked per GRBM_GFX_CNTL queue selection, so a readback after the restore below would sample
     * some other queue's bank and false-alarm. */
    tmp = g_gpuRegs[VGH_CP_HQD_ACTIVE];

    g_gpuRegs[VGH_GRBM_GFX_CNTL] = 0u;
    PuckMfence();

    SerialPuts("[gpu] (e:kiq) MEC un-halted, KIQ HQD active. CP_HQD_ACTIVE=");
    EcamPutHex((unsigned long long)tmp, 8);
    SerialPuts(" RLC_CP_SCHEDULERS="); EcamPutHex((unsigned long long)g_gpuRegs[VGH_RLC_CP_SCHEDULERS], 8);
    SerialPuts("\r\n");
    if ((tmp & 1u) == 0u)
        SerialPuts("[gpu] (e:kiq) NOTE: CP_HQD_ACTIVE read back 0 after commit -- continuing to the ring"
                   " attempt anyway (diagnostic only)\r\n");
}

/* Boot-12 (2026-07-04) CE icache fix. ROOT-CAUSE HYPOTHESIS: Vangogh uses amdgpu's
 * AMDGPU_FW_LOAD_RLC_BACKDOOR_AUTO path, whose gfx_v10_0_wait_for_rlc_autoload_complete (scratchpad
 * gfx_v10_0.c ~5617-5658) calls gfx_v10_0_rlc_backdoor_autoload_config_{me,ce,pfp,mec}_cache in that
 * exact order right after the RLC autoload-complete poll -- each one invalidates + reprograms ONE
 * front end's instruction-cache base to point at that front end's ucode inside the RLC's own
 * autoload GPU buffer (adev->gfx.rlc.rlc_autoload_gpu_addr + rlc_autoload_info[...].offset). Puck's
 * PSP-hybrid stage (d) authenticates PFP/ME/CE ucode via LOAD_IP_FW (which the PSP consumes and
 * presumably wires into ITS OWN idea of the icache, since PFP/ME visibly execute) but never calls the
 * *_IC_BASE-programming step at all -- for any of the three. The fact that PFP/ME run while CE has
 * been wedged identically across all 11 boots to date is consistent with the CE specifically lacking
 * a valid icache base (PFP/ME got themselves wired some other way this port doesn't yet understand;
 * this fix deliberately does NOT touch their IC_BASE registers -- see (c) below).
 *
 * This function mirrors gfx_v10_0_rlc_backdoor_autoload_config_ce_cache (scratchpad gfx_v10_0.c
 * 5506-5541) exactly for the CE only: invalidate, poll INVALIDATE_CACHE_COMPLETE (TSC-deadlined, log
 * timeout but continue -- never hang), write IC_BASE_LO/HI. The reference writes
 * `lower_32_bits(addr) & 0xFFFFF000` / `upper_32_bits(addr)` -- i.e. the FULL byte address, page-
 * masked, NOT addr>>12; IC_BASE_LO/HI are 32-bit halves of a byte address, not a page-number field
 * (confirmed by the reference doing a plain `&` mask, never a shift, and by CP_*_IC_BASE_CNTL being
 * the SEPARATE register that carries VMID/CACHE_POLICY -- the address itself has no room for a
 * shifted encoding). The reference never touches IC_BASE_CNTL in this sequence (no PRIME_ICACHE step
 * either) -- see the VGH_CP_CE_IC_BASE_CNTL comment; this port matches that silence exactly by also
 * not writing IC_BASE_CNTL, so there is nothing to log for it beyond the diagnostic dump.
 *
 * Called unconditionally from PuckGpuRingTest, right after PuckGpuKiqBringUp() and before the first
 * ring attempt -- reprogramming a working icache base to a fresh valid copy is exactly what the
 * backdoor-autoload path itself does on every real boot, so doing it unconditionally here carries no
 * regression risk if CE_IC_BASE was already fine. ORDERING: the CP front ends are HALTED at this
 * point -- PuckGpuBringUp (stage (d)) leaves CP_ME_CNTL at its post-autoload halted value and never
 * un-halts; RingAttemptHqd's own halt (its step (a)) only happens later, per-attempt -- so this fix
 * runs squarely inside that stage-(d)-halted window, matching the reference (which itself runs the
 * *_cache config helpers before gfx_v10_0_cp_gfx_start ever un-halts anything). */
static void PuckGpuCeIcacheFix(void)
{
    const PuckGpuFw *fw;
    const unsigned char *payload;
    unsigned int size, tmp;
    unsigned long long stageMc, addr;
    unsigned long long end;

    fw = PuckGpuFwFind("vangogh_ce.bin");
    payload = FwPayload(fw, &size);
    if (!payload || !g_pspWin || !g_pspWinMc)
    {
        SerialPuts("[icache] CE ucode payload/window unavailable; skipping CE icache reprogram\r\n");
        return;
    }

    /* (a) Stage the CE ucode payload into the dedicated window offset (clear of the KIQ/gfx-ring
     * VGH_RT_* block, which on the carveout base ends at +0x1908000 -- see VGH_PSPWIN_CE_IC). */
    GpuUcWrite(VGH_PSPWIN_CE_IC, payload, size);
    stageMc = g_pspWinMc + VGH_PSPWIN_CE_IC; /* already 1 MiB-aligned -> page-aligned */

    /* (b) Invalidate the CE L1 instruction cache (CP_CE_IC_OP_CNTL.INVALIDATE_CACHE, gc_10_3_0_offset.h
     * mmCP_CE_IC_OP_CNTL=0x584b idx1), poll INVALIDATE_CACHE_COMPLETE (gc_10_3_0_sh_mask.h
     * CP_CE_IC_OP_CNTL__INVALIDATE_CACHE_COMPLETE_MASK=0x2), mirroring
     * gfx_v10_0_rlc_backdoor_autoload_config_ce_cache's 50 ms/1 us-step poll with a TSC deadline
     * instead of udelay(1) spin-counting. Timeout: log and continue -- never hang. */
    tmp = g_gpuRegs[VGH_CP_CE_IC_OP_CNTL];
    tmp |= VGH_CP_CE_IC_OP_CNTL_INVALIDATE_CACHE_MASK;
    g_gpuRegs[VGH_CP_CE_IC_OP_CNTL] = tmp;
    PuckMfence();

    end = __rdtsc() + (g_tscFreqHz / 1000ull) * 10ull; /* ~10 ms */
    for (;;)
    {
        tmp = g_gpuRegs[VGH_CP_CE_IC_OP_CNTL];
        if ((tmp & VGH_CP_CE_IC_OP_CNTL_INVALIDATE_CACHE_COMPLETE_MASK) != 0u) break;
        if (__rdtsc() > end)
        {
            SerialPuts("[icache] CE icache invalidate TIMEOUT (op_cntl=");
            EcamPutHex((unsigned long long)tmp, 8); SerialPuts(") -- continuing anyway\r\n");
            break;
        }
    }

    /* (c) Program CP_CE_IC_BASE_LO/HI with the staged blob's MC address -- the full byte address,
     * page-masked (see the function comment for why this is not addr>>12). IC_BASE_CNTL is
     * deliberately left untouched: the reference sequence never writes it either. */
    addr = stageMc & 0xFFFFFFFFFFFFF000ull;
    g_gpuRegs[VGH_CP_CE_IC_BASE_LO] = (unsigned int)(addr & 0xFFFFF000u);
    g_gpuRegs[VGH_CP_CE_IC_BASE_HI] = (unsigned int)(addr >> 32);
    PuckMfence();

    SerialPuts("[icache] CE IC_BASE reprogrammed -> "); EcamPutHex(stageMc, 10);
    SerialPuts(" (size="); EcamPutDec(size); SerialPuts(" bytes)\r\n");
}

static void PuckGpuRingTest(void)
{
    int passCe;
    volatile unsigned char *cpuBase = g_gartTestCpu;
    const char *passLabel;

    if (!g_gartTestReady)
    {
        SerialPuts("[gpu] (e) ring test skipped: stage (c) GART buffer not published\r\n");
        g_gpuBringUpNote = "(e) skipped: no GART test buffer";
        return;
    }

    PuckGpuConstantsInit();

    /* Boot-5: the full KIQ bring-up is now UNCONDITIONAL, before any ring attempt at all -- boot 4
     * proved the scheduler-poke-alone gradation insufficient (the poke's own ring attempt still
     * stalled identically to no poke at all), so there is nothing left to gain from trying a bare poke
     * first. PuckGpuKiqBringUp() calls PuckGpuKiqPoke() internally as one of its steps. */
    PuckGpuKiqBringUp();

    /* Boot-12 (2026-07-04): icache diagnostic dump (tagged "[icache]") right after KIQ bring-up, then
     * the unconditional CE icache reprogram, both BEFORE the first ring attempt -- see
     * PuckGpuCeIcacheFix's comment for the root-cause hypothesis and why "unconditional" is safe. The
     * CP front ends are still HALTED here (stage (d) never un-halted; RingAttemptHqd's own halt is
     * later, per-attempt), matching the reference sequence's own timing. */
    GpuIcacheDump("icache");
    PuckGpuCeIcacheFix();

    /* "hqd-ce": THE proven configuration (boot 6, 2026-07-03): CE un-halted (CP_ME_CNTL=0) + the
     * SET_BASE CE_PARTITION tail. Boot 6's B2 executed the whole stream -- SCRATCH_REG0=0xDEADBEEF,
     * our TYPE3 headers visible in the PFP/ME header FIFOs, RB0_RPTR fully advanced, CP_STAT idle.
     * The B1 "hqd-noce" experiment (CE halted) is retired: it wedged the PFP at a NEW pc (0xa7)
     * without consuming anything -- the CE must run and must get its partition base on gfx10.3,
     * even though drivers never feed it CE IBs. Boot 10: RingAttemptHqd's `emitCePartitionTail`
     * parameter is gone -- the full clear-state CSB (+ CE_PARTITION tail) it used to gate is now
     * unconditional, so both call sites below dropped their trailing `1` argument. */
    passCe = RingAttemptHqd("hqd-ce", g_gartTestCpu, g_gartTestVa, 0u);
    passLabel = "hqd-ce";

    if (!passCe && g_pspWin && g_pspWinMc)
    {
        /* Boot-8 fallback: a cold boot read the GART block's ring back as ALL ZEROS from the CPU --
         * either the low-RAM [0,2MiB) identity block is unreliable across firmware boots or a GPU
         * agent scribbled it. The carveout block (+0x1800000, UC CPU mapping, FB-direct MC addresses,
         * no GART and no low RAM anywhere in the path) sidesteps both suspects. Boot 10 CAUTION (boot-10
         * live finding): a CE wedged on DE_COUNTER_UNDERFLOW from attempt A survives halt->un-halt --
         * this fallback attempt's own post-un-halt CP_STAT poll can read a CE that is still stuck from
         * attempt A, not freshly wedged by attempt B itself. Treat a second-attempt CE wedge signature
         * as inconclusive about attempt B specifically unless attempt A's stall dump showed a clean CE. */
        SerialPuts("[gpu] (e:hqd-ce) FAILED; retrying on the carveout block (e:hqd-ce-vram)\r\n");
        passCe = RingAttemptHqd("hqd-ce-vram", g_pspWin + 0x01800000ull, g_pspWinMc + 0x01800000ull, 0u);
        if (passCe)
        {
            cpuBase = (volatile unsigned char *)(g_pspWin + 0x01800000ull);
            passLabel = "hqd-ce-vram";
        }
    }

    if (!passCe)
    {
        g_gpuBringUpNote = "(e) hqd-ce full-CSB ring test FAILED on gart AND vram blocks (see stall dumps)";
        return;
    }

    /* reg/mem verdict latched by RingAttemptHqd: boot 6 proved reg=Y; mem tests the new WRITE_DATA
     * CACHE_POLICY=BYPASS fix (mem=N with reg=Y means GL2 write-back visibility still needs work,
     * but execution itself is proven either way). */
    if (RingIbFence(passLabel, cpuBase))
        g_gpuBringUpNote = g_rtMemPass
            ? "(e) RING+IB LIVE (hqd-ce, full-CSB): reg+mem executed, fence signalled -- FIRST SILICON COMPLETE"
            : "(e) RING+IB LIVE (hqd-ce, full-CSB): reg executed (mem GL2-invisible), fence signalled";
    else
        g_gpuBringUpNote = g_rtMemPass
            ? "(e) ring test PASS (hqd-ce, full-CSB, reg+mem); IB/fence FAIL"
            : "(e) ring test PASS (hqd-ce, full-CSB, reg only; mem GL2-invisible); IB/fence FAIL";
}

/* Stages (c)+(d) entry point, wired into EfiEntry after PuckInitClock (the polls need the
 * calibrated TSC). No-ops with one line unless stage (b) mapped the register BAR AND the ESP
 * carried the \amdgpu blobs -- QEMU exercises the preload path and then skips here. */
void PuckGpuBringUp(void)
{
    if (!g_gpuRegs)
    { SerialPuts("[gpu] bring-up skipped: no register BAR mapped (expected under QEMU)\r\n"); return; }
    if (g_gpuFwCount == 0)
    { SerialPuts("[gpu] bring-up skipped: no \\amdgpu microcode preloaded\r\n"); return; }
    if (g_gpuRegs[VGH_GRBM_STATUS] == 0xFFFFFFFFu)
    { SerialPuts("[gpu] bring-up skipped: GRBM_STATUS reads all-F (bus dead?)\r\n"); return; }
    if (g_gpuRegs[VGH_MP0_C2PMSG_81] == 0)
    { SerialPuts("[gpu] bring-up skipped: PSP sOS not alive (C2PMSG_81 == 0)\r\n"); return; }

    if (PuckGpuGartBringUp() != 0) /* stage (c); also maps the carveout window stage (d) rides on */
    { g_gpuBringUpNote = "(c) GMC/GART FAILED"; return; }

    if (g_gpuRegs[VGH_RLC_BOOTLOAD_STATUS] & 0x80000000u)
    {
        /* Warm: a prior boot (this session, no D3cold) already autoloaded the RLC. The microcode is
         * resident but its provenance this boot is the prior load, not ours -- flag it honestly. */
        SerialPuts("[gpu] (d) RLC already bootloaded (warm); skipping the PSP fw load\r\n");
        g_gpuBringUpNote = "(d) RLC warm (PSP load SKIPPED this boot)";
    }
    else if (PuckGpuPspLoad() != 0)
    { g_gpuBringUpNote = "(d) PSP fw load FAILED (see scrolled log)"; return; }
    else
        /* PuckGpuPspLoad returned 0 only after its RLC-BOOTLOAD-bit31 + CP_STAT==0 polls both
         * passed -- the spec's stage-(d)-done condition. Our PSP load authenticated the microcode. */
        g_gpuBringUpNote = "(d) PSP LOAD OK: microcode authenticated + RLC autoloaded";

    /* Leave the GFX CP front ends HALTED here -- do NOT un-halt yet. The 2026-07-03 23:45 hardware
     * log falsified "un-halt in (d), program the ring in (e)": WPTR was delivered (0xa) but RB_RPTR
     * stayed 0 and SCRATCH never changed, i.e. the CP never fetched even though it had been running
     * (CP_STAT busy, not halted) the whole time the ring was programmed underneath it. Per amdgpu
     * gfx_v10_0_cp_gfx_start ordering, ALL CP_RB0 / doorbell registers are programmed while CE/PFP/ME
     * stay halted, and only then does CP_ME_CNTL clear to un-halt -- so a PFP that free-runs against
     * an empty/half-programmed ring never gets a clean first fetch. Stage (e)'s per-attempt functions
     * (RingAttemptHqd / RingAttempt) now each own the halt->program->un-halt sequence; report state
     * here, don't touch CP_ME_CNTL. */
    SerialPuts("[gpu] (d) MICROCODE LIVE: RLC bootloaded, CP left HALTED for stage (e) (CP_STAT=");
    EcamPutHex((unsigned long long)g_gpuRegs[VGH_CP_STAT], 8);
    SerialPuts(")\r\n");
    GpuPutReg("CP_STAT     ", VGH_CP_STAT);
    GpuPutReg("CP_ME_CNTL  ", VGH_CP_ME_CNTL);
    GpuPutReg("RLC_BOOTLOAD", VGH_RLC_BOOTLOAD_STATUS);
    GpuPutReg("GRBM_STATUS ", VGH_GRBM_STATUS);
    GpuPutReg("VM_L2_FAULT ", VGH_GCVM_L2_FAULT_STATUS);

    /* Stage (e): first silicon executes. Reached only when (c) built the GART and (d) left the
     * microcode resident (RLC bootloaded, CP still halted; a (d) failure returned earlier). It
     * latches its own g_gpuBringUpNote (overriding the (d) note) with the ring/IB/fence outcome. */
    PuckGpuRingTest();
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
    bs = EfiField(systemTable, EFI_ST_BOOTSERVICES_OFFSET);

    /* Bring the panel up as the very FIRST thing (only SerialInit + the GOP capture run before it), so
     * the firmware boot logo clears to black the instant our image takes control -- the earliest
     * possible "we are running" beacon -- and every message below renders on the Deck's only console
     * (it has no serial port). After this, even the IDT panic handler's trap-frame dump is on-panel. */
    PuckGopCapture(bs);
    PuckFbInitEarly();

    SerialPuts("[boot] Puck.BareMetal UEFI image entered; firmware boot services are up.\r\n");

    /* Capture the ACPI RSDP from the UEFI configuration table while it is still valid (the ECAM base
     * is parsed out of MCFG later, after paging). Must happen before ExitBootServices. */
    PuckAcpiCaptureRsdp(systemTable);

    /* Preload the dynamic guest's .so closure from the ESP into RAM + register it in the VFS, while
     * the firmware filesystem is still available. Then the Van Gogh GPU microcode (kernel-consumed,
     * not guest-visible) from \amdgpu\ -- stage (d) feeds it to the PSP long after the ESP is gone. */
    PuckEfiPreloadSos(imageHandle, bs);
    PuckEfiPreloadGpuFw(imageHandle, bs);

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
    {
        /* Identity-map all physical RAM (memory-map types 1..10). The framebuffer is NOT RAM -- on the
         * Deck it is a high MMIO aperture at ~995 GiB, far above the 512 GiB one PDPT can span -- so it
         * is deliberately excluded here and mapped explicitly just below. Clamp numGiB to [4, 512]. */
        unsigned long long ramTop = MemMapTopPhys((unsigned char *)mapBuf, finalMapSize, descSize);
        unsigned long long numGiB = (ramTop + 0x3FFFFFFFull) >> 30; /* round up to a whole GiB */
        if (numGiB < 4)   numGiB = 4;
        if (numGiB > 512) numGiB = 512;
        PuckInitPaging(numGiB);

        /* Arm the PAT (PA6=WC) BEFORE first mapping the framebuffer, then map the aperture
         * WRITE-COMBINING exactly once. WC is the correct type for a scanout aperture (matches Linux
         * efifb's ioremap_wc); the display controller does not snoop the CPU cache, so WB shows stale
         * garbage. fb.phys (~995 GiB) is far above our RAM map, so PuckMapMmio takes its 4 KiB
         * high-BAR path -- which no longer issues the destructive global wbinvd. */
        PuckInitPat();
        if (g_fb.phys)
        {
            unsigned long long i, n = (unsigned long long)g_fb.pitch * g_fb.height;
            g_fb.base = (unsigned int *)PuckMapMmio(g_fb.phys,
                (unsigned long long)g_fb.pitch * g_fb.height * 4ull, PUCK_MT_WC);
            for (i = 0; i < n; i++) g_fb.base[i] = 0; /* clear the panel to black via the WC mapping */
            PuckMfence();                             /* drain the WC buffers so it reaches the panel */
        }

        SerialPuts("[kernel] CR3 switched; RAM numGiB="); SerialPutHex64(numGiB);
        SerialPuts(" fb@"); SerialPutHex64(g_fb.phys); SerialPuts(" mapped WC.\r\n");
    }

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

    PuckGpuProbe();         /* stage (b): find the Van Gogh GPU, map its register BAR, read health */

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

    PuckGpuBringUp();   /* stages (c) GMC/GART + (d) PSP fw load; needs the calibrated TSC for its
                         * poll deadlines. One skip line under QEMU (no GPU found by stage (b)). */

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

    /* Re-read the GPU health set onto the final screen: the boot log scrolls past one Deck panel,
     * and this parked screen is what gets photographed. */
    PuckGpuHealthDump();

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
