using System.Numerics;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>
/// Places a contour's material boundaries on its surface (<see cref="SdfMeshPathBake"/>): the field's material is sampled
/// where a hit would read it, at points lifted a little off the surface, in rounds a bake dispatches as points lattices.
/// <para>The first round samples each quad's four quarter centres and each vertex. A quad whose vertices all report one
/// material keeps the quarter rule: one quad of that material when its quarters agree, its four quarters each of its own
/// material otherwise, which keeps a feature thinner than a cell that no vertex sees (a painted iris). A quad whose
/// vertices disagree is cut instead: each mesh edge of its two triangles whose ends disagree is searched by bisection over
/// <see cref="Rounds"/> more rounds for the point its material changes, and each triangle splits along the line between the
/// points on its edges, so a boundary follows its true place to a thirty-second of an edge rather than stepping in half
/// cells. An edge's point is shared by both triangles across it, so the boundary is one unbroken line.</para>
/// <para>A crease splits a vertex into copies at one place with their own normals (<see cref="SdfContour.SplitAtCreases"/>).
/// The copies are one site: the site's material is sampled once, along the normal of the vertex the contour placed, and an
/// edge between two sites is searched once, whichever copies its triangles name, so the faces either side of a crease cut
/// a boundary at one point and leave no crack between them.</para>
/// </summary>
internal sealed class SdfMeshMaterialCut {
    /// <summary>The bisection rounds after the first: each halves the interval a boundary lies in along its edge.</summary>
    public const int Rounds = 4;

    // The cosine past which a boundary point's two edge ends face apart as a crease's two sides do (thirty-two degrees, the
    // turn SdfContour.SplitAtCreases splits a crease at).
    private const float CreaseCosine = 0.848f;
    // The offset, in metres, outside the surface a material point lies, where a field hit would be accepted.
    private const float Lift = 0.0005f;
    // The bilinear coordinates of a quad's quarter centres, in the order the quarter rule reads their materials: a quad's
    // corners (a, b, c, d) sit at (0, 0), (1, 0), (1, 1) and (0, 1).
    private static readonly Vector2[] Quarters = [new(x: 0.25f, y: 0.25f), new(x: 0.75f, y: 0.25f), new(x: 0.75f, y: 0.75f), new(x: 0.25f, y: 0.75f)];

    private readonly SdfContour m_contour;
    // Each vertex's site: the first vertex at its place, which the crease copies appended after it share.
    private readonly int[] m_site;
    private int[] m_quarters = [];
    private int[] m_vertices = [];
    // The edges searched, keyed by their two sites (lower first), each with the interval its boundary lies in, as a
    // fraction of the way from the lower site, and the material of that site.
    private readonly Dictionary<(int Low, int High), int> m_edgeOf = [];
    private readonly List<(int Low, int High)> m_edges = [];
    private float[] m_from = [];
    private float[] m_to = [];
    private int m_round = -1;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshMaterialCut"/> class.</summary>
    /// <param name="contour">The contour, split at its creases, in the frame its group bakes in.</param>
    public SdfMeshMaterialCut(SdfContour contour) {
        var first = new Dictionary<Vector3, int>(capacity: contour.VertexCount);

        m_contour = contour;
        m_site = new int[contour.VertexCount];
        for (var vertex = 0; (vertex < m_site.Length); vertex++) {
            m_site[vertex] = (first.TryAdd(key: Position(vertex: vertex), value: vertex) ? vertex : first[Position(vertex: vertex)]);
        }
    }

    /// <summary>Gets the mesh edges the cut searches for a boundary.</summary>
    public int Edges => m_edges.Count;

    /// <summary>Returns the points the next round samples, in the frame the contour is in, or none once every round is
    /// answered.</summary>
    /// <returns>The points, each a little outside the surface.</returns>
    public Vector4[] Points() {
        if (m_round < 0) {
            var contour = m_contour;
            var points = new Vector4[((4 * contour.QuadCount) + contour.VertexCount)];

            for (var quad = 0; (quad < contour.QuadCount); quad++) {
                var corners = contour.Quads.AsSpan(length: 4, start: (4 * quad));

                for (var quarter = 0; (quarter < 4); quarter++) {
                    var at = Quarters[quarter];
                    var position = Bilinear(a: Position(vertex: corners[0]), b: Position(vertex: corners[1]), c: Position(vertex: corners[2]), d: Position(vertex: corners[3]), at: at);
                    var normal = Bilinear(a: Normal(vertex: corners[0]), b: Normal(vertex: corners[1]), c: Normal(vertex: corners[2]), d: Normal(vertex: corners[3]), at: at);

                    points[((4 * quad) + quarter)] = Lifted(normal: normal, position: position);
                }
            }
            for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
                points[((4 * contour.QuadCount) + vertex)] = Lifted(normal: Normal(vertex: m_site[vertex]), position: Position(vertex: vertex));
            }

            return points;
        }
        if ((m_round >= Rounds) || (m_edges.Count == 0)) {
            return [];
        }

