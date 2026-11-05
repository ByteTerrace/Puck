using System.Numerics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // One placement algorithm for the CPU reference and GPU runtime. Each trial round traces every unresolved
    // probe together; the coordinator waits once per round, never once per ray or once per worker thread.
    private ProbeData[] PlaceBatch(List<Candidate> candidates, int first, int count, Layout layout,
        SdfMeshIrradianceScene scene, int geometry, ProbeBlock block, ParallelOptions parallel) {
        var centres = new Vector3[count];
        var spacings = new Vector3[count];
        var pushed = new Vector3[count];
        var offsets = new Vector3[count];
        var near = new bool[count];
        var pending = new bool[count];
        var valid = new bool[count];
        var inside = new float[count];
        var savedHits = new uint[count * SdfMeshProbeDepth.HitWords];
        var indices = new List<int>(count);
        var clearance = SdfMeshIrradianceProbe.Clearance;

        for (var i = 0; i < count; i++) {
            var candidate = candidates[first + i];
            if (!candidate.Trace) { continue; }
            spacings[i] = layout.SpacingOf(candidate.Level);
            centres[i] = new Vector3(candidate.Cell.X + 0.5f, candidate.Cell.Y + 0.5f, candidate.Cell.Z + 0.5f) * spacings[i];
            indices.Add(i);
        }
        TraceRound();
        foreach (var i in indices) {
            block.Hits(i).CopyTo(savedHits.AsSpan(i * SdfMeshProbeDepth.HitWords));
            near[i] = scene.TryNearestSurface(centres[i], clearance, out var surface, out var normal, out _);
            valid[i] = inside[i] <= InsideShare && !near[i];
            if (valid[i]) { continue; }
            pending[i] = near[i] || scene.TryNearestSurface(centres[i], (0.5f * spacings[i]).Length() + clearance, out surface, out normal, out _);
            pushed[i] = surface + normal * (1.25f * clearance) - centres[i];
        }
        for (var candidate = -1; candidate < Candidates.Length; candidate++) {
            indices.Clear();
            for (var i = 0; i < count; i++) {
                if (!pending[i] || valid[i]) { continue; }
                var trial = candidate < 0 ? pushed[i] : Candidates[candidate] * spacings[i];
                var reach = 0.5f * spacings[i];
                if (MathF.Abs(trial.X) > reach.X || MathF.Abs(trial.Y) > reach.Y || MathF.Abs(trial.Z) > reach.Z) { continue; }
                var origin = centres[i] + trial;
                if (scene.TryNearestSurface(origin, clearance, out _, out _, out _)) { continue; }
                if (Blocked(origin, scene, MathF.Max(spacings[i].X, MathF.Max(spacings[i].Y, spacings[i].Z)))) { continue; }
                offsets[i] = trial;
                indices.Add(i);
            }
            if (indices.Count == 0) { continue; }
            TraceRound();
            foreach (var i in indices) { valid[i] = inside[i] <= InsideShare; }
        }
        var result = new ProbeData[count];
        for (var i = 0; i < count; i++) {
            if (!candidates[first + i].Trace) { continue; }
            if (!valid[i]) {
                savedHits.AsSpan(i * SdfMeshProbeDepth.HitWords, SdfMeshProbeDepth.HitWords).CopyTo(block.Hits(i));
                SummarizeHits(block.Hits(i), block.Sky(i), out inside[i]);
                offsets[i] = Vector3.Zero;
            }
            result[i] = new ProbeData(Geometry: geometry, Hits: block, HitIndex: i, Inside: inside[i], Invalid: !valid[i], Offset: offsets[i]);
        }
        return result;

        void TraceRound() {
            if (indices.Count == 0) { return; }
            var origins = indices.Select(i => new Vector4(centres[i] + offsets[i], layout.Reach)).ToArray();
            var trace = GpuTrace;
            if (trace is not null) {
                var hits = trace.Trace(scene, origins);
                for (var j = 0; j < indices.Count; j++) {
                    var i = indices[j];
                    hits.AsSpan(j * SdfMeshProbeDepth.HitWords, SdfMeshProbeDepth.HitWords).CopyTo(block.Hits(i));
                    SummarizeHits(block.Hits(i), block.Sky(i), out inside[i]);
                }
            } else {
                Parallel.For(0, indices.Count, parallel, j => {
                    var i = indices[j];
                    TraceHits(centres[i] + offsets[i], layout, scene, block.Hits(i), block.Sky(i), out inside[i]);
                });
            }
        }
    }
}
