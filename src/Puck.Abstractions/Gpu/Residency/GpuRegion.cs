using System.Runtime.InteropServices;

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
/// the owed words into the slot's host-visible staging buffer, and one dispatch of the copy kernel moves them into the
/// one device-local buffer every slot binds; the caller orders that dispatch after earlier readers of the buffer and
/// before this frame's, as it orders any compute write.
/// </para>
/// <para>
/// A staged region's destination is either its own device-local buffer or an external one the owner keeps
/// (<see cref="GpuRegion(IGpuBuffer, long, int, IGpuBufferFactory, IGpuBindings, IGpuRecorder, IGpuComputePipeline, in GpuObjectName, GpuRegionCopySets)"/>):
/// its word 0 lands at the word <see cref="Target"/> names, and since other work may write the destination, a
/// retargeted region owes every word written after it.
/// </para>
/// <para>
/// A staged region that owns its destination grows (<see cref="Grow"/>) without waiting for its readers: it creates a
/// larger destination, and its next copy recording clears it and copies the old destination's bytes into it on the GPU
/// (<see cref="RecordCarry"/>), so growing writes no carried word from the host and leaves the old destination to the
/// caller, who disposes it once that recording's submission and every earlier reader have completed. Its host copy lives
/// in native memory, so a large region costs the managed heap nothing and growing it allocates no managed array.
/// </para>
/// <para>
/// The copy kernel reads the staging buffer as uints at <see cref="CopySourceBinding"/> and writes the destination at
/// <see cref="CopyDestinationBinding"/>, <see cref="CopyWorkgroupSize"/> threads per group, and takes no push constants:
/// the staging buffer states its copy. Its first <see cref="CopyHeaderWords"/> uints are <c>(count, runCount,
/// blockBase, destinationBase)</c>, then come <c>runCount</c> <c>(region word, first thread)</c> pairs, and the block
/// starts at <c>blockBase</c>, holding the owed runs packed one after another. Thread <c>i</c> (its dispatch row times
/// <see cref="CopyRowThreads"/> plus its column, <see cref="CopyGroups"/> sizing the dispatch) below <c>count</c> finds
/// the last run whose first thread is at most <c>i</c>, takes <c>word</c> as that run's region word plus <c>i</c> minus
/// its first thread, and copies <c>destination[destinationBase + word] = source[blockBase + i]</c>. A staging buffer
/// therefore holds only what its slot owes, and grows when a flush owes more. That kernel is <c>Puck.Shaders</c>'
/// <c>region-copy.comp</c>, created from <see cref="CopyPipeline"/> once per device and leased by every owner
/// (<c>GpuRegionCopyPass</c>, an entry of the pass-pipeline cache). Its copy sets are its share of a <see cref="GpuRegionCopyPool"/>: one the region
/// creates for itself alone, or its owner's, reserved for all its regions when the owner was admitted, so a region the
/// owner creates at a later frame takes no descriptor range.
/// </para>
/// </summary>
public sealed unsafe class GpuRegion : IDisposable {
    /// <summary>The copy kernel's binding for the destination buffer it writes.</summary>
    public const uint CopyDestinationBinding = 1U;
    /// <summary>The uints of the header a staging buffer leads with: the copy's thread count, its run count, where the
    /// block starts and the destination word the block's word 0 lands at.</summary>
    public const int CopyHeaderWords = 4;
    /// <summary>The copy kernel's binding for the staging buffer it reads.</summary>
    public const uint CopySourceBinding = 0U;
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
    /// <summary>The most separate word ranges one copy carries; past it neighbouring ranges pair up and the words
    /// between them are copied again.</summary>
    public const int MaxCopyRuns = 256;

    // Where a staging buffer's block starts, in uints: past the header and the run-table reserve of two uints per run.
    private const int CopyBlockBase = (CopyHeaderWords + (MaxCopyRuns * 2));
    // The most separate owed ranges a host-written buffer holds before neighbours pair up.
    private const int HostRunCapacity = 8;
    /// <summary>The largest gap of unchanged words a staged region's owed runs join across: a run-table entry costs two
    /// words, so copying a gap of up to two costs no more than another run.</summary>
    public const int StagedMergeGapWords = 2;
    /// <summary>The most bytes a region holds: a staged region's staging buffer, the header and run-table reserve before
    /// the whole region, binds as one storage view, and Vulkan states a storage view's range
    /// (<c>maxStorageBufferRange</c>) in a uint32, so it is the whole uints below 4 GiB less that reserve. Every word
    /// then fits the copy kernel's uint indices and the region's own int word counts.</summary>
    public const long MaxByteCount = ((((long)(uint.MaxValue / sizeof(uint))) * sizeof(uint)) - (((long)CopyBlockBase) * sizeof(uint)));
    // The most bytes one host write moves: a span's length is an int, so a longer run is written in pieces.
    private const int WriteChunkBytes = (1 << 30);

