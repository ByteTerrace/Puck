using System.Numerics;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// Laws over <see cref="SdfSampledLattice"/>'s refinement: a disc thinner than a lattice cell, which the lattice's own
/// samples miss in places, refines and contours as one closed two-manifold, with no crack where the contour's resolution
/// changes, and its mesh lies within half a lattice cell of the field, as a prototype bake's does.
/// </summary>
public sealed class SdfSampledLatticeLawTests {
    private const float Cell = 0.015f;
    private const int Samples = 33;
    private const int Factor = 3;
    private static readonly Vector3 Origin = new(value: -0.24f);
    private static readonly Vector3 Normal = Vector3.Normalize(value: new Vector3(x: 0.3f, y: 1f, z: 0.2f));

    // A tilted disc 6 mm thick and 18 cm across: thinner than the 15 mm lattice cell, so its inside samples are sparse.
    private static float Field(Vector3 point) {
        var slab = (MathF.Abs(Vector3.Dot(vector1: point, vector2: Normal)) - 0.003f);
        var sphere = (point.Length() - 0.18f);

        return MathF.Max(slab, sphere);
    }
    private static Vector3 Gradient(Vector3 point) {
        const float Step = 1.0e-4f;

        return new Vector3(
            x: (Field(point: (point + new Vector3(x: Step, y: 0f, z: 0f))) - Field(point: (point - new Vector3(x: Step, y: 0f, z: 0f)))),
            y: (Field(point: (point + new Vector3(x: 0f, y: Step, z: 0f))) - Field(point: (point - new Vector3(x: 0f, y: Step, z: 0f)))),
            z: (Field(point: (point + new Vector3(x: 0f, y: 0f, z: Step))) - Field(point: (point - new Vector3(x: 0f, y: 0f, z: Step))))
        );
    }
    private static SdfContour Bake(bool refine) {
        var count = ((Samples * Samples) * Samples);
        var values = new float[count];

        for (var index = 0; (index < count); index++) {
            var at = new Vector3(x: (index % Samples), y: ((index / Samples) % Samples), z: (index / (Samples * Samples)));

            values[index] = Field(point: (Origin + (at * Cell)));
        }

        var lattice = new SdfSampledLattice(cell: new Vector3(value: Cell), materials: new int[count], origin: Origin, samplesX: Samples, samplesY: Samples, samplesZ: Samples, values: values);

        if (refine) {
            var fine = lattice.Refine(factor: Factor);

            Assert.NotEmpty(collection: fine);
            lattice.AnswerRefinement(first: 0, materials: new int[fine.Length], values: [.. fine.Select(selector: Field)]);
        }

        var points = lattice.GradientPoints();
        var gradients = new float[(3 * points.Length)];

        for (var index = 0; (index < points.Length); index++) {
            var gradient = Gradient(point: points[index]);

            gradients[(3 * index)] = gradient.X;
            gradients[((3 * index) + 1)] = gradient.Y;
            gradients[((3 * index) + 2)] = gradient.Z;
        }
        lattice.AnswerGradients(first: 0, gradients: gradients);

        return lattice.Contour();
    }
    private static List<(int A, int B, int C)> Triangles(SdfContour contour) {
        var triangles = new List<(int A, int B, int C)>();

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var (a, b, c, d) = (contour.Quads[(4 * quad)], contour.Quads[((4 * quad) + 1)], contour.Quads[((4 * quad) + 2)], contour.Quads[((4 * quad) + 3)]);

            triangles.Add(item: (a, b, c));
            if (c != d) {
                triangles.Add(item: (a, c, d));
            }
        }

        return triangles;
    }

    [Fact]
    public void ThinDiscRefinesIntoAClosedTwoManifold() {
        var contour = Bake(refine: true);
        var uses = new Dictionary<(int, int), int>();

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) }) {
                var key = ((from < to) ? (from, to) : (to, from));

                uses[key] = (uses.GetValueOrDefault(key: key) + 1);
            }
        }

        Assert.NotEmpty(collection: uses);
        Assert.All(collection: uses, action: static use => Assert.Equal(expected: 2, actual: use.Value));
    }

    [Fact]
    public void RefinedMeshLiesWithinHalfALatticeCellOfTheField() {
        var contour = Bake(refine: true);

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            var point = new Vector3(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)]);

            Assert.True(condition: (MathF.Abs(Field(point: point)) <= (0.5f * Cell)), userMessage: $"vertex {vertex} at {point} lies {Field(point: point)} from the field");
        }
    }

    [Fact]
    public void RefinementKeepsTheDiscWholeWhereTheLatticeAloneBreaksIt() {
        Assert.True(condition: (Pieces(contour: Bake(refine: false)) > 1));
        Assert.Equal(expected: 1, actual: Pieces(contour: Bake(refine: true)));
    }

    // The connected pieces of a contour's triangles.
    private static int Pieces(SdfContour contour) {
        var parent = Enumerable.Range(start: 0, count: contour.VertexCount).ToArray();

        int Root(int vertex) {
            while (parent[vertex] != vertex) {
                vertex = parent[vertex] = parent[parent[vertex]];
            }

            return vertex;
        }

        var used = new HashSet<int>();

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            parent[Root(vertex: b)] = Root(vertex: a);
            parent[Root(vertex: c)] = Root(vertex: a);
            _ = used.Add(item: a);
            _ = used.Add(item: b);
            _ = used.Add(item: c);
        }

        return used.Select(selector: Root).Distinct().Count();
    }
}
