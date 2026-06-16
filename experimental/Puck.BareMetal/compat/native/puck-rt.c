/* Hosted (Windows) native runtime glue. Part of Puck.BareMetal.
 *
 * The stock .NET 10 ILC emits, for managed entry points and loop safepoints, references
 * to low-level runtime symbols NativeAOT normally gets from its native runtime, which a
 * freestanding link does not include:
 *
 *   RhpReversePInvoke / RhpReversePInvokeReturn  - managed<->native transition for the
 *                                                  unmanaged entry point
 *   RhpGcPoll                                     - cooperative GC safepoint callback
 *   RhpTrapThreads                                - the data flag the inlined GC poll tests
 *   RhpFallbackFailFast                           - the JIT's fallback fail-fast path
 *
 * The ILC roots the helper-routed exports (RhpNewFast etc.) but not these JIT-transition
 * symbols, and emits no RhpTrapThreads. With no GC and no thread suspension the transition
 * is a no-op and threads are never trapped.
 *
 * Also provides the block-memory intrinsics (memset/memcpy/memmove) the JIT lowers struct
 * copies, stackalloc, and array init to; MSVC supplies these from vcruntime.lib, which a
 * freestanding link does not pull.
 *
 * Compile freestanding (no CRT): cl /c /GS- ... Plain C names map 1:1 to the undecorated
 * x64 symbols the linker asks for.
 */

/* Never nonzero: the GC poll the JIT inlines as `if (RhpTrapThreads) RhpGcPoll()` is dead. */
unsigned int RhpTrapThreads = 0;

void RhpReversePInvoke(void *frame) { (void)frame; }
void RhpReversePInvokeReturn(void *frame) { (void)frame; }
/* Forward (managed->native) transition for native calls that are NOT [SuppressGCTransition]
 * — e.g. calls through delegate* unmanaged<> function pointers. No GC, so the transition
 * frame is never walked: no-op. */
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }
void RhpGcPoll(void) { }

/* The JIT's fallback fail-fast (bounds/overflow/etc.). The ILC does not root the managed
 * export, so provide it here. No exceptions: terminate. */
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int uExitCode);
void RhpFallbackFailFast(void) { ExitProcess(0xFFu); }

/* kernel32 + compiler intrinsics used by the race-safe class-constructor runner below. Declared
 * here rather than via <windows.h> to keep this translation unit freestanding (no CRT). On the
 * EFI host these would come from our own threading layer instead of kernel32. */
__declspec(dllimport) unsigned long __stdcall GetCurrentThreadId(void);
long long _InterlockedCompareExchange64(long long volatile *destination, long long exchange, long long comparand);
long long _InterlockedExchange64(long long volatile *destination, long long value);
#pragma intrinsic(_InterlockedCompareExchange64, _InterlockedExchange64)

/* Array allocation fast path. The ILC lowers `newarr` to a raw external RhpNewArrayFast
 * reference without routing it to a managed method, so the managed export stays unresolved
 * (as with the transition stubs above). Allocate BaseSize + numElements*ComponentSize
 * zero-initialised bytes (via the mimalloc-backed PuckAllocZeroed), store the MethodTable
 * pointer at offset 0 and the element count at offset sizeof(void*). Only the first two
 * MethodTable fields are needed: ushort ComponentSize, ushort Flags, uint BaseSize. */
typedef struct PuckMethodTable
{
    unsigned short ComponentSize; /* _usComponentSize (offset 0) */
    unsigned short Flags;         /* _usFlags         (offset 2) */
    unsigned int   BaseSize;      /* _uBaseSize       (offset 4) */
} PuckMethodTable;

extern void *PuckAllocZeroed(unsigned long long size);

void *RhpNewArrayFast(PuckMethodTable *pMethodTable, int numElements)
{
    unsigned long long size;
    void **result;

    if (numElements < 0)
        ExitProcess(0xFFu);

    size = (unsigned long long)pMethodTable->BaseSize
         + (unsigned long long)(unsigned int)numElements * pMethodTable->ComponentSize;

    result = (void **)PuckAllocZeroed(size); /* zero-initialised; NULL only on OOM */
    if (result == 0)
        ExitProcess(0xFFu);

    result[0] = pMethodTable;                                          /* MethodTable* */
    *(int *)((unsigned char *)result + sizeof(void *)) = numElements;  /* array length */
    return result;
}

