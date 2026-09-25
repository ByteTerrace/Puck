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
/// (<see cref="GpuRegion(IGpuBuffer, int, int, IGpuBufferFactory, IGpuBindings, IGpuRecorder, IGpuComputePipeline, in GpuObjectName, GpuRegionCopySets)"/>):
/// its word 0 lands at the word <see cref="Target"/> names, and since other work may write the destination, a
/// retargeted region owes every word written after it.
/// </para>
/// <para>
/// The copy kernel reads the staging buffer as uints at <see cref="CopySourceBinding"/> and writes the destination at
/// <see cref="CopyDestinationBinding"/>, <see cref="CopyWorkgroupSize"/> threads per group, and takes no push constants:
/// the staging buffer states its copy. Its first <see cref="CopyHeaderWords"/> uints are <c>(count, runCount,
/// blockBase, destinationBase)</c>, then come <c>runCount</c> <c>(block offset, first thread)</c> pairs, and the block
/// starts at <c>blockBase</c>. Thread <c>i</c> (its dispatch row times <see cref="CopyRowThreads"/> plus its column,
/// <see cref="CopyGroups"/> sizing the dispatch) below <c>count</c> finds the last run whose first thread is at most
/// <c>i</c>, takes <c>word</c> as that run's block offset plus <c>i</c> minus its first thread, and copies
/// <c>destination[destinationBase + word] = source[blockBase + word]</c>. That kernel is <c>Puck.Shaders</c>'
/// <c>region-copy.comp</c>, created from <see cref="CopyPipeline"/> once per device and leased by every owner
/// (<c>GpuRegionCopyPipelineCache</c>). Its copy sets are its share of a <see cref="GpuRegionCopyPool"/>: one the region
/// creates for itself alone, or its owner's, reserved for all its regions when the owner was admitted, so a region the
/// owner creates at a later frame takes no descriptor range.
/// </para>
/// </summary>
public sealed class GpuRegion : IDisposable {
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
    // A run-table entry costs two words, so copying a gap of up to two unchanged words costs no more than another run.
    private const int StagedMergeGapWords = 2;

