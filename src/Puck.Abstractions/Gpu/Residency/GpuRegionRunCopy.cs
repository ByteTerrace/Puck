using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One kind of a staged region's copies whose runs each name their own source word (<see cref="GpuRegion.TryOweMove"/>
/// and <see cref="GpuRegion.TryOweGather"/>): the runs owed until the region's next recorded copy, and their dispatch of
/// the region-copy kernel in the mode that reads each run from its source word (<see cref="GpuCopyRequests"/> holds the
/// request buffer and copy set of each slot). A move's source is the region's destination itself; a gather's is a
/// host-visible buffer its owner packed and keeps alive until the submission that records it has completed. The region
/// owns it, stages it at a slot's flush and records it in that slot's copy, so its runs are ordered against every other
/// write of the destination by the one recording door.
/// </summary>
internal sealed class GpuRegionRunCopy : IDisposable {
    private readonly GpuCopyRequests m_requests;
    private readonly uint m_mode;
    private readonly List<(int Destination, int Source, int Words)> m_runs = [];
    private uint[] m_header = [];
    private int m_headerWords;
    private IGpuStorageBuffer? m_source;
    private IGpuStorageBuffer? m_stagedSource;
    private int m_staged;

    internal GpuRegionRunCopy(IGpuBindings bindings, IGpuBufferFactory buffers, IGpuComputePipeline pipeline, IGpuRecorder recorder,
        in GpuObjectName name, int slotCount, uint mode) {
        m_mode = mode;
        m_requests = new GpuCopyRequests(bindings: bindings, buffers: buffers, name: in name, names: [name], pipeline: pipeline, recorder: recorder,
            requestWords: (GpuRegion.CopyHeaderWords + (3 * GpuRegion.MaxCopyRuns)), slotCount: slotCount);
    }

    // The runs owed, and the gather source they read (null for moves).
    internal List<(int Destination, int Source, int Words)> Runs => m_runs;
    internal IGpuStorageBuffer? Source => m_source;
    // Whether a range overlaps the words an owed run writes or, for a move, reads.
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

    // Takes the owed runs as the slot's next request: the header in the copy's mode, each run's destination word and first
    // thread, then each run's source word.
    internal void Stage(int destinationWord) {
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
        m_headerWords = words;
        m_staged = m_runs.Count;
        m_stagedSource = m_source;
    }
    // Records the runs the slot's flush staged into the destination; false when none was staged. The runs it records are
    // no longer owed.
    internal bool Record(nint commandBuffer, int slot, IGpuBuffer destination, bool pipelineBound) {
        if (m_staged == 0) { return false; }
        // A move reads the destination; its source binding holds the request, which it never reads.
        var source = ((IGpuBuffer?)m_stagedSource ?? m_requests.RequestOf(recording: 0, slot: slot));

        m_requests.Record(commandBuffer: commandBuffer, destination: destination, pipelineBound: pipelineBound, recording: 0,
            request: m_header.AsSpan(start: 0, length: m_headerWords), slot: slot, source: source);
        Recorded += m_staged;
        m_runs.RemoveRange(index: 0, count: m_staged);
        if (m_runs.Count == 0) { m_source = null; }
        m_staged = 0;
        m_stagedSource = null;

        return true;
    }
    // The runs recorded over the region's life.
    internal long Recorded { get; private set; }

    public void Dispose() {
        m_requests.Dispose();
        m_runs.Clear();
        m_source = null;
    }
}
