using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Holds the cull to the values the fixed-point walks compute at the edges of its certificates: a clipped
/// scope bounded by its operands, a quantized rotation that contracts a far point, a translation sum a float could not
/// hold, a box whose loose interval a certified sweep reads, and random fields of rotated, scaled and blended shapes.
/// Each law compares a program against a cull-off reference built from the same instructions with no instance declared
/// and every chain opened by a zero elongation, an exact identity that leaves the chain no certificate.</summary>
public sealed class SdfFieldEvaluatorCullBoundaryLawTests {
    [InlineData(SdfBlendOp.Intersection)]
    [InlineData(SdfBlendOp.SmoothIntersection)]
    [Theory]
    public void AnIntersectionBoundDoesNotReplaceItsOperandDistanceBounds(SdfBlendOp blend) {
        SdfFieldEvaluator Build(bool reference) {
            var builder = new SdfProgramBuilder();
            var plane = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            var box = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

            _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: plane);

            if (!reference) {
                _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2f);
            }

            _ = builder.PushField(compose: SdfBlendOp.Union);
            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Box(halfExtents: new Vector3(x: 1f, y: 20f, z: 1f), round: 0f, material: box);
            _ = builder.Box(halfExtents: new Vector3(x: 20f, y: 1f, z: 1f), round: 0f, material: box, blend: blend, smooth: 0.25f);
            _ = builder.PopField();

