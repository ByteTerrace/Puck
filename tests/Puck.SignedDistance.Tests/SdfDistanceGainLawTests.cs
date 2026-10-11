using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: an instance's distance gain (<see cref="SdfProgram.InstanceDistanceGain"/>) is a certificate. Wherever a
/// point lies outside the instance's packed influence sphere, the instance's field is at least the gain times the gap
/// to that sphere, which is exactly what mapCore's whole-instance rejection (<c>sdfInstanceCannotWin</c>) and the
/// group-mask word summary test. A gauge (<see cref="SdfShapeType.Superellipsoid"/>) and a non-uniform
/// <see cref="SdfOp.Scale"/> grow more slowly than distance, so they certify less than one, never nothing, and a
/// smooth union's sag widens the packed sphere until that gain absorbs it. The fixed-point evaluator, the GPU field's
/// CPU mirror, is the oracle.
/// </summary>
public sealed class SdfDistanceGainLawTests {
    private const int Directions = 96;
    private const float FieldTolerance = 0.0005f;

    public static TheoryData<float, float, float, float> Gauges => new() {
        { 2f, 0.3f, 0.05f, 0.08f },
        { 2.1f, 0.05f, 0.2f, 0.02f },
        { 2.6f, 0.25f, 0.25f, 0.04f },
        { 3f, 0.4f, 0.1f, 0.1f },
        { 8f, 0.2f, 0.05f, 0.3f },
    };

