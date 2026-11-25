using System.Numerics;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // The trials a probe still unresolved traces in one round on the GPU, at first and at most: each round doubles them,
    // so a probe that resolves at its n-th trial costs fewer than 2n trial traces and about log2(n) rounds, where a round
    // a trial would cost a round trip each. The CPU reference traces one a round; the chosen trial is the same.
    private const int FirstPlacementTrials = 4;
    private const int MostPlacementTrials = 32;

    // One placement algorithm for the CPU reference and GPU runtime. A probe that its first trace leaves inside geometry
    // or near a surface tries the offsets in order (the pushed one, then Candidates), skipping those the host's filters
    // refuse, and keeps the first whose trace leaves it outside. Each round traces every unresolved probe's next trials
    // together, and the coordinator waits once per round, never once per ray, trial or worker thread. A probe's trials
    // after the one it keeps are traced and discarded, so the offset kept does not depend on how many a round traces.
    // On the GPU each placed probe's traces also land in the device hit cache: its first trace in one fresh slot, each
    // trial in a slot of its own. `fresh` receives the slot holding the hits the probe keeps, which also rides its
    // ProbeData (GpuSlot) for its relight to adopt (AdoptGpuHits); every other slot is freed here.
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
        // The next offset each unresolved probe tries: -1 the pushed one, then each candidate's index.
        var next = new int[count];
        // A probe's saved words are written in Place before the result loop reads them, so the scratch keeps stale words
        // only for probes that read none. Rented for the batch alone and returned once its results are read.
        using var savedLease = RentedArrays.Rent<uint>(minimumLength: (count * SdfMeshProbeDepth.HitWords), array: out var savedHits);
        var indices = new List<int>(count);
        var clearance = SdfMeshIrradianceProbe.Clearance;
        var trace = GpuTrace;
        var trialSlots = new uint[count];
        // A round's trials: each one's probe and offset, in each probe's order, and on the GPU the cache slot it traces into.
        var trialProbes = new List<int>();
        var trialOffsets = new List<Vector3>();
        var roundSlots = new List<uint>();
        var trialSky = new float[6];

        Array.Fill(fresh, NoGpuHits, 0, count);
        Array.Fill(trialSlots, NoGpuHits);
        Array.Fill(next, -1);
        for (var i = 0; i < count; i++) {
            var candidate = candidates[first + i];
            if (!candidate.Trace) { continue; }
            spacings[i] = layout.SpacingOf(candidate.Level);
            centres[i] = new Vector3(candidate.Cell.X + 0.5f, candidate.Cell.Y + 0.5f, candidate.Cell.Z + 0.5f) * spacings[i];
            indices.Add(i);
        }
        try {
            TraceFirst();
            Place();
        } catch {
            if (trace is not null) {
                for (var i = 0; i < count; i++) {
                    if (fresh[i] != NoGpuHits) { m_hitSlots.Free(start: fresh[i]); }
                    if (trialSlots[i] != NoGpuHits) { m_hitSlots.Free(start: trialSlots[i]); }
                }
                foreach (var slot in roundSlots) { m_hitSlots.Free(start: slot); }
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
            // A probe valid after a trial keeps its trial's slot; any other keeps its first trace's.
            if (trialSlots[i] != NoGpuHits) {
                m_hitSlots.Free(start: fresh[i]);
                fresh[i] = trialSlots[i];
            }
            result[i] = new ProbeData(Geometry: geometry, Hits: block, HitIndex: i, Inside: inside[i], Invalid: !valid[i], Offset: offsets[i],
                GpuSlot: (fresh[i] == NoGpuHits) ? 0u : fresh[i], Reach: FarthestHit(hits: block.Hits(i), limit: layout.Reach, out var frontHit, out var openSkyGroups), ReceivesSurface: valid[i] && frontHit, OpenSkyGroups: openSkyGroups);
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
            for (var trials = ((trace is null) ? 1 : FirstPlacementTrials); ; trials = Math.Min(val1: (2 * trials), val2: MostPlacementTrials)) {
                trialProbes.Clear();
                trialOffsets.Clear();
                // Each round's trials hold cache slots until it lands, so a round takes no more than the cache has free, and
                // at least one, whose slot AllocateGpuHits finds by trimming as any trace's.
                var room = (trace is null) ? int.MaxValue : (int)Math.Clamp(value: m_hitSlots.FreeCount, min: 1UL, max: (ulong)int.MaxValue);
                for (var i = 0; i < count; i++) {
                    if (!pending[i] || valid[i]) { continue; }
                    for (var taken = 0; (taken < trials) && (trialProbes.Count < room) && (next[i] < Candidates.Length); next[i]++) {
                        var trial = next[i] < 0 ? pushed[i] : Candidates[next[i]] * spacings[i];
                        var reach = 0.5f * spacings[i];
                        if (MathF.Abs(trial.X) > reach.X || MathF.Abs(trial.Y) > reach.Y || MathF.Abs(trial.Z) > reach.Z) { continue; }
                        var origin = centres[i] + trial;
                        if (scene.TryNearestSurface(origin, clearance, out _, out _, out _)) { continue; }
                        if (Blocked(origin, scene, MathF.Max(spacings[i].X, MathF.Max(spacings[i].Y, spacings[i].Z)))) { continue; }
                        trialProbes.Add(i);
                        trialOffsets.Add(trial);
                        taken++;
                    }
                }
                if (trialProbes.Count == 0) { return; }
                TraceTrials();
            }
        }

        void TraceFirst() {
            if (indices.Count == 0) { return; }
            var origins = indices.Select(i => new Vector4(centres[i], layout.Reach)).ToArray();
            if (trace is not null) {
                var slots = new uint[indices.Count];
                for (var j = 0; j < slots.Length; j++) {
                    slots[j] = fresh[indices[j]] = AllocateGpuHits(layout: layout, lows: lows);
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
                    TraceHits(centres[i], layout, scene, block.Hits(i), block.Sky(i), out inside[i]);
                });
            }
        }

        // Traces the round's trials and keeps, for each probe, its first trial the trace leaves outside geometry, with its
        // hits and, on the GPU, its cache slot; the round's other slots are freed.
        void TraceTrials() {
            if (trace is null) {
                // One trial a probe a round: it traces straight into the probe's block, as the first trace did.
                Parallel.For(0, trialProbes.Count, parallel, j => {
                    var i = trialProbes[j];
                    offsets[i] = trialOffsets[j];
                    TraceHits(centres[i] + offsets[i], layout, scene, block.Hits(i), block.Sky(i), out inside[i]);
                });
                foreach (var i in trialProbes) { valid[i] = inside[i] <= SdfMeshIrradianceProbe.InsideShare; }
                return;
            }
            var origins = new Vector4[trialProbes.Count];
            roundSlots.Clear();
            for (var j = 0; j < origins.Length; j++) {
                origins[j] = new Vector4(centres[trialProbes[j]] + trialOffsets[j], layout.Reach);
                roundSlots.Add(AllocateGpuHits(layout: layout, lows: lows));
            }
            var hits = trace.Trace(scene, origins, roundSlots.ToArray(), GpuHitCapacity, reuse: m_gpuAnswer);
            m_gpuAnswer = hits;
            for (var j = 0; j < origins.Length; j++) {
                var i = trialProbes[j];
                var answer = hits.AsSpan(j * SdfMeshProbeDepth.HitWords, SdfMeshProbeDepth.HitWords);
                if (!valid[i]) {
                    _ = SummarizeHits(answer, trialSky, out var trialInside);
                    if (trialInside <= SdfMeshIrradianceProbe.InsideShare) {
                        answer.CopyTo(block.Hits(i));
                        SummarizeHits(block.Hits(i), block.Sky(i), out inside[i]);
                        offsets[i] = trialOffsets[j];
                        valid[i] = true;
                        trialSlots[i] = roundSlots[j];
                        continue;
                    }
                }
                m_hitSlots.Free(start: roundSlots[j]);
            }
            roundSlots.Clear();
        }
    }
}
