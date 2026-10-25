using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: the planar merge changes no pixel. It keeps every vertex it keeps where it was, with its normal and member;
/// covers each material's surface facing each normal with exactly the area it covered, inside exactly the outline it had,
/// edge for edge; opens no edge (an edge one side names and the other does not, as a T-junction leaves); winds every
/// triangle as its normal faces; and empties each flat face of its inside vertices: a gridded cube keeps the vertices
/// along its edges and nothing inside its faces. A vertex an unmerged neighbour names stays, and so does every vertex of a
/// triangle that cannot merge.
/// </summary>
public sealed class SdfMeshPlanarMergeLawTests {
    private const int Cells = 8;
    private const float Cell = 0.25f;

    [Fact]
    public void AGriddedCubeKeepsItsEdgesAndEmptiesItsFaces() {
        foreach (var jitter in new[] { false, true }) {
            var cube = Cube(jitter: jitter, kink: false);
            var merged = SdfMeshPlanarMerge.Merge(mesh: cube);

            // Each face is a polygon of the 4 x Cells vertices along its edges, which a polygon covers with two triangles
            // fewer.
            Holds(before: cube, after: merged);
            Assert.Equal(expected: (6 * ((4 * Cells) - 2)), actual: merged.TriangleCount);
            Assert.Equal(expected: (6 * (4 * Cells)), actual: merged.Positions.Length);
            Assert.Equal(expected: cube.BakeCell, actual: merged.BakeCell);
        }
    }
    [Fact]
    public void AVertexAnUnmergedNeighbourNamesStays() {
        var cube = Cube(jitter: true, kink: true);
        var merged = SdfMeshPlanarMerge.Merge(mesh: cube);
        var kept = merged.Positions.ToArray().ToHashSet();

        Holds(before: cube, after: merged);
        Assert.True(condition: (merged.TriangleCount > (6 * ((4 * Cells) - 2))));

        // Every vertex of a triangle whose corners carry differing normals stays at its place.
        var positions = cube.Positions.Span;
        var normals = cube.Normals.Span;
        var indices = cube.Indices.Span;

        for (var triangle = 0; (triangle < cube.TriangleCount); triangle++) {
            var (a, b, c) = ((int)indices[(3 * triangle)], (int)indices[((3 * triangle) + 1)], (int)indices[((3 * triangle) + 2)]);

            if ((normals[a] == normals[b]) && (normals[a] == normals[c])) { continue; }
            Assert.Contains(expected: positions[a], collection: kept);
            Assert.Contains(expected: positions[b], collection: kept);
            Assert.Contains(expected: positions[c], collection: kept);
        }
    }
    [Fact]
    public void MaterialsAndMembersBoundTheirPolygons() {
        // One plane, its halves of two materials sharing the vertices along the line between them, which steps once, its left
        // columns riding a member: the line and the open edge keep their vertices, a vertex inside one material goes, and
        // no triangle crosses a member that did not.
        var plane = Plane(material: static (x, y) => ((x < ((y < (Cells / 2)) ? (Cells / 2) : ((Cells / 2) + 1))) ? 0u : 1u), member: static (x, _) => ((x < 2) ? (byte)0 : SdfMesh.NoMember));
        var merged = SdfMeshPlanarMerge.Merge(mesh: plane);
        var kept = merged.Positions.ToArray();

        Holds(before: plane, after: merged);
        Assert.True(condition: (merged.TriangleCount < plane.TriangleCount));
        Assert.DoesNotContain(expected: plane.Positions.Span[((6 * (Cells + 1)) + 6)], collection: kept);
        Assert.Equal(expected: Mixed(mesh: plane), actual: Mixed(mesh: merged));
        for (var y = 0; (y <= (Cells / 2)); y++) {
            Assert.Contains(expected: plane.Positions.Span[((y * (Cells + 1)) + (Cells / 2))], collection: kept);
        }

        // A mesh that carries no vertex normals, or one triangle, or no region of two, is the mesh itself; here the first
        // triangle's corners carry its neighbour's normal off its plane.
        var bare = new SdfMesh(indices: plane.Indices, positions: plane.Positions);
        var one = new SdfMesh(indices: new uint[] { 0, 1, 2 }, normals: new Vector3[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitZ, Vector3.UnitX });
        var apart = new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            normals: new Vector3[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
            positions: new Vector3[] { Vector3.Zero, new(x: 0f, y: 1f, z: 1f), Vector3.UnitX, new(x: 1f, y: 0f, z: -1f) }
        );

        Assert.Same(expected: bare, actual: SdfMeshPlanarMerge.Merge(mesh: bare));
        Assert.Same(expected: one, actual: SdfMeshPlanarMerge.Merge(mesh: one));
        Assert.Same(expected: apart, actual: SdfMeshPlanarMerge.Merge(mesh: apart));
    }
    [Fact]
    public void OrientationIsExactOnFloats() {
        // Collinear on a diagonal, where the doubles cannot decide; across the whole float range; and subnormal.
        Assert.Equal(expected: 0, actual: SdfMeshPlanarMerge.Orient(ax: 0.1f, ay: 0.1f, bx: 0.3f, by: 0.3f, cx: 0.7f, cy: 0.7f));
        Assert.Equal(expected: 0, actual: SdfMeshPlanarMerge.Orient(ax: 0f, ay: 0f, bx: 1e-30f, by: 1e-30f, cx: 1e10f, cy: 1e10f));
        Assert.Equal(expected: 1, actual: SdfMeshPlanarMerge.Orient(ax: 0f, ay: 0f, bx: 1e10f, by: 1e10f, cx: 1e-30f, cy: 2e-30f));
        Assert.Equal(expected: -1, actual: SdfMeshPlanarMerge.Orient(ax: float.Epsilon, ay: 0f, bx: 0f, by: float.Epsilon, cx: (2f * float.Epsilon), cy: float.Epsilon));
        Assert.Equal(expected: 0, actual: SdfMeshPlanarMerge.Orient(ax: 0f, ay: 0f, bx: 0f, by: 0f, cx: 1f, cy: 1f));
        Assert.Equal(expected: 1, actual: SdfMeshPlanarMerge.Orient(ax: 0f, ay: 0f, bx: 1f, by: 0f, cx: 0f, cy: 1f));
        Assert.Equal(expected: -1, actual: SdfMeshPlanarMerge.Orient(ax: 1f, ay: 0f, bx: 0f, by: 0f, cx: 0f, cy: 1f));
    }

