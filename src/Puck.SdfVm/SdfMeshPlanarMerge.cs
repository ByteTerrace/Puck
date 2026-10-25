using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

/// <summary>
/// The bake's first level of detail: a contoured mesh's coplanar triangles merged into the polygons they tile, changing no
/// pixel. The contour carries a quad a lattice cell on every flat face, which every pass that draws or traces the mesh pays
/// for.
/// <para>A region is a connected run of triangles, across edges two triangles share by index, that shade alike everywhere:
/// one material, one member (<see cref="SdfMesh.VertexMembers"/>), one normal bit for bit at every vertex, and every vertex
/// within a few units in the last place of a float of one plane. A vertex only its region's triangles name at its place,
/// and whose triangles close a ring about it, is removed, and the hole its ring leaves is triangulated again from the
/// vertices about it. Every other vertex stays: one an unmerged neighbour names, one two regions share, one on the mesh's
/// open edge. So every region keeps its outline edge for edge, no T-junction opens a crack, and the rasterizer covers each
/// polygon's outline as it did. A straight run between two merged faces keeps its vertices too: dropped from both sides, the
/// geometry holds but the rasterizer snaps the shared edge from other vertices, for one part in a hundred of the triangles
/// left.</para>
/// <para>A removal is taken only where exact orientation tests on the vertices' own float coordinates certify it: each new
/// triangle faces as its region does and holds no other vertex of the hole, so together they cover the hole exactly. Each
/// is then improved by flipping edges toward the Delaunay triangulation of the region's remaining vertices where the circle
/// test is certain, so the polygons' triangles are as round as their vertices allow. No vertex moves and none changes its
/// normal, so a region shades as its one normal everywhere, as it did. The merge is a pure function of the mesh, and
/// presentation alone.</para>
/// </summary>
public static class SdfMeshPlanarMerge {
    // How far off a region's plane a vertex may lie, as a share of the larger of its own largest coordinate and its
    // region's first vertex's, or of a metre for vertices nearer the origin: 2^-21, four to eight units in the last place
    // of a float, the rounding the contour's float positions carry. A flat face's vertices lie on its plane to that
    // rounding (an axis-aligned face's share its one coordinate exactly), so no merged surface moves past it.
    private const double PlaneTolerance = (1.0 / (1 << 21));

    /// <summary>Merges a mesh's coplanar triangles into the polygons they tile.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The merged mesh, with the same <see cref="SdfMesh.BakeCell"/> and members, and the attributes of the
    /// vertices it keeps; <paramref name="mesh"/> itself when nothing merges or it carries no vertex normals.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static SdfMesh Merge(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(argument: mesh);

