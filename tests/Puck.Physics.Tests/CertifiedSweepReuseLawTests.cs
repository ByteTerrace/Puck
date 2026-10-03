using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests;

/// <summary>Exact bounds reuse removes repeated field walks without changing any body-step result, including
/// contact and budget exhaustion. The uncached evaluator is the independent cost and state reference.</summary>
public sealed class CertifiedSweepReuseLawTests(ITestOutputHelper output) {
    [InlineData(false, false, 64)]
    [InlineData(true, false, 64)]
    [InlineData(false, true, 64)]
    [InlineData(true, false, 1)]
    [Theory]
    public void RepeatedBodyStepsKeepEveryStateBitAndReduceFieldWalks(bool capsule, bool wall, int budget) {
        var evaluator = Scene();
        var direct = new CountedBounds(evaluator: evaluator);
        var cached = new SdfBoundsCache(field: evaluator);
        var before = Run(bounds: direct, budget: budget, capsule: capsule, evaluator: evaluator, wall: wall);
        var after = Run(bounds: cached, budget: budget, capsule: capsule, evaluator: evaluator, wall: wall);

        Assert.Equal(actual: after.Hash, expected: before.Hash);
        Assert.Equal(actual: after.Sweeps, expected: before.Sweeps);
        Assert.Equal(actual: after.Queries, expected: before.Queries);
        Assert.Equal(expected: direct.Queries, actual: cached.BoundsQueries);
        Assert.True(condition: (cached.FieldEvaluations < (direct.Queries / 4)),
            userMessage: $"exact reuse must remove at least three quarters of field walks: {cached.FieldEvaluations} of {direct.Queries}");
        Assert.True(condition: (cached.InstructionsWalked < (direct.Instructions / 4)));
        output.WriteLine(message: $"capsule={capsule}, wall={wall}, budget={budget}: sweeps {before.Sweeps} -> {after.Sweeps}; bounds {before.Queries} -> {after.Queries}; field evaluations {direct.Queries} -> {cached.FieldEvaluations}; instructions {direct.Instructions} -> {cached.InstructionsWalked}; state {before.Hash:X16} = {after.Hash:X16}");
    }

    private static (ulong Hash, long Sweeps, long Queries) Run(IFieldBounds bounds, int budget, bool capsule, SdfFieldEvaluator evaluator, bool wall) {
        var work = new FixedContactSweepWork();
        var solver = new FixedFieldContactSolver(
            contactSkin: Q(value: 0.02), field: evaluator, gradientProbe: FixedQ4816.Zero, gradientUp: false,
            groundedThreshold: Q(value: 0.5), maxIterations: 4, query: evaluator,
            sweep: new CertifiedFieldSweep(field: bounds), sweepBoundsQueryBudget: budget, sweepWork: work);
        FixedBodyColliderVolume[] volumes = [new(
            Kind: (capsule ? FixedBodyColliderKind.Capsule : FixedBodyColliderKind.Sphere),
            Center: V(x: 0, y: 0.5, z: 0), Endpoint: V(x: 0, y: 1.5, z: 0), HalfExtents: default,
            Rotation: FixedQuaternion.Identity, Radius: Q(value: 0.5))];
        var position = (wall ? V(x: 0, y: 1, z: -90) : V(x: 0, y: 0.02, z: 0));
        var hash = Fnv1aHash.Create();

        for (var tick = 0; (tick < 256); tick++) {
            var previous = position;
            var velocity = (wall ? V(x: 0, y: 0, z: -50) : V(x: 0, y: -0.1, z: 0));

            position += velocity;
            var resolution = solver.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position,
                previousPosition: previous, up: FixedVector3.UnitY, velocity: ref velocity, volumes: volumes);

            Add(hash: ref hash, vector: position);
            Add(hash: ref hash, vector: velocity);
            Add(hash: ref hash, vector: resolution.ObstructionNormal);
            Add(hash: ref hash, vector: resolution.GroundNormal);
            hash.Add(value: (resolution.Grounded ? 1u : 0u));
            hash.Add(value: (resolution.Unproved ? 1u : 0u));
            hash.Add(value: ((uint)resolution.Refusal));
        }

        return (hash.Value, work.Read(kind: FixedContactSweepWork.Sweeps), work.Read(kind: FixedContactSweepWork.BoundsQueries));
    }
    private static SdfFieldEvaluator Scene() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0, y: 0, z: -100))
            .Box(halfExtents: new Vector3(x: 8, y: 8, z: 0.01f), material: material, round: 0);

        for (var index = 0; (index < 12); index++) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (10 + (index * 3)), y: 1, z: 0))
                .Sphere(material: material, radius: 1);
        }

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static void Add(ref Fnv1aHash hash, FixedVector3 vector) {
        hash.Add(value: vector.X.Value);
        hash.Add(value: vector.Y.Value);
        hash.Add(value: vector.Z.Value);
    }

    private sealed class CountedBounds(SdfFieldEvaluator evaluator) : IFieldBounds {
        public long Instructions { get; private set; }
        public long Queries { get; private set; }
        public FixedQ4816 StepScale => evaluator.StepScale;

        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            Queries++;
            var answered = evaluator.TryDistanceBounds(distance: out distance, instructionsWalked: out var walked, lower: lower, upper: upper);

            Instructions += walked;
            return answered;
        }
    }
}
