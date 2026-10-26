using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a merged mesh's coarser levels draw a subset of its own vertices; each level's recorded error holds every
/// vertex of the mesh within that distance of a triangle of its own material whose interpolated normal there is within the
/// level's angle; no level opens an edge, moves a corner or an open edge, or turns a triangle against its normals; and a
/// crease and a material's edge stay where their vertices were, moving only within the error.
/// </summary>
public sealed class SdfMeshCollapseLawTests {
    [Fact]
    public void IndependentlyLevelledChunksKeepEverySeamEdgeThroughTheStore() {
        var source = Sphere(cells: 24);
        var large = new SdfMesh(positions: source.Positions.ToArray().Select(static p => p * 12f).ToArray(), indices: source.Indices,
            normals: source.Normals, triangleMaterials: source.TriangleMaterials) { BakeCell = source.BakeCell * 12f };
        var mesh = SdfMeshChunks.Bake(large);
        Assert.True(mesh.Chunks.Count > 1);
        Assert.Contains(mesh.Chunks, static chunk => chunk.Levels.Count > 0);
        using var scratch = new Puck.Testing.TemporaryDirectory(prefix: "puck-chunk-seams-");
        var store = new SdfMeshStore(scratch.PathOf("meshes"));
        var geometry = new SdfMeshGeometry(Dynamic: false, Hash: 123);
        Assert.True(store.Write(7, geometry, mesh, [0]));
        Assert.True(store.TryRead(7, geometry, out var read, out _, out _));
        Assert.Equal(mesh.Chunks.Count, read.Chunks.Count);
        Assert.Equal(mesh.Indices.ToArray(), read.Indices.ToArray());
        for (var at = 0; at < mesh.Chunks.Count; at++) {
            var chunk = mesh.Chunks[at];
            var boundary = Boundary(chunk, chunk.Indices);
            Assert.Equal(chunk.Levels.Count, read.Chunks[at].Levels.Count);
            for (var level = 0; level < chunk.Levels.Count; level++) {
                Assert.True(boundary.SetEquals(Boundary(chunk, chunk.Levels[level].Indices)));
                Assert.Equal(chunk.Levels[level].Indices.ToArray(), read.Chunks[at].Levels[level].Indices.ToArray());
                Assert.Equal(chunk.Levels[level].Error, read.Chunks[at].Levels[level].Error);
            }
        }
    }

