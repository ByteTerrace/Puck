using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed partial class SdfFieldEvaluatorCullBoundaryLawTests {
    // One emitter, one interpreter. The existing Chain helper inserts zero Elongate only in the reference,
    // which has no Instance metadata. Its points/intervals are identical and both cull mechanisms are disabled.
    private static (SdfFieldEvaluator Field, SdfFieldEvaluator Reference, int Instructions, int ReferenceInstructions) SuperellipsoidPair(
        Action<SdfProgramBuilder, bool> emit
    ) {
        SdfProgram Program(bool reference) {
            var builder = new SdfProgramBuilder();
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));
            emit(builder, reference);
            return builder.Build();
        }

        var program = Program(reference: false);
        var referenceProgram = Program(reference: true);
        var field = new SdfFieldEvaluator(program: program);
        var reference = new SdfFieldEvaluator(program: referenceProgram);

        Assert.Equal(expected: reference.Frame, actual: field.Frame);
        Assert.Equal(expected: reference.StepScale, actual: field.StepScale);
        return (field, reference, program.Instructions.Count, referenceProgram.Instructions.Count);
    }

    private static FixedPosition RawPosition(long x, long y = 0L, long z = 0L) => FixedPosition.FromLocal(local: new FixedVector3(
        X: FixedQ4816.FromRawBits(value: x),
        Y: FixedQ4816.FromRawBits(value: y),
        Z: FixedQ4816.FromRawBits(value: z)
    ));

    private static void AssertSuperellipsoidAnswers(
        (SdfFieldEvaluator Field, SdfFieldEvaluator Reference, int Instructions, int ReferenceInstructions) pair,
        FixedPosition position,
        long halfRaw = 0L,
        int? material = null
    ) {
        Assert.True(condition: pair.Reference.TryDistance(position: position, distance: out var expected, material: out var expectedMaterial, instructionsWalked: out var referenceWalked));
        // This is a functional check that the reference still skips nothing, even if zero-op lowering changes later.
        Assert.Equal(expected: pair.ReferenceInstructions, actual: referenceWalked);
        Assert.True(condition: pair.Field.TryDistance(position: position, distance: out var actual, material: out var actualMaterial));
        Assert.Equal(expected: expected, actual: actual);
        Assert.Equal(expected: expectedMaterial, actual: actualMaterial);
        if (material is { } expectedWinner) { Assert.Equal(expected: expectedWinner, actual: actualMaterial); }

        var expectedGradientFound = pair.Reference.TryFieldGradient(position: position, gradient: out var expectedGradient);
        var actualGradientFound = pair.Field.TryFieldGradient(position: position, gradient: out var actualGradient);
        Assert.Equal(expected: expectedGradientFound, actual: actualGradientFound);
        Assert.Equal(expected: expectedGradient, actual: actualGradient);

        Assert.True(condition: position.TryDelta(origin: FixedPosition.Zero, delta: out var local));
        var half = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: halfRaw),
            Y: FixedQ4816.FromRawBits(value: halfRaw),
            Z: FixedQ4816.FromRawBits(value: halfRaw)
        );
        var lower = FixedPosition.FromLocal(local: (local - half));
        var upper = FixedPosition.FromLocal(local: (local + half));
        var expectedBoxFound = pair.Reference.TryDistanceBounds(lower: lower, upper: upper, distance: out var expectedBox);
        var actualBoxFound = pair.Field.TryDistanceBounds(lower: lower, upper: upper, distance: out var actualBox);
        Assert.Equal(expected: expectedBoxFound, actual: actualBoxFound);
        Assert.Equal(expected: expectedBox, actual: actualBox);
    }

    [InlineData(2f)]
    [InlineData(2.0000152587890625f)]
    [InlineData(2.05f)]
    [InlineData(2.5f)]
    [InlineData(3f)]
    [InlineData(7.9999847412109375f)]
    [InlineData(8f)]
    [Theory]
    public void SuperellipsoidPointCertificatesKeepTransformedScopedAndTiedAnswers(float exponent) {
        var pair = SuperellipsoidPair((builder, reference) => {
            _ = builder.Plane(normal: Vector3.UnitY, offset: 2f, material: 0);

            for (var index = 0; (index < 4); index++) {
                var center = new Vector3(x: ((index - 2) * 10f), y: 2f, z: 0f);
                if (!reference) { _ = builder.BeginInstance(boundCenter: center, boundRadius: 6f); }
                _ = builder.PushField(compose: SdfBlendOp.Union);
                Chain(builder: builder, reference: reference, offset: center);
                _ = builder.Rotate(rotation: new Quaternion(x: 0.1f, y: 0.2f, z: 0.05f, w: 0.9733961f));
                _ = builder.Scale(scale: new Vector3(value: 1.25f));
                _ = builder.MirrorX();
                _ = builder.Translate(offset: new Vector3(x: 0.3f, y: -0.1f, z: 0.2f));
                _ = builder.Superellipsoid(radii: new Vector3(x: 0.2f, y: 1.1f, z: 0.7f), exponent: exponent, material: 1);
                Chain(builder: builder, reference: reference, offset: (center + Vector3.UnitX));
                _ = builder.Superellipsoid(radii: new Vector3(x: 0.7f, y: 0.4f, z: 0.5f), exponent: exponent, material: 2, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
                Chain(builder: builder, reference: reference, offset: (center + new Vector3(x: 0f, y: 0.2f, z: 0f)));
                _ = builder.Superellipsoid(radii: new Vector3(x: 0.15f, y: 0.2f, z: 0.15f), exponent: exponent, material: 0, blend: SdfBlendOp.Subtraction);
                Chain(builder: builder, reference: reference, offset: center);
                _ = builder.Superellipsoid(radii: new Vector3(value: 2f), exponent: exponent, material: 0, blend: SdfBlendOp.SmoothIntersection, smooth: 0.2f);
                _ = builder.PopField();
                if (!reference) { _ = builder.EndInstance(); }
            }

            // Equal distances retain the first material even with candidate instance metadata present.
            foreach (var material in ((int[])[1, 2,])) {
                var center = new Vector3(x: 35f, y: 0f, z: 0f);
                if (!reference) { _ = builder.BeginInstance(boundCenter: center, boundRadius: 3f); }
                Chain(builder: builder, reference: reference, offset: center);
                _ = builder.Superellipsoid(radii: new Vector3(x: 0.4f, y: 0.8f, z: 0.6f), exponent: exponent, material: material);
                if (!reference) { _ = builder.EndInstance(); }
            }
        });

        foreach (var x in ((double[])[-20.0, -10.0, 0.0, 10.0, 20.0, 35.0,])) {
            foreach (var y in ((double[])[-0.3, 0.2, 2.2, 4.5,])) {
                foreach (var z in ((double[])[-0.4, 0.0, 1.0,])) {
                    AssertSuperellipsoidAnswers(pair: pair, position: Position(x: x, y: y, z: z), halfRaw: ((1L << FixedQ4816.FractionBitCount) / 4L));
                }
            }
        }
        AssertSuperellipsoidAnswers(pair: pair, position: Position(x: 35.0, y: 0.0, z: 0.0), material: 1);
    }

    [InlineData(0.0001f, 0.0002f, 0.0004f, 2.05f)]
    [InlineData(-0.2f, 1.1f, 0.7f, 2f)]
    [InlineData(0.2f, 1.1f, 0.7f, 8f)]
    [InlineData(0.0001f, 1000000000000f, 1000000000000f, 2.05f)]
    [InlineData(67108860f, 134217728f, 268435456f, 2.5f)]
    [InlineData(67108864f, 134217728f, 268435456f, 2.05f)]
    [InlineData(67108872f, 134217728f, 268435456f, 8f)]
    [Theory]
    public void SuperellipsoidRatioBranchesKeepTheirExactPointGradientAndBoxAnswers(float x, float y, float z, float exponent) {
        var pair = SuperellipsoidPair((builder, reference) => {
            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Superellipsoid(radii: new Vector3(x: x, y: y, z: z), exponent: exponent, material: 1);
        });
        const long Threshold = (1L << 36);
        foreach (var raw in ((long[])[0L, 1L, -1L, (Threshold - 1L), Threshold, (Threshold + 1L), -(Threshold + 1L),])) {
            AssertSuperellipsoidAnswers(pair: pair, position: RawPosition(x: raw, y: 1L, z: -1L), halfRaw: 1L, material: 1);
        }
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void FarSuperellipsoidsActuallySkipAsSegmentsAndInstances(bool instances) {
        var pair = SuperellipsoidPair((builder, reference) => {
            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Sphere(radius: 1f, material: 0);
            for (var index = 0; (index < 16); index++) {
                var center = new Vector3(x: (1000f + (index * 25f)), y: 0f, z: 0f);
                if (instances && !reference) { _ = builder.BeginInstance(boundCenter: center, boundRadius: 4f); }
                Chain(builder: builder, reference: reference, offset: center);
                _ = builder.Superellipsoid(radii: new Vector3(x: 0.3f, y: 1.2f, z: 0.8f), exponent: 2.05f, material: 1);
                if (instances && !reference) { _ = builder.EndInstance(); }
            }
        });

        AssertSuperellipsoidAnswers(pair: pair, position: FixedPosition.Zero, halfRaw: 1L, material: 0);
        Assert.True(condition: pair.Field.TryDistance(position: FixedPosition.Zero, distance: out _, material: out _, instructionsWalked: out var walked));
        // Compare against this program's count, not the reference's extra identity instructions.
        Assert.True(condition: (walked < pair.Instructions));
    }

    [InlineData(2f)]
    [InlineData(2.05f)]
    [Theory]
    public void AnExtremeSmoothMarginCannotWrapATinyGainIntoAClearingCertificate(float exponent) {
        var pair = SuperellipsoidPair((builder, reference) => {
            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Superellipsoid(
                radii: new Vector3(x: 0.0001f, y: 264860.5625f, z: 264860.5625f),
                exponent: exponent, material: 1, blend: SdfBlendOp.SmoothUnion, smooth: 140732723625984f
            );
        });
        Assert.True(condition: (pair.Field.Frame >= FixedQ4816.Epsilon));
        AssertSuperellipsoidAnswers(pair: pair, position: FixedPosition.Zero, material: 1);
        Assert.True(condition: pair.Field.TryDistance(position: FixedPosition.Zero, distance: out var distance, material: out _));
        Assert.True(condition: (distance < FixedQ4816.Zero));
    }
}