            if (!reference) {
                _ = builder.EndInstance();
            }

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        AssertSameAnswer(culled: Build(reference: false), reference: Build(reference: true), position: Position(x: 10L, y: 10L));
    }
    [InlineData(SdfBlendOp.Subtraction)]
    [InlineData(SdfBlendOp.SmoothSubtraction)]
    [Theory]
    public void ACarvedCapKeepsItsSubjectsDistanceBound(SdfBlendOp blend) {
        SdfFieldEvaluator Build(bool reference) {
            var builder = new SdfProgramBuilder();
            var plane = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            var cap = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

            _ = builder.Plane(normal: Vector3.UnitY, offset: 15f, material: plane);

            if (!reference) {
                _ = builder.BeginInstance(boundCenter: new Vector3(x: 17.5f, y: 0f, z: 0f), boundRadius: 14f);
            }

            _ = builder.PushField(compose: SdfBlendOp.Union);
            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Sphere(radius: 20f, material: cap);
            Chain(builder: builder, reference: reference, offset: new Vector3(x: -5f, y: 0f, z: 0f));
            _ = builder.Box(halfExtents: new Vector3(value: 20f), round: 0f, material: cap, blend: blend, smooth: 0.25f);
            _ = builder.PopField();

            if (!reference) {
                _ = builder.EndInstance();
            }

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        AssertSameAnswer(culled: Build(reference: false), reference: Build(reference: true), position: Position(x: -30L, y: 0L));
    }
    // A unit sphere behind a quantized 45-degree turn, a hundred thousand units out along +X, behind a plane its exact
    // sphere would clear by a twentieth of a unit: the fixed-point turn contracts the far point by more than that, so
    // the sphere's computed answer wins, and a skip must account for it, alone or in a scoped instance.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AQuantizedRotationKeepsItsWinningCandidate(bool scopedInstance) {
        SdfFieldEvaluator Build(bool reference) {
            var builder = new SdfProgramBuilder();
            var plane = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            var sphere = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

            _ = builder.Plane(normal: Vector3.UnitX, offset: -1.1f, material: plane);

            if (scopedInstance && !reference) {
                _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1.01f);
            }

            if (scopedInstance) {
                _ = builder.PushField(compose: SdfBlendOp.Union);
            }

            Chain(builder: builder, reference: reference, offset: Vector3.Zero);
            _ = builder.Rotate(rotation: new Quaternion(w: 0.9238795f, x: 0f, y: 0f, z: 0.38268343f));
            _ = builder.Sphere(radius: 1f, material: sphere);

            if (scopedInstance) {
                _ = builder.PopField();
            }

            if (scopedInstance && !reference) {
                _ = builder.EndInstance();
            }

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        AssertSameAnswer(culled: Build(reference: false), reference: Build(reference: true), position: Position(x: 100_000L, y: 0L));
    }
    // A unit sphere translated by 2²⁴ and then by one: a float sum of the two lands a unit short, while the fixed-point
    // walk puts the sphere exactly under the query, where it wins inside the plane's half-unit answer.
    [Fact]
    public void ATranslatedChainUsesTheCompiledCenter() {
        SdfFieldEvaluator Build(bool reference) {
            var builder = new SdfProgramBuilder();
            var plane = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            var sphere = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

            _ = builder.Plane(normal: Vector3.UnitY, offset: -0.5f, material: plane);
            Chain(builder: builder, reference: reference, offset: new Vector3(x: 16_777_216f, y: 0f, z: 0f));
            _ = builder.Translate(offset: Vector3.UnitX);
            _ = builder.Sphere(radius: 1f, material: sphere);

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        AssertSameAnswer(culled: Build(reference: false), reference: Build(reference: true), position: Position(x: 16_777_217L, y: 0L));
    }
    // A plane held by an elongation, answering a half unit, beside a unit sphere turned 45 degrees three units away: the
    // turned sphere's loose interval lowers the box's lower end, which a stationary sweep at budget two reads as contact,
    // so the box walk may not skip it however far its point answers lie.
    [Fact]
    public void BoxCullingKeepsTheCertifiedSweepAnswer() {
        SdfFieldEvaluator Build(bool reference) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.ResetPoint().Elongate(extents: new Vector3(value: 100f));
            _ = builder.Plane(normal: Vector3.UnitZ, offset: 0.5f, material: material);
            Chain(builder: builder, reference: reference, offset: new Vector3(x: 3f, y: 0f, z: 0f));
            _ = builder.Rotate(rotation: new Quaternion(w: 0.9238795f, x: 0f, y: 0f, z: 0.38268343f));
            _ = builder.Sphere(radius: 1f, material: material);

            return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        }

        var culledField = Build(reference: false);
        var referenceField = Build(reference: true);
        var culled = new CertifiedFieldSweep(field: culledField);
        var reference = new CertifiedFieldSweep(field: referenceField);
        var tolerance = FixedQ4816.FromDouble(value: 0.01);

        Assert.True(condition: reference.TryCertifiedSweep(origin: FixedPosition.Zero, displacement: FixedVector3.Zero, radius: FixedQ4816.One, boundsQueryBudget: 2, contactTolerance: tolerance, sweep: out var expected));
        Assert.True(condition: culled.TryCertifiedSweep(origin: FixedPosition.Zero, displacement: FixedVector3.Zero, radius: FixedQ4816.One, boundsQueryBudget: 2, contactTolerance: tolerance, sweep: out var actual));
        Assert.Equal(expected: expected.Outcome, actual: actual.Outcome);
        Assert.Equal(expected: expected.Fraction, actual: actual.Fraction);

        foreach (var half in ((double[])[0.0, 0.5, 1.0, 2.0,])) {
            var lower = Position(x: -half, y: -half, z: -half);
            var upper = Position(x: half, y: half, z: half);

            Assert.True(condition: referenceField.TryDistanceBounds(distance: out var expectedBounds, lower: lower, upper: upper));
            Assert.True(condition: culledField.TryDistanceBounds(distance: out var actualBounds, lower: lower, upper: upper));
            Assert.True(condition: (actualBounds == expectedBounds), userMessage: $"the box of half-width {half} answered {actualBounds}, the cull-off walk {expectedBounds}");
        }
    }
    // Random fields of rotated, scaled, mirrored and translated shapes of every certified kind, in union instances,
    // scoped instances and bare chains, some far out and some at large coordinates, beside a plane: every point answer,
    // its material, and every box's interval match the cull-off walk.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [Theory]
    public void CertifiedSkipsLeaveEveryRandomFieldAsTheCullOffWalkAnswersIt(int seed) {
        var random = new Random(Seed: seed);
        var culledBuilder = new SdfProgramBuilder();
        var referenceBuilder = new SdfProgramBuilder();
        var chains = new List<Action<SdfProgramBuilder, bool, SdfBlendOp, int>>();
        var origin = new Vector3(x: (seed * 1000f), y: 0f, z: (seed * -700f));

        foreach (var builder in ((SdfProgramBuilder[])[culledBuilder, referenceBuilder,])) {
            for (var material = 0; (material < 4); material++) {
                _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: material, y: 1f, z: 1f)));
            }
        }

        for (var chain = 0; (chain < 48); chain++) {
            var cluster = (origin + new Vector3(x: ((chain / 6) * 9f), y: 0f, z: ((chain % 3) * 7f)));
            var offset = (cluster + new Vector3(x: Next(random: random, scale: 2f), y: Next(random: random, scale: 2f), z: Next(random: random, scale: 2f)));
            var rotation = Quaternion.Normalize(value: new Quaternion(x: Next(random: random, scale: 1f), y: Next(random: random, scale: 1f), z: Next(random: random, scale: 1f), w: Next(random: random, scale: 1f)));
            var scale = (0.5f + (random.NextSingle() * 2f));
            var mirror = (random.Next(maxValue: 4) == 0);
            var inner = new Vector3(x: Next(random: random, scale: 0.5f), y: Next(random: random, scale: 0.5f), z: Next(random: random, scale: 0.5f));
            var shape = random.Next(maxValue: 7);
            var size = (0.2f + random.NextSingle());

            chains.Add(item: (builder, reference, blend, material) => {
                Chain(builder: builder, offset: offset, reference: reference);
                _ = builder.Rotate(rotation: rotation);
                _ = builder.Scale(scale: new Vector3(value: scale));

                if (mirror) {
                    _ = builder.MirrorX();
                }

                _ = builder.Translate(offset: inner);
                _ = shape switch {
                    0 => builder.Sphere(blend: blend, material: material, radius: size, smooth: 0.3f),
                    1 => builder.Box(blend: blend, halfExtents: new Vector3(x: size, y: (size * 0.5f), z: (size * 1.5f)), material: material, round: (size * 0.2f), smooth: 0.3f),
                    2 => builder.Cylinder(blend: blend, halfHeight: size, material: material, radius: (size * 0.6f), smooth: 0.3f),
                    3 => builder.Torus(blend: blend, majorRadius: size, material: material, minorRadius: (size * 0.3f), smooth: 0.3f),
                    4 => builder.Capsule(blend: blend, endpoint: new Vector3(x: size, y: (size * 2f), z: 0f), material: material, radius: (size * 0.4f), smooth: 0.3f),
                    5 => builder.Trapezoid(blend: blend, bottomHalfWidth: size, halfHeight: (size * 0.7f), lift: SdfLift.Revolve, liftAmount: (size * 0.5f), material: material, smooth: 0.3f, topHalfWidth: (size * 0.4f)),
                    _ => builder.Trapezoid(blend: blend, bottomHalfWidth: size, halfHeight: (size * 0.7f), lift: SdfLift.Extrude, liftAmount: (size * 0.5f), material: material, smooth: 0.3f, topHalfWidth: (size * 0.4f)),
                };
            });
        }

        foreach (var builder in ((SdfProgramBuilder[])[culledBuilder, referenceBuilder,])) {
            var reference = ReferenceEquals(objA: builder, objB: referenceBuilder);

            _ = builder.Plane(material: 0, normal: Vector3.UnitY, offset: 3f);

            for (var group = 0; (group < 8); group++) {
                var members = chains.GetRange(count: 6, index: (group * 6));
                var center = (origin + new Vector3(x: (group * 9f), y: 0f, z: 7f));

                void Emit(SdfProgramBuilder target) {
                    switch (group % 3) {
                        case 0: {
                                foreach (var member in members) {
                                    member(target, reference, SdfBlendOp.Union, (group % 4));
                                }

                                break;
                            }
                        case 1: {
                                _ = target.PushField(compose: SdfBlendOp.Union);

                                for (var member = 0; (member < members.Count); member++) {
                                    members[member](target, reference, ((SdfBlendOp[])[SdfBlendOp.Union, SdfBlendOp.SmoothUnion, SdfBlendOp.SmoothUnion, SdfBlendOp.Subtraction, SdfBlendOp.Intersection, SdfBlendOp.SmoothUnion,])[member], ((member + group) % 4));
                                }

                                _ = target.PopField();
                                break;
                            }
                        default: {
                                for (var member = 0; (member < members.Count); member++) {
                                    // An odd seed's top-level smooth union leaves no instance to walk ahead of its place.
                                    members[member](target, reference, (((member == 2) && ((seed % 2) == 1)) ? SdfBlendOp.SmoothUnion : SdfBlendOp.Union), (member % 4));
                                }

                                break;
                            }
                    }
                }

                if (reference || ((group % 3) == 2)) {
                    Emit(target: builder);
                } else {
                    _ = builder.Instance(boundCenter: center, boundRadius: 30f, emit: Emit);
                }
            }
        }

        var culled = new SdfFieldEvaluator(program: culledBuilder.Build(buildInstanceGrid: false));
        var cullOff = new SdfFieldEvaluator(program: referenceBuilder.Build(buildInstanceGrid: false));

        for (var sample = 0; (sample < 400); sample++) {
            var far = ((sample % 4) == 0);
            var point = (origin + new Vector3(
                x: (far ? Next(random: random, scale: 4000f) : (Next(random: random, scale: 40f) + 30f)),
                y: Next(random: random, scale: (far ? 2000f : 6f)),
                z: (far ? Next(random: random, scale: 4000f) : (Next(random: random, scale: 20f) + 7f))
            ));
            var position = Position(x: point.X, y: point.Y, z: point.Z);

            Assert.True(condition: cullOff.TryDistance(distance: out var expected, material: out var expectedMaterial, position: position));
            Assert.True(condition: culled.TryDistance(distance: out var actual, material: out var actualMaterial, position: position));
            Assert.True(
                condition: ((actual == expected) && (actualMaterial == expectedMaterial)),
                userMessage: $"seed {seed} at {point} the culled walk answered {actual} in material {actualMaterial}, the cull-off walk {expected} in material {expectedMaterial}"
            );

            var half = ((sample % 3) * 0.75f);
            var lower = Position(x: (point.X - half), y: (point.Y - half), z: (point.Z - half));
            var upper = Position(x: (point.X + half), y: (point.Y + half), z: (point.Z + half));

            Assert.True(condition: cullOff.TryDistanceBounds(distance: out var expectedBounds, lower: lower, upper: upper));
            Assert.True(condition: culled.TryDistanceBounds(distance: out var actualBounds, lower: lower, upper: upper));
            Assert.True(
                condition: (actualBounds == expectedBounds),
                userMessage: $"seed {seed} the box of half-width {half} about {point} answered {actualBounds}, the cull-off walk {expectedBounds}"
            );
        }
    }

    private static float Next(Random random, float scale) => (((random.NextSingle() * 2f) - 1f) * scale);
    private static void Chain(SdfProgramBuilder builder, bool reference, Vector3 offset) {
        _ = builder.ResetPoint();

        if (reference) {
            _ = builder.Elongate(extents: Vector3.Zero);
        }

        _ = builder.Translate(offset: offset);
    }
    private static FixedPosition Position(long x, long y) => FixedPosition.FromLocal(local: new FixedVector3(
        X: FixedQ4816.FromInteger(value: x),
        Y: FixedQ4816.FromInteger(value: y),
        Z: FixedQ4816.Zero
    ));
    private static FixedPosition Position(double x, double y, double z) => FixedPosition.FromLocal(local: new FixedVector3(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    ));
    // The culled walk answers the reference's distance and material, which is the expected material, and its point box
    // is the reference's.
    private static void AssertSameAnswer(SdfFieldEvaluator culled, SdfFieldEvaluator reference, FixedPosition position) {
        Assert.True(condition: reference.TryDistance(distance: out var expected, material: out var expectedMaterial, position: position));
        Assert.Equal(actual: expectedMaterial, expected: 1);
        Assert.True(condition: culled.TryDistance(distance: out var actual, material: out var actualMaterial, position: position));
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(actual: actualMaterial, expected: expectedMaterial);
        Assert.True(condition: reference.TryDistanceBounds(distance: out var expectedBounds, lower: position, upper: position));
        Assert.True(condition: culled.TryDistanceBounds(distance: out var actualBounds, lower: position, upper: position));
        Assert.Equal(actual: actualBounds, expected: expectedBounds);
    }
}
