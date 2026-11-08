using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

// Per-consumer presentation evidence. Unknown, changed and retired observations always keep demand. The GPU
// evaluates the same slabs as the host's frustum test; the result describes a completed earlier frame, not simulation.
internal sealed class SdfPortalSight {
    internal const int Capacity = SdfWorldTables.MaxScreenSurfaces;
    internal readonly record struct Query(CameraSnapshot Camera, Vector3 Center, Vector3 HalfX, Vector3 HalfY, Vector3 HalfZ);
    private readonly Query?[] m_queries = new Query?[Capacity];
    private readonly Query?[] m_hidden = new Query?[Capacity];
    private readonly List<SdfMeshOcclusion> m_readers = [];
    private SdfWorldView? m_view;
    private object? m_program;
    private long m_epoch;

    internal bool Occluded(int screen, in Query query) {
        if ((uint)screen >= Capacity) { return false; }
        m_queries[screen] = query;
        return m_hidden[screen] == query;
    }
    internal void Follow(SdfWorldView? view) {
        var program = view?.Residency.Frame?.Program;
        if (view != m_view || !ReferenceEquals(program, m_program)) {
            Array.Clear(m_queries);
            Array.Clear(m_hidden);
            m_epoch++;
            m_view = view;
            m_program = program;
        }
    }
    internal void Attach(SdfMeshOcclusion reader) => m_readers.Add(reader);
    internal void Detach(SdfMeshOcclusion reader) {
        m_readers.Remove(reader);
        Array.Clear(m_hidden);
    }
    internal long Capture(Span<Vector4> rows, Query?[] queries, in CameraSnapshot camera) {
        for (var index = 0; index < Capacity; index++) {
            var query = m_queries[index] is { } requested && requested.Camera == camera ? requested : (Query?)null;
            queries[index] = query;
            if (query is not { } slab) { continue; }
            var first = index * 4;
            rows[first] = new Vector4(slab.Center, 1f);
            rows[first + 1] = new Vector4(slab.HalfX, 0f);
            rows[first + 2] = new Vector4(slab.HalfY, 0f);
            rows[first + 3] = new Vector4(slab.HalfZ, 0f);
        }
        return m_epoch;
    }
    internal void Complete(long epoch, Query?[] queries, ReadOnlySpan<uint> visible) {
        if (epoch != m_epoch) { return; }
        for (var index = 0; index < Capacity; index++) {
            if (queries[index] == m_queries[index]) {
                m_hidden[index] = visible[index] == 0 ? queries[index] : null;
            }
        }
    }
}
