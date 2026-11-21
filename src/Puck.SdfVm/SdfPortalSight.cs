using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

// Per-consumer presentation evidence. Unknown, changed and retired observations always keep demand. The GPU
// evaluates the same slabs as the host's frustum test; the result describes a completed earlier frame, not simulation.
//
// A completed test hides its slab from the camera it ran for, and, where the test also proved the slab hidden from every
// eye within its reach (sdfOcclusionHiddenWithin), from any later camera whose eye stays within that reach of the tested
// one, whichever way it faces. So a portal is hidden only while a completed test covers the current camera; Occluded is
// the one door that applies a verdict.
internal sealed class SdfPortalSight {
    internal const int Capacity = SdfWorldTables.MaxScreenSurfaces;
    // The reach a test asks for, in eye steps since the previous recording: a verdict is read back a few frames after its
    // recording (the node's frame slots), so it covers that many steps at the eye's current pace, twice over.
    internal const float ReachSteps = 8f;
    // The least and most reach a test asks for, in metres: the least keeps a verdict through a still eye's drift; the most
    // keeps the widened region a fraction of a view, past which a test proves little.
    internal const float MinimumReach = 0.05f;
    internal const float MaximumReach = 2f;

    internal readonly record struct Query(CameraSnapshot Camera, Vector3 Center, Vector3 HalfX, Vector3 HalfY, Vector3 HalfZ) {
        // Whether another query names the same slab.
        public bool SameSlab(in Query other) => (Center == other.Center) && (HalfX == other.HalfX) && (HalfY == other.HalfY) && (HalfZ == other.HalfZ);
    }
    // A query as one recording tested it, with the reach of eyes its second verdict covers.
    internal readonly record struct Sighting(Query Query, float Reach);
    // A completed verdict: the sighting, whether the slab is hidden from its camera, and whether from every eye within
    // its reach.
    private readonly record struct Verdict(Sighting Sighting, bool Hidden, bool HiddenWithin);

    private readonly Query?[] m_queries = new Query?[Capacity];
    private readonly Verdict?[] m_verdicts = new Verdict?[Capacity];
    private readonly List<SdfMeshOcclusion> m_readers = [];
    private SdfWorldView? m_view;
    private object? m_program;
    private long m_epoch;
    private Vector3? m_capturedEye;

    internal bool Occluded(int screen, in Query query) {
        if ((uint)screen >= Capacity) { return false; }
        m_queries[screen] = query;
        if (m_verdicts[screen] is not { } verdict || !verdict.Sighting.Query.SameSlab(other: in query)) { return false; }
        if (verdict.Hidden && (verdict.Sighting.Query.Camera == query.Camera)) { return true; }
        return verdict.HiddenWithin && (Vector3.Distance(value1: verdict.Sighting.Query.Camera.Position, value2: query.Camera.Position) <= verdict.Sighting.Reach);
    }
    internal void Follow(SdfWorldView? view) {
        var program = view?.Residency.Frame?.Program;
        if (view != m_view || !ReferenceEquals(program, m_program)) {
            Array.Clear(m_queries);
            Array.Clear(m_verdicts);
            m_epoch++;
            m_view = view;
            m_program = program;
            m_capturedEye = null;
        }
    }
    internal void Attach(SdfMeshOcclusion reader) => m_readers.Add(reader);
    internal void Detach(SdfMeshOcclusion reader) {
        m_readers.Remove(reader);
        Array.Clear(m_verdicts);
    }
    internal long Capture(Span<Vector4> rows, Sighting?[] sightings, in CameraSnapshot camera) {
        var reach = Math.Clamp(value: ((m_capturedEye is { } previous) ? (ReachSteps * Vector3.Distance(value1: previous, value2: camera.Position)) : MaximumReach),
            min: MinimumReach, max: MaximumReach);

        m_capturedEye = camera.Position;
        for (var index = 0; index < Capacity; index++) {
            var sighting = ((m_queries[index] is { } requested) && (requested.Camera == camera)) ? new Sighting(Query: requested, Reach: reach) : (Sighting?)null;
            sightings[index] = sighting;
            if (sighting is not { Query: var slab }) { continue; }
            var first = index * 4;
            rows[first] = new Vector4(slab.Center, 1f);
            rows[first + 1] = new Vector4(slab.HalfX, reach);
            rows[first + 2] = new Vector4(slab.HalfY, 0f);
            rows[first + 3] = new Vector4(slab.HalfZ, 0f);
        }
        return m_epoch;
    }
    // Takes each completed verdict (bit one: the slab may show; bit two: it is hidden within the sighting's reach). A later
    // sighting's verdict replaces an earlier one, since one queue completes recordings in order.
    internal void Complete(long epoch, Sighting?[] sightings, ReadOnlySpan<uint> verdicts) {
        if (epoch != m_epoch) { return; }
        for (var index = 0; index < Capacity; index++) {
            if (sightings[index] is not { } sighting) { continue; }
            m_verdicts[index] = new Verdict(Sighting: sighting, Hidden: ((verdicts[index] & 1u) == 0u), HiddenWithin: ((verdicts[index] & 2u) != 0u));
        }
    }
}
