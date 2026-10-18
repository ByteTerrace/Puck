using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// Each slot owns one readback buffer per demand: a pick's copies the record's first four words, an inspector's its first six. Every record
// of the render extent is the frame's own, so the answer needs no currency check. Its view's picker polls only
// signaled submissions; changing demand never replaces an in-flight buffer.
internal sealed class SdfWorldPickReadback : IDisposable {
    // Words 0 to 3: the ray parameter, the identity, the material and the triangle (sdf-visibility.hlsli).
    private const int PickBytes = 16;
    // Words 0 to 5: an inspector's surface normal (word 5) too.
    private const int SurfaceBytes = 24;
    private const int NormalOffset = 20;

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

    public void Prepare(int slot, uint width, uint height, SdfFrame frame, string visibility, SdfReprojectionView sample, long cut) {
        Poll();
        var target = m_slots[slot];

        if (target.Record || target.Submit) {
            // The slot's last copy recorded but never reached a submission: its request records again.
            target.Record = false;
            target.Submit = false;
            m_picker.Complete(readback: this, result: null);
        }
        if (!m_picker.Take(cut: cut, frame: frame, height: height, readback: this, request: out var request, sample: sample, width: width)) {
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
        target.Visibility = visibility;
        target.Record = true;
    }
    public ulong ReadbackBytes => m_slots.Aggregate(0UL, static (bytes, slot) => checked(((bytes
        + (slot.IdentityBuffer?.SizeBytes ?? 0UL)) + (slot.SurfaceBuffer?.SizeBytes ?? 0UL))));

    public bool Take(int slot, int index, out RenderGraphBufferReadback readback) {
        var target = m_slots[slot];

        if (!target.Record) {
            readback = default;
            return false;
        }
        var request = target.Request;

        target.Submit = true;
        switch (index) {
            case 0:
                readback = new RenderGraphBufferReadback(Version: target.Visibility!,
                    SourceOffsetBytes: (((((ulong)request.Y) * request.Width) + request.X) * SdfWorldPackage.VisibilityRecordByteLength),
                    SizeBytes: ((ulong)target.RecordBytes), Destination: target.Buffer!);
                return true;
            default:
                target.Record = false;
                readback = default;
                return false;
        }
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
            m_picker.Complete(readback: this, result: Answer(slot: slot));
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

    private SdfPickResult Answer(Slot slot) {
        var bytes = m_bytes.AsSpan(start: 0, length: slot.RecordBytes);

        slot.Buffer!.Read(destination: bytes);
        var request = slot.Request;
        var identity = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[4..]);
        var normal = (((identity != 0) && (request.Sample is not null))
            ? DecodeNormal(packed: BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[NormalOffset..]))
            : (0.0, 0.0, 0.0));

        return request with {
            Distance = BitConverter.Int32BitsToSingle(value: BinaryPrimitives.ReadInt32LittleEndian(source: bytes)),
            Identity = identity,
            Material = BinaryPrimitives.ReadInt32LittleEndian(source: bytes[8..]),
            Normal = new Vector3(x: ((float)normal.Item1), y: ((float)normal.Item2), z: ((float)normal.Item3)),
        };
    }

    // The visibility record's normal: two signed-normalized 16-bit octahedral coordinates, X in bits 0..15 and Y in bits
    // 16..31, a sync pair with the visibility record's packing in frame/sdf-visibility.hlsli. The reserved pair (-32768, -32768) means no
    // normal and decodes as zero; any other unfolds the lower half across the diagonals and normalizes.
    private static (double X, double Y, double Z) DecodeNormal(uint packed) {
        if (packed == 0x80008000U) {
            return (0.0, 0.0, 0.0);
        }

        var x = (((short)packed) / 32767.0);
        var y = (((short)(packed >> 16)) / 32767.0);
        var z = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: y));

        if (z < 0.0) {
            (x, y) = (((1.0 - Math.Abs(value: y)) * ((x >= 0.0) ? 1.0 : -1.0)), ((1.0 - Math.Abs(value: x)) * ((y >= 0.0) ? 1.0 : -1.0)));
        }

        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        return ((x / length), (y / length), (z / length));
    }

    private sealed class Slot {
        public IGpuReadbackBuffer? IdentityBuffer;
        public IGpuReadbackBuffer? SurfaceBuffer;

        public IGpuReadbackBuffer? Buffer => ((Request.Sample is null) ? IdentityBuffer : SurfaceBuffer);
        public int RecordBytes => ((Request.Sample is null) ? PickBytes : SurfaceBytes);

        public IGpuSubmissionFence? Fence;
        public SdfPickResult Request;
        public string? Visibility;
        public bool Record;
        public bool Submit;
    }
}