/* Pointer-element array fast path. The ILC routes `new T[]` for pointer-sized element types
 * (object[], nint[], ulong[], ...) to RhpNewPtrArrayFast; allocation is identical to
 * RhpNewArrayFast (the MethodTable's component size is the pointer size), so forward to it. */
void *RhpNewPtrArrayFast(PuckMethodTable *pMethodTable, int numElements)
{
    return RhpNewArrayFast(pMethodTable, numElements);
}

/* Single-object allocation fast path. The ILC references RhpNewFast as a raw symbol for
 * `newobj` of a reference type without rooting the managed export. Allocate BaseSize
 * zero-initialised bytes and store the MethodTable pointer. */
void *RhpNewFast(PuckMethodTable *pMethodTable)
{
    void **result = (void **)PuckAllocZeroed(pMethodTable->BaseSize);
    if (result == 0)
        ExitProcess(0xFFu);

    result[0] = pMethodTable;
    return result;
}

/* Reference-store write barriers. The JIT emits these for storing an object reference into a
 * heap field, a static ref field, or a ref array element. With no GC a barrier is just the
 * pointer store. The ILC references them as raw symbols without rooting the managed versions. */
void RhpAssignRef(void **dst, void *reference)        { *dst = reference; }
void RhpCheckedAssignRef(void **dst, void *reference) { *dst = reference; }

/* ---------------------------------------------------------------------------------------
 * Race-safe lazy static-constructor runner.
 *
 * Puck.Runtime's ClassConstructorRunner runs a type's .cctor on first static access, but its
 * core is single-threaded: it zeroes the cctor pointer then runs the .cctor, so two threads
 * racing a first-access could both run it, and a thread that observes the zeroed pointer would
 * proceed using statics another thread is still constructing. For the multi-threaded host
 * (GPU-driver worker threads triggering a first-access after startup) that is a correctness hazard.
 *
 * This runs each .cctor exactly once. The ILC emits StaticClassConstructionContext
 * (ClassConstructorRunner.cs) as a SINGLE pointer field (the .cctor address) — it cannot be
 * extended — so all state lives in that one 8-byte cell, distinguished by the fact that .cctor
 * code addresses are pointer-aligned (low bit 0):
 *   - low bit 0, non-zero  -> NOT STARTED; the value is the real .cctor code address;
 *   - low bit 1            -> RUNNING; the value is (constructing thread id << 1) | 1;
 *   - 0                    -> DONE.
 * The winner CASes the real address to its running-tag, runs the .cctor (using the address it
 * captured), then publishes 0. A concurrent first-accessor that finds RUNNING owned by ANOTHER
 * thread waits until DONE; one that finds its OWN tid returns (reentrant access from inside the
 * .cctor, so the partially-initialized statics are reachable — matching the single-threaded
 * zeroing trick the core runner relies on).
 *
 * x64 has a strong store order and completion is published with an interlocked (full-barrier)
 * store, so a waiter that observes 0 also observes every write the .cctor made to the statics.
 * (Reentrant cross-thread .cctor cycles remain a user-level deadlock, as on any runtime.) */
void PuckRunClassConstructor(long long volatile *pCctor)
{
    long long myTid;
    long long runningTag;

    if (*pCctor == 0)
        return; /* already initialized (fast path) */

    myTid = (long long)(unsigned long long)GetCurrentThreadId();
    runningTag = (myTid << 1) | 1;

    for (;;)
    {
        long long v = *pCctor;

        if (v == 0)
            return; /* done */

        if (v & 1)
        {
            /* running */
            if ((v >> 1) == myTid)
                return; /* reentrant: this thread is constructing it */

            while (*pCctor != 0) /* another thread is constructing; wait for completion */
                ;
            return;
        }

        /* not started: v is the real .cctor code address */
        if (_InterlockedCompareExchange64(pCctor, runningTag, v) == v)
        {
            ((void (*)(void))v)(); /* run the managed .cctor (no GC => no transition) */
            (void)_InterlockedExchange64(pCctor, 0); /* publish completion (release) */
            return;
        }
        /* lost the claim race; re-evaluate */
    }
}

