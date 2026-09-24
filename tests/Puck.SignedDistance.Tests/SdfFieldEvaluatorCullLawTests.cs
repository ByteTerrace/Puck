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
            var center = new Vector3(
                x: (-55f + (index * 10f)),
                y: 0f,
                z: 0f
            );
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

        var smoothCenter = new Vector3(
            x: 200f,
            y: 0f,
            z: 0f
        );

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

        var subtractCenter = new Vector3(
            x: 0f,
            y: 0f,
            z: 200f
        );

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

        var scopedCenter = new Vector3(
            x: 0f,
            y: 200f,
            z: 0f
        );

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
    // Reproduces the case IsPureUnionInstance alone does not exclude: an instance whose body carries no leading
    // ResetPoint (legal — BeginInstance imposes no such requirement), immediately followed by a world-set
    // instruction that also has no leading ResetPoint and so depends on whatever local position/scale the
    // instance's own Translate would have left behind. Builds the identical instruction sequence two ways: `culled`
    // opens the far instance with BeginInstance/EndInstance so the evaluator's cull may act on it, `unwrapped` emits
    // the same instructions with no instance metadata at all, so nothing is ever skipped. A cull that fires here
    // without accounting for the point-state carry would leave `culled`'s trailing sphere reading a stale local
    // position and answer differently from `unwrapped`.
    private static (SdfFieldEvaluator Culled, SdfFieldEvaluator Unwrapped) BuildLeakyFrameFixture() {
        var culledBuilder = new SdfProgramBuilder();
        var unwrappedBuilder = new SdfProgramBuilder();
        var material = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = unwrappedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        void EmitNear(SdfProgramBuilder builder) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(
                x: 5f,
                y: 0f,
                z: 0f
            ));
            _ = builder.Sphere(
                blend: SdfBlendOp.Union,
                material: material,
                radius: 0.1f
            );
        }

        EmitNear(builder: culledBuilder);
        EmitNear(builder: unwrappedBuilder);

        void EmitFarBodyNoReset(SdfProgramBuilder builder) {
            _ = builder.Translate(offset: new Vector3(
                x: 1f,
                y: 0f,
                z: 0f
            ));
            _ = builder.Sphere(
                blend: SdfBlendOp.Union,
                material: material,
                radius: 0.5f
            );
        }

        _ = culledBuilder.BeginInstance(
            boundCenter: new Vector3(
                x: 100f,
                y: 0f,
                z: 0f
            ),
            boundRadius: 1f
        );
        EmitFarBodyNoReset(builder: culledBuilder);
        _ = culledBuilder.EndInstance();
        EmitFarBodyNoReset(builder: unwrappedBuilder);

        void EmitTrailingNoReset(SdfProgramBuilder builder) {
            _ = builder.Sphere(
                blend: SdfBlendOp.Union,
                material: material,
                radius: 0.5f
            );
        }

        EmitTrailingNoReset(builder: culledBuilder);
        EmitTrailingNoReset(builder: unwrappedBuilder);

        return (
            new SdfFieldEvaluator(program: culledBuilder.Build()),
            new SdfFieldEvaluator(program: unwrappedBuilder.Build())
        );
    }
    // Every witness instance declares a bound far out along +X but places its shapes at the origin, under the one near
    // sphere, in a material of its own. The bound does not contain the shapes, so a skipped instance and an evaluated
    // one answer differently at the origin: any body that runs there wins the union with a negative distance and the
    // witness material. A query answering exactly the near sphere's own distance and material has therefore run none
    // of the bodies, and each skip cost one bound test.
    private static (SdfFieldEvaluator Witnessed, SdfFieldEvaluator NearOnly, int WitnessMaterial) BuildFarInstanceWitnesses(int instanceCount, int shapesPerInstance) {
        var witnessedBuilder = new SdfProgramBuilder();
        var nearOnlyBuilder = new SdfProgramBuilder();
        var nearMaterial = witnessedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var witnessMaterial = witnessedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

        _ = nearOnlyBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        void EmitNear(SdfProgramBuilder builder) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(
                x: 0f,
                y: 0f,
                z: 3f
            ));
            _ = builder.Sphere(
                blend: SdfBlendOp.Union,
                material: nearMaterial,
                radius: 0.5f
            );
        }

        EmitNear(builder: witnessedBuilder);
        EmitNear(builder: nearOnlyBuilder);

        for (var index = 0; (index < instanceCount); index++) {
            _ = witnessedBuilder.Instance(
                boundCenter: new Vector3(
                    x: (1000f + (index * 25f)),
                    y: 0f,
                    z: 0f
                ),
                boundRadius: ((shapesPerInstance * 1.0f) + 1.0f),
                emit: builder => {
                    for (var shape = 0; (shape < shapesPerInstance); shape++) {
                        _ = builder.ResetPoint();
                        _ = builder.Translate(offset: new Vector3(
                            x: (shape * 0.1f),
                            y: 0f,
                            z: 0f
                        ));
                        _ = builder.Sphere(
                            blend: SdfBlendOp.Union,
                            material: witnessMaterial,
                            radius: 0.4f
                        );
                    }
                }
            );
        }

        return (
            new SdfFieldEvaluator(program: witnessedBuilder.Build()),
            new SdfFieldEvaluator(program: nearOnlyBuilder.Build()),
            witnessMaterial
        );
    }
    private static FixedVector3 Direction(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));

    [Fact]
    public void APlaneInsideASmallInstanceBoundIsNeverCulled() {
        var culledBuilder = new SdfProgramBuilder();
        var unwrappedBuilder = new SdfProgramBuilder();
        var material = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = unwrappedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // The sphere comes first so a running best exists when the plane's turn comes; the plane's declared bound is
        // a unit sphere at its origin, while its influence is the whole space.
        foreach (var builder in ((SdfProgramBuilder[])[culledBuilder, unwrappedBuilder,])) {
            var far = new Vector3(
                x: 60f,
                y: 0f,
                z: 0f
            );
            var declares = ReferenceEquals(
                objA: builder,
                objB: culledBuilder
            );

            if (declares) {
                _ = builder.BeginInstance(
                    boundCenter: far,
                    boundRadius: 1f
                );
            }

            _ = builder.ResetPoint();
            _ = builder.Translate(offset: far);
            _ = builder.Sphere(
                material: material,
                radius: 0.75f
            );

            if (declares) {
                _ = builder.EndInstance();
                _ = builder.BeginInstance(
                    boundCenter: new Vector3(
                        x: 0f,
                        y: 2f,
                        z: 0f
                    ),
                    boundRadius: 1f
                );
            }

            _ = builder.ResetPoint();
            _ = builder.Plane(
                material: material,
                normal: Vector3.UnitY,
                offset: 2f
            );

            if (declares) {
                _ = builder.EndInstance();
            }
        }

        var culled = new SdfFieldEvaluator(program: culledBuilder.Build(buildInstanceGrid: false));
        var unwrapped = new SdfFieldEvaluator(program: unwrappedBuilder.Build(buildInstanceGrid: false));

        foreach (var x in ((double[])[-80.0, -20.0, 0.0, 20.0, 57.0, 80.0,])) {
            var position = Position(
                x: x,
                y: 0.0,
                z: 0.0
            );

            Assert.True(condition: unwrapped.TryDistance(
                distance: out var expected,
                material: out var expectedMaterial,
                position: position
            ));
            Assert.True(condition: culled.TryDistance(
                distance: out var actual,
                material: out var actualMaterial,
                position: position
            ));
            Assert.Equal(
                actual: actual,
                expected: expected
            );
            Assert.Equal(
                actual: actualMaterial,
                expected: expectedMaterial
            );
        }
    }
    [Fact]
    public void CullMatchesTheUncalledReferenceAcrossALatticeOfDistancesGradientsAndCasts() {
        var (culled, unwrapped) = BuildFixture();
        var samples = 0;

        for (var xi = -6; (xi <= 6); xi++) {
            var x = (xi * 10.0);

            foreach (var y in ((double[])[-1.0, 0.0, 1.0, 3.0,])) {
                foreach (var z in ((double[])[-3.0, 0.0, 3.0,])) {
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
                        actual: culledFound,
                        expected: unwrappedFound
                    );
                    Assert.Equal(
                        actual: culledDistance,
                        expected: unwrappedDistance
                    );
                    Assert.Equal(
                        actual: culledMaterial,
                        expected: unwrappedMaterial
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
                        actual: culledGradientFound,
                        expected: unwrappedGradientFound
                    );
                    Assert.Equal(
                        actual: culledGradient,
                        expected: unwrappedGradient
                    );

                    foreach (var direction in ((double[][])[[1.0, 0.0, 0.0,], [-1.0, 0.0, 0.0,], [0.0, -1.0, 0.0,], [0.3, -0.9, 0.3,],])) {
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
                            actual: culledRayFound,
                            expected: unwrappedRayFound
                        );
                        Assert.Equal(
                            actual: culledRayHit,
                            expected: unwrappedRayHit
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
                            actual: culledSphereFound,
                            expected: unwrappedSphereFound
                        );
                        Assert.Equal(
                            actual: culledSphereHit,
                            expected: unwrappedSphereHit
                        );
                    }
                }
            }
        }

        Assert.True(condition: (samples > 0));
    }
    [Fact]
    public void CullNeverFiresWhenTheFollowingInstructionIsNotAResetPoint() {
        var (culled, unwrapped) = BuildLeakyFrameFixture();
        var position = Position(
            x: 5.0,
            y: 0.0,
            z: 0.0
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
            actual: culledFound,
            expected: unwrappedFound
        );
        Assert.Equal(
            actual: culledDistance,
            expected: unwrappedDistance
        );
        Assert.Equal(
            actual: culledMaterial,
            expected: unwrappedMaterial
        );
    }
    [Fact]
    public void FarInstancesOutsideTheRunningBestRunNoneOfTheirBodies() {
        var (witnessed, nearOnly, witnessMaterial) = BuildFarInstanceWitnesses(
            instanceCount: 4000,
            shapesPerInstance: 8
        );

        foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (0.5, 0.25, 1.0), (-2.0, 1.0, 0.0), (0.0, -1.5, 5.0) }) {
            var position = Position(
                x: x,
                y: y,
                z: z
            );

            Assert.True(condition: nearOnly.TryDistance(
                distance: out var nearDistance,
                material: out var nearMaterial,
                position: position
            ));
            Assert.True(condition: witnessed.TryDistance(
                distance: out var distance,
                material: out var material,
                position: position
            ));
            Assert.True(
                condition: ((distance == nearDistance) && (material == nearMaterial)),
                userMessage: $"at ({x}, {y}, {z}) a far instance body ran: answered {distance} in material {material}, the near sphere alone answers {nearDistance} in material {nearMaterial}"
            );
        }

        // Inside the first bound the cull may not skip, so the same program answers with a body: the witnesses bite.
        Assert.True(condition: witnessed.TryDistance(
            distance: out _,
            material: out var boundMaterial,
            position: Position(
                x: 1000.0,
                y: 0.0,
                z: 0.0
            )
        ));
        Assert.Equal(
            actual: boundMaterial,
            expected: witnessMaterial
        );
    }
}
