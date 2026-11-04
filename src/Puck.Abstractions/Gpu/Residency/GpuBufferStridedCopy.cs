namespace Puck.Abstractions.Gpu;

/// <summary>The actual rows and words a strided copy requests, including coarsened run gaps, and its host request bytes.</summary>
/// <param name="Rows">The rows copied after clipping to the row limit.</param>
/// <param name="Words">The uint words copied.</param>
/// <param name="RequestBytes">The header and run-table bytes written to the host-visible request buffer.</param>
public readonly record struct GpuBufferCopyWork(uint Rows, uint Words, uint RequestBytes);

/// <summary>Copies selected rows between differently strided GPU buffers through the shared region-copy pipeline.
/// Each frame slot and recording has independent request storage and descriptors. The caller retires a slot before
/// reusing it, records each recording at most once per submission, and orders source/destination accesses with its
/// buffers' readers and writers. This helper owns neither the pipeline nor the copied buffers and records no barriers.</summary>
public sealed class GpuBufferStridedCopy : IDisposable {
    private const int RequestWords = (GpuRegion.CopyHeaderWords + (2 * GpuRegion.MaxCopyRuns));
    private readonly IGpuBindings m_bindings;
    private readonly IGpuRecorder m_recorder;
    private readonly IGpuComputePipeline m_pipeline;
    private readonly GpuRegionCopyPool m_pool;
    private readonly IGpuStorageBuffer?[] m_requests;
    private readonly IGpuBuffer?[] m_sources;
    private readonly IGpuBuffer?[] m_destinations;
    private readonly uint[] m_words = new uint[RequestWords];
    private readonly int m_frameSlotCount;
    private readonly int m_recordingCount;
    private bool m_disposed;

    /// <summary>Creates request buffers and reserves sets for every frame slot and independent recording.</summary>
    /// <param name="buffers">The buffer factory.</param>
    /// <param name="bindings">The descriptor service.</param>
    /// <param name="recorder">The command recorder.</param>
    /// <param name="pipeline">The shared pipeline described by <see cref="GpuRegion.CopyPipeline"/>.</param>
    /// <param name="frameSlotCount">The fenced frame slots; positive.</param>
    /// <param name="recordingCount">Independent recordings in each slot; positive.</param>
    /// <param name="name">The owner's object name.</param>
    public GpuBufferStridedCopy(IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline pipeline,
        int frameSlotCount, int recordingCount, in GpuObjectName name) {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordingCount);
        m_bindings = bindings;
        m_recorder = recorder;
        m_pipeline = pipeline;
        m_frameSlotCount = frameSlotCount;
        m_recordingCount = recordingCount;
        var count = checked((frameSlotCount * recordingCount));

        m_requests = new IGpuStorageBuffer?[count];
        m_sources = new IGpuBuffer?[count];
        m_destinations = new IGpuBuffer?[count];
        var names = new GpuObjectName[recordingCount];

        for (var recording = 0; recording < recordingCount; recording++) {
            names[recording] = (name.IsEmpty ? default : new GpuObjectName(owner: name.Owner!, part: name.Part!,
                detail: string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name.Detail ?? "copy"} request {recording}")));
        }
        m_pool = new GpuRegionCopyPool(bindings: bindings, copyPipeline: pipeline, slotCount: frameSlotCount, name: name, regions: names);
        try {
            for (var recording = 0; recording < recordingCount; recording++) {
                for (var slot = 0; slot < frameSlotCount; slot++) {
                    m_requests[IndexOf(slot, recording)] = buffers.CreateHostVisible(name: names[recording].At(slot),
                        sizeBytes: (RequestWords * sizeof(uint)), usage: GpuBufferUsage.Storage);
                }
            }
        } catch {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets the actual host-visible request allocations. The caller accounts its descriptors separately.</summary>
    public GpuMemoryBytes OwnedBytes {
        get {
            var bytes = 0UL;
            foreach (var request in m_requests) { bytes += (request?.SizeBytes ?? 0UL); }
            return new GpuMemoryBytes(DeviceLocal: 0, HostVisible: bytes);
        }
    }

    /// <summary>Returns the descriptors reserved by a recorder with these frame-slot and recording counts.</summary>
    /// <param name="frameSlotCount">The fenced frame slots; positive.</param>
    /// <param name="recordingCount">Independent recordings in each slot; positive.</param>
    /// <returns>The pool's required sizes.</returns>
    public static GpuDescriptorPoolSizes PoolSizes(int frameSlotCount, int recordingCount) =>
        GpuRegionCopyPool.SizesOf(regionCount: recordingCount, slotCount: frameSlotCount);

    /// <summary>Records a bit-exact gather of row runs. Runs name absolute row indices, clipped to the exclusive
    /// row limit. Source and destination must be distinct buffers; strides are at least the copied row width.</summary>
    /// <param name="commandBuffer">The recording command buffer.</param>
    /// <param name="slot">A frame slot whose previous users have retired.</param>
    /// <param name="recording">An independent recording within the slot, used at most once per submission.</param>
    /// <param name="source">The source storage buffer.</param>
    /// <param name="destination">The destination storage buffer.</param>
    /// <param name="rows">Sorted disjoint row runs, including any conservatively coarsened gaps.</param>
    /// <param name="rowWords">The uint words copied from each row; positive.</param>
    /// <param name="sourceStrideWords">The source row stride in uint words.</param>
    /// <param name="destinationStrideWords">The destination row stride in uint words.</param>
    /// <param name="rowLimit">The exclusive row limit.</param>
    /// <param name="sourceBaseWord">The source's first row word.</param>
    /// <param name="destinationBaseWord">The destination's first row word.</param>
    /// <returns>The actual copied rows/words and host request bytes, zero for no work.</returns>
    public GpuBufferCopyWork Record(nint commandBuffer, int slot, int recording, IGpuBuffer source, IGpuBuffer destination,
        GpuUploadRuns rows, uint rowWords, uint sourceStrideWords, uint destinationStrideWords, uint rowLimit,
        uint sourceBaseWord = 0, uint destinationBaseWord = 0) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfZero(rowWords);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStrideWords, rowWords);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStrideWords, rowWords);
        var index = IndexOf(slot, recording);
        if ((rows.Count == 0) || (rowLimit == 0)) { return default; }
        if (source.BufferHandle == destination.BufferHandle) { throw new ArgumentException(message: "A strided copy requires distinct source and destination storage.", paramName: nameof(destination)); }