    private static HashSet<(Vector3, Vector3)> Boundary(SdfMesh mesh, ReadOnlyMemory<uint> indices) {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        for (var at = 0; at < indices.Length; at += 3) {
            for (var corner = 0; corner < 3; corner++) {
                var a = mesh.Positions.Span[(int)indices.Span[at + corner]];
                var b = mesh.Positions.Span[(int)indices.Span[at + (corner + 1) % 3]];
                var key = (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z)) < 0 ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        return edges.Where(static edge => edge.Value == 1).Select(static edge => edge.Key).ToHashSet();
    }
    [Fact]
    public void ACurvedSurfaceThinsWithinItsRecordedErrors() {
        var sphere = Sphere(cells: 24);
        var levelled = SdfMeshCollapse.Levels(mesh: sphere);

        Assert.Equal(expected: 2, actual: levelled.Levels.Count);
        Assert.True(condition: (levelled.Levels[0].TriangleCount < (sphere.TriangleCount / 4)));
        Assert.True(condition: (levelled.Levels[1].TriangleCount <= (SdfMeshCollapse.KeptShare * levelled.Levels[0].TriangleCount)));
        for (var level = 0; (level < levelled.Levels.Count); level++) {
            Holds(degrees: SdfMeshCollapse.NormalDegrees[level], level: levelled.Levels[level], mesh: sphere, share: SdfMeshCollapse.CellShares[level]);
        }

        Assert.True(condition: (levelled.Levels[1].Error >= levelled.Levels[0].Error));
    }
    [Fact]
    public void ACubesCreasesSlideAndItsCornersStay() {
        var cube = Cube(cells: 6);
        var levelled = SdfMeshCollapse.Levels(mesh: cube);
        var level = Assert.Single(collection: levelled.Levels);

        Holds(degrees: SdfMeshCollapse.NormalDegrees[0], level: level, mesh: cube, share: SdfMeshCollapse.CellShares[0]);
        // Its faces are flat and its edges straight, so the level loses nothing and keeps little more than its corners.
        Assert.True(condition: (level.Error < 1e-6f));
        Assert.True(condition: (level.TriangleCount <= 24));

        var kept = new HashSet<Vector3>(collection: level.Indices.ToArray().Select(selector: index => cube.Positions.Span[(int)index]));

        foreach (var corner in Corners()) {
            Assert.Contains(expected: corner, collection: kept);
        }
    }
    [Fact]
    public void AMaterialsEdgeAndAnOpenEdgeStay() {
        var plane = Plane(cells: 12);
        var levelled = SdfMeshCollapse.Levels(mesh: plane);

        Assert.NotEmpty(collection: levelled.Levels);
        for (var level = 0; (level < levelled.Levels.Count); level++) {
            Holds(degrees: SdfMeshCollapse.NormalDegrees[level], level: levelled.Levels[level], mesh: plane, share: SdfMeshCollapse.CellShares[level]);
        }

        // The open edge's vertices never move: every one is still a vertex of each level.
        var positions = plane.Positions.Span;

        foreach (var level in levelled.Levels) {
            var kept = new HashSet<Vector3>(collection: level.Indices.ToArray().Select(selector: index => plane.Positions.Span[(int)index]));

            foreach (var position in positions) {
                if ((MathF.Abs(x: position.X) == 1f) || (MathF.Abs(x: position.Y) == 1f)) {
                    Assert.Contains(expected: position, collection: kept);
                }
            }
        }
    }
    [Fact]
    public void AMeshNoLevelSavesOnReturnsItself() {
        // Without normals, a cell or a fourth triangle, with members, or where every place is a corner, nothing collapses.
        var sphere = Sphere(cells: 4);
        var tetrahedron = Tetrahedron();

        Assert.Same(expected: tetrahedron, actual: SdfMeshCollapse.Levels(mesh: tetrahedron));
        Assert.Empty(collection: SdfMeshCollapse.Levels(mesh: new SdfMesh(indices: sphere.Indices, positions: sphere.Positions) { BakeCell = sphere.BakeCell }).Levels);
        Assert.Empty(collection: SdfMeshCollapse.Levels(mesh: (sphere with { BakeCell = Vector3.Zero })).Levels);
        Assert.Empty(collection: SdfMeshCollapse.Levels(mesh: new SdfMesh(indices: sphere.Indices.Slice(length: 9, start: 0), normals: sphere.Normals, positions: sphere.Positions) { BakeCell = sphere.BakeCell }).Levels);
        Assert.Empty(collection: SdfMeshCollapse.Levels(mesh: new SdfMesh(indices: sphere.Indices, members: new SdfMeshMember[] { new(Orientation: Quaternion.Identity, Position: Vector3.Zero, SlotOffset: 1) }, normals: sphere.Normals, positions: sphere.Positions, vertexMembers: new byte[sphere.Positions.Length]) { BakeCell = sphere.BakeCell }).Levels);
        Assert.Throws<ArgumentNullException>(testCode: static () => SdfMeshCollapse.Levels(mesh: null!));
    }

    // A sphere of radius one, each face of a cube about it a grid of cells by cells whose vertices are pushed out onto it,
    // shared across the cube's edges, with its own normals.
    private static SdfMesh Sphere(int cells) {
        var builder = new Builder();

        foreach (var (origin, u, v) in Faces()) {
            builder.Grid(cells: cells, material: static (_, _) => 0u, normal: static p => Vector3.Normalize(value: p), point: (x, y) => Vector3.Normalize(value: (origin + ((((2f * x) / cells) * u) + (((2f * y) / cells) * v)))), shared: true);
        }

        return builder.Mesh(cell: (2f / cells));
    }
    // A cube of side two, each face a grid of its own vertices: the copies a crease split leaves along its edges.
    private static SdfMesh Cube(int cells) {
        var builder = new Builder();

        foreach (var (origin, u, v) in Faces()) {
            var normal = Vector3.Cross(vector1: u, vector2: v);

            builder.Grid(cells: cells, material: static (_, _) => 0u, normal: _ => normal, point: (x, y) => (origin + ((((2f * x) / cells) * u) + (((2f * y) / cells) * v))), shared: false);
        }

        return builder.Mesh(cell: (2f / cells));
    }
    // A square of side two facing +z, open along its edge, with a second material below a staircase across it.
    private static SdfMesh Plane(int cells) {
        var builder = new Builder();

        builder.Grid(cells: cells, material: (x, y) => ((y < ((x / 2) + 3)) ? 1u : 0u), normal: static _ => Vector3.UnitZ, point: (x, y) => new Vector3(x: (((2f * x) / cells) - 1f), y: (((2f * y) / cells) - 1f), z: 0f), shared: true, materials: true);

        return builder.Mesh(cell: (2f / cells));
    }
    // A tetrahedron whose every vertex is three copies, one a face.
    private static SdfMesh Tetrahedron() {
        Vector3[] corners = [new(x: 1f, y: 1f, z: 1f), new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: 1f, z: -1f), new(x: -1f, y: -1f, z: 1f)];
        int[][] faces = [[0, 1, 2], [0, 3, 1], [0, 2, 3], [1, 3, 2]];
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();

        foreach (var face in faces) {
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: (corners[face[1]] - corners[face[0]]), vector2: (corners[face[2]] - corners[face[0]])));

