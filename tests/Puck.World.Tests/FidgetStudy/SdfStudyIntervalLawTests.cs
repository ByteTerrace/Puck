using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>
/// THE LAW: the study's interval evaluator is sound against point evaluation. Over balls of every size, from a single
/// point to metres, and over the counters view's own tile cone slabs, every sampled point's field value lies inside
/// the enclosure, and the tape pruned by the walk's choices returns exactly the full walk's value at every sample.
/// It covers a fixture exercising every transcribed shape, transform, fold, field op and blend, plus the counters and
/// parity worlds' composed programs. The discriminating leg removes the outward rounding and must then find samples
/// outside their enclosures.
/// </summary>
public sealed class SdfStudyIntervalLawTests {
    private static readonly double[] Radii = [0.0, 1e-3, 0.05, 0.3, 1.5];

    [Fact]
    public void EveryFixtureSampleLiesInsideItsEnclosure() {
        var tape = new SdfStudyTape(program: Fixture(), transforms: []);
        var outcome = Check(tape: tape, outward: true, centers: FixtureCenters(), samplesPerBall: 24);

        Assert.True(condition: SdfStudyPoint.IsFaithful(first: out var first, tape: tape), userMessage: first);
        Assert.Equal(actual: outcome.Outside, expected: 0L);
        Assert.Equal(actual: outcome.Mismatches, expected: 0L);
        Assert.True(condition: (outcome.Pruned > 0L), userMessage: "the fixture must exercise pruning");
    }
    [Fact]
    public void RemovingOutwardRoundingIsCaught() {
        var tape = new SdfStudyTape(program: Fixture(), transforms: []);
        var outcome = Check(tape: tape, outward: false, centers: FixtureCenters(), samplesPerBall: 24);

        Assert.True(condition: (outcome.Outside > 0L), userMessage: "without outward rounding some sample must fall outside its enclosure");
    }
    [InlineData("counters", "tests/Puck.Counters/counters.world.json")]
    [InlineData("parity", "tests/Puck.Parity/parity.world.json")]
    [Theory]
    public void EveryWorldTileSlabSampleLiesInsideItsEnclosure(string name, string path) {
        var scene = SdfStudyScene.Load(name: name, relativePath: path);
        var tape = new SdfStudyTape(program: scene.Program, transforms: scene.Transforms);
        var view = SdfStudyView.CountersFloor;
        var interval = new SdfStudyInterval(tape: tape);
        var all = Enumerable.Range(start: 0, count: tape.Segments.Length).ToArray();
        var outside = 0L;
        var mismatches = 0L;
        var samples = 0L;

        Assert.True(condition: SdfStudyPoint.IsFaithful(first: out var first, tape: tape), userMessage: first);
        for (var tileY = 2; (tileY < 51); tileY += 7) {
            for (var tileX = 3; (tileX < 90); tileX += 9) {
                var cone = view.Cone(tileSize: 16, tileX: tileX, tileY: tileY);
                var boundaries = SdfStudyTiles.Boundaries(chord: cone.Chord, far: view.FarDistance);

                for (var k = 0; (k < (boundaries.Length - 1)); k += 5) {
                    var (center, radius) = SdfStudyTiles.Slab(view: view, cone: cone, t0: boundaries[k], t1: boundaries[(k + 1)]);
                    var live = new bool[tape.InstructionCount];

                    var (lo, hi) = interval.Walk(attribution: null, center: center, live: live, radius: radius, segments: all);
                    var rewrites = interval.Rewrites(live: live, segments: all);

                    for (var sample = 0; (sample < 4); sample++) {
                        // Pixel rays of the tile at depths across the slab.
                        var u = ((((tileX * 16) + ((sample * 5) % 16)) + 0.5) / view.Width);
                        var v = ((((tileY * 16) + ((sample * 11) % 16)) + 0.5) / view.Height);
                        var t = (boundaries[k] + ((boundaries[(k + 1)] - boundaries[k]) * (sample / 3.0)));
                        var p = (view.Eye + (view.Direction(u: u, v: v) * t));
                        var work = new SdfStudyWork();
                        var value = SdfStudyPoint.Evaluate(live: null, p: p, rewrites: null, segments: all, sphereSkips: false, tape: tape, work: ref work);
                        var pruned = SdfStudyPoint.Evaluate(live: live, p: p, rewrites: rewrites, segments: all, sphereSkips: false, tape: tape, work: ref work);

                        samples++;
                        outside += (((value < lo) || (value > hi)) ? 1 : 0);
                        mismatches += ((pruned != value) ? 1 : 0);
                    }
                }
            }
        }
        Assert.True(condition: (samples > 0L));
        Assert.Equal(actual: outside, expected: 0L);
        Assert.Equal(actual: mismatches, expected: 0L);
    }

