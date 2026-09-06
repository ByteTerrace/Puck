using System.Diagnostics;
using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Proves <see cref="SdfFieldEvaluator"/>'s instance cull is exact-by-construction: wrapping a scattered
/// field's per-object shapes in <see cref="SdfProgramBuilder.Instance"/> bounds (so the evaluator may skip an
/// out-of-reach one) must never change a distance, gradient, material, or cast hit the SAME instruction sequence
/// produces when emitted with no instance metadata at all — the "cull off" reference, since an evaluator over a
/// program declaring no instances has nothing to cull.</summary>
public sealed class SdfFieldEvaluatorCullLawTests {
    private const int UnionInstanceCount = 12;

    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static FixedVector3 Direction(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    // Builds the same field twice over the same instruction sequence: `culled` wraps each per-object shape in an
    // Instance bound the evaluator's cull may act on, `unwrapped` emits the identical instructions with no instance
    // metadata, so its evaluator declares no instance and skips nothing. Includes one instance of each blend family
    // IsPureUnionInstance must refuse to cull (SmoothUnion, Subtraction, a PushField/PopField scope) alongside the
    // plain-Union majority, so a law run over both programs exercises the cull's positive and negative cases together.
    private static (SdfFieldEvaluator Culled, SdfFieldEvaluator Unwrapped) BuildFixture() {
        var culledBuilder = new SdfProgramBuilder();
        var unwrappedBuilder = new SdfProgramBuilder();
        var material = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = unwrappedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = culledBuilder.Plane(
            material: material,
            normal: Vector3.UnitY,
            offset: 2f
        );
        _ = unwrappedBuilder.Plane(
            material: material,
            normal: Vector3.UnitY,
            offset: 2f
        );

        for (var index = 0; (index < UnionInstanceCount); index++) {
            var center = new Vector3((-55f + (index * 10f)), 0f, 0f);
            const float Radius = 0.75f;

            void Emit(SdfProgramBuilder builder) {
                _ = builder.ResetPoint();
                _ = builder.Translate(offset: center);
                _ = builder.Sphere(
                    blend: SdfBlendOp.Union,
                    material: material,
                    radius: Radius
                );
            }

            _ = culledBuilder.Instance(
                boundCenter: center,
                boundRadius: (Radius + 0.01f),
                emit: Emit
            );
            Emit(builder: unwrappedBuilder);
        }

        var smoothCenter = new Vector3(200f, 0f, 0f);

        void EmitSmooth(SdfProgramBuilder builder) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: smoothCenter);
            _ = builder.Sphere(
                blend: SdfBlendOp.SmoothUnion,
                material: material,
                radius: 1f,
                smooth: 0.5f
            );
        }

        _ = culledBuilder.Instance(
            boundCenter: smoothCenter,
            boundRadius: 1.6f,
            emit: EmitSmooth
        );
        EmitSmooth(builder: unwrappedBuilder);

        var subtractCenter = new Vector3(0f, 0f, 200f);