    // A cube of side two about the origin, each face a grid of its own vertices (the copies a crease split leaves along its
    // edges), its inside vertices moved within their cells where `jitter` asks, and one inside vertex of the +x face given a
    // tilted normal where `kink` asks: the triangles about it cannot merge.
    private static SdfMesh Cube(bool jitter, bool kink) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        (Vector3 Origin, Vector3 U, Vector3 V)[] faces = [
            (new(x: 1f, y: -1f, z: -1f), Vector3.UnitY, Vector3.UnitZ),
            (new(x: -1f, y: -1f, z: -1f), Vector3.UnitZ, Vector3.UnitY),
            (new(x: -1f, y: 1f, z: -1f), Vector3.UnitZ, Vector3.UnitX),
            (new(x: -1f, y: -1f, z: -1f), Vector3.UnitX, Vector3.UnitZ),
            (new(x: -1f, y: -1f, z: 1f), Vector3.UnitX, Vector3.UnitY),
            (new(x: -1f, y: -1f, z: -1f), Vector3.UnitY, Vector3.UnitX),
        ];

        for (var face = 0; (face < faces.Length); face++) {
            var (origin, u, v) = faces[face];
            var normal = Vector3.Cross(vector1: u, vector2: v);

            Grid(
                indices: indices,
                material: static (_, _) => 0u,
                normal: (x, y) => ((kink && (face == 0) && (x == 3) && (y == 4)) ? Vector3.Normalize(value: (normal + (0.1f * u))) : normal),
                normals: normals,
                point: (x, y) => (origin + ((((x * Cell) + Jitter(on: jitter, x: x, y: y)) * u) + (((y * Cell) + Jitter(on: jitter, x: y, y: x)) * v))),
                positions: positions
            );
        }

