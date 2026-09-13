using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The conservativeness contract of <see cref="SdfOp.GaussianPush"/>: the reach-independent operator-norm bound
/// (<c>1 + |Push|·sqrt(2/e)/min(Radii)</c>) mirrored against the packed step scale, a zero-push identity, a
/// finite-difference proof the warped field never changes faster than true distance can over a grid, the
/// single-instruction packing, and the warp-free evaluator's refusal by name.
/// </summary>
public sealed class GaussianPushLawTests {
    private static SdfProgram BuildBumpedSphere(Vector3 center, Vector3 radii, Vector3 push, float radius = 1.0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .GaussianPush(
            center: center,
            push: push,
            radii: radii
        )
            .Sphere(
            radius: radius,
            material: material
        )
            .Build();
    }
    // Reproduces SDF_OP_GAUSSIAN_PUSH's mapCore case plus the trailing stepScale multiply exactly.
    private static float BumpedFieldDistance(Vector3 p, Vector3 center, Vector3 radii, Vector3 push, float radius, float stepScale) {
        var offset = ((p - center) / radii);
        var weight = MathF.Exp(x: -Vector3.Dot(
            vector1: offset,
            vector2: offset
        ));
        var warped = (p - (push * weight));

        return ((warped.Length() - radius) * stepScale);
    }
    // KEEP IN SYNC with SdfProgram.GaussianPushLipschitz.
    private static float MirrorOperatorNorm(Vector3 radii, Vector3 push) {
        var minRadius = MathF.Min(
            x: MathF.Abs(x: radii.X),
            y: MathF.Min(
                x: MathF.Abs(x: radii.Y),
                y: MathF.Abs(x: radii.Z)
            )
        );
        const float SqrtTwoOverE = 0.8577638f;

        return (1.0f + ((push.Length() * SqrtTwoOverE) / minRadius));
    }

