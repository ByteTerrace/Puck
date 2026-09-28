using System.Buffers.Binary;
using System.Numerics;
using Puck.Assets.Textures;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// Each slot owns a V-row readback and, only when an inspector asks, a V-through-N surface readback.
// Its view's picker polls only signaled submissions; changing demand never replaces an in-flight buffer.
internal sealed class SdfWorldPickReadback : IDisposable {
    private const int PickBytes = 16;
    private const int SurfaceBytes = 48;

    private readonly SdfWorldPicker m_picker;
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly Slot[] m_slots;
    private readonly byte[] m_bytes = new byte[SurfaceBytes];

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

    public void Prepare(int slot, uint width, uint height, SdfFrame frame, string version, SdfReprojectionView sample, long cut) {
        Poll();
        var target = m_slots[slot];

        target.Record = false;
        target.Submit = false;
        if (!m_picker.Take(cut: cut, frame: frame, height: height, request: out var request, sample: sample, width: width)) {
            return;
        }
        if (request.Sample is not null) {
            target.SurfaceBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: SurfaceBytes,
                name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick-surface", index: slot));
        } else {
            target.IdentityBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: PickBytes,
                name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick", index: slot));
        }
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
            SizeBytes: ((ulong)target.SizeBytes), Destination: target.Buffer!);
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
            slot.Buffer!.Read(destination: m_bytes.AsSpan(start: 0, length: slot.SizeBytes));
            var identity = BinaryPrimitives.ReadUInt32LittleEndian(source: m_bytes.AsSpan(start: 4));
            var normal = (((identity != 0) && (slot.Request.Sample is not null))
                ? OctahedralNormal.DecodeSnorm16(packed: BinaryPrimitives.ReadUInt32LittleEndian(source: m_bytes.AsSpan(start: 44)))
                : (0.0, 0.0, 0.0));

            m_picker.Publish(result: slot.Request with {
                Distance = BitConverter.Int32BitsToSingle(value: BinaryPrimitives.ReadInt32LittleEndian(source: m_bytes)),
                Identity = identity,
                Material = BinaryPrimitives.ReadInt32LittleEndian(source: m_bytes.AsSpan(start: 8)),
                Normal = new Vector3(x: ((float)normal.Item1), y: ((float)normal.Item2), z: ((float)normal.Item3)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(source: m_bytes.AsSpan(start: 12)),
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
            slot.IdentityBuffer?.Dispose();
            slot.SurfaceBuffer?.Dispose();
        }
    }

    private sealed class Slot {
        public IGpuReadbackBuffer? IdentityBuffer;
        public IGpuReadbackBuffer? SurfaceBuffer;

        public IGpuReadbackBuffer? Buffer => ((Request.Sample is null) ? IdentityBuffer : SurfaceBuffer);
        public int SizeBytes => ((Request.Sample is null) ? PickBytes : SurfaceBytes);

        public IGpuSubmissionFence? Fence;
        public SdfPickResult Request;
        public string? Version;
        public bool Record;
        public bool Submit;
    }
}