        return new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray()) { BakeCell = new Vector3(value: Cell) };
    }
    // One grid in the z = 0 plane facing +z, with a material a cell and a member a vertex.
    private static SdfMesh Plane(Func<int, int, uint> material, Func<int, int, byte> member) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        var materials = new List<uint>();
        var members = new List<byte>();

        // The line between the materials stays straight; every other inside vertex moves within its cell.
        Grid(indices: indices, material: material, materials: materials, normal: static (_, _) => Vector3.UnitZ, normals: normals, point: static (x, y) => new Vector3(x: ((x * Cell) + Jitter(on: (x != (Cells / 2)), x: x, y: y)), y: ((y * Cell) + Jitter(on: true, x: y, y: x)), z: 0f), positions: positions);
        for (var y = 0; (y <= Cells); y++) {
            for (var x = 0; (x <= Cells); x++) {
                members.Add(item: member(arg1: x, arg2: y));
            }
        }

        return new SdfMesh(indices: indices.ToArray(), members: new SdfMeshMember[] { new(Orientation: Quaternion.Identity, Position: Vector3.Zero, SlotOffset: 1) }, normals: normals.ToArray(), positions: positions.ToArray(), triangleMaterials: materials.ToArray(), vertexMembers: members.ToArray());
    }
    // A grid of Cells by Cells cells, two triangles a cell wound about u then v.
    private static void Grid(List<Vector3> positions, List<Vector3> normals, List<uint> indices, Func<int, int, Vector3> point, Func<int, int, Vector3> normal, Func<int, int, uint> material, List<uint>? materials = null) {
        var first = (uint)positions.Count;

        for (var y = 0; (y <= Cells); y++) {
            for (var x = 0; (x <= Cells); x++) {
                positions.Add(item: point(arg1: x, arg2: y));
                normals.Add(item: normal(arg1: x, arg2: y));
            }
        }

        uint At(int x, int y) => (first + (uint)((y * (Cells + 1)) + x));

        for (var y = 0; (y < Cells); y++) {
            for (var x = 0; (x < Cells); x++) {
                indices.AddRange(collection: [At(x: x, y: y), At(x: (x + 1), y: y), At(x: (x + 1), y: (y + 1)), At(x: x, y: y), At(x: (x + 1), y: (y + 1)), At(x: x, y: (y + 1))]);
                materials?.AddRange(collection: [material(arg1: x, arg2: y), material(arg1: x, arg2: y)]);
            }
        }
    }
    // A move of an inside grid vertex within its cell, in binary fractions so positions stay exact; none on the grid's edge.
    private static float Jitter(bool on, int x, int y) => ((on && (x > 0) && (x < Cells) && (y > 0) && (y < Cells)) ? ((((((x * 7) + (y * 3)) % 5) - 2) * Cell) / 16f) : 0f);

    // The merge's whole contract on one mesh: kept vertices unmoved, area per material and normal, no edge opened, every
    // merged triangle wound along its normal.
    private static void Holds(SdfMesh before, SdfMesh after) {
        var vertices = new HashSet<(Vector3, Vector3, byte)>();
        var beforeMembers = before.VertexMembers.Span;
        var afterMembers = after.VertexMembers.Span;

        for (var vertex = 0; (vertex < before.Positions.Length); vertex++) {
            _ = vertices.Add(item: (before.Positions.Span[vertex], before.Normals.Span[vertex], (beforeMembers.IsEmpty ? SdfMesh.NoMember : beforeMembers[vertex])));
        }
        for (var vertex = 0; (vertex < after.Positions.Length); vertex++) {
            Assert.Contains(expected: (after.Positions.Span[vertex], after.Normals.Span[vertex], (afterMembers.IsEmpty ? SdfMesh.NoMember : afterMembers[vertex])), collection: vertices);
        }

        var (areaBefore, areaAfter) = (Areas(mesh: before), Areas(mesh: after));

        Assert.Equal(expected: areaBefore.Count, actual: areaAfter.Count);
        foreach (var (key, area) in areaBefore) {
            Assert.Equal(expected: area, actual: areaAfter[key], tolerance: (1e-9 * Math.Abs(value: area)));
        }
        Assert.Equal(expected: OpenEdges(mesh: before), actual: OpenEdges(mesh: after));
        Assert.True(condition: Outline(mesh: before).SetEquals(other: Outline(mesh: after)));

        var positions = after.Positions.Span;
        var normals = after.Normals.Span;
        var indices = after.Indices.Span;

        for (var triangle = 0; (triangle < after.TriangleCount); triangle++) {
            var (a, b, c) = ((int)indices[(3 * triangle)], (int)indices[((3 * triangle) + 1)], (int)indices[((3 * triangle) + 2)]);

            if ((normals[a] != normals[b]) || (normals[a] != normals[c])) { continue; }
            Assert.True(condition: (Vector3.Dot(vector1: Vector3.Cross(vector1: (positions[b] - positions[a]), vector2: (positions[c] - positions[a])), vector2: normals[a]) > 0f));
        }
    }
    // The triangles whose corners ride different members.
    private static int Mixed(SdfMesh mesh) {
        var members = mesh.VertexMembers.Span;
        var indices = mesh.Indices.Span;
        var mixed = 0;

        for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
            var (a, b, c) = (members[(int)indices[(3 * triangle)]], members[(int)indices[((3 * triangle) + 1)]], members[(int)indices[((3 * triangle) + 2)]]);

            mixed += (((a == b) && (b == c)) ? 0 : 1);
        }

        return mixed;
    }
    // The signed area each material covers facing each normal, the normal its triangles' first corner carries.
    private static Dictionary<(uint, Vector3), double> Areas(SdfMesh mesh) {
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;
        var materials = mesh.TriangleMaterials.Span;
        var areas = new Dictionary<(uint, Vector3), double>();

        for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
            var (a, b, c) = (positions[(int)indices[(3 * triangle)]], positions[(int)indices[((3 * triangle) + 1)]], positions[(int)indices[((3 * triangle) + 2)]]);
            var normal = normals[(int)indices[(3 * triangle)]];
            var key = ((materials.IsEmpty ? 0u : materials[triangle]), normal);

            areas[key] = (areas.GetValueOrDefault(key: key) + (0.5 * Vector3.Dot(vector1: Vector3.Cross(vector1: (b - a), vector2: (c - a)), vector2: normal)));
        }

        return areas;
    }
    // The outline of each material's surface facing each normal: the edges between two places that its triangles run one
    // way more often than the other.
    private static HashSet<(uint, Vector3, Vector3, Vector3)> Outline(SdfMesh mesh) {
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;
        var materials = mesh.TriangleMaterials.Span;
        var counts = new Dictionary<(uint, Vector3, Vector3, Vector3), int>();

        for (var at = 0; (at < indices.Length); at++) {
            var triangle = (at / 3);
            var bucket = ((materials.IsEmpty ? 0u : materials[triangle]), normals[(int)indices[(3 * triangle)]]);
            var key = (bucket.Item1, bucket.Item2, positions[(int)indices[at]], positions[(int)indices[(((at % 3) == 2) ? (at - 2) : (at + 1))]]);

            counts[key] = (counts.GetValueOrDefault(key: key) + 1);
        }

        return [.. counts.Keys.Where(predicate: key => (counts[key] != counts.GetValueOrDefault(key: (key.Item1, key.Item2, key.Item4, key.Item3))))];
    }
    // The edges between two places one way more often than the other: the mesh's open edge, and every crack.
    private static int OpenEdges(SdfMesh mesh) {
        var positions = mesh.Positions.Span;
        var indices = mesh.Indices.Span;
        var counts = new Dictionary<(Vector3, Vector3), int>();

        for (var at = 0; (at < indices.Length); at++) {
            var from = positions[(int)indices[at]];
            var to = positions[(int)indices[(((at % 3) == 2) ? (at - 2) : (at + 1))]];

            counts[(from, to)] = (counts.GetValueOrDefault(key: (from, to)) + 1);
        }

        return counts.Count(predicate: entry => (entry.Value != counts.GetValueOrDefault(key: (entry.Key.Item2, entry.Key.Item1))));
    }
}
