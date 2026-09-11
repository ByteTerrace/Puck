using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <c>SdfProgramBuilder.ConvexPolygon</c>'s exact polygon SDF agrees with the family's other exact 2D
/// cores on a shape both can spell (a rectangle), evaluates with the right sign on a genuinely convex authored
/// profile (a pentagon), and refuses a vertex list that is not a well-formed clockwise convex hull by name — never
/// silently accepting a concave or degenerate authoring.
/// </summary>
public sealed class ConvexProfileLawTests {
    [Fact]
    public void ARectangleAuthoredAsFourVerticesMatchesRoundedRectangle() {
        const float HalfWidth = 1f;
        const float HalfHeight = 0.6f;
        const float HalfDepth = 1f;

        // Clockwise (X right, Y up): bottom-right, bottom-left, top-left, top-right.
        Vector2[] rectangle = [
            new(HalfWidth, -HalfHeight),
            new(-HalfWidth, -HalfHeight),
            new(-HalfWidth, HalfHeight),
            new(HalfWidth, HalfHeight),
        ];

        var convexEvaluator = ConvexPolygon(rectangle, HalfDepth);
        var roundedRectangleEvaluator = RoundedRectangle(HalfWidth, HalfHeight, HalfDepth);

        foreach (var (x, y, z) in new[] {
            (0.0, 0.0, 0.0),
            (0.9, 0.0, 0.0),
            (0.0, 0.5, 0.0),
            (1.1, 0.0, 0.0),
            (0.0, 0.0, 0.9),
            (0.0, 0.0, 1.2),
            (0.99, 0.59, 0.99),
        }) {
            var position = Position(x, y, z);

            Assert.True(convexEvaluator.TryDistance(position, out var convexDistance, out _));
            Assert.True(roundedRectangleEvaluator.TryDistance(position, out var rectangleDistance, out _));
            Assert.InRange((double)(convexDistance - rectangleDistance), -0.003, 0.003);
        }
    }

    [Fact]
    public void TheStudyShoulderPentagonEvaluatesWithTheRightSignAtItsCentreAndOutside() {
        // moth-study.glsl's shoulderProfile: fiveSides(p, (-.12,.10), (.08,.20), (.29,.10), (.48,-.45), (.12,-.24)).
        Vector2[] pentagon = [
            new(-0.12f, 0.10f),
            new(0.08f, 0.20f),
            new(0.29f, 0.10f),
            new(0.48f, -0.45f),
            new(0.12f, -0.24f),
        ];

        Assert.True(SdfPrismProfile.IsValidConvexHull(pentagon));

        var evaluator = ConvexPolygon(pentagon, 0.2f);

        // The unweighted centroid of a convex polygon always lies inside it.
        var centroid = ((pentagon[0] + pentagon[1] + pentagon[2] + pentagon[3] + pentagon[4]) / 5f);

        Assert.True(evaluator.TryDistance(Position(centroid.X, centroid.Y, 0), out var inside, out _));
        Assert.True(inside < FixedQ4816.Zero, userMessage: $"the pentagon's own centroid read {(double)inside}");

        Assert.True(evaluator.TryDistance(Position(5, 5, 0), out var outside, out _));
        Assert.True(outside > FixedQ4816.Zero);
    }

    [Fact]
    public void AConcavePolygonIsRefusedByName() {
        // A dart: vertex[2] is pushed IN past the line between its neighbours, breaking convexity while keeping
        // clockwise winding at every OTHER vertex.
        Vector2[] dart = [
            new(1f, -1f),
            new(-1f, -1f),
            new(0f, 0f),
            new(-1f, 1f),
            new(1f, 1f),
        ];

        Assert.False(SdfPrismProfile.IsValidConvexHull(dart));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => builder.ConvexPolygon(dart, 0f, SdfLift.Extrude, 1f, material));

