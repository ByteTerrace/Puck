using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfCullAdversarialLawTests {
    private readonly ITestOutputHelper m_output;

    public SdfCullAdversarialLawTests(ITestOutputHelper output) { m_output = output; }

    // Zero elongation is the existing cull-off oracle: it leaves the point unchanged but prevents a chain certificate.
    private static void Reset(SdfProgramBuilder builder, bool cull) {
        _ = builder.ResetPoint();
        if (!cull) { _ = builder.Elongate(extents: Vector3.Zero); }
    }

    [Fact]
    public void SmoothUnionMarginCannotWrapIntoASkippableChain() {
        SdfFieldEvaluator Build(bool cull) {
            var b = new SdfProgramBuilder();
            var material = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            Reset(builder: b, cull: cull);
            _ = b.Sphere(material: material, radius: 1f);
            Reset(builder: b, cull: cull);
            _ = b.Translate(offset: new Vector3(100f, 0f, 0f));
            // The largest float below the positive Q48.16 limit fits as k, but k + k/32768 + 16 raws does not.
            _ = b.Sphere(material: material, radius: 1f, blend: SdfBlendOp.SmoothUnion, smooth: 140737479966720f);
            return new SdfFieldEvaluator(program: b.Build());
        }
        var reference = Build(cull: false);
        var culled = Build(cull: true);
        Assert.True(reference.TryDistance(position: FixedPosition.Zero, distance: out var expected, material: out var expectedMaterial));
        Assert.True(culled.TryDistance(position: FixedPosition.Zero, distance: out var actual, material: out var actualMaterial));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedMaterial, actualMaterial);
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void InstanceBoundaryDoesNotKillTheLivePoint(bool scoped) {
        SdfFieldEvaluator Build(bool cull) {
            var b = new SdfProgramBuilder();
            var material = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            Reset(builder: b, cull: cull);
            _ = b.Sphere(material: material, radius: 1f);
            void Emit(SdfProgramBuilder target) {
                if (scoped) { _ = target.PushField(compose: SdfBlendOp.Union); }
                Reset(builder: target, cull: cull);
                _ = target.Translate(offset: new Vector3(x: 100f, y: 0f, z: 0f)).Sphere(material: material, radius: 1f);
                if (scoped) { _ = target.PopField(); }
            }
            if (cull) { _ = b.Instance(boundCenter: new Vector3(x: 100f, y: 0f, z: 0f), boundRadius: 1f, emit: Emit); } else { Emit(target: b); }
            _ = b.Sphere(material: material, radius: 200f);
            return new SdfFieldEvaluator(program: b.Build());
        }
        var reference = Build(cull: false);
        var culled = Build(cull: true);

        Assert.True(condition: reference.TryDistance(position: FixedPosition.Zero, distance: out var expected, material: out var expectedMaterial));
        Assert.True(condition: culled.TryDistance(position: FixedPosition.Zero, distance: out var actual, material: out var actualMaterial));
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
        Assert.True(condition: reference.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var expectedBounds));
        Assert.True(condition: culled.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var actualBounds));
        Assert.Equal(actual: actualBounds, expected: expectedBounds);
    }
    [Fact]
    public void APrimedInstanceCannotInheritAnEarlierPoint() {
        SdfFieldEvaluator Build(bool cull) {
            var b = new SdfProgramBuilder();
            var material = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            void First(SdfProgramBuilder target) {
                Reset(builder: target, cull: cull);
                _ = target.Translate(offset: new Vector3(x: 100f, y: 0f, z: 0f)).Sphere(material: material, radius: 1f);
            }
            if (cull) { _ = b.Instance(boundCenter: new Vector3(x: 100f, y: 0f, z: 0f), boundRadius: 1f, emit: First); } else { First(target: b); }
            Reset(builder: b, cull: cull);
            _ = b.Translate(offset: new Vector3(x: 2f, y: 0f, z: 0f));
            if (cull) { _ = b.Instance(boundCenter: new Vector3(x: 2f, y: 0f, z: 0f), boundRadius: 1f, emit: target => target.Sphere(material: material, radius: 1f)); } else { _ = b.Sphere(material: material, radius: 1f); }
            return new SdfFieldEvaluator(program: b.Build());
        }
        var reference = Build(cull: false);
        var culled = Build(cull: true);

        Assert.True(condition: reference.TryDistance(position: FixedPosition.Zero, distance: out var expected, material: out var expectedMaterial));
        Assert.True(condition: culled.TryDistance(position: FixedPosition.Zero, distance: out var actual, material: out var actualMaterial));
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
        Assert.True(condition: reference.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var expectedBounds));
        Assert.True(condition: culled.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var actualBounds));
        Assert.Equal(actual: actualBounds, expected: expectedBounds);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ScopeBoundsIncludeTheFiniteFarSeed(bool relief) {
        SdfFieldEvaluator Build(bool cull) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 300_000_000f);
            if (!relief) { _ = builder.Dilate(radius: -1_000_000_000f); }
            void Emit(SdfProgramBuilder b) {
                _ = b.PushField(compose: SdfBlendOp.Union);
                Reset(builder: b, cull: cull);
                _ = b.Translate(offset: new Vector3(x: -2_000_000_000f, y: 0f, z: 0f));
                _ = b.Scale(scale: new Vector3(value: 100_000_000f));
                _ = b.Sphere(material: material, radius: 1f);
                if (relief) { _ = b.NoiseDisplace(amplitude: 0.5f, frequency: 1f, octaves: 1); }
                _ = b.PopField();
            }
            if (cull) { _ = builder.Instance(boundCenter: new Vector3(x: -2_000_000_000f, y: 0f, z: 0f), boundRadius: 100_000_000f, emit: Emit); } else { Emit(b: builder); }
            return new SdfFieldEvaluator(program: builder.Build());
        }
        var reference = Build(cull: false);
        var culled = Build(cull: true);

        Assert.True(condition: reference.TryDistance(position: FixedPosition.Zero, distance: out var expected, material: out var expectedMaterial));
        Assert.True(condition: culled.TryDistance(position: FixedPosition.Zero, distance: out var actual, material: out var actualMaterial));
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
        Assert.True(condition: reference.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var expectedBounds));
        Assert.True(condition: culled.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out var actualBounds));
        Assert.Equal(actual: actualBounds, expected: expectedBounds);
    }
    [Fact]
    public void ExcessiveReliefSlackCannotOverflowCertificateConstruction() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Instance(boundCenter: Vector3.Zero, boundRadius: 1f, emit: b => {
            _ = b.PushField(compose: SdfBlendOp.Union).ResetPoint().Sphere(material: material, radius: 1f);
            for (var i = 0; (i < 32); i++) {
                _ = b.NoiseDisplace(amplitude: 10_000_000_000_000f, frequency: 0f, octaves: 1);
                _ = b.ResetPoint();
                _ = b.Sphere(material: material, radius: 1f);
                _ = b.Sphere(material: material, radius: 1f, blend: SdfBlendOp.Intersection);
            }
            _ = b.PopField();
        });
        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryDistance(position: FixedPosition.Zero, distance: out var distance, material: out _));
        Assert.Equal(-FixedQ4816.One, distance);
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [Theory]
    public void HierarchyKeepsOrderTiesAndScopeEdges(int count) {
        foreach (var barrier in new[] { SdfBlendOp.Union, SdfBlendOp.Intersection, SdfBlendOp.SmoothIntersection,
            SdfBlendOp.SmoothSubtraction, SdfBlendOp.ChamferUnion, SdfBlendOp.GrooveUnion, SdfBlendOp.PipeUnion }) {
            SdfFieldEvaluator Build(bool cull) {
                var b = new SdfProgramBuilder();
                var first = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
                var second = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

                Reset(builder: b, cull: cull);
                _ = b.Sphere(material: first, radius: 1f);
                _ = b.PushField(compose: SdfBlendOp.Union);
                Reset(builder: b, cull: cull);
                _ = b.Sphere(material: second, radius: 1f);
                for (var i = 0; (i < count); i++) {
                    Reset(builder: b, cull: cull);
                    _ = b.Translate(offset: new Vector3(x: (100f + i), y: 0f, z: 0f));
                    _ = b.Sphere(material: second, radius: 1f,
                        blend: (i % 3) switch { 0 => SdfBlendOp.Union, 1 => SdfBlendOp.SmoothUnion, _ => SdfBlendOp.Subtraction }, smooth: 0.5f);
                }
                Reset(builder: b, cull: cull);
                _ = b.Translate(offset: new Vector3(x: 2f, y: 0f, z: 0f));
                _ = b.Sphere(material: second, radius: 1f, blend: barrier, smooth: 0.5f);
                _ = b.PopField();
                Reset(builder: b, cull: cull);
                _ = b.Sphere(material: second, radius: 1f);
                return new SdfFieldEvaluator(program: b.Build());
            }
            var culled = Build(cull: true);
            var reference = Build(cull: false);

            foreach (var raw in new long[] { -65537, -65536, -1, 0, 1, 65535, 65536, 65537, (98L * 65536) }) {
                var position = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromRawBits(value: raw), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

                Assert.Equal(reference.TryDistance(distance: out var expected, material: out var expectedMaterial, position: position),
                    culled.TryDistance(distance: out var actual, instructionsWalked: out var walked, material: out var actualMaterial, position: position));
                Assert.Equal(actual: actual, expected: expected);
                Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
                if ((raw == 0) && (barrier == SdfBlendOp.Union)) { m_output.WriteLine(message: $"chains={count}: {walked} instructions/evaluation"); }
            }
        }
    }
    [Fact]
    public void ReliefOctaveExtremesKeepTheReferenceAnswer() {
        for (var octaves = 1; octaves <= SdfProgramBuilder.MaxNoiseOctaves; octaves++) {
            foreach (var gain in new[] { 0.0000001f, 0.05f, 0.5f, 1f, 2f, 1000f, 1e12f }) {
                foreach (var frequency in new[] { 0f, -0.75f, 8f, 1e12f }) {
                    SdfFieldEvaluator Build(bool cull) {
                        var b = new SdfProgramBuilder();
                        var material = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

                        _ = b.Plane(material: material, normal: Vector3.UnitY, offset: 0f);
                        void Emit(SdfProgramBuilder target) {
                            _ = target.PushField(compose: SdfBlendOp.Union);
                            Reset(builder: target, cull: cull);
                            _ = target.Translate(offset: new Vector3(x: 2f, y: 0f, z: 0f)).Sphere(material: material, radius: 1f);
                            _ = target.NoiseDisplace(amplitude: ((frequency < 0f) ? -4f : 4f), frequency: frequency, gain: gain,
                                lacunarity: ((gain == 0.05f) ? 1f : ((gain <= 1f) ? 4f : gain)), octaves: octaves, seed: uint.MaxValue);
                            _ = target.PopField();
                        }
                        if (cull) { _ = b.Instance(boundCenter: new Vector3(x: 2f, y: 0f, z: 0f), boundRadius: 3f, emit: Emit); } else { Emit(target: b); }
                        return new SdfFieldEvaluator(program: b.Build());
                    }
                    var reference = Build(cull: false);
                    var culled = Build(cull: true);

                    foreach (var x in new[] { 0f, 1f, 2f, 4f }) {
                        var position = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

                        Assert.Equal(reference.TryDistance(distance: out var expected, material: out var expectedMaterial, position: position),
                            culled.TryDistance(distance: out var actual, material: out var actualMaterial, position: position));
                        Assert.Equal(actual: actual, expected: expected);
                        Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
                    }
                }
            }
        }
    }
    [Fact]
    public void FarReliefInstancesRetainWholeInstanceCulls() {
        var b = new SdfProgramBuilder();
        var material = b.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = b.ResetPoint().Sphere(material: material, radius: 1f);
        for (var i = 0; (i < 64); i++) {
            var center = new Vector3(x: (100f + (i * 10f)), y: 0f, z: 0f);

            _ = b.Instance(boundCenter: center, boundRadius: 2f, emit: target => target
                .PushField(compose: SdfBlendOp.Union).ResetPoint().Translate(offset: center)
                .Sphere(material: material, radius: 1f).NoiseDisplace(amplitude: 0.5f, frequency: 1f).PopField());
        }
        var evaluator = new SdfFieldEvaluator(program: b.Build());

        Assert.True(condition: evaluator.TryDistance(position: FixedPosition.Zero, distance: out var distance, material: out _, instructionsWalked: out var walked));
        Assert.Equal(-FixedQ4816.One, distance);
        Assert.Equal(actual: walked, expected: 2);
        m_output.WriteLine(message: $"64 relief instances: {walked} instructions/evaluation");
    }
}
