using System.Numerics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // One placement algorithm for the CPU reference and GPU runtime. Each trial round traces every unresolved
    // probe together; the coordinator waits once per round, never once per ray or once per worker thread.
    // On the GPU each placed probe's traces also land in the device hit cache: its first trace in one fresh slot, its
    // trial traces in a second. `fresh` receives the slot holding the hits the probe keeps, which also rides its
    // ProbeData (GpuSlot) for its relight to adopt (AdoptGpuHits); the other slot is freed here.
    private ProbeData[] PlaceBatch(List<Candidate> candidates, int first, int count, Layout layout, (int X, int Y, int Z)[] lows,
        SdfMeshIrradianceScene scene, int geometry, ProbeBlock block, ParallelOptions parallel, uint[] fresh) {
        var centres = new Vector3[count];
        var spacings = new Vector3[count];
        var pushed = new Vector3[count];
        var offsets = new Vector3[count];
        var near = new bool[count];
        var pending = new bool[count];
        var valid = new bool[count];
        var inside = new float[count];
        // A probe's saved words are written in Place before the result loop reads them, so the scratch keeps stale words
        // only for probes that read none.
        GrowScratch(array: ref m_gpuSavedHits, length: (count * SdfMeshProbeDepth.HitWords));
        var savedHits = m_gpuSavedHits;
        var indices = new List<int>(count);
        var clearance = SdfMeshIrradianceProbe.Clearance;
        var trace = GpuTrace;
        var trialSlots = new uint[count];
        var firstRound = true;

        Array.Fill(fresh, NoGpuHits, 0, count);
        Array.Fill(trialSlots, NoGpuHits);
        for (var i = 0; i < count; i++) {
            var candidate = candidates[first + i];
            if (!candidate.Trace) { continue; }
            spacings[i] = layout.SpacingOf(candidate.Level);
            centres[i] = new Vector3(candidate.Cell.X + 0.5f, candidate.Cell.Y + 0.5f, candidate.Cell.Z + 0.5f) * spacings[i];
            indices.Add(i);
        }
        try {
            TraceRound();
            firstRound = false;
            Place();
        } catch {
            if (trace is not null) {
                for (var i = 0; i < count; i++) {
                    if (fresh[i] != NoGpuHits) { m_hitSlots.Free(start: fresh[i]); }
                    if (trialSlots[i] != NoGpuHits) { m_hitSlots.Free(start: trialSlots[i]); }
                }
                Array.Fill(fresh, NoGpuHits, 0, count);
            }
            throw;
        }
        var result = new ProbeData[count];
        for (var i = 0; i < count; i++) {
            if (!candidates[first + i].Trace) { continue; }
            if (!valid[i]) {
                savedHits.AsSpan(i * SdfMeshProbeDepth.HitWords, SdfMeshProbeDepth.HitWords).CopyTo(block.Hits(i));
                SummarizeHits(block.Hits(i), block.Sky(i), out inside[i]);
                offsets[i] = Vector3.Zero;
            }
            // A probe valid after a trial keeps its trial slot; any other keeps its first trace's.
            if (trialSlots[i] != NoGpuHits) {
                var (kept, freed) = valid[i] ? (trialSlots[i], fresh[i]) : (fresh[i], trialSlots[i]);
                m_hitSlots.Free(start: freed);
                fresh[i] = kept;
            }
            result[i] = new ProbeData(Geometry: geometry, Hits: block, HitIndex: i, Inside: inside[i], Invalid: !valid[i], Offset: offsets[i],
                GpuSlot: (fresh[i] == NoGpuHits) ? 0u : fresh[i]);
        }
        return result;

        void Place() {
            foreach (var i in indices) {
                block.Hits(i).CopyTo(savedHits.AsSpan(i * SdfMeshProbeDepth.HitWords));
                near[i] = scene.TryNearestSurface(centres[i], clearance, out var surface, out var normal, out _);
                valid[i] = inside[i] <= SdfMeshIrradianceProbe.InsideShare && !near[i];
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
                foreach (var i in indices) { valid[i] = inside[i] <= SdfMeshIrradianceProbe.InsideShare; }
            }
        }

        void TraceRound() {
            if (indices.Count == 0) { return; }
            var origins = indices.Select(i => new Vector4(centres[i] + offsets[i], layout.Reach)).ToArray();
            if (trace is not null) {
                var slots = new uint[indices.Count];
                for (var j = 0; j < slots.Length; j++) {
                    var i = indices[j];
                    ref var slot = ref (firstRound ? ref fresh[i] : ref trialSlots[i]);
                    if (slot == NoGpuHits) { slot = AllocateGpuHits(layout: layout, lows: lows); }
                    slots[j] = slot;
                }
                var hits = trace.Trace(scene, origins, slots, GpuHitCapacity, reuse: m_gpuAnswer);
                m_gpuAnswer = hits;
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