/* ---------------------------------------------------------------------------------------
 * Polymorphic interface dispatch.
 *
 * The stock ILC lowers an interface call with 2+ implementers (one the JIT cannot
 * devirtualize) to `mov rcx,<this>; lea r11,[cell]; call qword ptr [r11]`, where `cell` is a
 * per-call-site InterfaceDispatchCell whose first field (m_pStub) it initializes to
 * RhpInitialDynamicInterfaceDispatch. Puck.Runtime has no interface-dispatch runtime, so that
 * symbol is unresolved.
 *
 * Normal NativeAOT resolves the cell once and then rewrites it to cache the answer (the
 * RhpInterfaceDispatch1/2/4/... fast paths). We don't need the cache: a DI-container teardown
 * path is not hot, and skipping the cache removes all the stub/rewrite machinery. So the cell
 * stays forever in its initial "interface + slot" metadata form and we fully resolve on every
 * call. The entry trampoline is in assembly (compat/native/interface-dispatch-x64.asm); it
 * calls the resolver below with (this, cell) and tail-jumps to whatever we return.
 *
 * Resolution is the (interface, slot) -> implementation walk over the type's DispatchMap. The
 * MethodTable / DispatchMap / InterfaceDispatchCell layout constants below were cross-checked
 * against the .obj this very ILC emits. Anything not modelled (sealed-vtable slots, static
 * virtuals, default interface methods, generic-variant dispatch, metadata-token / vtable-offset
 * cells, array dispatch) fails fast rather than silently mis-dispatching. */

/* InterfaceDispatchCell: { code* m_pStub; uintptr m_pCache; }. In a cell's initial form
 * m_pCache encodes the interface type; the interface SLOT lives in a following "terminator"
 * cell (m_pStub == 0). See InterfaceDispatchCell::GetDispatchCellInfo in rhbinder.h. */
typedef struct PuckInterfaceDispatchCell
{
    void              *m_pStub;
    unsigned long long m_pCache;
} PuckInterfaceDispatchCell;

/* m_pCache low-2-bit tags (rhbinder.h InterfaceDispatchCell::Flags). */
#define PUCK_IDC_MASK                       0x3ull
#define PUCK_IDC_INTERFACE_POINTER_OR_TOKEN 0x1ull /* m_pCache &= ~3 is the MethodTable*    */
#define PUCK_IDC_INDIRECTED_RELATIVE_PTR    0x2ull /* relptr to a MethodTable** indirection  */
#define PUCK_IDC_INTERFACE_RELATIVE_PTR     0x3ull /* relptr directly to the MethodTable     */

/* DispatchMap: ushort counts header then 6-byte {ifaceIndex, ifaceMethodSlot, implSlot}
 * entries (Internal.Runtime.DispatchMap). Only the standard (instance) entries are walked. */
typedef struct PuckDispatchMapEntry
{
    unsigned short InterfaceIndex;
    unsigned short InterfaceMethodSlot;
    unsigned short ImplMethodSlot;
} PuckDispatchMapEntry;

typedef struct PuckDispatchMap
{
    unsigned short StandardEntryCount;
    unsigned short DefaultEntryCount;
    unsigned short StandardStaticEntryCount;
    unsigned short DefaultStaticEntryCount;
    PuckDispatchMapEntry Entries[1]; /* [StandardEntryCount + DefaultEntryCount + ...] */
} PuckDispatchMap;

/* MethodTable flag bits we consult (Internal.Runtime.EETypeFlags). */
#define PUCK_MT_HASDISPATCHMAP   0x00040000u
#define PUCK_MT_HASFINALIZER     0x00100000u
#define PUCK_MT_ELEMENTTYPE_MASK 0x7C000000u
#define PUCK_MT_ELEMENTTYPE_SHIFT 26
#define PUCK_ET_ARRAY            0x17u
#define PUCK_ET_SZARRAY          0x18u

/* MethodTable accessors. The fixed header is 24 bytes (uint flags, uint baseSize,
 * MethodTable* relatedType, ushort numVtableSlots, ushort numInterfaces, uint hashCode);
 * the vtable, then the interface map, then the optional fields follow at computed offsets. */
#define PUCK_MT_HEADER_SIZE 24u

static unsigned int   Puck_MT_Flags(void *mt)          { return *(unsigned int *)mt; }
static unsigned short Puck_MT_NumVtableSlots(void *mt) { return *(unsigned short *)((char *)mt + 16); }
static unsigned short Puck_MT_NumInterfaces(void *mt)  { return *(unsigned short *)((char *)mt + 18); }
static void          *Puck_MT_RelatedType(void *mt)    { return *(void **)((char *)mt + 8); }
static void         **Puck_MT_Vtable(void *mt)         { return (void **)((char *)mt + PUCK_MT_HEADER_SIZE); }

static void **Puck_MT_InterfaceMap(void *mt)
{
    return (void **)((char *)mt + PUCK_MT_HEADER_SIZE + (unsigned)8 * Puck_MT_NumVtableSlots(mt));
}

