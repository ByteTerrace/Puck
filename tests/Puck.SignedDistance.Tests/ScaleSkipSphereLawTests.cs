using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws for the skip spheres of shapes reached through a <see cref="SdfOp.Scale"/>. <c>mapCore</c> skips a
/// shape (or its whole segment) when <c>|p - C| - R</c> is at least the running minimum, so a skip is exact only if
/// the shape's candidate, as the kernel composes it, is never below <c>|p - C| - R</c>. Each law reads the packed
/// sphere the kernels read and checks that inequality densely against an independent double-precision walk of the
/// chain (inverse rotations, the scale's divide and min-axis distance factor, and the analytic primitive).</summary>
public sealed class ScaleSkipSphereLawTests {
    private const float SphereRadius = 0.5f;

    private static readonly Vector3 BoxHalfExtents = new(x: 0.4f, y: 0.25f, z: 0.6f);
    private static readonly Vector3 FirstOffset = new(x: 3f, y: -1.5f, z: 2f);
    private static readonly Vector3 SecondOffset = new(x: 0.75f, y: 0.5f, z: -1.25f);
    private static readonly Quaternion FirstRotation = Quaternion.Normalize(value: Quaternion.CreateFromYawPitchRoll(pitch: -0.4f, roll: 1.1f, yaw: 0.7f));
    private static readonly Quaternion SecondRotation = Quaternion.Normalize(value: Quaternion.CreateFromYawPitchRoll(pitch: 0.2f, roll: 0.5f, yaw: -1.3f));

    // One chain op, as the kernel applies it to the sample point.
    private abstract record Step;
    private sealed record Move(Vector3 Offset) : Step;
    private sealed record Turn(Quaternion Rotation) : Step;
    private sealed record Stretch(Vector3 Scale) : Step;
    private sealed record Ride(Vector3 Position, Quaternion Orientation) : Step;

    private static SdfProgram Program(IReadOnlyList<Step> steps, bool box) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.ResetPoint();
        foreach (var step in steps) {
            _ = step switch {
                Move move => builder.Translate(offset: move.Offset),
                Turn turn => builder.Rotate(rotation: turn.Rotation),
                Stretch { Scale.X: < 0f } => builder.MirrorX(),
                Stretch stretch => builder.Scale(scale: stretch.Scale),
                Ride => builder.TransformDynamic(slot: 0),
                _ => throw new InvalidOperationException(),
            };
        }
        _ = (box
            ? builder.Box(halfExtents: BoxHalfExtents, material: material, round: 0f)
            : builder.Sphere(material: material, radius: SphereRadius));

