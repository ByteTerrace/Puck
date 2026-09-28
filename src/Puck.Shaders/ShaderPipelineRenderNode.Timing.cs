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

    /// <summary>Gets or sets whether pass timestamp queries are recorded. Off by default: no pools, buffers or query
    /// operations exist until enabled. Disabling withdraws the readout immediately and retires pools after their fences.</summary>
    public bool TimingEnabled {
        get => m_timingEnabled;
        set {
            if (m_timingEnabled == value) { return; }
            m_timingEnabled = value;
            m_timingEpoch++;
            m_timingCompleted = false;
            m_timingRevision = -1;
            m_timings = [];
            PollTimings();
        }
    }
    /// <summary>Gets the current graph's completed per-pass means. Empty while disabled, unsupported, or awaiting its
    /// first timed submission. A pass with no completed pair has zero samples, never a fabricated time.</summary>
    public ReadOnlySpan<GpuPassTiming> Timings => ((m_timingCompleted && (m_timingRevision == m_revision)) ? m_timings : []);
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

    private void PrepareTiming(int slot) {
        PollTimings();
        if (!TimingEnabled || (m_gpu.TimestampFactory is not { } factory) || (m_passLabels.Length == 0)) { return; }
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
            if (target.Pool is null) { return; }
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
    private void BeginTiming(nint command, int slot, int pass) {
        if (!TimingEnabled || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
        var first = checked((uint)(pass * 2));

        pool.Reset(command: command, count: 2, first: first);
        pool.Write(command: command, index: first);
    }
    private void EndTiming(nint command, int slot, int pass) {
        if (!TimingEnabled || (m_timingSlots.Length == 0) || (m_timingSlots[slot].Pool is not { } pool)) { return; }
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
        if (TimingEnabled && (m_timingSlots.Length != 0) && (m_timingSlots[slot].Pool is not null)) {
            m_timingSlots[slot].Fence = fence;
        }
    }
    private void PollTimings() {
        foreach (var slot in m_timingSlots) {
            if ((slot.Fence is { } fence) && !fence.IsSignaled) { continue; }
            if (!TimingEnabled) { slot.Dispose(); continue; }
            if (slot.Fence is null) { continue; }
            slot.Fence = null;
            if ((slot.Revision != m_revision) || (m_timingRevision != m_revision) || (slot.Epoch != m_timingEpoch)) { continue; }
            slot.Readback!.Read(destination: slot.Bytes);
            var pool = slot.Pool!;
            var mask = ((pool.ValidBits == 64) ? ulong.MaxValue : ((1UL << ((int)pool.ValidBits)) - 1));

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

                m_timingValues[index] = value;
                m_timingNext[pass] = ((m_timingNext[pass] + 1) % TimingWindow);
                m_timings[pass] = entry with { Milliseconds = (total / count), Samples = count };
            }
        }
    }
    private void ReleaseTiming() {
        foreach (var slot in m_timingSlots) { slot.Dispose(); }
        m_timingSlots = [];
        m_timingCompleted = false;
        m_timingEpoch++;
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