            foreach (var corner in face) {
                indices.Add(item: (uint)positions.Count);
                positions.Add(item: corners[corner]);
                normals.Add(item: normal);
            }
        }

        return new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray()) { BakeCell = Vector3.One };
    }
    private static (Vector3 Origin, Vector3 U, Vector3 V)[] Faces() => [
        (new(x: 1f, y: -1f, z: -1f), Vector3.UnitY, Vector3.UnitZ),
        (new(x: -1f, y: -1f, z: -1f), Vector3.UnitZ, Vector3.UnitY),
        (new(x: -1f, y: 1f, z: -1f), Vector3.UnitZ, Vector3.UnitX),
        (new(x: -1f, y: -1f, z: -1f), Vector3.UnitX, Vector3.UnitZ),
        (new(x: -1f, y: -1f, z: 1f), Vector3.UnitX, Vector3.UnitY),
        (new(x: -1f, y: -1f, z: -1f), Vector3.UnitY, Vector3.UnitX),
    ];
    private static IEnumerable<Vector3> Corners() =>
        from x in new[] { -1f, 1f }
        from y in new[] { -1f, 1f }
        from z in new[] { -1f, 1f }
        select new Vector3(x: x, y: y, z: z);

    // A level's whole contract against its mesh, measured by brute force: its error within the level's bound and at least
    // every vertex's distance to the level's nearest triangle of its material, some such triangle within the error and the
    // angle, no edge opened, and no triangle turned against its normals.
    private static void Holds(SdfMesh mesh, SdfMeshLevel level, float share, float degrees) {
        var cell = mesh.BakeCell.X;
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = level.Indices.Span;
        var cosine = Math.Cos(d: ((degrees * Math.PI) / 180.0));

        Assert.True(condition: (level.Error <= (share * cell)));
        Assert.Equal(expected: OpenEdges(positions: positions, indices: mesh.Indices.Span), actual: OpenEdges(positions: positions, indices: indices));
        for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
            var (a, b, c) = ((int)indices[(3 * triangle)], (int)indices[((3 * triangle) + 1)], (int)indices[((3 * triangle) + 2)]);

            Assert.True(condition: (Vector3.Dot(vector1: Vector3.Cross(vector1: (positions[b] - positions[a]), vector2: (positions[c] - positions[a])), vector2: ((normals[a] + normals[b]) + normals[c])) > 0f));
        }

        var materials = mesh.TriangleMaterials.Span;
        var levelMaterials = level.TriangleMaterials.Span;
        var own = mesh.Indices.Span;

        for (var corner = 0; (corner < own.Length); corner++) {
            var vertex = (int)own[corner];
            var material = (materials.IsEmpty ? 0u : materials[(corner / 3)]);
            var nearest = double.PositiveInfinity;
            var within = false;

            for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
                if ((levelMaterials.IsEmpty ? 0u : levelMaterials[triangle]) != material) { continue; }

                var (a, b, c) = ((int)indices[(3 * triangle)], (int)indices[((3 * triangle) + 1)], (int)indices[((3 * triangle) + 2)]);
                var (distance, weights) = Nearest(a: positions[a], b: positions[b], c: positions[c], p: positions[vertex]);
                var normal = Vector3.Normalize(value: (((weights.X * normals[a]) + (weights.Y * normals[b])) + (weights.Z * normals[c])));

                nearest = Math.Min(val1: nearest, val2: distance);
                within |= ((distance <= (level.Error + 1e-6)) && (Vector3.Dot(vector1: normal, vector2: Vector3.Normalize(value: normals[vertex])) >= (cosine - 1e-6)));
            }

            Assert.True(condition: (nearest <= (level.Error + 1e-6)), userMessage: $"vertex {vertex} lies {nearest} from the level, past its error {level.Error}");
            Assert.True(condition: within, userMessage: $"vertex {vertex} has no triangle of its material within the error and the angle");
        }

        // The other way: each level triangle's centroid and edge midpoints lie within the error of the mesh's own
        // triangles of its material.
        for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
            var (a, b, c) = (positions[(int)indices[(3 * triangle)]], positions[(int)indices[((3 * triangle) + 1)]], positions[(int)indices[((3 * triangle) + 2)]]);
            var material = (levelMaterials.IsEmpty ? 0u : levelMaterials[triangle]);

            foreach (var point in (ReadOnlySpan<Vector3>)[((a + b) + c) / 3f, (a + b) / 2f, (b + c) / 2f, (c + a) / 2f]) {
                var nearest = double.PositiveInfinity;

                for (var other = 0; (other < (own.Length / 3)); other++) {
                    if ((materials.IsEmpty ? 0u : materials[other]) == material) {
                        nearest = Math.Min(val1: nearest, val2: Nearest(a: positions[(int)own[(3 * other)]], b: positions[(int)own[((3 * other) + 1)]], c: positions[(int)own[((3 * other) + 2)]], p: point).Distance);
                    }
                }

                Assert.True(condition: (nearest <= (level.Error + 1e-6)), userMessage: $"triangle {triangle} strays {nearest} from the mesh, past its error {level.Error}");
            }
        }
    }
    private static int OpenEdges(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
        var edges = new Dictionary<(Vector3, Vector3), int>();

        for (var corner = 0; (corner < indices.Length); corner++) {
            var from = positions[(int)indices[corner]];
            var to = positions[(int)indices[(((corner % 3) == 2) ? (corner - 2) : (corner + 1))]];

            edges[(from, to)] = (edges.GetValueOrDefault(key: (from, to)) + 1);
        }

        return edges.Count(predicate: edge => (edges.GetValueOrDefault(key: (edge.Key.Item2, edge.Key.Item1)) != edge.Value));
    }
    // The distance from a point to a triangle and the barycentric weights of its nearest point.
    private static (double Distance, Vector3 Weights) Nearest(Vector3 a, Vector3 b, Vector3 c, Vector3 p) {
        var best = (Distance: double.PositiveInfinity, Weights: Vector3.Zero);

        // The plane's foot where it falls inside, else the nearest point of each edge.
        var normal = Vector3.Cross(vector1: (b - a), vector2: (c - a));
        var foot = (p - ((Vector3.Dot(vector1: (p - a), vector2: normal) / normal.LengthSquared()) * normal));
        var area = normal.Length();
        var u = (Vector3.Dot(vector1: Vector3.Cross(vector1: (c - b), vector2: (foot - b)), vector2: normal) / (area * area));
        var v = (Vector3.Dot(vector1: Vector3.Cross(vector1: (a - c), vector2: (foot - c)), vector2: normal) / (area * area));

        if ((u >= 0f) && (v >= 0f) && ((u + v) <= 1f)) {
            best = (Vector3.Distance(value1: foot, value2: p), new Vector3(x: u, y: v, z: ((1f - u) - v)));
        }

        foreach (var (from, to, weights) in (ReadOnlySpan<(Vector3, Vector3, Func<float, Vector3>)>)[(a, b, t => new Vector3(x: (1f - t), y: t, z: 0f)), (b, c, t => new Vector3(x: 0f, y: (1f - t), z: t)), (c, a, t => new Vector3(x: t, y: 0f, z: (1f - t)))]) {
            var along = Math.Clamp(value: (Vector3.Dot(vector1: (p - from), vector2: (to - from)) / (to - from).LengthSquared()), max: 1f, min: 0f);
            var distance = Vector3.Distance(value1: (from + (along * (to - from))), value2: p);

            if (distance < best.Distance) {
                best = (distance, weights(arg: along));
            }
        }

        return best;
    }

    // Builds a mesh grid by grid: a grid's vertices shared with an earlier grid's at the same position where `shared` asks.
    private sealed class Builder {
        private readonly List<Vector3> m_positions = [];
        private readonly List<Vector3> m_normals = [];
        private readonly List<uint> m_indices = [];
        private readonly List<uint> m_materials = [];
        private readonly Dictionary<Vector3, uint> m_shared = [];
        private bool m_carriesMaterials;

        public void Grid(int cells, Func<int, int, Vector3> point, Func<Vector3, Vector3> normal, Func<int, int, uint> material, bool shared, bool materials = false) {
            var at = new uint[(cells + 1), (cells + 1)];

            m_carriesMaterials |= materials;
            for (var y = 0; (y <= cells); y++) {
                for (var x = 0; (x <= cells); x++) {
                    var position = point(arg1: x, arg2: y);

                    if (!shared || !m_shared.TryGetValue(key: position, value: out at[x, y])) {
                        at[x, y] = (uint)m_positions.Count;
                        m_positions.Add(item: position);
                        m_normals.Add(item: normal(arg: position));
                        if (shared) { m_shared.Add(key: position, value: at[x, y]); }
                    }
                }
            }
            for (var y = 0; (y < cells); y++) {
                for (var x = 0; (x < cells); x++) {
                    m_indices.AddRange(collection: [at[x, y], at[(x + 1), y], at[(x + 1), (y + 1)], at[x, y], at[(x + 1), (y + 1)], at[x, (y + 1)]]);
                    m_materials.AddRange(collection: [material(arg1: x, arg2: y), material(arg1: x, arg2: y)]);
                }
            }
        }
        public SdfMesh Mesh(float cell) => new(indices: m_indices.ToArray(), normals: m_normals.ToArray(), positions: m_positions.ToArray(), triangleMaterials: (m_carriesMaterials ? m_materials.ToArray() : default)) { BakeCell = new Vector3(value: cell) };
    }
}
