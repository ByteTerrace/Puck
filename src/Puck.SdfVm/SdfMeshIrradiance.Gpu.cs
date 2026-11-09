using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Maths;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // An explicit world.probe read checks the runtime's cached output against one further CPU-reference relight.
    // The local term, the sky light and visibility must agree to float precision; the varying term also includes the
    // convergence residual (ConvergedShare). This diagnostic does no work during presentation.
    private string DescribeRelightReference(ProbeData probe, Vector3 origin, SdfMeshIrradianceScene scene) {
        Span<Vector3> radiance = stackalloc Vector3[6];
        Span<Vector3> local = stackalloc Vector3[6];
        Span<Vector3> sky = stackalloc Vector3[6];
        Span<uint> visibility = stackalloc uint[SdfMeshIrradianceProbe.LightWords];
        Span<uint> hits = stackalloc uint[SdfMeshProbeDepth.HitWords];
        SdfMeshProbeDepth.TraceHits(scene, origin, m_layout.Reach, hits, out _);
        Relight(origin, hits, m_layout, m_lighting!, Lows(), scene, m_lighting!.Feedback && m_layout.Bounce,
            radiance, local, sky, false, visibility);
        var localError = 0f;
        var relightError = 0f;
        var skyError = 0f;
        var bits = 0;
        for (var i = 0; i < 6; i++) {
            for (var c = 0; c < 3; c++) {
                localError = MathF.Max(localError, MathF.Abs(local[i][c] - probe.Local[i][c]) / MathF.Max(1, MathF.Abs(local[i][c])));
                relightError = MathF.Max(relightError, MathF.Abs(radiance[i][c] - probe.Radiance[i][c]) / MathF.Max(1, MathF.Abs(radiance[i][c])));
                skyError = MathF.Max(skyError, MathF.Abs(sky[i][c] - probe.SkyLight[i][c]) / MathF.Max(1, MathF.Abs(sky[i][c])));
            }
        }
        for (var i = 0; i < visibility.Length; i++) { bits += BitOperations.PopCount(visibility[i] ^ probe.Lights[i]); }
        return FormattableString.Invariant($" reference-local-error={localError:G5} reference-relight-error={relightError:G5} reference-sky-error={skyError:G5} reference-visibility-errors={bits}");
    }
    // SlotWords is the sole mutation door. A job consumes these deltas under the volume gate, then owns them until
    // completion. Its GPU feedback is exactly the preceding completed batches, independent of display uploads.
    private readonly HashSet<int> m_gpuDirty = [];
    private uint[]? m_gpuVolume;
    // The worker's GPU scratch, grown and kept across its batches. Only the worker task's body touches it, and a worker
    // starts only once the last one completed, so it takes no gate; a job reads the origins, input and updates before
    // Submit returns, and the readback arrays are read before the next job is submitted.
    private Vector4[] m_gpuOrigins = [];
    private uint[] m_gpuInput = [];
    private uint[] m_gpuAnswer = [];
    private uint[] m_gpuHits = [];
    private uint[] m_gpuSavedHits = [];
    private uint[] m_gpuFresh = [];
    private int[] m_gpuDestinations = [];
    private Vector4[]? m_gpuLightingRows;
    private int[] m_gpuUpdateAt = [];
    private uint[] m_gpuUpdateData = [];
    private bool m_gpuDraining;
    // Set by a device reattach; cleared once a completed job carried the volume's full snapshot into the new feedback.
    private bool m_republishOwed;

    // Under the gate, once the feedback holds the volume's words: owes every slot to the next publication, so each
    // measured level republishes whole into the new tables.
    private void RepublishAll() {
        m_republishOwed = false;
        for (var level = 0; level < m_levels.Length; level++) {
            var first = m_layout.OffsetOf(level: level);
            for (var slot = first; slot < first + m_layout.CountOf(level: level); slot++) {
                if (m_dirtySlots[slot]) { continue; }
                m_dirtySlots[slot] = true;
                m_levels[level].Changed.Add(item: slot);
            }
        }
    }

    // Allocation tokens never repeat. Selection validates both words on the device, so a cached CPU description
    // cannot reuse hits after its slot was reclaimed. Only the coordinator allocates or frees, between jobs.
    // The device hit cache holds GpuHitCapacity slots: every window cell's owner, plus two fresh slots for each probe a
    // batch places (its first trace and its trial traces, PlaceBatch), so it follows the published tier.
    private GpuRangeAllocator m_hitSlots = new(size: 1u, maxRanges: 1);
    private readonly Dictionary<(int Level, (int X, int Y, int Z) Cell), (uint Slot, ulong Token)> m_hitOwners = [];
    private ulong m_hitToken;
    internal uint GpuHitCapacity => m_hitSlots.Size;
    private static uint HitCapacityOf(Layout layout) => checked((uint)(layout.TotalCount + (2 * DrainBatchProbes)));

    // A new tier's probes are all new, so its cache starts empty at its own size.
    private void FitGpuHits(Layout layout) {
        var capacity = HitCapacityOf(layout: layout);
        if (m_hitSlots.Size == capacity) { return; }
        m_hitOwners.Clear();
        m_hitSlots = new GpuRangeAllocator(size: capacity, maxRanges: (int)capacity);
    }

    // Releases the owners outside the windows once more owners are held than the windows have cells, or whenever
    // `force`; selection has no use for their hits, and a later visit retraces them.
    private void TrimGpuHits(Layout layout, (int X, int Y, int Z)[] lows, bool force = false) {
        if (!force && m_hitOwners.Count <= layout.TotalCount) { return; }
        var dropped = m_hitOwners.Where(entry => {
            var (level, cell) = entry.Key;
            var low = lows[level]; var dims = layout.DimsOf(level);
            return cell.X < low.X || cell.Y < low.Y || cell.Z < low.Z || cell.X >= low.X + dims.X || cell.Y >= low.Y + dims.Y || cell.Z >= low.Z + dims.Z;
        }).OrderByDescending(entry => entry.Value.Slot).ToArray();
        foreach (var entry in dropped) { m_hitSlots.Free(entry.Value.Slot); m_hitOwners.Remove(entry.Key); }
    }

    // A fresh slot the trace writes a placed probe's hits into; the probe's relight adopts it (AdoptGpuHits).
    private uint AllocateGpuHits(Layout layout, (int X, int Y, int Z)[] lows) {
        if (m_hitSlots.FreeCount == 0) { TrimGpuHits(layout: layout, lows: lows, force: true); }
        return m_hitSlots.Allocate(count: 1);
    }

    // Makes a placed probe's fresh slot its cell's owner under a new token, freeing the slot the cell owned before.
    private (uint Slot, ulong Token) AdoptGpuHits(Candidate candidate, uint slot) {
        var key = (candidate.Level, candidate.Cell);
        if (m_hitOwners.TryGetValue(key, out var previous) && previous.Slot != slot) { m_hitSlots.Free(start: previous.Slot); }
        var owner = (slot, checked(++m_hitToken));
        m_hitOwners[key] = owner;
        return owner;
    }

    // Frees each fresh slot a batch placed whose relight never adopted it (a dropped or cancelled batch).
    private void FreeUnadoptedGpuHits(List<Candidate> candidates, int first, ReadOnlySpan<uint> fresh) {
        for (var i = 0; i < fresh.Length; i++) {
            if (fresh[i] == NoGpuHits) { continue; }
            var candidate = candidates[first + i];
            if (m_hitOwners.TryGetValue((candidate.Level, candidate.Cell), out var owner) && owner.Slot == fresh[i]) { continue; }
            m_hitSlots.Free(start: fresh[i]);
        }
    }
    private const uint NoGpuHits = uint.MaxValue;

    private bool ListGpu(in Snapshot snapshot, bool all, List<Candidate> candidates, List<float>? ages, bool interleave) {
        var layout = snapshot.Layout;
        uint[] records;
        lock (m_gate) {
            if (!Stands(in snapshot)) { throw new OperationCanceledException(); }
            records = m_volume;
        }
        // The loop below writes every origin and every word of every row, so the reused arrays need no clearing.
        var inputWords = (layout.TotalCount * SdfMeshProbeRelight.SelectionInputWords);
        GrowScratch(array: ref m_gpuOrigins, length: layout.TotalCount);
        GrowScratch(array: ref m_gpuInput, length: inputWords);
        var origins = m_gpuOrigins;
        var input = m_gpuInput;
        FitGpuHits(layout: layout);
        TrimGpuHits(layout: layout, lows: snapshot.Lows);
        var now = Stopwatch.GetTimestamp();
        candidates.Clear();
        // This is a snapshot pack, with no eligibility decision. The device evaluates changed-box/ray reach,
        // missing traces, rebase, sweep and lighting versions; the host keeps the existing stable priority order.
        for (var level = 0; level < m_levels.Length; level++) {
            var low = snapshot.Lows[level];
            var dims = layout.DimsOf(level);
            var spacing = layout.SpacingOf(level);
            for (var z = low.Z; z < low.Z + dims.Z; z++) {
                for (var y = low.Y; y < low.Y + dims.Y; y++) {
                    for (var x = low.X; x < low.X + dims.X; x++) {
                        var i = candidates.Count;
                        var present = m_levels[level].Cache.TryGetValue((x, y, z), out var probe);
                        var center = new Vector3(x + .5f, y + .5f, z + .5f) * spacing;
                        origins[i] = new(center, 0);
                        var row = input.AsSpan(i * SdfMeshProbeRelight.SelectionInputWords, SdfMeshProbeRelight.SelectionInputWords);
                        var slot = layout.OffsetOf(level) + SlotOf(x, y, z, dims);
                        var written = (records[slot * SdfMeshIrradianceProbe.ProbeWords] & ~SdfMeshIrradianceProbe.InvalidFlag) == (KeyOf(x, y, z) | SdfMeshIrradianceProbe.WrittenFlag);
                        row[0] = (present ? 1u : 0) | (written ? 0 : 2u) | ((uint)ProbePhase(x, y, z) << 2);
                        row[1] = (uint)probe.Geometry; row[2] = (uint)probe.Lighting; row[3] = (uint)probe.Sweep; row[4] = (uint)level;
                        row[5] = BitConverter.SingleToUInt32Bits(probe.Offset.X);
                        row[6] = BitConverter.SingleToUInt32Bits(probe.Offset.Y);
                        row[7] = BitConverter.SingleToUInt32Bits(probe.Offset.Z);
                        row[8] = probe.GpuSlot;
                        var token = m_hitOwners.TryGetValue((level, (x, y, z)), out var owner) && owner.Token == probe.GpuToken ? probe.GpuToken : 0;
                        row[9] = PackedIntPair.Low(value: token);
                        row[10] = PackedIntPair.High(value: token);
                        var age = !present || probe.Lighting == snapshot.Lighting.Version ? 0 : (float)Stopwatch.GetElapsedTime(StaleSince(probe.Lighting, in snapshot), now).TotalSeconds;
                        row[11] = BitConverter.SingleToUInt32Bits(age);
                        var priority = ProbePriority((x, y, z), low, center, snapshot.Camera, spacing);
                        var bound = SdfMeshProbeRelight.AgeBoundOf(level, snapshot.AgeTarget);
                        candidates.Add(new Candidate(Cell: (x, y, z), Band: priority.Band, Morton: priority.Morton, Level: level, LitAt: present ? probe.LitAt : 0, Trace: false,
                            Overdue: bound > 0 && age >= bound, Urgency: AgePriority(age, bound, InAnyView(center, in snapshot), probe.Moved)));
                        if (present) { RecordAge(ages, level, age); }
                    }
                }
            }
        }
        var changes = new List<Vector4> {
            new(all ? 1 : 0, BitConverter.Int32BitsToSingle(snapshot.Geometry), BitConverter.Int32BitsToSingle(snapshot.Lighting.Version), BitConverter.Int32BitsToSingle(snapshot.Sweep)),
            new(snapshot.Changes.Length, 0, BitConverter.Int32BitsToSingle(snapshot.Changes.Length == 0 ? 0 : snapshot.Changes[0].Geometry), layout.Reach),
            new(layout.SpacingOf(0), 0), new(layout.SpacingOf(1), 0), new(layout.SpacingOf(2), 0),
        };
        for (var level = 0; level < m_levels.Length; level++) {
            changes.Add(new(interleave ? 1 : 0, SdfMeshProbeRelight.InterleaveOf(level), SdfMeshProbeRelight.AgeBoundOf(level, snapshot.AgeTarget), BitConverter.Int32BitsToSingle(m_selectionSweep)));
        }
        foreach (var (geometry, boxes) in snapshot.Changes) {
            if (boxes is null) { changes.Add(new(0, 0, 0, BitConverter.Int32BitsToSingle(geometry))); changes.Add(new(0, 0, 0, 1)); }
            else { foreach (var box in boxes) { changes.Add(new(box.Low, BitConverter.Int32BitsToSingle(geometry))); changes.Add(new(box.High, 0)); } }
        }
        changes[1] = changes[1] with { Y = (changes.Count - SdfMeshProbeRelight.SelectionHeaderRows) / 2 };
        var selected = GpuTrace!.Select(snapshot.Scene, origins.AsMemory(start: 0, length: layout.TotalCount), input.AsMemory(start: 0, length: inputWords), [.. changes], GpuHitCapacity, reuse: m_gpuAnswer);
        m_gpuAnswer = selected;
        var take = 0;
        var stale = false;
        for (var i = 0; i < layout.TotalCount; i++) {
            stale |= (selected[i] & 4) != 0;
            if ((selected[i] & 2) != 0) { candidates[take++] = candidates[i] with { Trace = (selected[i] & 1) != 0 }; }
        }
        candidates.RemoveRange(take, candidates.Count - take);
        OrderCandidates(candidates, all);
        return stale;
    }

    // Grows an array to hold at least `length` entries, never shrinking it. The entries a caller does not write keep an
    // earlier use's values.
    private static void GrowScratch<T>(ref T[] array, int length) {
        if (array.Length < length) { array = new T[length]; }
    }

    // The relight's lighting rows, exactly sized: the array `rows` holds is rewritten and returned when it already has
    // the length, so batches under one lighting share one array. A null `rows` always allocates; a caller that keeps the
    // returned array owns it and passes it back.
    private static Vector4[] LightingRows(Lighting lighting, in Snapshot snapshot, bool feedback, Vector4[]? rows = null) {
        var length = SdfMeshProbeRelight.HeaderRows + (lighting.Materials.Length + 1) * SdfMeshProbeRelight.MaterialRows +
            (lighting.Lights.Length + lighting.Points.Length) * SdfMeshProbeRelight.LightRows;

        if (rows is null || (rows.Length != length)) { rows = new Vector4[length]; }
        else { Array.Clear(rows); }
        rows[0] = new(lighting.Materials.Length + 1, lighting.Lights.Length, lighting.Points.Length, snapshot.Layout.Reach);
        rows[1] = new(lighting.Direct ? 1 : 0, lighting.Emission ? 1 : 0, feedback ? 1 : 0, lighting.Sky ? 1 : 0);
        rows[2] = new(lighting.Gains.Lights, lighting.Gains.Emission, lighting.Gains.Feedback, lighting.Gains.Sky);
        for (var level = 0; level < SdfMeshIrradianceProbe.Levels; level++) {
            var low = snapshot.Lows[level];
            var dims = snapshot.Layout.DimsOf(level);
            rows[3 + 2 * level] = new(low.X, low.Y, low.Z, dims.X | (dims.Y << 10) | (dims.Z << 20));
            rows[4 + 2 * level] = new(snapshot.Layout.SpacingOf(level), 0);
        }
        rows[9] = new(AxisTotals[0], AxisTotals[1], AxisTotals[2], AxisTotals[3]);
        rows[10] = new(AxisTotals[4], AxisTotals[5], 0, 0);
        for (var i = 0; i < lighting.SkyTexels.Length; i++) { rows[SdfMeshProbeRelight.SkyRow + i] = new(lighting.SkyTexels[i], 0); }
        var cursor = SdfMeshProbeRelight.HeaderRows;
        for (var i = 0; i <= lighting.Materials.Length; i++) {
            var material = lighting.MaterialAt(i);
            var bleed = material.Bleed ?? Vector3.One;
            rows[cursor++] = new(material.Albedo * (1 - material.Metal) * bleed, material.Wrap);
            rows[cursor++] = new(material.EmissiveLane is null ? material.Albedo * material.Emissive * bleed : Vector3.Zero, 0);
        }
        for (var i = 0; i < lighting.Lights.Length; i++) {
            var light = lighting.Lights[i];
            rows[cursor++] = new(light.Direction, 0);
            rows[cursor++] = new(light.Color, light.Weight);
            rows[cursor++] = default;
            rows[cursor++] = new(light.Bounce, lighting.LightIndices[i], light.CastsShadow ? 1 : 0, 0);
        }
        foreach (var light in lighting.Points) {
            rows[cursor++] = new(light.Position, light.Range);
            rows[cursor++] = new(light.Color, light.Weight);
            rows[cursor++] = new(light.SpotAxis, light.SpotOffset);
            rows[cursor++] = new(light.Bounce, light.Index, 1, 1);
        }
        return rows;
    }

    // The predicted rays one relight job carries at most whatever its measured rate (RelightRays). A relight shades every
    // hit texel of each probe toward every shadowing light that reaches it (sdfProbeShade): a probe that reuses its local
    // cube traces toward the directional lights alone, a fresh one toward every point light in range as well, so one
    // batch's cost spans two orders of magnitude for one probe count. A whole 384-probe batch of fresh probes beside the
    // debug world's stage, 78 shadowing point lights in range of each, ran past the device's timeout as one dispatch.
    internal const long RelightRaysPerJob = 1L << 17;
    private long[] m_gpuRelightRays = [];

    // Relights a batch's probes on the GPU and commits each probe's published record into the feedback volume: the rows
    // for the whole batch are composed once, then submitted in consecutive jobs whose predicted rays fit background
    // work's share of a frame at the relight's measured rate (SdfMeshProbeGpuWork.RelightRaysWithinShare, at most
    // RelightRaysPerJob; a probe whose own prediction exceeds it goes alone), each committed before the next is
    // submitted, so no relight job holds the frame behind it longer than that share however many lights reach the batch.
    // False when the snapshot no longer stands at a commit; the probes committed before it stay committed.
    private bool RelightGpu(List<Candidate> candidates, int first, int count, ProbeData[] probes, LightBlock lights,
        Lighting lighting, in Snapshot snapshot, bool feedback) {
        var rows = (m_gpuLightingRows = LightingRows(lighting, in snapshot, feedback, rows: m_gpuLightingRows));
        var inputWords = (count * SdfMeshProbeRelight.InputWords);
        GrowScratch(array: ref m_gpuOrigins, length: count);
        GrowScratch(array: ref m_gpuInput, length: inputWords);
        GrowScratch(array: ref m_gpuRelightRays, length: count);
        // A probe without hits leaves its row and origin untouched, and Compose and the row writes below skip words, so
        // the batch starts from zeros as a new array would.
        Array.Clear(array: m_gpuOrigins, index: 0, length: count);
        Array.Clear(array: m_gpuInput, index: 0, length: inputWords);
        var input = m_gpuInput;
        var origins = m_gpuOrigins;
        for (var i = 0; i < count; i++) {
            m_gpuRelightRays[i] = 0;
            var candidate = candidates[first + i];
            var probe = probes[i];
            if (probe.Hits is null) { continue; }
            var cell = candidate.Cell;
            origins[i] = new(new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + .5f) * snapshot.Layout.SpacingOf(candidate.Level) + probe.Offset, snapshot.Layout.Reach);
            var row = input.AsSpan(i * SdfMeshProbeRelight.InputWords, SdfMeshProbeRelight.InputWords);
            row[0] = probe.Invalid ? 0u : 1u;
            if (candidate.Trace) {
                // The trace already wrote this probe's hits into its fresh device slot (PlaceBatch); the relight's cache
                // pass stamps the slot with the new token before anything reads it.
                var owner = AdoptGpuHits(candidate, probe.GpuSlot);
                probe = probe with { GpuSlot = owner.Slot, GpuToken = owner.Token };
                probes[i] = probe;
                row[0] |= 4;
            }
            row[1] = PackedIntPair.Low(value: probe.GpuToken);
            row[2] = PackedIntPair.High(value: probe.GpuToken);
            row[3] = probe.GpuSlot;
            Compose(in probe, candidate.Cell, row[SdfMeshProbeRelight.RecordInputWord..]);
            var reuses = false;
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) && previous.Shading is not null &&
                previous.Geometry == snapshot.Geometry && previous.LocalVersion == lighting.LocalVersion) {
                row[0] |= 2;
                reuses = true;
                MemoryMarshal.Cast<Vector3, uint>(previous.Local).CopyTo(row[(SdfMeshProbeRelight.PreviousWord + 18)..]);
                previous.Lights.CopyTo(row.Slice(SdfMeshProbeRelight.PreviousWord + 36, SdfMeshIrradianceProbe.LightWords));
            }
            if (!probe.Invalid) { m_gpuRelightRays[i] = RelightRays(lighting: lighting, origin: new Vector3(origins[i].X, origins[i].Y, origins[i].Z), reuses: reuses); }
        }
        for (var start = 0; (start < count);) {
            var end = start + 1;
            var bound = GpuTrace!.RelightRaysWithinShare;
            for (var taken = m_gpuRelightRays[start]; (end < count) && ((taken + m_gpuRelightRays[end]) <= bound); end++) { taken += m_gpuRelightRays[end]; }
            var rays = 0L;
            for (var i = start; (i < end); i++) { rays += m_gpuRelightRays[i]; }
            using var output = RelightGpuJob(candidates: candidates, first: first, start: start, count: (end - start), probes: probes, lights: lights, rows: rows, rays: rays, snapshot: in snapshot);
            if (!CommitGpu(candidates: candidates, first: first, start: start, count: (end - start), probes: probes, output: output, snapshot: in snapshot)) { return false; }
            start = end;
        }
        return true;
    }

    // The rays sdfProbeShade traces from one valid probe, for each of its hit texels at most, and the light loop after
    // them: one toward each shadowing directional light, and unless the probe reuses its local cube, one toward each point
    // light whose range reaches the probe's origin (a hit's own distance is not known before the trace answers).
    private static long RelightRays(Lighting lighting, Vector3 origin, bool reuses) {
        var lights = 0L;
        foreach (var light in lighting.Lights) { if (light.CastsShadow) { lights++; } }
        if (!reuses) {
            foreach (var point in lighting.Points) { if (Vector3.DistanceSquared(point.Position, origin) < (point.Range * point.Range)) { lights++; } }
        }
        return (lights * (SdfMeshProbeDepth.HitCount + 1));
    }

    // Submits the probes [start, start + count) of a composed batch as one relight job and copies their answers into the
    // batch's light block.
    private SdfMeshProbeGpuWork.Result RelightGpuJob(List<Candidate> candidates, int first, int start, int count, ProbeData[] probes, LightBlock lights,
        Vector4[] rows, long rays, in Snapshot snapshot) {
        SdfMeshProbeGpuWork.Deltas updates;
        int volumeWords;
        uint[]? snapshotOf = null;
        lock (m_gate) {
            volumeWords = m_volume.Length;
            if (!ReferenceEquals(m_gpuVolume, m_volume)) {
                updates = new(Words: new int[] { 0 }, Data: (uint[])m_volume.Clone(), Count: 1, Length: m_volume.Length);
                m_gpuVolume = m_volume;
                snapshotOf = m_volume;
            } else {
                var dirty = m_gpuDirty.Count;
                GrowScratch(array: ref m_gpuUpdateAt, length: dirty);
                GrowScratch(array: ref m_gpuUpdateData, length: (dirty * SdfMeshIrradianceProbe.ProbeWords));
                var i = 0;
                foreach (var slot in m_gpuDirty) {
                    var word = slot * SdfMeshIrradianceProbe.ProbeWords;
                    m_gpuUpdateAt[i] = word;
                    m_volume.AsSpan(word, SdfMeshIrradianceProbe.ProbeWords).CopyTo(m_gpuUpdateData.AsSpan(i * SdfMeshIrradianceProbe.ProbeWords, SdfMeshIrradianceProbe.ProbeWords));
                    i++;
                }
                updates = new(Words: m_gpuUpdateAt, Data: m_gpuUpdateData, Count: dirty, Length: SdfMeshIrradianceProbe.ProbeWords);
            }
            m_gpuDirty.Clear();
        }
        SdfMeshProbeGpuWork.Result output;
        try {
            output = GpuTrace!.Relight(snapshot.Scene, m_gpuOrigins.AsMemory(start: start, length: count),
                m_gpuInput.AsMemory(start: (start * SdfMeshProbeRelight.InputWords), length: (count * SdfMeshProbeRelight.InputWords)), rows, volumeWords, in updates, GpuHitCapacity, rays: rays, reuse: m_gpuAnswer);
            m_gpuAnswer = output.Words;
        }
        catch (OperationCanceledException) {
            // Admission cancellation consumed no delta. Re-send a full snapshot on the next accepted job.
            lock (m_gate) { m_gpuVolume = null; }
            throw;
        }
        if (snapshotOf is not null) {
            lock (m_gate) {
                if (m_republishOwed && ReferenceEquals(m_volume, snapshotOf)) { RepublishAll(); }
            }
        }
        try {
            for (var j = 0; j < count; j++) {
                var i = start + j;
                var probe = probes[i];
                if (probe.Hits is null || probe.Invalid) { continue; }
                var row = output.Words.AsSpan(j * SdfMeshProbeRelight.ResultWords, SdfMeshProbeRelight.OutputWords);
                MemoryMarshal.Cast<uint, Vector3>(row[..18]).CopyTo(lights.Radiance(i));
                MemoryMarshal.Cast<uint, Vector3>(row.Slice(18, 18)).CopyTo(lights.Local(i));
                row.Slice(36, SdfMeshIrradianceProbe.LightWords).CopyTo(lights.Lights(i));
                MemoryMarshal.Cast<uint, Vector3>(row.Slice(SdfMeshProbeRelight.SkyOutputWord, 18)).CopyTo(lights.SkyLight(i));
                var candidate = candidates[first + i];
                probes[i] = probe with { Moved = candidate.Trace || !m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) ||
                    previous.Shading is null || !Settled(lights.Radiance(i), previous.Radiance) };
            }
        } catch { output.Dispose(); throw; }
        return output;
    }

    // Copies one relight job's published records into the feedback volume at their probes' slots. False, with the batch's
    // volume snapshot dropped, when the snapshot no longer stands.
    private bool CommitGpu(List<Candidate> candidates, int first, int start, int count, ProbeData[] probes, SdfMeshProbeGpuWork.Result output, in Snapshot snapshot) {
        GrowScratch(array: ref m_gpuDestinations, length: count);
        var destinations = m_gpuDestinations;
        lock (m_gate) {
            if (!Stands(in snapshot)) { m_gpuVolume = null; return false; }
            for (var j = 0; j < count; j++) {
                var candidate = candidates[first + start + j];
                destinations[j] = probes[start + j].Hits is null ? -1 : VolumeSlot(candidate.Level, candidate.Cell);
            }
        }
        try { GpuTrace!.Commit(snapshot.Scene, snapshot.Geometry, snapshot.WindowVersion, output, destinations.AsMemory(start: 0, length: count)); }
        catch (OperationCanceledException) { lock (m_gate) { m_gpuVolume = null; } throw; }
        return true;
    }
}