    [Fact]
    public void ANonFiniteCenterRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.GaussianPush(
            center: new Vector3(
                x: float.NaN,
                y: 0f,
                z: 0f
            ),
            radii: Vector3.One,
            push: Vector3.Zero
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "center"
        );
    }
    [Fact]
    public void ARadiusFloorsAwayFromZeroRatherThanDividingByZero() {
        // Radii of exactly zero would divide by zero in the raw formula; the builder clamps to GaussianPushMinRadius
        // instead of refusing, mirroring AxialProfile's finite-degenerate-input posture.
        var program = BuildBumpedSphere(
            center: Vector3.Zero,
            radii: Vector3.Zero,
            push: new Vector3(value: 0.1f)
        );

        Assert.True(condition: float.IsFinite(f: program.StepScale));
        Assert.True(condition: (program.StepScale > 0f));
    }
    [Fact]
    public void NonFiniteHeaderPayloadIsRefused() {
        var program = BuildBumpedSphere(
            Vector3.Zero,
            Vector3.One,
            Vector3.One
        );
        var instructions = program.Instructions.Select(selector: i => ((i.Op == SdfOp.GaussianPush)
            ? i with { Shape = BitConverter.SingleToUInt32Bits(value: float.NaN) }
            : i)).ToArray();

        Assert.Throws<ArgumentException>(testCode: () => new SdfProgram(
            instructions,
            [new SdfMaterial(Vector3.One)]
        ));
    }
    [Fact]
    public void OneInstructionPreservesAnisotropicRadiiAndAllPushComponents() {
        var program = BuildBumpedSphere(
            new(
                x: 1f,
                y: 2f,
                z: 3f
            ),
            new(
                x: 0.2f,
                y: 0.4f,
                z: 0.8f
            ),
            new(
                x: -0.3f,
                y: 0.7f,
                z: -0.6f
            )
        );
        var instruction = Assert.Single(
            collection: program.Instructions,
            predicate: i => (i.Op == SdfOp.GaussianPush)
        );

        Assert.Equal(
            new Vector4(
                w: -0.3f,
                x: 1f,
                y: 2f,
                z: 3f
            ),
            instruction.Data0
        );
        Assert.Equal(
            new Vector4(
                w: 0.7f,
                x: 0.2f,
                y: 0.4f,
                z: 0.8f
            ),
            instruction.Data1
        );
        Assert.Equal(
            -0.6f,
            BitConverter.UInt32BitsToSingle(value: instruction.Shape)
        );
    }
    [Fact]
    public void TheStepClampMatchesTheDerivedOperatorNorm() {
        var center = new Vector3(
            x: 0f,
            y: 0f,
            z: 1f
        );
        var radii = new Vector3(
            x: 0.3f,
            y: 0.4f,
            z: 0.2f
        );
        var push = new Vector3(
            x: 0f,
            y: 0f,
            z: 1.5f
        );

        var program = BuildBumpedSphere(
            center: center,
            radii: radii,
            push: push
        );
        var expected = (1.0f / MathF.Max(
            x: MirrorOperatorNorm(
                push: push,
                radii: radii
            ),
            y: 1.0f
        ));

        Assert.Equal(
            actual: program.StepScale,
            expected: expected,
            precision: 5
        );
        Assert.True(condition: (program.StepScale < 1.0f));
    }
    [Fact]
    public void TheWarpFreeEvaluatorRefusesTheOpByName() {
        var program = BuildBumpedSphere(
            center: Vector3.Zero,
            radii: Vector3.One,
            push: new Vector3(value: 0.2f)
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "GaussianPush"
        );
    }
    [Fact]
    public void TheWarpedFieldStaysAConservativeLowerBoundOnAGrid() {
        var center = new Vector3(
            x: 0.3f,
            y: 0.5f,
            z: -0.2f
        );
        var radii = new Vector3(
            x: 0.35f,
            y: 0.30f,
            z: 0.40f
        );
        var push = new Vector3(
            x: 0.4f,
            y: -0.3f,
            z: 0.6f
        );
        const float Radius = 1.0f;
        const float Step = 0.25f;
        const float Tolerance = 5.0e-4f;

        var program = BuildBumpedSphere(
            center: center,
            radii: radii,
            push: push
        );
        var stepScale = program.StepScale;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

        for (var x = -3.0f; (x <= 3.0f); x += Step) {
            for (var y = -3.0f; (y <= 3.0f); y += Step) {
                for (var z = -3.0f; (z <= 3.0f); z += Step) {
                    var p = new Vector3(
                        x: x,
                        y: y,
                        z: z
                    );
                    var d0 = BumpedFieldDistance(
                        center: center,
                        p: p,
                        push: push,
                        radii: radii,
                        radius: Radius,
                        stepScale: stepScale
                    );

                    foreach (var axis in axes) {
                        var d1 = BumpedFieldDistance(
                            center: center,
                            p: (p + (axis * Step)),
                            push: push,
                            radii: radii,
                            radius: Radius,
                            stepScale: stepScale
                        );

                        Assert.True(condition: (MathF.Abs(x: (d1 - d0)) <= (Step + Tolerance)));
                    }
                }
            }
        }
    }
    [Fact]
    public void ZeroPushIsAnExactIdentity() {
        var center = new Vector3(
            x: 0.2f,
            y: -0.1f,
            z: 0.3f
        );
        var radii = new Vector3(
            x: 0.5f,
            y: 0.5f,
            z: 0.5f
        );
        var push = Vector3.Zero;
        var program = BuildBumpedSphere(
            center: center,
            radii: radii,
            push: push
        );

        Assert.Equal(
            actual: program.StepScale,
            expected: 1.0f
        );

        Vector3[] points = [
            new(
                x: 3f,
                y: 0f,
                z: 0f
            ),
            new(
                x: 0f,
                y: 5f,
                z: 0f
            ),
            new(
                x: -2f,
                y: -4f,
                z: 1.5f
            ),
            new(
                x: 0.2f,
                y: 0.5f,
                z: -0.3f
            ),
        ];

        foreach (var point in points) {
            var expected = (point.Length() - 1.0f);
            var actual = BumpedFieldDistance(
                p: point,
                center: center,
                radii: radii,
                push: push,
                radius: 1.0f,
                stepScale: program.StepScale
            );

            Assert.Equal(
                actual: actual,
                expected: expected,
                precision: 5
            );
        }
    }
}