    [MemberData(nameof(Gauges))]
    [Theory]
    public void AnAnisotropicGaugeCertifiesItsSlowerGrowth(float exponent, float x, float y, float z) {
        var radii = new Vector3(x: x, y: y, z: z);
        var program = Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Op(SdfOp.Translate, new Vector4(w: 0f, x: 1f, y: 2f, z: -1f)),
            Gauge(blend: SdfBlendOp.Union, exponent: exponent, radii: radii), Op(SdfOp.PopField)],
            center: new Vector3(x: 1f, y: 2f, z: -1f), radius: radii.Length());
        var gain = program.InstanceDistanceGain(index: 0);
        var anisotropy = (MathF.Min(x: x, y: MathF.Min(x: y, y: z)) / MathF.Max(x: x, y: MathF.Max(x: y, y: z)));

        Assert.InRange(actual: gain, high: anisotropy, low: (0.5f * anisotropy));
        AssertCertified(center: new Vector3(x: 1f, y: 2f, z: -1f), program: program);
    }
    [Fact]
    public void ASmoothUnionOfGaugesWidensItsBoundUntilTheGainAbsorbsItsSag() {
        const float Smooth = 0.06f;
        var radii = new Vector3(x: 0.3f, y: 0.04f, z: 0.05f);
        var program = Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Gauge(blend: SdfBlendOp.Union, exponent: 2.1f, radii: radii),
            Op(SdfOp.ResetPoint), Op(SdfOp.Translate, new Vector4(w: 0f, x: 0.25f, y: 0.05f, z: 0f)),
            Gauge(blend: SdfBlendOp.SmoothUnion, exponent: 2.1f, radii: radii, smooth: Smooth), Op(SdfOp.PopField)],
            center: Vector3.Zero, radius: ((0.25f + 0.05f) + radii.Length()));
        var gain = program.InstanceDistanceGain(index: 0);
        var cost = program.InspectInstance(index: 0);

        Assert.True(condition: (gain > 0f), userMessage: "A smooth union of gauges certifies a gain.");
        Assert.True(condition: (cost.Halo >= (Smooth / gain)), userMessage: $"The halo {cost.Halo} absorbs the sag {Smooth} at gain {gain}.");
        AssertCertified(center: Vector3.Zero, program: program);
    }
    [Fact]
    public void ANonUniformScaleCertifiesItsSmallestOverLargestAxis() {
        var program = Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Op(SdfOp.Scale, new Vector4(w: 1f, x: 4f, y: 1f, z: 2f)),
            Sphere(blend: SdfBlendOp.Union, radius: 0.1f), Op(SdfOp.PopField)], center: Vector3.Zero, radius: 0.4f);

        Assert.Equal(expected: 0.25f, actual: program.InstanceDistanceGain(index: 0));
    }
    [Fact]
    public void AnOperandOnlySomeWalksApplyOrAnApproximateOneCertifiesOnlyWhatSurvivesIt() {
        // A clipper the shadow and ambient walks drop leaves the subject, whose solid the clipped bound need not contain.
        Assert.Equal(expected: 0f, actual: Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Sphere(blend: SdfBlendOp.Union, radius: 2f),
            Op(SdfOp.ResetPoint), Sphere(blend: SdfBlendOp.Intersection, radius: 0.5f) with { Secondary = false }, Op(SdfOp.PopField)],
            center: Vector3.Zero, radius: 0.5f).InstanceDistanceGain(index: 0));
        // A subtraction never lowers its subject, whatever it subtracts; an optional union only adds an operand.
        Assert.Equal(expected: 1f, actual: Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Sphere(blend: SdfBlendOp.Union, radius: 1f),
            Op(SdfOp.ResetPoint), Sphere(blend: SdfBlendOp.Union, radius: 0.5f) with { Detail = true },
            Op(SdfOp.ResetPoint), Gauge(blend: SdfBlendOp.Subtraction, exponent: 3f, radii: new Vector3(x: 0.2f, y: 0.9f, z: 0.2f)), Op(SdfOp.PopField)],
            center: Vector3.Zero, radius: 1f).InstanceDistanceGain(index: 0));
        // A domain operation outside the similarities and scales certifies nothing.
        Assert.Equal(expected: 0f, actual: Build(instructions: [Op(SdfOp.PushField), Op(SdfOp.ResetPoint),
            Op(SdfOp.Elongate, new Vector4(w: 0f, x: 0.5f, y: 0f, z: 0f)), Sphere(blend: SdfBlendOp.Union, radius: 1f), Op(SdfOp.PopField)],
            center: Vector3.Zero, radius: 1.5f).InstanceDistanceGain(index: 0));
    }

    // Samples the field along a spread of directions at gaps from just outside the packed sphere to well past it.
    private static void AssertCertified(SdfProgram program, Vector3 center) {
        var gain = program.InstanceDistanceGain(index: 0);
        var radius = program.InspectInstance(index: 0).BoundRadius;
        var evaluator = new SdfFieldEvaluator(program: program);
        var golden = (MathF.PI * (3f - MathF.Sqrt(x: 5f)));

        Assert.True(condition: (gain > 0f));
        for (var index = 0; (index < Directions); index++) {
            var y = (1f - ((2f * (index + 0.5f)) / Directions));
            var ring = MathF.Sqrt(x: (1f - (y * y)));
            var direction = new Vector3(x: (ring * MathF.Cos(x: (golden * index))), y: y, z: (ring * MathF.Sin(x: (golden * index))));

            foreach (var gap in ((ReadOnlySpan<float>)[0.002f, 0.01f, 0.05f, 0.2f, 1f, 4f])) {
                var point = (center + (direction * (radius + gap)));

                Assert.True(condition: evaluator.TryDistance(FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point)), out var distance, out _));
                Assert.True(condition: (((float)distance) >= ((gain * gap) - FieldTolerance)),
                    userMessage: $"At {point}, {gap} outside the packed sphere, the field {((float)distance)} fell below the certified {(gain * gap)}.");
            }
        }
    }
    private static SdfProgram Build(SdfInstruction[] instructions, Vector3 center, float radius) => new(
        instructions: instructions,
        instances: [new SdfInstanceRange(First: 0, End: instructions.Length, IsDynamic: false, Center: center, Radius: radius, Slot: 0)],
        materials: [new SdfMaterial(Albedo: Vector3.One)],
        screenSurfaces: null
    );
    private static SdfInstruction Gauge(SdfBlendOp blend, float exponent, Vector3 radii, float smooth = 0f) => new(
        Op: SdfOp.ShapeBlend,
        Shape: ((uint)SdfShapeType.Superellipsoid),
        Blend: ((uint)blend),
        Material: 0,
        Data0: new Vector4(value: radii, w: exponent),
        Data1: new Vector4(w: (1f / radii.Z), x: smooth, y: (1f / radii.X), z: (1f / radii.Y))
    );
    private static SdfInstruction Op(SdfOp op, Vector4 data = default) => new(
        Op: op,
        Shape: 0,
        Blend: ((uint)SdfBlendOp.Union),
        Material: 0,
        Data0: data,
        Data1: Vector4.Zero
    );
    private static SdfInstruction Sphere(SdfBlendOp blend, float radius) => new(
        Op: SdfOp.ShapeBlend,
        Shape: ((uint)SdfShapeType.Sphere),
        Blend: ((uint)blend),
        Material: 0,
        Data0: new Vector4(w: 0f, x: radius, y: 0f, z: 0f),
        Data1: Vector4.Zero
    );
}
