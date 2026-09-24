using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// A bound on a cast never hides a nearer surface: <see cref="SdfFieldEvaluator.Raycast"/> bounded at <c>B</c>
/// answers exactly what the unbounded cast answers whenever either one's hit lies nearer than <c>B</c>. This is the
/// reference a traversal bounded by mesh depth is checked against.
/// </summary>
public sealed class SdfFieldEvaluatorBoundAgreementLawTests {
    // A ground plane, a sphere, a rounded box and a smooth-blended sphere: exact, rounded, and blended fields, so the
    // march takes long, short and throttled steps before its hits.
    private static SdfFieldEvaluator BuildScene() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var other = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

        _ = builder
            .Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: 0f,
            y: 1f,
            z: -4f
        ))
            .Sphere(
            radius: 1f,
            material: other
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: 2.5f,
            y: 0.75f,
            z: -6f
        ))
            .Box(
            halfExtents: new Vector3(
                x: 0.75f,
                y: 0.75f,
                z: 0.5f
            ),
            round: 0.1f,
            material: material
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: -2f,
            y: 0.6f,
            z: -5f
        ))
            .Sphere(
            radius: 0.6f,
            material: other,
            blend: SdfBlendOp.SmoothUnion,
            smooth: 0.4f
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

    [Fact]
    public void ABoundedCastAgreesWithTheUnboundedCastNearerThanTheBound() {
        var evaluator = BuildScene();
        var tick = FixedQ4816.Epsilon;
        FixedPosition[] origins = [
            FixedPosition.FromLocal(local: Vector(x: 0.0, y: 1.5, z: 2.0)),
            FixedPosition.FromLocal(local: Vector(x: 1.25, y: 0.3, z: 0.0)),
            FixedPosition.FromLocal(local: Vector(x: -3.0, y: 4.0, z: -1.0)),
        ];
        FixedQ4816[] fixedBounds = [
            FixedQ4816.FromDouble(value: 0.25),
            FixedQ4816.FromDouble(value: 2.0),
            FixedQ4816.FromDouble(value: 5.5),
            FixedQ4816.FromInteger(value: 12L),
        ];
        var nearerHits = 0;
        var hiddenBeyondBound = 0;

        foreach (var origin in origins) {
            for (var yaw = -8; (yaw <= 8); yaw++) {
                for (var pitch = -6; (pitch <= 2); pitch++) {
                    var direction = Vector(
                        x: (yaw * 0.125),
                        y: (pitch * 0.125),
                        z: -1.0
                    );
                    var unboundedHit = evaluator.Raycast(
                        dir: direction,
                        hit: out var unbounded,
                        maxDist: FixedQ4816.MaxValue,
                        origin: origin
                    );
                    // The tightest discriminating bounds sit one tick either side of the unbounded hit.
                    var bounds = new List<FixedQ4816>(collection: fixedBounds);

                    if (unboundedHit) {
                        bounds.Add(item: (unbounded.Distance + tick));
                        bounds.Add(item: unbounded.Distance);
                        bounds.Add(item: (unbounded.Distance - tick));
                    }

                    foreach (var bound in bounds) {
                        if (bound <= FixedQ4816.Zero) {
                            continue;
                        }

                        var boundedHit = evaluator.Raycast(
                            dir: direction,
                            hit: out var bounded,
                            maxDist: bound,
                            origin: origin
                        );

                        if (unboundedHit && (unbounded.Distance < bound)) {
                            Assert.True(condition: boundedHit);
                            Assert.Equal(actual: bounded, expected: unbounded);
                            nearerHits++;
                        }
                        if (boundedHit && (bounded.Distance < bound)) {
                            Assert.True(condition: unboundedHit);
                            Assert.Equal(actual: bounded, expected: unbounded);
                        }
                        if (unboundedHit && (unbounded.Distance > bound) && !boundedHit) {
                            hiddenBeyondBound++;
                        }
                    }
                }
            }
        }

        // Neither side of the law is vacuous: casts agree below their bounds, and bounds stop casts short of hits.
        Assert.True(condition: (nearerHits > 100));
        Assert.True(condition: (hiddenBeyondBound > 100));
    }
}
