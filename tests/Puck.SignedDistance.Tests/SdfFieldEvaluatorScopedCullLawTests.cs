using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Proves the cull's reach beyond hard-union instances leaves every answer where the stream puts it: a scoped
/// instance joining through a hard-union pop, a chain of smooth-union or subtracted shapes inside a scope, and the
/// nearest instance walked ahead of its place. Each law compares a program against a cull-off reference built from the
/// same instructions with no instance declared and every chain opened by a zero elongation, an exact identity that
/// leaves the chain no reach sphere and so nothing for either walk to skip.</summary>
public sealed class SdfFieldEvaluatorScopedCullLawTests {
    private const int ScopedInstanceCount = 6;

    // A plane under a row of scoped instances, each a slab with two smooth-union spheres and a carved notch inside one
    // scope, beside a multi-shape union instance and two instances answering the same sphere in different materials,
    // the later one with the wider bound so it is the one walked first. A top-level smooth union, when asked for, makes
    // the stream's answer more than the least of its instances, so no instance may be walked ahead of its place.
    private static (SdfFieldEvaluator Culled, SdfFieldEvaluator Reference) BuildFixture(bool topLevelSmooth) {
        var culledBuilder = new SdfProgramBuilder();
        var referenceBuilder = new SdfProgramBuilder();
        var slab = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var first = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        var second = culledBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

        _ = referenceBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        _ = referenceBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        _ = referenceBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

        foreach (var builder in ((SdfProgramBuilder[])[culledBuilder, referenceBuilder,])) {
            var culls = ReferenceEquals(objA: builder, objB: culledBuilder);

            void Chain(Vector3 offset) {
                _ = builder.ResetPoint();

                if (!culls) {
                    _ = builder.Elongate(extents: Vector3.Zero);
                }

                _ = builder.Translate(offset: offset);
            }

            _ = builder.Plane(material: slab, normal: Vector3.UnitY, offset: 2f);

            for (var index = 0; (index < ScopedInstanceCount); index++) {
                var center = new Vector3(x: ((index * 9f) - 22f), y: 0f, z: 0f);

                void EmitScoped() {
                    _ = builder.PushField(compose: SdfBlendOp.Union);
                    Chain(offset: center);
                    _ = builder.Box(halfExtents: new Vector3(x: 2f, y: 0.3f, z: 2f), material: slab, round: 0f);
                    Chain(offset: (center + new Vector3(x: 1.2f, y: 0.8f, z: 0f)));
                    _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: first, radius: 0.5f, smooth: 0.4f);
                    Chain(offset: (center + new Vector3(x: -1.5f, y: 1f, z: 0.8f)));
                    _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: second, radius: 0.4f, smooth: 0.3f);
                    Chain(offset: (center + new Vector3(x: 0f, y: 0.3f, z: 0f)));
                    _ = builder.Box(blend: SdfBlendOp.Subtraction, halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f), material: slab, round: 0f);
                    _ = builder.PopField();
                }

