using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

internal sealed partial class SdfWorldPickReadback {
    private const int IndirectBytes = (SdfWorldPackage.IndirectPickWords * sizeof(uint));

    private readonly byte[] m_indirectBytes = new byte[IndirectBytes];

    public ulong ReadbackBytes => m_slots.Aggregate(0UL, static (bytes, slot) => checked((((((bytes
        + (slot.IdentityBuffer?.SizeBytes ?? 0UL)) + (slot.SurfaceBuffer?.SizeBytes ?? 0UL))
        + (slot.IndirectBuffer?.SizeBytes ?? 0UL)) + (slot.ProbeBuffer?.SizeBytes ?? 0UL)) + (slot.ReceiverBuffer?.SizeBytes ?? 0UL))));

    public void PrepareIndirect(int slot, SdfIndirectCache? cache, string? cacheVersion, string? diagnosticVersion, Span<byte> block) {
        var target = m_slots[slot];

        target.Cache = null;
        target.Lighting = null;
        target.PreviousLightingStamp = 0u;
        var applyOffset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.IndirectApply));

        static float ApplyWord(ReadOnlySpan<byte> bytes, int offset, int word) => BinaryPrimitives.ReadSingleLittleEndian(source: bytes[(offset + (word * sizeof(float)))..]);
        target.Application = new SdfIndirectApplication(ApplyWord(bytes: block, offset: applyOffset, word: 3),
            new Vector3(x: ApplyWord(bytes: block, offset: applyOffset, word: 0), y: ApplyWord(bytes: block, offset: applyOffset, word: 1), z: ApplyWord(bytes: block, offset: applyOffset, word: 2)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block[((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.IndirectContact))..]));
        if (!target.Record || (target.Request.Sample is null) || (cache is null) || (cacheVersion is null) || (diagnosticVersion is null)) { return; }
        target.IndirectBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: IndirectBytes,
            name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "pick-indirect", index: slot));
        var probeBytes = checked(((((ulong)cache.Layout.ProbeCapacity) * SdfIndirectLayout.ProbeWords) * sizeof(uint)));

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
        SdfFrameBlock.WriteIndirectPick(block: block, enabled: true, x: target.Request.X, y: target.Request.Y);
    }

    private SdfIndirectPick? AnswerIndirect(Slot slot) {
        if (slot.Cache is not { } cache) { return null; }
        slot.IndirectBuffer!.Read(destination: m_indirectBytes);
        slot.ProbeBuffer!.Read(destination: slot.ProbeBytes);
        uint Word(int word) => BinaryPrimitives.ReadUInt32LittleEndian(source: m_indirectBytes.AsSpan(start: (word * sizeof(uint))));
        float Scalar(int word) => BitConverter.UInt32BitsToSingle(value: Word(word: word));
        Vector3 Vector(int word) => new(x: Scalar(word: word), y: Scalar(word: (word + 1)), z: Scalar(word: (word + 2)));
        var corners = new SdfIndirectPickCorner[8];

        for (var corner = 0; (corner < corners.Length); corner++) {
            var word = (16 + (corner * 4));

            corners[corner] = new SdfIndirectPickCorner(unchecked((int)Word(word: word)), ((IrradianceProbeClass)Word(word: (word + 1))), Scalar(word: (word + 2)), Word(word: (word + 3)));
        }
        var classes = new int[5];

        foreach (var brick in cache.Bricks) {
            for (var local = 0; (local < SdfIndirectLayout.ProbesPerBrick); local++) {
                var index = ((brick.Slot * SdfIndirectLayout.ProbesPerBrick) + local);
                var state = BinaryPrimitives.ReadUInt32LittleEndian(source: slot.ProbeBytes.AsSpan(start: (((index * SdfIndirectLayout.ProbeWords) + 3) * sizeof(uint))));

                classes[(((state >> SdfIndirectLayout.EpochShift) == cache.Epoch) ? (int)(state & SdfIndirectLayout.ClassMask) : 4)]++;
            }
        }
        var census = new SdfIndirectCensus(classes[((int)IrradianceProbeClass.Active)], classes[((int)IrradianceProbeClass.Relocated)],
            classes[((int)IrradianceProbeClass.Inactive)], classes[((int)IrradianceProbeClass.Dormant)], classes[4]);

        return new SdfIndirectPick(((SdfIndirectPickStatus)Word(word: 0)), ((SdfIndirectTier)Word(word: 1)), unchecked((int)Word(word: 2)), Word(word: 3),
            Vector(word: 4), Vector(word: 8), Scalar(word: 7), Vector(word: 12), Word(word: 11), Word(word: 15), Array.AsReadOnly(array: corners),
            new SdfIndirectPickSources(Vector(word: 48), Vector(word: 52), Vector(word: 56), Vector(word: 60), Vector(word: 64)), cache, census, slot.Lighting) {
            Method = ((SdfIndirectMethod)Word(word: 51)),
            SourcesEnabled = ((SdfIndirectSources)Word(word: 55)),
            Application = slot.Application,
            Near = ((SdfIndirectNearOutcome)Word(word: 59)),
            NearDirection = Vector(word: 68),
            NearPreviousPublication = Word(word: 71),
            NearSource = (((Word(word: 59) is 2u or 3u) && (((SdfIndirectTier)Word(word: 1)) == SdfIndirectTier.High) &&
                (((SdfIndirectMethod)Word(word: 51)) == SdfIndirectMethod.Cache) &&
                (Word(word: 11) == ((uint)cache.PublishedGeneration)) && (Word(word: 15) == cache.PublishedStamp) &&
                (Word(word: 71) == slot.PreviousLightingStamp)) ? slot.Lighting : null),
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
        public SdfIndirectApplication Application;
    }
}
