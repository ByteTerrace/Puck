using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The wide reader answers the field the fixed-point reader answers, in double precision: a regional evaluator's surface
// normal, read through it, points where the fixed-point gradient points. The two round differently, so each law holds
// them to an angle, never to bits.
public sealed class SdfFieldEvaluatorWideLawTests {
    // The walks at one point that record its least region: two for each of a region's four sizes.
    private const int Walks = 8;
    // The cosine two unit normals keep: the readers differ by raws in a difference of two taps a hundredth apart.
    private const float Agreement = 0.999f;

    private static FixedPosition At(Vector3 point) =>
        FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: point.X), Y: FixedQ4816.FromDouble(value: point.Y), Z: FixedQ4816.FromDouble(value: point.Z)));
    private static IEnumerable<Vector3> Lattice(Vector3 low, Vector3 high, int steps) {
        for (var x = 0; (x <= steps); x++) {
            for (var y = 0; (y <= steps); y++) {
                for (var z = 0; (z <= steps); z++) {
                    yield return new Vector3(x: (low.X + (((high.X - low.X) * x) / steps)), y: (low.Y + (((high.Y - low.Y) * y) / steps)), z: (low.Z + (((high.Z - low.Z) * z) / steps)));
                }
            }
        }
    }
    private static void AssertNormalsAgree(SdfProgram program, IEnumerable<Vector3> points) {
        var plain = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact);
        var regional = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact, regional: true);
        var walksBefore = SdfFieldEvaluator.WideWork.Read(kind: SdfFieldEvaluator.WideWalks);
        var compared = 0;

        foreach (var point in points) {
            var position = At(point: point);

            for (var walk = 0; (walk < Walks); walk++) {
                _ = regional.TryDistance(distance: out _, material: out _, position: position);
            }

            if (!plain.TryFieldGradient(gradient: out var gradient, position: position)) {
                continue;
            }

            Assert.True(condition: regional.TrySurfaceNormal(normal: out var normal, position: position), userMessage: $"No normal at {point}.");

            var expected = Vector3.Normalize(value: gradient.ToVector3());

            Assert.True(condition: (Vector3.Dot(vector1: expected, vector2: normal) >= Agreement), userMessage: $"At {point} the fixed-point gradient is {expected} and the wide normal {normal}.");
            compared++;
        }

        Assert.True(condition: (compared > 0));
        Assert.True(condition: (SdfFieldEvaluator.WideWork.Read(kind: SdfFieldEvaluator.WideWalks) > walksBefore), userMessage: "No normal was read through the wide reader.");
    }

    // The primitives with shared formulas under translation, rotation and scale, joined hard, smoothly and by a carve.
    [Fact]
    public void ShapesAndBlendsReadAsTheFixedPointReaderDoes() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -2f, z: 0f)).Box(halfExtents: new Vector3(x: 6f, y: 1f, z: 6f), material: material, round: 0.1f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -2f, y: 0f, z: 0.5f)).Sphere(blend: SdfBlendOp.SmoothUnion, material: material, radius: 1f, smooth: 0.4f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1.5f, y: 0f, z: -1f)).Rotate(rotation: Quaternion.CreateFromYawPitchRoll(pitch: 0.4f, roll: 0.2f, yaw: 0.7f))
            .Superellipsoid(blend: SdfBlendOp.SmoothUnion, exponent: 4f, material: material, radii: new Vector3(x: 1.2f, y: 0.6f, z: 0.9f), smooth: 0.25f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 3.5f, y: 0f, z: 2f)).Scale(scale: new Vector3(value: 1.5f)).Capsule(endpoint: new Vector3(x: 0f, y: 1.5f, z: 0f), material: material, radius: 0.4f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1.5f, y: 0.5f, z: -1f)).Sphere(blend: SdfBlendOp.Subtraction, material: material, radius: 0.5f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -4f, y: 0f, z: -3f)).Sphere(blend: SdfBlendOp.ChamferUnion, material: material, radius: 0.8f, smooth: 0.2f);

        AssertNormalsAgree(points: Lattice(high: new Vector3(x: 5.37f, y: 2.41f, z: 3.63f), low: new Vector3(x: -5.21f, y: -0.87f, z: -3.79f), steps: 9), program: builder.Build());
    }
    // A fold's copies, a relieved scope and a scope popped by a smooth union: the stream's own semantics.
    [Fact]
    public void FoldsReliefsAndScopesReadAsTheFixedPointReaderDoes() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Instance(boundCenter: new Vector3(x: 0f, y: -4f, z: 0f), boundRadius: 12f, emit: instance => {
            _ = instance.PushField(compose: SdfBlendOp.Union).ResetPoint().Translate(offset: new Vector3(x: 0f, y: -4f, z: 0f)).Sphere(material: material, radius: 4f);
            _ = instance.NoiseDisplace(amplitude: 0.25f, frequency: 1.5f, octaves: 2);
            _ = instance.PopField();
        });
        _ = builder.ResetPoint().RepeatLimited(limit: new Vector3(x: 2f, y: 0f, z: 0f), spacing: new Vector3(x: 2f, y: 1000f, z: 1000f)).Translate(offset: new Vector3(x: 0f, y: 1.5f, z: 0f))
            .Sphere(blend: SdfBlendOp.SmoothUnion, material: material, radius: 0.4f, smooth: 0.2f);
        _ = builder.PushField(compose: SdfBlendOp.SmoothUnion, smooth: 0.3f).ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.5f, z: 3f)).Box(halfExtents: new Vector3(value: 0.6f), material: material, round: 0f).PopField();

        AssertNormalsAgree(points: Lattice(high: new Vector3(x: 4.43f, y: 2.53f, z: 4.17f), low: new Vector3(x: -4.61f, y: -0.29f, z: -1.33f), steps: 9), program: builder.Build());
    }
}
