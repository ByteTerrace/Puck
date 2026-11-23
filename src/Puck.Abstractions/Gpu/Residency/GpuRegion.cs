using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// A block of bytes the host keeps and GPU work reads, resident under one <see cref="GpuResidencyPolicy"/>. The region
/// holds the host's copy of its bytes (<see cref="Contents"/>); <see cref="Write"/> changes it and owes only the words
/// that differ, <see cref="Flush"/> sends one frame slot what that slot owes, <see cref="RecordCopy"/> records the
/// staged policy's copy, and <see cref="Buffer"/> is the buffer that slot's GPU work binds. Under every policy that
/// buffer holds <see cref="Contents"/> once the slot is flushed and its copy has run, so a reader cannot tell the
/// policies apart by what it reads.
/// <para>
/// A slot is flushed only after the submission that last used it has retired.
/// <see cref="GpuResidencyPolicy.Ring"/> gives each slot its own host-visible buffer and writes each only what it is
/// behind by. <see cref="GpuResidencyPolicy.InPlace"/> writes the one host-visible buffer every slot binds, so it is
/// flushed only when no submission that reads the region is in flight. <see cref="GpuResidencyPolicy.Staged"/> writes
/// the owed words into the slot's block of a host-visible staging ring, and one dispatch of the copy kernel moves them into the
/// one device-local buffer every slot binds; the caller orders that dispatch after earlier readers of the buffer and
/// before this frame's, as it orders any compute write.
/// </para>
/// <para>
/// A staged region's staging is sized by use, not capacity: each slot keeps a small request buffer (the copy's header
/// and run table), and every slot's flush takes a block of one host-visible ring for the words it owes. A slot's block
/// stays readable until that slot's next flush, which the contract above makes the point its last submission is known
/// retired, and a ring the region replaced (to grow past its room, or to shrink once it holds far more than the region
/// stages) is released once every block in it is. A staged region that owns its destination starts it cleared on the
/// GPU (<see cref="RecordCarry"/>) rather than staging its zeroed host copy.
/// </para>
/// <para>
/// A staged region's destination is either its own device-local buffer or an external one the owner keeps
/// (<see cref="GpuRegion(IGpuBuffer, long, int, IGpuBufferFactory, IGpuBindings, IGpuRecorder, IGpuComputePipeline, in GpuObjectName, GpuRegionCopySets, IAllocationObserver)"/>):
/// its word 0 lands at the word <see cref="Target"/> names, and since other work may write the destination, a
/// retargeted region owes every word written after it.
/// </para>
/// <para>
/// A staged region that owns its destination grows (<see cref="Grow"/>) without waiting for its readers: it creates a
/// larger destination, and its next copy recording clears it and copies the old destination's bytes into it on the GPU
/// (<see cref="RecordCarry"/>), so growing writes no carried word from the host and leaves the old destination to the
/// caller, who disposes it once that recording's submission and every earlier reader have completed. A ring or in-place
/// region grows too, by replacing its host-visible buffers and owing each the whole region, so an owner grows a region
/// without knowing which policy <see cref="GpuResidency.Select"/> chose for it; a region created from the device's
/// profile chooses again at each new size, so growth is the one door that moves a region between policies. The host copy lives in native memory, so a
/// large region holds no managed copy of its contents, including when it grows.
/// </para>
/// <para>
/// Such a region can keep a host copy of its leading bytes alone and take the rest from an owner that already holds those
/// words in arrays of its own (<see cref="Shadow"/>, <see cref="IGpuRegionSource"/>): a write there owes its words
/// without comparing them (<see cref="OweSourced"/>), and a flush asks the owner for every owed word past the copy, so a
/// region of baked meshes holds no second copy of them on the host.
/// </para>
/// <para>
/// The copy kernel reads requests at <see cref="CopyRequestBinding"/>, data at <see cref="CopySourceBinding"/>, and writes the destination at
/// <see cref="CopyDestinationBinding"/>, <see cref="CopyWorkgroupSize"/> threads per group, and takes no push constants:
/// the request buffer states its copy. Its first <see cref="CopyHeaderWords"/> uints are <c>(count, runCount,
/// blockBase, destinationBase, rowWords, sourceStride, destinationStride)</c>, then come <c>runCount</c> <c>(region word, first thread)</c> pairs, and the block
/// starts at <c>blockBase</c> of the source (the ring), holding the owed runs packed one after another. Thread <c>t</c> (its dispatch row times
/// <see cref="CopyRowThreads"/> plus its column, <see cref="CopyGroups"/> sizing the dispatch) copies every block word
/// <c>i</c> below <c>count</c> from <c>t</c> in steps of the dispatch's threads (<see cref="CopyThreads"/>): it finds the
/// last run whose first thread is at most <c>i</c>, takes <c>word</c> as that run's region word plus <c>i</c> minus its
/// first thread, and copies <c>destination[destinationBase + word] = source[blockBase + i]</c>. A ring block
/// therefore holds only what its flush owes. That kernel is <c>Puck.Shaders</c>'
/// <c>region-copy.comp</c>, created from <see cref="CopyPipeline"/> once per device and leased by every owner
/// (<c>GpuRegionCopyPass</c>, an entry of the pass-pipeline cache). Its copy sets are its share of a <see cref="GpuRegionCopyPool"/>: one the region
/// creates for itself alone, or its owner's, reserved for all its regions when the owner was admitted, so a region the
/// owner creates at a later frame takes no descriptor range.
/// </para>
/// </summary>
public sealed unsafe class GpuRegion : IDisposable {
    // The host-visible buffers a ring or in-place region replaced by growing, which its owner disposes together.
    private sealed class ReplacedBuffers(IGpuBuffer[] buffers) : IDisposable {
        public void Dispose() {
            foreach (var buffer in buffers) {
                buffer.Dispose();
            }
        }
    }

    /// <summary>The copy kernel's binding for the destination buffer it writes.</summary>
    public const uint CopyDestinationBinding = 1U;
    /// <summary>The uints of the shared packed/strided copy request header.</summary>
    public const int CopyHeaderWords = 7;
    /// <summary>The copy kernel's binding for the request header and run table.</summary>
    public const uint CopyRequestBinding = 0U;
    /// <summary>The copy kernel's binding for source data, which packed regions bind to their request buffer too.</summary>
    public const uint CopySourceBinding = 2U;
    /// <summary>The copy kernel's threads per group.</summary>
    public const uint CopyWorkgroupSize = 64U;
    /// <summary>The most groups one dispatch dimension carries on both backends (Vulkan's guaranteed
    /// <c>maxComputeWorkGroupCount</c>, Direct3D 12's <c>D3D12_CS_DISPATCH_MAX_THREAD_GROUPS_PER_DIMENSION</c>). A copy
    /// of more words than one row of that many groups carries dispatches further rows in its second dimension, and the
    /// kernel numbers its threads row by row, <see cref="CopyRowThreads"/> to a row.</summary>
    public const uint CopyMaxGroupsPerDimension = 65_535U;
    /// <summary>The threads in one row of a copy's dispatch: <see cref="CopyMaxGroupsPerDimension"/> groups of
    /// <see cref="CopyWorkgroupSize"/>.</summary>
    public const uint CopyRowThreads = (CopyMaxGroupsPerDimension * CopyWorkgroupSize);
    /// <summary>The most rows one copy dispatches: Direct3D 12's GPU-based validation refuses a dispatch of more than
    /// 4,194,303 groups, and 64 rows of <see cref="CopyMaxGroupsPerDimension"/> groups is the most whole rows within it.
    /// A copy of more words than those threads has each thread copy several, one dispatch's threads apart.</summary>
    public const uint CopyMaxRows = 64U;
    /// <summary>The most separate word ranges one copy carries; past it neighbouring ranges pair up and the words
    /// between them are copied again.</summary>
    public const int MaxCopyRuns = 256;
    /// <summary>The value of a packed request's sixth word (<c>sourceStride</c>, which a packed copy leaves zero) that
    /// makes it a move within the destination (<see cref="TryOweMove"/>): each run's words are read from the destination
    /// itself, from the run's source word, and the source words, one per run, follow the run table, each counted from
    /// <c>destinationBase</c> as the run's region word is.</summary>
    public const uint CopyMoveMode = 1U;
    /// <summary>The value of a packed request's sixth word that makes it a gather (<see cref="TryOweGather"/>): each run's
    /// words are read from the source binding from <c>sourceBase</c> plus the run's source word, which follow the run table
    /// as a move's do.</summary>
    public const uint CopyGatherMode = 2U;
    /// <summary>The most moves a region owes at once (<see cref="TryOweMove"/>): one dispatch's run table.</summary>
    public const int MaxMoves = MaxCopyRuns;

    // A slot's request buffer, in uints: the header and the run-table reserve of two uints per run. A flush whose
    // clipped runs pass the reserve grows its slot's buffer.
    private const int CopyBlockBase = (CopyHeaderWords + (MaxCopyRuns * 2));
    private const ulong RequestReserveBytes = (((ulong)CopyBlockBase) * sizeof(uint));
    // Clipping can split each coalesced run at every native page. Reserve that worst-case request size in the
    // address ceiling, not in each staging allocation; StageOwed allocates only its actual request table.
    private const long MaxCopyHeaderBytes = ((CopyBlockBase * 4L) + (((uint.MaxValue / GpuBufferPages.MinimumPageBytes) + 1L) * 8L));
    // The most runs one request's header and run table hold within MaxCopyHeaderBytes.
    private const int MostRequestRuns = ((int)(((MaxCopyHeaderBytes / sizeof(uint)) - CopyHeaderWords) / 2));
    // The most separate owed ranges a host-written buffer holds before neighbours pair up.
    private const int HostRunCapacity = 8;

    /// <summary>The largest gap of unchanged words a staged region's owed runs join across: a run-table entry costs two
    /// words, so copying a gap of up to two costs no more than another run.</summary>
    public const int StagedMergeGapWords = 2;
    /// <summary>The most bytes a region holds: its destination and its staging ring each bind as one storage view, and
    /// Vulkan states a storage view's range (<c>maxStorageBufferRange</c>) in a uint32, so it is the whole uints below
    /// 4 GiB less the most a request's header and run table take. Every word then fits the copy kernel's uint indices and
    /// the region's own int word counts.</summary>
    public const long MaxByteCount = ((((long)(uint.MaxValue / sizeof(uint))) * sizeof(uint)) - MaxCopyHeaderBytes);

    // The most bytes one host write moves: a span's length is an int, so a longer run is written in pieces.
    private const int WriteChunkBytes = (1 << 30);

    /// <summary>The bytes a staged region's ring keeps however little it stages: 1 MiB, or every slot's whole update of a
    /// region small enough. A ring holding more than four times what its live blocks and the next flush take, and more
    /// than this, is replaced by one of twice that, at least this, and one left with no live block is released, so one
    /// flush of a whole large region holds its host memory only until its block is released.</summary>
    public const ulong StagingFloorBytes = (1UL << 20);

    // The host's copy of the region's first ShadowBytes bytes, native memory the region frees when disposed; the words past
    // it are m_source's.
    private byte* m_contents;
    private long m_shadowBytes;

    // Told of every change to the host copy's storage, outside the native allocator (MemoryBacking.NativeDirect).
    private readonly IAllocationObserver? m_hostCopies;

    private IGpuRegionSource? m_source;
    // Whether the words past the host copy are an owner's: a source holds them, or an exact source was handed over and
    // let go after its upload (RecordCopy), which leaves the host copy where it stands.
    private bool Sourced => ((m_source is not null) || m_exactSource);

    private readonly IGpuComputePipeline? m_copyPipeline;

    // The staged policy's copy sets: its share of its owner's reserved pool, or of its own; null under the other
    // policies. MoveCopySets hands a reserved region another share.
    private GpuRegionCopySets? m_copySets;

    // The share its owner reserved, whatever policy the region holds now, which a selecting region stages through once
    // it grows past a ring's share (Restage); null when the owner reserved none.
    private readonly GpuRegionCopySets? m_reservedCopySets;
    // The device profile a selecting region chooses its policy from at every size (GpuResidency.Select), and whether a
    // reader is in flight while the host writes it; null for a region created under a fixed policy.
    private readonly GpuMemoryProfile? m_profile;
    private readonly bool m_readersInFlight;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuBufferFactory m_buffers;
    private readonly GpuObjectName m_name;
    // The name a staged region's staging buffers take: the region's, unless its owner names them apart.
    private readonly GpuObjectName m_stagingName;

    // The name a slot's host buffer is created under: a staged region's is its staging, any other's is the region.
    private GpuObjectName HostName(int slot) => ((Policy == GpuResidencyPolicy.Staged) ? m_stagingName : m_name).At(index: slot);

    private readonly GpuBufferUsage m_usage;
    // What a staged region's own device-local destination declares, at creation, growth and shrink alike.
    private readonly GpuBufferUsage m_destinationUsage;

    // The destination a staged region's copies write; replaced when the region grows.
    private IGpuBuffer? m_destination;
    private GpuBufferPages? m_pages;
    private ulong m_pageRevision;

    // The moves within the destination (TryOweMove) and the gathers from an owner's packed buffer (TryOweGather) owed until
    // the next recorded copy, in words from the destination's base; null until reserved (ReserveRunCopies).
    private GpuRegionRunCopy? m_moves;
    private GpuRegionRunCopy? m_gathers;

    // Exact, so carrying unmapped debt never joins it across words an exact source was not owed.
    private readonly GpuUploadRuns m_deferredCopies = new(MaxCopyRuns, exact: true);
    private readonly List<(int Start, int Length, GpuBufferPages.Lease Lease)> m_copyRuns = [];

    /// <summary>Asks this destination for proof that an entire byte range can be written. The proof stays valid
    /// across an ordered contiguous carry, but not page revocation, shrink or device replacement.</summary>
    /// <param name="offset">The first destination byte.</param>
    /// <param name="bytes">The positive byte count.</param>
    /// <returns>A destination-issued lease, or null while any page is unavailable.</returns>
    public GpuBufferPages.Lease? TryLease(long offset, long bytes) =>
        (((offset >= 0) && (bytes > 0) && (offset <= ByteCount) && (bytes <= (ByteCount - offset))) ? m_pages?.TryLease(bytes: bytes, offset: (offset + (((long)m_destinationWord) * sizeof(uint)))) : null);

    /// <summary>Gets the current destination's writable-page revision; zero for a host-visible policy.</summary>
    public ulong PageRevision => (m_pages?.Revision ?? 0);

