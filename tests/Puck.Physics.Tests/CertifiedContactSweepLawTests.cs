using System.Numerics;
using Puck.Maths;
using Puck.Physics.Fields;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="FixedFieldContactSolver.ResolveSweep"/> proves a moving body's travel clear of the
/// field before the endpoint solve runs, so a body never passes through geometry however fast it moves or however close
/// it starts, it stops on the face it approached, a sweep cut short or refused keeps only the ground it proved, and the
/// work is counted. A radius stepper, which walks the step in samples one radius apart and resolves each, is kept
/// here as each tunneling fixture's red leg: it passes through, so the fixture discriminates.
/// </summary>
public sealed class CertifiedContactSweepLawTests {
    private static readonly FixedQ4816 Skin = FixedQ4816.FromDouble(value: 0.02);
    private static readonly FixedQ4816 Radius = FixedQ4816.FromDouble(value: 0.5);
    private static readonly FixedBodyColliderVolume[] Ball = [new(Kind: FixedBodyColliderKind.Sphere, Center: FixedVector3.Zero, Endpoint: default, HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: Radius)];

    // The wall's near face, at z = WallZ + WallHalfThickness, which a body flying toward -z approaches.
    private const float WallZ = -100f;
    private const float WallHalfThickness = 0.01f;

    private static FixedVector3 Z(double z) => new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: z));
    private static double Read(FixedQ4816 value) => (value.Value / 65536.0);
    private static SdfFieldEvaluator Wall(bool overestimating = false, Vector3? halfExtents = null, float y = 0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var extents = (halfExtents ?? new Vector3(x: 8f, y: 8f, z: WallHalfThickness));

        _ = builder.Translate(offset: new Vector3(x: 0f, y: y, z: WallZ)).Box(halfExtents: extents, material: material, round: 0f);

        // A chamfer intersection of a box with itself reads √2 of its distance outside it: a field whose Lipschitz
        // bound exceeds one, which a sample-trusting stepper steps over.
        if (overestimating) {
            _ = builder.Box(blend: SdfBlendOp.ChamferIntersection, halfExtents: extents, material: material, round: 0f, smooth: 0f);
        }

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedFieldContactSolver Solver(SdfFieldEvaluator evaluator, FixedContactSweepWork work, int budget = FixedFieldContactSolver.DefaultSweepBoundsQueryBudget, ICertifiedSweepQuery? sweep = null, bool noSweep = false) => new(
        contactSkin: Skin,
        field: evaluator,
        gradientProbe: FixedQ4816.Zero,
        gradientUp: false,
        groundedThreshold: FixedQ4816.FromDouble(value: 0.5),
        maxIterations: 4,
        query: evaluator,
        sweep: (noSweep ? null : (sweep ?? evaluator)),
        sweepBoundsQueryBudget: budget,
        sweepWork: work
    );
    // A radius stepper: samples one smallest radius apart, each resolved, stopping at the first contact.
    private static FixedVector3 RadiusStepper(FixedFieldContactSolver solver, FixedVector3 previous, FixedVector3 target, FixedBodyColliderVolume[] volumes) {
        var delta = (target - previous);
        var step = volumes.Min(selector: static volume => volume.Radius);
        var velocity = FixedVector3.Zero;
        var up = FixedVector3.UnitY;

        if (delta.Length <= step) {
            _ = solver.Resolve(orientation: FixedQuaternion.Identity, position: ref target, up: in up, velocity: ref velocity, volumes: volumes);

            return target;
        }

        var steps = Math.Max(val1: 2, val2: ((int)(((delta.Length.Value + step.Value) - 1L) / step.Value)));

        for (var index = 1; (index <= steps); index++) {
            var proposed = (previous + (delta * (FixedQ4816.FromInteger(value: index) / FixedQ4816.FromInteger(value: steps))));
            var candidate = proposed;
            var resolution = solver.Resolve(orientation: FixedQuaternion.Identity, position: ref candidate, up: in up, velocity: ref velocity, volumes: volumes);

            if (resolution.Grounded || (resolution.ObstructionNormal != FixedVector3.Zero) || (candidate != proposed)) {
                return candidate;
            }
        }

        _ = solver.Resolve(orientation: FixedQuaternion.Identity, position: ref target, up: in up, velocity: ref velocity, volumes: volumes);

        return target;
    }
    private static FixedVector3 Sweep(FixedFieldContactSolver solver, FixedVector3 previous, FixedVector3 target, FixedBodyColliderVolume[]? volumes = null) {
        var velocity = (target - previous);

        _ = solver.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref target, previousPosition: in previous, up: FixedVector3.UnitY, velocity: ref velocity, volumes: (volumes ?? Ball));

        return target;
    }

    [InlineData(1.0)]
    [InlineData(10.0)]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(100000.0)]
    [Theory]
    public void AThinWallIsNeverCrossedAtAnySpeedAndTheBodyStopsOnItsFace(double speed) {
        var evaluator = Wall();
        var solver = Solver(evaluator: evaluator, work: new FixedContactSweepWork());
        var face = (WallZ + WallHalfThickness);
        var resting = ((face + Read(value: Radius)) + Read(value: Skin));

        // Ten starts strung before the wall, each flying until it has passed the wall's plane by a full step.
        for (var start = 0; (start < 10); start++) {
            var position = Z(z: (-90.0 - (start * 0.37)));

            for (var tick = 0; ((tick < 64) && (Read(value: position.Z) > (face - 1.0))); tick++) {
                var next = Sweep(previous: position, solver: solver, target: (position + Z(z: -speed)));

                Assert.True(condition: (Read(value: next.Z) > face), userMessage: $"start {start}, speed {speed}: the body passed the face to {Read(value: next.Z)}");

                if (next == position) {
                    break;
                }

                position = next;
            }

            // It rests exactly where the contact solve holds a body off a face: its radius plus the skin.
            Assert.InRange(actual: Read(value: position.Z), high: (resting + 0.0005), low: (resting - 0.0005));
        }
    }
    [Fact]
    public void AFieldThatOverstatesItsDistanceIsStillNeverCrossed() {
        var evaluator = Wall(overestimating: true);
        var solver = Solver(evaluator: evaluator, work: new FixedContactSweepWork());
        var face = (WallZ + WallHalfThickness);
        var stepperCrossings = 0;

        for (var start = 0; (start < 40); start++) {
            foreach (var speed in ((double[])[15.0, 100.0, 1000.0])) {
                var previous = Z(z: (-94.0 + (start * 0.137)));
                var target = (previous + Z(z: -speed));

                Assert.True(condition: (Read(value: Sweep(previous: previous, solver: solver, target: target).Z) > face), userMessage: $"start {start}, speed {speed}: the certified sweep crossed");

                if (Read(value: RadiusStepper(previous: previous, solver: solver, target: target, volumes: Ball).Z) < face) {
                    stepperCrossings++;
                }
            }
        }

        // The red leg: a radius stepper trusts the √2-overstated distance and steps through.
        Assert.True(condition: (stepperCrossings > 0), userMessage: "the radius stepper never crossed; the fixture no longer discriminates");
    }
    [Fact]
    public void ABodyStartingInsideItsRadiusOfTheWallStopsOnTheNearSide() {
        var evaluator = Wall();
        var solver = Solver(evaluator: evaluator, work: new FixedContactSweepWork());
        var face = (WallZ + WallHalfThickness);
        var stepperCrossings = 0;
        var cases = 0;

        // Starting closer to the face than the radius, moving less than the radius: a radius stepper resolves such a
        // step only at its endpoint, past the wall, where the push points on through.
        foreach (var gap in ((double[])[0.3, 0.25, 0.2, 0.15, 0.1, 0.05])) {
            foreach (var move in ((double[])[0.12, 0.2, 0.3, 0.34, 0.45])) {
                if ((gap - move) > -(2 * WallHalfThickness)) {
                    continue;
                }

                cases++;

                var previous = Z(z: (face + gap));
                var target = (previous + Z(z: -move));

                Assert.True(condition: (Read(value: Sweep(previous: previous, solver: solver, target: target).Z) > face), userMessage: $"gap {gap}, move {move}: the certified sweep crossed");

                if (Read(value: RadiusStepper(previous: previous, solver: solver, target: target, volumes: Ball).Z) < face) {
                    stepperCrossings++;
                }
            }
        }

        Assert.True(condition: (cases > 0));
        Assert.Equal(actual: stepperCrossings, expected: cases);
    }
    [Fact]
    public void ACapsuleSweepsItsWholeCoreNotOnlyItsEnds() {
        // A thin bar at waist height, between the capsule's two sphere centres, crossed edge-on: neither end comes near it.
        var evaluator = Wall(halfExtents: new Vector3(x: 8f, y: 0.005f, z: WallHalfThickness), y: 0.85f);
        var solver = Solver(evaluator: evaluator, work: new FixedContactSweepWork());
        var capsule = new FixedBodyColliderVolume[] { new(Kind: FixedBodyColliderKind.Capsule, Center: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 0.35), Z: FixedQ4816.Zero), Endpoint: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 1.35), Z: FixedQ4816.Zero), HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.35)) };
        var face = (WallZ + WallHalfThickness);

        foreach (var speed in ((double[])[5.0, 50.0, 500.0])) {
            var reached = Sweep(previous: Z(z: -95.0), solver: solver, target: Z(z: (-95.0 - speed)), volumes: capsule);

            Assert.True(condition: (Read(value: reached.Z) > face), userMessage: $"speed {speed}: the capsule's waist passed the bar to {Read(value: reached.Z)}");
        }
    }
    [Fact]
    public void ASweepThatRunsOutOfBudgetKeepsOnlyTheGroundItProved() {
        var evaluator = Wall();
        var previous = Z(z: -50.0);
        var target = Z(z: -120.0);
        var reached = new List<double>();

        foreach (var budget in ((int[])[1, 2, 3, 5, 8, 64])) {
            var work = new FixedContactSweepWork();
            var position = Sweep(previous: previous, solver: Solver(budget: budget, evaluator: evaluator, work: work), target: target);

            reached.Add(item: Read(value: position.Z));
            Assert.True(condition: (Read(value: position.Z) > (WallZ + WallHalfThickness)));
            Assert.True(condition: (work.Read(kind: FixedContactSweepWork.BoundsQueries) <= budget), userMessage: $"budget {budget}: spent {work.Read(kind: FixedContactSweepWork.BoundsQueries)}");

            if (budget == 1) {
                // One query proves the start and nothing more: the body does not move.
                Assert.Equal(actual: position, expected: previous);
                Assert.Equal(expected: 1L, actual: work.Read(kind: FixedContactSweepWork.Exhausted));
            }
        }

        // A larger budget never proves less ground.
        for (var index = 1; (index < reached.Count); index++) {
            Assert.True(condition: (reached[index] <= reached[(index - 1)]), userMessage: $"budget step {index}: {reached[index]} after {reached[(index - 1)]}");
        }
    }
    [Fact]
    public void ABodyAtOrBeyondTheFieldsFrameStopsAtProvedGround() {
        var builder = new SdfProgramBuilder();

        _ = builder.Trapezoid(bottomHalfWidth: 1f, halfHeight: 1f, lift: SdfLift.Extrude, liftAmount: 1f, material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), topHalfWidth: 0.5f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var frame = evaluator.Frame;
        var work = new FixedContactSweepWork();
        var solver = Solver(evaluator: evaluator, work: work);
        var step = new FixedVector3(X: FixedQ4816.FromInteger(value: 64), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);

        Assert.True(condition: ((frame > FixedQ4816.Zero) && (frame < FixedQ4816.FromInteger(value: (1L << 30)))), userMessage: $"the trapezoid's frame is {frame}");

        // Outside the frame nothing can be proved, so the body keeps its start.
        var outside = new FixedVector3(X: (frame + FixedQ4816.FromInteger(value: 10)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);

        Assert.Equal(expected: outside, actual: Sweep(previous: outside, solver: solver, target: (outside + step)));

        // Crossing out of the frame, the body stops inside it, at ground the sweep proved.
        var inside = new FixedVector3(X: (frame - FixedQ4816.FromInteger(value: 32)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var reached = Sweep(previous: inside, solver: solver, target: (inside + step));

        Assert.True(condition: ((reached.X >= inside.X) && (reached.X <= frame)), userMessage: $"reached {reached.X} from {inside.X} against the frame {frame}");
        Assert.True(condition: (work.Read(kind: FixedContactSweepWork.Exhausted) >= 2L));
    }
    [Fact]
    public void AClearStepMovesTheWholeWayAndCountsItsWork() {
        var evaluator = Wall();
        var work = new FixedContactSweepWork();
        var previous = Z(z: -10.0);
        var target = Z(z: -11.0);

        Assert.Equal(expected: target, actual: Sweep(previous: previous, solver: Solver(evaluator: evaluator, work: work), target: target));
        Assert.Equal(expected: 1L, actual: work.Read(kind: FixedContactSweepWork.Sweeps));
        Assert.Equal(expected: 0L, actual: (work.Read(kind: FixedContactSweepWork.Contacts) + work.Read(kind: FixedContactSweepWork.Exhausted)));
        Assert.InRange(actual: work.Read(kind: FixedContactSweepWork.BoundsQueries), high: 4L, low: 2L);
        // A field with no geometry to cross is not swept at all.
        var empty = new FixedContactSweepWork();

        Assert.Equal(expected: target, actual: Sweep(previous: previous, solver: Solver(evaluator: evaluator, noSweep: true, work: empty), target: target));
        Assert.Equal(expected: 0L, actual: empty.Read(kind: FixedContactSweepWork.Sweeps));
    }
    [Fact]
    public void ABodyPressingIntoAWallSpendsAFewQueriesATickNotItsWholeBudget() {
        var evaluator = Wall();
        var work = new FixedContactSweepWork();
        var solver = Solver(evaluator: evaluator, work: work);
        var position = Z(z: -90.0);

        for (var tick = 0; (tick < 40); tick++) {
            position = Sweep(previous: position, solver: solver, target: (position + Z(z: -50.0)));
        }

        var before = work.Read(kind: FixedContactSweepWork.BoundsQueries);

        for (var tick = 0; (tick < 10); tick++) {
            position = Sweep(previous: position, solver: solver, target: (position + Z(z: -50.0)));
        }

        // The contact tolerance ends the approach at the skin instead of creeping on: well under the 64 budget a tick.
        Assert.InRange(actual: ((work.Read(kind: FixedContactSweepWork.BoundsQueries) - before) / 10), high: 16L, low: 1L);
        Assert.Equal(expected: 0L, actual: work.Read(kind: FixedContactSweepWork.Exhausted));
    }
    [Fact]
    public void TheLatticeBoundsEncloseEveryPointAnswerAndTheUnionRefusesWithAnyPart() {
        var lattice = new FieldLattice(input: new FieldLatticeInput(
            Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: 6, Depth: 6, Layers: 1, StepEveryTicks: 1),
            Fields: [new FieldDescriptorInput(Name: "ground", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 100), HeightScale: FixedQ4816.One, IsMedium: false, Color: "#808080")],
            Reactions: [],
            Paint: []
        ));

        lattice.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[.. Enumerable.Range(count: 36, start: 0).Select(selector: static cell => FixedQ4816.FromInteger(value: ((cell * 7) % 5)).Value)]]));

        var solid = new FieldLatticeSolid(lattice: lattice);
        var random = new Random(Seed: 9091);

        for (var trial = 0; (trial < 400); trial++) {
            var lower = new FixedVector3(X: FixedQ4816.FromDouble(value: ((random.NextDouble() * 9) - 1.5)), Y: FixedQ4816.FromDouble(value: ((random.NextDouble() * 7) - 1.5)), Z: FixedQ4816.FromDouble(value: ((random.NextDouble() * 9) - 1.5)));
            var size = new FixedVector3(X: FixedQ4816.FromDouble(value: (random.NextDouble() * 2)), Y: FixedQ4816.FromDouble(value: (random.NextDouble() * 2)), Z: FixedQ4816.FromDouble(value: (random.NextDouble() * 2)));

            Assert.True(condition: solid.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: (lower + size))));

            for (var sample = 0; (sample < 16); sample++) {
                var point = (lower + new FixedVector3(X: (size.X * FixedQ4816.FromDouble(value: random.NextDouble())), Y: (size.Y * FixedQ4816.FromDouble(value: random.NextDouble())), Z: (size.Z * FixedQ4816.FromDouble(value: random.NextDouble()))));

                Assert.True(condition: solid.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: point)));
                Assert.True(condition: bounds.Contains(value: distance), userMessage: $"trial {trial}: {Read(value: distance)} outside [{Read(value: bounds.Lower)}, {Read(value: bounds.Upper)}]");
            }
        }

        // The union of the lattice and a shapeless program refuses every box: a part that cannot bound a box leaves the
        // union nothing proved there.
        var union = new FieldBoundsUnion(a: solid, b: new SdfFieldEvaluator(program: new SdfProgramBuilder().Build()));

        Assert.False(condition: union.TryDistanceBounds(distance: out _, lower: FixedPosition.Zero, upper: FixedPosition.Zero));
    }
}
