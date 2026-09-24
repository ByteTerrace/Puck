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
/// The copy kernel reads the staging buffer as uints at <see cref="CopySourceBinding"/> and writes the device-local
/// buffer at <see cref="CopyDestinationBinding"/>, <see cref="CopyWorkgroupSize"/> threads per group, with the push
/// constants <c>(count, runCount, offset, tableBase)</c> in uints: thread <c>i</c> below <c>count</c> copies
/// <c>destination[word] = source[tableBase + word]</c>, where <c>word</c> is <c>offset + i</c> for one run and, for two
/// or more, is found in the <c>(table offset, first thread)</c> pairs the staging buffer leads with. The SDF engine's
/// <c>sdf-frame-upload.comp</c> is that kernel.
/// </para>
/// </summary>
public sealed class GpuRegion : IDisposable {
    /// <summary>The copy kernel's binding for the device-local buffer it writes.</summary>
    public const uint CopyDestinationBinding = 1U;
    /// <summary>The copy kernel's push-constant block in bytes: four uints.</summary>
    public const int CopyPushByteLength = (sizeof(uint) * 4);
    /// <summary>The copy kernel's binding for the staging buffer it reads.</summary>
    public const uint CopySourceBinding = 0U;
    /// <summary>The copy kernel's threads per group.</summary>
    public const uint CopyWorkgroupSize = 64U;
    /// <summary>The most separate word ranges one copy carries; past it neighbouring ranges pair up and the words
    /// between them are copied again.</summary>
    public const int MaxCopyRuns = 256;
    /// <summary>The most words a staged region holds: its first copy is one one-dimensional dispatch, and one dispatch
    /// dimension carries at most 65,535 groups on both backends (Vulkan's guaranteed <c>maxComputeWorkGroupCount</c>,
    /// Direct3D 12's <c>D3D12_CS_DISPATCH_MAX_THREAD_GROUPS_PER_DIMENSION</c>).</summary>
    public const int MaxStagedWords = (65_535 * ((int)CopyWorkgroupSize));

    // The staging buffer's leading run-table reserve, in uints: two per run.
    private const int CopyRunTableWords = (MaxCopyRuns * 2);
    // The most separate owed ranges a host-written buffer holds before neighbours pair up.
    private const int HostRunCapacity = 8;
    // A run-table entry costs two words, so copying a gap of up to two unchanged words costs no more than another run.
    private const int StagedMergeGapWords = 2;

    private readonly byte[] m_contents;
    private readonly IGpuComputePipeline m_copyPipeline;
    private readonly nint[] m_copySets;
    private readonly IGpuDescriptorAllocator m_descriptors;
    private readonly nint m_deviceHandle;
    private readonly IGpuBuffer? m_deviceLocal;

    private readonly List<IGpuBuffer> m_ownedBuffers = [];

    // The ranges each host-visible buffer still owes: one list under InPlace and Staged, one per slot under Ring.
    private readonly GpuUploadRuns[] m_owed;

    private readonly byte[] m_push = new byte[CopyPushByteLength];

    private readonly IGpuComputeRecorder m_recorder;
    private readonly uint[] m_runTable;
    private readonly IGpuStorageBuffer[] m_hostBuffers;

    private nint m_copyPool;
    private bool m_disposed;

    // The slot whose staging buffer holds every owed word, or -1 when a write has owed more since.
    private int m_stagedSlot = -1;

    /// <summary>Initializes a new instance of the <see cref="GpuRegion"/> class, creating its buffers and, under the
    /// staged policy, one copy descriptor set per slot. Every buffer starts owing the whole region, since a new
    /// buffer's contents are undefined, and <see cref="Contents"/> starts zeroed.</summary>
    /// <param name="policy">The residency policy, normally <see cref="GpuResidency.Select"/>'s choice.</param>
    /// <param name="byteCount">The region's size in bytes; positive and a whole number of uints.</param>
    /// <param name="slotCount">The caller's frame slots; at least one.</param>
    /// <param name="device">The device the buffers live on.</param>
    /// <param name="buffers">The factory that creates the host-visible and device-local buffers.</param>
    /// <param name="descriptors">The allocator the staged policy's copy sets come from.</param>
    /// <param name="recorder">The recorder the staged policy's copy is recorded through.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, built from <see cref="CopyBindings"/> and a
    /// <see cref="CopyPushByteLength"/>-byte push range; read only under the staged policy. The caller owns it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/>, <paramref name="buffers"/>,
    /// <paramref name="descriptors"/>, <paramref name="recorder"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is not a defined policy,
    /// <paramref name="byteCount"/> is not positive or not a whole number of uints, <paramref name="slotCount"/> is not
    /// positive, or a staged region holds more than <see cref="MaxStagedWords"/> words.</exception>
    public GpuRegion(GpuResidencyPolicy policy, int byteCount, int slotCount, IGpuDeviceContext device, IGpuStorageBufferFactory buffers, IGpuDescriptorAllocator descriptors, IGpuComputeRecorder recorder, IGpuComputePipeline copyPipeline) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(copyPipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: byteCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: slotCount);

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