        var middles = new Vector4[m_edges.Count];

        for (var edge = 0; (edge < m_edges.Count); edge++) {
            var (position, normal) = Along(edge: edge, t: (0.5f * (m_from[edge] + m_to[edge])));

            middles[edge] = Lifted(normal: normal, position: position);
        }

        return middles;
    }
    /// <summary>Answers the round <see cref="Points"/> last returned.</summary>
    /// <param name="materials">One material per point, in the points' order.</param>
    public void Answer(ReadOnlySpan<int> materials) {
        if (m_round < 0) {
            AnswerFirst(materials: materials);
        } else {
            for (var edge = 0; (edge < m_edges.Count); edge++) {
                var middle = (0.5f * (m_from[edge] + m_to[edge]));

                if (materials[edge] == m_vertices[m_edges[edge].Low]) {
                    m_from[edge] = middle;
                } else {
                    m_to[edge] = middle;
                }
            }
        }
        m_round++;
    }
    // The first round's answer: each quarter's and vertex's material, and the edges whose ends disagree.
    private void AnswerFirst(ReadOnlySpan<int> materials) {
        var contour = m_contour;
        var quadCount = contour.QuadCount;

        m_quarters = materials[..(4 * quadCount)].ToArray();
        m_vertices = new int[contour.VertexCount];
        for (var vertex = 0; (vertex < m_vertices.Length); vertex++) {
            m_vertices[vertex] = materials[((4 * quadCount) + m_site[vertex])];
        }

        Span<int> triangle = stackalloc int[3];

        for (var quad = 0; (quad < quadCount); quad++) {
            if (!Mixed(quad: quad)) {
                continue;
            }

            for (var half = 0; (half < Halves(quad: quad)); half++) {
                Half(half: half, quad: quad, triangle: triangle);
                for (var side = 0; (side < 3); side++) {
                    var (a, b) = (triangle[side], triangle[((side + 1) % 3)]);

                    if ((m_vertices[a] != m_vertices[b]) && m_edgeOf.TryAdd(key: Key(a: m_site[a], b: m_site[b]), value: m_edges.Count)) {
                        m_edges.Add(item: Key(a: m_site[a], b: m_site[b]));
                    }
                }
            }
        }

        m_from = new float[m_edges.Count];
        m_to = new float[m_edges.Count];
        Array.Fill(array: m_to, value: 1f);
    }
    /// <summary>Builds the mesh once every round is answered: each quad by the quarter rule, or cut along its boundary
    /// where its vertices disagree.</summary>
    /// <returns>The mesh, in the frame the contour is in; <see langword="null"/> for a contour with no quad.</returns>
    public SdfMesh? Build() {
        var contour = m_contour;

        if (contour.QuadCount == 0) { return null; }

        var positions = new List<Vector3>(capacity: (contour.VertexCount + m_edges.Count));
        var normals = new List<Vector3>(capacity: (contour.VertexCount + m_edges.Count));
        var indices = new List<uint>(capacity: (6 * contour.QuadCount));
        var triangleMaterials = new List<uint>(capacity: (2 * contour.QuadCount));

        uint Add(Vector3 point, Vector3 normal) {
            var length = normal.Length();

            positions.Add(item: point);
            normals.Add(item: ((length > 0f) ? (normal / length) : normal));

            return (uint)(positions.Count - 1);
        }
        // A triangle, dropping one with no area, as a contour's triangle (its last two vertices one) has.
        void Triangle(uint a, uint b, uint c, int material) {
            if (Vector3.Cross(vector1: (positions[(int)b] - positions[(int)a]), vector2: (positions[(int)c] - positions[(int)a])).LengthSquared() <= 0f) { return; }
            indices.Add(item: a); indices.Add(item: b); indices.Add(item: c);
            triangleMaterials.Add(item: (uint)Math.Max(val1: 0, val2: material));
        }
        void Quad(uint a, uint b, uint c, uint d, int material) {
            Triangle(a: a, b: b, c: c, material: material);
            Triangle(a: a, b: c, c: d, material: material);
        }

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            _ = Add(normal: Normal(vertex: vertex), point: Position(vertex: vertex));
        }

        // Each searched edge's boundary point, between the last interval's ends, as a vertex for the pieces it bounds, its
        // normal blended between the vertices of the triangle that names it. Where those two normals turn apart past
        // CreaseCosine the boundary runs along a crease (a raised plate's foot, a bezel's rim), and each piece takes its own
        // end's normal there: a normal blended across the crease shades the piece on the face as a spike of the face beyond
        // it. Every vertex of one edge lies at its one point.
        var boundary = new Dictionary<(int Edge, int Low, int High, int Side), uint>();

        // The boundary vertex of the edge between `a` and `b` for the piece on `a`'s side.
        uint On(int a, int b) {
            var edge = m_edgeOf[Key(a: m_site[a], b: m_site[b])];
            var middle = (0.5f * (m_from[edge] + m_to[edge]));
            var (aNormal, bNormal) = (Vector3.Normalize(value: Normal(vertex: a)), Vector3.Normalize(value: Normal(vertex: b)));
            var creased = (Vector3.Dot(vector1: aNormal, vector2: bNormal) < CreaseCosine);
            var key = (edge, Math.Min(val1: a, val2: b), Math.Max(val1: a, val2: b), (creased ? a : -1));

            if (!boundary.TryGetValue(key: key, value: out var vertex)) {
                var fromA = ((m_site[a] == m_edges[edge].Low) ? middle : (1f - middle));

                vertex = Add(normal: (creased ? aNormal : Vector3.Lerp(value1: Normal(vertex: a), value2: Normal(vertex: b), amount: fromA)), point: Along(edge: edge, t: middle).Position);
                boundary.Add(key: key, value: vertex);
            }

            return vertex;
        }

        Span<int> triangle = stackalloc int[3];

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var corners = contour.Quads.AsSpan(length: 4, start: (4 * quad));

            if (Mixed(quad: quad)) {
                for (var half = 0; (half < Halves(quad: quad)); half++) {
                    Half(half: half, quad: quad, triangle: triangle);

                    var (v0, v1, v2) = (triangle[0], triangle[1], triangle[2]);
                    var (m0, m1, m2) = (m_vertices[v0], m_vertices[v1], m_vertices[v2]);

                    if ((m0 == m1) && (m1 == m2)) {
                        Triangle(a: (uint)v0, b: (uint)v1, c: (uint)v2, material: m0);
                    } else if ((m0 != m1) && (m1 != m2) && (m2 != m0)) {
                        // Three materials: a quad of each about the point between the three edges' boundary points.
                        var centre = Add(
                            normal: ((normals[(int)On(a: v0, b: v1)] + normals[(int)On(a: v1, b: v2)]) + normals[(int)On(a: v2, b: v0)]),
                            point: (((positions[(int)On(a: v0, b: v1)] + positions[(int)On(a: v1, b: v2)]) + positions[(int)On(a: v2, b: v0)]) / 3f)
                        );

                        Quad(a: (uint)v0, b: On(a: v0, b: v1), c: centre, d: On(a: v0, b: v2), material: m0);
                        Quad(a: (uint)v1, b: On(a: v1, b: v2), c: centre, d: On(a: v1, b: v0), material: m1);
                        Quad(a: (uint)v2, b: On(a: v2, b: v0), c: centre, d: On(a: v2, b: v1), material: m2);
                    } else {
                        // Two materials: the corner on its own is cut off by the line between its edges' boundary points.
                        var (odd, s1, s2) = ((m1 == m2) ? (v0, v1, v2) : ((m0 == m2) ? (v1, v2, v0) : (v2, v0, v1)));
                        Triangle(a: (uint)odd, b: On(a: odd, b: s1), c: On(a: odd, b: s2), material: m_vertices[odd]);
                        Quad(a: On(a: s1, b: odd), b: (uint)s1, c: (uint)s2, d: On(a: s2, b: odd), material: m_vertices[s1]);
                    }
                }

                continue;
            }

            var quarter = m_quarters.AsSpan(length: 4, start: (4 * quad));

            if ((quarter[0] == quarter[1]) && (quarter[0] == quarter[2]) && (quarter[0] == quarter[3])) {
                Quad(a: (uint)corners[0], b: (uint)corners[1], c: (uint)corners[2], d: (uint)corners[3], material: quarter[0]);
                continue;
            }

            var (p0, p1, p2, p3) = (Position(vertex: corners[0]), Position(vertex: corners[1]), Position(vertex: corners[2]), Position(vertex: corners[3]));
            var (n0, n1, n2, n3) = (Normal(vertex: corners[0]), Normal(vertex: corners[1]), Normal(vertex: corners[2]), Normal(vertex: corners[3]));

            uint At(Vector2 at) => Add(normal: Bilinear(a: n0, b: n1, c: n2, d: n3, at: at), point: Bilinear(a: p0, b: p1, c: p2, d: p3, at: at));

            var ab = At(at: new Vector2(x: 0.5f, y: 0f));
            var bc = At(at: new Vector2(x: 1f, y: 0.5f));
            var cd = At(at: new Vector2(x: 0.5f, y: 1f));
            var da = At(at: new Vector2(x: 0f, y: 0.5f));
            var center = At(at: new Vector2(x: 0.5f, y: 0.5f));

            Quad(a: (uint)corners[0], b: ab, c: center, d: da, material: quarter[0]);
            Quad(a: ab, b: (uint)corners[1], c: bc, d: center, material: quarter[1]);
            Quad(a: center, b: bc, c: (uint)corners[2], d: cd, material: quarter[2]);
            Quad(a: da, b: center, c: cd, d: (uint)corners[3], material: quarter[3]);
        }

        return new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray(), triangleMaterials: triangleMaterials.ToArray());
    }

    // Whether a quad's vertices report more than one material.
    private bool Mixed(int quad) {
        var corners = m_contour.Quads.AsSpan(length: 4, start: (4 * quad));
        var material = m_vertices[corners[0]];

        return ((m_vertices[corners[1]] != material) || (m_vertices[corners[2]] != material) || (m_vertices[corners[3]] != material));
    }
    // The triangles a quad meshes as: two along its first diagonal, or one where its last two vertices are one.
    private int Halves(int quad) => ((m_contour.Quads[((4 * quad) + 2)] == m_contour.Quads[((4 * quad) + 3)]) ? 1 : 2);
    private void Half(int quad, int half, Span<int> triangle) {
        var corners = m_contour.Quads.AsSpan(length: 4, start: (4 * quad));

        triangle[0] = corners[0];
        triangle[1] = corners[(1 + half)];
        triangle[2] = corners[(2 + half)];
    }
    private static (int Low, int High) Key(int a, int b) => ((a < b) ? (a, b) : (b, a));
    // The point a fraction `t` along a searched edge from its lower vertex, and the normal there.
    private (Vector3 Position, Vector3 Normal) Along(int edge, float t) {
        var (low, high) = m_edges[edge];

        return (Vector3.Lerp(value1: Position(vertex: low), value2: Position(vertex: high), amount: t), Vector3.Lerp(value1: Normal(vertex: low), value2: Normal(vertex: high), amount: t));
    }
    private Vector3 Position(int vertex) => new(x: m_contour.Positions[(3 * vertex)], y: m_contour.Positions[((3 * vertex) + 1)], z: m_contour.Positions[((3 * vertex) + 2)]);
    private Vector3 Normal(int vertex) => new(x: m_contour.Normals[(3 * vertex)], y: m_contour.Normals[((3 * vertex) + 1)], z: m_contour.Normals[((3 * vertex) + 2)]);
    private static Vector3 Bilinear(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 at) =>
        Vector3.Lerp(value1: Vector3.Lerp(value1: a, value2: b, amount: at.X), value2: Vector3.Lerp(value1: d, value2: c, amount: at.X), amount: at.Y);
    // A surface point lifted along its normal to where a field hit would read its material.
    private static Vector4 Lifted(Vector3 position, Vector3 normal) {
        var length = normal.Length();

        return new Vector4(value: (position + ((length > 0f) ? (normal * (Lift / length)) : Vector3.Zero)), w: 1f);
    }
}
