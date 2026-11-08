using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// Each slot owns one readback buffer per demand: a pick's copies the record's first four words, an inspector's its first six. Every record
// of the render extent is the frame's own, so the answer needs no currency check. A copy is read only once the node proves
// its slot complete (RenderGraphSlotResults), which precedes the next copy into that slot's buffers; changing demand never
// replaces an in-flight buffer.
internal sealed class SdfWorldPickReadback : IDisposable {
    // Words 0 to 3: the ray parameter, the identity, the material and the triangle (sdf-visibility.hlsli).
    private const int PickBytes = 16;
    // Words 0 to 5: an inspector's surface normal (word 5) too.
    private const int SurfaceBytes = 24;
    private const int NormalOffset = 20;

    private readonly SdfWorldPicker m_picker;
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly Slot[] m_slots;
    private readonly RenderGraphSlotResults<Slot> m_copies;
    private readonly byte[] m_bytes = new byte[SurfaceBytes];

    private bool m_disposed;

    public SdfWorldPickReadback(SdfWorldPicker picker, RenderGraphPackageRecorderContext context) {
        m_picker = picker;
        m_context = context;
        m_slots = new Slot[context.InFlightFrames];
        for (var index = 0; (index < m_slots.Length); index++) {
            m_slots[index] = new Slot();
        }
        // A copy that never reached a submission ends unanswered, so its request records again.
        m_copies = new RenderGraphSlotResults<Slot>(slots: m_slots.Length,
            complete: slot => m_picker.Complete(readback: this, result: Answer(slot: slot)),
            discard: _ => m_picker.Complete(readback: this, result: null));
        picker.Attach(readback: this);
    }

    public void Prepare(int slot, uint width, uint height, SdfFrame frame, string visibility, SdfReprojectionView sample, long cut) {
        var target = m_slots[slot];

        if (target.Prepared) {
            // The slot's last request was prepared but its copy never recorded: it records again.
            target.Prepared = false;
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
        target.Prepared = true;
    }
    public ulong ReadbackBytes => m_slots.Aggregate(0UL, static (bytes, slot) => checked(((bytes
        + (slot.IdentityBuffer?.SizeBytes ?? 0UL)) + (slot.SurfaceBuffer?.SizeBytes ?? 0UL))));

    // Takes the slot's prepared copy, once, capturing it with the recording.
    public bool Take(int slot, out RenderGraphBufferReadback readback) {
        var target = m_slots[slot];

        if (!target.Prepared) {
            readback = default;
            return false;
        }
        var request = target.Request;

        target.Prepared = false;
        m_copies.Record(slot: slot, result: target);
        readback = new RenderGraphBufferReadback(Version: target.Visibility!,
            SourceOffsetBytes: (((((ulong)request.Y) * request.Width) + request.X) * SdfWorldPackage.VisibilityRecordByteLength),
            SizeBytes: ((ulong)target.RecordBytes), Destination: target.Buffer!);
        return true;
    }
    public void Submitted(int slot) => m_copies.Submitted(slot: slot);
    public void Completed(int slot) => m_copies.Completed(slot: slot);
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

    // The visibility record's normal word (SdfVisibility.NormalScale): two signed-normalized 16-bit octahedral
    // coordinates unpacked and decoded through SdfOctahedral, the C# statement of the kernels' sdfOctDecode. The reserved
    // pair (SdfVisibility.ZeroNormal) means no normal and decodes as zero.
    private static (double X, double Y, double Z) DecodeNormal(uint packed) {
        if (packed == SdfVisibility.ZeroNormal) {
            return (0.0, 0.0, 0.0);
        }

        var normal = SdfOctahedral.Decode(
            x: (((short)packed) / ((double)SdfVisibility.NormalScale)),
            y: (((short)(packed >> 16)) / ((double)SdfVisibility.NormalScale))
        );

        return (normal.X, normal.Y, normal.Z);
    }

    private sealed class Slot {
        public IGpuReadbackBuffer? IdentityBuffer;
        public IGpuReadbackBuffer? SurfaceBuffer;

        public IGpuReadbackBuffer? Buffer => ((Request.Sample is null) ? IdentityBuffer : SurfaceBuffer);
        public int RecordBytes => ((Request.Sample is null) ? PickBytes : SurfaceBytes);

        public SdfPickResult Request;
        public string? Visibility;
        // Whether Prepare took a request whose copy the slot's recording has not yet taken.
        public bool Prepared;
    }
}