/* MethodTable.DispatchMap. For statically compiled (non-dynamic) types the field is a 4-byte
 * self-relative pointer sitting just past the type-manager-indirection and writable-data cells
 * (MethodTable.GetFieldOffset(ETF_DispatchMap)); we never build dynamic types. */
static PuckDispatchMap *Puck_MT_DispatchMap(void *mt)
{
    unsigned int off;
    int         *pRel;

    if (!(Puck_MT_Flags(mt) & PUCK_MT_HASDISPATCHMAP))
        return 0;

    off = PUCK_MT_HEADER_SIZE
        + (unsigned)8 * Puck_MT_NumVtableSlots(mt)
        + (unsigned)8 * Puck_MT_NumInterfaces(mt)
        + 4u  /* type-manager indirection (relative pointer) */
        + 4u; /* writable data           (relative pointer) */

    pRel = (int *)((char *)mt + off);
    return (PuckDispatchMap *)((char *)pRel + *pRel); /* resolve RelativePointer<DispatchMap> */
}

/* MethodTable.GetSealedVirtualSlot. Interface implementations that are not in the main vtable —
 * notably every interface method an array (T[]) provides via Array<T>, and sealed-type interface
 * methods — live in a separate "sealed vtable". Its relative pointer sits at
 * GetFieldOffset(ETF_SealedVirtualSlots): just past the dispatch-map and finalizer cells. Each
 * sealed slot is itself a 4-byte self-relative pointer to the method code (static types). */
static void *Puck_MT_SealedVtableSlot(void *mt, unsigned short sealedIndex)
{
    unsigned int flags = Puck_MT_Flags(mt);
    unsigned int off;
    int         *pSealedRel;
    char        *sealedVtable;
    int         *pSlot;

    off = PUCK_MT_HEADER_SIZE
        + (unsigned)8 * Puck_MT_NumVtableSlots(mt)
        + (unsigned)8 * Puck_MT_NumInterfaces(mt)
        + 4u   /* type-manager indirection */
        + 4u;  /* writable data            */
    if (flags & PUCK_MT_HASDISPATCHMAP)
        off += 4u; /* dispatch map */
    if (flags & PUCK_MT_HASFINALIZER)
        off += 4u; /* finalizer */

    pSealedRel = (int *)((char *)mt + off);
    sealedVtable = (char *)pSealedRel + *pSealedRel;       /* RelativePointer<sealed vtable> */
    pSlot = (int *)(sealedVtable + (unsigned)4 * sealedIndex);
    return (char *)pSlot + *pSlot;                          /* RelativePointer<code> */
}