        void EmitSubtract(SdfProgramBuilder builder) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: subtractCenter);
            _ = builder.Sphere(
                blend: SdfBlendOp.Subtraction,
                material: material,
                radius: 0.4f
            );
        }

        _ = culledBuilder.Instance(
            boundCenter: subtractCenter,
            boundRadius: 0.5f,
            emit: EmitSubtract
        );
        EmitSubtract(builder: unwrappedBuilder);

        var scopedCenter = new Vector3(0f, 200f, 0f);

        void EmitScoped(SdfProgramBuilder builder) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: scopedCenter);
            _ = builder.PushField(compose: SdfBlendOp.Union);
            _ = builder.Sphere(
                blend: SdfBlendOp.Union,
                material: material,
                radius: 0.6f
            );
            _ = builder.Onion(thickness: 0.05f);
            _ = builder.PopField();
        }

        _ = culledBuilder.Instance(
            boundCenter: scopedCenter,
            boundRadius: 0.7f,
            emit: EmitScoped
        );
        EmitScoped(builder: unwrappedBuilder);

        return (
            new SdfFieldEvaluator(program: culledBuilder.Build()),
            new SdfFieldEvaluator(program: unwrappedBuilder.Build())
        );
    }

    [Fact]
    public void CullMatchesTheUncalledReferenceAcrossALatticeOfDistancesGradientsAndCasts() {
        var (culled, unwrapped) = BuildFixture();
        var samples = 0;

        for (var xi = -6; (xi <= 6); xi++) {
            var x = (xi * 10.0);

            foreach (var y in (double[])[-1.0, 0.0, 1.0, 3.0,]) {
                foreach (var z in (double[])[-3.0, 0.0, 3.0,]) {
                    samples++;

                    var position = Position(
                        x: x,
                        y: y,
                        z: z
                    );
                    var culledFound = culled.TryDistance(
                        distance: out var culledDistance,
                        material: out var culledMaterial,
                        position: position
                    );
                    var unwrappedFound = unwrapped.TryDistance(
                        distance: out var unwrappedDistance,
                        material: out var unwrappedMaterial,
                        position: position
                    );

                    Assert.Equal(
                        expected: unwrappedFound,
                        actual: culledFound
                    );
                    Assert.Equal(
                        expected: unwrappedDistance,
                        actual: culledDistance
                    );
                    Assert.Equal(
                        expected: unwrappedMaterial,
                        actual: culledMaterial
                    );

                    var culledGradientFound = culled.TryFieldGradient(
                        gradient: out var culledGradient,
                        position: position
                    );
                    var unwrappedGradientFound = unwrapped.TryFieldGradient(
                        gradient: out var unwrappedGradient,
                        position: position
                    );

                    Assert.Equal(
                        expected: unwrappedGradientFound,
                        actual: culledGradientFound
                    );
                    Assert.Equal(
                        expected: unwrappedGradient,
                        actual: culledGradient
                    );

                    foreach (var direction in (double[][])[[1.0, 0.0, 0.0,], [-1.0, 0.0, 0.0,], [0.0, -1.0, 0.0,], [0.3, -0.9, 0.3,],]) {
                        var dir = Direction(
                            x: direction[0],
                            y: direction[1],
                            z: direction[2]
                        );
                        var maxDist = FixedQ4816.FromInteger(value: 400L);
                        var culledRayFound = culled.Raycast(
                            dir: dir,
                            hit: out var culledRayHit,
                            maxDist: maxDist,
                            origin: position
                        );
                        var unwrappedRayFound = unwrapped.Raycast(
                            dir: dir,
                            hit: out var unwrappedRayHit,
                            maxDist: maxDist,
                            origin: position
                        );

                        Assert.Equal(
                            expected: unwrappedRayFound,
                            actual: culledRayFound
                        );
                        Assert.Equal(
                            expected: unwrappedRayHit,
                            actual: culledRayHit
                        );

                        var radius = FixedQ4816.FromDouble(value: 0.3);
                        var culledSphereFound = culled.SphereCast(
                            dir: dir,
                            hit: out var culledSphereHit,
                            maxDist: maxDist,
                            origin: position,
                            radius: radius
                        );
                        var unwrappedSphereFound = unwrapped.SphereCast(
                            dir: dir,
                            hit: out var unwrappedSphereHit,
                            maxDist: maxDist,
                            origin: position,
                            radius: radius
                        );

                        Assert.Equal(
                            expected: unwrappedSphereFound,
                            actual: culledSphereFound
                        );
                        Assert.Equal(
                            expected: unwrappedSphereHit,
                            actual: culledSphereHit
                        );
                    }
                }
            }
        }

        Assert.True(condition: (samples > 0));
    }
    // Every far instance holds ShapesPerInstance chained union spheres, so evaluating one fully costs far more than
    // testing its bound once — without the cull, this many far instances is slow enough to discriminate reliably
    // from a bound-only skip on any machine.
    private static SdfFieldEvaluator BuildManyFarInstancesEvaluator(int instanceCount, int shapesPerInstance) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(0f, 0f, 3f));
        _ = builder.Sphere(
            blend: SdfBlendOp.Union,
            material: material,
            radius: 0.5f
        );

        for (var index = 0; (index < instanceCount); index++) {
            var center = new Vector3((1000f + (index * 25f)), 0f, 0f);

            void Emit(SdfProgramBuilder b) {
                for (var shape = 0; (shape < shapesPerInstance); shape++) {
                    _ = b.ResetPoint();
                    _ = b.Translate(offset: (center + new Vector3(shape, 0f, 0f)));
                    _ = b.Sphere(
                        blend: SdfBlendOp.Union,
                        material: material,
                        radius: 0.4f
                    );
                }
            }

            _ = builder.Instance(
                boundCenter: center,
                boundRadius: ((shapesPerInstance * 1.0f) + 1.0f),
                emit: Emit
            );
        }

        return new SdfFieldEvaluator(program: builder.Build());
    }

    [Fact]
    public void CullSkipsFarInstancesInsteadOfEvaluatingEveryShapeInThem() {
        const int InstanceCount = 4000;
        const int ShapesPerInstance = 8;

        var evaluator = BuildManyFarInstancesEvaluator(
            instanceCount: InstanceCount,
            shapesPerInstance: ShapesPerInstance
        );
        var origin = Position(
            x: 0.0,
            y: 0.0,
            z: 0.0
        );

        // Warms the JIT once outside the timed section.
        _ = evaluator.TryDistance(
            distance: out _,
            material: out _,
            position: origin
        );

        var elapsed = Stopwatch.StartNew();

        for (var iteration = 0; (iteration < 20); iteration++) {
            _ = evaluator.TryDistance(
                distance: out _,
                material: out _,
                position: origin
            );
        }

        elapsed.Stop();

        Assert.True(
            condition: (elapsed.Elapsed < TimeSpan.FromMilliseconds(value: 100.0)),
            userMessage: $"20 queries over {InstanceCount} far instances of {ShapesPerInstance} shapes each took {elapsed.Elapsed}, long enough to indicate every shape in every far instance was evaluated instead of each instance's bound being tested once."
        );
    }
}
