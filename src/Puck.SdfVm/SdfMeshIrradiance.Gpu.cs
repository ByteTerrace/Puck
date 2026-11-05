using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // An explicit world.probe read checks the runtime's cached output against one further CPU-reference relight.
    // The local term and visibility must agree to float precision; the varying term also includes the convergence
    // residual (ConvergedShare). This diagnostic does no work during presentation.
    private string DescribeRelightReference(ProbeData probe, Vector3 origin, SdfMeshIrradianceScene scene) {
        Span<Vector3> radiance = stackalloc Vector3[6];
        Span<Vector3> local = stackalloc Vector3[6];
        Span<uint> visibility = stackalloc uint[SdfMeshIrradianceProbe.LightWords];
        Relight(origin, probe.HitWords, m_layout, m_lighting!, Lows(), scene, m_lighting!.Feedback && m_layout.Bounce,
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

    private bool ListGpu(in Snapshot snapshot, bool all, List<Candidate> candidates, List<float>? ages) {
        var layout = snapshot.Layout;
        var origins = new Vector4[layout.TotalCount];
        var input = new uint[layout.TotalCount * SdfMeshProbeRelight.SelectionInputWords];
        var staleHits = new List<int>();
        var now = Stopwatch.GetTimestamp();
        candidates.Clear();
        ages?.Clear();
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
                        row[0] = present ? 1u : 0;
                        row[1] = (uint)probe.Geometry; row[2] = (uint)probe.Lighting; row[3] = (uint)probe.Sweep; row[4] = (uint)level;
                        row[5] = BitConverter.SingleToUInt32Bits(probe.Offset.X);
                        row[6] = BitConverter.SingleToUInt32Bits(probe.Offset.Y);
                        row[7] = BitConverter.SingleToUInt32Bits(probe.Offset.Z);
                        if (present && probe.Geometry != snapshot.Geometry) { staleHits.Add(i); }
                        candidates.Add(new Candidate(Cell: (x, y, z), Distance: Vector3.DistanceSquared(center, snapshot.Camera), Level: level, LitAt: present ? probe.LitAt : 0, Trace: false));
                        if (present) { ages?.Add(probe.Lighting == snapshot.Lighting.Version ? 0 : (float)Stopwatch.GetElapsedTime(StaleSince(probe.Lighting, in snapshot), now).TotalSeconds); }
                    }
                }
            }
        }
        if (staleHits.Count > 0) {
            var cursor = input.Length;
            Array.Resize(ref input, input.Length + staleHits.Count * SdfMeshProbeDepth.HitWords);
            foreach (var i in staleHits) {
                var candidate = candidates[i];
                _ = m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var probe);
                input[i * SdfMeshProbeRelight.SelectionInputWords + 8] = (uint)cursor;
                probe.HitWords.CopyTo(input.AsSpan(cursor));
                cursor += SdfMeshProbeDepth.HitWords;
            }
        }
        var changes = new List<Vector4> {
            new(all ? 1 : 0, snapshot.Geometry, snapshot.Lighting.Version, snapshot.Sweep),
            new(snapshot.Changes.Length, 0, snapshot.Changes.Length == 0 ? 0 : snapshot.Changes[0].Geometry, layout.Reach),
            new(layout.SpacingOf(0), 0), new(layout.SpacingOf(1), 0), new(layout.SpacingOf(2), 0),
        };
        foreach (var (geometry, boxes) in snapshot.Changes) {
            if (boxes is null) { changes.Add(new(0, 0, 0, geometry)); changes.Add(new(0, 0, 0, 1)); }
            else { foreach (var box in boxes) { changes.Add(new(box.Low, geometry)); changes.Add(new(box.High, 0)); } }
        }
        changes[1] = changes[1] with { Y = (changes.Count - 5) / 2 };
        var selected = GpuTrace!.Select(snapshot.Scene, origins, input, [.. changes]);
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

    private void RelightGpu(List<Candidate> candidates, int first, int count, ProbeData[] probes, LightBlock lights,
        Lighting lighting, in Snapshot snapshot, bool feedback) {
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
        var input = new uint[count * SdfMeshProbeRelight.InputWords];
        var origins = new Vector4[count];
        for (var i = 0; i < count; i++) {
            var candidate = candidates[first + i];
            var probe = probes[i];
            if (probe.Hits is null || probe.Invalid) { continue; }
            var cell = candidate.Cell;
            origins[i] = new(new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + .5f) * snapshot.Layout.SpacingOf(candidate.Level) + probe.Offset, snapshot.Layout.Reach);
            var row = input.AsSpan(i * SdfMeshProbeRelight.InputWords, SdfMeshProbeRelight.InputWords);
            row[0] = 1;
            probe.HitWords.CopyTo(row[1..]);
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) && previous.Shading is not null &&
                previous.Geometry == snapshot.Geometry && previous.LocalVersion == lighting.LocalVersion) {
                row[0] |= 2;
                MemoryMarshal.Cast<Vector3, uint>(previous.Local).CopyTo(row[(1 + SdfMeshProbeDepth.HitWords + 18)..]);
                previous.Lights.CopyTo(row[(1 + SdfMeshProbeDepth.HitWords + 36)..]);
            }
        }
        (int Word, uint[] Words)[] updates;
        int volumeWords;
        lock (m_gate) {
            volumeWords = m_volume.Length;
            if (!ReferenceEquals(m_gpuVolume, m_volume)) {
                updates = [(0, (uint[])m_volume.Clone())];
                m_gpuVolume = m_volume;
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
        uint[] output;
        try { output = GpuTrace!.Relight(snapshot.Scene, origins, input, rows, volumeWords, updates); }
        catch (OperationCanceledException) {
            // Admission cancellation consumed no delta. Re-send a full snapshot on the next accepted job.
            lock (m_gate) { m_gpuVolume = null; }
            throw;
        }
        for (var i = 0; i < count; i++) {
            var probe = probes[i];
            if (probe.Hits is null || probe.Invalid) { continue; }
            var row = output.AsSpan(i * SdfMeshProbeRelight.OutputWords, SdfMeshProbeRelight.OutputWords);
            MemoryMarshal.Cast<uint, Vector3>(row[..18]).CopyTo(lights.Radiance(i));
            MemoryMarshal.Cast<uint, Vector3>(row.Slice(18, 18)).CopyTo(lights.Local(i));
            row[36..].CopyTo(lights.Lights(i));
            var candidate = candidates[first + i];
            probes[i] = probe with { Moved = candidate.Trace || !m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) ||
                previous.Shading is null || !Settled(lights.Radiance(i), previous.Radiance) };
        }
    }
}