/* Called from the asm trampoline: resolve (this, cell) to the implementation code address. */
void *PuckResolveInterfaceDispatch(void *pThis, PuckInterfaceDispatchCell *pCell)
{
    void                         *pTgtType;
    void                         *pItfType;
    void                         *pCur;
    unsigned long long            cache;
    unsigned long long            tag;
    PuckInterfaceDispatchCell *pTerminator;
    unsigned long long            terminatorCache;
    unsigned short                itfSlot;

    if (pThis == 0)
        ExitProcess(0xFDu); /* NullReferenceException on the dispatch target */

    pTgtType = *(void **)pThis; /* object header: MethodTable* at offset 0 */

    /* --- decode the cell --- The active cell's m_pCache carries the interface type; the slot
     * (and cell kind, in the high 16 bits) lives in the terminator cell whose m_pStub == 0. */
    cache = pCell->m_pCache;

    pTerminator = pCell;
    while (pTerminator->m_pStub != 0)
        pTerminator++;
    terminatorCache = pTerminator->m_pCache;

    if ((unsigned int)(terminatorCache >> 16) != 0u /* DispatchCellType::InterfaceAndSlot */)
        ExitProcess(0xFCu); /* metadata-token / vtable-offset cells are not modeled */

    itfSlot = (unsigned short)terminatorCache;

    tag = cache & PUCK_IDC_MASK;
    if (tag == PUCK_IDC_INTERFACE_POINTER_OR_TOKEN)
    {
        pItfType = (void *)(cache & ~PUCK_IDC_MASK);
    }
    else if (tag == PUCK_IDC_INTERFACE_RELATIVE_PTR || tag == PUCK_IDC_INDIRECTED_RELATIVE_PTR)
    {
        /* Self-relative to the address of m_pCache; (int) sign-extends the low 32 bits. */
        unsigned long long p = (unsigned long long)&pCell->m_pCache + (unsigned long long)(long long)(int)cache;
        p &= ~PUCK_IDC_MASK;
        pItfType = (tag == PUCK_IDC_INTERFACE_RELATIVE_PTR) ? (void *)p : *(void **)p;
    }
    else
    {
        /* IDC_CachePointerPointsAtCache: only reachable once a cell has been cached, which we
         * never do. */
        ExitProcess(0xFCu);
        return 0;
    }

    /* --- walk the type hierarchy resolving (interface, slot) -> implementation --- */
    for (pCur = pTgtType; pCur != 0; pCur = Puck_MT_RelatedType(pCur))
    {
        PuckDispatchMap *map = Puck_MT_DispatchMap(pCur);
        if (map != 0)
        {
            void          **ifaceMap = Puck_MT_InterfaceMap(pCur);
            unsigned int    count = map->StandardEntryCount;
            unsigned int    k;

            for (k = 0; k < count; k++)
            {
                PuckDispatchMapEntry *e = &map->Entries[k];

                if (e->InterfaceMethodSlot == itfSlot && ifaceMap[e->InterfaceIndex] == pItfType)
                {
                    unsigned short nvt = Puck_MT_NumVtableSlots(pCur);

                    /* Index the most-derived type's vtable so overrides resolve correctly. */
                    if (e->ImplMethodSlot < nvt)
                        return Puck_MT_Vtable(pTgtType)[e->ImplMethodSlot];

                    /* Otherwise the implementation lives in pCur's sealed vtable. This is how a
                     * T[] supplies IEnumerable<T>.GetEnumerator etc. (via the shared Array<T>
                     * methods, which recover T from `this`); also how sealed types may store
                     * their interface impls. */
                    return Puck_MT_SealedVtableSlot(pCur, (unsigned short)(e->ImplMethodSlot - nvt));
                }
            }
        }

        /* Arrays resolve their interface methods from their OWN dispatch map (above) — a match is
         * found on the first iteration. If none matched, do not mis-walk _relatedType (which for
         * an array is the element type, not a base type): stop. */
        {
            unsigned int et = (Puck_MT_Flags(pCur) & PUCK_MT_ELEMENTTYPE_MASK) >> PUCK_MT_ELEMENTTYPE_SHIFT;
            if (et == PUCK_ET_ARRAY || et == PUCK_ET_SZARRAY)
                ExitProcess(0xFAu);
        }
    }

    /* Unimplemented interface method (default-interface-method dispatch is not modeled). */
    ExitProcess(0xF9u);
    return 0;
}

/* ---------------------------------------------------------------------------------------
 * GC static initialization + startup wrapper.
 *
 * The stock ILC emits a per-type "spine" object for each type's GC (reference-typed) statics
 * and a __GCStaticRegion table of pointers to the base cells. In a normal NativeAOT build the
 * native bootstrapper calls the managed InitializeModules, which walks that region, allocates
 * each spine, and patches the base cells. This build strips the bootstrapper and enters
 * at __managed__Main directly, so the region is never walked and every GC-static base reads as
 * a bogus address (any reference-typed static faults on first write).
 *
 * We re-create the GC-static slice of NativeAOT's InitializeStatics here in C and run it before
 * entering managed code, via PuckStart (the binary's real entry point — see EntryPointSymbol in
 * build/Puck.BareMetal.props). With no GC, the spines are simply allocated once (mimalloc,
 * never freed) and never need GC-handle rooting.
 *
 * win-x64 encoding (verified from the linker relocations of the generated object):
 *   - __ReadyToRunHeader is a real symbol; its section rows store ABSOLUTE Start/End.
 *   - the GC static region entries are RELATIVE (int32) pointers; each base cell holds
 *     relptr32(MethodTable) with the low bits as flags (Uninitialized=1, HasPreInit=2). */
