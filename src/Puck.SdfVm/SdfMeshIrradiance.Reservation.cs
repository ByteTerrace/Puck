using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // What the residency's lighting reservation is sized from (Reserve): the trace words the frame's static draws need.
    // The frame thread's alone, as Stage is.
    private readonly TraceEstimate m_traceEstimate = new();

    // Sizes the residency's lighting reservation (SdfLightingHold): the table and the hit cache of the layout the frame
    // asks for, which the probe service allocates as its first relight and trace of that layout run, the largest batch
    // and a camera sample a view, and the trace words of the meshes the frame's static draws name, from the frame they
    // name them: before their words stand in the region, before a scene builds them, and long before the probe scenes
    // grow to hold them, so the pool counts them from the first while the bake fills the region.
    private void Reserve(SdfFrame frame, SdfLightingHold hold, SdfIndirectTier tier) {
        var layout = Layout.Of(bounces: frame.IndirectBounces, levels: frame.IndirectLevels, reach: 1f, tier: tier);
        var caches = checked(((SdfMeshProbeGpuWork.HitCacheBytes(capacity: HitCapacityOf(layout: layout)) + SdfMeshProbeGpuWork.BatchOutputBytes(probes: DrainBatchProbes)) +
            SdfMeshProbeGpuWork.CameraSampleCacheBytes(origins: frame.Views.Count)));

        hold.Reserve(
            cacheBytes: checked((long)caches),
            tableBytes: (((long)layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords) * sizeof(uint)),
            traceBytes: (m_traceEstimate.Update(draws: frame.DrawRows) * sizeof(uint))
        );
    }

    // The trace words a frame's static draws need in the probe scenes (SdfMeshIrradianceScene.EstimateGpuWords): each
    // geometry once, however many draws trace it, and each draw's instance. A draw adds what the scene would hold for it
    // (TraceDraws): its group's floor pieces where it traces the floor (at the placement's first piece), else its own
    // mesh, whether or not its words stand yet. Kept by the rows the draws' journal names, as the scene key is
    // (StaticKey), so a frame whose static draws stand costs nothing.
    private sealed class TraceEstimate {
        private readonly GpuRowCursor m_cursor = new();
        private readonly GpuUploadRuns m_runs = new(capacity: GpuRegion.MaxCopyRuns);
        // Each geometry's draws and its block's words, as estimated when its first draw came.
        private readonly Dictionary<SdfMeshIrradianceScene.GeometryKey, (int Draws, long Words)> m_geometries = [];
        // The mesh each row traces (its own, or the floor whose pieces it traces), null for a row that adds nothing, and
        // whether it traces a floor.
        private SdfMesh?[] m_traced = [];
        private bool[] m_floors = [];
        private int m_extent;
        private long m_geometryWords;
        private long m_instanceWords;

        // Folds the rows the journal names since the last update and returns the words in all.
        public long Update(SdfMeshDraws draws) {
            if (m_cursor.Collect(reader: draws.Changes, through: draws.Serial, table: draws.Roster, rows: draws.Count, revision: draws.Revision, into: m_runs)) {
                for (var row = 0; (row < m_extent); row++) { Remove(row: row); }
            }
            if (m_traced.Length < draws.Count) {
                var size = ArrayCapacity.Grown(current: m_traced.Length, required: draws.Count);

                Array.Resize(array: ref m_traced, newSize: size);
                Array.Resize(array: ref m_floors, newSize: size);
            }
            for (var row = draws.Count; (row < m_extent); row++) { Remove(row: row); }
            for (var run = 0; (run < m_runs.Count); run++) {
                var end = (m_runs.Start(index: run) + m_runs.Length(index: run));

                for (var row = m_runs.Start(index: run); (row < end); row++) {
                    Remove(row: row);

                    var draw = draws[row];

                    if (!draw.InStaticScene) { continue; }
                    if (draw.Trace is { } floor) {
                        if (draw.Piece != 0) { continue; }
                        m_traced[row] = floor;
                        m_floors[row] = true;
                    } else {
                        m_traced[row] = draw.Mesh;
                        m_floors[row] = false;
                    }
                    Change(row: row, sign: 1);
                }
            }
            m_extent = draws.Count;
            m_cursor.Commit();
            return checked(m_geometryWords + m_instanceWords);
        }
        private void Remove(int row) {
            if (m_traced[row] is null) { return; }
            Change(row: row, sign: -1);
            m_traced[row] = null;
        }
        // Adds (sign one) or takes back (minus one) what a row's traced mesh, or each piece of its floor, adds.
        private void Change(int row, int sign) {
            var mesh = m_traced[row]!;
            var pieces = (m_floors[row] ? mesh.PieceCount : 1);

            for (var piece = 0; (piece < pieces); piece++) {
                var (key, words, instance) = SdfMeshIrradianceScene.EstimateGpuWords(error: TraceError, mesh: (m_floors[row] ? mesh.Piece(index: piece) : mesh));
                var (count, held) = (m_geometries.TryGetValue(key: key, value: out var entry) ? entry : (0, words));

                m_instanceWords += (sign * instance);
                count += sign;
                if (count == 0) {
                    _ = m_geometries.Remove(key: key);
                    m_geometryWords -= held;
                    continue;
                }
                if (count == 1 && sign > 0) { m_geometryWords += held; }
                m_geometries[key] = (count, held);
            }
        }
    }
}