                if (culls) {
                    _ = builder.Instance(boundCenter: center, boundRadius: 3.5f, emit: _ => EmitScoped());
                } else {
                    EmitScoped();
                }
            }

            var spread = new Vector3(x: 30f, y: 0f, z: 6f);

            void EmitSpread() {
                Chain(offset: spread);
                _ = builder.Box(halfExtents: new Vector3(x: 1f, y: 1f, z: 1f), material: slab, round: 0.1f);
                Chain(offset: (spread + new Vector3(x: 8f, y: 0f, z: 0f)));
                _ = builder.Cylinder(halfHeight: 0.5f, material: slab, radius: 0.8f);
            }

            if (culls) {
                _ = builder.Instance(boundCenter: (spread + new Vector3(x: 4f, y: 0f, z: 0f)), boundRadius: 6f, emit: _ => EmitSpread());
            } else {
                EmitSpread();
            }

            var tie = new Vector3(x: 40f, y: 0f, z: -4f);

            foreach (var (material, radius) in ((ValueTuple<int, float>[])[(first, 1.2f), (second, 4f),])) {
                void EmitTie() {
                    Chain(offset: tie);
                    _ = builder.Sphere(material: material, radius: 1f);
                }

                if (culls) {
                    _ = builder.Instance(boundCenter: tie, boundRadius: radius, emit: _ => EmitTie());
                } else {
                    EmitTie();
                }
            }

            if (topLevelSmooth) {
                Chain(offset: new Vector3(x: 12f, y: 3f, z: 0f));
                _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: first, radius: 0.6f, smooth: 0.5f);
            }
        }

        return (
            new SdfFieldEvaluator(program: culledBuilder.Build(buildInstanceGrid: false)),
            new SdfFieldEvaluator(program: referenceBuilder.Build(buildInstanceGrid: false))
        );
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ScopedChainedAndPrimedSkipsAnswerAsTheCullOffReference(bool topLevelSmooth) {
        var (culled, reference) = BuildFixture(topLevelSmooth: topLevelSmooth);
        var samples = 0;

        for (var x = -30.0; (x <= 48.0); x += 0.7) {
            foreach (var y in ((double[])[-1.2, 0.0, 0.31, 0.75, 1.3, 2.4,])) {
                foreach (var z in ((double[])[-4.3, -0.9, 0.0, 0.55, 3.1,])) {
                    var position = Position(x: x, y: y, z: z);

                    Assert.True(condition: reference.TryDistance(distance: out var expected, material: out var expectedMaterial, position: position));
                    Assert.True(condition: culled.TryDistance(distance: out var actual, material: out var actualMaterial, position: position));
                    Assert.True(
                        condition: ((actual == expected) && (actualMaterial == expectedMaterial)),
                        userMessage: $"at ({x}, {y}, {z}) the culled walk answered {actual} in material {actualMaterial}, the reference {expected} in material {expectedMaterial}"
                    );

                    if ((samples++ % 5) == 0) {
                        var culledFound = culled.TryFieldGradient(gradient: out var culledGradient, position: position);
                        var referenceFound = reference.TryFieldGradient(gradient: out var referenceGradient, position: position);

                        Assert.Equal(actual: culledFound, expected: referenceFound);
                        Assert.Equal(actual: culledGradient, expected: referenceGradient);
                    }
                }
            }
        }

        Assert.True(condition: (samples > 0));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CulledBoxBoundsHoldTheCullOffAnswersInsideThem(bool topLevelSmooth) {
        var (culled, reference) = BuildFixture(topLevelSmooth: topLevelSmooth);
        var half = 0.35;

        for (var x = -30.0; (x <= 48.0); x += 1.3) {
            foreach (var y in ((double[])[-0.5, 0.4, 1.1, 2.2,])) {
                foreach (var z in ((double[])[-4.0, 0.0, 0.7,])) {
                    Assert.True(condition: culled.TryDistanceBounds(
                        distance: out var bounds,
                        lower: Position(x: (x - half), y: (y - half), z: (z - half)),
                        upper: Position(x: (x + half), y: (y + half), z: (z + half))
                    ));

                    foreach (var (dx, dy, dz) in ((ValueTuple<double, double, double>[])[(-half, -half, -half), (half, half, half), (0.0, 0.0, 0.0), (half, -half, 0.1), (-0.2, half, -half),])) {
                        Assert.True(condition: reference.TryDistance(distance: out var answer, material: out _, position: Position(x: (x + dx), y: (y + dy), z: (z + dz))));
                        Assert.True(
                            condition: bounds.Contains(value: answer),
                            userMessage: $"the box about ({x}, {y}, {z}) bounds {bounds}, which misses the reference answer {answer} at offset ({dx}, {dy}, {dz})"
                        );
                    }
                }
            }
        }

        // A box of one point bounds that point's answer within a few raws, so a skip that moves the answer by more,
        // such as a smooth union taken inside its blend radius, shows where a wide box's slack would hide it.
        for (var x = -30.0; (x <= 48.0); x += 0.7) {
            foreach (var y in ((double[])[0.0, 0.31, 0.75, 1.3,])) {
                foreach (var z in ((double[])[-0.9, 0.0, 0.55,])) {
                    var position = Position(x: x, y: y, z: z);

                    Assert.True(condition: culled.TryDistanceBounds(distance: out var bounds, lower: position, upper: position));
                    Assert.True(condition: reference.TryDistance(distance: out var answer, material: out _, position: position));
                    Assert.True(
                        condition: bounds.Contains(value: answer),
                        userMessage: $"the point box at ({x}, {y}, {z}) bounds {bounds}, which misses the reference answer {answer}"
                    );
                }
            }
        }
    }
    // Every witness instance is a scope joining through a hard-union pop whose bound lies far along +X while its shapes
    // sit at the origin, under the near sphere, in a material of their own; any body that runs there wins with a
    // negative distance and the witness material.
    [Fact]
    public void FarScopedInstancesOutsideTheRunningBestRunNoneOfTheirBodies() {
        var witnessedBuilder = new SdfProgramBuilder();
        var nearOnlyBuilder = new SdfProgramBuilder();
        var nearMaterial = witnessedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var witnessMaterial = witnessedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

        _ = nearOnlyBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        foreach (var builder in ((SdfProgramBuilder[])[witnessedBuilder, nearOnlyBuilder,])) {
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(x: 0f, y: 0f, z: 3f));
            _ = builder.Sphere(material: nearMaterial, radius: 0.5f);
        }

        for (var index = 0; (index < 64); index++) {
            _ = witnessedBuilder.Instance(
                boundCenter: new Vector3(x: (1000f + (index * 25f)), y: 0f, z: 0f),
                boundRadius: 3f,
                emit: builder => {
                    _ = builder.PushField(compose: SdfBlendOp.Union);
                    _ = builder.ResetPoint();
                    _ = builder.Sphere(material: witnessMaterial, radius: 0.4f);
                    _ = builder.ResetPoint();
                    _ = builder.Translate(offset: new Vector3(x: 0.3f, y: 0f, z: 0f));
                    _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: witnessMaterial, radius: 0.3f, smooth: 0.2f);
                    _ = builder.PopField();
                }
            );
        }

        var witnessed = new SdfFieldEvaluator(program: witnessedBuilder.Build(buildInstanceGrid: false));
        var nearOnly = new SdfFieldEvaluator(program: nearOnlyBuilder.Build(buildInstanceGrid: false));

        foreach (var (x, y, z) in ((ValueTuple<double, double, double>[])[(0.0, 0.0, 0.0), (0.5, 0.25, 1.0), (-2.0, 1.0, 0.0),])) {
            var position = Position(x: x, y: y, z: z);

            Assert.True(condition: nearOnly.TryDistance(distance: out var nearDistance, material: out var nearMaterialFound, position: position));
            Assert.True(condition: witnessed.TryDistance(distance: out var distance, material: out var material, position: position));
            Assert.True(
                condition: ((distance == nearDistance) && (material == nearMaterialFound)),
                userMessage: $"at ({x}, {y}, {z}) a far scoped instance body ran: answered {distance} in material {material}, the near sphere alone answers {nearDistance} in material {nearMaterialFound}"
            );
        }

        // Inside the first bound the cull may not skip, so the witnesses bite.
        Assert.True(condition: witnessed.TryDistance(distance: out _, material: out var boundMaterial, position: Position(x: 1000.0, y: 0.0, z: 0.0)));
        Assert.Equal(actual: boundMaterial, expected: witnessMaterial);
    }
    // A scope whose slab sits under the query and whose spheres lie well past their blend radius: a smooth union that
    // far above the answer leaves it bitwise, so its chain is skipped exactly as a union chain is, and the two programs
    // walk the same instructions.
    [Fact]
    public void FarSmoothUnionChainsWalkNoMoreThanFarUnionChains() {
        SdfFieldEvaluator Build(SdfBlendOp blend) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.PushField(compose: SdfBlendOp.Union);
            _ = builder.ResetPoint();
            _ = builder.Box(halfExtents: new Vector3(x: 4f, y: 0.3f, z: 4f), material: material, round: 0f);

            for (var index = 0; (index < 4); index++) {
                _ = builder.ResetPoint();
                _ = builder.Translate(offset: new Vector3(x: ((index * 2f) - 3f), y: 2.5f, z: 3f));
                _ = builder.Sphere(blend: blend, material: material, radius: 0.4f, smooth: 0.5f);
            }

            _ = builder.PopField();

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        var union = Build(blend: SdfBlendOp.Union);
        var smooth = Build(blend: SdfBlendOp.SmoothUnion);
        var position = Position(x: 0.2, y: 0.55, z: -3.0);

        Assert.True(condition: union.TryDistance(distance: out _, instructionsWalked: out var unionWalked, material: out _, position: position));
        Assert.True(condition: smooth.TryDistance(distance: out var smoothDistance, instructionsWalked: out var smoothWalked, material: out _, position: position));
        Assert.Equal(actual: smoothWalked, expected: unionWalked);
        Assert.Equal(actual: smoothDistance, expected: FixedQ4816.FromDouble(value: 0.25));
    }
    // Two instances answer the same unit sphere in different materials. The later one declares the wider bound, so it
    // is the nearest by reach and is walked first; the earlier one's bound, once the evaluator pads it (by 1.0001 and
    // 0.001, its CullBoundRadiusScale and CullBoundRadiusPadding), is the sphere itself, so its reach equals the
    // primer's answer exactly. Only a strictly greater reach may skip it, so the earlier material wins the tie, as it
    // does in stream order.
    [Fact]
    public void AnEarlierInstanceTyingTheNearestKeepsItsMaterial() {
        var tight = BitConverter.SingleToInt32Bits(value: 0.998f);

        while (FixedQ4816.FromDouble(value: ((BitConverter.Int32BitsToSingle(value: tight) * 1.0001f) + 0.001f)) < FixedQ4816.One) {
            tight++;
        }

        var tightRadius = BitConverter.Int32BitsToSingle(value: tight);

        Assert.Equal(actual: FixedQ4816.FromDouble(value: ((tightRadius * 1.0001f) + 0.001f)), expected: FixedQ4816.One);

        var builder = new SdfProgramBuilder();
        var first = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        var second = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));
        var center = new Vector3(x: 7f, y: 0f, z: -3f);

        foreach (var (material, radius) in ((ValueTuple<int, float>[])[(first, tightRadius), (second, 4f),])) {
            _ = builder.Instance(boundCenter: center, boundRadius: radius, emit: instance => {
                _ = instance.ResetPoint();
                _ = instance.Translate(offset: center);
                _ = instance.Sphere(material: material, radius: 1f);
            });
        }

        var evaluator = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));

        foreach (var (x, y, z) in ((ValueTuple<double, double, double>[])[(7.0, 2.5, -3.0), (9.0, 0.0, -3.0), (7.0, 0.0, 0.5), (8.5, 1.5, -1.5),])) {
            Assert.True(condition: evaluator.TryDistance(distance: out _, material: out var material, position: Position(x: x, y: y, z: z)));
            Assert.True(
                condition: (material == first),
                userMessage: $"at ({x}, {y}, {z}) the tie went to material {material}, not the earlier instance's {first}"
            );
        }
    }
    // A chain that ends at a scope's pop, followed by a shape that still reads the chain's translated point: skipping
    // the chain would leave that shape the world point, so the chain must be walked even where its own sphere is far
    // past the scope's answer, which a sphere at the origin keeps small.
    [Fact]
    public void AChainWhosePointIsReadPastItsScopeEdgeIsNeverSkipped() {
        var culledBuilder = new SdfProgramBuilder();
        var referenceBuilder = new SdfProgramBuilder();

        foreach (var builder in ((SdfProgramBuilder[])[culledBuilder, referenceBuilder,])) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            var unwrap = ReferenceEquals(objA: builder, objB: referenceBuilder);

            _ = builder.PushField(compose: SdfBlendOp.Union);
            _ = builder.ResetPoint();

            if (unwrap) {
                _ = builder.Elongate(extents: Vector3.Zero);
            }

            _ = builder.Sphere(material: material, radius: 0.25f);
            _ = builder.ResetPoint();

            if (unwrap) {
                _ = builder.Elongate(extents: Vector3.Zero);
            }

            _ = builder.Translate(offset: new Vector3(x: 20f, y: 0f, z: 0f));
            _ = builder.Sphere(material: material, radius: 0.5f);
            _ = builder.PopField();
            _ = builder.Sphere(material: material, radius: 1f);
        }

        var culled = new SdfFieldEvaluator(program: culledBuilder.Build(buildInstanceGrid: false));
        var reference = new SdfFieldEvaluator(program: referenceBuilder.Build(buildInstanceGrid: false));

        foreach (var x in ((double[])[-3.0, 0.0, 4.0, 17.5, 20.0,])) {
            var position = Position(x: x, y: 0.5, z: 0.0);

            Assert.True(condition: reference.TryDistance(distance: out var expected, material: out _, position: position));
            Assert.True(condition: culled.TryDistance(distance: out var actual, material: out _, position: position));
            Assert.True(condition: (actual == expected), userMessage: $"at x = {x} the culled walk answered {actual}, the reference {expected}");
        }
    }
    [Fact]
    public void EveryStaticShapeKeepsItsReachSphereWhateverItsBlend() {
        SdfProgram Build(SdfBlendOp blend) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.ResetPoint();
            _ = builder.Sphere(material: material, radius: 0.5f);
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(x: 3f, y: 1f, z: -2f));
            _ = builder.Sphere(blend: blend, material: material, radius: 0.7f, smooth: 0.25f);

            return builder.Build(buildInstanceGrid: false);
        }

        var union = Build(blend: SdfBlendOp.Union);
        var shape = (union.Instructions.Count - 1);
        var expected = union.ShapeSkipSphere(instruction: shape);

        Assert.Equal(actual: expected.Mode, expected: SdfProgram.BoundModeStatic);

        foreach (var blend in ((SdfBlendOp[])[SdfBlendOp.Union, SdfBlendOp.SmoothUnion, SdfBlendOp.Subtraction, SdfBlendOp.Intersection,])) {
            var reach = Build(blend: blend).ShapeReachSphere(instruction: shape);

            Assert.Equal(actual: reach, expected: expected);
        }

        Assert.Equal(actual: union.ShapeReachSphere(instruction: (shape - 1)).Mode, expected: SdfProgram.BoundModeNone);
    }
}