typedef struct PuckReadyToRunHeader
{
    unsigned int   Signature;        /* 'RTR\0' == 0x00525452 */
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

#define PUCK_RTR_GCSTATICREGION 201
#define PUCK_GCSTATIC_UNINITIALIZED 0x1u
#define PUCK_GCSTATIC_HASPREINIT    0x2u
#define PUCK_GCSTATIC_MASK          0x3u

/* NativeAOT relative pointer: a 32-bit signed self-relative displacement. */
static void *Puck_ReadRelPtr32(void *address)
{
    return (char *)address + *(int *)address;
}

/* Defined at the bottom of this file (block-memory intrinsics); forward-declared so the GC
 * static pre-init copy below can use it. */
void *memcpy(void *dest, const void *src, unsigned long long count);

void PuckInitGCStatics(void)
{
    PuckReadyToRunHeader *header = &__ReadyToRunHeader;
    PuckModuleInfoRow *rows;
    unsigned int i;

    if (header->Signature != 0x00525452u)
        ExitProcess(0xFEu); /* header layout mismatch — fail loudly rather than corrupt */

    rows = (PuckModuleInfoRow *)((char *)header + sizeof(PuckReadyToRunHeader));

    for (i = 0; i < header->NumberOfSections; i++)
    {
        char *block;
        char *end;

        if (rows[i].SectionId != PUCK_RTR_GCSTATICREGION)
            continue;

        block = (char *)rows[i].Start;
        end = (char *)rows[i].End;

        for (; block < end; block += sizeof(int))
        {
            void *pBlock = Puck_ReadRelPtr32(block);                              /* -> base cell */
            unsigned long long blockAddr = (unsigned long long)Puck_ReadRelPtr32(pBlock); /* MethodTable | flags */

            if (blockAddr & PUCK_GCSTATIC_UNINITIALIZED)
            {
                PuckMethodTable *eeType = (PuckMethodTable *)(blockAddr & ~(unsigned long long)PUCK_GCSTATIC_MASK);
                void *spine = RhpNewFast(eeType); /* zero-initialised */

                /* Pre-initialized GC statics (HASPREINIT): the static's initial field data is a
                 * constant object-image the ILC emitted, and the region slot immediately after
                 * the base cell (pBlock + sizeof(int)) is a relptr to it. The blob is the full
                 * field region — including reference fields, which are linker-relocated absolute
                 * pointers to frozen objects — so we copy it verbatim over the zeroed spine.
                 *
                 * Destination/size mirror NativeAOT's GetRawData / GetRawObjectDataSize: field
                 * data starts after the MethodTable* header (spine + sizeof(void*)) and spans
                 * BaseSize - 2*sizeof(void*) bytes (the object header + MethodTable* are not part
                 * of the field data). No GC, so the managed RhBulkMoveWithWriteBarrier reduces to
                 * a plain copy.
                 *
                 * Reachability: not observed with this toolchain config. The stock ILC FULLY
                 * freezes an all-constant static (its spine never enters this UNINITIALIZED
                 * branch), and for a static whose spine must be built at runtime it runs the
                 * whole .cctor at runtime (initializing every field in IL) rather than emitting a
                 * partial pre-init blob. HASPREINIT appears to require a config the ILC doesn't
                 * pick here (e.g. frozen objects disabled). This is therefore a correctness
                 * safety-net, kept faithful to StartupCodeHelpers.InitializeStatics so it is
                 * correct if/when the ILC does emit it. */
                if (blockAddr & PUCK_GCSTATIC_HASPREINIT)
                {
                    void *preInit = Puck_ReadRelPtr32((char *)pBlock + sizeof(int));
                    memcpy((char *)spine + sizeof(void *), preInit, eeType->BaseSize - 2 * sizeof(void *));
                }

                *(void **)pBlock = spine; /* patch base cell with the spine pointer */
            }
        }
    }
}

/* The binary's real entry point: initialize GC statics, enter managed code, then terminate
 * the whole process. We must ExitProcess rather than return: returning only exits the main
 * thread (via the OS thread thunk), so any background threads a P/Invoke'd library spun up
 * (e.g. a Vulkan driver's worker threads) would keep the process alive. */
extern int __managed__Main(void);
void PuckStart(void)
{
    PuckInitGCStatics();
    ExitProcess((unsigned int)__managed__Main());
}

/* Block-memory intrinsics. RyuJIT lowers struct copies, stackalloc zero-init, array
 * initialization, cpblk/initblk, etc. to calls to these. MSVC normally supplies them from
 * vcruntime.lib, which a freestanding link does not pull, so define them here.
 *
 * #pragma function stops MSVC from turning the copy/fill loops below back into calls to
 * these same intrinsics (which would recurse infinitely). size_t is 8 bytes on Win64.
 *
 * The prototypes must be visible before #pragma function, otherwise MSVC does not yet
 * recognise the names as the intrinsics it is being told to suppress (warning C4164). We
 * declare them ourselves rather than pull in <string.h> to stay freestanding (no CRT). */
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