    private static (long Outside, long Mismatches, long Pruned) Check(SdfStudyTape tape, bool outward, IReadOnlyList<V3> centers, int samplesPerBall) {
        var interval = new SdfStudyInterval(outward: outward, tape: tape);
        var all = Enumerable.Range(start: 0, count: tape.Segments.Length).ToArray();
        var random = new Random(Seed: 20261001);
        var outside = 0L;
        var mismatches = 0L;
        var pruned = 0L;

        foreach (var center in centers) {
            foreach (var radius in Radii) {
                var live = new bool[tape.InstructionCount];

                var (lo, hi) = interval.Walk(attribution: null, center: center, live: live, radius: radius, segments: all);
                var rewrites = interval.Rewrites(live: live, segments: all);

                pruned += live.Count(predicate: flag => !flag);
                for (var sample = 0; (sample < samplesPerBall); sample++) {
                    var p = ((sample == 0) ? center : (center + (Direction(random: random) * (radius * Math.Cbrt(d: random.NextDouble())))));
                    var work = new SdfStudyWork();
                    var value = SdfStudyPoint.Evaluate(live: null, p: p, rewrites: null, segments: all, sphereSkips: false, tape: tape, work: ref work);
                    var prunedValue = SdfStudyPoint.Evaluate(live: live, p: p, rewrites: rewrites, segments: all, sphereSkips: false, tape: tape, work: ref work);

                    outside += (((value < lo) || (value > hi)) ? 1 : 0);
                    mismatches += ((prunedValue != value) ? 1 : 0);
                }
            }
        }

        return (outside, mismatches, pruned);
    }
    private static V3 Direction(Random random) {
        while (true) {
            var v = new V3(X: ((random.NextDouble() * 2.0) - 1.0), Y: ((random.NextDouble() * 2.0) - 1.0), Z: ((random.NextDouble() * 2.0) - 1.0));
            var length = v.Length;

            if ((length > 1e-3) && (length <= 1.0)) {
                return (v * (1.0 / length));
            }
        }
    }
    private static List<V3> FixtureCenters() {
        var centers = new List<V3>();

        for (var x = -6.0; (x <= 6.0); x += 0.75) {
            for (var y = -1.0; (y <= 3.0); y += 0.8) {
                centers.Add(item: new V3(X: (x + 0.013), Y: (y + 0.007), Z: ((0.31 * x) - 0.4)));
            }
        }

        return centers;
    }
    // Every transcribed shape, transform, fold, field op and blend, in instances and field scopes.
    private static SdfProgram Fixture() {
        var builder = new SdfProgramBuilder();
        var m = builder.AddMaterial(material: new(Vector3.One));
        var tilt = Quaternion.CreateFromYawPitchRoll(pitch: 0.25f, roll: -0.15f, yaw: 0.4f);

        // Relief ops act on the whole accumulator, so each sits in a field scope of its own.
        builder.PushField().Plane(normal: Vector3.UnitY, offset: 0.5f, material: m);
        builder.NoiseDisplace(frequency: 1.7f, amplitude: 0.04f, octaves: 3).PopField();
        builder.Instance(boundCenter: new Vector3(x: -4f, y: 0.5f, z: -1.5f), boundRadius: 2.5f, emit: b => {
            b.ResetPoint().Translate(offset: new Vector3(x: -4f, y: 0.5f, z: -1.5f)).Rotate(rotation: tilt).Scale(scale: new Vector3(x: 1.4f, y: 0.8f, z: 1.1f));
            b.Box(halfExtents: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f), round: 0.08f, material: m);
            b.Sphere(radius: 0.45f, material: m, blend: SdfBlendOp.SmoothUnion, smooth: 0.2f);
            b.Cylinder(radius: 0.2f, halfHeight: 0.9f, material: m, blend: SdfBlendOp.Subtraction);
            b.Torus(majorRadius: 0.5f, minorRadius: 0.1f, material: m, blend: SdfBlendOp.Union);
        });
        builder.Instance(boundCenter: new Vector3(x: -1f, y: 0.6f, z: -0.6f), boundRadius: 2.2f, emit: b => {
            b.ResetPoint().Translate(offset: new Vector3(x: -1f, y: 0.6f, z: -0.6f)).SymmetryPlane(normal: Vector3.UnitX, offset: 0f).Translate(offset: new Vector3(x: 0.5f, y: 0f, z: 0f));
            b.Capsule(endpoint: new Vector3(x: 0.3f, y: 0.7f, z: 0.1f), radius: 0.15f, material: m);
            b.RoundCone(lowerRadius: 0.3f, upperRadius: 0.1f, height: 0.6f, material: m, blend: SdfBlendOp.SmoothIntersection, smooth: 0.1f);
            b.Superellipsoid(radii: new Vector3(x: 0.4f, y: 0.3f, z: 0.5f), exponent: 2f, material: m, blend: SdfBlendOp.Union);
            b.Superellipsoid(radii: new Vector3(x: 0.3f, y: 0.5f, z: 0.3f), exponent: 3.5f, material: m, blend: SdfBlendOp.SmoothSubtraction, smooth: 0.05f);
        });
        builder.Instance(boundCenter: new Vector3(x: 2f, y: 0.5f, z: 0.6f), boundRadius: 3f, emit: b => {
            b.ResetPoint().PushField().Translate(offset: new Vector3(x: 2f, y: 0.5f, z: 0.6f)).RepeatLimited(spacing: new Vector3(x: 0.7f, y: 10f, z: 0.9f), limit: new Vector3(x: 2f, y: 0f, z: 1f));
            b.RoundedRectangle(halfWidth: 0.25f, halfHeight: 0.2f, cornerRadius: 0.05f, lift: SdfLift.Extrude, liftAmount: 0.15f, material: m);
            b.ChamferedRectangle(halfWidth: 0.2f, halfHeight: 0.3f, chamfer: 0.06f, lift: SdfLift.Revolve, liftAmount: 0.1f, material: m, blend: SdfBlendOp.GrooveUnion, smooth: 0.05f);
            b.Trapezoid(bottomHalfWidth: 0.3f, topHalfWidth: 0.1f, halfHeight: 0.25f, lift: SdfLift.Extrude, liftAmount: 0.1f, material: m, blend: SdfBlendOp.PipeUnion, smooth: 0.03f);
            b.Displace(frequency: new Vector3(x: 6f, y: 5f, z: 7f), amplitude: 0.02f).PopField();
        });
        builder.Instance(boundCenter: new Vector3(x: 5f, y: 0.6f, z: 1.5f), boundRadius: 2.5f, emit: b => {
            b.ResetPoint().Translate(offset: new Vector3(x: 5f, y: 0.6f, z: 1.5f)).PushField();
            b.RepeatPolar(count: 6, axis: SdfAxis.Y, mirror: true).Translate(offset: new Vector3(x: 0.8f, y: 0f, z: 0f));
            b.Box(halfExtents: new Vector3(x: 0.15f, y: 0.5f, z: 0.1f), round: 0f, material: m);
            b.ResetPoint().Translate(offset: new Vector3(x: 5f, y: 0.6f, z: 1.5f)).Elongate(extents: new Vector3(x: 0.2f, y: 0f, z: 0.1f));
            b.Sphere(radius: 0.3f, material: m, blend: SdfBlendOp.Intersection);
            b.Onion(thickness: 0.05f).Dilate(radius: 0.02f);
            b.PopField();
            b.ResetPoint().PushField().Translate(offset: new Vector3(x: 5f, y: -0.3f, z: 1.5f)).Repeat(spacing: new Vector3(x: 0.5f, y: 100f, z: 0.5f));
            b.Sphere(radius: 0.12f, material: m, blend: SdfBlendOp.ChamferUnion, smooth: 0.05f);
            b.ResetPoint().Translate(offset: new Vector3(x: 5f, y: -0.3f, z: 1.5f));
            b.CellDisplace(amplitude: 0.03f, frequency: 2f, mode: SdfCellMode.F2MinusF1, randomness: 0.15f, seed: 7u).PopField();
        });

        return builder.Build(buildInstanceGrid: false);
    }
}
