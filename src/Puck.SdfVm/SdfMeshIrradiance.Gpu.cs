using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
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
        using var hold = m_blocks.Enter();
        Relight(origin, hits, m_layout, m_lighting!, Lows(), scene, m_lighting!.Feedback && m_layout.Bounce,
            radiance, local, sky, false, visibility, in hold);
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
    private readonly MarkedIndexSet m_gpuDirty = new();
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
    // The candidate each row of a selection answers for (ListGpu).
    private int[] m_gpuSelectRows = [];
    // The selection's header and change rows (SelectionRows), rewritten in place while their length holds.
    private Vector4[] m_gpuSelectionRows = [];
    private Vector4[]? m_gpuLightingRows;
    private int[] m_gpuUpdateAt = [];
    private uint[] m_gpuUpdateData = [];
    // Set by a device reattach; cleared once a completed job carried the volume's full snapshot into the new feedback.
    private bool m_republishOwed;

    // Under the gate, once the feedback holds the volume's words: owes every slot to the next publication, so each
    // measured level republishes whole into the new tables.
    private void RepublishAll() {
        m_republishOwed = false;
        m_geometryOwed = true;
        for (var level = 0; level < m_levels.Length; level++) {
            if (m_layout.Measures(level: level)) { OweSlots(level: level); }
        }
    }
    // Under the gate: owes every slot of a level to its next publication.
    private void OweSlots(int level) {
        var first = m_layout.OffsetOf(level: level);

        for (var slot = first; slot < first + m_layout.CountOf(level: level); slot++) { _ = m_levels[level].Changed.Add(index: slot); }
    }

    // Allocation tokens never repeat. Selection validates both words on the device, so a cached CPU description
    // cannot reuse hits after its slot was reclaimed. Only the coordinator allocates or frees, between jobs.
    // The device hit cache holds GpuHitCapacity slots: every window cell's owner, plus two fresh slots for each probe a
    // batch places (its first trace and the trial it keeps, PlaceBatch), so it follows the published tier. A placement
    // round's other trials borrow free slots only until the round lands, and never more than are free.
    private GpuRangeAllocator m_hitSlots = new(size: 1u, maxRanges: 1);
    private readonly Dictionary<(int Level, (int X, int Y, int Z) Cell), (uint Slot, ulong Token)> m_hitOwners = [];
    private ulong m_hitToken;
    internal uint GpuHitCapacity => ((uint)m_hitSlots.Size);
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
        var dropped = m_droppedHits;

        dropped.Clear();
        foreach (var (key, owner) in m_hitOwners) {
            var (level, cell) = key;
            var low = lows[level]; var dims = layout.DimsOf(level);
            if (cell.X < low.X || cell.Y < low.Y || cell.Z < low.Z || cell.X >= low.X + dims.X || cell.Y >= low.Y + dims.Y || cell.Z >= low.Z + dims.Z) { dropped.Add((owner.Slot, key)); }
        }
        // Freed from the highest slot down, as the range allocator merges them.
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dropped).Sort(comparer: default(SlotsDescending));
        foreach (var (slot, key) in dropped) { m_hitSlots.Free(slot); m_hitOwners.Remove(key); }
        dropped.Clear();
    }
    private readonly List<(uint Slot, (int Level, (int X, int Y, int Z) Cell) Key)> m_droppedHits = [];
    private readonly struct SlotsDescending : IComparer<(uint Slot, (int Level, (int X, int Y, int Z) Cell) Key)> {
        public int Compare((uint Slot, (int Level, (int X, int Y, int Z) Cell) Key) x, (uint Slot, (int Level, (int X, int Y, int Z) Cell) Key) y) => y.Slot.CompareTo(x.Slot);
    }

    // A fresh slot the trace writes a placed probe's hits into; the probe's relight adopts it (AdoptGpuHits).
    private uint AllocateGpuHits(Layout layout, (int X, int Y, int Z)[] lows) {
        if (m_hitSlots.FreeCount == 0) { TrimGpuHits(layout: layout, lows: lows, force: true); }
        return ((uint)m_hitSlots.Allocate(count: 1));
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
        using var hold = m_blocks.Enter();
        uint[] records;
        lock (m_gate) {
            if (!Stands(in snapshot)) { throw new OperationCanceledException(); }
            records = m_volume;
        }
        // The loop below writes every origin and every word of every row, so the reused arrays need no clearing.
        var inputWords = (layout.TotalCount * SdfMeshProbeRelight.SelectionInputWords);
        ArrayCapacity.EnsureScratch(ref m_gpuOrigins, layout.TotalCount);
        ArrayCapacity.EnsureScratch(ref m_gpuInput, inputWords);
        ArrayCapacity.EnsureScratch(ref m_gpuSelectRows, layout.TotalCount);
        var origins = m_gpuOrigins;
        var input = m_gpuInput;
        var selectRows = m_gpuSelectRows;
        var selections = 0;
        FitGpuHits(layout: layout);
        TrimGpuHits(layout: layout, lows: snapshot.Lows);
        var now = Time.GetTimestamp();
        candidates.Clear();
        var deferred = false;
        // Nothing reads cached hits for a skipped row. The selector validates the allocation token on the device
        // for every row that can consume those hits, including a rebase across changed geometry.
        for (var level = layout.Finest; level < m_levels.Length; level++) {
            var low = snapshot.Lows[level];
            var dims = layout.DimsOf(level);
            var spacing = layout.SpacingOf(level);
            for (var z = low.Z; z < low.Z + dims.Z; z++) {
                for (var y = low.Y; y < low.Y + dims.Y; y++) {
                    for (var x = low.X; x < low.X + dims.X; x++) {
                        var present = m_levels[level].Cache.TryGetValue((x, y, z), out var probe, hold: in hold);
                        var center = new Vector3(x + .5f, y + .5f, z + .5f) * spacing;
                        var slot = layout.OffsetOf(level) + SlotOf(x, y, z, dims);
                        var written = Holds(records.AsSpan(slot * SdfMeshIrradianceProbe.ProbeWords), (x, y, z));
                        var phase = ProbePhase(x, y, z);
                        var token = m_hitOwners.TryGetValue((level, (x, y, z)), out var owner) && owner.Token == probe.GpuToken ? probe.GpuToken : 0;
                        var change = ChangeOf(in probe, snapshot.Lighting, spacing, center, snapshot.Scene, m_inputChanges);
                        var age = !present || change == InputChange.None ? 0 : (float)Time.GetElapsedTime(StaleSince(probe.Lighting, in snapshot), now).TotalSeconds;
                        var bound = SdfMeshProbeRelight.AgeBoundOf(level, snapshot.AgeTarget);
                        if (present) { RecordAge(ages, level, age, change != InputChange.None); }
                        var required = !written || token == 0 || probe.Geometry != snapshot.Geometry;
                        if (!required && !OwesRelight(in probe, change, in snapshot, all, interleave, level, age, bound, phase, now, ref deferred)) { continue; }
                        // A probe holding no device hits traces, as the selector's first test answers (sdf-probe-select:
                        // no token is never present), so it takes no row of the selection.
                        if (token != 0) {
                            var i = selections++;
                            selectRows[i] = candidates.Count;
                            origins[i] = new(center, probe.Reach);
                            var row = input.AsSpan(i * SdfMeshProbeRelight.SelectionInputWords, SdfMeshProbeRelight.SelectionInputWords);
                            row[0] = (uint)probe.Geometry;
                            row[1] = (uint)level;
                            row[2] = BitConverter.SingleToUInt32Bits(probe.Offset.X);
                            row[3] = BitConverter.SingleToUInt32Bits(probe.Offset.Y);
                            row[4] = BitConverter.SingleToUInt32Bits(probe.Offset.Z);
                            row[5] = probe.GpuSlot;
                            row[6] = PackedIntPair.Low(value: token);
                            row[7] = PackedIntPair.High(value: token);
                        }
                        var priority = ProbePriority((x, y, z), low, center, snapshot.Camera, spacing);
                        var visible = InAnyView(center, in snapshot);
                        candidates.Add(new Candidate(Cell: (x, y, z), Band: priority.Band, Morton: priority.Morton, Level: level, LitAt: present ? probe.LitAt : 0, Trace: (token == 0),
                            Overdue: bound > 0 && age >= bound, Stale: present && change != InputChange.None, Urgency: AgePriority(age, bound, visible, probe.Moved), Visible: visible,
                            Seen: visible && present && change == InputChange.Due && probe.Lighting < snapshot.Lighting.Jump));
                    }
                }
            }
        }
        if (selections == 0) {
            OrderCandidates(candidates, all);
            return deferred;
        }
        var changes = (m_gpuSelectionRows = SelectionRows(changes: snapshot.Changes, geometry: snapshot.Geometry, layout: layout, rows: m_gpuSelectionRows));
        var selected = GpuTrace!.Select(snapshot.Scene, origins.AsMemory(start: 0, length: selections), input.AsMemory(start: 0, length: selections * SdfMeshProbeRelight.SelectionInputWords), changes, GpuHitCapacity, reuse: m_gpuAnswer);
        m_gpuAnswer = selected;
        for (var i = 0; i < selections; i++) { candidates[selectRows[i]] = candidates[selectRows[i]] with { Trace = selected[i] != 0 }; }
        OrderCandidates(candidates, all);
        return deferred;
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
            // A level the volume does not measure reads as unpublished, as the views read it (PublishedOf).
            rows[4 + 2 * level] = new(snapshot.Layout.Measures(level) ? snapshot.Layout.SpacingOf(level) : Vector3.Zero, 0);
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

    // The predicted rays one relight job carries at most before the relight's timestamps have measured its rate
    // (RelightRays), and on a queue without timestamps. A relight shades every hit texel of each probe toward every
    // shadowing light that reaches it (sdfProbeShade): a probe that reuses its local cube traces toward the directional
    // lights alone, a fresh one toward every point light in range as well, so one batch's cost spans two orders of
    // magnitude for one probe count. A whole 384-probe batch of fresh probes beside the debug world's stage, 78 shadowing
    // point lights in range of each (about 4.4 million rays), ran past the device's timeout as one dispatch.
    internal const long RelightRaysPerJob = 1L << 17;
    // The predicted rays one relight job carries at most once the rate is measured, a quarter of that dispatch: the
    // measured rate cuts a job to the live frame allowance below it, and it bounds a job whose rays cost far
    // more than the rate's recent jobs.
    internal const long RelightRaysCeiling = 1L << 20;
    private long[] m_gpuRelightRays = [];

    // Relights a batch's probes on the GPU and commits each probe's published record into the feedback volume: the rows
    // for the whole batch are composed once and handed to the probe service in one call, which cuts them into jobs whose
    // predicted rays fit the live frame allowance at the relight's measured rate
    // (SdfMeshProbeGpuWork.Relight), keeps several in flight, and commits the whole batch after its last relight, so
    // every probe of the batch reads the bounce the volume held before the batch, as the CPU reference's batch does.
    // False when the snapshot no longer stands before the batch is handed over.
    private bool RelightGpu(List<Candidate> candidates, int first, int count, ProbeData[] probes, LightBlock lights,
        Lighting lighting, in Snapshot snapshot, bool feedback, in ReaderEpochs<Reclaimable>.Hold hold) {
        var rows = (m_gpuLightingRows = LightingRows(lighting, in snapshot, feedback, rows: m_gpuLightingRows));
        var inputWords = (count * SdfMeshProbeRelight.InputWords);
        ArrayCapacity.EnsureScratch(ref m_gpuOrigins, count);
        ArrayCapacity.EnsureScratch(ref m_gpuInput, inputWords);
        ArrayCapacity.EnsureScratch(ref m_gpuRelightRays, count);
        ArrayCapacity.EnsureScratch(ref m_gpuDestinations, count);
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
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous, hold: in hold) &&
                ReusesLocal(changes: snapshot.Changes, geometry: snapshot.Geometry, lighting: lighting, origin: new Vector3(origins[i].X, origins[i].Y, origins[i].Z), previous: in previous, trace: false)) {
                row[0] |= 2;
                reuses = true;
                if (previous.Geometry != snapshot.Geometry) { Counters.Count(kind: LocalKept); }
                MemoryMarshal.Cast<Vector3, uint>(previous.Local).CopyTo(row[(SdfMeshProbeRelight.PreviousWord + 18)..]);
                previous.Lights.CopyTo(row.Slice(SdfMeshProbeRelight.PreviousWord + 36, SdfMeshIrradianceProbe.LightWords));
            }
            if (!probe.Invalid) { m_gpuRelightRays[i] = RelightRays(lighting: lighting, origin: new Vector3(origins[i].X, origins[i].Y, origins[i].Z), reuses: reuses); }
        }
        SdfMeshProbeGpuWork.Deltas updates;
        int volumeWords;
        uint[]? snapshotOf = null;
        // The volume's full snapshot when the feedback must be rewritten whole, rented for this job alone: Relight answers
        // or cancels the job before it returns or throws, so nothing reads it after, and the shared cache, which pressure
        // trims, keeps it for the next rewrite rather than every irradiance holding a second volume for good.
        uint[]? volumeSnapshot = null;
        lock (m_gate) {
            if (!Stands(in snapshot)) { m_gpuVolume = null; return false; }
            for (var i = 0; i < count; i++) {
                var candidate = candidates[first + i];
                m_gpuDestinations[i] = probes[i].Hits is null ? -1 : VolumeSlot(candidate.Level, candidate.Cell);
            }
            volumeWords = m_volume.Length;
            if (!ReferenceEquals(m_gpuVolume, m_volume)) {
                ArrayCapacity.EnsureScratch(ref m_gpuUpdateAt, 1);
                volumeSnapshot = RentedArrays.Rent<uint>(minimumLength: m_volume.Length);
                m_gpuUpdateAt[0] = 0;
                m_volume.CopyTo(array: volumeSnapshot, index: 0);
                updates = new(Words: m_gpuUpdateAt, Data: volumeSnapshot.AsMemory(start: 0, length: m_volume.Length), Count: 1, Length: m_volume.Length);
                m_gpuVolume = m_volume;
                snapshotOf = m_volume;
            } else {
                var dirty = m_gpuDirty.Count;
                ArrayCapacity.EnsureScratch(ref m_gpuUpdateAt, dirty);
                ArrayCapacity.EnsureScratch(ref m_gpuUpdateData, (dirty * SdfMeshIrradianceProbe.ProbeWords));
                var i = 0;
                foreach (var slot in m_gpuDirty.Members) {
                    var word = slot * SdfMeshIrradianceProbe.ProbeWords;
                    m_gpuUpdateAt[i] = word;
                    m_volume.AsSpan(word, SdfMeshIrradianceProbe.ProbeWords).CopyTo(m_gpuUpdateData.AsSpan(i * SdfMeshIrradianceProbe.ProbeWords, SdfMeshIrradianceProbe.ProbeWords));
                    i++;
                }
                updates = new(Words: m_gpuUpdateAt, Data: m_gpuUpdateData, Count: dirty, Length: SdfMeshIrradianceProbe.ProbeWords);
            }
            m_gpuDirty.Clear();
        }
        uint[] words;
        try {
            words = GpuTrace!.Relight(snapshot.Scene, origins.AsMemory(start: 0, length: count), input.AsMemory(start: 0, length: inputWords), rows, volumeWords, updates,
                GpuHitCapacity, rays: m_gpuRelightRays.AsMemory(start: 0, length: count), destinations: m_gpuDestinations.AsMemory(start: 0, length: count),
                geometry: snapshot.Geometry, window: snapshot.WindowVersion, reuse: m_gpuAnswer);
            m_gpuAnswer = words;
        }
        catch (OperationCanceledException) {
            // A cancelled job may or may not have consumed the delta. Re-send a full snapshot on the next accepted job.
            lock (m_gate) { m_gpuVolume = null; }
            throw;
        }
        finally {
            if (volumeSnapshot is not null) { RentedArrays.Return(array: volumeSnapshot); }
        }
        if (snapshotOf is not null) {
            lock (m_gate) {
                if (m_republishOwed && ReferenceEquals(m_volume, snapshotOf)) { RepublishAll(); }
            }
        }
        for (var i = 0; i < count; i++) {
            var probe = probes[i];
            if (probe.Hits is null || probe.Invalid) { continue; }
            var row = words.AsSpan(i * SdfMeshProbeRelight.ResultWords, SdfMeshProbeRelight.OutputWords);
            MemoryMarshal.Cast<uint, Vector3>(row[..18]).CopyTo(lights.Radiance(i));
            MemoryMarshal.Cast<uint, Vector3>(row.Slice(18, 18)).CopyTo(lights.Local(i));
            row.Slice(36, SdfMeshIrradianceProbe.LightWords).CopyTo(lights.Lights(i));
            MemoryMarshal.Cast<uint, Vector3>(row.Slice(SdfMeshProbeRelight.SkyOutputWord, 18)).CopyTo(lights.SkyLight(i));
            var candidate = candidates[first + i];
            probes[i] = probe with { Moved = Moves(candidate.Trace, m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous, hold: in hold) ? previous : null, lights.Radiance(i), lights.SkyLight(i)) };
        }
        return true;
    }

    // The rays sdfProbeShade traces from one valid probe, for each of its hit texels at most, and the light loop after
    // them: one toward each shadowing directional light from each, and unless the probe reuses its local cube, one toward
    // each point light whose range reaches the probe's origin from each hit (a hit's own distance is not known before the
    // trace answers) and one toward every point light from the centre, for its visibility bit.
    private static long RelightRays(Lighting lighting, Vector3 origin, bool reuses) {
        var lights = 0L;
        foreach (var light in lighting.Lights) { if (light.CastsShadow) { lights++; } }
        var rays = (lights * (SdfMeshProbeDepth.HitCount + 1));
        if (!reuses) {
            foreach (var point in lighting.Points) {
                rays += ((Vector3.DistanceSquared(point.Position, origin) < (point.Range * point.Range)) ? (SdfMeshProbeDepth.HitCount + 1) : 1);
            }
        }
        return rays;
    }
}
