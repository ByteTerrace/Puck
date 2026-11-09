using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

// One view's sight queries against its mesh pass's farthest-depth pyramid: per-consumer presentation evidence that a box
// is hidden from the view. Two kinds of query share the one test (sdfOcclusionMaySee) and the one completion seam: a
// portal slab, one per screen index, applied only to the exact query and camera it was captured for; and a keyed group
// of boxes (a rated body's swept reach, or the receivers its shadow can reach), hidden only when every box of the group
// is, applied only to the version and camera it was captured for. Unknown, changed, retired and lapsed observations always
// keep demand. The GPU evaluates the same boxes as the host's frustum test; a result describes a completed earlier frame,
// not simulation, so moving occluders carry its delay. Frame-thread state: the pack, the footprints and the recording all
// run there (runtime contracts: a node's frame is one record job, serial on the frame thread).
internal sealed class SdfPortalSight {
    // The screen slabs' query indices: query i below this is screen i.
    internal const int Screens = SdfWorldTables.MaxScreenSurfaces;
    // The frames a group may go unasked before it stops being captured.
    private const long RetireFrames = 30L;

    internal readonly record struct Query(CameraSnapshot Camera, Vector3 Center, Vector3 HalfX, Vector3 HalfY, Vector3 HalfZ);

    // One keyed group's request and its latest completed answer.
    internal sealed class Group(long key) {
        public readonly long Key = key;
        public long Version = long.MinValue;
        public CameraSnapshot Camera;
        public SdfMeshBox[] Boxes = [];
        public int Count;
        // The sight frame the version was first asked on, and the latest frame it was asked on.
        public long Asked;
        public long Touched;
        // The latest completed answer: the version and camera it was captured for, and whether every box was hidden.
        public long DoneVersion = long.MinValue;
        public CameraSnapshot DoneCamera;
        public bool DoneHidden;
    }

    // What one recording captured: its epoch and frame, each screen's query, and each group's version and query range.
    internal sealed class Captured {
        internal readonly Query?[] Screens = new Query?[SdfPortalSight.Screens];
        internal readonly List<(Group Group, long Version, CameraSnapshot Camera, int First, int Count)> Groups = [];
        internal long Epoch;
        internal int Queries;
    }

    private readonly Query?[] m_queries = new Query?[Screens];
    private readonly Query?[] m_hidden = new Query?[Screens];
    private readonly Dictionary<long, Group> m_groups = [];
    private readonly List<long> m_retired = [];
    private readonly List<SdfMeshOcclusion> m_readers = [];
    private SdfWorldView? m_view;
    private object? m_program;
    private long m_epoch;
    private long m_frame;
    private int m_latency = -1;

    // The frames from a group version's first ask to its first completed answer, the latest measured; -1 before one.
    internal int LatencyFrames => m_latency;
    // The queries the next capture holds, which sizes the recorder's buffers.
    internal int QueryCount {
        get {
            var count = Screens;

            foreach (var group in m_groups.Values) { count += group.Count; }
            return count;
        }
    }