        return builder.Build();
    }
    private static int ShapeIndex(SdfProgram program) {
        for (var index = 0; (index < program.Instructions.Count); index++) {
            if (program.Instructions[index].Op == SdfOp.ShapeBlend) {
                return index;
            }
        }

        throw new InvalidOperationException(message: "The program carries no shape.");
    }
    // The candidate the kernel composes, in double: every point op inverted in order, then the primitive times the
    // product of the scales' min axes.
    private static double Candidate(IReadOnlyList<Step> steps, bool box, (double X, double Y, double Z) point) {
        var p = point;
        var distanceScale = 1d;

        foreach (var step in steps) {
            switch (step) {
                case Move move:
                    p = ((p.X - move.Offset.X), (p.Y - move.Offset.Y), (p.Z - move.Offset.Z));
                    break;
                case Turn turn:
                    p = RotateInverse(point: p, rotation: turn.Rotation);
                    break;
                case Stretch { Scale.X: < 0f }:
                    p = (-p.X, p.Y, p.Z);
                    break;
                case Stretch stretch:
                    p = ((p.X / stretch.Scale.X), (p.Y / stretch.Scale.Y), (p.Z / stretch.Scale.Z));
                    distanceScale *= Math.Min(val1: stretch.Scale.X, val2: Math.Min(val1: stretch.Scale.Y, val2: stretch.Scale.Z));
                    break;
                case Ride ride:
                    p = RotateInverse(point: ((p.X - ride.Position.X), (p.Y - ride.Position.Y), (p.Z - ride.Position.Z)), rotation: ride.Orientation);
                    break;
            }
        }

        return (distanceScale * (box ? Box(p: p) : (Length(p: p) - SphereRadius)));
    }
    private static double Box((double X, double Y, double Z) p) {
        var qx = (Math.Abs(value: p.X) - BoxHalfExtents.X);
        var qy = (Math.Abs(value: p.Y) - BoxHalfExtents.Y);
        var qz = (Math.Abs(value: p.Z) - BoxHalfExtents.Z);

        return (Length(p: (Math.Max(val1: qx, val2: 0d), Math.Max(val1: qy, val2: 0d), Math.Max(val1: qz, val2: 0d)))
            + Math.Min(val1: Math.Max(val1: qx, val2: Math.Max(val1: qy, val2: qz)), val2: 0d));
    }
    private static double Length((double X, double Y, double Z) p) => Math.Sqrt(d: (((p.X * p.X) + (p.Y * p.Y)) + (p.Z * p.Z)));
    // q⁻¹·p·q for a unit quaternion, written out rather than through System.Numerics.
    private static (double X, double Y, double Z) RotateInverse((double X, double Y, double Z) point, Quaternion rotation) {
        double ux = -rotation.X, uy = -rotation.Y, uz = -rotation.Z, w = rotation.W;

        var (cx, cy, cz) = Cross(a: (ux, uy, uz), b: point);
        var (tx, ty, tz) = Cross(a: (ux, uy, uz), b: (((w * point.X) + cx), ((w * point.Y) + cy), ((w * point.Z) + cz)));

        return ((point.X + (2d * tx)), (point.Y + (2d * ty)), (point.Z + (2d * tz)));
    }
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (
        ((a.Y * b.Z) - (a.Z * b.Y)),
        ((a.Z * b.X) - (a.X * b.Z)),
        ((a.X * b.Y) - (a.Y * b.X))
    );
    // A dense cloud around a sphere: a 25³ lattice over four radii either side of it, and 512 Fibonacci directions
    // at radii out to 400 radii, where a sphere's lower bound grows fastest against a slowly growing candidate.
    private static IEnumerable<(double X, double Y, double Z)> Samples(Vector3 center, float radius) {
        const int Lattice = 25;
        const int Directions = 512;
        var reach = (4d * radius);

        for (var i = 0; (i < Lattice); i++) {
            for (var j = 0; (j < Lattice); j++) {
                for (var k = 0; (k < Lattice); k++) {
                    yield return (
                        (center.X + (reach * (((2d * i) / (Lattice - 1)) - 1d))),
                        (center.Y + (reach * (((2d * j) / (Lattice - 1)) - 1d))),
                        (center.Z + (reach * (((2d * k) / (Lattice - 1)) - 1d)))
                    );
                }
            }
        }
        double[] distances = [0.5d, 1d, 1.5d, 2d, 3d, 5d, 10d, 25d, 100d, 400d];

        for (var n = 0; (n < Directions); n++) {
            var y = (1d - ((2d * (n + 0.5d)) / Directions));
            var ring = Math.Sqrt(d: (1d - (y * y)));
            var phi = (n * 2.399963229728653d);

            foreach (var distance in distances) {
                var r = (distance * radius);

                yield return ((center.X + ((r * ring) * Math.Cos(d: phi))), (center.Y + (r * y)), (center.Z + ((r * ring) * Math.Sin(a: phi))));
            }
        }
    }
    // The worst violation of candidate(p) >= |p - C| - R over the cloud: positive means a skip there drops a candidate
    // that would have won, so the sphere is unsound.
    private static double WorstBreach(IReadOnlyList<Step> steps, bool box, Vector3 center, float radius) {
        var worst = double.NegativeInfinity;

        foreach (var point in Samples(center: center, radius: radius)) {
            var lowerBound = (Length(p: ((point.X - center.X), (point.Y - center.Y), (point.Z - center.Z))) - radius);

            worst = Math.Max(val1: worst, val2: (lowerBound - Candidate(box: box, point: point, steps: steps)));
        }

        return worst;
    }
    private static IReadOnlyList<Step> Chain(params Vector3[] scales) {
        var steps = new List<Step> { new Move(Offset: FirstOffset), new Turn(Rotation: FirstRotation) };

        foreach (var scale in scales) {
            steps.Add(item: new Stretch(Scale: scale));
            steps.Add(item: new Move(Offset: SecondOffset));
            steps.Add(item: new Turn(Rotation: SecondRotation));
        }

        return steps;
    }

    public static TheoryData<string, bool> UniformChains => new() {
        { "identity", false }, { "identity", true },
        { "shrink", false }, { "shrink", true },
        { "grow", false }, { "grow", true },
        { "compound", false }, { "compound", true },
    };

    private static IReadOnlyList<Step> UniformChain(string name) => name switch {
        "identity" => Chain(scales: Vector3.One),
        "shrink" => Chain(scales: new Vector3(value: 0.35f)),
        "grow" => Chain(scales: new Vector3(value: 2.5f)),
        "compound" => Chain(new Vector3(value: 3f), new Vector3(value: 0.25f)),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(name)),
    };

    [MemberData(memberName: nameof(UniformChains))]
    [Theory]
    public void AUniformlyScaledShapesSphereBoundsItsCandidateEverywhere(string chain, bool box) {
        var steps = UniformChain(name: chain);
        var program = Program(box: box, steps: steps);
        var shape = program.ShapeSkipSphere(instruction: ShapeIndex(program: program));
        var segment = program.SegmentSkipSphere(segment: 0);

        Assert.Equal(expected: SdfProgram.BoundModeStatic, actual: shape.Mode);
        Assert.Equal(expected: SdfProgram.BoundModeStatic, actual: segment.Mode);
        Assert.True(condition: (WorstBreach(box: box, center: shape.Center, radius: shape.Radius, steps: steps) <= 0d), userMessage: $"The {chain} shape sphere ({shape.Center}, {shape.Radius}) is breached.");
        Assert.True(condition: (WorstBreach(box: box, center: segment.Center, radius: segment.Radius, steps: steps) <= 0d), userMessage: $"The {chain} segment sphere ({segment.Center}, {segment.Radius}) is breached.");
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnIdentityScaleLeavesTheSphereBitIdentical(bool box) {
        var scaled = Program(box: box, steps: Chain(scales: Vector3.One));
        var plain = Program(box: box, steps: [new Move(Offset: FirstOffset), new Turn(Rotation: FirstRotation), new Move(Offset: SecondOffset), new Turn(Rotation: SecondRotation)]);

        Assert.Equal(expected: plain.ShapeSkipSphere(instruction: ShapeIndex(program: plain)), actual: scaled.ShapeSkipSphere(instruction: ShapeIndex(program: scaled)));
        Assert.Equal(expected: plain.SegmentSkipSphere(segment: 0), actual: scaled.SegmentSkipSphere(segment: 0));
    }
    [InlineData(0f)]
    [InlineData(1.2f)]
    [InlineData(2.7f)]
    [Theory]
    public void AUniformScaleAfterADynamicTransformBoundsItsCandidateInEveryPose(float yaw) {
        Step[] chain = [new Move(Offset: SecondOffset), new Turn(Rotation: SecondRotation), new Stretch(Scale: new Vector3(value: 1.75f)), new Move(Offset: FirstOffset)];
        var program = Program(box: true, steps: [new Ride(Position: Vector3.Zero, Orientation: Quaternion.Identity), .. chain]);
        var shape = program.ShapeSkipSphere(instruction: ShapeIndex(program: program));

        Assert.Equal(expected: SdfProgram.BoundModeDynamic, actual: shape.Mode);
        Assert.Equal(expected: 0, actual: shape.Slot);

        // The kernel adds the slot's per-frame position to the packed center; the orientation must be covered by the radius.
        var position = new Vector3(x: -4f, y: 1f, z: 7.5f);
        var pose = new Ride(Position: position, Orientation: Quaternion.Normalize(value: Quaternion.CreateFromYawPitchRoll(pitch: (0.5f * yaw), roll: -yaw, yaw: yaw)));

        Assert.True(condition: (WorstBreach(box: true, center: (shape.Center + position), radius: shape.Radius, steps: [pose, .. chain]) <= 0d));
    }
    [Fact]
    public void AUniformScaleBeforeADynamicTransformKeepsNoSphere() {
        var program = Program(box: false, steps: [new Stretch(Scale: new Vector3(value: 2f)), new Ride(Position: Vector3.Zero, Orientation: Quaternion.Identity)]);

        Assert.Equal(expected: SdfProgram.BoundModeNone, actual: program.ShapeSkipSphere(instruction: ShapeIndex(program: program)).Mode);
        Assert.Equal(expected: SdfProgram.BoundModeNone, actual: program.SegmentSkipSphere(segment: 0).Mode);
    }
    // The proof the non-uniform arm rests on, run: the max-axis sphere contains the stretched shape, yet its candidate
    // min(s)·f(S⁻¹q) falls below that sphere's lower bound far along the long axis, so a skip there would change the
    // field. The analysis therefore keeps no sphere, for a stretch and for the mirror alike.
    [InlineData(3f, 1f, 1f)]
    [InlineData(1f, 0.5f, 1.25f)]
    [InlineData(-1f, 1f, 1f)]
    [Theory]
    public void ANonUniformScaleKeepsNoSphereBecauseNoSphereBoundsItsCandidate(float x, float y, float z) {
        var steps = Chain(scales: new Vector3(x: x, y: y, z: z));
        var program = Program(box: false, steps: steps);

        Assert.Equal(expected: SdfProgram.BoundModeNone, actual: program.ShapeSkipSphere(instruction: ShapeIndex(program: program)).Mode);
        Assert.Equal(expected: SdfProgram.BoundModeNone, actual: program.SegmentSkipSphere(segment: 0).Mode);
        if (x < 0f) {
            return;
        }

        // The conservative candidate: the forward image of the local center, and the radius times the largest axis.
        var forward = (Vector3.Transform(value: (SecondOffset * new Vector3(x: x, y: y, z: z)), rotation: FirstRotation) + FirstOffset);
        var conservative = (((SphereRadius * MathF.Max(x: x, y: MathF.Max(x: y, y: z))) * 1.0001f) + 0.001f);

        Assert.True(condition: (WorstBreach(box: false, center: forward, radius: conservative, steps: steps) > 0d));
    }
}
