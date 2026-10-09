using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The transport recorder's copies of the cache's measured-cost counters (SdfIndirectLayout.Cost*), one readback buffer
// per frame slot. A copy is taken after each trace dispatch and read only once its submission's fence has signaled,
// in submission order, by the cache's produced-frame boundary (SdfIndirectCache.BeginFrame); nothing ever waits for it.
internal sealed class SdfIndirectCostReadback : IDisposable {
    private const int Bytes = (SdfIndirectLayout.CostWords * sizeof(uint));

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly Slot[] m_slots;

    private readonly uint[] m_words = new uint[SdfIndirectLayout.CostWords];
    private readonly List<Slot> m_signaled = [];

    private long m_sequence;
    private bool m_disposed;

    public SdfIndirectCostReadback(RenderGraphPackageRecorderContext context) {
        m_context = context;
        m_slots = new Slot[Math.Max(val1: 1, val2: context.InFlightFrames)];
        for (var index = 0; (index < m_slots.Length); index++) { m_slots[index] = new Slot(); }
    }

    /// <summary>Gets the actual readback bytes this recorder created.</summary>
    public ulong ReadbackBytes => m_slots.Aggregate(0UL, static (bytes, slot) => (bytes + (slot.Buffer?.SizeBytes ?? 0UL)));

    /// <summary>Asks the graph to copy the counters of the recording frame's cache version after its dispatch.</summary>
    public void Prepare(SdfIndirectCache cache, int slot, string version, ulong offsetBytes) {
        Poll(cache: cache);
        var target = m_slots[slot];

        target.Buffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: Bytes,
            name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "costs", index: slot));
        target.Version = version;
        target.Offset = offsetBytes;
        target.Generation = cache.CostGeneration;
        target.Record = true;
        target.Submit = false;
    }
    public bool Take(int slot, int index, out RenderGraphBufferReadback readback) {
        var target = m_slots[slot];

        if ((index != 0) || !target.Record) {
            readback = default;
            return false;
        }
        target.Record = false;
        target.Submit = true;
        readback = new RenderGraphBufferReadback(Version: target.Version!, SourceOffsetBytes: target.Offset, SizeBytes: Bytes, Destination: target.Buffer!);
        return true;
    }
    public void Submitted(int slot, IGpuSubmissionFence fence) {
        var target = m_slots[slot];

        if (!target.Submit) { return; }
        target.Submit = false;
        target.Fence = fence;
        target.Sequence = ++m_sequence;
    }
    // Reads every signaled copy in the order its submissions were made.
    public void Poll(SdfIndirectCache cache) {
        m_signaled.Clear();
        foreach (var slot in m_slots) {
            if ((slot.Fence is { } fence) && fence.IsSignaled) { m_signaled.Add(item: slot); }
        }
        m_signaled.Sort(comparison: static (a, b) => a.Sequence.CompareTo(value: b.Sequence));
        foreach (var slot in m_signaled) {
            slot.Fence = null;
            slot.Buffer!.Read(destination: MemoryMarshal.AsBytes(span: m_words.AsSpan()));
            cache.ObserveCost(counters: m_words, generation: slot.Generation, sequence: slot.Sequence);
        }
    }
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var slot in m_slots) { slot.Buffer?.Dispose(); }
    }

    private sealed class Slot {
        public IGpuReadbackBuffer? Buffer;
        public IGpuSubmissionFence? Fence;
        public string? Version;
        public ulong Offset;
        public long Generation;
        public long Sequence;
        public bool Record;
        public bool Submit;
    }
}
