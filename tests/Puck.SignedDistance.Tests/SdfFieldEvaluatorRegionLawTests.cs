using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

// A regional evaluator answers what the plain one answers, to the bit: its region tapes drop only what cannot change a
// point walk anywhere in the region. Each law builds one program and holds the two evaluators to each other at points
// chosen to stand where a wrong drop would show.
public sealed class SdfFieldEvaluatorRegionLawTests {
    private static FixedPosition At(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z)));
    private static FixedPosition AtRaw(long x, long y, long z) =>
        FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z)));
    private static IEnumerable<FixedPosition> Lattice(Vector3 low, Vector3 high, int steps) {
        for (var x = 0; (x <= steps); x++) {
            for (var y = 0; (y <= steps); y++) {
                for (var z = 0; (z <= steps); z++) {
                    yield return At(
                        x: (low.X + (((high.X - low.X) * x) / steps)),
                        y: (low.Y + (((high.Y - low.Y) * y) / steps)),
                        z: (low.Z + (((high.Z - low.Z) * z) / steps))
                    );
                }
            }
        }
    }
    // The raws either side of each face a region can have near the origin, on every axis at once and one at a time.
    private static IEnumerable<FixedPosition> Faces() {
        foreach (var face in new[] { -16L, -8L, -4L, -2L, 0L, 2L, 4L, 8L, 16L }) {
            foreach (var offset in new[] { -1L, 0L, 1L }) {
                var raw = ((face << FixedQ4816.FractionBitCount) + offset);

                yield return AtRaw(x: raw, y: raw, z: raw);
                yield return AtRaw(x: raw, y: 32768L, z: 32768L);
                yield return AtRaw(x: 32768L, y: raw, z: 32768L);
                yield return AtRaw(x: 32768L, y: 32768L, z: raw);
            }
        }
    }
    private static void AssertSame(SdfProgram program, IEnumerable<FixedPosition> points) {
        var plain = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact);
        var regional = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact, regional: true);

        Assert.False(condition: plain.IndexesRegions);
        Assert.True(condition: regional.IndexesRegions);

        foreach (var point in points) {
            var answered = plain.TryDistance(distance: out var expected, material: out var expectedMaterial, position: point);

            Assert.Equal(actual: regional.TryDistance(distance: out var actual, material: out var actualMaterial, position: point), expected: answered);
            Assert.Equal(actual: actual, expected: expected);
            Assert.Equal(actual: actualMaterial, expected: expectedMaterial);

            var hasGradient = plain.TryFieldGradient(gradient: out var expectedGradient, position: point);

            Assert.Equal(actual: regional.TryFieldGradient(gradient: out var actualGradient, position: point), expected: hasGradient);
            Assert.Equal(actual: actualGradient, expected: expectedGradient);
        }
    }

    // A fold is not continuous across its seam: the copy a point reads changes there, so a chain far from a region's
    // centre can be the nearest thing at another point of the region. The chain carries no certificate, and the walk's
    // enclosure of it over the box, through the fold's own interval rule, is what keeps it.
    [Fact]
    public void AFoldedChainStaysInTheTapeOfARegionItsSeamCrosses() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var copy = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -1.3125f, z: 0f)).Box(halfExtents: new Vector3(x: 8f, y: 1f, z: 8f), material: ground, round: 0f);
        _ = builder.ResetPoint().RepeatLimited(limit: new Vector3(x: 1f, y: 0f, z: 0f), spacing: new Vector3(x: 2f, y: 1000f, z: 1000f)).Translate(offset: new Vector3(x: 0.75f, y: 0f, z: 0f))
            .Sphere(blend: SdfBlendOp.SmoothUnion, material: copy, radius: 0.125f, smooth: 0.14f);

        var program = builder.Build();
        var plain = new SdfFieldEvaluator(program: program);

        // Either side of the seam at x = 1: the copy at 0.75 is the nearest thing on one side and far on the other.
        Assert.True(condition: plain.TryDistance(distance: out var near, material: out var nearMaterial, position: At(x: 0.9921875, y: 0.0, z: 0.0)));
        Assert.True(condition: plain.TryDistance(distance: out var far, material: out _, position: At(x: 1.015625, y: 0.0, z: 0.0)));
        Assert.Equal(actual: nearMaterial, expected: copy);
        Assert.True(condition: (near < far));

        AssertSame(points: Lattice(high: new Vector3(x: 1.5f, y: 0.5f, z: 0.5f), low: new Vector3(x: 0.5f, y: -0.25f, z: -0.5f), steps: 16).Concat(second: Faces()), program: program);
    }
    // Two instances whose candidates are equal on a plane: the earlier keeps its material there. The region's primer is
    // the later one, which stands inside the region, so the earlier is skipped only when it is strictly past it.
    [Fact]
    public void EqualCandidatesKeepTheEarlierInstancesMaterial() {
        var builder = new SdfProgramBuilder();
        var earlier = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var later = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        var distant = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

        _ = builder.Instance(boundCenter: new Vector3(x: -0.5f, y: 1f, z: 1f), boundRadius: 1f, emit: instance => _ = instance.ResetPoint().Translate(offset: new Vector3(x: -0.5f, y: 1f, z: 1f)).Sphere(material: earlier, radius: 0.5f));
        _ = builder.Instance(boundCenter: new Vector3(x: 1.5f, y: 1f, z: 1f), boundRadius: 1f, emit: instance => _ = instance.ResetPoint().Translate(offset: new Vector3(x: 1.5f, y: 1f, z: 1f)).Sphere(material: later, radius: 0.5f));

        for (var copy = 0; (copy < 24); copy++) {
            var centre = new Vector3(x: (40f + (3f * copy)), y: 1f, z: (-30f + (5f * copy)));

            _ = builder.Instance(boundCenter: centre, boundRadius: 1f, emit: instance => _ = instance.ResetPoint().Translate(offset: centre).Sphere(material: distant, radius: 0.5f));
        }

        var program = builder.Build();
        var plain = new SdfFieldEvaluator(program: program);

        Assert.True(condition: program.InstancesComposable);
        // On the plane x = 0.5 the two spheres are equally far, to the raw.
        Assert.True(condition: plain.TryDistance(distance: out _, material: out var tied, position: At(x: 0.5, y: 1.25, z: 0.75)));
        Assert.Equal(actual: tied, expected: earlier);

        AssertSame(points: Lattice(high: new Vector3(x: 2f, y: 2f, z: 2f), low: new Vector3(x: -1f, y: 0f, z: 0f), steps: 12).Concat(second: Faces()), program: program);
    }
    // Operands in stream order from far to near, in steps smaller than their smooth radius, so each blends with the one
    // before: no one of them takes the accumulator over, and what a region keeps has to carry the chain of blends.
    [InlineData(0.05f)]
    [InlineData(0.4f)]
    [InlineData(2f)]
    [Theory]
    public void AStaircaseOfSmoothUnionsAnswersAsTheWholeStream(float smooth) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var step = 0; (step < 96); step++) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (30f - (0.3f * step)), y: (0.02f * step), z: 0f)).Sphere(blend: SdfBlendOp.SmoothUnion, material: material, radius: 0.1f, smooth: smooth);
        }

        // A hard union after the run, and one that takes the accumulator over wherever it is the nearest.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1f, y: -3f, z: 0f)).Box(halfExtents: new Vector3(x: 4f, y: 0.5f, z: 4f), material: material, round: 0f);

        AssertSame(
            points: Lattice(high: new Vector3(x: 6f, y: 3f, z: 1f), low: new Vector3(x: -2f, y: -3f, z: -1f), steps: 12)
                .Concat(second: Lattice(high: new Vector3(x: 31f, y: 1f, z: 0.5f), low: new Vector3(x: 20f, y: -1f, z: -0.5f), steps: 8))
                .Concat(second: Faces()),
            program: builder.Build()
        );
    }
    // A relieved scope, a carve and a chamfer beside plain shapes: what the region's walk cannot judge it keeps.
    [Fact]
    public void ScopesCarvesAndOtherBlendsAnswerAsTheWholeStream() =>
        AssertSame(points: Lattice(high: new Vector3(x: 8f, y: 3f, z: 3f), low: new Vector3(x: -8f, y: -2f, z: -3f), steps: 12).Concat(second: Faces()), program: MixedProgram());
    // A box query over regions is cut along the faces of the regions it meets and answers the hull of its parts. Each
    // box here stands where a wrong cut or a wrong tape would show: inside one region, across region faces of every
    // edge and across the origin, where eight root regions meet, about the carve and the chamfer, as a single point, and
    // wider than half a root region, which the whole stream answers. The enclosure is held to the whole stream's point
    // answers, and to itself from an index no query has touched, since a tape is a function of the program alone.
    [Fact]
    public void ABoxOverRegionsEnclosesEveryPointAnswerInIt() {
        var program = MixedProgram();
        var plain = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact);
        var warmed = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact, regional: true);
        var centres = new[] {
            new Vector3(x: 0f, y: 0f, z: 0f), new Vector3(x: 0.3f, y: 0.1f, z: -0.2f), new Vector3(x: 2f, y: 0f, z: 2f), new Vector3(x: 4f, y: 0.5f, z: 0f),
            new Vector3(x: 5f, y: 0.75f, z: 0f), new Vector3(x: 5.2f, y: 1.4f, z: 0.3f), new Vector3(x: -5f, y: 0f, z: 1f), new Vector3(x: -4f, y: 0.6f, z: 2f),
            new Vector3(x: 8f, y: 1f, z: -1f), new Vector3(x: -8f, y: -1f, z: 0f), new Vector3(x: 0f, y: 6.5f, z: 0f), new Vector3(x: 15.9f, y: 0.2f, z: 0.1f),
            new Vector3(x: -16f, y: 6f, z: 16f),
        };

        foreach (var centre in centres) {
            foreach (var half in new[] { 0f, 0.2f, 0.55f, 1.1f, 2.3f, 4.5f }) {
                var low = (centre - new Vector3(value: half));
                var high = (centre + new Vector3(x: half, y: (1.5f * half), z: half));
                var (lower, upper) = (At(x: low.X, y: low.Y, z: low.Z), At(x: high.X, y: high.Y, z: high.Z));
                var fresh = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact, regional: true);

                Assert.True(condition: warmed.TryDistanceBounds(distance: out var bounds, lower: lower, upper: upper));
                Assert.True(condition: fresh.TryDistanceBounds(distance: out var again, lower: lower, upper: upper));
                Assert.Equal(actual: again, expected: bounds);

                foreach (var point in Lattice(high: high, low: low, steps: 5)) {
                    Assert.True(condition: plain.TryDistance(distance: out var distance, material: out _, position: point));
                    Assert.True(condition: bounds.Contains(value: distance), userMessage: $"The box about {centre} of half {half} answered {bounds}, which leaves out {distance}.");
                }
            }
        }
    }
    private static SdfProgram MixedProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var other = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitZ));

        _ = builder.Instance(boundCenter: new Vector3(x: 0f, y: -4f, z: 0f), boundRadius: 12f, emit: instance => {
            _ = instance.PushField(compose: SdfBlendOp.Union).ResetPoint().Translate(offset: new Vector3(x: 0f, y: -4f, z: 0f)).Sphere(material: material, radius: 4f);
            _ = instance.NoiseDisplace(amplitude: 0.25f, frequency: 1.5f, octaves: 2);
            _ = instance.PopField();
        });
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 5f, y: 0f, z: 0f)).Box(halfExtents: new Vector3(value: 1f), material: other, round: 0.1f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 5f, y: 0.75f, z: 0f)).Sphere(blend: SdfBlendOp.Subtraction, material: other, radius: 0.75f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -5f, y: 0f, z: 1f)).Sphere(blend: SdfBlendOp.ChamferUnion, material: material, radius: 1f, smooth: 0.3f);

        for (var copy = 0; (copy < 40); copy++) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (-60f + (3f * copy)), y: 6f, z: (20f - copy))).Capsule(blend: ((copy % 2) == 0) ? SdfBlendOp.Union : SdfBlendOp.SmoothUnion, endpoint: new Vector3(x: 0f, y: 1f, z: 0f), material: other, radius: 0.25f, smooth: 0.2f);
        }

        return builder.Build();
    }
    // The point of a tape: far from most of a large program, a walk reads a small part of it.
    [Fact]
    public void ATapeHoldsASmallPartOfALargeStream() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var x = 0; (x < 16); x++) {
            for (var z = 0; (z < 16); z++) {
                for (var y = 0; (y < 4); y++) {
                    _ = builder.ResetPoint().Translate(offset: new Vector3(x: (12f * x), y: (12f * y), z: (12f * z))).Superellipsoid(blend: SdfBlendOp.SmoothUnion, exponent: 2f, material: material, radii: new Vector3(x: 1f, y: 0.75f, z: 1f), smooth: 0.2f);
                }
            }
        }

        var program = builder.Build();
        var regional = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Contact, regional: true);

        Assert.True(condition: regional.TryDistance(distance: out _, instructionsWalked: out var walked, material: out _, position: At(x: 60.5, y: 13.0, z: 61.0)));
        Assert.True(condition: ((walked * 50) < program.Instructions.Count), userMessage: $"A walk beside one shape of {program.Instructions.Count} instructions read {walked}.");
        Assert.True(condition: regional.TryDistanceBounds(distance: out _, instructionsWalked: out var boxWalked, lower: At(x: 60.0, y: 12.5, z: 60.5), upper: At(x: 61.0, y: 13.5, z: 61.5)));
        Assert.True(condition: ((boxWalked * 50) < program.Instructions.Count), userMessage: $"A box beside one shape of {program.Instructions.Count} instructions read {boxWalked}.");

        AssertSame(points: Lattice(high: new Vector3(x: 64f, y: 15f, z: 64f), low: new Vector3(x: 56f, y: 9f, z: 56f), steps: 8), program: program);
    }
}