    private readonly byte[] m_contents;
    private readonly IGpuComputePipeline m_copyPipeline;
    // The staged policy's copy sets: its share of its owner's reserved pool, or of its own; null under the other
    // policies.
    private readonly GpuRegionCopySets? m_copySets;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuBuffer? m_destination;
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
    /// <param name="byteCount">The region's size in bytes; positive and a whole number of uints.</param>
    /// <param name="slotCount">The caller's frame slots; at least one.</param>
    /// <param name="buffers">The factory that creates the host-visible and device-local buffers.</param>
    /// <param name="bindings">The bindings service the staged policy's copy sets come from.</param>
    /// <param name="recorder">The recorder the staged policy's copy is recorded through.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, created from <see cref="CopyPipeline"/>; read only under the
    /// staged policy. The caller owns it and keeps it alive while the region records copies.</param>
    /// <param name="name">The region's debug name, from its creator's identity: each slot's host-visible buffer and copy
    /// set is named at its slot's index, and the device-local buffer and the copy pool bare.</param>
    /// <param name="copySets">The region's share of the copy pool its owner reserved (<see cref="GpuRegionCopyPool.Region"/>),
    /// which a staged region writes in place of creating a pool of its own, or <see langword="null"/> to create one
    /// (named by <paramref name="name"/>); read only under the staged policy. The owner keeps the pool after the region
    /// is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffers"/>,
    /// <paramref name="bindings"/>, <paramref name="recorder"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> or <paramref name="memory"/> is not a
    /// defined value, <paramref name="byteCount"/> is not positive or not a whole number of uints, or
    /// <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, int byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null) : this(
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
        slotCount: slotCount
    ) { }
    /// <summary>Initializes a new instance of the <see cref="GpuRegion"/> class that stages into an external
    /// destination: its policy is <see cref="GpuResidencyPolicy.Staged"/>, its word 0 lands at word 0 of
    /// <paramref name="destination"/> until <see cref="Target"/> moves it, and it owes nothing until a write, since what
    /// the destination holds is the owner's. <see cref="Contents"/> starts zeroed.</summary>
    /// <param name="destination">The device-local buffer the copy writes; the caller owns it and keeps it alive while the
    /// region records copies.</param>
    /// <param name="byteCount">The region's size in bytes; positive and a whole number of uints.</param>
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is not positive or not a whole number
    /// of uints, or <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="copySets"/> holds another number of slots than
    /// <paramref name="slotCount"/>.</exception>
    public GpuRegion(IGpuBuffer destination, int byteCount, int slotCount, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets = null) : this(
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
        slotCount: slotCount
    ) { }

    private GpuRegion(GpuResidencyPolicy policy, GpuHostVisibleMemory memory, int byteCount, int slotCount, IGpuBuffer? destination, IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline copyPipeline, in GpuObjectName name, GpuRegionCopySets? copySets) {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(copyPipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: byteCount);
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

        var words = (byteCount / sizeof(uint));

        ByteCount = byteCount;
        Memory = ((policy == GpuResidencyPolicy.Staged)
            ? GpuHostVisibleMemory.Host
            : memory
        );
        Policy = policy;
        SlotCount = slotCount;
        m_contents = new byte[byteCount];
        m_copyPipeline = copyPipeline;
        m_bindings = bindings;
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
                        usage: GpuBufferUsage.Storage
                    )
                    : buffers.CreateHostVisible(
                        name: name.At(index: index),
                        sizeBytes: hostBytes,
                        usage: GpuBufferUsage.Storage
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
                        copyPipeline: copyPipeline,
                        name: in name,
                        regions: [name],
                        slotCount: slotCount
                    );
                    copySets = m_ownedCopyPool.Region(index: 0);
                }

                m_copySets = copySets;

                for (var slot = 0; (slot < slotCount); slot++) {
                    WriteCopySet(slot: slot);
                }
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
            Kind: GpuComputeBindingKind.StorageBufferRead
        ),
        new GpuComputeBinding(
            Binding: CopyDestinationBinding,
            Kind: GpuComputeBindingKind.StorageBufferReadWrite
        ),
    ];
    /// <summary>Gets the copy kernel's pipeline description: <see cref="CopyBindings"/>, no push constants, and each
    /// register at its binding number.</summary>
    public static GpuComputePipelineDescription CopyPipeline { get; } = new(
        Bindings: CopyBindings,
        Name: "region-copy",
        PushConstantBinding: null,
        Registers: GpuRegisterNumbering.Binding
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

    /// <summary>Gets the region's size in bytes.</summary>
    public int ByteCount { get; }
    /// <summary>Gets the host's copy of the region: what every slot's <see cref="Buffer"/> holds once flushed and
    /// copied.</summary>
    public ReadOnlySpan<byte> Contents => m_contents;
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
    /// It is a storage buffer of <see cref="ByteCount"/> bytes or more, read as uints from its start, except an external
    /// destination, whose layout is its owner's.</summary>
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

        m_ownedCopyPool?.Dispose();

        foreach (var buffer in m_ownedBuffers) {
            buffer.Dispose();
        }

        m_ownedBuffers.Clear();
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
                    firstWord: 0,
                    owed: m_owed[slot]
                );
                m_owed[slot].Clear();

                break;
            case GpuResidencyPolicy.InPlace:
                WriteOwed(
                    buffer: m_hostBuffers[0],
                    firstWord: 0,
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="InvalidOperationException">The staged region owes words that <paramref name="slot"/>'s staging
    /// buffer does not hold, because the slot was not flushed after the last write.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void RecordCopy(nint commandBuffer, int slot) {
        RequireSlot(slot: slot);

        if (!OwesCopy) {
            return;
        }

        var owed = m_owed[0];

        if (m_stagedSlot != slot) {
            throw new InvalidOperationException(message: $"The region owes words slot {slot}'s staging buffer does not hold; flush the slot after the last write and before recording its copy.");
        }

        // Another region its owner's reserved sets serve wrote the slot's set since this one did, as a replacement the
        // owner abandoned does; the slot's last submission has retired, so the set is rewritten before it is bound.
        if (!m_copySets!.IsWrittenBy(
            region: this,
            slot: slot
        )) {
            WriteCopySet(slot: slot);
        }

        m_recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: m_copyPipeline.Handle
        );
        m_recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_copySets.SetOf(slot: slot),
            group: 0,
            pipelineLayoutHandle: m_copyPipeline.LayoutHandle
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
    public bool Write(int offset, ReadOnlySpan<byte> bytes) {
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
        var endWord = (((end - 1) / sizeof(uint)) + 1);

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
            value: (m_knownWords * sizeof(uint))
        );
        var owed = OweDifferences(
            bytes: bytes[..(knownEnd - offset)],
            offset: offset
        );

        if (knownEnd < end) {
            Owe(
                endWord: endWord,
                startWord: (knownEnd / sizeof(uint))
            );
            owed = true;
        }

        bytes.CopyTo(destination: m_contents.AsSpan(start: offset));

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
    private bool OweDifferences(ReadOnlySpan<byte> bytes, int offset) {
        var contents = m_contents.AsSpan(
            length: bytes.Length,
            start: offset
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
            index = Math.Min(
                val1: bytes.Length,
                val2: (((((offset + index) / sizeof(uint)) + 1) * sizeof(uint)) - offset)
            );

            while (index < bytes.Length) {
                var next = Math.Min(
                    val1: bytes.Length,
                    val2: (index + sizeof(uint))
                );

                if (bytes[index..next].SequenceEqual(other: contents[index..next])) {
                    break;
                }

                index = next;
            }

            Owe(
                endWord: ((((offset + index) - 1) / sizeof(uint)) + 1),
                startWord: ((offset + start) / sizeof(uint))
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
    // Stages every owed word at the slot's staging buffer's block, then the header and run table in front of it: the
    // copy's thread count, run count, block base and destination word, then per run its block offset and first thread.
    private void StageOwed(int slot) {
        var owed = m_owed[0];

        if (owed.Count == 0) {
            m_stagedSlot = slot;

            return;
        }

        var prefix = 0;

        WriteOwed(
            buffer: m_hostBuffers[slot],
            firstWord: CopyBlockBase,
            owed: owed
        );

        for (var run = 0; (run < owed.Count); run++) {
            m_header[(CopyHeaderWords + (run * 2))] = ((uint)owed.Start(index: run));
            m_header[((CopyHeaderWords + (run * 2)) + 1)] = ((uint)prefix);
            prefix += owed.Length(index: run);
        }

        m_header[0] = ((uint)prefix);
        m_header[1] = ((uint)owed.Count);
        m_header[2] = CopyBlockBase;
        m_header[3] = ((uint)m_destinationWord);
        m_hostBuffers[slot].Write<uint>(data: m_header.AsSpan(
            length: (CopyHeaderWords + (owed.Count * 2)),
            start: 0
        ));
        m_stagedSlot = slot;
    }
    // Writes each owed word range of the contents into the buffer, word w landing at word firstWord + w.
    private void WriteOwed(GpuUploadRuns owed, IGpuStorageBuffer buffer, int firstWord) {
        for (var run = 0; (run < owed.Count); run++) {
            var start = (owed.Start(index: run) * sizeof(uint));

            buffer.Write<byte>(
                data: m_contents.AsSpan(
                    length: (owed.Length(index: run) * sizeof(uint)),
                    start: start
                ),
                destinationOffsetBytes: ((((ulong)firstWord) * sizeof(uint)) + ((ulong)start))
            );
        }
    }
}
