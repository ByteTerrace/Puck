using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: the irradiance volume's trace scene answers every ray as a search of every static triangle in world space
/// does, through identity, turned, scaled, mirrored and stretched draws alike: the same hit, distance, normal (wound as the
/// world triangle winds) and material, and the same open rays. A scene built from a previous one builds only the geometry
/// new to it: new geometry builds alone, a move or a renumbered mesh builds nothing, and geometry a change set aside is
/// taken back from the scene that retained it.
/// </summary>
public sealed class SdfMeshIrradianceSceneLawTests {
    // A rippled sheet of quads, wide enough that its hierarchy and preparation run in parallel.
    private static SdfMesh Sheet(int quads, uint materials) {
        var positions = new Vector3[(quads + 1) * (quads + 1)];
        var indices = new uint[6 * quads * quads];
        var triangleMaterials = new uint[2 * quads * quads];

        for (var z = 0; (z <= quads); z++) {
            for (var x = 0; (x <= quads); x++) {
                positions[(z * (quads + 1)) + x] = new Vector3(x: ((x / (float)quads) - 0.5f), y: (0.05f * MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.45f)), z: ((z / (float)quads) - 0.5f));
            }
        }
        for (var z = 0; (z < quads); z++) {
            for (var x = 0; (x < quads); x++) {
                var quad = ((z * quads) + x);
                var corner = (uint)((z * (quads + 1)) + x);

                new uint[] { corner, (corner + (uint)(quads + 1)), (corner + 1), (corner + 1), (corner + (uint)(quads + 1)), (corner + (uint)(quads + 2)) }.CopyTo(array: indices, index: (6 * quad));
                triangleMaterials[2 * quad] = (uint)(quad % materials);
                triangleMaterials[(2 * quad) + 1] = (uint)((quad + 1) % materials);
            }
        }

