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
    public void AGlyphCounterKeepsItsOutlineWithinATightBudgetWhileItsFacesSimplify() {
        const int Cells = 16;
        var builder = new Builder();
        var circle = Enumerable.Range(count: Cells, start: 0).Select(selector: i => new Vector2(x: MathF.Cos(x: ((i * MathF.Tau) / Cells)), y: MathF.Sin(x: ((i * MathF.Tau) / Cells)))).ToArray();

        Vector3 Point(float radius, int angle, float z) => new(value: (circle[(angle % Cells)] * radius), z: z);
        // An extruded O with one material: its front/back and walls share positions but have split normals.
        // The counter must survive even when text and the surface it carves share their material.
        builder.Grid(Cells, (x, y) => Point(angle: y, radius: (0.5f + ((0.5f * x) / Cells)), z: 0.1f), static _ => Vector3.UnitZ, static (_, _) => 0u, false);
        builder.Grid(Cells, (x, y) => Point(angle: y, radius: (1f - ((0.5f * x) / Cells)), z: -0.1f), static _ => -Vector3.UnitZ, static (_, _) => 0u, false);
        builder.Grid(Cells, (x, y) => Point(angle: x, radius: 1f, z: (-0.1f + ((0.2f * y) / Cells))), static p => Vector3.Normalize(value: new Vector3(x: p.X, y: p.Y, z: 0f)), static (_, _) => 0u, false);
        builder.Grid(Cells, (x, y) => Point(angle: x, radius: 0.5f, z: (0.1f - ((0.2f * y) / Cells))), static p => -Vector3.Normalize(value: new Vector3(x: p.X, y: p.Y, z: 0f)), static (_, _) => 0u, false);
        var ring = builder.Mesh(cell: 0.1f);
        var expected = FeatureEdges(ring, ring.Indices);

        Assert.NotEmpty(collection: expected);
        var levelled = SdfMeshCollapse.Levels(ring, preserveFeatures: true);

        Assert.NotEmpty(collection: levelled.Levels);
        Assert.True(condition: (levelled.Levels[0].TriangleCount < (ring.TriangleCount / 2)));
        var ringPositions = ring.Positions.Span;

        for (var number = 1; (number <= levelled.Levels.Count); number++) {
            var levelIndices = levelled.LevelIndices(level: number);
            var actual = FeatureEdges(ring, levelIndices);
            var corners = levelIndices.Span;

            CheckLines(budget: (0.1 / 8), source: expected, target: actual);
            CheckLines(budget: (0.1 / 8), source: actual, target: expected);
            Assert.Equal(0, OpenEdges(ringPositions, corners));
            // No projected triangle may cover the counter's centre, independently of the outline-distance check.
            for (var at = 0; (at < corners.Length); at += 3) {
                var a = ringPositions[((int)corners[at])];
                var b = ringPositions[((int)corners[(at + 1)])];
                var c = ringPositions[((int)corners[(at + 2)])];
                var ab = ((a.X * b.Y) - (a.Y * b.X));
                var bc = ((b.X * c.Y) - (b.Y * c.X));
                var ca = ((c.X * a.Y) - (c.Y * a.X));

                Assert.False(condition: (((ab > 1e-6f) && (bc > 1e-6f) && (ca > 1e-6f)) || ((ab < -1e-6f) && (bc < -1e-6f) && (ca < -1e-6f))));
            }
        }
        // No detectable outline gives the glyph mode no evidence of a safe counter boundary.
        var smooth = Sphere(cells: 8);

        Assert.Same(smooth, SdfMeshCollapse.Levels(smooth, preserveFeatures: true));
    }

    private static void CheckLines(HashSet<(Vector3, Vector3)> source, HashSet<(Vector3, Vector3)> target, double budget) {
        foreach (var (a, b) in source) {
            foreach (var point in new[] { a, b, ((a + b) * 0.5f) }) {
                var nearest = float.PositiveInfinity;

                foreach (var (c, d) in target) {
                    var direction = (d - c);
                    var along = Math.Clamp((Vector3.Dot(vector1: (point - c), vector2: direction) / direction.LengthSquared()), 0f, 1f);

                    nearest = MathF.Min(x: nearest, y: Vector3.Distance(value1: point, value2: (c + (along * direction))));
                }
                Assert.InRange(actual: nearest, high: (budget + 1e-6), low: 0);
            }
        }
    }
    private static HashSet<(Vector3, Vector3)> FeatureEdges(SdfMesh mesh, ReadOnlyMemory<uint> indices) {
        var edges = new Dictionary<(Vector3, Vector3), HashSet<(uint, uint)>>();

        for (var at = 0; (at < indices.Length); at += 3) {
            for (var corner = 0; (corner < 3); corner++) {
                var a = indices.Span[(at + corner)];
                var b = indices.Span[(at + ((corner + 1) % 3))];
                var pa = mesh.Positions.Span[((int)a)];
                var pb = mesh.Positions.Span[((int)b)];

                if ((pa.X, pa.Y, pa.Z).CompareTo(other: (pb.X, pb.Y, pb.Z)) > 0) { (pa, pb) = (pb, pa); (a, b) = (b, a); }
                if (!edges.TryGetValue(key: (pa, pb), value: out var copies)) { copies = []; edges.Add(key: (pa, pb), value: copies); }
                copies.Add(item: (a, b));
            }
        }
        return edges.Where(predicate: static edge => (edge.Value.Count > 1)).Select(selector: static edge => edge.Key).ToHashSet();
    }

    [Fact]
    public void IndependentlyLevelledChunksKeepEverySeamEdgeThroughTheStore() {
        var source = Sphere(cells: 24);
        var large = new SdfMesh(positions: source.Positions.ToArray().Select(selector: static p => (p * 12f)).ToArray(), indices: source.Indices,
            normals: source.Normals, triangleMaterials: source.TriangleMaterials) { BakeCell = (source.BakeCell * 12f) };
        var mesh = SdfMeshChunks.Bake(large);

        Assert.True(condition: (mesh.Chunks.Count > 1));
        Assert.Contains(collection: mesh.Chunks, filter: static chunk => (chunk.Levels.Count > 0));
        var uncollapsed = SdfMeshChunks.Bake(large, preserveFeatures: true);

        Assert.Equal(mesh.Chunks.Count, uncollapsed.Chunks.Count);
        for (var at = 0; (at < mesh.Chunks.Count); at++) {
            var levelled = mesh.Chunks[at];
            var retained = uncollapsed.Chunks[at];

            Assert.Equal(levelled.Positions.ToArray(), retained.Positions.ToArray());
            Assert.Equal(levelled.Normals.ToArray(), retained.Normals.ToArray());
            Assert.Equal(levelled.Indices.ToArray(), retained.Indices.ToArray());
            Assert.Equal(levelled.TriangleMaterials.ToArray(), retained.TriangleMaterials.ToArray());
        }
        using var scratch = new Puck.Testing.TemporaryDirectory(prefix: "puck-chunk-seams-");
        var store = new SdfMeshStore(scratch.PathOf(name: "meshes"));
        var geometry = new SdfMeshGeometry(Dynamic: false, Hash: 123);

        Assert.True(condition: store.Write(geometry: geometry, identity: 7, materials: [0], mesh: mesh));
        Assert.True(condition: store.TryRead(geometry: geometry, identity: 7, materials: out _, mesh: out var read, refusal: out _));
        Assert.NotNull(@object: read);
        Assert.Equal(mesh.Chunks.Count, read.Chunks.Count);
        Assert.Equal(mesh.Indices.ToArray(), read.Indices.ToArray());
        for (var at = 0; (at < mesh.Chunks.Count); at++) {
            var chunk = mesh.Chunks[at];
            var boundary = Boundary(chunk, chunk.Indices);

            Assert.Equal(chunk.Levels.Count, read.Chunks[at].Levels.Count);
            for (var level = 0; (level < chunk.Levels.Count); level++) {
                Assert.True(condition: boundary.SetEquals(other: Boundary(chunk, chunk.LevelIndices(level: (level + 1)))));
                Assert.Equal(chunk.LevelIndices(level: (level + 1)).ToArray(), read.Chunks[at].LevelIndices(level: (level + 1)).ToArray());
                Assert.Equal(chunk.Levels[level].Error, read.Chunks[at].Levels[level].Error);
            }
        }
    }

    private static HashSet<(Vector3, Vector3)> Boundary(SdfMesh mesh, ReadOnlyMemory<uint> indices) {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        var positions = mesh.Positions.Span;

        for (var at = 0; (at < indices.Length); at += 3) {
            for (var corner = 0; (corner < 3); corner++) {
                var a = positions[((int)indices.Span[(at + corner)])];
                var b = positions[((int)indices.Span[(at + ((corner + 1) % 3))])];
                var key = (((a.X, a.Y, a.Z).CompareTo(other: (b.X, b.Y, b.Z)) < 0) ? (a, b) : (b, a));

                edges[key] = (edges.GetValueOrDefault(key: key) + 1);
            }
        }
        return edges.Where(predicate: static edge => (edge.Value == 1)).Select(selector: static edge => edge.Key).ToHashSet();
    }

    [Fact]
    public void ACurvedSurfaceThinsWithinItsRecordedErrors() {
        var sphere = Sphere(cells: 24);
        var levelled = SdfMeshCollapse.Levels(mesh: sphere);
        var retained = SdfMeshChunks.Bake(sphere, preserveFeatures: true);
        var merged = SdfMeshPlanarMerge.Merge(mesh: sphere);

        Assert.Empty(collection: retained.Levels);
        Assert.Equal(merged.Positions.ToArray(), retained.Positions.ToArray());
        Assert.Equal(merged.Indices.ToArray(), retained.Indices.ToArray());

        Assert.True(condition: (levelled.Levels.Count >= 2));
        Assert.True(condition: (levelled.Levels[0].TriangleCount < (sphere.TriangleCount / 4)));
        for (var level = 1; (level < levelled.Levels.Count); level++) {
            Assert.True(condition: (levelled.Levels[level].TriangleCount <= (SdfMeshCollapse.KeptShare * levelled.Levels[(level - 1)].TriangleCount)));
            Assert.True(condition: (levelled.Levels[level].Error >= levelled.Levels[(level - 1)].Error));
        }
        HoldsChain(levelled: levelled, mesh: sphere);
    }
    [Fact]
    public void ACubesCreasesSlideAndItsCornersStayUntilALevelsErrorSpansItsFaces() {
        var cube = Cube(cells: 6);
        var levelled = SdfMeshCollapse.Levels(mesh: cube);
        var level = levelled.Levels[0];

        Holds(degrees: SdfMeshCollapse.NormalDegrees[0], number: 1, levelled: levelled, mesh: cube, share: SdfMeshCollapse.CellShares[0]);
        // Its faces are flat and its edges straight, so the first level loses nothing and keeps little more than its corners.
        Assert.True(condition: (level.Error < 1e-6f));
        Assert.True(condition: (level.TriangleCount <= 24));

        var levelledPositions = levelled.Positions;
        var kept = new HashSet<Vector3>(collection: levelled.LevelIndices(level: 1).ToArray().Select(selector: index => levelledPositions.Span[((int)index)]));

        foreach (var corner in Corners()) {
            Assert.Contains(collection: kept, expected: corner);
        }
        // A coarser level, whose error spans the faces, may close a corner within it; so may a tetrahedron's.
        HoldsChain(levelled: levelled, mesh: cube);
        var tetrahedron = Tetrahedron();

        HoldsChain(levelled: SdfMeshCollapse.Levels(mesh: tetrahedron), mesh: tetrahedron);
    }
    [Fact]
    public void ALargeMeshsClustersPartitionItsClusteredLevelsAndKeepTheirSeams() {
        var sphere = Sphere(cells: 80);
        var large = new SdfMesh(indices: sphere.Indices, normals: sphere.Normals, positions: sphere.Positions.ToArray().Select(selector: static point => (3f * point)).ToArray()) { BakeCell = (3f * sphere.BakeCell) };
        var levelled = SdfMeshCollapse.Levels(mesh: large);
        var clusters = Assert.IsType<SdfMeshClusters>(@object: levelled.Clusters);
        var positions = levelled.Positions.Span;

        Assert.InRange(actual: clusters.Count, low: 2, high: SdfMeshClusters.MostClusters);
        Assert.InRange(actual: clusters.Levels, low: 2, high: Math.Min(val1: SdfMeshClusters.MostLevels, val2: (levelled.Levels.Count + 1)));
        HoldsChain(levelled: levelled, mesh: large);

        // Each clustered level's triangles are its clusters' runs, each run within its cluster's box; and every position
        // two clusters share at level zero is a vertex of both at every clustered level.
        HashSet<Vector3>[] Runs(int level) {
            var indices = levelled.LevelIndices(level: level).Span;
            var sets = new HashSet<Vector3>[clusters.Count];
            var first = 0;

            Assert.Equal(expected: (indices.Length / 3), actual: Enumerable.Range(start: 0, count: clusters.Count).Sum(selector: cluster => clusters.Triangles(cluster: cluster, level: level)));
            for (var cluster = 0; (cluster < clusters.Count); cluster++) {
                sets[cluster] = [];
                Assert.Equal(expected: first, actual: clusters.FirstTriangle(cluster: cluster, level: level));
                for (var corner = (3 * first); (corner < (3 * (first + clusters.Triangles(cluster: cluster, level: level)))); corner++) {
                    var point = levelled.Positions.Span[((int)indices[corner])];

                    Assert.True(condition: (Vector3.Min(value1: point, value2: clusters.Low(cluster: cluster)) == clusters.Low(cluster: cluster)) && (Vector3.Max(value1: point, value2: clusters.High(cluster: cluster)) == clusters.High(cluster: cluster)));
                    sets[cluster].Add(item: point);
                }
                first += clusters.Triangles(cluster: cluster, level: level);
            }
            return sets;
        }
        var own = Runs(level: 0);

        for (var level = 1; (level < clusters.Levels); level++) {
            var sets = Runs(level: level);

            for (var a = 0; (a < clusters.Count); a++) {
                for (var b = (a + 1); (b < clusters.Count); b++) {
                    foreach (var seam in own[a].Intersect(second: own[b])) {
                        Assert.Contains(collection: sets[a], expected: seam);
                        Assert.Contains(collection: sets[b], expected: seam);
                    }
                }
            }
        }
        Assert.True(condition: (positions.Length >= large.VertexCount));
    }
    [Fact]
    public void AMaterialsEdgeAndAnOpenEdgeStay() {
        var plane = Plane(cells: 12);
        var levelled = SdfMeshCollapse.Levels(mesh: plane);

        Assert.NotEmpty(collection: levelled.Levels);
        HoldsChain(levelled: levelled, mesh: plane);

        // The open edge's vertices never move: every one is still a vertex of each level.
        var planePositions = plane.Positions;

        for (var level = 1; (level <= levelled.Levels.Count); level++) {
            var kept = new HashSet<Vector3>(collection: levelled.LevelIndices(level: level).ToArray().Select(selector: index => planePositions.Span[((int)index)]));

            foreach (var position in planePositions.Span) {
                if ((MathF.Abs(x: position.X) == 1f) || (MathF.Abs(x: position.Y) == 1f)) {
                    Assert.Contains(collection: kept, expected: position);
                }
            }
        }
    }
    [Fact]
    public void AMeshNoLevelSavesOnReturnsItself() {
        // Without normals, a cell or a fourth triangle, or with members, nothing collapses.
        var sphere = Sphere(cells: 4);

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
                indices.Add(item: ((uint)positions.Count));
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
    // Each level of a chain holds the contract of one run of the collapse, the runs in order: a run that saves too little
    // keeps no level (SdfMeshCollapse.KeptShare), so the n-th level is the n-th run's or a later one's.
    private static void HoldsChain(SdfMesh mesh, SdfMesh levelled) {
        var run = 0;

        for (var number = 1; (number <= levelled.Levels.Count); number++) {
            string? violation = null;

            for (; (run < SdfMeshCollapse.CellShares.Length); run++) {
                violation = Violation(degrees: SdfMeshCollapse.NormalDegrees[run], levelled: levelled, mesh: mesh, number: number, share: SdfMeshCollapse.CellShares[run]);
                if (violation is null) { break; }
            }

            Assert.True(condition: (run < SdfMeshCollapse.CellShares.Length), userMessage: $"level {number} holds no run's bounds after the finer level's: {violation}");
            run++;
        }
    }
    // A level's whole contract against its mesh, measured by brute force: its error within the level's bound and at least
    // every vertex's distance to the level's nearest triangle of its material, some such triangle within the error and the
    // angle (the angle waived for a vertex within the error of a sharp turn, SdfMeshCollapse.SharpDegrees, measured here
    // in a straight line, which is never farther than along the mesh, so the check is looser than the collapse's), no edge
    // opened, and no triangle turned against its normals. Every level vertex, made ones included, sits at one of the mesh's
    // own positions. Fails the test at the first breach.
    private static void Holds(SdfMesh mesh, SdfMesh levelled, int number, float share, float degrees) =>
        Assert.Null(@object: Violation(degrees: degrees, levelled: levelled, mesh: mesh, number: number, share: share));
    // The first breach of a level's contract (Holds), or null where it holds.
    private static string? Violation(SdfMesh mesh, SdfMesh levelled, int number, float share, float degrees) {
        var level = levelled.Levels[(number - 1)];
        var cell = mesh.BakeCell.X;
        var ownPositions = mesh.Positions.Span;
        var positions = levelled.Positions.Span;
        var normals = levelled.Normals.Span;
        var indices = levelled.LevelIndices(level: number).Span;
        var cosine = Math.Cos(d: ((degrees * Math.PI) / 180.0));
        var places = new HashSet<Vector3>(collection: ownPositions.ToArray());

        if (level.Error > (share * cell)) { return $"error {level.Error} past the bound {share * cell}"; }
        foreach (var index in indices) {
            if (!places.Contains(item: positions[((int)index)])) { return $"vertex {index} sits at none of the mesh's own positions"; }
        }
        if (OpenEdges(positions: positions, indices: mesh.Indices.Span) != OpenEdges(indices: indices, positions: positions)) { return "an edge opened"; }
        for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
            var (a, b, c) = (((int)indices[(3 * triangle)]), ((int)indices[((3 * triangle) + 1)]), ((int)indices[((3 * triangle) + 2)]));

            if (!(Vector3.Dot(vector1: Vector3.Cross(vector1: (positions[b] - positions[a]), vector2: (positions[c] - positions[a])), vector2: ((normals[a] + normals[b]) + normals[c])) > 0f)) { return $"triangle {triangle} turned against its normals"; }
        }

        var materials = mesh.TriangleMaterials.Span;
        var levelMaterials = levelled.LevelMaterials(level: number).Span;
        var own = mesh.Indices.Span;
        var sharp = Sharp(mesh: mesh);

        for (var corner = 0; (corner < own.Length); corner++) {
            var vertex = ((int)own[corner]);
            var material = (materials.IsEmpty ? 0u : materials[(corner / 3)]);
            var nearest = double.PositiveInfinity;
            var sample = positions[vertex];
            var error = level.Error;
            var waived = sharp.Any(predicate: point => (Vector3.Distance(value1: point, value2: sample) < error));
            var within = false;

            for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
                if ((levelMaterials.IsEmpty ? 0u : levelMaterials[triangle]) != material) { continue; }

                var (a, b, c) = (((int)indices[(3 * triangle)]), ((int)indices[((3 * triangle) + 1)]), ((int)indices[((3 * triangle) + 2)]));
                var (distance, weights) = Nearest(a: positions[a], b: positions[b], c: positions[c], p: positions[vertex]);
                var normal = Vector3.Normalize(value: (((weights.X * normals[a]) + (weights.Y * normals[b])) + (weights.Z * normals[c])));

                nearest = Math.Min(val1: nearest, val2: distance);
                within |= ((distance <= (level.Error + 1e-6)) && (waived || (Vector3.Dot(vector1: normal, vector2: Vector3.Normalize(value: normals[vertex])) >= (cosine - 1e-6))));
            }

            if (!(nearest <= (level.Error + 1e-6))) { return $"vertex {vertex} lies {nearest} from the level, past its error {level.Error}"; }
            if (!within) { return $"vertex {vertex} has no triangle of its material within the error and the angle"; }
        }

        // The other way: each level triangle's centroid and edge midpoints lie within the error of the mesh's own
        // triangles of its material.
        for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
            var (a, b, c) = (positions[((int)indices[(3 * triangle)])], positions[((int)indices[((3 * triangle) + 1)])], positions[((int)indices[((3 * triangle) + 2)])]);
            var material = (levelMaterials.IsEmpty ? 0u : levelMaterials[triangle]);

            foreach (var point in ((ReadOnlySpan<Vector3>)[(((a + b) + c) / 3f), ((a + b) / 2f), ((b + c) / 2f), ((c + a) / 2f)])) {
                var nearest = double.PositiveInfinity;

                for (var other = 0; (other < (own.Length / 3)); other++) {
                    if ((materials.IsEmpty ? 0u : materials[other]) == material) {
                        nearest = Math.Min(val1: nearest, val2: Nearest(a: positions[((int)own[(3 * other)])], b: positions[((int)own[((3 * other) + 1)])], c: positions[((int)own[((3 * other) + 2)])], p: point).Distance);
                    }
                }

                if (!(nearest <= (level.Error + 1e-6))) { return $"triangle {triangle} strays {nearest} from the mesh, past its error {level.Error}"; }
            }
        }
        return null;
    }
    // The positions where a mesh's surface turns sharply: copies at one position whose normals part by more than
    // SdfMeshCollapse.SharpDegrees, and both ends of an edge whose normals do.
    private static List<Vector3> Sharp(SdfMesh mesh) {
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;
        var sharp = Math.Cos(d: ((SdfMeshCollapse.SharpDegrees * Math.PI) / 180.0));
        var points = new List<Vector3>();
        bool Parts(Vector3 a, Vector3 b) => (Vector3.Dot(vector1: Vector3.Normalize(value: a), vector2: Vector3.Normalize(value: b)) < sharp);

        for (var a = 0; (a < positions.Length); a++) {
            for (var b = (a + 1); (b < positions.Length); b++) {
                if ((positions[a] == positions[b]) && Parts(a: normals[a], b: normals[b])) { points.Add(item: positions[a]); }
            }
        }
        for (var corner = 0; (corner < indices.Length); corner++) {
            var a = ((int)indices[corner]);
            var b = ((int)indices[(((corner % 3) == 2) ? (corner - 2) : (corner + 1))]);

            if (Parts(a: normals[a], b: normals[b])) { points.Add(item: positions[a]); points.Add(item: positions[b]); }
        }
        return points;
    }
    private static int OpenEdges(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
        var edges = new Dictionary<(Vector3, Vector3), int>();

        for (var corner = 0; (corner < indices.Length); corner++) {
            var from = positions[((int)indices[corner])];
            var to = positions[((int)indices[(((corner % 3) == 2) ? (corner - 2) : (corner + 1))])];

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

        foreach (var (from, to, weights) in ((ReadOnlySpan<(Vector3, Vector3, Func<float, Vector3>)>)[(a, b, t => new Vector3(x: (1f - t), y: t, z: 0f)), (b, c, t => new Vector3(x: 0f, y: (1f - t), z: t)), (c, a, t => new Vector3(x: t, y: 0f, z: (1f - t)))])) {
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
                        at[x, y] = ((uint)m_positions.Count);
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
