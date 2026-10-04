using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

internal sealed partial class SdfWorldPickReadback {
    private const int IndirectBytes = SdfWorldPackage.IndirectPickWords * sizeof(uint);
    private readonly byte[] m_indirectBytes = new byte[IndirectBytes];

    public ulong ReadbackBytes => m_slots.Aggregate(0UL, static (bytes, slot) => checked(bytes
        + (slot.IdentityBuffer?.SizeBytes ?? 0UL) + (slot.SurfaceBuffer?.SizeBytes ?? 0UL)
        + (slot.IndirectBuffer?.SizeBytes ?? 0UL) + (slot.ProbeBuffer?.SizeBytes ?? 0UL) + (slot.ReceiverBuffer?.SizeBytes ?? 0UL)));

    public void PrepareIndirect(int slot, SdfIndirectCache? cache, string? cacheVersion, string? diagnosticVersion, Span<byte> block) {
        var target = m_slots[slot];
        target.Cache = null;
        target.Lighting = null;
        target.PreviousLightingStamp = 0u;
        if (!target.Record || target.Request.Sample is null || cache is null || cacheVersion is null || diagnosticVersion is null) { return; }
        target.IndirectBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: IndirectBytes,
            name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick-indirect", index: slot));
        var probeBytes = checked((ulong)cache.Layout.ProbeCapacity * SdfIndirectLayout.ProbeWords * sizeof(uint));
        if (target.ProbeBuffer?.SizeBytes != probeBytes) {
            target.ProbeBuffer?.Dispose();
            target.ProbeBuffer = m_context.Services.BufferFactory.CreateReadback(sizeBytes: probeBytes,
                name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick-probe-census", index: slot));
            target.ProbeBytes = new byte[checked((int)probeBytes)];
        }
        target.Cache = cache.Snapshot();
        target.Lighting = cache.PublishedLightingSource;
        target.PreviousLightingStamp = cache.PreviousPublishedStamp;
        target.CacheVersion = cacheVersion;
        target.IndirectVersion = diagnosticVersion;
        SdfFrameBlock.WriteIndirectPick(block, true, target.Request.X, target.Request.Y);
    }

    private SdfIndirectPick? AnswerIndirect(Slot slot) {
        if (slot.Cache is not { } cache) { return null; }
        slot.IndirectBuffer!.Read(m_indirectBytes);
        slot.ProbeBuffer!.Read(slot.ProbeBytes);
        uint Word(int word) => BinaryPrimitives.ReadUInt32LittleEndian(m_indirectBytes.AsSpan(word * sizeof(uint)));
        float Scalar(int word) => BitConverter.UInt32BitsToSingle(Word(word));
        Vector3 Vector(int word) => new(Scalar(word), Scalar(word + 1), Scalar(word + 2));
        var corners = new SdfIndirectPickCorner[8];
        for (var corner = 0; corner < corners.Length; corner++) {
            var word = 16 + corner * 4;
            corners[corner] = new SdfIndirectPickCorner(unchecked((int)Word(word)), (IrradianceProbeClass)Word(word + 1), Scalar(word + 2), Word(word + 3));
        }
        var classes = new int[5];
        foreach (var brick in cache.Bricks) {
            for (var local = 0; local < SdfIndirectLayout.ProbesPerBrick; local++) {
                var index = brick.Slot * SdfIndirectLayout.ProbesPerBrick + local;
                var state = BinaryPrimitives.ReadUInt32LittleEndian(slot.ProbeBytes.AsSpan((index * SdfIndirectLayout.ProbeWords + 3) * sizeof(uint)));
                classes[(state >> SdfIndirectLayout.EpochShift) == cache.Epoch ? (int)(state & SdfIndirectLayout.ClassMask) : 4]++;
            }
        }
        var census = new SdfIndirectCensus(classes[(int)IrradianceProbeClass.Active], classes[(int)IrradianceProbeClass.Relocated],
            classes[(int)IrradianceProbeClass.Inactive], classes[(int)IrradianceProbeClass.Dormant], classes[4]);
        return new SdfIndirectPick((SdfIndirectPickStatus)Word(0), (SdfIndirectTier)Word(1), unchecked((int)Word(2)), Word(3),
            Vector(4), Vector(8), Scalar(7), Vector(12), Word(11), Word(15), Array.AsReadOnly(corners),
            new SdfIndirectPickSources(Vector(48), Vector(52), Vector(56), Vector(60), Vector(64)), cache, census, slot.Lighting) {
            Method = (SdfIndirectMethod)Word(51),
            SourcesEnabled = (SdfIndirectSources)Word(55),
            Near = (SdfIndirectNearOutcome)Word(59),
            NearDirection = Vector(68),
            NearPreviousPublication = Word(71),
            NearSource = (Word(59) is 2u or 3u) && (SdfIndirectTier)Word(1) == SdfIndirectTier.High &&
                (SdfIndirectMethod)Word(51) == SdfIndirectMethod.Cache &&
                Word(11) == (uint)cache.PublishedGeneration && Word(15) == cache.PublishedStamp &&
                Word(71) == slot.PreviousLightingStamp ? slot.Lighting : null,
        };
    }

    private sealed partial class Slot {
        public IGpuReadbackBuffer? IndirectBuffer;
        public IGpuReadbackBuffer? ProbeBuffer;
        public byte[] ProbeBytes = [];
        public string? IndirectVersion;
        public string? CacheVersion;
        public SdfIndirectCacheSnapshot? Cache;
        public SdfIndirectLightingSnapshot? Lighting;
        public uint PreviousLightingStamp;
    }
}
