using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SeamBlendLawTests {
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
    [InlineData(SdfBlendOp.GrooveSubtraction)]
    [InlineData(SdfBlendOp.PipeSubtraction)]
    [Theory]
    public void FixedFieldMatchesTheRoundTubeFormulaInsideOutsideAndOnTheSeam(SdfBlendOp blend) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.Plane(
            Vector3.UnitX,
            0f,
            material
        );
        builder.ResetPoint().Plane(
            Vector3.UnitY,
            0f,
            material,
            blend,
            0.25f
        );
        var program = builder.Build();
        var field = new SdfFieldEvaluator(program: program);

        Assert.InRange(
            program.StepScale,
            0.70710f,
            0.70711f
        );
        foreach (var (x, y) in new (double, double)[] { (0, 0), (.25, 0), (-.1, -.1), (.1, .1), (-2, .01), (.01, -2), (3, 4), (-3, -4) }) {
            var point = FixedPosition.FromLocal(local: new(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y),
                Z: FixedQ4816.Zero
            ));

            Assert.True(condition: field.TryDistance(
                distance: out var distance,
                material: out _,
                position: point
            ));
            var tube = (Math.Sqrt(d: ((x * x) + (y * y))) - .25);
            var expected = blend switch {
                SdfBlendOp.GrooveUnion => Math.Max(
                val1: Math.Min(
                    val1: x,
                    val2: y
                ),
                val2: -tube
            ),
                SdfBlendOp.PipeUnion => Math.Min(
                val1: Math.Min(
                    val1: x,
                    val2: y
                ),
                val2: tube
            ),
                SdfBlendOp.GrooveSubtraction => Math.Max(
                val1: Math.Max(
                    val1: x,
                    val2: -y
                ),
                val2: -tube
            ),
                SdfBlendOp.PipeSubtraction => Math.Min(
                val1: Math.Max(
                    val1: x,
                    val2: -y
                ),
                val2: tube
            ),
                _ => throw new ArgumentOutOfRangeException(paramName: nameof(blend))
            };

            Assert.InRange(
                actual: ((double)distance),
                high: (expected + 0.00005),
                low: (expected - 0.00005)
            );
        }
    }
    [Fact]
    public void MorphFieldMatchesLinearInterpolationAndPreservesUnitStepScale() {
        var builder = new SdfProgramBuilder();
        var materialA = builder.AddMaterial(material: new(Vector3.One));
        var materialB = builder.AddMaterial(material: new(Vector3.Zero));

        // Field evaluator has no dynamic transforms, so lane 0 reads as 0.
        // from = -1, to = 1 -> t = (0 - (-1)) / (1 - (-1)) = 0.5.
        builder.Sphere(
            1f,
            materialA
        );
        builder.PushFieldMorph(
            from: -1f,
            laneIndex: 0,
            to: 1f
        );
        builder.Sphere(
            3f,
            materialB
        );
        builder.PopField();

        var program = builder.Build();

        Assert.Equal(
            1f,
            program.StepScale
        );

        var field = new SdfFieldEvaluator(program: program);
        var origin = FixedPosition.FromLocal(local: FixedVector3.Zero);

        Assert.True(condition: field.TryDistance(
            distance: out var distance,
            material: out var material,
            position: origin
        ));

        // Sphere(1) distance at origin is -1, Sphere(3) distance is -3. Lerp with t=0.5 gives -2.
        Assert.InRange(
            actual: ((double)distance),
            high: -1.9999,
            low: -2.0001
        );
        // At t = 0.5, tie-break gives candidate (materialB).
        Assert.Equal(
            actual: material,
            expected: materialB
        );
    }
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
    [InlineData(SdfBlendOp.GrooveSubtraction)]
    [InlineData(SdfBlendOp.PipeSubtraction)]
    [Theory]
    public void RepeatedSeamsFoldTheUnequalOperandBounds(SdfBlendOp blend) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        for (var i = 0; (i < 5); i++) {
            builder.ResetPoint().Sphere(
            1f,
            material,
            blend,
            0.2f
        );
        }
        Assert.InRange(
            builder.Build().StepScale,
            ((1f / MathF.Sqrt(x: 5f)) - 0.000001f),
            ((1f / MathF.Sqrt(x: 5f)) + 0.000001f)
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void StairsFieldMatchesTriangleWaveAndPreservesUnitStepScale(bool subtraction) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));
        var radius = 0.5f;
        var steps = 2;

        builder.Plane(
            Vector3.UnitX,
            0f,
            material
        );
        builder.PushFieldStairs(
            radius: radius,
            steps: steps,
            subtraction: subtraction
        );
        builder.ResetPoint().Plane(
            Vector3.UnitY,
            0f,
            material
        );
        builder.PopField();

        var program = builder.Build();

        Assert.Equal(
            1f,
            program.StepScale
        );

        var field = new SdfFieldEvaluator(program: program);
        var s = (((double)radius) / steps);
        var period = (2.0 * s);

        foreach (var (x, y) in new (double, double)[] { (0, 0), (.25, 0), (-.1, -.1), (.1, .1), (-2, .01), (.01, -2), (3, 4), (-3, -4) }) {
            var point = FixedPosition.FromLocal(local: new(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y),
                Z: FixedQ4816.Zero
            ));

            Assert.True(condition: field.TryDistance(
                distance: out var distance,
                material: out _,
                position: point
            ));

            double expected;

            if (subtraction) {
                var u = (-y - radius);
                var arg = ((u - x) + s);
                var m = (arg - (period * Math.Floor(d: (arg / period))));
                var w = (m - s);
                var dStairs = (0.5 * ((u + x) + Math.Abs(value: w)));

                expected = Math.Max(
                    val1: Math.Max(
                        val1: x,
                        val2: -y
                    ),
                    val2: -dStairs
                );
            } else {
                var u = (y - radius);
                var arg = ((u - x) + s);
                var m = (arg - (period * Math.Floor(d: (arg / period))));
                var w = (m - s);
                var dStairs = (0.5 * ((u + x) + Math.Abs(value: w)));

                expected = Math.Min(
                    val1: Math.Min(
                        val1: x,
                        val2: y
                    ),
                    val2: dStairs
                );
            }

            Assert.InRange(
                actual: ((double)distance),
                high: (expected + 0.0001),
                low: (expected - 0.0001)
            );
        }
    }
    [InlineData(0f)]
    [InlineData(-0.25f)]
    [InlineData(float.NaN)]
    [Theory]
    public void StairsRefusesANonPositiveRadiusAtEveryDoor(float radius) {
        foreach (var subtraction in new[] { false, true }) {
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().PushFieldStairs(
                radius: radius,
                steps: 2,
                subtraction: subtraction
            ));
        }

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().PushField().PopFieldStairsUnion(
            radius: radius,
            steps: 2
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().PushField().PopFieldStairsSubtraction(
            radius: radius,
            steps: 2
        ));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.Sphere(
            1f,
            material
        );
        builder.PushFieldStairs(
            radius: 0.5f,
            steps: 2,
            subtraction: true
        );
        builder.Sphere(
            0.5f,
            material
        );
        builder.PopField();

        var packed = builder.Build().Instructions
            .Select(selector: instruction => ((instruction.Op == SdfOp.PopField)
                ? (instruction with { Data1 = instruction.Data1 with { X = radius } })
                : instruction
            ))
            .ToArray();

        Assert.ThrowsAny<ArgumentException>(testCode: () => new SdfProgram(
            packed,
            [new SdfMaterial(Albedo: Vector3.One)]
        ));
    }
    [Fact]
    public void StairsSubtractionResolvesItsMaterialByTheSubtractionRule() {
        var builder = new SdfProgramBuilder();
        var subject = builder.AddMaterial(material: new(Vector3.One));
        var carve = builder.AddMaterial(material: new(Vector3.Zero));

        builder.Sphere(
            1f,
            subject
        );
        builder.PushFieldStairs(
            radius: 0.2f,
            steps: 2,
            subtraction: true
        );
        builder.Sphere(
            0.5f,
            carve
        );
        builder.PopField();

        var field = new SdfFieldEvaluator(program: builder.Build());

        // Inside the carve (-b > a) the carved surface shows, so the carve's material wins; near the subject's rim the
        // carve is farther than the subject's own face and the subject keeps its material.
        foreach (var (x, expected) in new (double, int)[] { (0, carve), (0.9, subject) }) {
            Assert.True(condition: field.TryDistance(
                distance: out _,
                material: out var material,
                position: FixedPosition.FromLocal(local: new(
                    X: FixedQ4816.FromDouble(value: x),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                ))
            ));
            Assert.Equal(
                actual: material,
                expected: expected
            );
        }
    }
}
