using System.Buffers.Binary;
using System.Numerics;
using Puck.Assets.Textures;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// Each slot owns one readback buffer per demand: a pick's copies V through L.x, an inspector's V through N, and either
// way the frame's dispatch box after them, so the answer is judged current by the box its own frame wrote. A slot also
// keeps the transform table of the frame its copy recorded, the rows that frame's upload staged, so a winning slot
// resolves against the table the record was rendered with. Its view's picker polls only signaled submissions; changing
// demand never replaces an in-flight buffer.
internal sealed partial class SdfWorldPickReadback : IDisposable {
    // V (4 words), C (3) and L.x: the identity, the ray parameter, the material, the flags and the transform slot.
    private const int PickBytes = 32;
    // V through N: an inspector's surface normal too.
    private const int SurfaceBytes = 48;
    private const int BoxBytes = (SdfVisibility.BoxWords * sizeof(uint));
    private const int FrameSlotOffset = 28;
    private const int NormalOffset = 44;

    private readonly SdfWorldPicker m_picker;
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly Slot[] m_slots;
    private readonly byte[] m_bytes = new byte[(SurfaceBytes + BoxBytes)];

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

    public void Prepare(int slot, uint width, uint height, SdfFrame frame, string visibility, string box, SdfReprojectionView sample, long cut) {
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
            target.SurfaceBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: (SurfaceBytes + BoxBytes),
                name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick-surface", index: slot));
        } else {
            target.IdentityBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: (PickBytes + BoxBytes),
                name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick", index: slot));
        }
        target.Capture(transforms: frame.DynamicTransforms);
        target.Request = request;
        target.Visibility = visibility;
        target.Box = box;
        target.Record = true;
    }
    public bool Take(int slot, int index, out RenderGraphBufferReadback readback) {
        var target = m_slots[slot];

        if (target.ReceiverRecord) {
            if (index == 0) {
                target.ReceiverSubmit = true;
                readback = new RenderGraphBufferReadback(Version: target.ReceiverVersion!, SourceOffsetBytes: target.ReceiverOffset,
                    SizeBytes: sizeof(uint), Destination: target.ReceiverBuffer!);
                return true;
            }
            index--;
        }
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
            case 1:
                readback = new RenderGraphBufferReadback(Version: target.Box!, SourceOffsetBytes: 0, SizeBytes: BoxBytes,
                    Destination: target.Buffer!, DestinationOffsetBytes: ((ulong)target.RecordBytes));
                return true;
            case 2 when target.Cache is not null:
                readback = new RenderGraphBufferReadback(Version: target.IndirectVersion!, SourceOffsetBytes: 0,
                    SizeBytes: IndirectBytes, Destination: target.IndirectBuffer!);
                return true;
            case 3 when target.Cache is not null:
                readback = new RenderGraphBufferReadback(Version: target.CacheVersion!, SourceOffsetBytes: 0,
                    SizeBytes: target.ProbeBuffer!.SizeBytes, Destination: target.ProbeBuffer);
                return true;
            default:
                target.Record = false;
                readback = default;
                return false;
        }
    }
    public void Submitted(int slot, IGpuSubmissionFence fence) {
        var target = m_slots[slot];

        if (target.ReceiverSubmit) {
            target.ReceiverRecord = false;
            target.ReceiverSubmit = false;
            target.ReceiverFence = fence;
        }
        if (!target.Submit) {
            return;
        }
        target.Submit = false;
        target.Fence = fence;
    }
    public void Poll() {
        PollReceivers();
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
            slot.IndirectBuffer?.Dispose();
            slot.ProbeBuffer?.Dispose();
            slot.ReceiverBuffer?.Dispose();
        }
    }

    // A pixel outside its frame's dispatch box holds an earlier frame's record, so it answers nothing: the sky.
    private SdfPickResult Answer(Slot slot) {
        var bytes = m_bytes.AsSpan(start: 0, length: (slot.RecordBytes + BoxBytes));

        slot.Buffer!.Read(destination: bytes);
        var request = slot.Request;
        Span<uint> box = stackalloc uint[SdfVisibility.BoxWords];

        for (var word = 0; (word < box.Length); word++) {
            box[word] = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[(slot.RecordBytes + (word * sizeof(uint)))..]);
        }
        if (!SdfVisibility.IsCurrent(box: box, x: request.X, y: request.Y)) {
            return request;
        }
        var identity = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[4..]);
        var normal = (((identity != 0) && (request.Sample is not null))
            ? OctahedralNormal.DecodeSnorm16(packed: BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[NormalOffset..]))
            : (0.0, 0.0, 0.0));
        var transformSlot = ((SdfVisibility.KindOf(identity: identity) == SdfVisibilityKind.Sdf)
            ? SdfVisibility.TransformSlotOf(word: BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[FrameSlotOffset..]))
            : null);

        return request with {
            Distance = BitConverter.Int32BitsToSingle(value: BinaryPrimitives.ReadInt32LittleEndian(source: bytes)),
            Identity = identity,
            Material = BinaryPrimitives.ReadInt32LittleEndian(source: bytes[8..]),
            Normal = new Vector3(x: ((float)normal.Item1), y: ((float)normal.Item2), z: ((float)normal.Item3)),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[12..]),
            TransformSlot = transformSlot,
            Transform = ((transformSlot is { } index) ? slot.TransformAt(slot: index) : null),
            Indirect = AnswerIndirect(slot),
        };
    }

    private sealed partial class Slot {
        private DynamicTransform[] m_transforms = [];
        private int m_transformCount;

        public IGpuReadbackBuffer? IdentityBuffer;
        public IGpuReadbackBuffer? SurfaceBuffer;

        public IGpuReadbackBuffer? Buffer => ((Request.Sample is null) ? IdentityBuffer : SurfaceBuffer);
        public int RecordBytes => ((Request.Sample is null) ? PickBytes : SurfaceBytes);

        public IGpuSubmissionFence? Fence;
        public SdfPickResult Request;
        public string? Visibility;
        public string? Box;
        public bool Record;
        public bool Submit;

        // Copies the recording frame's table into storage the slot reuses, grown only when a larger table arrives.
        public void Capture(IReadOnlyList<DynamicTransform> transforms) {
            if (m_transforms.Length < transforms.Count) {
                m_transforms = new DynamicTransform[transforms.Count];
            }
            for (var index = 0; (index < transforms.Count); index++) {
                m_transforms[index] = transforms[index];
            }
            m_transformCount = transforms.Count;
        }
        public DynamicTransform TransformAt(int slot) => ((slot < m_transformCount)
            ? m_transforms[slot]
            : throw new InvalidDataException(message: $"The visibility record names transform slot {slot}, outside the {m_transformCount}-slot table of the frame it was rendered from."));
    }
}
