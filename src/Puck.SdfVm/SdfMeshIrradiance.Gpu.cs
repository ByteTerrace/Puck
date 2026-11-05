using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // An explicit world.probe read checks the runtime's cached output against one further CPU-reference relight.
    // The local term and visibility must agree to float precision; the varying term also includes the convergence
    // residual (ConvergedShare). This diagnostic does no work during presentation.
    private string DescribeRelightReference(ProbeData probe, Vector3 origin, SdfMeshIrradianceScene scene) {
        Span<Vector3> radiance = stackalloc Vector3[6];
        Span<Vector3> local = stackalloc Vector3[6];
        Span<uint> visibility = stackalloc uint[SdfMeshIrradianceProbe.LightWords];
        Span<uint> hits = stackalloc uint[SdfMeshProbeDepth.HitWords];
        SdfMeshProbeDepth.TraceHits(scene, origin, m_layout.Reach, hits, out _);
        Relight(origin, hits, m_layout, m_lighting!, Lows(), scene, m_lighting!.Feedback && m_layout.Bounce,
            radiance, local, false, visibility);
        var localError = 0f;
        var relightError = 0f;
        var bits = 0;
        for (var i = 0; i < 6; i++) {
            for (var c = 0; c < 3; c++) {
                localError = MathF.Max(localError, MathF.Abs(local[i][c] - probe.Local[i][c]) / MathF.Max(1, MathF.Abs(local[i][c])));
                relightError = MathF.Max(relightError, MathF.Abs(radiance[i][c] - probe.Radiance[i][c]) / MathF.Max(1, MathF.Abs(radiance[i][c])));
            }
        }
        for (var i = 0; i < visibility.Length; i++) { bits += BitOperations.PopCount(visibility[i] ^ probe.Lights[i]); }
        return FormattableString.Invariant($" reference-local-error={localError:G5} reference-relight-error={relightError:G5} reference-visibility-errors={bits}");
    }
    // SlotWords is the sole mutation door. A job consumes these deltas under the volume gate, then owns them until
    // completion. Its GPU feedback is exactly the preceding completed batches, independent of display uploads.
    private readonly HashSet<int> m_gpuDirty = [];
    private uint[]? m_gpuVolume;
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
    private void FreeUnadoptedGpuHits(List<Candidate> candidates, int first, uint[] fresh) {
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
        var origins = new Vector4[layout.TotalCount];
        var input = new uint[layout.TotalCount * SdfMeshProbeRelight.SelectionInputWords];
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
                        row[9] = (uint)token;
                        row[10] = (uint)(token >> 32);
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
        var selected = GpuTrace!.Select(snapshot.Scene, origins, input, [.. changes], GpuHitCapacity);
        var take = 0;
        var stale = false;
        for (var i = 0; i < selected.Length; i++) {
            stale |= (selected[i] & 4) != 0;
            if ((selected[i] & 2) != 0) { candidates[take++] = candidates[i] with { Trace = (selected[i] & 1) != 0 }; }
        }
        candidates.RemoveRange(take, candidates.Count - take);
        OrderCandidates(candidates, all);
        return stale;
    }

    private static Vector4[] LightingRows(Lighting lighting, in Snapshot snapshot, bool feedback) {
        var rows = new Vector4[SdfMeshProbeRelight.HeaderRows + (lighting.Materials.Length + 1) * SdfMeshProbeRelight.MaterialRows +
            (lighting.Lights.Length + lighting.Points.Length) * SdfMeshProbeRelight.LightRows];
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
        for (var i = 0; i < lighting.SkyCoefficients.Length; i++) { rows[11 + i] = new(lighting.SkyCoefficients[i], 0); }
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

    private SdfMeshProbeGpuWork.Result RelightGpu(List<Candidate> candidates, int first, int count, ProbeData[] probes, LightBlock lights,
        Lighting lighting, in Snapshot snapshot, bool feedback) {
        var rows = LightingRows(lighting, in snapshot, feedback);
        var input = new uint[count * SdfMeshProbeRelight.InputWords];
        var origins = new Vector4[count];
        for (var i = 0; i < count; i++) {
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
            row[1] = (uint)probe.GpuToken;
            row[2] = (uint)(probe.GpuToken >> 32);
            row[3] = probe.GpuSlot;
            Compose(in probe, candidate.Cell, row[SdfMeshProbeRelight.RecordInputWord..]);
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) && previous.Shading is not null &&
                previous.Geometry == snapshot.Geometry && previous.LocalVersion == lighting.LocalVersion) {
                row[0] |= 2;
                MemoryMarshal.Cast<Vector3, uint>(previous.Local).CopyTo(row[(SdfMeshProbeRelight.PreviousWord + 18)..]);
                previous.Lights.CopyTo(row[(SdfMeshProbeRelight.PreviousWord + 36)..]);
            }
        }
        (int Word, uint[] Words)[] updates;
        int volumeWords;
        uint[]? snapshotOf = null;
        lock (m_gate) {
            volumeWords = m_volume.Length;
            if (!ReferenceEquals(m_gpuVolume, m_volume)) {
                updates = [(0, (uint[])m_volume.Clone())];
                m_gpuVolume = m_volume;
                snapshotOf = m_volume;
            } else {
                updates = new (int, uint[])[m_gpuDirty.Count];
                var i = 0;
                foreach (var slot in m_gpuDirty) {
                    var word = slot * SdfMeshIrradianceProbe.ProbeWords;
                    updates[i++] = (word, m_volume.AsSpan(word, SdfMeshIrradianceProbe.ProbeWords).ToArray());
                }
            }
            m_gpuDirty.Clear();
        }
        SdfMeshProbeGpuWork.Result output;
        try { output = GpuTrace!.Relight(snapshot.Scene, origins, input, rows, volumeWords, updates, GpuHitCapacity); }
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
            for (var i = 0; i < count; i++) {
                var probe = probes[i];
                if (probe.Hits is null || probe.Invalid) { continue; }
                var row = output.Words.AsSpan(i * SdfMeshProbeRelight.ResultWords, SdfMeshProbeRelight.OutputWords);
                MemoryMarshal.Cast<uint, Vector3>(row[..18]).CopyTo(lights.Radiance(i));
                MemoryMarshal.Cast<uint, Vector3>(row.Slice(18, 18)).CopyTo(lights.Local(i));
                row[36..].CopyTo(lights.Lights(i));
                var candidate = candidates[first + i];
                probes[i] = probe with { Moved = candidate.Trace || !m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) ||
                    previous.Shading is null || !Settled(lights.Radiance(i), previous.Radiance) };
            }
        } catch { output.Dispose(); throw; }
        return output;
    }
}