    // The host's copy of the region, ByteCount bytes of native memory the region frees when disposed.
    private byte* m_contents;
    private readonly IGpuComputePipeline? m_copyPipeline;

    // The staged policy's copy sets: its share of its owner's reserved pool, or of its own; null under the other
    // policies. MoveCopySets hands a reserved region another share.
    private GpuRegionCopySets? m_copySets;

    private readonly IGpuBindings m_bindings;
    private readonly IGpuBufferFactory m_buffers;
    private readonly GpuObjectName m_name;
    private readonly GpuBufferUsage m_usage;
    // The destination a staged region's copies write; replaced when the region grows.
    private IGpuBuffer? m_destination;
    // The destination a grown region's next copy recording carries its bytes from, and how many; null when none is owed.
    private IGpuBuffer? m_carrySource;
    private long m_carryBytes;
    private readonly bool m_external;
    // The copy pool the region created for itself, which it destroys with it; null when its owner reserved its sets.
    private readonly GpuRegionCopyPool? m_ownedCopyPool;
    private readonly List<IGpuBuffer> m_ownedBuffers = [];
    // The ranges each host-visible buffer still owes: one list under InPlace and Staged, one per slot under Ring.
    private readonly GpuUploadRuns[] m_owed;
    private readonly IGpuRecorder m_recorder;
    // The header and run table a staged flush writes at the front of the slot's staging buffer.
    private readonly uint[] m_header;
    private readonly IGpuStorageBuffer[] m_hostBuffers;