        var runCount = 0;
        var copiedRows = 0U;
        var copiedWords = 0U;
        var lastRowEnd = 0U;

        for (var run = 0; run < rows.Count; run++) {
            var start = rows.Start(run);
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            if (((uint)start) >= rowLimit) { break; }
            var end = ((uint)Math.Min((ulong)rowLimit, ((ulong)start + ((uint)rows.Length(run)))));
            var length = (end - ((uint)start));
            if (runCount == GpuRegion.MaxCopyRuns) { throw new ArgumentException(message: "A strided request exceeds the shared copy run capacity.", paramName: nameof(rows)); }
            m_words[(GpuRegion.CopyHeaderWords + (2 * runCount))] = checked((((uint)start) * rowWords));
            m_words[(GpuRegion.CopyHeaderWords + (2 * runCount) + 1)] = copiedWords;
            copiedRows = checked((copiedRows + length));
            copiedWords = checked((copiedWords + checked((length * rowWords))));
            lastRowEnd = end;
            runCount++;
        }
        if (runCount == 0) { return default; }
        if (copiedWords > (GpuRegion.MaxByteCount / sizeof(uint))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(rows), message: "The strided request exceeds the shared copy kernel's word domain.");
        }
        RequireExtent(source, sourceBaseWord, sourceStrideWords, rowWords, lastRowEnd);
        RequireExtent(destination, destinationBaseWord, destinationStrideWords, rowWords, lastRowEnd);
        m_words[0] = copiedWords;
        m_words[1] = ((uint)runCount);
        m_words[2] = sourceBaseWord;
        m_words[3] = destinationBaseWord;
        m_words[4] = rowWords;
        m_words[5] = sourceStrideWords;
        m_words[6] = destinationStrideWords;
        var request = m_requests[index]!;
        var requestWords = (GpuRegion.CopyHeaderWords + (2 * runCount));
        request.Write<uint>(data: m_words.AsSpan(0, requestWords));
        var set = m_pool.Region(recording).SetOf(slot);
        if (m_sources[index] is null) { Bind(set, GpuRegion.CopyRequestBinding, request, GpuBindingKind.ReadOnlyBuffer); }
        if (!ReferenceEquals(m_sources[index], source)) { Bind(set, GpuRegion.CopySourceBinding, source, GpuBindingKind.ReadOnlyBuffer); m_sources[index] = source; }
        if (!ReferenceEquals(m_destinations[index], destination)) { Bind(set, GpuRegion.CopyDestinationBinding, destination, GpuBindingKind.ReadWriteBuffer); m_destinations[index] = destination; }
        GpuRegion.RecordCopyDispatch(recorder: m_recorder, commandBuffer: commandBuffer, pipeline: m_pipeline, set: set, words: copiedWords);
        return new GpuBufferCopyWork(Rows: copiedRows, Words: copiedWords, RequestBytes: checked((((uint)requestWords) * sizeof(uint))));
    }

    private static void RequireExtent(IGpuBuffer buffer, uint baseWord, uint stride, uint width, uint rowEnd) {
        var end = (((ulong)baseWord) + (((ulong)(rowEnd - 1)) * stride) + width);
        if ((end > (buffer.SizeBytes / sizeof(uint))) || (end > ((ulong)(GpuRegion.MaxByteCount / sizeof(uint))))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(buffer), message: "The strided copy exceeds its buffer or the shared copy word-address range.");
        }
    }
    private int IndexOf(int slot, int recording) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, m_frameSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(recording);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(recording, m_recordingCount);
        return ((recording * m_frameSlotCount) + slot);
    }
    private void Bind(nint set, uint binding, IGpuBuffer buffer, GpuBindingKind kind) =>
        m_bindings.WriteBuffer(descriptorSetHandle: set, binding: binding, bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes, elementStride: sizeof(uint), kind: kind);

    /// <summary>Releases the private requests and descriptor pool after all submitted users have retired.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var request in m_requests) { request?.Dispose(); }
        Array.Clear(m_requests);
        Array.Clear(m_sources);
        Array.Clear(m_destinations);
        m_pool.Dispose();
    }
}
