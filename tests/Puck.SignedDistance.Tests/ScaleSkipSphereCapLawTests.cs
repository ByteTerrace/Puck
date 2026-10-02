using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class ScaleSkipSphereCapLawTests {
    [InlineData("sweep", 0.5f)]
    [InlineData("sweep", 1f)]
    [InlineData("sweep", 2f)]
    [InlineData("path", 0.5f)]
    [InlineData("path", 1f)]
    [InlineData("path", 2f)]
    [InlineData("stroke", 0.5f)]
    [InlineData("stroke", 1f)]
    [InlineData("stroke", 2f)]
    [Theory]
    public void ALocalDistanceCapCannotHideAWinningScaledCandidate(string shape, float scale) {
        const float FarDistance = 1e9f;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().Scale(scale: new Vector3(value: scale));
        if (shape == "sweep") {
            builder.Sweep(
                a: -Vector3.UnitY,
                b: Vector3.Zero,
                c: Vector3.UnitY,
                radiusStart: 0.1f,
                radiusEnd: 0.1f,
                bulge: 0f,
                strands: 1,
                twist: 0f,
                strandOffset: 0f,
                material: material
            );
        } else {
            builder.Path(
                profile: new SdfPathProfile(
                    Contours: [new SdfPathContour(new Vector2(x: -0.5f, y: -0.5f), [
                        new SdfPathSegment(new Vector2(x: 0.5f, y: -0.5f)),
                        new SdfPathSegment(new Vector2(x: 0.5f, y: 0.5f)),
                        new SdfPathSegment(new Vector2(x: -0.5f, y: 0.5f)),
                    ])],
                    Stroke: ((shape == "stroke") ? new SdfPathStroke(RadiusStart: 0.1f, RadiusEnd: 0.1f) : null)
                ),
                scale: Vector2.One,
                halfDepth: 0.1f,
                material: material
            );
        }
        var program = builder.Build();
        var shapeIndex = Enumerable.Range(start: 0, count: program.Instructions.Count).Single(predicate: index => (program.Instructions[index].Op == SdfOp.ShapeBlend));
        var point = new Vector3(x: 2e9f, y: 0f, z: 0f);
        // Each local primitive is over 1e9 from this sample, so its shader minimum saturates. Extrusion at z=0
        // leaves the path cap unchanged; the straight sweep has zero conservative margin.
        var candidate = (FarDistance * scale);
        var expected = MathF.Min(x: FarDistance, y: candidate);

        foreach (var sphere in new[] { program.ShapeSkipSphere(instruction: shapeIndex), program.SegmentSkipSphere(segment: 0) }) {
            var clearance = MathF.Max(x: (FarDistance + sphere.Radius), y: 0f);
            var skipped = ((sphere.Mode != SdfProgram.BoundModeNone) && (Vector3.DistanceSquared(value1: point, value2: sphere.Center) >= (clearance * clearance)));

            Assert.Equal(actual: (skipped ? FarDistance : expected), expected: expected);
            Assert.Equal(expected: ((scale < 1f) ? SdfProgram.BoundModeNone : SdfProgram.BoundModeStatic), actual: sphere.Mode);
        }
    }
}
