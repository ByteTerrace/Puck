using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    /// <summary>The most recent completed samples retained in each pass's observational mean.</summary>
    public const int TimingWindow = 32;

    private TimingSlot[] m_timingSlots = [];
    private GpuPassTiming[] m_timings = [];
    private double[] m_timingValues = [];
    private int[] m_timingNext = [];
    private long m_timingRevision = -1;
    private bool m_timingEnabled;
    private long m_timingEpoch;
    private bool m_timingCompleted;
    private string? m_timingRefusal;
    private long m_timingFrames;
    private double m_timingLatest;
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
    /// <summary>Gets the current graph's completed per-pass means. Empty while disabled, unsupported, or awaiting its
    /// first timed submission. A pass with no completed pair has zero samples, never a fabricated time.</summary>
    public ReadOnlySpan<GpuPassTiming> Timings => ((m_timingCompleted && (m_timingRevision == m_revision)) ? m_timings : []);
    /// <summary>Gets how many timed submissions this node has read back, which moves with each one and never repeats.</summary>
    public long TimingFrames => m_timingFrames;
    /// <summary>Gets the summed pass time, in milliseconds, of the latest timed submission read back for the current graph,
    /// or zero while <see cref="Timings"/> is empty.</summary>
    public double LatestTimingMilliseconds => (Timings.IsEmpty ? 0d : m_timingLatest);
    /// <summary>Gets the identity of the submission <see cref="LatestTimingMilliseconds"/> timed, as the node's work ledger
    /// numbers it: the newest submission read back, whichever frame slot held it. Zero while <see cref="Timings"/> is
    /// empty.</summary>
    public long LatestTimingSubmission => (Timings.IsEmpty ? 0L : m_timingLatestSubmission);
    /// <summary>Gets the logical bytes of timestamp readback buffers still owned, including disabled slots awaiting fences.</summary>
    public ulong TimingReadbackBytes {
        get { var bytes = 0UL; foreach (var slot in m_timingSlots) { if (slot.Readback is not null) { bytes += ((ulong)slot.Bytes.Length); } } return bytes; }
    }
    /// <summary>Gets the retained timestamp sample, rolling-window and slot-mask payload bytes, excluding managed object headers.</summary>
    public ulong TimingCpuBytes {
        get {
            var bytes = ((ulong)(((m_timingValues.Length * sizeof(double)) + (m_timingNext.Length * sizeof(int))) + (m_timings.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<GpuPassTiming>())));

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
            m_timingValues = new double[(m_passLabels.Length * TimingWindow)];
            m_timingNext = new int[m_passLabels.Length];
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
    private void EndTiming(nint command, int slot, int pass) {
        if (!TimingActive || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
        var target = m_timingSlots[slot];
        var first = checked((uint)(pass * 2));

        pool.Write(command: command, index: (first + 1));
        pool.Resolve(command: command, first: first, count: 2, destination: target.Readback!.BufferHandle, offset: (((ulong)pass) * 16));
        m_gpu.Recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: target.Readback.BufferHandle,
            sourceStageMask: GpuStage.Transfer, sourceAccessMask: GpuAccess.TransferWrite,
            destinationStageMask: GpuStage.Host, destinationAccessMask: GpuAccess.HostRead);
        target.Recorded[pass] = true;
    }
    private void SubmitTiming(int slot, IGpuSubmissionFence fence) {
        if (TimingActive && (m_timingSlots.Length != 0) && (m_timingSlots[slot].Pool is not null)) {
            m_timingSlots[slot].Fence = fence;
            m_timingSlots[slot].Submission = m_submissions;
        }
    }
    private void PollTimings() {
        foreach (var slot in m_timingSlots) {
            if ((slot.Fence is { } fence) && !fence.IsSignaled) { continue; }
            if (!TimingActive) { slot.Dispose(); continue; }
            if (slot.Fence is null) { continue; }
            slot.Fence = null;
            if ((slot.Revision != m_revision) || (m_timingRevision != m_revision) || (slot.Epoch != m_timingEpoch)) { continue; }
            slot.Readback!.Read(destination: slot.Bytes);
            var pool = slot.Pool!;
            var mask = ((pool.ValidBits == 64) ? ulong.MaxValue : ((1UL << ((int)pool.ValidBits)) - 1));
            var frame = 0d;

            for (var pass = 0; (pass < slot.Recorded.Length); pass++) {
                if (!slot.Recorded[pass]) { continue; }
                var start = BinaryPrimitives.ReadUInt64LittleEndian(source: slot.Bytes.AsSpan(start: (pass * 16)));
                var end = BinaryPrimitives.ReadUInt64LittleEndian(source: slot.Bytes.AsSpan(start: ((pass * 16) + 8)));
                var value = (((unchecked((end - start)) & mask) * pool.NanosecondsPerTick) / 1_000_000);

                m_timingCompleted = true;
                var entry = m_timings[pass];
                var count = Math.Min(val1: TimingWindow, val2: (entry.Samples + 1));
                var index = ((pass * TimingWindow) + m_timingNext[pass]);
                var total = (((entry.Milliseconds * entry.Samples) - m_timingValues[index]) + value);

                frame += value;
                m_timingValues[index] = value;
                m_timingNext[pass] = ((m_timingNext[pass] + 1) % TimingWindow);
                m_timings[pass] = entry with { Milliseconds = (total / count), Samples = count };
            }
            // Several slots can complete in one poll, so the newest submission, not the last slot read, is the latest.
            if (slot.Submission > m_timingLatestSubmission) {
                m_timingLatest = frame;
                m_timingLatestSubmission = slot.Submission;
            }
            m_timingFrames++;
        }
    }
    private void ReleaseTiming() {
        foreach (var slot in m_timingSlots) { slot.Dispose(); }
        m_timingSlots = [];
        m_timingCompleted = false;
        m_timingEpoch++;
        m_timingRefusal = null;
        m_timings = [];
        m_timingValues = [];
        m_timingNext = [];
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
