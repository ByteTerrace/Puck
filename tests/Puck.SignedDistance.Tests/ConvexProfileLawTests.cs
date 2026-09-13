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
    private static SdfFieldEvaluator ConvexPolygon(Vector2[] vertices, float halfDepth, float cornerRadius = 0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().ConvexPolygon(
            vertices,
            cornerRadius,
            SdfLift.Extrude,
            halfDepth,
            material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static SdfFieldEvaluator RoundedRectangle(float halfWidth, float halfHeight, float halfDepth) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().RoundedRectangle(
            halfWidth,
            halfHeight,
            0f,
            SdfLift.Extrude,
            halfDepth,
            material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }

    [Fact]
    public void AConcavePolygonIsRefusedByName() {
        // A dart: vertex[2] is pushed IN past the line between its neighbours, breaking convexity while keeping
        // clockwise winding at every OTHER vertex.
        Vector2[] dart = [
            new(
                x: 1f,
                y: -1f
            ),
            new(
                x: -1f,
                y: -1f
            ),
            new(
                x: 0f,
                y: 0f
            ),
            new(
                x: -1f,
                y: 1f
            ),
            new(
                x: 1f,
                y: 1f
            ),
        ];

        Assert.False(condition: SdfPrismProfile.IsValidConvexHull(vertices: dart));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.ConvexPolygon(
            dart,
            0f,
            SdfLift.Extrude,
            1f,
            material
        ));

        Assert.Contains(
            "convex",
            refusal.Message
        );
    }
    [Fact]
    public void ACounterclockwiseWindingIsRefusedByName() {
        // The exact rectangle from the first law, wound the opposite way.
        Vector2[] counterclockwise = [
            new(
                x: 1f,
                y: -1f
            ),
            new(
                x: 1f,
                y: 1f
            ),
            new(
                x: -1f,
                y: 1f
            ),
            new(
                x: -1f,
                y: -1f
            ),
        ];

        Assert.False(condition: SdfPrismProfile.IsValidConvexHull(vertices: counterclockwise));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.ConvexPolygon(
            counterclockwise,
            0f,
            SdfLift.Extrude,
            1f,
            material
        ));

        // The control: the same four points, clockwise, are admitted.
        Vector2[] clockwise = [.. counterclockwise];

        Array.Reverse(array: clockwise);
        Assert.True(condition: SdfPrismProfile.IsValidConvexHull(vertices: clockwise));
    }
    [Fact]
    public void ARectangleAuthoredAsFourVerticesMatchesRoundedRectangle() {
        const float HalfWidth = 1f;
        const float HalfHeight = 0.6f;
        const float HalfDepth = 1f;

        // Clockwise (X right, Y up): bottom-right, bottom-left, top-left, top-right.
        Vector2[] rectangle = [
            new(
                x: HalfWidth,
                y: -HalfHeight
            ),
            new(
                x: -HalfWidth,
                y: -HalfHeight
            ),
            new(
                x: -HalfWidth,
                y: HalfHeight
            ),
            new(
                x: HalfWidth,
                y: HalfHeight
            ),
        ];

        var convexEvaluator = ConvexPolygon(
            rectangle,
            HalfDepth
        );
        var roundedRectangleEvaluator = RoundedRectangle(
            halfDepth: HalfDepth,
            halfHeight: HalfHeight,
            halfWidth: HalfWidth
        );

        foreach (var (x, y, z) in new[] {
            (0.0, 0.0, 0.0),
            (0.9, 0.0, 0.0),
            (0.0, 0.5, 0.0),
            (1.1, 0.0, 0.0),
            (0.0, 0.0, 0.9),
            (0.0, 0.0, 1.2),
            (0.99, 0.59, 0.99),
        }) {
            var position = Position(
                x: x,
                y: y,
                z: z
            );

            Assert.True(condition: convexEvaluator.TryDistance(
                distance: out var convexDistance,
                material: out _,
                position: position
            ));
            Assert.True(condition: roundedRectangleEvaluator.TryDistance(
                distance: out var rectangleDistance,
                material: out _,
                position: position
            ));
            Assert.InRange(
                actual: ((double)(convexDistance - rectangleDistance)),
                high: 0.003,
                low: -0.003
            );
        }
    }
    [InlineData(3)]
    [InlineData(8)]
    [Theory]
    public void AVertexCountAtTheSupportedBoundsIsAdmitted(int count) {
        var vertices = new Vector2[count];
        var angleStep = ((2f * MathF.PI) / count);

        for (var i = 0; (i < count); i++) {
            var angle = (-angleStep * i);

            vertices[i] = new Vector2(
                x: MathF.Cos(x: angle),
                y: MathF.Sin(x: angle)
            );
        }

        Assert.True(condition: SdfPrismProfile.IsValidConvexHull(vertices: vertices));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ConvexPolygon(
            vertices,
            0f,
            SdfLift.Extrude,
            1f,
            material
        );
        _ = Assert.Single(collection: builder.Build().Instructions);
    }
    [InlineData(2)]
    [InlineData(9)]
    [Theory]
    public void AVertexCountOutsideTheSupportedRangeIsRefusedByName(int count) {
        var vertices = new Vector2[count];
        var angleStep = ((2f * MathF.PI) / count);

        // A regular (clockwise, since angle DECREASES with index — negative step) n-gon: always convex, so a
        // refusal at 2 or 9 vertices is purely a COUNT refusal, not a winding/convexity one.
        for (var i = 0; (i < count); i++) {
            var angle = (-angleStep * i);

            vertices[i] = new Vector2(
                x: MathF.Cos(x: angle),
                y: MathF.Sin(x: angle)
            );
        }

        Assert.False(condition: SdfPrismProfile.IsValidConvexHull(vertices: vertices));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.ConvexPolygon(
            vertices,
            0f,
            SdfLift.Extrude,
            1f,
            material
        ));
    }
    // THE LAW: every vertex lies in the unit square. The profile rides the shape's own XY scale like every other
    // Prism profile, and SdfSolidGeometry.Reach's Prism arm (sqrt(3) x max scale) covers exactly that frame — a vertex
    // past it would leave the cull bound short and clip the prism at its tile edges.
    [Fact]
    public void AVertexOutsideTheUnitSquareIsRefusedByName() {
        Vector2[] tooWide = [new(
                x: 1.5f,
                y: -1f
            ), new(
                x: -1f,
                y: -1f
            ), new(
                x: -1f,
                y: 1f
            ), new(
                x: 1.5f,
                y: 1f
            )];

        Assert.False(condition: SdfPrismProfile.IsValidConvexHull(vertices: tooWide));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.ConvexPolygon(
            tooWide,
            0f,
            SdfLift.Extrude,
            1f,
            material
        ));

        // The control: the same outline on the unit square's own boundary is admitted.
        Vector2[] onTheBoundary = [new(
                x: 1f,
                y: -1f
            ), new(
                x: -1f,
                y: -1f
            ), new(
                x: -1f,
                y: 1f
            ), new(
                x: 1f,
                y: 1f
            )];

        Assert.True(condition: SdfPrismProfile.IsValidConvexHull(vertices: onTheBoundary));
        _ = builder.ConvexPolygon(
            onTheBoundary,
            0f,
            SdfLift.Extrude,
            1f,
            material
        );
    }
    [Fact]
    public void RoundingFilletsTheCornersWithoutMovingTheFaces() {
        Vector2[] square = [
            new(
                x: 1f,
                y: -1f
            ),
            new(
                x: -1f,
                y: -1f
            ),
            new(
                x: -1f,
                y: 1f
            ),
            new(
                x: 1f,
                y: 1f
            ),
        ];

        var sharp = ConvexPolygon(
            cornerRadius: 0f,
            halfDepth: 1f,
            vertices: square
        );
        var rounded = ConvexPolygon(
            cornerRadius: 0.3f,
            halfDepth: 1f,
            vertices: square
        );

        // The faces stay put.
        Assert.True(condition: sharp.TryDistance(
            Position(
                x: 0,
                y: 1,
                z: 0
            ),
            out var sharpFace,
            out _
        ));
        Assert.True(condition: rounded.TryDistance(
            Position(
                x: 0,
                y: 1,
                z: 0
            ),
            out var roundedFace,
            out _
        ));
        Assert.InRange(
            actual: ((double)sharpFace),
            high: 0.01,
            low: -0.01
        );
        Assert.InRange(
            actual: ((double)roundedFace),
            high: 0.01,
            low: -0.01
        );

        // The corner the fillet cuts: inside the sharp square, outside the rounded one.
        Assert.True(condition: sharp.TryDistance(
            Position(
                x: 0.95,
                y: 0.95,
                z: 0
            ),
            out var sharpCorner,
            out _
        ));
        Assert.True(condition: rounded.TryDistance(
            Position(
                x: 0.95,
                y: 0.95,
                z: 0
            ),
            out var roundedCorner,
            out _
        ));
        Assert.True(condition: (sharpCorner < FixedQ4816.Zero));
        Assert.True(condition: (roundedCorner > FixedQ4816.Zero));
    }
    [Fact]
    public void TheStudyShoulderPentagonEvaluatesWithTheRightSignAtItsCentreAndOutside() {
        // moth-study.glsl's shoulderProfile: fiveSides(p, (-.12,.10), (.08,.20), (.29,.10), (.48,-.45), (.12,-.24)).
        Vector2[] pentagon = [
            new(
                x: -0.12f,
                y: 0.10f
            ),
            new(
                x: 0.08f,
                y: 0.20f
            ),
            new(
                x: 0.29f,
                y: 0.10f
            ),
            new(
                x: 0.48f,
                y: -0.45f
            ),
            new(
                x: 0.12f,
                y: -0.24f
            ),
        ];

        Assert.True(condition: SdfPrismProfile.IsValidConvexHull(vertices: pentagon));

        var evaluator = ConvexPolygon(
            pentagon,
            0.2f
        );

        // The unweighted centroid of a convex polygon always lies inside it.
        var centroid = (((((pentagon[0] + pentagon[1]) + pentagon[2]) + pentagon[3]) + pentagon[4]) / 5f);

        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: centroid.X,
                y: centroid.Y,
                z: 0
            ),
            out var inside,
            out _
        ));
        Assert.True(
            (inside < FixedQ4816.Zero),
            userMessage: $"the pentagon's own centroid read {((double)inside)}"
        );

        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: 5,
                y: 5,
                z: 0
            ),
            out var outside,
            out _
        ));
        Assert.True(condition: (outside > FixedQ4816.Zero));
    }
}
