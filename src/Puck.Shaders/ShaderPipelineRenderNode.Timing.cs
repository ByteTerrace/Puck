using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    /// <summary>The number of render graph frames a pass's observational window covers, and the samples it retains a
    /// pass: a pass records at most once a frame.</summary>
    public const int TimingWindow = 32;

    private TimingSlot[] m_timingSlots = [];
    private GpuPassTiming[] m_timings = [];
    // Each pass's latest TimingWindow samples, by the submission that took them and the render graph frame it rendered in.
    private (long Submission, long Frame, double Milliseconds)[] m_timingValues = [];
    // The frame the window was last computed for, and the first frame the current graph's timing covers.
    private long m_timingWindowAt = -1;
    private long m_timingSince;
    private int m_timingWindowFrames;
    private long m_timingRevision = -1;
    private bool m_timingEnabled;
    private long m_timingEpoch;
    private bool m_timingCompleted;
    private string? m_timingRefusal;
    private long m_timingFrames;
    private double m_timingLatest;
    private (double Start, double End) m_timingLatestSpan;
    private long m_timingLatestSubmission;
    private long m_timingRefusedFaultsRevision;

    /// <summary>Gets or sets whether pass timestamp queries are recorded. Off by default: no pools, buffers or query
    /// operations exist until enabled. Disabling withdraws the readout immediately and retires pools after their fences.
    /// Enabling again forgets an earlier refusal, so a new demand tries once more.</summary>
    public bool TimingEnabled {
        get => m_timingEnabled;
        set {
            if (m_timingEnabled == value) { return; }
            m_timingEnabled = value;
            m_timingEpoch++;
            m_timingCompleted = false;
            m_timingRefusal = null;
            m_timingRevision = -1;
            m_timings = [];
            PollTimings();
        }
    }
    /// <summary>Gets why this node refused the timing it was asked for, or null while timing is off or recording. Timing
    /// is observational: a device without timestamps, or a pool or readback the device would not create, refuses timing
    /// by name and leaves the graph rendering. A refusal is tried again only when timing is enabled anew or the
    /// operator's GPU faults move, never once per frame.</summary>
    public string? TimingRefusal => (m_timingEnabled ? m_timingRefusal : null);
    /// <summary>Gets or sets the render graph frame the node's next submission renders in, which the runtime writes before
    /// it begins the node's frame and the node's timing window counts in (<see cref="TimingsAt"/>).</summary>
    public long TimingFrame { get; set; }
    /// <summary>Returns the current graph's completed per-pass readings over the <see cref="TimingWindow"/> render graph
    /// frames whose timestamps can have completed by <paramref name="frame"/> (the window ends the node's frames in flight
    /// before it). A frame the node or a pass did not render counts as zero time, so <see cref="GpuPassTiming.PerFrameMs"/>
    /// is what the pass costs a frame on average, and a node that stopped rendering reads zero and stale rather than the
    /// cost of its last renders. Empty while disabled, unsupported, or awaiting its first timed submission. A pass with no
    /// completed pair in the window has zero samples, never a fabricated time.</summary>
    /// <param name="frame">The render graph's latest frame (<c>RenderGraphRuntime.Latest.Frame</c>).</param>
    /// <returns>The readings, one a pass.</returns>
    public ReadOnlySpan<GpuPassTiming> TimingsAt(long frame) {
        if (!HasCompletedTiming) { return []; }
        if (m_timingWindowAt != frame) { UpdateTimingWindow(frame: frame); }
        return m_timings;
    }
    private bool HasCompletedTiming => m_timingCompleted && (m_timingRevision == m_revision);
    /// <summary>Gets the number of render graph frames the latest <see cref="TimingsAt"/> window covered: the window's
    /// width, or fewer while the current graph's timing is younger than it.</summary>
    public int TimingWindowFrames => (HasCompletedTiming ? m_timingWindowFrames : 0);
    /// <summary>Gets how many timed submissions this node has read back, which moves with each one and never repeats.</summary>
    public long TimingFrames => m_timingFrames;
    /// <summary>Gets the summed pass time, in milliseconds, of the latest timed submission read back for the current graph,
    /// or zero while <see cref="TimingsAt"/> is empty.</summary>
    public double LatestTimingMilliseconds => (HasCompletedTiming ? m_timingLatest : 0d);
    /// <summary>Gets the identity of the submission <see cref="LatestTimingMilliseconds"/> timed, as the node's work ledger
    /// numbers it: the newest submission read back, whichever frame slot held it. Zero while <see cref="TimingsAt"/> is
    /// empty.</summary>
    public long LatestTimingSubmission => (HasCompletedTiming ? m_timingLatestSubmission : 0L);
    /// <summary>Gets when the latest timed submission's first timed pass began and its last timed pass ended, in
    /// milliseconds on the recording queue's timestamp clock, which every node on that queue shares; (0, 0) while
    /// <see cref="TimingsAt"/> is empty. Two nodes' spans compare only when they come from the same queue.</summary>
    public (double Start, double End) LatestTimingSpan => (HasCompletedTiming ? m_timingLatestSpan : default);
    /// <summary>Gets the logical bytes of timestamp readback buffers still owned, including disabled slots awaiting fences.</summary>
    public ulong TimingReadbackBytes {
        get { var bytes = 0UL; foreach (var slot in m_timingSlots) { if (slot.Readback is not null) { bytes += ((ulong)slot.Bytes.Length); } } return bytes; }
    }
    /// <summary>Gets the retained timestamp sample, rolling-window and slot-mask payload bytes, excluding managed object headers.</summary>
    public ulong TimingCpuBytes {
        get {
            var bytes = ((ulong)((m_timingValues.Length * (sizeof(long) + sizeof(double))) + (m_timings.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<GpuPassTiming>())));

            foreach (var slot in m_timingSlots) { bytes += ((ulong)(slot.Bytes.Length + slot.Recorded.Length)); }
            return bytes;
        }
    }

    // Whether a slot still holds work PollTimings acts on: a fence to read back after, or a pool to retire once timing
    // stopped.
    private bool TimingPending {
        get {
            foreach (var slot in m_timingSlots) {
                if ((slot.Fence is not null) || (!TimingActive && (slot.Pool is not null))) {
                    return true;
                }
            }

            return false;
        }
    }
    // Timing records only while it is enabled and not refused.
    private bool TimingActive => (m_timingEnabled && (m_timingRefusal is null));

    private void PrepareTiming(int slot) {
        PollTimings();
        if (!m_timingEnabled || (m_passLabels.Length == 0)) { return; }
        if (m_timingRefusal is not null) {
            if (FaultsRevision == m_timingRefusedFaultsRevision) { return; }
            m_timingRefusal = null;
        }
        if (m_gpu.TimestampFactory is not { } factory) {
            RefuseTiming(reason: "unsupported: the device records no timestamps");
            return;
        }
        try {
            ProvisionTiming(factory: factory, slot: slot);
        } catch (Exception error) when ((error is not DeviceLostException)) {
            RefuseTiming(reason: error.Message);
        }
    }
    // Sizes the readout to the installed graph and creates the slot's pool and readback on its first timed frame. A
    // slot whose creation fails releases what it created at once, since nothing recorded against it.
    private void ProvisionTiming(IGpuTimestampFactory factory, int slot) {
        if (m_timingRevision != m_revision) {
            m_timings = new GpuPassTiming[m_passLabels.Length];
            m_timingValues = new (long Submission, long Frame, double Milliseconds)[m_passLabels.Length * TimingWindow];
            m_timingCompleted = false;
            m_timingWindowAt = -1;
            m_timingSince = TimingFrame;
            m_timingWindowFrames = 0;
            m_timingLatestSubmission = m_submissions;
            for (var index = 0; (index < m_timings.Length); index++) {
                m_timings[index] = new GpuPassTiming(Pass: m_passLabels[index], Milliseconds: 0, Samples: 0);
            }
            m_timingRevision = m_revision;
        }
        if (m_timingSlots.Length == 0) {
            m_timingSlots = new TimingSlot[m_inFlight];
            for (var index = 0; (index < m_timingSlots.Length); index++) { m_timingSlots[index] = new TimingSlot(); }
        }
        var target = m_timingSlots[slot];

        if (target.Recorded.Length != m_passLabels.Length) { target.Dispose(); }
        if (target.Pool is null) {
            target.Pool = factory.Create(count: checked((uint)(m_passLabels.Length * 2)),
                name: new GpuObjectName(owner: m_name, part: "timing", index: slot));
            if (target.Pool is null) {
                RefuseTiming(reason: "unsupported: the recording queue records no timestamps");
                return;
            }
            try {
                target.Bytes = new byte[(m_passLabels.Length * 16)];
                target.Recorded = new bool[m_passLabels.Length];
                target.Readback = m_gpu.BufferFactory.CreateReadback(sizeBytes: ((ulong)target.Bytes.Length),
                    name: new GpuObjectName(detail: "readback", index: slot, owner: m_name, part: "timing"));
            } catch { target.Dispose(); throw; }
        }
        Array.Clear(array: target.Recorded);
        target.Revision = m_revision;
        target.Epoch = m_timingEpoch;
    }
    // Refuses timing by name. Slots that earlier submissions still read retire after their fences, as a disable's do.
    private void RefuseTiming(string reason) {
        m_timingRefusal = reason;
        m_timingRefusedFaultsRevision = FaultsRevision;
        m_timingRevision = -1;
        m_timingCompleted = false;
        m_timings = [];
        PollTimings();
    }
    private void BeginTiming(nint command, int slot, int pass) {
        if (!TimingActive || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
        var first = checked((uint)(pass * 2));

        pool.Reset(command: command, count: 2, first: first);
        pool.Write(command: command, index: first);
    }
    // Writes a pass's closing timestamp; `mark` also notes the pair for ResolveTimings, which a pass emitted on a lane
    // notes at its decision instead (MarkTimed), since the frame's tail resolves the pairs before the lanes run.
    private void EndTiming(nint command, int slot, int pass, bool mark = true) {
        if (!TimingActive || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
        var first = checked((uint)(pass * 2));

        pool.Write(command: command, index: (first + 1));
        if (mark) { m_timingSlots[slot].Recorded[pass] = true; }
    }
    private void MarkTimed(int slot, int pass) {
        if (!TimingActive || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is null)) { return; }
        m_timingSlots[slot].Recorded[pass] = true;
    }
    // Copies every pair the submission wrote into the slot's readback, one copy per run of adjacent timed passes, after
    // the last command that writes one. A copy waits for its timestamps to land (VK_QUERY_RESULT_WAIT_BIT on Vulkan), so
    // a copy between passes would drain the queue at every pass boundary.
    private void ResolveTimings(nint command, int slot) {
        if (!TimingActive || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
        var target = m_timingSlots[slot];
        var recorded = target.Recorded;
        var resolved = false;

        for (var pass = 0; (pass < recorded.Length);) {
            if (!recorded[pass]) { pass++; continue; }
            var end = pass;

            while ((end < recorded.Length) && recorded[end]) { end++; }
            pool.Resolve(command: command, first: checked((uint)(pass * 2)), count: checked((uint)((end - pass) * 2)),
                destination: target.Readback!.BufferHandle, offset: (((ulong)pass) * 16));
            resolved = true;
            pass = end;
        }
        if (resolved) {
            GpuReadbackCopy.MakeHostVisible(recorder: m_gpu.Recorder, commandBuffer: command, destinationBufferHandle: target.Readback!.BufferHandle);
        }
    }
    private void SubmitTiming(int slot, IGpuSubmissionFence fence) {
        if (TimingActive && (m_timingSlots.Length != 0) && (m_timingSlots[slot].Pool is not null)) {
            m_timingSlots[slot].Fence = fence;
            m_timingSlots[slot].Submission = m_submissions;
            m_timingSlots[slot].Frame = TimingFrame;
        }
    }
    private void PollTimings() {
        foreach (var slot in m_timingSlots) {
            if ((slot.Fence is { } fence) && !Signaled(fence: fence)) { continue; }
            if (!TimingActive) { slot.Dispose(); continue; }
            if (slot.Fence is null) { continue; }
            slot.Fence = null;
            if ((slot.Revision != m_revision) || (m_timingRevision != m_revision) || (slot.Epoch != m_timingEpoch)) { continue; }
            slot.Readback!.Read(destination: slot.Bytes);
            var pool = slot.Pool!;
            var mask = ((pool.ValidBits == 64) ? ulong.MaxValue : ((1UL << ((int)pool.ValidBits)) - 1));
            var frame = 0d;
            var first = ulong.MaxValue;
            var last = 0UL;

            for (var pass = 0; (pass < slot.Recorded.Length); pass++) {
                if (!slot.Recorded[pass]) { continue; }
                var start = BinaryPrimitives.ReadUInt64LittleEndian(source: slot.Bytes.AsSpan(start: (pass * 16)));
                var end = BinaryPrimitives.ReadUInt64LittleEndian(source: slot.Bytes.AsSpan(start: ((pass * 16) + 8)));
                var value = (((unchecked((end - start)) & mask) * pool.NanosecondsPerTick) / 1_000_000);

                first = Math.Min(val1: first, val2: start);
                last = Math.Max(val1: last, val2: end);

                m_timingCompleted = true;
                frame += value;
                var index = ((pass * TimingWindow) + (int)(slot.Submission % TimingWindow));
                if (m_timingValues[index].Submission < slot.Submission) {
                    m_timingValues[index] = (slot.Submission, slot.Frame, value);
                }
                if (m_timings[pass].LastSubmission < slot.Submission) {
                    m_timings[pass] = m_timings[pass] with { LastSubmission = slot.Submission };
                }
            }
            // Several slots can complete in one poll, so the newest submission, not the last slot read, is the latest.
            if (slot.Submission > m_timingLatestSubmission) {
                m_timingLatest = frame;
                m_timingLatestSubmission = slot.Submission;
                m_timingLatestSpan = (last >= first)
                    ? (((first * pool.NanosecondsPerTick) / 1_000_000), ((last * pool.NanosecondsPerTick) / 1_000_000))
                    : default;
            }
            m_timingWindowAt = -1;
            m_timingFrames++;
        }
    }
    // Computes each pass's reading over the frames whose timestamps can have completed by the given frame: the window
    // ends the node's frames in flight before it and starts no earlier than the frame the current graph's timing began.
    private void UpdateTimingWindow(long frame) {
        var last = (frame - m_inFlight);
        var first = Math.Max(val1: (last - TimingWindow + 1), val2: m_timingSince);
        var frames = (int)Math.Clamp(value: ((last - first) + 1), min: 1L, max: TimingWindow);

        for (var pass = 0; pass < m_timings.Length; pass++) {
            var count = 0;
            var total = 0d;
            foreach (var (submission, sampled, milliseconds) in m_timingValues.AsSpan(pass * TimingWindow, TimingWindow)) {
                if ((submission == 0) || (sampled < first) || (sampled > last)) { continue; }
                total += milliseconds;
                count++;
            }
            m_timings[pass] = m_timings[pass] with {
                Milliseconds = (count == 0 ? 0 : total / count),
                Samples = count,
                PerFrameMs = total / frames,
            };
        }
        m_timingWindowFrames = frames;
        m_timingWindowAt = frame;
    }
    private void ReleaseTiming() {
        foreach (var slot in m_timingSlots) { slot.Dispose(); }
        m_timingSlots = [];
        m_timingCompleted = false;
        m_timingEpoch++;
        m_timingRefusal = null;
        m_timings = [];
        m_timingValues = [];
        m_timingRevision = -1;
    }

    private sealed class TimingSlot : IDisposable {
        public IGpuTimestampPool? Pool;
        public IGpuReadbackBuffer? Readback;
        public IGpuSubmissionFence? Fence;
        public byte[] Bytes = [];
        public bool[] Recorded = [];
        public long Revision;
        public long Epoch;
        public long Submission;
        public long Frame;


        public void Dispose() {
            Pool?.Dispose();
            Readback?.Dispose();
            Pool = null;
            Readback = null;
            Fence = null;
            Bytes = [];
            Recorded = [];
        }
    }
}
