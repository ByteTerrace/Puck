using System.Numerics;
using Puck.Maths;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    private readonly record struct FeedbackRead(Vector3 Light, bool Answers);

    // Diagnostic only: stream each hit's three cages through one fixed scratch, retaining its gathered RGB rather than
    // its probe payload. The same CPU Relight/Select/Gather evaluates the reference; there is no second gather formula.
    private async Task<Dictionary<(int Level, (int X, int Y, int Z) Cell), string>> ReadReferences(SdfMeshProbeGpuWork gpu,
        SdfMeshIrradianceScene scene, Layout layout, (int X, int Y, int Z)[] lows, Lighting lighting,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData> probes,
        (Vector3 Point, Vector3 Normal, int Geometry, int Window, long Relit) key) {
        var descriptions = new Dictionary<(int Level, (int X, int Y, int Z) Cell), string>();
        using var scratch = new FeedbackScratch(this, lighting);
        var hits = new uint[SdfMeshProbeDepth.HitWords];
        var feedback = new FeedbackRead[SdfMeshProbeDepth.HitCount];
        var requested = ReferenceCells(layout, lows, probes, key.Point, key.Normal);
        foreach (var (cell, probe) in probes) {
            if (probe.Invalid || !requested.Contains(cell)) { continue; }
            var origin = new Vector3(cell.Cell.X + .5f, cell.Cell.Y + .5f, cell.Cell.Z + .5f) * layout.SpacingOf(cell.Level) + probe.Offset;
            SdfMeshProbeDepth.TraceHits(scene, origin, layout.Reach, hits, out _);
            Array.Clear(feedback);
            if (lighting.Feedback && layout.Bounce) {
                for (var texel = 0; texel < SdfMeshProbeDepth.HitCount; texel++) {
                    lock (m_gate) {
                        if (m_disposed || m_owed || m_relit != key.Relit || m_queryWanted != (key.Point, key.Normal)) { throw new OperationCanceledException(); }
                    }
                    if (!SdfMeshProbeDepth.TryHit(hits, texel, out var distance, out var normal, out _, out var back) || back) { continue; }
                    var point = origin + SdfMeshProbeDepth.RayDirections[texel] * distance;
                    await ReadFeedbackCage(gpu, scene, layout, lows, key.Geometry, key.Window, point, normal, scratch).ConfigureAwait(false);
                    feedback[texel] = ReadFeedback(point, normal, layout, lows, lighting.Epoch, scratch.Probes);
                }
            }
            descriptions.Add(cell, DescribeRelightReference(probe, origin, scene, layout, lighting, lows, hits, feedback));
        }
        return descriptions;
    }

    private HashSet<(int Level, (int X, int Y, int Z) Cell)> ReferenceCells(Layout layout, (int X, int Y, int Z)[] lows,
        IReadOnlyDictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData> probes, Vector3 point, Vector3 normal) {
        Span<(int X, int Y, int Z)> cells = stackalloc (int, int, int)[GatherSlots];
        Span<float> weights = stackalloc float[GatherSlots];
        var corners = new Corners();
        using var hold = m_blocks.Enter();
        var selection = Select(point, normal, layout, lows, cells, weights, ref corners, in hold, readback: probes);
        var requested = new HashSet<(int Level, (int X, int Y, int Z) Cell)>();
        for (var i = 0; i < (selection.RemainderLevel >= 0 ? GatherSlots : 8); i++) {
            _ = requested.Add((i < 8 ? selection.Level : selection.RemainderLevel, cells[i]));
        }
        return requested;
    }

    private FeedbackRead ReadFeedback(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows, int epoch,
        IReadOnlyDictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData> probes) {
        using var hold = m_blocks.Enter();
        var (sky, bounce, answers) = IncomingParts(point, normal, layout, lows, epoch, in hold, readback: probes);
        return new FeedbackRead(sky + bounce, answers);
    }

    private static async Task ReadFeedbackCage(SdfMeshProbeGpuWork gpu, SdfMeshIrradianceScene scene, Layout layout,
        (int X, int Y, int Z)[] lows, int geometry, int window, Vector3 point, Vector3 normal, FeedbackScratch scratch) {
        scratch.Probes.Clear();
        LocateCages(layout, lows, point, normal, scratch.Cells, scratch.Slots);
        var result = await gpu.ReadRecords(scene, scratch.Slots, scratch.CacheSlots, layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords,
            geometry, window, scratch.Result).ConfigureAwait(false);
        for (var i = 0; i < scratch.Slots.Length; i++) {
            if (scratch.Slots[i] < 0) { continue; }
            var record = result.Span.Slice(i * SdfMeshProbeGpuWork.ReadRecordWords + SdfMeshProbeRelight.OutputWords, SdfMeshIrradianceProbe.ProbeWords);
            if (!Holds(record, scratch.Cells[i].Cell)) { continue; }
            DecodeRecord(record, scratch.Hits, scratch.Lights, i);
            var xy = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.OffsetWord]);
            var zi = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.OffsetWord + 1]);
            scratch.Probes.Add(scratch.Cells[i], new ProbeData(Geometry: geometry, HitsBlock: scratch.Hits.Index, HitIndex: i,
                Invalid: (record[0] & SdfMeshIrradianceProbe.InvalidFlag) != 0, Offset: new Vector3(xy, zi.X), Inside: zi.Y,
                ShadingBlock: scratch.Lights.Index, LightIndex: i, Epoch: (int)(record[0] & SdfMeshIrradianceProbe.EpochMask)));
        }
    }

    // Private to one query task and never published: its blocks can be freed directly at the task's end. A loop may
    // rewrite them only after the preceding readback answered and its gather ended.
    private sealed class FeedbackScratch : IDisposable {
        public readonly int[] Slots = new int[SdfMeshProbeGpuWork.ReadRecordLimit];
        public readonly uint[] CacheSlots = new uint[SdfMeshProbeGpuWork.ReadRecordLimit];
        public readonly uint[] Result = new uint[SdfMeshProbeGpuWork.ReadRecordLimit * SdfMeshProbeGpuWork.ReadRecordWords + 1];
        public readonly (int Level, (int X, int Y, int Z) Cell)[] Cells = new (int, (int, int, int))[SdfMeshProbeGpuWork.ReadRecordLimit];
        public readonly Dictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData> Probes = new(SdfMeshProbeGpuWork.ReadRecordLimit);
        public ProbeBlock Hits { get; }
        public LightBlock Lights { get; }
        public FeedbackScratch(SdfMeshIrradiance volume, Lighting lighting) {
            Array.Fill(CacheSlots, uint.MaxValue);
            Hits = new ProbeBlock(SdfMeshProbeGpuWork.ReadRecordLimit, volume.m_allocator, payloadHits: false);
            try { Lights = new LightBlock(SdfMeshProbeGpuWork.ReadRecordLimit, lighting.Inputs, volume.m_allocator); }
            catch { Hits.Reclaim(); throw; }
            Hits.Charge(volume);
            Lights.Charge(volume);
        }
        public void Dispose() { Hits.Reclaim(); Lights.Reclaim(); }
    }
}