    internal bool Occluded(int screen, in Query query) {
        if ((uint)screen >= Screens) { return false; }
        m_queries[screen] = query;
        return m_hidden[screen] == query;
    }
    // Asks whether a group's boxes are hidden from the view, and keeps the group captured. A version or camera that
    // differs from the group's request replaces it (its boxes copied), so it answers false until a recording captured for
    // that version and camera completes.
    internal bool Hidden(long key, long version, in CameraSnapshot camera, ReadOnlySpan<SdfMeshBox> boxes) {
        if (!m_groups.TryGetValue(key, out var group)) {
            group = new Group(key);
            m_groups.Add(key, group);
        }
        if ((group.Version != version) || (group.Camera != camera)) {
            group.Version = version;
            group.Camera = camera;
            if (group.Boxes.Length < boxes.Length) { group.Boxes = new SdfMeshBox[boxes.Length]; }
            boxes.CopyTo(group.Boxes);
            group.Count = boxes.Length;
            group.Asked = m_frame;
        }
        group.Touched = m_frame;

        return group.DoneHidden && (group.DoneVersion == version) && (group.DoneCamera == camera);
    }
    internal void Follow(SdfWorldView? view, long frame) {
        m_frame = frame;
        var program = view?.Residency.Frame?.Program;
        if (view != m_view || !ReferenceEquals(program, m_program)) {
            Array.Clear(m_queries);
            m_groups.Clear();
            Lapse();
            m_epoch++;
            m_view = view;
            m_program = program;
        }
    }
    // Forgets every completed answer: the view rendered a frame no sight test ran on, so no answer describes what it shows.
    internal void Lapse() {
        Array.Clear(m_hidden);
        foreach (var group in m_groups.Values) { group.DoneHidden = false; }
    }
    internal void Attach(SdfMeshOcclusion reader) => m_readers.Add(reader);
    internal void Detach(SdfMeshOcclusion reader) {
        m_readers.Remove(reader);
        Lapse();
    }
    // Writes the queries captured for a camera into the rows after the sight's header row, four rows a query, and records
    // what they were; returns the query count. Groups asked for another camera, or not asked for RetireFrames, are left
    // out, and the latter retired.
    internal int Capture(Span<Vector4> rows, Captured captured, in CameraSnapshot camera) {
        captured.Groups.Clear();
        captured.Epoch = m_epoch;
        for (var index = 0; index < Screens; index++) {
            var query = m_queries[index] is { } requested && requested.Camera == camera ? requested : (Query?)null;
            captured.Screens[index] = query;
            var first = index * 4;
            if (query is not { } slab) {
                rows.Slice(first, 4).Clear();
                continue;
            }
            rows[first] = new Vector4(slab.Center, 1f);
            rows[first + 1] = new Vector4(slab.HalfX, 0f);
            rows[first + 2] = new Vector4(slab.HalfY, 0f);
            rows[first + 3] = new Vector4(slab.HalfZ, 0f);
        }

        var next = Screens;

        m_retired.Clear();
        foreach (var group in m_groups.Values) {
            if ((m_frame - group.Touched) > RetireFrames) {
                m_retired.Add(group.Key);
                continue;
            }
            if ((group.Camera != camera) || (group.Count == 0) || (((next + group.Count) * 4) > rows.Length)) { continue; }
            captured.Groups.Add((group, group.Version, group.Camera, next, group.Count));
            for (var box = 0; box < group.Count; box++) {
                var bounded = group.Boxes[box];
                var first = (next + box) * 4;
                rows[first] = new Vector4(bounded.Center, 1f);
                rows[first + 1] = new Vector4(bounded.HalfX, 0f);
                rows[first + 2] = new Vector4(bounded.HalfY, 0f);
                rows[first + 3] = new Vector4(bounded.HalfZ, 0f);
            }
            next += group.Count;
        }
        foreach (var key in m_retired) { m_groups.Remove(key); }

        captured.Queries = next;

        return next;
    }
    // Applies a completed recording's verdicts (nonzero: the box may show): a screen's to the query still asked, a group's
    // to the group still held, as its latest answer for the version and camera captured.
    internal void Complete(Captured captured, ReadOnlySpan<uint> visible) {
        if (captured.Epoch != m_epoch) { return; }
        for (var index = 0; index < Screens; index++) {
            if (captured.Screens[index] == m_queries[index]) {
                m_hidden[index] = visible[index] == 0 ? captured.Screens[index] : null;
            }
        }
        foreach (var (group, version, camera, first, count) in captured.Groups) {
            if (!m_groups.TryGetValue(group.Key, out var held) || !ReferenceEquals(held, group)) { continue; }

            var hidden = true;

            for (var index = first; index < (first + count); index++) {
                hidden &= (visible[index] == 0u);
            }
            if ((group.DoneVersion != version) && (version == group.Version)) {
                m_latency = (int)Math.Min(m_frame - group.Asked, int.MaxValue);
            }
            group.DoneVersion = version;
            group.DoneCamera = camera;
            group.DoneHidden = hidden;
        }
    }
}