    private int m_destinationWord;
    private bool m_disposed;
    // The leading words the destination is known to hold as Contents once every owed word has reached it: the whole
    // region for its own buffers, which start owing everything, and none for an external destination until a write
    // after the last retarget covers them.
    private int m_knownWords;
    // The slot whose staging buffer holds every owed word, or -1 when a write has owed more since.
    private int m_stagedSlot = -1;

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
    /// <exception cref="ArgumentNullException"><paramref name="buffers"/>,
    /// <paramref name="bindings"/> or <paramref name="recorder"/> is <see langword="null"/>, or a staged region has no
    /// <paramref name="copyPipeline"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> or <paramref name="memory"/> is not a
    /// defined value, <paramref name="byteCount"/> is not positive, not a whole number of uints or past
    /// <see cref="MaxByteCount"/>,
    /// <paramref name="slotCount"/> is not positive, <paramref name="usage"/> is neither storage nor uniform, or a
    /// uniform region is staged or not a multiple of <see cref="IGpuBindings.ConstantBufferAlignment"/> bytes.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, long byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline? copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null, GpuBufferUsage usage = GpuBufferUsage.Storage) : this(
        bindings: bindings,
        buffers: buffers,
        byteCount: byteCount,
        copyPipeline: copyPipeline,
        copySets: copySets,
        destination: null,
        memory: memory,
        name: in name,
        policy: policy,
        recorder: recorder,
        slotCount: slotCount,
        usage: usage
    ) { }
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
    /// <exception cref="ArgumentNullException"><paramref name="destination"/>, <paramref name="buffers"/>,
    /// <paramref name="bindings"/>, <paramref name="recorder"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not positive, not a whole number
    /// of uints or past <see cref="MaxByteCount"/>, or <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(IGpuBuffer destination, long byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null) : this(
        bindings: bindings,
        buffers: buffers,
        byteCount: byteCount,
        copyPipeline: copyPipeline,
        copySets: copySets,
        destination: (destination ?? throw new ArgumentNullException(paramName: nameof(destination))),
        memory: GpuHostVisibleMemory.Host,
        name: in name,
        policy: GpuResidencyPolicy.Staged,
        recorder: recorder,
        slotCount: slotCount,
        usage: GpuBufferUsage.Storage
    ) { }

    private GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, long byteCount, int slotCount, GpuBufferUsage usage, IGpuBuffer? destination, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline? copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets) {
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
            if ((((ulong)byteCount) % IGpuBindings.ConstantBufferAlignment) != 0) {
                throw new ArgumentOutOfRangeException(
                    actualValue: byteCount,
                    message: $"A uniform region holds a multiple of {IGpuBindings.ConstantBufferAlignment} bytes, the constant-buffer view it is bound as.",
                    paramName: nameof(byteCount)
                );
            }
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

        var words = ((int)(byteCount / sizeof(uint)));

        ByteCount = byteCount;
        Memory = ((policy == GpuResidencyPolicy.Staged)
            ? GpuHostVisibleMemory.Host
            : memory
        );
        Policy = policy;
        SlotCount = slotCount;
        m_contents = ((byte*)NativeMemory.AllocZeroed(byteCount: ((nuint)byteCount)));
        m_copyPipeline = copyPipeline;
        m_bindings = bindings;
        m_buffers = buffers;
        m_name = name;
        m_usage = usage;
        m_destination = destination;
        m_external = (destination is not null);
        m_recorder = recorder;
        m_header = new uint[((policy == GpuResidencyPolicy.Staged)
            ? CopyBlockBase
            : 0
        )];
        m_owed = new GpuUploadRuns[((policy == GpuResidencyPolicy.Ring)
            ? slotCount
            : 1
        )];

        for (var index = 0; (index < m_owed.Length); index++) {
            m_owed[index] = ((policy == GpuResidencyPolicy.Staged)
                ? new GpuUploadRuns(
                    capacity: MaxCopyRuns,
                    mergeGap: StagedMergeGapWords
                )
                : new GpuUploadRuns(capacity: HostRunCapacity)
            );

            if (!m_external) {
                m_owed[index].Add(
                    length: words,
                    start: 0
                );
            }
        }

        m_knownWords = (m_external
            ? 0
            : words
        );
        m_hostBuffers = new IGpuStorageBuffer[((policy == GpuResidencyPolicy.InPlace)
            ? 1
            : slotCount
        )];

        try {
            var hostBytes = ((policy == GpuResidencyPolicy.Staged)
                ? ((((ulong)CopyBlockBase) * sizeof(uint)) + ((ulong)byteCount))
                : ((ulong)byteCount)
            );

            for (var index = 0; (index < m_hostBuffers.Length); index++) {
                m_hostBuffers[index] = ((Memory == GpuHostVisibleMemory.DeviceLocal)
                    ? buffers.CreateHostVisibleDeviceLocal(
                        name: name.At(index: index),
                        sizeBytes: hostBytes,
                        usage: usage
                    )
                    : buffers.CreateHostVisible(
                        name: name.At(index: index),
                        sizeBytes: hostBytes,
                        usage: usage
                    )
                );
                m_ownedBuffers.Add(item: m_hostBuffers[index]);
            }

            if (policy == GpuResidencyPolicy.Staged) {
                if (!m_external) {
                    m_destination = buffers.CreateDeviceLocal(
                        name: name,
                        sizeBytes: ((ulong)byteCount),
                        usage: GpuBufferUsage.Storage
                    );
                    m_ownedBuffers.Add(item: m_destination);
                }

                if (copySets is null) {
                    m_ownedCopyPool = new GpuRegionCopyPool(
                        bindings: bindings,
                        copyPipeline: copyPipeline!,
                        name: in name,
                        regions: [name],
                        slotCount: slotCount
                    );
                    copySets = m_ownedCopyPool.Region(index: 0);
                }

                // No slot's copy set is written here: RecordCopy writes a slot's set the first time that slot records a
                // copy, inside the pass that records it, so the writes are counted where the upload's work is.
                m_copySets = copySets;
            }
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Gets the copy kernel's descriptor set: the staging buffer it reads at <see cref="CopySourceBinding"/> and
    /// the destination it writes at <see cref="CopyDestinationBinding"/>.</summary>
    public static IReadOnlyList<GpuComputeBinding> CopyBindings { get; } = [
        new GpuComputeBinding(
            Binding: CopySourceBinding,
            Kind: GpuBindingKind.ReadOnlyBuffer
        ),
        new GpuComputeBinding(
            Binding: CopyDestinationBinding,
            Kind: GpuBindingKind.ReadWriteBuffer
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
    /// <see cref="CopyMaxGroupsPerDimension"/> groups, and as many full rows of that width as it takes past one.</summary>
    /// <param name="count">The words the copy carries; at least one.</param>
    /// <returns>The groups along the dispatch's first and second dimensions.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero.</exception>
    public static (uint X, uint Y) CopyGroups(uint count) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);

        var groups = ((((ulong)count) + (CopyWorkgroupSize - 1UL)) / CopyWorkgroupSize);

        return ((groups <= CopyMaxGroupsPerDimension)
            ? (((uint)groups), 1U)
            : (CopyMaxGroupsPerDimension, ((uint)((groups + (CopyMaxGroupsPerDimension - 1UL)) / CopyMaxGroupsPerDimension)))
        );
    }
    /// <summary>Returns the bytes of the buffers a region that owns its destination creates: one host-visible buffer per
    /// slot under the ring, the one shared buffer in place, and under the staged policy one staging buffer per slot (the
    /// header and run-table reserve before the region's bytes) and the device-local destination. It is each buffer's
    /// logical size, not the backend's allocation.</summary>
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
            GpuResidencyPolicy.Staged => checked(((((((ulong)CopyBlockBase) * sizeof(uint)) + ((ulong)byteCount)) * ((ulong)slotCount)) + ((ulong)byteCount))),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: policy,
                message: "The residency policy is not defined.",
                paramName: nameof(policy)
            ),
        };
    }

    /// <summary>Gets the bytes of the buffers the region holds, by the memory they live in: its host-visible buffers
    /// (one per slot under the ring or staged, one in place; a staged buffer carries the header and run-table reserve
    /// before the words it stages) in the memory <see cref="Memory"/> names, and a staged region's own device-local
    /// destination. A destination the region was handed, or one it replaced by growing, is its owner's, not counted
    /// here. Each is a buffer's logical size, <see cref="BytesOf"/> until a staging buffer or the region grows.</summary>
    public GpuMemoryBytes OwnedBytes {
        get {
            var host = 0UL;

            foreach (var buffer in m_hostBuffers) {
                host = checked((host + buffer.SizeBytes));
            }

            var destination = (((Policy == GpuResidencyPolicy.Staged) && !m_external)
                ? ((ulong)ByteCount)
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
    /// <summary>Gets the host's copy of the whole region: what every slot's <see cref="Buffer"/> holds once flushed and
    /// copied. The span reads native memory the region frees when disposed or moves when it grows, so it is read before
    /// either. A span counts its length in an int, so a region past <see cref="int.MaxValue"/> bytes is read a piece at
    /// a time through <see cref="ContentsAt"/>.</summary>
    /// <exception cref="InvalidOperationException">The region holds more bytes than one span reaches.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public ReadOnlySpan<byte> Contents {
        get {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );

            if (ByteCount > int.MaxValue) {
                throw new InvalidOperationException(message: $"The region's {ByteCount} bytes are past what one span reaches; read it through ContentsAt.");
            }

            return new ReadOnlySpan<byte>(pointer: m_contents, length: ((int)ByteCount));
        }
    }
    /// <summary>Returns <paramref name="length"/> bytes of the host's copy of the region from <paramref name="offset"/>,
    /// as <see cref="Contents"/> holds them. The span reads native memory the region frees when disposed or moves when it
    /// grows, so it is read before either.</summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="length">The bytes.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative,
    /// or the span runs past <see cref="ByteCount"/>.</exception>
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
    /// <summary>Gets the retained CPU shadow payload bytes: the native host copy's size.</summary>
    public ulong CpuShadowBytes => ((ulong)ByteCount);
    /// <summary>Gets whether the region has grown since its last copy recording, so its next one clears the new
    /// destination and carries the old one's bytes into it (<see cref="RecordCarry"/>).</summary>
    public bool OwesCarry => (m_carrySource is not null);
    /// <summary>Gets the retained CPU upload-header scratch payload bytes, excluding managed array overhead.</summary>
    public ulong CpuScratchBytes => (((ulong)m_header.LongLength) * sizeof(uint));
    /// <summary>Gets the handle of the pipeline <see cref="RecordCopy"/> dispatches, or zero for a region that copies
    /// nothing.</summary>
    public nint CopyPipelineHandle => (m_copyPipeline?.Handle ?? 0);
    /// <summary>Gets whether the next <see cref="RecordCopy"/> records a copy: the region is staged and owes words.</summary>
    public bool OwesCopy => (
        (Policy == GpuResidencyPolicy.Staged) &&
        !m_disposed &&
        (m_owed[0].Count > 0)
    );
    /// <summary>Gets where the buffers the region's readers bind directly live: its ring's or in-place buffer's memory,
    /// and <see cref="GpuHostVisibleMemory.Host"/> for a staged region, whose staging buffers are host memory.</summary>
    public GpuHostVisibleMemory Memory { get; }
    /// <summary>Gets the residency policy the region was created under.</summary>
    public GpuResidencyPolicy Policy { get; }
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

        m_copySets?.ForgetWriter(region: this);
        m_ownedCopyPool?.Dispose();

        foreach (var buffer in m_ownedBuffers) {
            buffer.Dispose();
        }

        m_ownedBuffers.Clear();
        m_carrySource = null;
        NativeMemory.Free(ptr: m_contents);
        m_contents = null;
    }
    /// <summary>Grows a staged region that owns its destination to <paramref name="byteCount"/> bytes without waiting
    /// for any submission: it creates a destination of the new size, extends <see cref="Contents"/> with zeros, and owes
    /// its next copy recording a carry (<see cref="RecordCarry"/>) that clears the new destination and copies the old
    /// one's bytes into it on the GPU, so no word the region held is staged again. Owed words stay owed, the copy sets
    /// are written again before each slot's next copy, and no staging buffer is created: one grows when a flush owes more
    /// than it holds.</summary>
    /// <param name="byteCount">The new size in bytes; larger than <see cref="ByteCount"/>, a whole number of uints and at
    /// most <see cref="MaxByteCount"/>.</param>
    /// <returns>The destination the region replaced, which the caller now owns: GPU work recorded before the grow may
    /// still read it, and the next copy recording's submission reads it once more, so the caller disposes it only once
    /// every such submission has completed. A region that grows again before a copy recording carries from the
    /// destination it held at its last recording and returns the one it never wrote.</returns>
    /// <exception cref="InvalidOperationException">The region is not staged, or its destination is external.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not larger than
    /// <see cref="ByteCount"/>, not a whole number of uints or past <see cref="MaxByteCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public IGpuBuffer Grow(long byteCount) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (Policy != GpuResidencyPolicy.Staged) ||
            m_external
        ) {
            throw new InvalidOperationException(message: "Only a staged region that owns its destination grows.");
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

        var replacement = m_buffers.CreateDeviceLocal(
            name: m_name,
            sizeBytes: ((ulong)byteCount),
            usage: GpuBufferUsage.Storage
        );
        var replaced = m_destination!;

        // A zeroed allocation takes fresh pages the system zeroes as they are first touched, so only the bytes held are
        // copied, where reallocating would also clear the new tail.
        var contents = ((byte*)NativeMemory.AllocZeroed(byteCount: ((nuint)byteCount)));

        System.Buffer.MemoryCopy(
            destination: contents,
            destinationSizeInBytes: byteCount,
            source: m_contents,
            sourceBytesToCopy: ByteCount
        );
        NativeMemory.Free(ptr: m_contents);
        m_contents = contents;
        _ = m_ownedBuffers.Remove(item: replaced);
        m_ownedBuffers.Add(item: replacement);

        if (m_carrySource is null) {
            m_carrySource = replaced;
            m_carryBytes = ByteCount;
        }

        m_destination = replacement;
        ByteCount = byteCount;
        m_knownWords = ((int)(byteCount / sizeof(uint)));
        m_copySets!.ForgetWriter(region: this);

        return replaced;
    }
    /// <summary>Records a grown region's carry: the clear of its destination, then the copy of the bytes the destination
    /// it replaced held, each ordered after the last, and the barrier handing the destination to the next access, the
    /// copy kernel's write when <paramref name="copies"/> and otherwise <paramref name="readers"/>. The replaced
    /// destination is read as a copy source after <paramref name="readers"/>' earlier reads of it. Nothing is recorded
    /// when no carry is owed (<see cref="OwesCarry"/>).</summary>
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

        if (m_carrySource is not { } source) {
            return;
        }

        var destination = m_destination!.BufferHandle;

        m_recorder.ClearStorageBuffer(
            bufferHandle: destination,
            commandBufferHandle: commandBuffer,
            sizeBytes: ((ulong)ByteCount)
        );
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

        var owed = m_owed[((Policy == GpuResidencyPolicy.Ring)
            ? slot
            : 0)];

        owed.Clear();
        owed.Add(
            length: ((int)(ByteCount / sizeof(uint))),
            start: 0
        );
        m_stagedSlot = -1;
    }
    /// <summary>Writes what <paramref name="slot"/> owes into its host-visible buffer: the words changed since the slot
    /// was last flushed under the ring, since the last flush under in place, and since the last recorded copy under the
    /// staged policy, whose owed words go to the slot's staging buffer behind the header and run table the copy reads.
    /// A slot owing nothing writes nothing.</summary>
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
                    owed: m_owed[slot]
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
    /// every owed word, after which nothing is owed until the next write. It records no barrier: the caller makes the
    /// destination's writes visible to its readers. Under the other policies, or with nothing owed, it records
    /// nothing.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="slot">The frame slot the command buffer belongs to, flushed since the last write.</param>
    /// <param name="pipelineBound">Whether the command buffer already has the copy pipeline (<see cref="CopyPipelineHandle"/>)
    /// bound, as after an earlier region's copy in one recording, so the copy binds only its set.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="InvalidOperationException">The staged region owes words that <paramref name="slot"/>'s staging
    /// buffer does not hold, because the slot was not flushed after the last write.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void RecordCopy(nint commandBuffer, int slot, bool pipelineBound = false) {
        RequireSlot(slot: slot);

        if (!OwesCopy) {
            return;
        }

        var owed = m_owed[0];

        if (m_stagedSlot != slot) {
            throw new InvalidOperationException(message: $"The region owes words slot {slot}'s staging buffer does not hold; flush the slot after the last write and before recording its copy.");
        }

        // The set is unwritten, or another region its owner's reserved sets serve wrote it since this one did; the
        // slot's last submission has retired, so the set is written before it is bound.
        if (!m_copySets!.IsWrittenBy(
            region: this,
            slot: slot
        )) {
            WriteCopySet(slot: slot);
        }

        if (!pipelineBound) {
            m_recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_copyPipeline!.Handle
            );
        }
        m_recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_copySets.SetOf(slot: slot),
            group: 0,
            pipelineLayoutHandle: m_copyPipeline!.LayoutHandle
        );
        var (groupsX, groupsY) = CopyGroups(count: m_header[0]);

        m_recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: groupsX,
            groupCountY: groupsY,
            groupCountZ: 1
        );
        owed.Clear();
        m_stagedSlot = -1;
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
    /// (an external destination's, after a retarget).</summary>
    /// <param name="offset">The byte offset the bytes start at.</param>
    /// <param name="bytes">The new contents of that span.</param>
    /// <returns><see langword="true"/> when any word was owed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, the span runs past
    /// <see cref="ByteCount"/>, or it lands past an external destination's end.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool Write(long offset, ReadOnlySpan<byte> bytes) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (ByteCount - bytes.Length),
            value: offset
        );

        if (bytes.IsEmpty) {
            return false;
        }

        var end = (offset + bytes.Length);
        var endWord = ((int)(((end - 1) / sizeof(uint)) + 1));

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> runs past <see cref="ByteCount"/>, or a run
    /// past the end of <paramref name="words"/>.</exception>
    /// <exception cref="InvalidOperationException">The region's destination is external, whose contents the region does
    /// not know.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public bool WriteRuns(ReadOnlySpan<uint> words, GpuUploadRuns runs) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: runs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (ByteCount / sizeof(uint)),
            value: ((long)words.Length)
        );

        if (m_external) {
            throw new InvalidOperationException(message: "A region with an external destination writes by comparing, since it does not know what its destination holds.");
        }

        for (var run = 0; (run < runs.Count); run++) {
            var start = runs.Start(index: run);
            var length = runs.Length(index: run);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                other: words.Length,
                value: (start + length),
                paramName: nameof(runs)
            );
            words.Slice(
                length: length,
                start: start
            ).CopyTo(destination: new Span<uint>(
                length: length,
                pointer: (((uint*)m_contents) + start)
            ));
            Owe(
                endWord: (start + length),
                startWord: start
            );
        }

        if (runs.Count == 0) {
            return false;
        }

        m_stagedSlot = -1;

        return true;
    }
    // Writes the slot's staging buffer as the source and the destination as the destination into the slot's copy set.
    private void WriteCopySet(int slot) {
        var set = m_copySets!.SetOf(slot: slot);

        m_bindings.WriteBuffer(
            binding: CopySourceBinding,
            bufferHandle: m_hostBuffers[slot].BufferHandle,
            bufferSize: m_hostBuffers[slot].SizeBytes,
            descriptorSetHandle: set,
            elementStride: sizeof(uint),
            kind: GpuBindingKind.ReadOnlyBuffer
        );
        m_bindings.WriteBuffer(
            binding: CopyDestinationBinding,
            bufferHandle: m_destination!.BufferHandle,
            bufferSize: m_destination.SizeBytes,
            descriptorSetHandle: set,
            elementStride: sizeof(uint),
            kind: GpuBindingKind.ReadWriteBuffer
        );
        m_copySets.WrittenBy(
            region: this,
            slot: slot
        );
    }
    // Owes words [startWord, endWord) in every buffer.
    private void Owe(int startWord, int endWord) {
        foreach (var owed in m_owed) {
            owed.Add(
                length: (endWord - startWord),
                start: startWord
            );
        }
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
                endWord: ((int)((((offset + index) - 1) / sizeof(uint)) + 1)),
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
    // Length bytes of the host copy from offset as a writable span; the copy moves when the region grows.
    private Span<byte> HostSpan(long offset, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (ByteCount - length),
            value: offset
        );

        return new Span<byte>(
            length: length,
            pointer: (m_contents + offset)
        );
    }
    // Writes the words [startWord, startWord + length) of the host copy into a buffer from byte destination, a piece of
    // at most WriteChunkBytes at a time, since a span's length is an int.
    private void WriteHostWords(IGpuStorageBuffer buffer, int startWord, int length, ulong destination) {
        var offset = (((long)startWord) * sizeof(uint));
        var remaining = (((long)length) * sizeof(uint));

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

    // Stages every owed run packed one after another at the slot's staging buffer's block, growing the buffer when it is
    // too small (the slot's last submission has retired, so its buffer is free), then the header and run table in front
    // of it: the copy's thread count, run count, block base and destination word, then per run its region word and first
    // thread, which is also where its words start in the block.
    private void StageOwed(int slot) {
        var owed = m_owed[0];

        if (owed.Count == 0) {
            m_stagedSlot = slot;

            return;
        }

        var words = 0L;

        for (var run = 0; (run < owed.Count); run++) {
            words += owed.Length(index: run);
        }

        var buffer = StagingFor(
            bytes: checked(((((ulong)CopyBlockBase) + ((ulong)words)) * sizeof(uint))),
            slot: slot
        );
        var prefix = 0;

        for (var run = 0; (run < owed.Count); run++) {
            var length = owed.Length(index: run);

            WriteHostWords(
                buffer: buffer,
                destination: ((((ulong)CopyBlockBase) + ((ulong)prefix)) * sizeof(uint)),
                length: length,
                startWord: owed.Start(index: run)
            );
            m_header[(CopyHeaderWords + (run * 2))] = ((uint)owed.Start(index: run));
            m_header[((CopyHeaderWords + (run * 2)) + 1)] = ((uint)prefix);
            prefix += length;
        }

        m_header[0] = ((uint)prefix);
        m_header[1] = ((uint)owed.Count);
        m_header[2] = CopyBlockBase;
        m_header[3] = ((uint)m_destinationWord);
        buffer.Write<uint>(data: m_header.AsSpan(
            length: (CopyHeaderWords + (owed.Count * 2)),
            start: 0
        ));
        m_stagedSlot = slot;
    }
    // Returns the slot's staging buffer, replaced first by one of at least the bytes asked, twice its size or the whole
    // region's reserve if larger, when it holds fewer; the copy sets are written again before each slot's next copy.
    private IGpuStorageBuffer StagingFor(int slot, ulong bytes) {
        var buffer = m_hostBuffers[slot];

        if (bytes <= buffer.SizeBytes) {
            return buffer;
        }

        var replacement = m_buffers.CreateHostVisible(
            name: m_name.At(index: slot),
            sizeBytes: Math.Max(
                val1: bytes,
                val2: Math.Min(
                    val1: (2UL * buffer.SizeBytes),
                    val2: ((((ulong)CopyBlockBase) * sizeof(uint)) + ((ulong)ByteCount))
                )
            ),
            usage: m_usage
        );

        _ = m_ownedBuffers.Remove(item: buffer);
        buffer.Dispose();
        m_ownedBuffers.Add(item: replacement);
        m_hostBuffers[slot] = replacement;
        m_copySets!.ForgetWriter(region: this);

        return replacement;
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