        return (((mesh.Normals.Length == 0) || (mesh.TriangleCount < 2)) ? mesh : new Merger(mesh: mesh).Run());
    }

    // One merge's state: the triangles as a pool of three half-edges each (half-edge h runs from corner h to the next corner
    // of its triangle), their twins by vertex index, the region of each, and per vertex its alive triangles' count and one
    // half-edge leaving it.
    private sealed class Merger {
        private readonly SdfMesh m_mesh;
        private readonly int m_triangles;
        private readonly int[] m_site;
        private readonly float[] m_u;
        private readonly float[] m_w;
        private readonly int[] m_corners;
        private readonly int[] m_twins;
        private readonly int[] m_regionOf;
        private readonly bool[] m_alive;
        private readonly int[] m_degree;
        private readonly int[] m_leave;
        private int m_removedTriangles;
        // Scratch every removal reuses.
        private readonly List<int> m_ring = [];
        private readonly List<int> m_polygon = [];
        private readonly List<int> m_outer = [];
        private readonly List<(int A, int B, int C)> m_ears = [];
        private readonly Stack<int> m_flips = new();
        private int[] m_previous = [];
        private int[] m_following = [];
        private int[] m_outside = [];

        public Merger(SdfMesh mesh) {
            m_mesh = mesh;
            m_triangles = mesh.TriangleCount;

            var positions = mesh.Positions.Span;
            var normals = mesh.Normals.Span;
            var indices = mesh.Indices.Span;
            var vertices = positions.Length;
            var first = new Dictionary<Vector3, int>(capacity: vertices);

            m_site = new int[vertices];
            m_u = new float[vertices];
            m_w = new float[vertices];
            for (var vertex = 0; (vertex < vertices); vertex++) {
                m_site[vertex] = (first.TryAdd(key: positions[vertex], value: vertex) ? vertex : first[positions[vertex]]);

                // Every vertex a region holds carries the region's normal, so its plane coordinates are the region's.
                (m_u[vertex], m_w[vertex]) = Plane(normal: normals[vertex], position: positions[vertex]);
            }

            m_corners = new int[(3 * m_triangles)];
            m_twins = new int[(3 * m_triangles)];
            m_regionOf = new int[m_triangles];
            m_alive = new bool[m_triangles];
            m_degree = new int[vertices];
            m_leave = new int[vertices];
            for (var half = 0; (half < indices.Length); half++) {
                m_corners[half] = (int)indices[half];
                m_degree[m_corners[half]]++;
                m_leave[m_corners[half]] = half;
            }
            Array.Fill(array: m_alive, value: true);
        }

        public SdfMesh Run() {
            Twins();
            if (!Regions()) { return m_mesh; }

            // The vertices are taken in a scattered order, so the holes removals leave stay a few vertices wide: in lattice
            // order each removal widens the last one's hole, and its ears and flips grow with it.
            var candidates = Candidates();

            candidates.Sort();
            foreach (var (_, vertex) in candidates) {
                Remove(vertex: vertex);
            }

            return ((m_removedTriangles == 0) ? m_mesh : Build());
        }

        private static int Next(int half) => (((half % 3) == 2) ? (half - 2) : (half + 1));
        private static int Previous(int half) => (((half % 3) == 0) ? (half + 2) : (half - 1));

        // Each half-edge's twin: the one half-edge running the other way between its two vertices, where exactly one runs
        // each way; -1 at an open or non-manifold edge.
        private void Twins() {
            var vertices = m_site.Length;
            var halves = (3 * m_triangles);
            var starts = new int[(vertices + 1)];

            for (var half = 0; (half < halves); half++) {
                starts[(m_corners[half] + 1)]++;
            }
            for (var vertex = 0; (vertex < vertices); vertex++) {
                starts[(vertex + 1)] += starts[vertex];
            }

            var outgoing = new int[halves];
            var filled = new int[vertices];

            for (var half = 0; (half < halves); half++) {
                var from = m_corners[half];

                outgoing[(starts[from] + filled[from]++)] = half;
            }
            for (var half = 0; (half < halves); half++) {
                var from = m_corners[half];
                var to = m_corners[Next(half: half)];
                var twin = -1;
                var back = 0;
                var same = 0;

                for (var at = starts[to]; (at < starts[(to + 1)]); at++) {
                    if (m_corners[Next(half: outgoing[at])] == from) {
                        back++;
                        twin = outgoing[at];
                    }
                }
                for (var at = starts[from]; (at < starts[(from + 1)]); at++) {
                    same += ((m_corners[Next(half: outgoing[at])] == to) ? 1 : 0);
                }
                m_twins[half] = (((back == 1) && (same == 1) && (from != to)) ? twin : -1);
            }
        }
        // Grows the regions: from each triangle no region holds yet that may seed one, every triangle reachable across twins
        // that shades as it does and lies on its plane. False when no region holds two triangles.
        private bool Regions() {
            var positions = m_mesh.Positions.Span;
            var normals = m_mesh.Normals.Span;
            var materials = m_mesh.TriangleMaterials.Span;
            var members = m_mesh.VertexMembers.Span;
            var eligible = new bool[m_triangles];
            var queue = new Queue<int>();
            var regions = 0;
            var any = false;

            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                var (a, b, c) = (m_corners[(3 * triangle)], m_corners[((3 * triangle) + 1)], m_corners[((3 * triangle) + 2)]);

                eligible[triangle] = (
                    Same(first: normals[a], second: normals[b]) &&
                    Same(first: normals[a], second: normals[c]) &&
                    (normals[a] != Vector3.Zero) &&
                    (members.IsEmpty || ((members[a] == members[b]) && (members[a] == members[c]))) &&
                    (Orient(a: a, b: b, c: c) > 0)
                );
            }

            Array.Fill(array: m_regionOf, value: -1);
            for (var seed = 0; (seed < m_triangles); seed++) {
                if (!eligible[seed] || (m_regionOf[seed] >= 0)) { continue; }

                var anchor = positions[m_corners[(3 * seed)]];
                var normal = normals[m_corners[(3 * seed)]];
                var plane = new Plane3(Normal: normal, Offset: ((((double)normal.X * anchor.X) + ((double)normal.Y * anchor.Y)) + ((double)normal.Z * anchor.Z)), Scale: Math.Max(val1: 1.0, val2: Largest(point: anchor)));

                if (!OnPlane(plane: plane, positions: positions, triangle: seed)) { continue; }

                var region = regions++;
                var material = (materials.IsEmpty ? 0u : materials[seed]);
                var member = (members.IsEmpty ? SdfMesh.NoMember : members[m_corners[(3 * seed)]]);
                var size = 0;

                m_regionOf[seed] = region;
                queue.Enqueue(item: seed);
                while (queue.TryDequeue(result: out var triangle)) {
                    size++;
                    for (var corner = 0; (corner < 3); corner++) {
                        var twin = m_twins[((3 * triangle) + corner)];

                        if (twin < 0) { continue; }

                        var other = (twin / 3);

                        if (
                            !eligible[other] ||
                            (m_regionOf[other] >= 0) ||
                            ((materials.IsEmpty ? 0u : materials[other]) != material) ||
                            (!members.IsEmpty && (members[m_corners[(3 * other)]] != member)) ||
                            !Same(first: normals[m_corners[(3 * other)]], second: normal) ||
                            !OnPlane(plane: plane, positions: positions, triangle: other)
                        ) { continue; }
                        m_regionOf[other] = region;
                        queue.Enqueue(item: other);
                    }
                }
                any |= (size > 1);
            }

            return any;
        }
        private bool OnPlane(Plane3 plane, ReadOnlySpan<Vector3> positions, int triangle) {
            var (nx, ny, nz) = ((double)plane.Normal.X, (double)plane.Normal.Y, (double)plane.Normal.Z);

            for (var corner = 0; (corner < 3); corner++) {
                var point = positions[m_corners[((3 * triangle) + corner)]];
                var distance = ((((nx * point.X) + (ny * point.Y)) + (nz * point.Z)) - plane.Offset);

                if (!(Math.Abs(value: distance) <= (PlaneTolerance * Math.Max(val1: plane.Scale, val2: Largest(point: point))))) { return false; }
            }

            return true;
        }
        // The vertices a removal may take, each the one vertex one region's triangles name at its place, no triangle outside
        // that region naming the place, keyed by a scattered order.
        private List<(uint Order, int Vertex)> Candidates() {
            const int None = -1;
            const int Blocked = -2;
            var vertices = m_site.Length;
            var named = new int[vertices];
            var region = new int[vertices];
            var candidates = new List<(uint Order, int Vertex)>();

            Array.Fill(array: named, value: None);
            for (var half = 0; (half < (3 * m_triangles)); half++) {
                var vertex = m_corners[half];
                var site = m_site[vertex];
                var own = m_regionOf[(half / 3)];

                if ((own < 0) || ((named[site] != None) && ((named[site] != vertex) || (region[site] != own)))) {
                    named[site] = Blocked;
                } else if (named[site] == None) {
                    named[site] = vertex;
                    region[site] = own;
                }
            }
            for (var site = 0; (site < vertices); site++) {
                if (named[site] >= 0) { candidates.Add(item: (unchecked((uint)named[site] * 2654435761u), named[site])); }
            }

            return candidates;
        }

        // The half-edges leaving a vertex, counter-clockwise about it, when its triangles close one ring about it.
        private bool Ring(int vertex, List<int> ring) {
            var start = m_leave[vertex];
            var half = start;

            ring.Clear();
            do {
                ring.Add(item: half);
                half = m_twins[Previous(half: half)];
            } while ((half >= 0) && (half != start) && (ring.Count < m_degree[vertex]));

            return ((half == start) && (ring.Count == m_degree[vertex]) && (m_corners[start] == vertex));
        }
        // Removes a vertex whose triangles close a ring about it, filling the hole with the ears of the polygon about it.
        private void Remove(int vertex) {
            if (!Ring(ring: m_ring, vertex: vertex) || !Turns(ring: m_ring, vertex: vertex)) { return; }

            m_polygon.Clear();
            m_outer.Clear();
            foreach (var half in m_ring) {
                m_polygon.Add(item: m_corners[Next(half: half)]);
                m_outer.Add(item: m_twins[Next(half: half)]);
            }
            if (!Triangulate(polygon: CollectionsMarshal.AsSpan(list: m_polygon))) { return; }
            Replace(ears: CollectionsMarshal.AsSpan(list: m_ears), outer: CollectionsMarshal.AsSpan(list: m_outer), polygon: CollectionsMarshal.AsSpan(list: m_polygon), ring: CollectionsMarshal.AsSpan(list: m_ring));
            Flip();
        }
        // Whether a ring's triangles turn once about its vertex in their plane: a ring that winds twice is no hole to fill.
        private bool Turns(List<int> ring, int vertex) {
            var angle = 0.0;
            var (cu, cw) = ((double)m_u[vertex], (double)m_w[vertex]);

            foreach (var half in ring) {
                var b = m_corners[Next(half: half)];
                var c = m_corners[Previous(half: half)];
                var (bu, bw) = ((m_u[b] - cu), (m_w[b] - cw));
                var (du, dw) = ((m_u[c] - cu), (m_w[c] - cw));

                angle += Math.Atan2(y: ((bu * dw) - (bw * du)), x: ((bu * du) + (bw * dw)));
            }

            return (Math.Abs(value: (angle - (2.0 * Math.PI))) < 1.0);
        }

        // Ear-clips a simple polygon wound counter-clockwise in its plane into m_ears (positions in the polygon), each ear
        // wound counter-clockwise and holding no other vertex of the polygon, on its edges included. False when no such ear
        // remains.
        private bool Triangulate(ReadOnlySpan<int> polygon) {
            var count = polygon.Length;

            m_ears.Clear();
            if (m_previous.Length < count) {
                m_previous = new int[(2 * count)];
                m_following = new int[(2 * count)];
            }
            for (var at = 0; (at < count); at++) {
                m_previous[at] = (((at + count) - 1) % count);
                m_following[at] = ((at + 1) % count);
            }

            var remaining = count;
            var ear = 0;

            while (remaining > 3) {
                var found = false;

                for (var tried = 0; (tried < remaining); tried++) {
                    var (before, after) = (m_previous[ear], m_following[ear]);

                    if (Orient(a: polygon[before], b: polygon[ear], c: polygon[after]) > 0) {
                        var clear = true;
                        var (a, b, c) = (polygon[before], polygon[ear], polygon[after]);
                        var (lowU, highU) = (MathF.Min(x: m_u[a], y: MathF.Min(x: m_u[b], y: m_u[c])), MathF.Max(x: m_u[a], y: MathF.Max(x: m_u[b], y: m_u[c])));
                        var (lowW, highW) = (MathF.Min(x: m_w[a], y: MathF.Min(x: m_w[b], y: m_w[c])), MathF.Max(x: m_w[a], y: MathF.Max(x: m_w[b], y: m_w[c])));

                        for (var other = m_following[after]; (other != before); other = m_following[other]) {
                            var point = polygon[other];

                            // A vertex outside the ear's box lies outside the ear.
                            if ((m_u[point] < lowU) || (m_u[point] > highU) || (m_w[point] < lowW) || (m_w[point] > highW)) { continue; }
                            if (Holds(a: a, b: b, c: c, point: point)) {
                                clear = false;
                                break;
                            }
                        }
                        if (clear) {
                            m_ears.Add(item: (before, ear, after));
                            m_following[before] = after;
                            m_previous[after] = before;
                            remaining--;
                            ear = after;
                            found = true;
                            break;
                        }
                    }
                    ear = m_following[ear];
                }
                if (!found) { return false; }
            }

            var (last, next) = (m_previous[ear], m_following[ear]);

            if (Orient(a: polygon[last], b: polygon[ear], c: polygon[next]) <= 0) { return false; }
            m_ears.Add(item: (last, ear, next));

            return true;
        }
        // Whether a point lies in the closed triangle a, b, c, its edges and corners included.
        private bool Holds(int a, int b, int c, int point) =>
            ((Orient(a: a, b: b, c: point) >= 0) && (Orient(a: b, b: c, c: point) >= 0) && (Orient(a: c, b: a, c: point) >= 0));

        // Fills a vertex's ring with the ears over the polygon about it.
        private void Replace(ReadOnlySpan<int> ring, ReadOnlySpan<int> outer, ReadOnlySpan<int> polygon, ReadOnlySpan<(int A, int B, int C)> ears) {
            var count = polygon.Length;

            foreach (var half in ring) {
                var triangle = (half / 3);

                m_alive[triangle] = false;
                for (var corner = 0; (corner < 3); corner++) {
                    m_degree[m_corners[((3 * triangle) + corner)]]--;
                }
            }

            // The ears replay in the order they were clipped: each polygon edge still standing, from a position to the one
            // following it, holds the half-edge outside it (the twin of the ring's outer edge, or the far side of a diagonal
            // an earlier ear cut), which the ear that takes it links.
            if (m_outside.Length < count) { m_outside = new int[(2 * count)]; }

            var outside = m_outside.AsSpan(length: count, start: 0);

            outer.CopyTo(destination: outside);
            // A hole takes two triangles fewer than its ring held, so each ear takes the place of one of the ring's triangles
            // in the region, and the two places left over stay dead.
            for (var at = 0; (at < ears.Length); at++) {
                var (a, b, c) = ears[at];
                var triangle = (ring[at] / 3);

                Fill(a: polygon[a], b: polygon[b], c: polygon[c], triangle: triangle);
                Link(half: (3 * triangle), twin: outside[a]);
                Link(half: ((3 * triangle) + 1), twin: outside[b]);
                if (at == (ears.Length - 1)) {
                    Link(half: ((3 * triangle) + 2), twin: outside[c]);
                } else {
                    m_twins[((3 * triangle) + 2)] = -1;
                    outside[a] = ((3 * triangle) + 2);
                }
                m_flips.Push(item: (3 * triangle));
                m_flips.Push(item: ((3 * triangle) + 1));
                m_flips.Push(item: ((3 * triangle) + 2));
            }
            m_removedTriangles += (ring.Length - ears.Length);
        }
        private void Fill(int triangle, int a, int b, int c) {
            m_corners[(3 * triangle)] = a;
            m_corners[((3 * triangle) + 1)] = b;
            m_corners[((3 * triangle) + 2)] = c;
            m_alive[triangle] = true;
            for (var corner = 0; (corner < 3); corner++) {
                m_degree[m_corners[((3 * triangle) + corner)]]++;
                m_leave[m_corners[((3 * triangle) + corner)]] = ((3 * triangle) + corner);
            }
        }
        // Flips the queued edges toward the region's Delaunay triangulation: an edge between two alive triangles of one region
        // whose far vertex lies certainly inside the near triangle's circumcircle, where the flipped pair winds as they did.
        private void Flip() {
            while (m_flips.TryPop(result: out var half)) {
                var twin = m_twins[half];

                if ((twin < 0) || !m_alive[(half / 3)] || !m_alive[(twin / 3)] || (m_regionOf[(half / 3)] != m_regionOf[(twin / 3)])) { continue; }

                var a = m_corners[half];
                var b = m_corners[Next(half: half)];
                var c = m_corners[Previous(half: half)];
                var d = m_corners[Previous(half: twin)];

                if (!InCircle(a: a, b: b, c: c, d: d) || (Orient(a: a, b: d, c: c) <= 0) || (Orient(a: d, b: b, c: c) <= 0)) { continue; }

                var bc = m_twins[Next(half: half)];
                var ca = m_twins[Previous(half: half)];
                var ad = m_twins[Next(half: twin)];
                var db = m_twins[Previous(half: twin)];
                var (p, q) = ((3 * (half / 3)), (3 * (twin / 3)));

                m_corners[p] = a;
                m_corners[(p + 1)] = d;
                m_corners[(p + 2)] = c;
                m_corners[q] = d;
                m_corners[(q + 1)] = b;
                m_corners[(q + 2)] = c;
                Link(half: p, twin: ad);
                Link(half: (p + 1), twin: (q + 2));
                Link(half: (p + 2), twin: ca);
                Link(half: q, twin: db);
                Link(half: (q + 1), twin: bc);
                m_degree[a]--;
                m_degree[b]--;
                m_degree[c]++;
                m_degree[d]++;
                m_leave[a] = p;
                m_leave[d] = (p + 1);
                m_leave[c] = (p + 2);
                m_leave[b] = (q + 1);
                m_flips.Push(item: p);
                m_flips.Push(item: (p + 2));
                m_flips.Push(item: q);
                m_flips.Push(item: (q + 1));
            }
        }
        private void Link(int half, int twin) {
            m_twins[half] = twin;
            if (twin >= 0) { m_twins[twin] = half; }
        }

        // The merged mesh: the triangles alive in the pool, each in the place of a triangle its region held (so with that
        // triangle's material), in the pool's order, and the vertices they name, in theirs.
        private SdfMesh Build() {
            var materials = m_mesh.TriangleMaterials.Span;
            var positions = m_mesh.Positions.Span;
            var normals = m_mesh.Normals.Span;
            var members = m_mesh.VertexMembers.Span;
            var remaining = (m_triangles - m_removedTriangles);
            var indices = new uint[(3 * remaining)];
            var triangleMaterials = (materials.IsEmpty ? [] : new uint[remaining]);
            var remap = new int[positions.Length];
            var at = 0;

            Array.Fill(array: remap, value: -1);
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }
                if (!materials.IsEmpty) { triangleMaterials[at] = materials[triangle]; }
                for (var corner = 0; (corner < 3); corner++) {
                    indices[((3 * at) + corner)] = (uint)m_corners[((3 * triangle) + corner)];
                    remap[m_corners[((3 * triangle) + corner)]] = 0;
                }
                at++;
            }

            var kept = 0;

            for (var vertex = 0; (vertex < remap.Length); vertex++) {
                if (remap[vertex] == 0) { remap[vertex] = kept++; }
            }

            var keptPositions = new Vector3[kept];
            var keptNormals = new Vector3[kept];
            var keptMembers = (members.IsEmpty ? [] : new byte[kept]);

            for (var vertex = 0; (vertex < remap.Length); vertex++) {
                if (remap[vertex] < 0) { continue; }
                keptPositions[remap[vertex]] = positions[vertex];
                keptNormals[remap[vertex]] = normals[vertex];
                if (!members.IsEmpty) { keptMembers[remap[vertex]] = members[vertex]; }
            }
            for (var index = 0; (index < indices.Length); index++) {
                indices[index] = (uint)remap[(int)indices[index]];
            }

            return new SdfMesh(indices: indices, members: m_mesh.Members, normals: keptNormals, positions: keptPositions, triangleMaterials: triangleMaterials, vertexMembers: keptMembers) { BakeCell = m_mesh.BakeCell };
        }

        private int Orient(int a, int b, int c) => SdfMeshPlanarMerge.Orient(ax: m_u[a], ay: m_w[a], bx: m_u[b], by: m_w[b], cx: m_u[c], cy: m_w[c]);
        private bool InCircle(int a, int b, int c, int d) => SdfMeshPlanarMerge.InCircle(ax: m_u[a], ay: m_w[a], bx: m_u[b], by: m_w[b], cx: m_u[c], cy: m_w[c], dx: m_u[d], dy: m_w[d]);
    }

    // A region's plane: its normal, the normal's dot product with the region's first vertex, and the larger of a metre and
    // that vertex's largest coordinate, which scales the tolerance.
    private readonly record struct Plane3(Vector3 Normal, double Offset, double Scale);

    // A point's coordinates in the plane its normal faces: the normal's largest axis dropped and the other two in the order
    // whose cross product points along the normal, so a triangle facing along it winds counter-clockwise.
    private static (float U, float W) Plane(Vector3 normal, Vector3 position) {
        var (x, y, z) = (MathF.Abs(x: normal.X), MathF.Abs(x: normal.Y), MathF.Abs(x: normal.Z));

        if ((z >= x) && (z >= y)) { return ((normal.Z >= 0f) ? (position.X, position.Y) : (position.Y, position.X)); }
        if (x >= y) { return ((normal.X >= 0f) ? (position.Y, position.Z) : (position.Z, position.Y)); }

        return ((normal.Y >= 0f) ? (position.Z, position.X) : (position.X, position.Z));
    }
    private static double Largest(Vector3 point) => Math.Max(val1: Math.Abs(value: point.X), val2: Math.Max(val1: Math.Abs(value: point.Y), val2: Math.Abs(value: point.Z)));
    private static bool Same(Vector3 first, Vector3 second) =>
        (BitConverter.SingleToUInt32Bits(value: first.X) == BitConverter.SingleToUInt32Bits(value: second.X)) &&
        (BitConverter.SingleToUInt32Bits(value: first.Y) == BitConverter.SingleToUInt32Bits(value: second.Y)) &&
        (BitConverter.SingleToUInt32Bits(value: first.Z) == BitConverter.SingleToUInt32Bits(value: second.Z));

    // The unit roundoff of a double, and Shewchuk's first error bounds for the orientation and circle determinants
    // ("Adaptive Precision Floating-Point Arithmetic and Fast Robust Geometric Predicates", 1997).
    private const double Epsilon = 1.1102230246251565E-16;
    private const double OrientBound = ((3.0 + (16.0 * Epsilon)) * Epsilon);
    private const double CircleBound = ((10.0 + (96.0 * Epsilon)) * Epsilon);

    /// <summary>Returns the exact sign of the orientation of three points with float coordinates.</summary>
    /// <param name="ax">The first point's first coordinate.</param>
    /// <param name="ay">The first point's second coordinate.</param>
    /// <param name="bx">The second point's first coordinate.</param>
    /// <param name="by">The second point's second coordinate.</param>
    /// <param name="cx">The third point's first coordinate.</param>
    /// <param name="cy">The third point's second coordinate.</param>
    /// <returns>1 when the points wind counter-clockwise, -1 when they wind clockwise, 0 when they are collinear.</returns>
    public static int Orient(float ax, float ay, float bx, float by, float cx, float cy) {
        var left = (((double)ax - cx) * ((double)by - cy));
        var right = (((double)ay - cy) * ((double)bx - cx));
        var determinant = (left - right);
        double sum;

        // A product of two rounded differences keeps its exact sign, so a determinant whose terms cannot cancel is exact in
        // sign; one whose terms can is certain past the bound, and is otherwise decided exactly.
        if (left > 0.0) {
            if (right <= 0.0) { return Math.Sign(value: determinant); }
            sum = (left + right);
        } else if (left < 0.0) {
            if (right >= 0.0) { return Math.Sign(value: determinant); }
            sum = (-left - right);
        } else {
            return Math.Sign(value: determinant);
        }
        if (Math.Abs(value: determinant) >= (OrientBound * sum)) { return Math.Sign(value: determinant); }

        return ExactOrient(ax: ax, ay: ay, bx: bx, by: by, cx: cx, cy: cy);
    }
    // Whether d lies certainly inside the circle through a, b and c, which wind counter-clockwise; false where rounding
    // leaves it in doubt, cocircular points included.
    private static bool InCircle(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy) {
        var (adx, ady) = (((double)ax - dx), ((double)ay - dy));
        var (bdx, bdy) = (((double)bx - dx), ((double)by - dy));
        var (cdx, cdy) = (((double)cx - dx), ((double)cy - dy));
        var (bdxcdy, cdxbdy) = ((bdx * cdy), (cdx * bdy));
        var (cdxady, adxcdy) = ((cdx * ady), (adx * cdy));
        var (adxbdy, bdxady) = ((adx * bdy), (bdx * ady));
        var alift = ((adx * adx) + (ady * ady));
        var blift = ((bdx * bdx) + (bdy * bdy));
        var clift = ((cdx * cdx) + (cdy * cdy));
        var determinant = (((alift * (bdxcdy - cdxbdy)) + (blift * (cdxady - adxcdy))) + (clift * (adxbdy - bdxady)));
        var permanent = ((((Math.Abs(value: bdxcdy) + Math.Abs(value: cdxbdy)) * alift) + ((Math.Abs(value: cdxady) + Math.Abs(value: adxcdy)) * blift)) + ((Math.Abs(value: adxbdy) + Math.Abs(value: bdxady)) * clift));

        return (determinant > (CircleBound * permanent));
    }
    // The orientation's sign in whole numbers: each float a multiple of the six's smallest unit, in a 128-bit integer where
    // their span allows and an arbitrary one otherwise.
    private static int ExactOrient(float ax, float ay, float bx, float by, float cx, float cy) {
        Span<float> values = [ax, ay, bx, by, cx, cy];
        Span<long> mantissas = stackalloc long[6];
        Span<int> exponents = stackalloc int[6];
        var lowest = int.MaxValue;
        var highest = int.MinValue;

        for (var at = 0; (at < 6); at++) {
            (mantissas[at], exponents[at]) = Decompose(value: values[at]);
            if (mantissas[at] == 0) { continue; }
            lowest = Math.Min(val1: lowest, val2: exponents[at]);
            highest = Math.Max(val1: highest, val2: (exponents[at] + (64 - BitOperations.LeadingZeroCount(value: (ulong)Math.Abs(value: mantissas[at])))));
        }
        if (lowest == int.MaxValue) { return 0; }
        if ((highest - lowest) <= 61) {
            Span<Int128> scaled = stackalloc Int128[6];

            for (var at = 0; (at < 6); at++) {
                scaled[at] = (((Int128)mantissas[at]) << (exponents[at] - lowest));
            }

            return Int128.Sign(value: (((scaled[0] - scaled[4]) * (scaled[3] - scaled[5])) - ((scaled[1] - scaled[5]) * (scaled[2] - scaled[4]))));
        }

        var large = new BigInteger[6];

        for (var at = 0; (at < 6); at++) {
            large[at] = (new BigInteger(value: mantissas[at]) << (exponents[at] - lowest));
        }

        return (((large[0] - large[4]) * (large[3] - large[5])) - ((large[1] - large[5]) * (large[2] - large[4]))).Sign;
    }
    // A finite float as an odd signed whole number times a power of two, or zero.
    private static (long Mantissa, int Exponent) Decompose(float value) {
        var bits = BitConverter.SingleToUInt32Bits(value: value);
        var field = (int)((bits >> 23) & 0xFFu);
        long mantissa = (bits & 0x7FFFFFu);
        var exponent = -149;

        if (field != 0) {
            mantissa |= 0x800000L;
            exponent = (field - 150);
        }
        if (mantissa == 0) { return (0, 0); }

        var zeros = BitOperations.TrailingZeroCount(value: mantissa);

        return ((((bits >> 31) != 0u) ? -(mantissa >> zeros) : (mantissa >> zeros)), (exponent + zeros));
    }
}
