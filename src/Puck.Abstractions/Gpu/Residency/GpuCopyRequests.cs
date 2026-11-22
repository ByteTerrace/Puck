namespace Puck.Abstractions.Gpu;

/// <summary>
/// The request buffers and copy sets of region-copy dispatches that state their own request each time
/// (<see cref="GpuBufferStridedCopy"/>, a region's moves and gathers): per frame slot and independent recording, a
/// host-visible request buffer of the most a request holds and a copy set, written again only when the buffers it binds
/// change. A slot is reused only once its last submission has retired, which its owner guarantees, so its request buffer
/// and set are free to rewrite when it records again.
/// </summary>
internal sealed class GpuCopyRequests : IDisposable {
    private readonly IGpuBindings m_bindings;
    private readonly IGpuRecorder m_recorder;
    private readonly IGpuComputePipeline m_pipeline;
    private readonly GpuRegionCopyPool m_pool;
    private readonly IGpuStorageBuffer?[] m_requests;
    // What each set was last written with: its source and destination buffers, compared as objects, never by native
    // handle, since a released buffer's handle can name a new buffer, whose memory the set's old descriptors do not
    // reach. The request buffer is the set's own for its life.
    private readonly (IGpuBuffer? Source, IGpuBuffer? Destination)[] m_written;
    private readonly int m_slotCount;
    private readonly int m_recordingCount;

    /// <summary>Initializes a new instance of the <see cref="GpuCopyRequests"/> class, creating every request buffer and
    /// reserving every copy set.</summary>
    /// <param name="buffers">The factory the request buffers come from.</param>
    /// <param name="bindings">The descriptor service.</param>
    /// <param name="recorder">The command recorder.</param>
    /// <param name="pipeline">The shared region-copy pipeline.</param>
    /// <param name="slotCount">The fenced frame slots; positive.</param>
    /// <param name="name">The copy pool's name.</param>
    /// <param name="names">One name per independent recording within a slot, each slot's buffer and set named at the
    /// slot's index.</param>
    /// <param name="requestWords">The most words one request holds: its header and run table.</param>
    public GpuCopyRequests(IGpuBufferFactory buffers, IGpuBindings bindings, IGpuRecorder recorder, IGpuComputePipeline pipeline, int slotCount,
        in GpuObjectName name, ReadOnlySpan<GpuObjectName> names, int requestWords) {
        m_bindings = bindings;
        m_recorder = recorder;
        m_pipeline = pipeline;
        m_slotCount = slotCount;
        m_recordingCount = names.Length;
        m_requests = new IGpuStorageBuffer?[checked(slotCount * names.Length)];
        m_written = new (IGpuBuffer?, IGpuBuffer?)[m_requests.Length];
        m_pool = new GpuRegionCopyPool(bindings: bindings, copyPipeline: pipeline, slotCount: slotCount, name: in name, regions: names);
        try {
            for (var recording = 0; (recording < names.Length); recording++) {
                for (var slot = 0; (slot < slotCount); slot++) {
                    m_requests[IndexOf(slot: slot, recording: recording)] = buffers.CreateHostVisible(name: names[recording].At(index: slot),
                        sizeBytes: (((ulong)requestWords) * sizeof(uint)), usage: GpuBufferUsage.Storage);
                }
            }
        } catch {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets the bytes the request buffers hold.</summary>
    public ulong RequestBytes {
        get {
            var bytes = 0UL;

            foreach (var request in m_requests) { bytes += (request?.SizeBytes ?? 0UL); }
            return bytes;
        }
    }

    /// <summary>Writes a request into a slot's request buffer, binds its set to the request, source and destination
    /// when it names other buffers, and records the dispatch of the request's words.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="slot">A frame slot whose last submission has retired.</param>
    /// <param name="recording">An independent recording within the slot, used at most once per submission.</param>
    /// <param name="request">The request: its header, whose first word counts the words it copies, and run table.</param>
    /// <param name="source">The buffer the copy reads at its source binding.</param>
    /// <param name="destination">The buffer the copy writes.</param>
    /// <param name="pipelineBound">Whether the command buffer already has the copy pipeline bound.</param>
    public void Record(nint commandBuffer, int slot, int recording, ReadOnlySpan<uint> request, IGpuBuffer source, IGpuBuffer destination, bool pipelineBound = false) {
        var index = IndexOf(slot: slot, recording: recording);
        var buffer = m_requests[index]!;
        var set = m_pool.Region(index: recording).SetOf(slot: slot);

        buffer.Write<uint>(data: request);
        if (!ReferenceEquals(objA: m_written[index].Source, objB: source) || !ReferenceEquals(objA: m_written[index].Destination, objB: destination)) {
            GpuRegion.WriteCopySet(bindings: m_bindings, destination: destination, request: buffer, set: set, source: source);
            m_written[index] = (source, destination);
        }
        GpuRegion.RecordCopyDispatch(recorder: m_recorder, commandBuffer: commandBuffer, pipeline: m_pipeline, set: set, words: request[0], pipelineBound: pipelineBound);
    }
    /// <summary>Returns a slot's request buffer, which a copy that reads no source binds there in its place.</summary>
    /// <param name="slot">The frame slot.</param>
    /// <param name="recording">The recording within the slot.</param>
    /// <returns>The request buffer.</returns>
    public IGpuStorageBuffer RequestOf(int slot, int recording) => m_requests[IndexOf(slot: slot, recording: recording)]!;

    private int IndexOf(int slot, int recording) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, m_slotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(recording);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(recording, m_recordingCount);
        return ((recording * m_slotCount) + slot);
    }

    /// <summary>Releases the request buffers and the copy pool once every submission that used them has retired.</summary>
    public void Dispose() {
        foreach (var request in m_requests) { request?.Dispose(); }
        Array.Clear(array: m_requests);
        m_pool.Dispose();
    }
}