    // Every region's content revisions come from one process-wide sequence, so no two contents of any regions share one.
    private static long ContentRevisions;
    private long m_contentRevision = Interlocked.Increment(location: ref ContentRevisions);

    /// <summary>Gets a revision of <see cref="Contents"/> that moves whenever a write changes its bytes or its size
    /// (<see cref="Write"/> owing a difference, <see cref="WriteRuns(ReadOnlySpan{uint}, GpuUploadRuns)"/> writing a run, a growth, shrink or shadow), and
    /// is never shared with another state of this or any other region's contents, so a reader that folds it in place of
    /// the bytes sees every change. A write that leaves the bytes as they were does not move it.</summary>
    public long ContentRevision => m_contentRevision;

    // Native paged buffers own their authority. A committed destination has no mapping transitions; this
    // region's all-writable authority can preserve its proofs across an ordered contiguous carry.
    private void SetDestinationPages(IGpuBuffer destination, bool carried = false) {
        if ((destination is not IGpuPagedBuffer) && carried && (m_pages is not null)) {
            m_pages.ResizeCarried(bytes: ((long)destination.SizeBytes));
            return;
        }
        var pages = ((destination is IGpuPagedBuffer paged) ? paged.Pages : new GpuBufferPages(((long)destination.SizeBytes)));

        if (ReferenceEquals(objA: m_pages, objB: pages)) { return; }
        m_pages?.Revoke();
        m_pages = pages;
        m_pageRevision = pages.Revision;
    }

    // The destination a grown region's next copy recording carries its bytes from, and how many; null when none is owed.
    private IGpuBuffer? m_carrySource;
    private long m_carryBytes;

    private readonly bool m_external;

    // The copy pool the region created for itself, which it destroys with it; null when its owner reserved its sets.
    // The policy-dependent state below is replaced together, only by the constructor and Restage.
    private GpuRegionCopyPool? m_ownedCopyPool;

    private readonly List<IGpuBuffer> m_ownedBuffers = [];

    // The ranges each host-visible buffer still owes: one list under InPlace and Staged, one per slot under Ring.
    private GpuUploadRuns[] m_owed;
    // Under Ring, the ranges written since the slots last took them (OwedOf): a write owes into this one list, in the
    // order it writes, rather than into every slot's, where it would land among the older frames' runs. Every read of a
    // slot's list folds it first.
    private GpuUploadRuns? m_fresh;

    private readonly IGpuRecorder m_recorder;

    // The header and run table a staged flush writes into the slot's request buffer.
    private uint[] m_header;
    private IGpuStorageBuffer[] m_hostBuffers;
    private int m_destinationWord;
    private bool m_disposed;
    // The leading words the destination is known to hold as Contents once every owed word has reached it: the whole
    // region for its own buffers, which start owing everything, and none for an external destination until a write
    // after the last retarget covers them.
    private int m_knownWords;

    // The slot whose request and ring block hold every owed word, or -1 when a write has owed more since.
    private int m_stagedSlot = -1;

    // The staged policy's staging ring, null until a flush owes words, and the byte its last block ended at. Every slot's
    // live block (m_blocks) lies in it or in a ring it replaced (m_retiredRings), which is disposed when its last block is
    // released. A block is released only at its own slot's next flush, the point that slot's last submission is known
    // retired: the one rule keeping a ring's bytes unwritten and undisposed while a submitted copy can still read them.
    private IGpuStorageBuffer? m_ring;
    private long m_ringHead;
    private readonly RingBlock[] m_blocks;
    private readonly List<RetiredRing> m_retiredRings = [];
    // Whether a staged region's own destination owes a clear on the GPU, which stands for its zeroed host copy.
    private bool m_clearOwed;
    // Whether the source answers only the words it was owed (Shadow's exact), so the owed runs join across nothing else.
    private bool m_exactSource;

    // A slot's block of a ring: its bytes from Offset, or none while Ring is null.
    private readonly record struct RingBlock(IGpuStorageBuffer? Ring, long Offset, long Bytes);
    // A ring the region replaced and the blocks of it still live.
    private sealed class RetiredRing(IGpuStorageBuffer ring, int live) {
        public IGpuStorageBuffer Ring { get; } = ring;
        public int Live { get; set; } = live;
    }

