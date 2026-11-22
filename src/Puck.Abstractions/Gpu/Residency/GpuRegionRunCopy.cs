using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One kind of a staged region's copies whose runs each name their own source word (<see cref="GpuRegion.TryOweMove"/>
/// and <see cref="GpuRegion.TryOweGather"/>): the runs owed until the region's next recorded copy, a request buffer and
/// a copy set per slot, and the dispatch of the region-copy kernel in the mode that reads each run from its source word.
/// A move's source is the region's destination itself; a gather's is a host-visible buffer its owner packed and keeps
/// alive until the submission that records it has completed. The region owns it, stages it at a slot's flush and
/// records it in that slot's copy, so its runs are ordered against every other write of the destination by the one
/// recording door.
/// </summary>
internal sealed class GpuRegionRunCopy : IDisposable {
    private readonly IGpuBindings m_bindings;
    private readonly IGpuBufferFactory m_buffers;
    private readonly IGpuComputePipeline m_pipeline;
    private readonly IGpuRecorder m_recorder;
    private readonly GpuRegionCopyPool m_pool;
    private readonly GpuObjectName m_requestName;
    private readonly GpuBufferUsage m_usage;
    private readonly uint m_mode;
    private readonly IGpuStorageBuffer?[] m_requests;
    // What each slot's set was last written with: its request, source and destination buffers.
    private readonly (nint Request, nint Source, nint Destination)[] m_written;
    private readonly List<(int Destination, int Source, int Words)> m_runs = [];
    private uint[] m_header = [];
    private IGpuStorageBuffer? m_source;
    private IGpuStorageBuffer? m_stagedSource;
    private int m_staged;

    internal GpuRegionRunCopy(IGpuBindings bindings, IGpuBufferFactory buffers, IGpuComputePipeline pipeline, IGpuRecorder recorder,
        in GpuObjectName name, in GpuObjectName requestName, GpuBufferUsage usage, int slotCount, uint mode) {
        m_bindings = bindings;
        m_buffers = buffers;
        m_pipeline = pipeline;
        m_recorder = recorder;
        m_requestName = requestName;
        m_usage = usage;
        m_mode = mode;
        m_pool = new GpuRegionCopyPool(bindings: bindings, copyPipeline: pipeline, name: in name, regions: [name], slotCount: slotCount);
        m_requests = new IGpuStorageBuffer?[slotCount];
        m_written = new (nint, nint, nint)[slotCount];
    }

    // The runs owed, and the gather source they read (null for moves).
    internal List<(int Destination, int Source, int Words)> Runs => m_runs;
    internal IGpuStorageBuffer? Source => m_source;
    // The words a run reads and writes, to every run's: whether a new run overlaps one owed, in either range.
    internal bool Overlaps(int first, int words) {
        foreach (var run in m_runs) {
            if (Overlap(run.Destination, run.Words, first, words) || ((m_source is null) && Overlap(run.Source, run.Words, first, words))) { return true; }
        }
        return false;
    }
    internal static bool Overlap(int first, int firstWords, int second, int secondWords) => ((first < (second + secondWords)) && (second < (first + firstWords)));
    // Owes a run; a gather names the buffer its source words lie in, which every run owed together shares.
    internal void Add(int destination, int source, int words, IGpuStorageBuffer? from) {
        m_source = from;
        m_runs.Add(item: (destination, source, words));
    }
    internal void Clear() {
        m_runs.Clear();
        m_source = null;
    }

    // Writes the owed runs' request into the slot's request buffer, whose last submission has retired: the header in the
    // copy's mode, each run's destination word and first thread, then each run's source word.
    internal void Stage(int slot, int destinationWord) {
        m_staged = 0;
        m_stagedSource = null;
        if (m_runs.Count == 0) { return; }

        var words = checked(GpuRegion.CopyHeaderWords + (3 * m_runs.Count));
        var prefix = 0U;

        ArrayCapacity.EnsureLength(ref m_header, words);
        for (var index = 0; (index < m_runs.Count); index++) {
            var (destination, source, length) = m_runs[index];

            m_header[GpuRegion.CopyHeaderWords + (index * 2)] = ((uint)destination);
            m_header[GpuRegion.CopyHeaderWords + (index * 2) + 1] = prefix;
            m_header[GpuRegion.CopyHeaderWords + (m_runs.Count * 2) + index] = ((uint)source);
            prefix = checked(prefix + ((uint)length));
        }
        m_header[0] = prefix;
        m_header[1] = ((uint)m_runs.Count);
        m_header[2] = 0U;
        m_header[3] = ((uint)destinationWord);
        m_header[4] = 0U;
        m_header[5] = m_mode;
        m_header[6] = 0U;
        if (m_requests[slot] is not { } request) {
            request = m_buffers.CreateHostVisible(name: m_requestName.At(index: slot),
                sizeBytes: (((ulong)(GpuRegion.CopyHeaderWords + (3 * GpuRegion.MaxCopyRuns))) * sizeof(uint)), usage: m_usage);
            m_requests[slot] = request;
        }
        request.Write<uint>(data: m_header.AsSpan(0, words));
        m_staged = m_runs.Count;
        m_stagedSource = m_source;
    }
    // Records the runs the slot's flush staged into the destination, writing the slot's set first when it names other
    // buffers; false when none was staged. The runs it records are no longer owed.
    internal bool Record(nint commandBuffer, int slot, IGpuBuffer destination, bool pipelineBound) {
        if (m_staged == 0) { return false; }

        var set = m_pool.Region(index: 0).SetOf(slot: slot);
        var request = m_requests[slot]!;
        // A move reads the destination; its source binding holds the request, which it never reads.
        var source = ((IGpuBuffer?)m_stagedSource ?? request);

        if (m_written[slot] != (request.BufferHandle, source.BufferHandle, destination.BufferHandle)) {
            Bind(set: set, binding: GpuRegion.CopyRequestBinding, buffer: request, kind: GpuBindingKind.ReadOnlyBuffer);
            Bind(set: set, binding: GpuRegion.CopySourceBinding, buffer: source, kind: GpuBindingKind.ReadOnlyBuffer);
            Bind(set: set, binding: GpuRegion.CopyDestinationBinding, buffer: destination, kind: GpuBindingKind.ReadWriteBuffer);
            m_written[slot] = (request.BufferHandle, source.BufferHandle, destination.BufferHandle);
        }
        GpuRegion.RecordCopyDispatch(recorder: m_recorder, commandBuffer: commandBuffer, pipeline: m_pipeline, set: set, words: m_header[0], pipelineBound: pipelineBound);
        Recorded += m_staged;
        m_runs.RemoveRange(index: 0, count: m_staged);
        if (m_runs.Count == 0) { m_source = null; }
        m_staged = 0;
        m_stagedSource = null;

        return true;
    }
    // The runs recorded over the region's life.
    internal long Recorded { get; private set; }

    private void Bind(nint set, uint binding, IGpuBuffer buffer, GpuBindingKind kind) =>
        m_bindings.WriteBuffer(binding: binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: set,
            elementStride: sizeof(uint), kind: kind);

    public void Dispose() {
        m_pool.Dispose();
        foreach (var request in m_requests) { request?.Dispose(); }
        Array.Clear(array: m_requests);
        Clear();
    }
}