        var words = (byteCount / sizeof(uint));

        if (
            (policy == GpuResidencyPolicy.Staged) &&
            (words > MaxStagedWords)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: byteCount,
                message: $"A staged region holds at most {MaxStagedWords} words, which one copy dispatch carries.",
                paramName: nameof(byteCount)
            );
        }

        ByteCount = byteCount;
        Policy = policy;
        SlotCount = slotCount;
        m_contents = new byte[byteCount];
        m_copyPipeline = copyPipeline;
        m_copySets = new nint[((policy == GpuResidencyPolicy.Staged)
            ? slotCount
            : 0
        )];
        m_descriptors = descriptors;
        m_deviceHandle = device.DeviceHandle;
        m_recorder = recorder;
        m_runTable = new uint[((policy == GpuResidencyPolicy.Staged)
            ? CopyRunTableWords
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
            m_owed[index].Add(
                length: words,
                start: 0
            );
        }

        m_hostBuffers = new IGpuStorageBuffer[((policy == GpuResidencyPolicy.InPlace)
            ? 1
            : slotCount
        )];

        try {
            var hostBytes = ((policy == GpuResidencyPolicy.Staged)
                ? ((((ulong)CopyRunTableWords) * sizeof(uint)) + ((ulong)byteCount))
                : ((ulong)byteCount)
            );

            for (var index = 0; (index < m_hostBuffers.Length); index++) {
                m_hostBuffers[index] = buffers.Create(
                    deviceContext: device,
                    sizeBytes: hostBytes
                );
                m_ownedBuffers.Add(item: m_hostBuffers[index]);
            }

            if (policy == GpuResidencyPolicy.Staged) {
                m_deviceLocal = buffers.CreateDeviceLocal(
                    deviceContext: device,
                    sizeBytes: ((ulong)byteCount)
                );
                m_ownedBuffers.Add(item: m_deviceLocal);
                CreateCopySets();
            }
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Gets the copy kernel's descriptor set: the staging buffer it reads at <see cref="CopySourceBinding"/> and
    /// the device-local buffer it writes at <see cref="CopyDestinationBinding"/>.</summary>
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
    /// <summary>Gets the region's size in bytes.</summary>
    public int ByteCount { get; }
    /// <summary>Gets the host's copy of the region: what every slot's <see cref="Buffer"/> holds once flushed and
    /// copied.</summary>
    public ReadOnlySpan<byte> Contents => m_contents;
    /// <summary>Gets the residency policy the region was created under.</summary>
    public GpuResidencyPolicy Policy { get; }
    /// <summary>Gets the number of frame slots the region serves.</summary>
    public int SlotCount { get; }

    /// <summary>Returns the buffer a slot's GPU work binds for the region: the slot's own host-visible buffer under
    /// the ring, and the one buffer every slot shares otherwise. It is a storage buffer of <see cref="ByteCount"/> bytes
    /// or more, read as uints from its start.</summary>
    /// <param name="slot">The frame slot, below <see cref="SlotCount"/>.</param>
    /// <returns>The buffer; the region owns it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public IGpuBuffer Buffer(int slot) {
        RequireSlot(slot: slot);

        return Policy switch {
            GpuResidencyPolicy.Ring => m_hostBuffers[slot],
            GpuResidencyPolicy.Staged => m_deviceLocal!,
            _ => m_hostBuffers[0],
        };
    }
    /// <summary>Releases the region's buffers and, under the staged policy, its copy descriptor sets. The caller
    /// retires every submission that reads the region first. Disposing twice does nothing.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_copyPool != 0) {
            m_descriptors.DestroyPool(
                deviceHandle: m_deviceHandle,
                poolHandle: m_copyPool
            );
            m_copyPool = 0;
        }

        foreach (var buffer in m_ownedBuffers) {
            buffer.Dispose();
        }

        m_ownedBuffers.Clear();
    }
    /// <summary>Writes what <paramref name="slot"/> owes into its host-visible buffer: the words changed since the slot
    /// was last flushed under the ring, since the last flush under in place, and since the last recorded copy under the
    /// staged policy, whose owed words go to the slot's staging buffer behind the run table the copy reads. A slot owing
    /// nothing writes nothing.</summary>
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
    /// every owed word, after which nothing is owed until the next write. It records no barrier. Under the other
    /// policies, or with nothing owed, it records nothing.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="slot">The frame slot the command buffer belongs to, flushed since the last write.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="InvalidOperationException">The staged region owes words that <paramref name="slot"/>'s staging
    /// buffer does not hold, because the slot was not flushed after the last write.</exception>
    /// <exception cref="ObjectDisposedException">The region has been disposed.</exception>
    public void RecordCopy(nint commandBuffer, int slot) {
        RequireSlot(slot: slot);

        var owed = m_owed[0];

        if (
            (Policy != GpuResidencyPolicy.Staged) ||
            (owed.Count == 0)
        ) {
            return;
        }

        if (m_stagedSlot != slot) {
            throw new InvalidOperationException(message: $"The region owes words slot {slot}'s staging buffer does not hold; flush the slot after the last write and before recording its copy.");
        }

        var count = 0U;

        for (var run = 0; (run < owed.Count); run++) {
            count += ((uint)owed.Length(index: run));
        }

        var push = MemoryMarshal.Cast<byte, uint>(span: m_push.AsSpan());

        push[0] = count;
        push[1] = ((uint)owed.Count);
        push[2] = ((uint)owed.Start(index: 0));
        push[3] = CopyRunTableWords;
        m_recorder.BindComputePipeline(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            pipelineHandle: m_copyPipeline.Handle
        );
        m_recorder.BindComputeDescriptorSet(
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_copySets[slot],
            deviceHandle: m_deviceHandle,
            pipelineLayoutHandle: m_copyPipeline.LayoutHandle
        );
        m_recorder.PushConstants(
            commandBufferHandle: commandBuffer,
            data: m_push,
            deviceHandle: m_deviceHandle,
            offset: 0,
            pipelineLayoutHandle: m_copyPipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        m_recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((count + (CopyWorkgroupSize - 1U)) / CopyWorkgroupSize),
            groupCountY: 1,
            groupCountZ: 1
        );
        owed.Clear();
        m_stagedSlot = -1;
    }
    /// <summary>Copies <paramref name="bytes"/> into <see cref="Contents"/> at <paramref name="offset"/> and owes the
    /// words from the first byte that differed to the last, in every buffer.</summary>
    /// <param name="offset">The byte offset the bytes start at.</param>
    /// <param name="bytes">The new contents of that span.</param>
    /// <returns><see langword="true"/> when any byte differed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, or the span runs past
    /// <see cref="ByteCount"/>.</exception>
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

        var destination = m_contents.AsSpan(
            length: bytes.Length,
            start: offset
        );
        var first = bytes.CommonPrefixLength(other: destination);

        if (first == bytes.Length) {
            return false;
        }

        var last = (bytes.Length - 1);

        while (bytes[last] == destination[last]) {
            last--;
        }

        bytes[first..(last + 1)].CopyTo(destination: destination[first..]);

        var firstWord = ((offset + first) / sizeof(uint));
        var endWord = (((offset + last) / sizeof(uint)) + 1);

        foreach (var owed in m_owed) {
            owed.Add(
                length: (endWord - firstWord),
                start: firstWord
            );
        }

        m_stagedSlot = -1;

        return true;
    }

    // One copy set per slot from one pool: the slot's staging buffer as the source, the device-local buffer as the
    // destination.
    private void CreateCopySets() {
        var sets = new IReadOnlyList<GpuComputeBinding>[SlotCount];

        Array.Fill(
            array: sets,
            value: CopyBindings
        );
        m_copyPool = m_descriptors.CreatePool(
            deviceHandle: m_deviceHandle,
            sizes: GpuDescriptorPoolSizes.ForSets(sets: sets)
        );

        for (var slot = 0; (slot < SlotCount); slot++) {
            var set = m_descriptors.AllocateSet(
                descriptorSetLayoutHandle: m_copyPipeline.DescriptorSetLayoutHandle,
                deviceHandle: m_deviceHandle,
                poolHandle: m_copyPool
            );

            m_descriptors.WriteStorageBufferReadOnly(
                binding: CopySourceBinding,
                bufferHandle: m_hostBuffers[slot].BufferHandle,
                bufferSize: m_hostBuffers[slot].SizeBytes,
                descriptorSetHandle: set,
                deviceHandle: m_deviceHandle
            );
            m_descriptors.WriteStorageBufferReadWrite(
                binding: CopyDestinationBinding,
                bufferHandle: m_deviceLocal!.BufferHandle,
                bufferSize: m_deviceLocal.SizeBytes,
                descriptorSetHandle: set,
                deviceHandle: m_deviceHandle
            );
            m_copySets[slot] = set;
        }
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
    // Stages every owed word behind the run-table reserve of the slot's staging buffer, and the run table itself — per
    // run its table offset and first thread, in uints — when there are two or more runs.
    private void StageOwed(int slot) {
        var owed = m_owed[0];
        var prefix = 0;

        WriteOwed(
            buffer: m_hostBuffers[slot],
            firstWord: CopyRunTableWords,
            owed: owed
        );

        for (var run = 0; (run < owed.Count); run++) {
            m_runTable[(run * 2)] = ((uint)owed.Start(index: run));
            m_runTable[((run * 2) + 1)] = ((uint)prefix);
            prefix += owed.Length(index: run);
        }

        if (owed.Count > 1) {
            m_hostBuffers[slot].Write<uint>(data: m_runTable.AsSpan(
                length: (owed.Count * 2),
                start: 0
            ));
        }

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