    /// <summary>Initializes a new instance of the <see cref="GpuRegion"/> class, creating its buffers and, under the
    /// staged policy, its device-local buffer and, unless its owner reserved them, a copy pool of one set per slot.
    /// Every buffer starts owing the whole region, since a new buffer's contents are undefined, and
    /// <see cref="Contents"/> starts zeroed.</summary>
    /// <param name="policy">The residency policy, normally <see cref="GpuResidency.Select"/>'s choice.</param>
    /// <param name="memory">Where the ring's or in-place buffer lives, normally <see cref="GpuResidency.RingMemory"/>'s
    /// choice; a staged region's staging buffers are host memory whatever it says.</param>
    /// <param name="byteCount">The region's size in bytes; positive, a whole number of uints and at most
    /// <see cref="MaxByteCount"/>.</param>
    /// <param name="slotCount">The caller's frame slots; at least one.</param>
    /// <param name="buffers">The factory that creates the host-visible and device-local buffers.</param>
    /// <param name="bindings">The bindings service the staged policy's copy sets come from.</param>
    /// <param name="recorder">The recorder the staged policy's copy is recorded through.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, created from <see cref="CopyPipeline"/>; required under the
    /// staged policy and read only there, so <see langword="null"/> under any other. The caller owns it and keeps it alive
    /// while the region records copies.</param>
    /// <param name="name">The region's debug name, from its creator's identity: each slot's host-visible buffer and copy
    /// set is named at its slot's index, and the device-local buffer and the copy pool bare.</param>
    /// <param name="copySets">The region's share of the copy pool its owner reserved (<see cref="GpuRegionCopyPool.Region"/>),
    /// which a staged region writes in place of creating a pool of its own, or <see langword="null"/> to create one
    /// (named by <paramref name="name"/>); read only under the staged policy. The owner keeps the pool after the region
    /// is disposed.</param>
    /// <param name="usage">How GPU work reads the buffer a slot binds: <see cref="GpuBufferUsage.Storage"/>, read as
    /// uints, or <see cref="GpuBufferUsage.Uniform"/>, bound as a constant buffer of the whole region, which then holds
    /// a multiple of <see cref="IGpuBindings.ConstantBufferAlignment"/> bytes and is never staged, since the staged
    /// policy's copy kernel writes its buffer as storage.</param>
    /// <param name="hostCopies">The observer told of every change to the region's host copy, native storage outside the
    /// engine's allocator (<see cref="MemoryBacking.NativeDirect"/>), or <see langword="null"/> for none. It must outlive
    /// the region.</param>
    /// <param name="staging">The name a staged region's staging buffers are created under, when they are another
    /// owner's memory than its destination: transfer storage beside storage that holds what is drawn. Each slot's
    /// buffer takes the slot as its index. <see langword="null"/> names them as the region.</param>
    /// <param name="destinationUsage">The usages a staged region's device-local destination declares beside
    /// <see cref="GpuBufferUsage.Storage"/>, at creation and at every growth and shrink: the mesh region's
    /// <see cref="GpuBufferUsage.AccelerationStructureInput"/>, which acceleration-structure builds read in place.</param>
    /// <param name="mapped">Whether a staged region's destination reserves the most a region holds
    /// (<see cref="MaxByteCount"/>) and backs only the chunks its size reaches
    /// (<see cref="IGpuBufferFactory.TryCreateMapped"/>), so <see cref="Grow"/> maps chunks in place and
    /// <see cref="Shrink"/> gives them back without a replacement. A device that has no such buffer gives the region an
    /// ordinary destination; <see cref="Mapped"/> reads which it has.</param>
    /// <param name="hostCopyBytes">The bytes of a staged region's host copy from its start, for an owner that hands the
    /// words past it to a source (<see cref="Shadow"/>) before it writes or owes any of them, so the region never holds a
    /// whole host copy it would give up at once; <see langword="null"/> for the whole region.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffers"/>,
    /// <paramref name="bindings"/> or <paramref name="recorder"/> is <see langword="null"/>, or a staged region has no
    /// <paramref name="copyPipeline"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> or <paramref name="memory"/> is not a
    /// defined value, <paramref name="byteCount"/> is not positive, not a whole number of uints or past
    /// <see cref="MaxByteCount"/>,
    /// <paramref name="slotCount"/> is not positive, <paramref name="usage"/> is neither storage nor uniform, a
    /// uniform region is staged or not a multiple of <see cref="IGpuBindings.ConstantBufferAlignment"/> bytes, or
    /// <paramref name="hostCopyBytes"/> is given for a region that is not staged, is negative, not a whole number of
    /// uints or past <paramref name="byteCount"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, long byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline? copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null, GpuBufferUsage usage = GpuBufferUsage.Storage, IAllocationObserver? hostCopies = null, GpuObjectName? staging = null, GpuBufferUsage destinationUsage = GpuBufferUsage.None, bool mapped = false, long? hostCopyBytes = null) : this(
        bindings: bindings,
        buffers: buffers,
        byteCount: byteCount,
        copyPipeline: copyPipeline,
        copySets: copySets,
        hostCopies: hostCopies,
        destination: null,
        memory: memory,
        name: in name,
        policy: policy,
        recorder: recorder,
        slotCount: slotCount,
        staging: staging,
        usage: usage,
        destinationUsage: destinationUsage,
        mapped: mapped,
        hostCopyBytes: hostCopyBytes
    ) { }
    /// <summary>Initializes a new instance of the <see cref="GpuRegion"/> class that chooses its own residency: the
    /// policy <see cref="GpuResidency.Select"/> picks for its size on <paramref name="profile"/>, a ring's buffers in the
    /// memory <see cref="GpuResidency.RingMemory"/> picks. <see cref="Grow"/> chooses again for each new size, so a
    /// region that grows past a ring's share of the aperture moves to the staged policy without its owner selecting
    /// anything. Its buffers are storage, read as uints.</summary>
    /// <param name="profile">The device's memory profile.</param>
    /// <param name="readersInFlight">Whether a submission reading the region can be in flight while the host writes
    /// it, as every per-frame owner's ring keeps one.</param>
    /// <param name="byteCount">The region's size in bytes; positive, a whole number of uints and at most
    /// <see cref="MaxByteCount"/>.</param>
    /// <param name="slotCount">The caller's frame slots; at least one.</param>
    /// <param name="buffers">The factory that creates the host-visible and device-local buffers.</param>
    /// <param name="bindings">The bindings service the staged policy's copy sets come from.</param>
    /// <param name="recorder">The recorder the staged policy's copy is recorded through.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, created from <see cref="CopyPipeline"/>, which the region
    /// stages through whenever its size selects the staged policy; the caller owns it and keeps it alive while the region
    /// records copies.</param>
    /// <param name="name">The region's debug name, as the policy constructor names it.</param>
    /// <param name="copySets">The region's share of the copy pool its owner reserved (<see cref="GpuRegionCopyPool.Region"/>),
    /// written whenever the region is staged, or <see langword="null"/> to create a pool of its own when it is.</param>
    /// <param name="hostCopies">The observer told of every change to the region's host copy, native storage outside the
    /// engine's allocator (<see cref="MemoryBacking.NativeDirect"/>), or <see langword="null"/> for none. It must outlive
    /// the region.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffers"/>, <paramref name="bindings"/>,
    /// <paramref name="recorder"/> or <paramref name="copyPipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not positive, not a whole number of
    /// uints or past <see cref="MaxByteCount"/>, or <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(GpuMemoryProfile profile, bool readersInFlight, long byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null, IAllocationObserver? hostCopies = null) : this(
        bindings: bindings,
        buffers: buffers,
        byteCount: byteCount,
        copyPipeline: (copyPipeline ?? throw new ArgumentNullException(paramName: nameof(copyPipeline))),
        copySets: copySets,
        hostCopies: hostCopies,
        destination: null,
        memory: GpuResidency.RingMemory(profile: profile),
        name: in name,
        policy: ((byteCount > 0)
            ? GpuResidency.Select(
                byteCount: ((ulong)byteCount),
                profile: profile,
                readersInFlight: readersInFlight
            )
            : GpuResidencyPolicy.Staged),
        recorder: recorder,
        slotCount: slotCount,
        usage: GpuBufferUsage.Storage
    ) {
        m_profile = profile;
        m_readersInFlight = readersInFlight;
    }
    /// <summary>Initializes a new instance of the <see cref="GpuRegion"/> class that stages into an external
    /// destination: its policy is <see cref="GpuResidencyPolicy.Staged"/>, its word 0 lands at word 0 of
    /// <paramref name="destination"/> until <see cref="Target"/> moves it, and it owes nothing until a write, since what
    /// the destination holds is the owner's. <see cref="Contents"/> starts zeroed.</summary>
    /// <param name="destination">The device-local buffer the copy writes; the caller owns it and keeps it alive while the
    /// region records copies.</param>
    /// <param name="byteCount">The region's size in bytes; positive, a whole number of uints and at most
    /// <see cref="MaxByteCount"/>.</param>
    /// <param name="slotCount">The caller's frame slots; at least one.</param>
    /// <param name="buffers">The factory that creates the staging buffers.</param>
    /// <param name="bindings">The bindings service the copy sets come from.</param>
    /// <param name="recorder">The recorder the copy is recorded through.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, created from <see cref="CopyPipeline"/>; the caller owns
    /// it and keeps it alive while the region records copies.</param>
    /// <param name="name">The region's debug name, from its creator's identity: each slot's staging buffer and copy set
    /// is named at its slot's index, and the copy pool bare; the destination keeps its owner's name.</param>
    /// <param name="copySets">The region's share of the copy pool its owner reserved (<see cref="GpuRegionCopyPool.Region"/>),
    /// which it writes in place of creating a pool of its own, or <see langword="null"/> to create one (named by
    /// <paramref name="name"/>). The owner keeps the pool after the region is disposed.</param>
    /// <param name="hostCopies">The observer told of every change to the region's host copy, native storage outside the
    /// engine's allocator (<see cref="MemoryBacking.NativeDirect"/>), or <see langword="null"/> for none. It must outlive
    /// the region.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/>, <paramref name="buffers"/>,
    /// <paramref name="bindings"/>, <paramref name="recorder"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not positive, not a whole number
    /// of uints or past <see cref="MaxByteCount"/>, or <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(IGpuBuffer destination, long byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null, IAllocationObserver? hostCopies = null) : this(
        bindings: bindings,
        buffers: buffers,
        byteCount: byteCount,
        copyPipeline: copyPipeline,
        copySets: copySets,
        hostCopies: hostCopies,
        destination: (destination ?? throw new ArgumentNullException(paramName: nameof(destination))),
        memory: GpuHostVisibleMemory.Host,
        name: in name,
        policy: GpuResidencyPolicy.Staged,
        recorder: recorder,
        slotCount: slotCount,
        usage: GpuBufferUsage.Storage
    ) { }

    private GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, long byteCount, int slotCount, GpuBufferUsage usage, IGpuBuffer? destination, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline? copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets, IAllocationObserver? hostCopies, GpuObjectName? staging = null, GpuBufferUsage destinationUsage = GpuBufferUsage.None, bool mapped = false, long? hostCopyBytes = null) {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxByteCount,
            value: byteCount
        );
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: slotCount);

        if (
            (copySets is not null) &&
            (copySets.SlotCount != slotCount)
        ) {
            throw new ArgumentException(
                message: $"The reserved copy sets hold {copySets.SlotCount} slot(s), but the region has {slotCount}.",
                paramName: nameof(copySets)
            );
        }
        if ((byteCount % sizeof(uint)) != 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: byteCount,
                message: "A region is a whole number of uints.",
                paramName: nameof(byteCount)
            );
        }

        if (!Enum.IsDefined(value: policy)) {
            throw new ArgumentOutOfRangeException(
                actualValue: policy,
                message: "The residency policy is not defined.",
                paramName: nameof(policy)
            );
        }

        if (!Enum.IsDefined(value: memory)) {
            throw new ArgumentOutOfRangeException(
                actualValue: memory,
                message: "The host-visible memory is not defined.",
                paramName: nameof(memory)
            );
        }

        if (usage is not (GpuBufferUsage.Storage or GpuBufferUsage.Uniform)) {
            throw new ArgumentOutOfRangeException(
                actualValue: usage,
                message: "A region's buffers are storage or uniform.",
                paramName: nameof(usage)
            );
        }
        if (usage == GpuBufferUsage.Uniform) {
            if (policy == GpuResidencyPolicy.Staged) {
                throw new ArgumentOutOfRangeException(
                    actualValue: policy,
                    message: "A uniform region is never staged: the copy kernel writes its buffer as storage.",
                    paramName: nameof(policy)
                );
            }
            IGpuBindings.RequireConstantBufferSize(bufferSize: ((ulong)byteCount), maximumBytes: bindings.MaxConstantBufferBytes);
        }
        if (
            (policy == GpuResidencyPolicy.Staged) &&
            (copyPipeline is null)
        ) {
            throw new ArgumentNullException(
                message: "A staged region copies through the copy kernel's pipeline.",
                paramName: nameof(copyPipeline)
            );
        }

        var hostBytes = (hostCopyBytes ?? byteCount);

        if (
            (hostCopyBytes is not null) &&
            ((policy != GpuResidencyPolicy.Staged) || (hostBytes < 0) || (hostBytes > byteCount) || ((hostBytes % sizeof(uint)) != 0))
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: hostCopyBytes,
                message: "A staged region's host copy holds a whole number of uints within the region.",
                paramName: nameof(hostCopyBytes)
            );
        }

        var words = ((int)(byteCount / sizeof(uint)));

        ByteCount = byteCount;
        Memory = ((policy == GpuResidencyPolicy.Staged)
            ? GpuHostVisibleMemory.Host
            : memory
        );
        Policy = policy;
        SlotCount = slotCount;
        m_hostCopies = hostCopies;
        m_copyPipeline = copyPipeline;
        m_bindings = bindings;
        m_buffers = buffers;
        m_name = name;
        m_stagingName = (staging ?? name);
        m_usage = usage;
        m_destinationUsage = (GpuBufferUsage.Storage | destinationUsage);
        m_destination = destination;
        m_external = (destination is not null);
        m_reservedCopySets = copySets;
        m_recorder = recorder;
        m_header = HeaderOf(policy: policy);
        // A staged region's own destination is cleared on the GPU, which leaves it holding the zeroed host copy, so it
        // owes no word; an external destination is its owner's and owes nothing until written.
        m_clearOwed = ((policy == GpuResidencyPolicy.Staged) && !m_external);
        m_owed = OwedOf(
            policy: policy,
            slotCount: slotCount,
            words: ((policy == GpuResidencyPolicy.Staged)
                ? 0
                : words
            )
        );
        m_blocks = new RingBlock[slotCount];
        m_fresh = FreshOf(policy: policy);
        m_knownWords = (m_external
            ? 0
            : ((int)(hostBytes / sizeof(uint)))
        );
        m_hostBuffers = [];

        try {
            m_contents = AllocateContents(hostBytes, zero: true);
            m_shadowBytes = hostBytes;
            m_hostBuffers = CreateHostBuffers(
                byteCount: byteCount,
                memory: Memory,
                policy: policy
            );
            m_ownedBuffers.AddRange(collection: m_hostBuffers);

            if (policy == GpuResidencyPolicy.Staged) {
                if (!m_external) {
                    m_destination = (mapped ? CreateMapped(byteCount: byteCount) : null) ?? buffers.CreateDeviceLocal(
                        name: name,
                        sizeBytes: ((ulong)byteCount),
                        usage: m_destinationUsage
                    );
                    m_ownedBuffers.Add(item: m_destination);
                }

                SetDestinationPages(m_destination!);

                // No slot's copy set is written here: RecordCopy writes a slot's set the first time that slot records a
                // copy, inside the pass that records it, so the writes are counted where the upload's work is.
                m_copySets = (copySets ?? (m_ownedCopyPool = CreateCopyPool()).Region(index: 0));
            }
        } catch {
            Dispose();

            throw;
        }
    }

    // The header and run table a flush under the policy writes: the staged copy's, and none under the others.
    private static uint[] HeaderOf(GpuResidencyPolicy policy) => new uint[((policy == GpuResidencyPolicy.Staged)
        ? CopyBlockBase
        : 0
    )];
    // The owed lists of a region under the policy, one per slot under the ring and one under the others, each owing its
    // first words.
    private static GpuUploadRuns[] OwedOf(GpuResidencyPolicy policy, int slotCount, int words) {
        var owed = new GpuUploadRuns[((policy == GpuResidencyPolicy.Ring)
            ? slotCount
            : 1
        )];

        for (var index = 0; (index < owed.Length); index++) {
            owed[index] = ((policy == GpuResidencyPolicy.Staged)
                ? new GpuUploadRuns(
                    capacity: MaxCopyRuns,
                    mergeGap: StagedMergeGapWords
                )
                : new GpuUploadRuns(capacity: HostRunCapacity)
            );

            if (words > 0) {
                owed[index].Add(
                    length: words,
                    start: 0
                );
            }
        }

        return owed;
    }
    // The ring's list of ranges written since the slots last took them, and none under the other policies.
    private static GpuUploadRuns? FreshOf(GpuResidencyPolicy policy) => ((policy == GpuResidencyPolicy.Ring)
        ? new GpuUploadRuns(capacity: HostRunCapacity)
        : null
    );
    // Creates the host-visible buffers of a region of byteCount bytes under the policy, in memory: one per slot under the
    // ring, one request buffer per slot under the staged policy (the header and run-table reserve; the words go to the
    // ring), and the one in place. Every buffer is created or none is: a failed creation releases those created before it.
    private IGpuStorageBuffer[] CreateHostBuffers(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, long byteCount) {
        var created = new IGpuStorageBuffer[((policy == GpuResidencyPolicy.InPlace)
            ? 1
            : SlotCount
        )];
        var hostBytes = ((policy == GpuResidencyPolicy.Staged)
            ? RequestReserveBytes
            : ((ulong)byteCount)
        );

        try {
            for (var index = 0; (index < created.Length); index++) {
                created[index] = ((memory == GpuHostVisibleMemory.DeviceLocal)
                    ? m_buffers.CreateHostVisibleDeviceLocal(
                        name: HostName(slot: index),
                        sizeBytes: hostBytes,
                        usage: m_usage
                    )
                    : m_buffers.CreateHostVisible(
                        name: HostName(slot: index),
                        sizeBytes: hostBytes,
                        usage: m_usage
                    )
                );
            }
        } catch {
            foreach (var buffer in created) {
                buffer?.Dispose();
            }

            throw;
        }

        return created;
    }
    // Reserves a mapped destination of the most a region holds and backs its first byteCount bytes, or answers null where
    // the device has none. A refused backing releases the reservation before the refusal leaves.
    private IGpuMappedBuffer? CreateMapped(long byteCount) {
        if (m_buffers.TryCreateMapped(name: m_name, reservedBytes: ((ulong)MaxByteCount), usage: m_destinationUsage) is not { } created) {
            return null;
        }
        try {
            created.Map(bytes: ((ulong)byteCount), offset: 0UL);
        } catch {
            created.Dispose();

            throw;
        }

        return created;
    }
    // Creates the copy pool of a staged region whose owner reserved no sets for it: one set per slot, named as the region.
    private GpuRegionCopyPool CreateCopyPool() => new(
        bindings: m_bindings,
        copyPipeline: m_copyPipeline!,
        name: in m_name,
        regions: [m_name],
        slotCount: SlotCount
    );

    /// <summary>Gets the copy kernel's request, destination and source-data descriptor bindings.</summary>
    public static IReadOnlyList<GpuComputeBinding> CopyBindings { get; } = [
        new GpuComputeBinding(
            Binding: CopyRequestBinding,
            Kind: GpuBindingKind.ReadOnlyBuffer
        ),
        new GpuComputeBinding(
            Binding: CopyDestinationBinding,
            Kind: GpuBindingKind.ReadWriteBuffer
        ),
        new GpuComputeBinding(
            Binding: CopySourceBinding,
            Kind: GpuBindingKind.ReadOnlyBuffer
        ),
    ];
    /// <summary>Gets the copy kernel's pipeline description: <see cref="CopyBindings"/>, no push constants, and each
    /// register at its binding number.</summary>
    public static GpuComputePipelineDescription CopyPipeline { get; } = new(
        Bindings: CopyBindings,
        Name: "region-copy",
        PushConstantBinding: null
    );

    /// <summary>Returns the groups a copy of <paramref name="count"/> words dispatches: one row of up to
    /// <see cref="CopyMaxGroupsPerDimension"/> groups, and as many full rows of that width as it takes past one, at most
    /// <see cref="CopyMaxRows"/>.</summary>
    /// <param name="count">The words the copy carries; at least one.</param>
    /// <returns>The groups along the dispatch's first and second dimensions.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero.</exception>
    public static (uint X, uint Y) CopyGroups(uint count) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);

        var groups = ((ulong)count).CeilingDivide(divisor: ((ulong)CopyWorkgroupSize));

        return ((groups <= CopyMaxGroupsPerDimension)
            ? (((uint)groups), 1U)
            : (CopyMaxGroupsPerDimension, ((uint)Math.Min(
                val1: groups.CeilingDivide(divisor: ((ulong)CopyMaxGroupsPerDimension)),
                val2: CopyMaxRows
            )))
        );
    }
    /// <summary>Returns the threads a copy of <paramref name="count"/> words dispatches (<see cref="CopyGroups"/> times
    /// <see cref="CopyWorkgroupSize"/>): the step between the words one thread copies.</summary>
    /// <param name="count">The words the copy carries; at least one.</param>
    /// <returns>The threads.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero.</exception>
    public static uint CopyThreads(uint count) {
        var (x, y) = CopyGroups(count: count);

        return ((x * CopyWorkgroupSize) * y);
    }
    /// <summary>Returns the bytes of the buffers a region that owns its destination holds: one host-visible buffer per
    /// slot under the ring, the one shared buffer in place, and under the staged policy one request buffer per slot, a
    /// staging ring holding one whole update of the region or its floor (<see cref="StagingFloorBytes"/>), whichever is
    /// more (what a flush of the whole region takes; a region staging less holds less), and the device-local destination.
    /// It is each buffer's logical size, not the backend's allocation.</summary>
    /// <param name="policy">The residency policy.</param>
    /// <param name="byteCount">The region's size in bytes; positive.</param>
    /// <param name="slotCount">The owner's frame slots; positive.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is not a defined value, or
    /// <paramref name="byteCount"/> or <paramref name="slotCount"/> is not positive.</exception>
    public static ulong BytesOf(GpuResidencyPolicy policy, long byteCount, int slotCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: byteCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: slotCount);

        return policy switch {
            GpuResidencyPolicy.Ring => checked((((ulong)byteCount) * ((ulong)slotCount))),
            GpuResidencyPolicy.InPlace => ((ulong)byteCount),
            GpuResidencyPolicy.Staged => checked((((RequestReserveBytes * ((ulong)slotCount)) + Math.Max(val1: ((ulong)byteCount), val2: Math.Min(val1: StagingFloorBytes, val2: (((ulong)slotCount) * ((ulong)byteCount))))) + ((ulong)byteCount))),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: policy,
                message: "The residency policy is not defined.",
                paramName: nameof(policy)
            ),
        };
    }

    /// <summary>Gets the bytes of the buffers the region holds, by the memory they live in: its host-visible buffers
    /// (one per slot under the ring, one in place; under the staged policy each slot's request buffer, the staging ring
    /// and any ring it replaced that a block still holds) in the memory <see cref="Memory"/> names, and a staged region's
    /// own device-local destination. A destination the region was handed, or one it replaced by growing, is its owner's,
    /// not counted here. Each is a buffer's logical size.</summary>
    public GpuMemoryBytes OwnedBytes {
        get {
            var host = (m_ring?.SizeBytes ?? 0UL);

            foreach (var buffer in m_hostBuffers) {
                host = checked((host + buffer.SizeBytes));
            }
            foreach (var retired in m_retiredRings) {
                host = checked((host + retired.Ring.SizeBytes));
            }

            // A mapped destination holds its backed chunks, not the address range it reserves.
            var destination = (((Policy == GpuResidencyPolicy.Staged) && !m_external)
                ? (Mapped ? DestinationBytes : ((ulong)ByteCount))
                : 0UL
            );

            return ((Memory == GpuHostVisibleMemory.DeviceLocal)
                ? new GpuMemoryBytes(
                    DeviceLocal: checked((host + destination)),
                    HostVisible: 0UL
                )
                : new GpuMemoryBytes(
                    DeviceLocal: destination,
                    HostVisible: host
                )
            );
        }
    }
    /// <summary>Gets the region's size in bytes, which <see cref="Grow"/> raises.</summary>
    public long ByteCount { get; private set; }
    /// <summary>Gets the host's copy of the region, its first <see cref="ShadowBytes"/> bytes (the whole region unless a
    /// source holds the rest): what every slot's <see cref="Buffer"/> holds there once flushed and copied. The span reads
    /// native memory the region frees when disposed or moves when it grows, so it is read before either. A span counts
    /// its length in an int, so a copy past <see cref="int.MaxValue"/> bytes is read a piece at a time through
    /// <see cref="ContentsAt"/>.</summary>
    /// <exception cref="InvalidOperationException">The host copy holds more bytes than one span reaches.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public ReadOnlySpan<byte> Contents {
        get {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );

            if (m_shadowBytes > int.MaxValue) {
                throw new InvalidOperationException(message: $"The region's {m_shadowBytes} host bytes are past what one span reaches; read it through ContentsAt.");
            }

            return new ReadOnlySpan<byte>(length: ((int)m_shadowBytes), pointer: m_contents);
        }
    }

    /// <summary>Returns <paramref name="length"/> bytes of the host's copy of the region from <paramref name="offset"/>,
    /// as <see cref="Contents"/> holds them. The span reads native memory the region frees when disposed or moves when it
    /// grows, so it is read before either.</summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="length">The bytes.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative,
    /// or the span runs past <see cref="ShadowBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public ReadOnlySpan<byte> ContentsAt(long offset, int length) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return HostSpan(
            length: length,
            offset: offset
        );
    }

    /// <summary>Gets the retained CPU shadow payload bytes: the native host copy's size, <see cref="ShadowBytes"/>.</summary>
    public ulong CpuShadowBytes => ((ulong)m_shadowBytes);
    /// <summary>Gets the bytes of the region, from its start, that the host copy holds: <see cref="ByteCount"/> until a
    /// source takes the words past them (<see cref="Shadow"/>).</summary>
    public long ShadowBytes => m_shadowBytes;
    /// <summary>Gets whether the region's next copy recording clears its destination (a new destination of its own, which
    /// holds the zeroed host copy once cleared) or carries a replaced destination's bytes into a grown one
    /// (<see cref="RecordCarry"/>).</summary>
    public bool OwesCarry => ((m_carrySource is not null) || (m_clearOwed && !m_disposed));
    /// <summary>Gets the pending-use predicate a retirement queue keeps for this region's replaced destinations.
    /// It stays true until a carry is recorded or cancelled. Poll retirement only after recording is submitted.</summary>
    public Func<bool> PendingCarry => (m_pendingCarry ??= () => OwesCarry);
    /// <summary>Gets whether the region's destination is mapped (<see cref="IGpuMappedBuffer"/>): it grows and shrinks in
    /// place, never holding a replacement beside it.</summary>
    public bool Mapped => (m_destination is IGpuMappedBuffer);
    /// <summary>Gets the device bytes the region's destination holds: a mapped destination's backed chunks, or an
    /// ordinary destination's size; zero for a region with no device-local destination of its own.</summary>
    public ulong DestinationBytes => ((m_destination is IGpuMappedBuffer mapped)
        ? mapped.MappedBytes
        : ((m_external || (m_destination is null)) ? 0UL : m_destination.SizeBytes));

    private Func<bool>? m_pendingCarry;

    /// <summary>Gets the retained CPU upload-header scratch payload bytes, excluding managed array overhead.</summary>
    public ulong CpuScratchBytes => (((ulong)m_header.LongLength) * sizeof(uint));
    /// <summary>Gets the handle of the pipeline <see cref="RecordCopy"/> dispatches, or zero for a region that copies
    /// nothing.</summary>
    public nint CopyPipelineHandle => (m_copyPipeline?.Handle ?? 0);
    /// <summary>Gets whether the next <see cref="RecordCopy"/> records a copy: the region is staged and owes words or
    /// moves.</summary>
    public bool OwesCopy => (
        (Policy == GpuResidencyPolicy.Staged) &&
        !m_disposed &&
        ((m_owed[0].Count > 0) || OwesMoves || OwesGathers)
    );
    /// <summary>Gets the bytes the next flush stages and <see cref="RecordCopy"/> copies: a staged region's owed words, and
    /// zero under the other policies.</summary>
    public long OwedCopyBytes {
        get {
            if ((Policy != GpuResidencyPolicy.Staged) || m_disposed) { return 0L; }

            var owed = m_owed[0];
            var words = 0L;

            for (var run = 0; (run < owed.Count); run++) { words += owed.Length(index: run); }
            return (words * sizeof(uint));
        }
    }
    /// <summary>Gets how many flushes of a staged region staged nothing because the account or the device refused their
    /// staging (<see cref="GpuMemoryRefusedException"/>): each left its words owed for the next flush.</summary>
    public long StagingRefusals { get; private set; }
    /// <summary>Gets where the buffers the region's readers bind directly live: its ring's or in-place buffer's memory,
    /// and <see cref="GpuHostVisibleMemory.Host"/> for a staged region, whose staging buffers are host memory.</summary>
    public GpuHostVisibleMemory Memory { get; private set; }
    /// <summary>Gets the residency policy the region holds: the one it was created under, or the staged policy once a
    /// region that chooses its own residency has grown past a ring's share (<see cref="Grow"/>).</summary>
    public GpuResidencyPolicy Policy { get; private set; }
    /// <summary>Gets the number of frame slots the region serves.</summary>
    public int SlotCount { get; }

    /// <summary>Returns the buffer a slot's GPU work binds for the region: the slot's own host-visible buffer under
    /// the ring, the destination under the staged policy, and the one host-visible buffer every slot shares in place.
    /// It is a buffer of the region's usage holding <see cref="ByteCount"/> bytes or more: a storage region reads as uints
    /// from its start, and a uniform region binds whole as a constant buffer; an external destination's layout is its
    /// owner's.</summary>
    /// <param name="slot">The frame slot, below <see cref="SlotCount"/>.</param>
    /// <returns>The buffer; the region owns it unless it is an external destination.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public IGpuBuffer Buffer(int slot) {
        RequireSlot(slot: slot);

        return Policy switch {
            GpuResidencyPolicy.Ring => m_hostBuffers[slot],
            GpuResidencyPolicy.Staged => m_destination!,
            _ => m_hostBuffers[0],
        };
    }
    /// <summary>Releases the region's buffers and, under the staged policy, the copy pool it created; an external
    /// destination and a reserved copy pool stay their owner's. The caller retires every submission that reads the region first. Disposing twice
    /// does nothing.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (!m_external) { m_pages?.Revoke(); }
        m_copySets?.ForgetWriter(region: this);
        m_ownedCopyPool?.Dispose();
        m_moves?.Dispose();
        m_gathers?.Dispose();

        foreach (var buffer in m_ownedBuffers) {
            buffer.Dispose();
        }
        m_ring?.Dispose();
        m_ring = null;
        foreach (var retired in m_retiredRings) {
            retired.Ring.Dispose();
        }
        m_retiredRings.Clear();

        m_ownedBuffers.Clear();
        m_carrySource = null;
        m_clearOwed = false;
        FreeContents(m_contents, m_shadowBytes);
        m_contents = null;
        m_shadowBytes = 0;
    }
    /// <summary>Grows a region that owns its buffers to <paramref name="byteCount"/> bytes, without waiting for any
    /// submission, and extends <see cref="Contents"/> with zeros. A region that chooses its own residency chooses again
    /// for the new size: past a ring's share of the aperture a ring or in-place region becomes staged, creating its
    /// staging buffers, destination and (unless its owner reserved its sets) copy pool, owing every word, and returning
    /// its host-visible buffers as a ring's grow does. Any other region keeps its policy. A staged region with a mapped
    /// destination (<see cref="Mapped"/>) backs the chunks the new size reaches and replaces nothing: every word stays
    /// where it stands, and a region with no source owes its new tail. Any other staged region
    /// creates a destination of the new size and owes its next copy recording a carry (<see cref="RecordCarry"/>) that
    /// clears the new destination and copies the old one's bytes into it on the GPU, so no word the region held is staged
    /// again (a destination still owing its first clear carries nothing: the grown one is cleared whole); owed words stay
    /// owed, the copy sets are written again before each slot's next copy, and no staging is created: the ring grows when a
    /// flush owes more than it has room for. A ring or in-place region creates host-visible buffers of
    /// the new size in the memory <see cref="Memory"/> names, and since a new buffer's contents are undefined, every
    /// buffer owes the whole region, so each slot's next flush writes all of it.</summary>
    /// <param name="byteCount">The new size in bytes; larger than <see cref="ByteCount"/>, a whole number of uints, at
    /// most <see cref="MaxByteCount"/>, and for a uniform region a multiple of
    /// <see cref="IGpuBindings.ConstantBufferAlignment"/>.</param>
    /// <returns>What the region replaced, which the caller now owns: a staged region's destination (nothing, for a mapped
    /// one), or a ring's or in-place region's host-visible buffers. GPU work recorded before the grow may still read it, and a staged region's
    /// next copy recording reads its old destination once more, so the caller disposes it only once every such
    /// submission has completed. A staged region that grows again before a copy recording carries from the destination
    /// it held at its last recording and returns the one it never wrote.</returns>
    /// <exception cref="InvalidOperationException">The region's destination is external.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not larger than
    /// <see cref="ByteCount"/>, not a whole number of uints, past <see cref="MaxByteCount"/>, or for a uniform region
    /// not a multiple of <see cref="IGpuBindings.ConstantBufferAlignment"/> or past the device's view limit.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public IDisposable Grow(long byteCount) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_external) {
            throw new InvalidOperationException(message: "A region with an external destination does not grow; its owner sizes the destination.");
        }
        if (
            (byteCount <= ByteCount) ||
            ((byteCount % sizeof(uint)) != 0) ||
            (byteCount > MaxByteCount)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: byteCount,
                message: $"A region grows to a whole number of uints past its {ByteCount} bytes and within the {MaxByteCount} a region holds.",
                paramName: nameof(byteCount)
            );
        }
        if (m_usage == GpuBufferUsage.Uniform) {
            IGpuBindings.RequireConstantBufferSize(bufferSize: ((ulong)byteCount), maximumBytes: m_bindings.MaxConstantBufferBytes);
        }

        if (Policy == GpuResidencyPolicy.Staged) {
            return GrowDestination(byteCount: byteCount);
        }
        // Select's choice between the ring and in place does not depend on size, and a larger size only ever moves a
        // region to the staged policy, so a selecting region that leaves its policy is restaged.
        if (
            (m_profile is { } profile) &&
            (GpuResidency.Select(
                byteCount: ((ulong)byteCount),
                profile: profile,
                readersInFlight: m_readersInFlight
            ) == GpuResidencyPolicy.Staged)
        ) {
            return Restage(byteCount: byteCount);
        }

        return GrowHostBuffers(byteCount: byteCount);
    }

    // Moves a ring or in-place region to the staged policy at byteCount bytes: creates its staging buffers, its
    // device-local destination and, unless its owner reserved its sets, its copy pool, all before anything changes, then
    // grows the host copy and owes every word, since the new destination holds nothing. Nothing is carried on the GPU: the
    // host copy holds the whole region (a source needs the staged policy from the start). The old host-visible buffers are
    // returned for the caller to retire, as a ring's grow returns them.
    private ReplacedBuffers Restage(long byteCount) {
        var staging = CreateHostBuffers(
            byteCount: byteCount,
            memory: GpuHostVisibleMemory.Host,
            policy: GpuResidencyPolicy.Staged
        );
        IGpuBuffer? destination = null;
        GpuRegionCopyPool? pool = null;

        try {
            destination = m_buffers.CreateDeviceLocal(
                name: m_name,
                sizeBytes: ((ulong)byteCount),
                usage: m_destinationUsage
            );
            pool = ((m_reservedCopySets is null)
                ? CreateCopyPool()
                : null
            );
            GrowContents(byteCount: byteCount);
        } catch {
            pool?.Dispose();
            destination?.Dispose();

            foreach (var buffer in staging) {
                buffer.Dispose();
            }

            throw;
        }

        var replaced = new IGpuBuffer[m_hostBuffers.Length];

        for (var index = 0; (index < replaced.Length); index++) {
            replaced[index] = m_hostBuffers[index];
            _ = m_ownedBuffers.Remove(item: m_hostBuffers[index]);
        }

        m_hostBuffers = staging;
        m_ownedBuffers.AddRange(collection: staging);
        m_destination = destination;
        SetDestinationPages(m_destination!);
        m_ownedBuffers.Add(item: destination);
        m_ownedCopyPool = pool;
        m_copySets = (m_reservedCopySets ?? pool!.Region(index: 0));
        m_copySets.ForgetWriter(region: this);
        m_header = HeaderOf(policy: GpuResidencyPolicy.Staged);
        m_owed = OwedOf(
            policy: GpuResidencyPolicy.Staged,
            slotCount: SlotCount,
            words: ((int)(byteCount / sizeof(uint)))
        );
        m_fresh = null;
        m_stagedSlot = -1;
        Memory = GpuHostVisibleMemory.Host;
        Policy = GpuResidencyPolicy.Staged;
        ByteCount = byteCount;

        return new ReplacedBuffers(buffers: replaced);
    }
    // Replaces every host-visible buffer of a ring or in-place region with one of byteCount bytes, created before any is
    // released so a failed creation leaves the region as it was, and owes the whole grown region in every buffer.
    private ReplacedBuffers GrowHostBuffers(long byteCount) {
        var created = CreateHostBuffers(
            byteCount: byteCount,
            memory: Memory,
            policy: Policy
        );
        var replaced = new IGpuBuffer[m_hostBuffers.Length];
        var retirement = new ReplacedBuffers(buffers: replaced);

        try {
            // Finish every allocation before moving ownership. If the CPU shadow cannot grow, the region still owns
            // its old buffers and the replacements are released just as on a failed buffer creation.
            GrowContents(byteCount: byteCount);
        } catch {
            foreach (var buffer in created) {
                buffer.Dispose();
            }

            throw;
        }

        for (var index = 0; (index < created.Length); index++) {
            replaced[index] = m_hostBuffers[index];
            _ = m_ownedBuffers.Remove(item: m_hostBuffers[index]);
            m_hostBuffers[index] = created[index];
            m_ownedBuffers.Add(item: created[index]);
        }

        ByteCount = byteCount;
        m_fresh?.Clear();

        foreach (var owed in m_owed) {
            owed.Clear();
            owed.Add(
                length: ((int)(byteCount / sizeof(uint))),
                start: 0
            );
        }

        return retirement;
    }
    // Extends the host copy of a region that holds all of it to byteCount bytes, its new tail zero. A zeroed allocation
    // takes fresh pages the system zeroes as they are first touched, so only the bytes held are copied, where
    // reallocating would also clear the new tail.
    private void GrowContents(long byteCount) {
        ReplaceContents(byteCount: byteCount, zero: true);
        m_knownWords = ((int)(byteCount / sizeof(uint)));
    }
    // Replaces the host copy with one of byteCount bytes holding as much of the old one as both reach, the rest zero when
    // asked. With the constructor and Dispose, the only place the host copy's storage changes, and each change is
    // reported to its observer.
    private void ReplaceContents(long byteCount, bool zero) {
        if (byteCount != m_shadowBytes) { InstallContents(CopyContents(byteCount, zero), byteCount); }
    }

    private byte* AllocateContents(long byteCount, bool zero) {
        if (m_hostCopies is not null && !m_hostCopies.TryAcquire(byteCount, MemoryBacking.NativeDirect)) {
            throw new GpuMemoryRefusedException(byteCount, "host", m_hostCopies.Name, byDevice: false);
        }
        try {
            return (byte*)(zero ? NativeMemory.AllocZeroed((nuint)byteCount) : NativeMemory.Alloc((nuint)byteCount));
        } catch {
            m_hostCopies?.Change(-byteCount, MemoryBacking.NativeDirect);
            throw;
        }
    }
    private byte* CopyContents(long byteCount, bool zero) {
        var contents = AllocateContents(byteCount, zero);
        System.Buffer.MemoryCopy(m_contents, contents, byteCount, Math.Min(m_shadowBytes, byteCount));
        return contents;
    }
    private void FreeContents(byte* contents, long bytes) {
        NativeMemory.Free(contents);
        m_hostCopies?.Change(-bytes, MemoryBacking.NativeDirect);
    }
    private void InstallContents(byte* contents, long byteCount) {
        FreeContents(m_contents, m_shadowBytes);
        m_contents = contents;
        m_shadowBytes = byteCount;
        m_contentRevision = Interlocked.Increment(location: ref ContentRevisions);
    }
    // Grows a staged region's own destination: a mapped one backs the chunks the new size reaches and keeps every word
    // where it stands, replacing nothing; any other is replaced by a larger one, which carries the old one's bytes on the
    // GPU (RecordCarry).
    private IDisposable GrowDestination(long byteCount) {
        if (m_destination is IGpuMappedBuffer mapped) {
            var contents = !Sourced ? CopyContents(byteCount, zero: true) : null;
            try {
                mapped.Map(bytes: ((ulong)(byteCount - ByteCount)), offset: ((ulong)ByteCount));
            } catch {
                if (contents is not null) { FreeContents(contents, byteCount); }
                throw;
            }
            if (contents is not null) {
                var known = ((int)(ByteCount / sizeof(uint)));

                // A newly backed chunk holds nothing defined, so the zeroed tail of the host copy is owed to it.
                InstallContents(contents, byteCount);
                Owe(endWord: ((int)(byteCount / sizeof(uint))), startWord: known);
                m_stagedSlot = -1;
            }
            ByteCount = byteCount;

            return GpuMappedRelease.None;
        }

        return GrowReplacing(byteCount: byteCount);
    }
    private IGpuBuffer GrowReplacing(long byteCount) {
        var replacement = m_buffers.CreateDeviceLocal(
            name: m_name,
            sizeBytes: ((ulong)byteCount),
            usage: m_destinationUsage
        );
        var replaced = m_destination!;

        // The host copy grows with a region that holds all of it; a source keeps the new tail, and the host copy its
        // shadow.
        try {
            if (!Sourced) {
                GrowContents(byteCount: byteCount);
            }
        } catch {
            replacement.Dispose();

            throw;
        }
        _ = m_ownedBuffers.Remove(item: replaced);
        m_ownedBuffers.Add(item: replacement);

        // A destination still owing its clear holds nothing to carry: the clear covers the grown one whole.
        if ((m_carrySource is null) && !m_clearOwed) {
            m_carrySource = replaced;
            m_carryBytes = ByteCount;
        }

        m_destination = replacement;
        SetDestinationPages(replacement, carried: (replaced is not IGpuPagedBuffer));
        ByteCount = byteCount;
        m_copySets!.ForgetWriter(region: this);

        return replaced;
    }

    /// <summary>Shrinks a staged region that owns its destination to <paramref name="byteCount"/> bytes without waiting
    /// for any submission: it keeps the first <paramref name="byteCount"/> bytes of <see cref="Contents"/> and frees the
    /// rest, and every word below the new size stays where it stands, so only the debt past the new size is dropped and
    /// a move owed past it with it. A mapped destination gives its chunks past the new size back in place; any other is
    /// replaced by one of the new size, which its next copy recording clears and fills with the words below the new size
    /// from the one it replaced on the GPU (<see cref="RecordCarry"/>), and whose page leases below the new size stay
    /// current. A staging ring larger than the flushes need is replaced at a later flush
    /// (<see cref="StagingFloorBytes"/>).</summary>
    /// <param name="byteCount">The new size in bytes; positive, smaller than <see cref="ByteCount"/> and a whole number of
    /// uints.</param>
    /// <returns>What the region gave up, which the caller now owns and disposes once every submission recorded before the
    /// shrink has completed and its carry is recorded (<see cref="PendingCarry"/>): the destination it replaced, or a
    /// mapped destination's chunks past the new size (<see cref="IGpuMappedBuffer.Unmap"/>).</returns>
    /// <exception cref="InvalidOperationException">The region is not staged, or its destination is external.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not positive, not smaller than
    /// <see cref="ByteCount"/> or not a whole number of uints.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public IDisposable Shrink(long byteCount) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (Policy != GpuResidencyPolicy.Staged) ||
            m_external
        ) {
            throw new InvalidOperationException(message: "Only a staged region that owns its destination shrinks.");
        }
        if (
            (byteCount <= 0) ||
            (byteCount >= ByteCount) ||
            ((byteCount % sizeof(uint)) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: byteCount,
                message: $"A region shrinks to a positive whole number of uints below its {ByteCount} bytes.",
                paramName: nameof(byteCount)
            );
        }
        // A move past the new size would read or write words the shrunk destination no longer holds.
        _ = m_moves?.Runs.RemoveAll(match: move => ((((long)Math.Max(val1: move.Source, val2: move.Destination)) + move.Words) * sizeof(uint)) > byteCount);
        _ = m_gathers?.Runs.RemoveAll(match: gather => ((((long)gather.Destination) + gather.Words) * sizeof(uint)) > byteCount);
        if (m_destination is IGpuMappedBuffer mapped) {
            return ShrinkMapped(byteCount: byteCount, mapped: mapped);
        }

        var replacement = m_buffers.CreateDeviceLocal(
            name: m_name,
            sizeBytes: ((ulong)byteCount),
            usage: m_destinationUsage
        );
        var replaced = m_destination!;
        var shadow = Math.Min(
            val1: m_shadowBytes,
            val2: byteCount
        );

        try {
            ReplaceContents(byteCount: shadow, zero: false);
        } catch {
            replacement.Dispose();
            throw;
        }
        _ = m_ownedBuffers.Remove(item: replaced);
        m_ownedBuffers.Add(item: replacement);
        // The new destination carries the words below its size from the one it replaces (RecordCarry), so the words
        // already there stay where they stand and only the debt past the new size is dropped. A carry a grow still owes
        // comes from the destination before it, which the replaced one never received; a destination still owing its
        // clear holds nothing to carry.
        if ((m_carrySource is null) && !m_clearOwed) {
            m_carrySource = replaced;
            m_carryBytes = byteCount;
        } else {
            m_carryBytes = Math.Min(val1: m_carryBytes, val2: byteCount);
        }
        m_destination = replacement;
        SetDestinationPages(replacement, carried: (replaced is not IGpuPagedBuffer));
        ByteCount = byteCount;
        m_knownWords = Math.Min(val1: m_knownWords, val2: ((int)(shadow / sizeof(uint))));
        m_copySets!.ForgetWriter(region: this);

        var kept = StagedRuns();

        kept.Append(limit: ((int)(byteCount / sizeof(uint))), source: m_owed[0]);
        m_owed[0].Clear();
        m_owed[0].Append(source: kept);
        m_stagedSlot = -1;

        return replaced;
    }
    /// <summary>Gets the bytes one mapping of a mapped region covers (<see cref="IGpuMappedBuffer.ChunkBytes"/>); zero for a
    /// region whose destination is not mapped.</summary>
    public long MappedChunkBytes => ((m_destination is IGpuMappedBuffer mapped) ? ((long)mapped.ChunkBytes) : 0L);
    /// <summary>Gets the device bytes held for mapped chunks that no chunk is mapped to
    /// (<see cref="IGpuMappedBuffer.SpareBytes"/>), which only a mapped region's growth takes; zero for a region whose
    /// destination is not mapped.</summary>
    public long MappedSpareBytes => ((m_destination is IGpuMappedBuffer mapped) ? ((long)mapped.SpareBytes) : 0L);

    /// <summary>Returns whether every chunk a range of a mapped region touches holds backing.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes; positive.</param>
    /// <returns><see langword="true"/> when the range is backed; <see langword="false"/> for a range past
    /// <see cref="ByteCount"/> or a region that is not mapped.</returns>
    public bool IsBacked(long offset, long bytes) => (Mapped && (TryLease(bytes: bytes, offset: offset) is not null));
    /// <summary>Backs every chunk a range of a mapped region touches that holds none, in place (<see cref="IGpuMappedBuffer.Map"/>):
    /// the chunks a placement inside <see cref="ByteCount"/> needs once an earlier <see cref="UnmapRange"/> gave them
    /// back.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes, within <see cref="ByteCount"/>.</param>
    /// <exception cref="InvalidOperationException">The region is not mapped.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range passes <see cref="ByteCount"/>.</exception>
    /// <exception cref="GpuMemoryRefusedException">The account or the device refused the backing; nothing was mapped.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void MapRange(long offset, long bytes) => MappedOf(bytes: bytes, offset: offset).Map(bytes: ((ulong)bytes), offset: ((ulong)offset));
    /// <summary>Gives back every mapped chunk that lies wholly inside a range of a mapped region, keeping its size: the
    /// chunks are revoked now, the words they held are owed no more (no reader may need them), and the release returned
    /// frees their backing once the caller has retired it behind their readers.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes, within <see cref="ByteCount"/>.</param>
    /// <returns>The release (<see cref="IGpuMappedBuffer.Unmap"/>).</returns>
    /// <exception cref="InvalidOperationException">The region is not mapped.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range passes <see cref="ByteCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public GpuMappedRelease UnmapRange(long offset, long bytes) {
        var release = MappedOf(bytes: bytes, offset: offset).Unmap(bytes: ((ulong)bytes), offset: ((ulong)offset));

        if (release.Bytes != 0UL) {
            var chunk = MappedChunkBytes;
            var first = (offset.CeilingDivide(divisor: chunk) * chunk);
            var end = Math.Min(val1: ((((offset + bytes) / chunk) * chunk)), val2: ByteCount);

            DropOwed(endWord: ((int)(end / sizeof(uint))), startWord: ((int)(first / sizeof(uint))));
        }

        return release;
    }
    // The mapped destination a range of the region lies in.
    private IGpuMappedBuffer MappedOf(long offset, long bytes) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_destination is not IGpuMappedBuffer mapped) {
            throw new InvalidOperationException(message: "Only a region with a mapped destination maps or unmaps a range in place.");
        }
        if ((offset < 0L) || (bytes < 0L) || (offset > ByteCount) || (bytes > (ByteCount - offset))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bytes), message: $"The range of {bytes} bytes at {offset} passes the region's {ByteCount}.");
        }

        return mapped;
    }
    // Owes no word of [startWord, endWord) any more: the staged policy's one owed list keeps every run outside it.
    private void DropOwed(int startWord, int endWord) {
        if (endWord <= startWord) { return; }

        var owed = m_owed[0];
        var kept = StagedRuns();

        for (var run = 0; (run < owed.Count); run++) {
            var start = owed.Start(index: run);
            var stop = (start + owed.Length(index: run));

            kept.Add(length: (Math.Min(val1: stop, val2: startWord) - start), start: start);
            kept.Add(length: (stop - Math.Max(val1: start, val2: endWord)), start: Math.Max(val1: start, val2: endWord));
        }
        owed.Clear();
        owed.Append(source: kept);
        m_stagedSlot = -1;
    }
    // A scratch list of staged owed runs, joining and coarsening as the region's owed list does.
    private GpuUploadRuns StagedRuns() => (m_exactSource
        ? new GpuUploadRuns(capacity: MaxCopyRuns, exact: true)
        : new GpuUploadRuns(capacity: MaxCopyRuns, mergeGap: StagedMergeGapWords));
    // Shrinks a mapped destination in place: the chunks wholly past the new size are revoked now and released behind
    // their readers, every word below it stays where it stands, and only the debt past the new size is dropped.
    private GpuMappedRelease ShrinkMapped(long byteCount, IGpuMappedBuffer mapped) {
        var words = ((int)(byteCount / sizeof(uint)));
        var shadow = Math.Min(
            val1: m_shadowBytes,
            val2: byteCount
        );
        var kept = StagedRuns();

        var contents = shadow < m_shadowBytes ? CopyContents(shadow, zero: false) : null;
        GpuMappedRelease release;
        try {
            release = mapped.Unmap(bytes: (mapped.SizeBytes - ((ulong)byteCount)), offset: ((ulong)byteCount));
        } catch {
            if (contents is not null) { FreeContents(contents, shadow); }
            throw;
        }
        if (contents is not null) { InstallContents(contents, shadow); }
        kept.Append(limit: words, source: m_owed[0]);
        m_owed[0].Clear();
        m_owed[0].Append(source: kept);
        m_knownWords = Math.Min(val1: m_knownWords, val2: ((int)(shadow / sizeof(uint))));
        ByteCount = byteCount;
        m_stagedSlot = -1;

        return release;
    }
    /// <summary>Records what a new destination owes before any copy: the clear of the whole destination, then, for a
    /// grown region, the copy of the bytes the destination it replaced held, each ordered after the last, and the barrier
    /// handing the destination to the next access, the copy kernel's write when <paramref name="copies"/> and otherwise
    /// <paramref name="readers"/>. The replaced destination is read as a copy source after <paramref name="readers"/>'
    /// earlier reads of it. Nothing is recorded when nothing is owed (<see cref="OwesCarry"/>).</summary>
    /// <param name="commandBuffer">The command buffer being recorded, submitted before any GPU work that reads the
    /// region's new destination.</param>
    /// <param name="readers">The shader stages that read the region's buffers.</param>
    /// <param name="copies">Whether the region records its copy next in the same command buffer.</param>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void RecordCarry(nint commandBuffer, GpuStage readers, bool copies) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!OwesCarry) {
            return;
        }

        var destination = m_destination!.BufferHandle;

        m_recorder.ClearStorageBuffer(
            bufferHandle: destination,
            commandBufferHandle: commandBuffer,
            sizeBytes: ((ulong)ByteCount)
        );
        m_clearOwed = false;
        if (m_carrySource is not { } source) {
            m_recorder.TransitionBuffer(
                bufferHandle: destination,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: (copies ? GpuAccess.ShaderWrite : GpuAccess.ShaderRead),
                destinationStageMask: (copies ? GpuStage.ComputeShader : readers),
                sourceAccessMask: GpuAccess.TransferWrite,
                sourceStageMask: GpuStage.Transfer
            );

            return;
        }
        m_recorder.TransitionBuffer(
            bufferHandle: destination,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.CopyWrite,
            destinationStageMask: GpuStage.Transfer,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        m_recorder.TransitionBuffer(
            bufferHandle: source.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.TransferRead,
            destinationStageMask: GpuStage.Transfer,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: readers
        );
        m_recorder.CopyBuffer(
            commandBufferHandle: commandBuffer,
            destinationBufferHandle: destination,
            sizeBytes: ((ulong)m_carryBytes),
            sourceBufferHandle: source.BufferHandle
        );
        m_recorder.TransitionBuffer(
            bufferHandle: destination,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: (copies ? GpuAccess.ShaderWrite : GpuAccess.ShaderRead),
            destinationStageMask: (copies ? GpuStage.ComputeShader : readers),
            sourceAccessMask: GpuAccess.CopyWrite,
            sourceStageMask: GpuStage.Transfer
        );
        m_carrySource = null;
    }
    /// <summary>Keeps the host copy of the region's first <paramref name="shadowBytes"/> bytes alone and hands every word
    /// past them to <paramref name="source"/>, which a flush asks for the words it owes there (<see cref="OweSourced"/>),
    /// so the region holds no copy of words its owner already holds. A host copy that grows starts its new bytes unknown, so
    /// writes there owe every word until each is written once; one that shrinks frees the rest. The shadow stays put as the
    /// region grows, and a region that shrinks below it shrinks it too. Owed words stay owed, except under an exact
    /// source.</summary>
    /// <param name="source">The owner of the words past the host copy, which the region holds until disposed.</param>
    /// <param name="shadowBytes">The bytes the host copy holds from the region's start; zero or more, a whole number of
    /// uints and at most <see cref="ByteCount"/>. A region holding no host copy keeps nothing of its contents on the
    /// host: every word it stages comes from its source.</param>
    /// <param name="exact">Whether the source answers only the words it was owed (<see cref="OweSourced"/>) since it was
    /// handed over: the region then joins no owed runs across a word it was not owed and never coarsens them, and the
    /// words past the host copy that an earlier source was owed and no flush staged are dropped, since the new source does
    /// not hold them; their owner owes them again. Once a recorded copy leaves nothing past the host copy owed, the
    /// region lets an exact source go, so the owner hands one over again before it owes more. A source that holds every
    /// word past the host copy (an owner's whole table) is not exact.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The region is not staged, or its destination is external.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shadowBytes"/> is negative, not a whole number of
    /// uints or past <see cref="ByteCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void Shadow(IGpuRegionSource source, long shadowBytes, bool exact = false) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: source);

        if (
            (Policy != GpuResidencyPolicy.Staged) ||
            m_external
        ) {
            throw new InvalidOperationException(message: "Only a staged region that owns its destination hands words to a source.");
        }
        if (
            (shadowBytes < 0) ||
            (shadowBytes > ByteCount) ||
            ((shadowBytes % sizeof(uint)) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: shadowBytes,
                message: $"A region's host copy holds a whole number of uints within its {ByteCount} bytes.",
                paramName: nameof(shadowBytes)
            );
        }

        // The destination holds the host copy no further than both copies reach.
        var knownWords = Math.Min(
            val1: m_knownWords,
            val2: ((int)(Math.Min(val1: m_shadowBytes, val2: shadowBytes) / sizeof(uint)))
        );
        ReplaceContents(byteCount: shadowBytes, zero: true);
        m_source = source;
        m_knownWords = knownWords;
        if (exact) {
            if (!m_exactSource) {
                m_exactSource = true;
                var owed = StagedRuns();

                owed.Append(source: m_owed[0]);
                m_owed[0] = owed;
            }
            DropOwed(endWord: ((int)(ByteCount / sizeof(uint))), startWord: ((int)(shadowBytes / sizeof(uint))));
        }
    }
    /// <summary>Owes the words of <paramref name="length"/> bytes from <paramref name="offset"/>, past the host copy, in
    /// every buffer, comparing nothing: the region's source (<see cref="Shadow"/>) holds what they are, and the next flush
    /// asks it for them.</summary>
    /// <param name="offset">The first byte; a whole number of uints, at or past <see cref="ShadowBytes"/>.</param>
    /// <param name="length">The bytes; positive, a whole number of uints, ending within <see cref="ByteCount"/>.</param>
    /// <exception cref="InvalidOperationException">The region has no source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span is not whole uints, starts before
    /// <see cref="ShadowBytes"/> or runs past <see cref="ByteCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void OweSourced(long offset, long length) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_source is null) {
            throw new InvalidOperationException(message: "The region has no source for the words past its host copy.");
        }
        if (
            (offset < m_shadowBytes) ||
            (length <= 0) ||
            (offset > (ByteCount - length)) ||
            ((offset % sizeof(uint)) != 0) ||
            ((length % sizeof(uint)) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: offset,
                message: $"Sourced words are whole uints between the host copy's {m_shadowBytes} bytes and the region's {ByteCount}.",
                paramName: nameof(offset)
            );
        }

        Owe(
            endWord: ((int)((offset + length) / sizeof(uint))),
            startWord: ((int)(offset / sizeof(uint)))
        );
        m_stagedSlot = -1;
    }
    /// <summary>Reserves what the region's moves (<see cref="TryOweMove"/>) and gathers (<see cref="TryOweGather"/>) record
    /// with: a copy set and a request buffer per slot for each, beside the slot's copy set, so neither rewrites the set its
    /// copy binds. Reserving twice does nothing.</summary>
    /// <exception cref="InvalidOperationException">The region is not staged, or its destination is external.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void ReserveRunCopies() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if ((Policy != GpuResidencyPolicy.Staged) || m_external) {
            throw new InvalidOperationException(message: "Only a staged region that owns its destination moves or gathers words into it.");
        }
        if (m_moves is not null) { return; }
        m_moves = CreateRunCopy(detail: "moves", mode: CopyMoveMode);
        m_gathers = CreateRunCopy(detail: "gathers", mode: CopyGatherMode);
    }
    private GpuRegionRunCopy CreateRunCopy(string detail, uint mode) {
        var name = (m_name.IsEmpty ? default : new GpuObjectName(owner: m_name.Owner!, part: m_name.Part!, detail: detail));

        return new GpuRegionRunCopy(bindings: m_bindings, buffers: m_buffers, pipeline: m_copyPipeline!, recorder: m_recorder, name: in name,
            slotCount: SlotCount, mode: mode);
    }
    /// <summary>Gets whether the region owes moves its next copy records (<see cref="TryOweMove"/>).</summary>
    public bool OwesMoves => (m_moves?.Runs.Count > 0);
    /// <summary>Gets whether the region owes gathers its next copy records (<see cref="TryOweGather"/>).</summary>
    public bool OwesGathers => (m_gathers?.Runs.Count > 0);
    /// <summary>Gets how many moves the region has recorded over its life (<see cref="TryOweMove"/>).</summary>
    public long MovesRecorded => (m_moves?.Recorded ?? 0L);
    /// <summary>Owes a move of <paramref name="length"/> bytes within the destination, from <paramref name="sourceOffset"/>
    /// to <paramref name="destinationOffset"/>, which the next recorded copy of any slot dispatches before its gathers
    /// and staged words, so every word written after this call lands after the move has read its source and written its
    /// destination. No word passes through the host. The words the move writes hold the source's words once it runs; the
    /// source keeps its words until something writes them.
    /// <para>Invariant: no two moves owed together overlap, in source or destination, and no move overlaps itself, so
    /// the moves of one dispatch read no word another of them writes. The caller keeps both ranges backed until the
    /// submission that records the move (<see cref="OwesMoves"/> false once recorded).</para></summary>
    /// <param name="sourceOffset">The source's first byte; a whole number of uints.</param>
    /// <param name="destinationOffset">The destination's first byte; a whole number of uints, at or past
    /// <see cref="ShadowBytes"/>.</param>
    /// <param name="length">The bytes; positive, a whole number of uints, both ranges ending within
    /// <see cref="ByteCount"/>.</param>
    /// <returns><see langword="false"/> when the region already owes <see cref="MaxMoves"/> moves, or the move overlaps
    /// one it owes; nothing is owed then.</returns>
    /// <exception cref="InvalidOperationException">The region reserved no run copies (<see cref="ReserveRunCopies"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A range is not whole uints, the destination starts before
    /// <see cref="ShadowBytes"/>, or a range runs past <see cref="ByteCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool TryOweMove(long sourceOffset, long destinationOffset, long length) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_moves is null) { throw new InvalidOperationException(message: "The region reserved no run copies."); }
        RequireRunDestination(destinationOffset: destinationOffset, length: length);
        if (((sourceOffset % sizeof(uint)) != 0) || (sourceOffset < 0) || (sourceOffset > (ByteCount - length))) {
            throw new ArgumentOutOfRangeException(actualValue: sourceOffset, message: $"A move's source is whole uints within the region's {ByteCount} bytes.", paramName: nameof(sourceOffset));
        }

        var source = ((int)(sourceOffset / sizeof(uint)));
        var destination = ((int)(destinationOffset / sizeof(uint)));
        var words = ((int)(length / sizeof(uint)));

        if ((m_moves.Runs.Count == MaxMoves) || GpuRegionRunCopy.Overlap(source, words, destination, words) ||
            m_moves.Overlaps(first: source, words: words) || m_moves.Overlaps(first: destination, words: words)) { return false; }
        m_moves.Add(destination: destination, from: null, source: source, words: words);
        m_stagedSlot = -1;

        return true;
    }
    /// <summary>Owes a gather of <paramref name="words"/> words a caller packed into a host-visible buffer of its own, from
    /// its word <paramref name="sourceWord"/> to the destination's byte <paramref name="destinationOffset"/>, which the next
    /// recorded copy of any slot dispatches after its moves and before its staged words. Every gather owed together reads
    /// one buffer, which the caller keeps alive and unwritten until the submission that records the gather has completed.
    /// The caller records the copy at once (it owes a gather only where it then flushes and records the region), and
    /// keeps the destination range backed until then.</summary>
    /// <param name="source">The buffer the words are packed in.</param>
    /// <param name="sourceWord">The first word of <paramref name="source"/> the gather reads.</param>
    /// <param name="destinationOffset">The destination's first byte; a whole number of uints, at or past
    /// <see cref="ShadowBytes"/>.</param>
    /// <param name="words">The words; positive, the range ending within <see cref="ByteCount"/>.</param>
    /// <returns><see langword="false"/> when the region already owes <see cref="MaxCopyRuns"/> gathers, the gather reads
    /// another buffer than those it owes, or its destination overlaps one it owes; nothing is owed then.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The region reserved no run copies (<see cref="ReserveRunCopies"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The destination is not whole uints, starts before
    /// <see cref="ShadowBytes"/> or runs past <see cref="ByteCount"/>, or the source words pass
    /// <paramref name="source"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool TryOweGather(IGpuStorageBuffer source, int sourceWord, long destinationOffset, int words) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentNullException.ThrowIfNull(argument: source);
        if (m_gathers is null) { throw new InvalidOperationException(message: "The region reserved no run copies."); }
        RequireRunDestination(destinationOffset: destinationOffset, length: (((long)words) * sizeof(uint)));
        if ((sourceWord < 0) || (((((ulong)sourceWord) + ((ulong)words)) * sizeof(uint)) > source.SizeBytes)) {
            throw new ArgumentOutOfRangeException(actualValue: sourceWord, message: $"A gather reads words within its {source.SizeBytes}-byte buffer.", paramName: nameof(sourceWord));
        }

        var destination = ((int)(destinationOffset / sizeof(uint)));

        if ((m_gathers.Runs.Count == MaxCopyRuns) || ((m_gathers.Source is { } owed) && !ReferenceEquals(objA: owed, objB: source)) ||
            m_gathers.Overlaps(first: destination, words: words)) { return false; }
        m_gathers.Add(destination: destination, from: source, source: sourceWord, words: words);
        m_stagedSlot = -1;

        return true;
    }
    private void RequireRunDestination(long destinationOffset, long length) {
        if ((length <= 0) || ((length % sizeof(uint)) != 0) || ((destinationOffset % sizeof(uint)) != 0) ||
            (destinationOffset < m_shadowBytes) || (destinationOffset > (ByteCount - length))) {
            throw new ArgumentOutOfRangeException(
                actualValue: destinationOffset,
                message: $"A run copy writes whole uints within the region's {ByteCount} bytes, past the host copy's {m_shadowBytes}.",
                paramName: nameof(destinationOffset)
            );
        }
    }
    /// <summary>Makes <paramref name="slot"/> owe every word of the region again, as a new buffer does, so its next flush
    /// or copy sends the whole region whatever the slot last held. An owner that rewrites the whole region each frame
    /// calls it so each frame's upload is the region, not the part that differs from what that slot held frames
    /// ago.</summary>
    /// <param name="slot">The frame slot, below <see cref="SlotCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void OweAll(int slot) {
        RequireSlot(slot: slot);

        var owed = ((Policy == GpuResidencyPolicy.Ring) ? OwedOf(slot: slot) : m_owed[0]);

        owed.Clear();
        owed.Add(
            length: ((int)(ByteCount / sizeof(uint))),
            start: 0
        );
        m_stagedSlot = -1;
    }
    /// <summary>Writes what <paramref name="slot"/> owes into its host-visible buffer: the words changed since the slot
    /// was last flushed under the ring, since the last flush under in place, and since the last recorded copy under the
    /// staged policy, whose owed words go to a block of the staging ring and whose header and run table go to the slot's
    /// request buffer, releasing the block the slot's previous flush took. A slot owing nothing writes nothing.</summary>
    /// <param name="slot">The frame slot, whose last submission has retired; below <see cref="SlotCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void Flush(int slot) {
        RequireSlot(slot: slot);

        switch (Policy) {
            case GpuResidencyPolicy.Ring:
                WriteOwed(
                    buffer: m_hostBuffers[slot],
                    owed: OwedOf(slot: slot)
                );
                m_owed[slot].Clear();

                break;
            case GpuResidencyPolicy.InPlace:
                WriteOwed(
                    buffer: m_hostBuffers[0],
                    owed: m_owed[0]
                );
                m_owed[0].Clear();

                break;
            default:
                StageOwed(slot: slot);

                break;
        }
    }
    /// <summary>Records the staged policy's copy for <paramref name="slot"/>: one dispatch of the copy kernel covering
    /// every writable owed word; unmapped words remain owed. It records no barrier: the caller makes the
    /// destination's writes visible to its readers. Under the other policies, or with nothing owed, it records
    /// nothing.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="slot">The frame slot the command buffer belongs to, flushed since the last write.</param>
    /// <param name="pipelineBound">Whether the command buffer already has the copy pipeline (<see cref="CopyPipelineHandle"/>)
    /// bound, as after an earlier region's copy in one recording, so the copy binds only its set.</param>
    /// <returns>Whether a dispatch was recorded. Unmapped debt remains owed and records no dispatch.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="InvalidOperationException">The staged region owes words that <paramref name="slot"/>'s staging
    /// buffer does not hold, because the slot was not flushed after the last write.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool RecordCopy(nint commandBuffer, int slot, bool pipelineBound = false) {
        RequireSlot(slot: slot);

        if (!OwesCopy) {
            return false;
        }

        var owed = m_owed[0];

        if (m_stagedSlot != slot) {
            throw new InvalidOperationException(message: $"The region owes words slot {slot}'s staging buffer does not hold; flush the slot after the last write and before recording its copy.");
        }

        foreach (var run in m_copyRuns) {
            if (!run.Lease.IsCurrent) { StageOwed(slot: slot); break; }
        }
        // Moves go first, then gathers: the words written after a move may be ones it reads or writes.
        var moved = RecordRunCopies(commandBuffer: commandBuffer, pipelineBound: pipelineBound, slot: slot);

        pipelineBound |= moved;
        if (m_header[0] == 0) { m_stagedSlot = -1; return moved; }

        // The set is unwritten, or another region its owner's reserved sets serve wrote it since this one did; the
        // slot's last submission has retired, so the set is written before it is bound.
        if (!m_copySets!.IsWrittenBy(
            region: this,
            slot: slot
        )) {
            WriteCopySet(slot: slot);
        }

        RecordCopyDispatch(recorder: m_recorder, commandBuffer: commandBuffer, pipeline: m_copyPipeline!,
            set: m_copySets.SetOf(slot: slot), words: m_header[0], pipelineBound: pipelineBound);
        owed.Clear();
        owed.Append(m_deferredCopies);
        m_stagedSlot = -1;
        // An exact source served this upload alone: once nothing past the host copy is owed, the region lets it go, so
        // it holds no owner's data (a returned snapshot) between uploads.
        if (m_exactSource && ((owed.Count == 0) || ((((long)(owed.Start(index: (owed.Count - 1)) + owed.Length(index: (owed.Count - 1)))) * sizeof(uint)) <= m_shadowBytes))) {
            m_source = null;
        }
        return true;
    }

    // Writes a copy set's three bindings (CopyBindings), each read as uints: the one door every region-copy set is
    // written through.
    internal static void WriteCopySet(IGpuBindings bindings, nint set, IGpuBuffer request, IGpuBuffer source, IGpuBuffer destination) {
        bindings.WriteBuffer(binding: CopyRequestBinding, bufferHandle: request.BufferHandle, bufferSize: request.SizeBytes,
            descriptorSetHandle: set, elementStride: sizeof(uint), kind: GpuBindingKind.ReadOnlyBuffer);
        bindings.WriteBuffer(binding: CopySourceBinding, bufferHandle: source.BufferHandle, bufferSize: source.SizeBytes,
            descriptorSetHandle: set, elementStride: sizeof(uint), kind: GpuBindingKind.ReadOnlyBuffer);
        bindings.WriteBuffer(binding: CopyDestinationBinding, bufferHandle: destination.BufferHandle, bufferSize: destination.SizeBytes,
            descriptorSetHandle: set, elementStride: sizeof(uint), kind: GpuBindingKind.ReadWriteBuffer);
    }
    // The one dispatch door for packed-region uploads and generic strided buffer copies.
    internal static void RecordCopyDispatch(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set, uint words, bool pipelineBound = false) {
        if (!pipelineBound) {
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: pipeline.Handle
            );
        }
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: set,
            group: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        var (groupsX, groupsY) = CopyGroups(count: words);

        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: groupsX,
            groupCountY: groupsY,
            groupCountZ: 1
        );
    }

    /// <summary>Moves a staged region whose owner reserved its copy sets to another share, one the owner reserved since,
    /// such as the pool of a graph that replaced the one the region was created with. The region writes a slot's set of
    /// the new share before it records that slot's next copy, and never touches its old share again, so the owner may
    /// dispose the old pool once no submission that recorded a copy with it is in flight. Contents and owed words are
    /// unchanged.</summary>
    /// <param name="copySets">The new share, of a pool holding the region's number of slots.</param>
    /// <exception cref="ArgumentNullException"><paramref name="copySets"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The region is not staged, or it created its own copy pool.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void MoveCopySets(GpuRegionCopySets copySets) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(copySets);

        if (
            (Policy != GpuResidencyPolicy.Staged) ||
            (m_ownedCopyPool is not null)
        ) {
            throw new InvalidOperationException(message: "Only a staged region whose owner reserved its copy sets moves to another share.");
        }
        if (copySets.SlotCount != SlotCount) {
            throw new ArgumentException(
                message: $"The copy sets hold {copySets.SlotCount} slot(s), but the region has {SlotCount}.",
                paramName: nameof(copySets)
            );
        }

        if (!ReferenceEquals(objA: m_copySets, objB: copySets)) {
            m_copySets?.ForgetWriter(region: this);
            m_copySets = copySets;
        }
    }
    /// <summary>Moves an external destination's landing point: the region's word 0 lands at
    /// <paramref name="destinationWord"/> from the next copy on, and since what the destination holds there is
    /// unknown, every word written after the move is owed whether or not it differs from <see cref="Contents"/>.</summary>
    /// <param name="destinationWord">The destination word, in uints from its start, that the region's word 0 lands
    /// at.</param>
    /// <exception cref="InvalidOperationException">The region has no external destination, or owes words a copy has not
    /// carried yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destinationWord"/> is negative or past the
    /// destination's last word.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void Target(int destinationWord) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!m_external) {
            throw new InvalidOperationException(message: "Only a region with an external destination can be retargeted.");
        }

        if (m_owed[0].Count != 0) {
            throw new InvalidOperationException(message: "The region owes words its destination has not received; record their copy before retargeting it.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value: destinationWord);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: (m_destination!.SizeBytes / sizeof(uint)),
            value: ((ulong)destinationWord)
        );

        m_destinationWord = destinationWord;
        m_knownWords = 0;
    }
    /// <summary>Copies <paramref name="bytes"/> into <see cref="Contents"/> at <paramref name="offset"/> and owes, in
    /// every buffer, each run of words that differed, and every word of the span the destination is not known to hold
    /// (an external destination's after a retarget, or the bytes a host copy grew by, <see cref="Shadow"/>).</summary>
    /// <param name="offset">The byte offset the bytes start at.</param>
    /// <param name="bytes">The new contents of that span.</param>
    /// <returns><see langword="true"/> when any word was owed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, the span runs past
    /// <see cref="ShadowBytes"/>, or it lands past an external destination's end.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool Write(long offset, ReadOnlySpan<byte> bytes) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (m_shadowBytes - bytes.Length),
            value: offset
        );

        if (bytes.IsEmpty) {
            return false;
        }

        var end = (offset + bytes.Length);
        var endWord = ((int)end.CeilingDivide(divisor: sizeof(uint)));

        if (
            m_external &&
            ((((ulong)m_destinationWord) + ((ulong)endWord)) > (m_destination!.SizeBytes / sizeof(uint)))
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: offset,
                message: $"The write ends at region word {endWord}, past the external destination's end from word {m_destinationWord}.",
                paramName: nameof(offset)
            );
        }

        var knownEnd = Math.Clamp(
            max: end,
            min: offset,
            value: (((long)m_knownWords) * sizeof(uint))
        );
        var owed = OweDifferences(
            bytes: bytes[..((int)(knownEnd - offset))],
            offset: offset
        );

        if (knownEnd < end) {
            Owe(
                endWord: endWord,
                startWord: ((int)(knownEnd / sizeof(uint)))
            );
            owed = true;
        }

        bytes.CopyTo(destination: HostSpan(
            length: bytes.Length,
            offset: offset
        ));

        if ((offset / sizeof(uint)) <= m_knownWords) {
            m_knownWords = Math.Max(
                val1: m_knownWords,
                val2: endWord
            );
        }

        if (owed) {
            m_stagedSlot = -1;
            m_contentRevision = Interlocked.Increment(location: ref ContentRevisions);
        }

        return owed;
    }
    /// <summary>Copies the runs of <paramref name="words"/> that <paramref name="runs"/> names into <see cref="Contents"/>
    /// at the same words and owes each in every buffer, comparing nothing: the caller has found every word of
    /// <paramref name="words"/> outside the runs equal to <see cref="Contents"/>, as a diff against the words it last
    /// wrote does, so the write costs the runs alone.</summary>
    /// <param name="words">The region's new words from word 0.</param>
    /// <param name="runs">The runs of words, from word 0, in which <paramref name="words"/> differs from
    /// <see cref="Contents"/>.</param>
    /// <returns><see langword="true"/> when any word was owed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> runs past <see cref="ShadowBytes"/>, or a run
    /// past the end of <paramref name="words"/>.</exception>
    /// <exception cref="InvalidOperationException">The region's destination is external, whose contents the region does
    /// not know.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool WriteRuns(ReadOnlySpan<uint> words, GpuUploadRuns runs) => WriteRuns(head: words, runs: runs, tail: []);
    /// <summary>Copies the runs that <paramref name="runs"/> names of words held in two buffers, <paramref name="head"/>
    /// then <paramref name="tail"/>, into <see cref="Contents"/> at the same words and owes each in every buffer, as
    /// <see cref="WriteRuns(ReadOnlySpan{uint}, GpuUploadRuns)"/> does for words held in one: a run that spans the two
    /// copies from both.</summary>
    /// <param name="head">The region's new words from word 0.</param>
    /// <param name="tail">The region's new words after <paramref name="head"/>.</param>
    /// <param name="runs">The runs of words, from word 0, in which the two differ from <see cref="Contents"/>.</param>
    /// <returns><see langword="true"/> when any word was owed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The words run past <see cref="ShadowBytes"/>, or a run past their
    /// end.</exception>
    /// <exception cref="InvalidOperationException">The region's destination is external, whose contents the region does
    /// not know.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool WriteRuns(ReadOnlySpan<uint> head, ReadOnlySpan<uint> tail, GpuUploadRuns runs) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: runs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (m_shadowBytes / sizeof(uint)),
            value: (((long)head.Length) + tail.Length)
        );

        if (m_external) {
            throw new InvalidOperationException(message: "A region with an external destination writes by comparing, since it does not know what its destination holds.");
        }

        for (var run = 0; (run < runs.Count); run++) {
            var start = runs.Start(index: run);
            var length = runs.Length(index: run);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                other: (head.Length + tail.Length),
                value: (start + length),
                paramName: nameof(runs)
            );
            var destination = new Span<uint>(
                length: length,
                pointer: (((uint*)m_contents) + start)
            );
            var fromHead = Math.Clamp(value: (head.Length - start), min: 0, max: length);

            head.Slice(length: fromHead, start: Math.Min(val1: start, val2: head.Length)).CopyTo(destination: destination);
            if (fromHead < length) { tail.Slice(length: (length - fromHead), start: ((start + fromHead) - head.Length)).CopyTo(destination: destination[fromHead..]); }
            Owe(
                endWord: (start + length),
                startWord: start
            );
        }

        if (runs.Count == 0) {
            return false;
        }

        m_stagedSlot = -1;
        m_contentRevision = Interlocked.Increment(location: ref ContentRevisions);

        return true;
    }

    // Writes the slot's request buffer as the request, the ring its block lies in as the source and the destination as
    // the destination into the slot's copy set.
    private void WriteCopySet(int slot) {
        var set = m_copySets!.SetOf(slot: slot);

        WriteCopySet(bindings: m_bindings, destination: m_destination!, request: m_hostBuffers[slot], set: set, source: m_blocks[slot].Ring!);
        m_copySets.WrittenBy(
            region: this,
            slot: slot
        );
    }
    // Owes words [startWord, endWord) in every buffer.
    private void Owe(int startWord, int endWord) {
        if (m_fresh is { } fresh) {
            fresh.Add(
                length: (endWord - startWord),
                start: startWord
            );

            return;
        }

        foreach (var owed in m_owed) {
            owed.Add(
                length: (endWord - startWord),
                start: startWord
            );
        }
    }
    // Hands the ranges written since the last fold to every slot's list, the one door to a Ring slot's owed list.
    private GpuUploadRuns OwedOf(int slot) {
        if ((m_fresh is { } fresh) && (fresh.Count > 0)) {
            foreach (var owed in m_owed) {
                for (var run = 0; (run < fresh.Count); run++) {
                    owed.Add(
                        length: fresh.Length(index: run),
                        start: fresh.Start(index: run)
                    );
                }
            }

            fresh.Clear();
        }

        return m_owed[slot];
    }
    // Owes each run of words in which bytes differ from the contents at offset, a word at a time past the first
    // differing byte, and returns whether any did.
    private bool OweDifferences(ReadOnlySpan<byte> bytes, long offset) {
        var contents = HostSpan(
            length: bytes.Length,
            offset: offset
        );
        var index = 0;
        var owed = false;

        while (index < bytes.Length) {
            index += bytes[index..].CommonPrefixLength(other: contents[index..]);

            if (index == bytes.Length) {
                break;
            }

            var start = index;

            // To the end of the word holding the first differing byte, then on while the next word differs.
            index = ((int)Math.Min(
                val1: bytes.Length,
                val2: (((((offset + index) / sizeof(uint)) + 1) * sizeof(uint)) - offset)
            ));

            while (index < bytes.Length) {
                var next = Math.Min(
                    val1: bytes.Length,
                    val2: (index + sizeof(uint))
                );

                if (((next - index) == sizeof(uint))
                    ? (MemoryMarshal.Read<uint>(source: bytes[index..]) == MemoryMarshal.Read<uint>(source: contents[index..]))
                    : bytes[index..next].SequenceEqual(other: contents[index..next])) {
                    break;
                }

                index = next;
            }

            Owe(
                endWord: ((int)(offset + index).CeilingDivide(divisor: sizeof(uint))),
                startWord: ((int)((offset + start) / sizeof(uint)))
            );
            owed = true;
        }

        return owed;
    }
    private void RequireSlot(int slot) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: SlotCount,
            value: slot
        );
    }
    // Length bytes of the host copy from offset as a writable span; the copy moves when it grows or shrinks.
    private Span<byte> HostSpan(long offset, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (m_shadowBytes - length),
            value: offset
        );

        return new Span<byte>(
            length: length,
            pointer: (m_contents + offset)
        );
    }
    // Writes the words [startWord, startWord + length) of the region into a buffer from byte destination: those the host
    // copy holds, a piece of at most WriteChunkBytes at a time since a span's length is an int, and those past it from the
    // source.
    private void WriteHostWords(IGpuStorageBuffer buffer, int startWord, int length, ulong destination) {
        var offset = (((long)startWord) * sizeof(uint));
        var remaining = Math.Clamp(
            max: (((long)length) * sizeof(uint)),
            min: 0L,
            value: (m_shadowBytes - offset)
        );
        var sourced = ((((long)length) * sizeof(uint)) - remaining);

        if (sourced > 0) {
            m_source!.WriteWords(
                buffer: buffer,
                count: ((int)(sourced / sizeof(uint))),
                destinationOffsetBytes: (destination + ((ulong)remaining)),
                word: ((int)((offset + remaining) / sizeof(uint)))
            );
        }

        while (remaining > 0) {
            var piece = ((int)Math.Min(
                val1: remaining,
                val2: WriteChunkBytes
            ));

            buffer.Write<byte>(
                data: HostSpan(
                    length: piece,
                    offset: offset
                ),
                destinationOffsetBytes: destination
            );
            offset += piece;
            destination += ((ulong)piece);
            remaining -= piece;
        }
    }
    // Stages every owed run packed one after another into a block of the ring the slot takes, then writes the header and
    // run table into the slot's request buffer: the copy's thread count, run count, the block's first ring word and the
    // destination word, then per run its region word and first thread, which is also where its words start in the block.
    // The slot's previous block is released first: its last submission has retired.
    private void StageOwed(int slot) {
        m_moves?.Stage(destinationWord: m_destinationWord);
        m_gathers?.Stage(destinationWord: m_destinationWord);

        var owed = m_owed[0];

        m_pages!.OweChanged(after: m_pageRevision, bytes: (((long)m_knownWords) * sizeof(uint)), offset: (((long)m_destinationWord) * sizeof(uint)), owed: owed);
        m_pageRevision = m_pages.Revision;
        m_copyRuns.Clear();
        m_deferredCopies.Clear();
        ReleaseBlock(slot: slot);
        m_header[0] = 0;
        if (owed.Count == 0) {
            DropIdleRing();
            m_stagedSlot = slot;
            return;
        }
        for (var run = 0; (run < owed.Count); run++) {
            m_pages!.Clip(owed.Start(index: run), owed.Length(index: run), m_destinationWord, m_copyRuns, m_deferredCopies);
        }
        // An exact source's runs never coarsen, so more than one request holds wait, owed, for the next copy.
        while (m_copyRuns.Count > MostRequestRuns) {
            var last = m_copyRuns[^1];

            m_deferredCopies.Add(length: last.Length, start: last.Start);
            m_copyRuns.RemoveAt(index: (m_copyRuns.Count - 1));
        }
        var requestWords = checked((CopyHeaderWords + (m_copyRuns.Count * 2)));

        if (m_header.Length < requestWords) { Array.Resize(array: ref m_header, newSize: requestWords); }
        var words = 0L;

        foreach (var run in m_copyRuns) { words += run.Length; }
        if (words == 0L) {
            DropIdleRing();
            m_stagedSlot = slot;
            return;
        }
        IGpuStorageBuffer request;
        IGpuStorageBuffer ring;
        long offset;

        // The one place a flush allocates. A flush whose request buffer or ring block the account or the device refuses
        // stages nothing: its words stay owed, the destination keeps what it holds, and the next flush asks again.
        try {
            request = RequestFor(bytes: checked((((ulong)requestWords) * sizeof(uint))), slot: slot);
            (ring, offset) = TakeBlock(bytes: checked((words * sizeof(uint))), slot: slot);
        } catch (GpuMemoryRefusedException) {
            StagingRefusals++;
            m_copyRuns.Clear();
            m_deferredCopies.Clear();
            m_stagedSlot = slot;
            return;
        }

        var blockBase = ((int)(offset / sizeof(uint)));
        var prefix = 0;

        for (var index = 0; (index < m_copyRuns.Count); index++) {
            var run = m_copyRuns[index];

            WriteHostWords(buffer: ring, destination: (((ulong)offset) + (((ulong)prefix) * sizeof(uint))), length: run.Length, startWord: run.Start);
            m_header[(CopyHeaderWords + (index * 2))] = ((uint)run.Start);
            m_header[((CopyHeaderWords + (index * 2)) + 1)] = ((uint)prefix);
            prefix += run.Length;
        }
        m_header[0] = ((uint)prefix);
        m_header[1] = ((uint)m_copyRuns.Count);
        m_header[2] = ((uint)blockBase);
        m_header[3] = ((uint)m_destinationWord);
        m_header[4] = 0; m_header[5] = 0; m_header[6] = 0;
        request.Write<uint>(data: m_header.AsSpan(0, requestWords));
        m_stagedSlot = slot;
    }
    // Records the moves and then the gathers the slot's flush staged, each followed by the barrier that orders its reads and
    // writes of the destination before the next dispatch's; false when neither was staged.
    private bool RecordRunCopies(nint commandBuffer, int slot, bool pipelineBound) {
        var recorded = false;

        foreach (var copies in (ReadOnlySpan<GpuRegionRunCopy?>)[m_moves, m_gathers]) {
            if ((copies is null) || !copies.Record(commandBuffer: commandBuffer, destination: m_destination!, pipelineBound: (pipelineBound || recorded), slot: slot)) { continue; }
            recorded = true;
            m_recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite),
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite),
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        return recorded;
    }
    // Returns the slot's request buffer, replaced first by one of at least the bytes asked when it holds fewer (the slot's
    // last submission has retired, so the buffer is free). The copy sets are written again before each slot's next copy.
    private IGpuStorageBuffer RequestFor(int slot, ulong bytes) {
        var buffer = m_hostBuffers[slot];

        if (bytes <= buffer.SizeBytes) { return buffer; }
        var replacement = m_buffers.CreateHostVisible(
            name: HostName(slot: slot),
            sizeBytes: ((ulong)GpuCapacity.Grown(current: ((long)buffer.SizeBytes), required: ((long)bytes), ceiling: MaxCopyHeaderBytes, divisor: 1)),
            usage: m_usage
        );

        _ = m_ownedBuffers.Remove(item: buffer);
        buffer.Dispose();
        m_ownedBuffers.Add(item: replacement);
        m_hostBuffers[slot] = replacement;
        m_copySets!.ForgetWriter(region: this);

        return replacement;
    }
    // Releases the slot's block, whose submission has retired: a block of a replaced ring disposes that ring with its
    // last block.
    private void ReleaseBlock(int slot) {
        if (m_blocks[slot].Ring is not { } ring) { return; }
        m_blocks[slot] = default;
        if (ReferenceEquals(objA: ring, objB: m_ring)) { return; }
        for (var index = 0; (index < m_retiredRings.Count); index++) {
            var retired = m_retiredRings[index];

            if (!ReferenceEquals(objA: retired.Ring, objB: ring)) { continue; }
            if (--retired.Live == 0) {
                retired.Ring.Dispose();
                m_retiredRings.RemoveAt(index: index);
            }
            return;
        }
    }
    // Takes a block of bytes for the slot (whose previous block is released) from the ring: after the last block when
    // that stretch is free, else from the ring's start, else from a replacement ring. A ring holding far more than its
    // live blocks and this one take is replaced too, by a smaller one. A replaced ring keeps the blocks it holds until
    // their slots release them, and is disposed at once when it holds none.
    private (IGpuStorageBuffer Ring, long Offset) TakeBlock(int slot, long bytes) {
        var live = 0L;

        for (var index = 0; (index < m_blocks.Length); index++) {
            if (ReferenceEquals(objA: m_blocks[index].Ring, objB: m_ring) && (m_ring is not null)) { live += m_blocks[index].Bytes; }
        }

        var capacity = ((long)(m_ring?.SizeBytes ?? 0UL));
        var need = (live + bytes);
        var floor = RingFloorBytes;
        var fit = ((m_ring is null) ? -1L
            : (((m_ringHead + bytes) <= capacity) && IsFree(offset: m_ringHead, bytes: bytes)) ? m_ringHead
            : (IsFree(offset: 0L, bytes: bytes) && (bytes <= capacity)) ? 0L
            : -1L);
        var oversized = ((m_ring is not null) && (capacity > Math.Max(val1: floor, val2: (4L * need))));
        var offset = (oversized ? -1L : fit);

        if (offset < 0L) {
            // The new ring must hold this block alone, since the live ones stay where they are; room for them too keeps
            // the next flushes from replacing it. It is never larger than the region itself (or the floor), so a ring
            // binds wherever the region's own destination does.
            var ceiling = Math.Max(val1: bytes, val2: Math.Min(val1: Math.Max(val1: ByteCount, val2: floor), val2: MaxByteCount));
            var least = Math.Min(val1: floor, val2: ceiling);
            // An oversized ring shrinks to twice what it must hold; one with no room grows (a fragmented one included).
            var size = (oversized
                ? Math.Max(val1: least, val2: Math.Min(val1: (2L * need), val2: ceiling))
                : GpuCapacity.Grown(current: capacity, required: Math.Min(val1: ceiling, val2: Math.Max(val1: need, val2: (capacity + 1L))), ceiling: ceiling, divisor: 1, minimum: least));

            // An oversized ring no block lives in is given back before its replacement is asked for, so the shrink never
            // holds both.
            if (oversized && (live == 0L) && (CountBlocksIn(ring: m_ring!) == 0)) {
                m_ring!.Dispose();
                m_ring = null;
                fit = -1L;
            }
            IGpuStorageBuffer replacement;

            try {
                replacement = m_buffers.CreateHostVisible(name: m_stagingName, sizeBytes: ((ulong)size), usage: m_usage);
            } catch (GpuMemoryRefusedException) when (fit >= 0L) {
                // A shrink is optional: a ring with room for the block keeps serving when its smaller replacement is
                // refused.
                return Place(slot: slot, offset: fit, bytes: bytes);
            }

            if (m_ring is { } old) {
                if (CountBlocksIn(ring: old) == 0) { old.Dispose(); }
                else { m_retiredRings.Add(item: new RetiredRing(ring: old, live: CountBlocksIn(ring: old))); }
            }
            m_ring = replacement;
            m_copySets!.ForgetWriter(region: this);
            offset = 0L;
        }

        return Place(slot: slot, offset: offset, bytes: bytes);
    }
    // Records the slot's block of the current ring.
    private (IGpuStorageBuffer Ring, long Offset) Place(int slot, long offset, long bytes) {
        m_blocks[slot] = new RingBlock(Ring: m_ring, Offset: offset, Bytes: bytes);
        m_ringHead = (offset + bytes);

        return (m_ring!, offset);
    }
    // The least a ring holds: StagingFloorBytes, or every slot's whole update of a smaller region, so a small region's
    // ring holds whatever its slots stage and is never replaced.
    private long RingFloorBytes => Math.Min(val1: ((long)StagingFloorBytes), val2: Math.Min(val1: (((long)SlotCount) * ByteCount), val2: MaxByteCount));
    // Disposes the current ring when no block lives in it and it holds more than the floor, so an idle region keeps no
    // more staging than that; the next flush takes a ring sized to what it stages.
    private void DropIdleRing() {
        if ((m_ring is not { } ring) || (((long)ring.SizeBytes) <= RingFloorBytes) || (CountBlocksIn(ring: ring) != 0)) { return; }
        ring.Dispose();
        m_ring = null;
        m_ringHead = 0L;
    }
    /// <summary>Releases what a staged region's <paramref name="slot"/> staged once the submission that last used the slot
    /// has retired, before the slot's next flush would: an owner that learns of the retirement early (a background job's
    /// completion) calls it, so a large flush's staging is given back at once rather than when the slot next flushes. A
    /// ring left with nothing staged and holding more than <see cref="StagingFloorBytes"/> is released with it. A slot
    /// flushed and not yet recorded keeps what it staged, and a disposed region, or one under another policy, does
    /// nothing.</summary>
    /// <param name="slot">The frame slot whose last submission has retired, below <see cref="SlotCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    public void Retire(int slot) {
        if (m_disposed || (Policy != GpuResidencyPolicy.Staged)) { return; }
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: SlotCount, value: slot);
        if (m_stagedSlot == slot) { return; }
        ReleaseBlock(slot: slot);
        DropIdleRing();
    }
    // Whether no live block of the current ring overlaps [offset, offset + bytes).
    private bool IsFree(long offset, long bytes) {
        foreach (var block in m_blocks) {
            if (ReferenceEquals(objA: block.Ring, objB: m_ring) && (block.Ring is not null) && (offset < (block.Offset + block.Bytes)) && (block.Offset < (offset + bytes))) { return false; }
        }
        return true;
    }
    private int CountBlocksIn(IGpuStorageBuffer ring) {
        var count = 0;

        foreach (var block in m_blocks) {
            if (ReferenceEquals(objA: block.Ring, objB: ring)) { count++; }
        }
        return count;
    }
    // Writes each owed word range of the contents into the buffer at the same words.
    private void WriteOwed(GpuUploadRuns owed, IGpuStorageBuffer buffer) {
        for (var run = 0; (run < owed.Count); run++) {
            var start = owed.Start(index: run);

            WriteHostWords(
                buffer: buffer,
                destination: (((ulong)start) * sizeof(uint)),
                length: owed.Length(index: run),
                startWord: start
            );
        }
    }
}
