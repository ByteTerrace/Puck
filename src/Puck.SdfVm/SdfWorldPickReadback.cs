using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The pass owns each slot's sixteen-byte V-row readback; its view's picker polls only signaled submissions.
internal sealed class SdfWorldPickReadback : IDisposable {
    private const int PickBytes = 16;

    private readonly SdfWorldPicker m_picker;
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly Slot[] m_slots;
    private readonly byte[] m_bytes = new byte[PickBytes];

    private bool m_disposed;

    public SdfWorldPickReadback(SdfWorldPicker picker, RenderGraphPackageRecorderContext context) {
        m_picker = picker;
        m_context = context;
        m_slots = new Slot[context.InFlightFrames];
        for (var index = 0; (index < m_slots.Length); index++) {
            m_slots[index] = new Slot();
        }
        picker.Attach(readback: this);
    }

    public void Prepare(int slot, uint width, uint height, SdfFrame frame, string version) {
        Poll();
        var target = m_slots[slot];

        target.Record = false;
        target.Submit = false;
        if (!m_picker.Take(frame: frame, height: height, request: out var request, width: width)) {
            return;
        }
        target.Buffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: PickBytes,
            name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick", index: slot));
        target.Request = request;
        target.Version = version;
        target.Record = true;
    }
    public bool Take(int slot, out RenderGraphBufferReadback readback) {
        var target = m_slots[slot];

        if (!target.Record) {
            readback = default;
            return false;
        }
        target.Record = false;
        target.Submit = true;
        var request = target.Request;

        readback = new RenderGraphBufferReadback(Version: target.Version!,
            SourceOffsetBytes: (((((ulong)request.Y) * request.Width) + request.X) * SdfWorldPackage.VisibilityRecordByteLength),
            SizeBytes: PickBytes, Destination: target.Buffer!);
        return true;
    }
    public void Submitted(int slot, IGpuSubmissionFence fence) {
        var target = m_slots[slot];

        if (!target.Submit) {
            return;
        }
        target.Submit = false;
        target.Fence = fence;
    }
    public void Poll() {
        foreach (var slot in m_slots) {
            if ((slot.Fence is not { } fence) || !fence.IsSignaled) {
                continue;
            }
            slot.Fence = null;
            slot.Buffer!.Read(destination: m_bytes);
            m_picker.Publish(result: slot.Request with {
                Distance = BitConverter.Int32BitsToSingle(value: BinaryPrimitives.ReadInt32LittleEndian(source: m_bytes)),
                Identity = BinaryPrimitives.ReadUInt32LittleEndian(source: m_bytes.AsSpan(start: 4)),
                Material = BinaryPrimitives.ReadInt32LittleEndian(source: m_bytes.AsSpan(start: 8)),
            });
        }
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_picker.Detach(readback: this);
        foreach (var slot in m_slots) {
            slot.Buffer?.Dispose();
        }
    }

    private sealed class Slot {
        public IGpuReadbackBuffer? Buffer;
        public IGpuSubmissionFence? Fence;
        public SdfPickResult Request;
        public string? Version;
        public bool Record;
        public bool Submit;
    }
}