        Assert.Contains("convex", refusal.Message);
    }

    [Fact]
    public void ACounterclockwiseWindingIsRefusedByName() {
        // The exact rectangle from the first law, wound the opposite way.
        Vector2[] counterclockwise = [
            new(1f, -1f),
            new(1f, 1f),
            new(-1f, 1f),
            new(-1f, -1f),
        ];

        Assert.False(SdfPrismProfile.IsValidConvexHull(counterclockwise));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ConvexPolygon(counterclockwise, 0f, SdfLift.Extrude, 1f, material));

        // The control: the same four points, clockwise, are admitted.
        Vector2[] clockwise = [.. counterclockwise];
        Array.Reverse(clockwise);
        Assert.True(SdfPrismProfile.IsValidConvexHull(clockwise));
    }

    // THE LAW: every vertex lies in the unit square. The profile rides the shape's own XY scale like every other
    // Prism profile, and SdfSolidGeometry.Reach's Prism arm (sqrt(3) x max scale) covers exactly that frame — a vertex
    // past it would leave the cull bound short and clip the prism at its tile edges.
    [Fact]
    public void AVertexOutsideTheUnitSquareIsRefusedByName() {
        Vector2[] tooWide = [new(1.5f, -1f), new(-1f, -1f), new(-1f, 1f), new(1.5f, 1f)];

        Assert.False(SdfPrismProfile.IsValidConvexHull(tooWide));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ConvexPolygon(tooWide, 0f, SdfLift.Extrude, 1f, material));

        // The control: the same outline on the unit square's own boundary is admitted.
        Vector2[] onTheBoundary = [new(1f, -1f), new(-1f, -1f), new(-1f, 1f), new(1f, 1f)];

        Assert.True(SdfPrismProfile.IsValidConvexHull(onTheBoundary));
        _ = builder.ConvexPolygon(onTheBoundary, 0f, SdfLift.Extrude, 1f, material);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void AVertexCountOutsideTheSupportedRangeIsRefusedByName(int count) {
        var vertices = new Vector2[count];
        var angleStep = ((2f * MathF.PI) / count);

        // A regular (clockwise, since angle DECREASES with index — negative step) n-gon: always convex, so a
        // refusal at 2 or 9 vertices is purely a COUNT refusal, not a winding/convexity one.
        for (var i = 0; (i < count); i++) {
            var angle = (-angleStep * i);

            vertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        Assert.False(SdfPrismProfile.IsValidConvexHull(vertices));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ConvexPolygon(vertices, 0f, SdfLift.Extrude, 1f, material));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void AVertexCountAtTheSupportedBoundsIsAdmitted(int count) {
        var vertices = new Vector2[count];
        var angleStep = ((2f * MathF.PI) / count);

        for (var i = 0; (i < count); i++) {
            var angle = (-angleStep * i);

            vertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        Assert.True(SdfPrismProfile.IsValidConvexHull(vertices));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ConvexPolygon(vertices, 0f, SdfLift.Extrude, 1f, material);
        _ = Assert.Single(builder.Build().Instructions);
    }

    [Fact]
    public void RoundingFilletsTheCornersWithoutMovingTheFaces() {
        Vector2[] square = [
            new(1f, -1f),
            new(-1f, -1f),
            new(-1f, 1f),
            new(1f, 1f),
        ];

        var sharp = ConvexPolygon(square, 1f, 0f);
        var rounded = ConvexPolygon(square, 1f, 0.3f);

        // The faces stay put.
        Assert.True(sharp.TryDistance(Position(0, 1, 0), out var sharpFace, out _));
        Assert.True(rounded.TryDistance(Position(0, 1, 0), out var roundedFace, out _));
        Assert.InRange((double)sharpFace, -0.01, 0.01);
        Assert.InRange((double)roundedFace, -0.01, 0.01);

        // The corner the fillet cuts: inside the sharp square, outside the rounded one.
        Assert.True(sharp.TryDistance(Position(0.95, 0.95, 0), out var sharpCorner, out _));
        Assert.True(rounded.TryDistance(Position(0.95, 0.95, 0), out var roundedCorner, out _));
        Assert.True(sharpCorner < FixedQ4816.Zero);
        Assert.True(roundedCorner > FixedQ4816.Zero);
    }

    private static SdfFieldEvaluator ConvexPolygon(Vector2[] vertices, float halfDepth, float cornerRadius = 0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().ConvexPolygon(vertices, cornerRadius, SdfLift.Extrude, halfDepth, material);

        return new SdfFieldEvaluator(builder.Build());
    }
    private static SdfFieldEvaluator RoundedRectangle(float halfWidth, float halfHeight, float halfDepth) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().RoundedRectangle(halfWidth, halfHeight, 0f, SdfLift.Extrude, halfDepth, material);

        return new SdfFieldEvaluator(builder.Build());
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
}