        return new SdfMesh(indices: indices, positions: positions, triangleMaterials: triangleMaterials);
    }
    // A unit cube about the origin, its faces wound outward, no triangle materials.
    private static SdfMesh Cube() {
        Vector3[] positions = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)];
        uint[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];

        return new SdfMesh(indices: indices, positions: positions);
    }
    private static SdfMeshDraw Draw(SdfMesh mesh, Matrix4x4 matrix, int material, bool dynamic = false) =>
        new(Identity: new object(), Material: material, Mesh: mesh, ObjectToWorld: matrix) { IsDynamic = dynamic };
    private static SdfMeshDraw[] Draws(SdfMesh sheet, SdfMesh cube) => [
        Draw(material: 0, matrix: Matrix4x4.Identity, mesh: sheet),
        Draw(material: 10, matrix: (Matrix4x4.CreateScale(scale: 4f) * Matrix4x4.CreateFromYawPitchRoll(yaw: 0.6f, pitch: 0.2f, roll: -0.3f) * Matrix4x4.CreateTranslation(xPosition: 1f, yPosition: 1.5f, zPosition: -0.5f)), mesh: sheet),
        Draw(material: 20, matrix: (Matrix4x4.CreateReflection(value: new Plane(normal: Vector3.UnitX, d: 0f)) * Matrix4x4.CreateTranslation(xPosition: -1.2f, yPosition: 0.4f, zPosition: 0.3f)), mesh: cube),
        Draw(material: 30, matrix: (Matrix4x4.CreateScale(xScale: 0.5f, yScale: 2f, zScale: 1f) * Matrix4x4.CreateTranslation(xPosition: 1.4f, yPosition: -0.6f, zPosition: 0.8f)), mesh: cube),
        Draw(dynamic: true, material: 40, matrix: Matrix4x4.Identity, mesh: cube),
        Draw(material: 50, matrix: Matrix4x4.CreateScale(scale: 0f), mesh: cube),
    ];

    // Every static triangle of the draws in world space, the search the scene must answer as.
    private sealed record World(Vector3 A, Vector3 B, Vector3 C, int Material);

    private static List<World> Flatten(IEnumerable<SdfMeshDraw> draws) {
        var triangles = new List<World>();

        foreach (var draw in draws) {
            if (draw.IsDynamic || !Matrix4x4.Invert(matrix: draw.ObjectToWorld, result: out _)) { continue; }

            var positions = draw.Mesh.Positions.Span;
            var indices = draw.Mesh.Indices.Span;
            var materials = draw.Mesh.TriangleMaterials.Span;

            for (var triangle = 0; (triangle < draw.Mesh.TriangleCount); triangle++) {
                triangles.Add(item: new World(
                    A: Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[(int)indices[3 * triangle]]),
                    B: Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[(int)indices[(3 * triangle) + 1]]),
                    C: Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[(int)indices[(3 * triangle) + 2]]),
                    Material: (draw.Material + (materials.IsEmpty ? 0 : (int)materials[triangle]))
                ));
            }
        }

        return triangles;
    }
    private static World? Nearest(List<World> triangles, Vector3 origin, Vector3 direction, float maximum, out float distance) {
        World? nearest = null;

        distance = maximum;
        foreach (var triangle in triangles) {
            var e1 = (triangle.B - triangle.A);
            var e2 = (triangle.C - triangle.A);
            var p = Vector3.Cross(vector1: direction, vector2: e2);
            var determinant = Vector3.Dot(vector1: e1, vector2: p);

            if (MathF.Abs(determinant) < 1.0e-12f) { continue; }

            var s = (origin - triangle.A);
            var u = (Vector3.Dot(vector1: s, vector2: p) / determinant);
            var q = Vector3.Cross(vector1: s, vector2: e1);
            var v = (Vector3.Dot(vector1: direction, vector2: q) / determinant);
            var t = (Vector3.Dot(vector1: e2, vector2: q) / determinant);

            if ((u >= 0f) && (v >= 0f) && ((u + v) <= 1f) && (t > 1.0e-4f) && (t < distance)) {
                distance = t;
                nearest = triangle;
            }
        }

        return nearest;
    }
    private static IEnumerable<(Vector3 Origin, Vector3 Direction)> Rays() {
        var random = new Random(Seed: 7);

        for (var ray = 0; (ray < 400); ray++) {
            var origin = new Vector3(x: ((random.NextSingle() * 6f) - 3f), y: ((random.NextSingle() * 6f) - 3f), z: ((random.NextSingle() * 6f) - 3f));
            var toward = new Vector3(x: ((random.NextSingle() * 2f) - 1f), y: ((random.NextSingle() * 2f) - 1f), z: ((random.NextSingle() * 2f) - 1f));

            yield return (origin, Vector3.Normalize(value: (toward - (origin * 0.3f))));
        }
    }
    private static void AnswersAsTheSearch(SdfMeshIrradianceScene scene, IEnumerable<SdfMeshDraw> draws) {
        var triangles = Flatten(draws: draws);
        var hits = 0;

        foreach (var (origin, direction) in Rays()) {
            var expected = Nearest(direction: direction, distance: out var expectedDistance, maximum: 20f, origin: origin, triangles: triangles);
            var hit = scene.Intersect(direction: direction, distance: out var distance, material: out var material, maximum: 20f, normal: out var normal, origin: origin);

            Assert.Equal(expected: (expected is not null), actual: hit);
            Assert.Equal(expected: (expected is null), actual: scene.Unblocked(direction: direction, maximum: 20f, origin: origin));
            if (expected is null) { continue; }
            hits++;
            Assert.Equal(expected: expectedDistance, actual: distance, tolerance: 1.0e-3f);
            Assert.Equal(expected: expected.Material, actual: material);
            Assert.True(condition: (Vector3.Dot(vector1: normal, vector2: Vector3.Normalize(value: Vector3.Cross(vector1: (expected.B - expected.A), vector2: (expected.C - expected.A)))) > 0.999f), userMessage: $"normal {normal} at {origin} along {direction}");
        }
        Assert.InRange(actual: hits, low: 100, high: 399);
    }

    [Fact]
    public void ATwoLevelSceneAnswersEveryRayAsASearchOfEveryWorldTriangle() {
        var sheet = Sheet(materials: 3, quads: 96);
        var cube = Cube();
        var draws = Draws(cube: cube, sheet: sheet);
        var scene = SdfMeshIrradianceScene.Build(draws: draws)!;

        Assert.Equal(expected: 4, actual: scene.DrawCount);
        Assert.Equal(expected: 2, actual: scene.GeometryCount);
        Assert.Equal(expected: ((2L * sheet.TriangleCount) + (2L * cube.TriangleCount)), actual: scene.TriangleCount);
        Assert.Equal(expected: (sheet.TriangleCount + (long)cube.TriangleCount), actual: scene.TrianglesBuilt);
        AnswersAsTheSearch(draws: draws, scene: scene);
        Assert.Null(@object: SdfMeshIrradianceScene.Build(draws: [Draw(dynamic: true, material: 0, matrix: Matrix4x4.Identity, mesh: cube)]));
        Assert.Null(@object: SdfMeshIrradianceScene.Build(draws: [Draw(material: 0, matrix: Matrix4x4.CreateScale(scale: 0f), mesh: cube)]));
    }
    [Fact]
    public void ASceneBuiltFromAnotherBuildsOnlyTheGeometryNewToIt() {
        var sheet = Sheet(materials: 3, quads: 96);
        var cube = Cube();
        var draws = Draws(cube: cube, sheet: sheet);
        var first = SdfMeshIrradianceScene.Build(draws: draws)!;

        // New geometry in place of the cube: only it builds, and the cube is retained.
        var other = Sheet(materials: 2, quads: 8);
        var replaced = draws.Select(selector: draw => (ReferenceEquals(objA: draw.Mesh, objB: cube) && !draw.IsDynamic) ? draw with { Mesh = other } : draw).ToArray();
        var swapped = SdfMeshIrradianceScene.Build(draws: replaced, previous: first)!;

        Assert.Equal(expected: (1, (long)other.TriangleCount, 1, (long)cube.TriangleCount), actual: (swapped.GeometriesBuilt, swapped.TrianglesBuilt, swapped.GeometriesReused, swapped.TrianglesRetained));
        AnswersAsTheSearch(draws: replaced, scene: swapped);

        // A move and a renumbered mesh (the same buffers, other triangle materials) build nothing.
        var renumbered = new SdfMesh(indices: sheet.Indices, positions: sheet.Positions, triangleMaterials: sheet.TriangleMaterials.ToArray().Select(selector: static material => (material + 5u)).ToArray());
        var moved = replaced.Select(selector: (draw, index) => (index == 0) ? draw with { Mesh = renumbered, ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 0f, yPosition: -0.25f, zPosition: 0f) } : draw).ToArray();
        var shifted = SdfMeshIrradianceScene.Build(draws: moved, previous: swapped)!;

        Assert.Equal(expected: (0, 0L), actual: (shifted.GeometriesBuilt, shifted.TrianglesBuilt));
        AnswersAsTheSearch(draws: moved, scene: shifted);

        // The cube comes back from the retained geometry, two scenes after it last drew.
        var back = SdfMeshIrradianceScene.Build(draws: draws, previous: shifted)!;

        Assert.Equal(expected: (0, 2, (long)other.TriangleCount), actual: (back.GeometriesBuilt, back.GeometriesReused, back.TrianglesRetained));
        AnswersAsTheSearch(draws: draws, scene: back);
    }
}
