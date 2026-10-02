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
    // The step's state: the body's position and velocity, and the lattice's cells.
    private static ulong StateHash(FieldLattice lattice, FixedVector3 position, FixedVector3 velocity) {
        var hash = Fnv1aHash.Create();

        foreach (var value in ((FixedQ4816[])[position.X, position.Y, position.Z, velocity.X, velocity.Y, velocity.Z])) {
            hash.Add(value: value.Value);
        }

        lattice.AppendStateHash(hash: ref hash);

        return hash.Value;
    }
    // The review's witness lattice: one cell, one layer, one field held at one with a height scale of the cell.
    private static FieldLattice UnitLattice(FixedQ4816 cell, FixedVector3 origin) => new(input: new FieldLatticeInput(
        Lattice: new FieldLatticeTopology(CellSize: cell, Depth: 1, Layers: 1, Origin: origin, StepEveryTicks: 1, Width: 1),
        Fields: [new FieldDescriptorInput(Name: "ground", Initial: FixedQ4816.One, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.One, HeightScale: cell, IsMedium: false, Color: "#808080")],
        Reactions: [],
        Paint: []
    ));
    // One cell at the origin, one unit on a side, its column one unit high: a box from y = -1 to y = 1.
    private static FieldLattice OneCellLattice() {
        var lattice = new FieldLattice(input: new FieldLatticeInput(
            Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: 1, Depth: 1, Layers: 1, StepEveryTicks: 1),
            Fields: [new FieldDescriptorInput(Name: "ground", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 100), HeightScale: FixedQ4816.One, IsMedium: false, Color: "#808080")],
            Reactions: [],
            Paint: []
        ));

        lattice.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[FixedQ4816.One.Value]]));

        return lattice;
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
    public void ACompoundBodyMovesItsShortestSweepEvenWhenEveryFractionRoundsAlikeOnTheQ16Grid() {
        // Two spheres nine units apart along a million-unit step toward the wall. The trailing core proves about 9.74
        // units and the leading core about 0.74, each under 2⁻¹⁶ of the step, so on the Q16 grid both fractions read
        // zero; the body must still move only the leading core's travel, or the leading sphere is carried through.
        var evaluator = Wall();
        var work = new FixedContactSweepWork();
        var body = new FixedBodyColliderVolume[] {
            new(Kind: FixedBodyColliderKind.Sphere, Center: FixedVector3.Zero, Endpoint: default, HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: Radius),
            new(Kind: FixedBodyColliderKind.Sphere, Center: Z(z: -9.0), Endpoint: default, HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: Radius),
        };
        var previous = Z(z: -90.0);
        var reached = Sweep(previous: previous, solver: Solver(evaluator: evaluator, work: work), target: (previous + Z(z: -1_000_000.0)), volumes: body);
        var face = (WallZ + WallHalfThickness);

        Assert.Equal(expected: 2L, actual: work.Read(kind: FixedContactSweepWork.Contacts));

        foreach (var volume in body) {
            var centre = Read(value: (reached.Z + volume.Center.Z));

            Assert.True(condition: (centre > face), userMessage: $"the sphere at body offset {Read(value: volume.Center.Z)} passed the face to {centre}; the body reached {Read(value: reached.Z)}");
        }

        // The leading sphere stops within its radius and the skin of the face, so the body moved the least proved travel.
        Assert.InRange(actual: Read(value: (reached.Z + body[1].Center.Z)), high: (face + 1.0), low: face);
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
    public void AnOversizedCapsuleCoreIsRefusedByNameAndTheStepChangesNothing() {
        // A two-raw radius on a 2⁴⁷-raw core needs 2³⁵ sweep pieces: past an int, where the piece count once threw
        // OverflowException inside the tick, and past MaximumCapsuleSweepPieces by far.
        var evaluator = Wall();
        var work = new FixedContactSweepWork();
        var capsule = new FixedBodyColliderVolume[] { new(Kind: FixedBodyColliderKind.Capsule, Center: FixedVector3.Zero, Endpoint: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromRawBits(value: (1L << 47)), Z: FixedQ4816.Zero), HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromRawBits(value: 2L)) };
        var previous = Z(z: -10.0);
        var position = Z(z: -11.0);
        var velocity = Z(z: -60.0);
        var resolution = Solver(evaluator: evaluator, work: work).ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position, previousPosition: in previous, up: FixedVector3.UnitY, velocity: ref velocity, volumes: capsule);

        Assert.Equal(expected: ContactRefusal.OversizedCapsuleCore, actual: resolution.Refusal);
        Assert.Equal(actual: position, expected: previous);
        Assert.Equal(expected: Z(z: -60.0), actual: velocity);
        Assert.Equal(expected: 0L, actual: work.Read(kind: FixedContactSweepWork.Sweeps));
        Assert.False(condition: FixedFieldContactSolver.CapsuleCoreFitsSweep(core: capsule[0].Endpoint, pieces: out _, radius: capsule[0].Radius));
    }
    [Fact]
    public void ACapsuleAtThePieceCeilingSweepsItsWholeCore() {
        // A core 62.86 radii long, measured with the rotation margin, takes exactly the ceiling's pieces; a tenth of a
        // unit longer takes one more and is refused.
        var radius = FixedQ4816.FromDouble(value: 0.35);
        var core = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 22.0), Z: FixedQ4816.Zero);

        Assert.True(condition: FixedFieldContactSolver.CapsuleCoreFitsSweep(core: core, pieces: out var pieces, radius: radius));
        Assert.Equal(actual: pieces, expected: FixedFieldContactSolver.MaximumCapsuleSweepPieces);
        Assert.False(condition: FixedFieldContactSolver.CapsuleCoreFitsSweep(core: (core + new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 0.1), Z: FixedQ4816.Zero)), pieces: out _, radius: radius));

        // A thin bar at the core's middle, crossed edge-on: neither end comes near it, so every piece must sweep.
        var evaluator = Wall(halfExtents: new Vector3(x: 8f, y: 0.005f, z: WallHalfThickness), y: 11.35f);
        var work = new FixedContactSweepWork();
        var lower = new FixedVector3(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero);
        var capsule = new FixedBodyColliderVolume[] { new(Kind: FixedBodyColliderKind.Capsule, Center: lower, Endpoint: (lower + core), HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: radius) };
        var reached = Sweep(previous: Z(z: -95.0), solver: Solver(evaluator: evaluator, work: work), target: Z(z: -150.0), volumes: capsule);

        Assert.True(condition: (Read(value: reached.Z) > (WallZ + WallHalfThickness)), userMessage: $"the capsule's middle passed the bar to {Read(value: reached.Z)}");
        Assert.Equal(expected: ((long)FixedFieldContactSolver.MaximumCapsuleSweepPieces), actual: work.Read(kind: FixedContactSweepWork.Sweeps));
    }
    [Fact]
    public void ACapsuleAtTheLeastAdmittedRadiusSweepsItsWholeCore() {
        // Two raws, the least radius validation admits: its core spheres are one raw, and a hundred-raw core takes 51
        // of them. A bar four raws thick crosses the core's middle, fifty raws from either end sphere.
        var radius = FixedQ4816.FromRawBits(value: FixedFieldContactSolver.MinimumColliderRadiusRaws);
        var lower = new FixedVector3(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero);
        var capsule = new FixedBodyColliderVolume[] { new(Kind: FixedBodyColliderKind.Capsule, Center: lower, Endpoint: (lower + new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromRawBits(value: 100L), Z: FixedQ4816.Zero)), HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: radius) };

        Assert.True(condition: FixedFieldContactSolver.VolumeCoreSweeps(volume: in capsule[0]));
        Assert.False(condition: FixedFieldContactSolver.VolumeCoreSweeps(volume: capsule[0] with { Radius = FixedQ4816.FromRawBits(value: (FixedFieldContactSolver.MinimumColliderRadiusRaws - 1L)) }));

        var evaluator = Wall(halfExtents: new Vector3(x: 8f, y: (2f / 65536f), z: WallHalfThickness), y: (52f / 65536f));
        var work = new FixedContactSweepWork();
        var reached = Sweep(previous: Z(z: -95.0), solver: Solver(evaluator: evaluator, work: work), target: Z(z: -150.0), volumes: capsule);

        Assert.True(condition: (Read(value: reached.Z) > (WallZ + WallHalfThickness)), userMessage: $"the capsule's middle passed the bar to {Read(value: reached.Z)}");
        Assert.Equal(expected: 51L, actual: work.Read(kind: FixedContactSweepWork.Sweeps));
    }
    public static TheoryData<string> CarrierEndSteps() => ["a core past +2^47", "a core past -2^47", "a displacement across the carrier"];
    [MemberData(memberName: nameof(CarrierEndSteps))]
    [Theory]
    public void ALatticeStepWhoseArithmeticLeavesTheCarrierIsRefusedAndChangesNothing(string step) {
        // The lattice solid has no frame to refuse a far origin, so the solver's own rule must: a core start or a
        // displacement the carrier cannot hold once wrapped to a point no sweep proved, which a lattice answers. A core
        // one unit past either end wrapped to the far end, three quarters inside it, where the lattice proved the
        // whole step clear and the body took it; a step from -1.5·2⁶² raws to +1.5·2⁶² wrapped to one of 2⁶² raws
        // backward, which the sweep followed toward the carrier's end.
        var lattice = OneCellLattice();
        var solid = new FieldLatticeSolid(lattice: lattice);
        var work = new FixedContactSweepWork();
        var solver = new FixedFieldContactSolver(
            contactSkin: Skin,
            field: solid,
            gradientProbe: FixedQ4816.Zero,
            gradientUp: false,
            groundedThreshold: FixedQ4816.FromDouble(value: 0.5),
            maxIterations: 4,
            query: new SdfFieldEvaluator(program: new SdfProgramBuilder().Build()),
            sweep: new CertifiedFieldSweep(field: solid),
            sweepBoundsQueryBudget: FixedFieldContactSolver.DefaultSweepBoundsQueryBudget,
            sweepWork: work
        );
        var quarter = FixedQ4816.FromDouble(value: 0.25);
        var far = FixedQ4816.FromRawBits(value: (3L << 61));

        var (offset, previous, target) = step switch {
            "a core past +2^47" => (new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), new FixedVector3(X: FixedQ4816.Zero, Y: (FixedQ4816.MaxValue - quarter), Z: FixedQ4816.Zero), new FixedVector3(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - quarter), Z: FixedQ4816.Zero)),
            "a core past -2^47" => (new FixedVector3(X: -FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), new FixedVector3(X: (FixedQ4816.MinValue + quarter), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), new FixedVector3(X: (FixedQ4816.MinValue + quarter), Y: FixedQ4816.Zero, Z: FixedQ4816.One)),
            _ => (FixedVector3.Zero, new FixedVector3(X: -far, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), new FixedVector3(X: far, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)),
        };
        var body = new FixedBodyColliderVolume[] { new(Kind: FixedBodyColliderKind.Sphere, Center: offset, Endpoint: default, HalfExtents: default, Rotation: FixedQuaternion.Identity, Radius: Radius) };
        var velocity = Z(z: -3.0);
        var before = StateHash(lattice: lattice, position: previous, velocity: velocity);
        var position = target;
        var resolution = solver.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position, previousPosition: in previous, up: FixedVector3.UnitY, velocity: ref velocity, volumes: body);

        Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: resolution.Refusal);
        Assert.Equal(expected: before, actual: StateHash(lattice: lattice, position: position, velocity: velocity));
        Assert.Equal(expected: 0L, actual: work.Read(kind: FixedContactSweepWork.Sweeps));
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
    public void TheLatticeBoundsEncloseThePointAnswerForBoxesReachingTheCarriersEnds() {
        var solid = new FieldLatticeSolid(lattice: OneCellLattice());
        var half = FixedQ4816.FromDouble(value: 0.5);
        var inside = new FixedVector3(X: half, Y: half, Z: half);
        var least = FixedQ4816.MinValue;
        var most = FixedQ4816.MaxValue;
        // Each box holds the one column's centre, where the point query answers -0.5. The review's box starts at
        // x = -2³¹, whose column index less the reach wrapped an int to 2³¹ - 2, so the walk visited no column; the
        // carrier's ends also wrapped the gap between box and column, reading a column inside the box as far away.
        (FixedVector3 Lower, FixedVector3 Upper)[] boxes = [
            (new(X: FixedQ4816.FromInteger(value: int.MinValue), Y: half, Z: half), inside),
            (new(X: least, Y: half, Z: half), inside),
            (inside, new(X: most, Y: half, Z: half)),
            (new(X: half, Y: half, Z: least), new(X: half, Y: half, Z: most)),
            (new(X: half, Y: least, Z: half), new(X: half, Y: most, Z: half)),
            (new(X: least, Y: least, Z: least), new(X: most, Y: most, Z: most)),
        ];

        Assert.True(condition: solid.TryDistance(distance: out var centre, material: out _, position: FixedPosition.FromLocal(local: inside)));
        Assert.Equal(actual: centre, expected: -half);

        foreach (var (lower, upper) in boxes) {
            Assert.True(condition: solid.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: upper)), userMessage: $"[{lower}, {upper}] was refused");
            Assert.True(condition: bounds.Contains(value: centre), userMessage: $"[{lower}, {upper}]: the centre's answer {Read(value: centre)} lies outside {bounds}");

            // Every corner is a point of the box too, and the point query answers there.
            foreach (var corner in ((FixedVector3[])[lower, upper])) {
                Assert.True(condition: solid.TryDistance(distance: out var atCorner, material: out _, position: FixedPosition.FromLocal(local: corner)));
                Assert.True(condition: bounds.Contains(value: atCorner), userMessage: $"[{lower}, {upper}]: the corner {corner} answers {Read(value: atCorner)} outside {bounds}");
            }
        }
    }
    [Fact]
    public async Task TheLatticePointQueryAnswersAtTheLastColumnsAnIntIndexHolds() {
        var solid = new FieldLatticeSolid(lattice: OneCellLattice());
        // The point's column is 2³¹ - 3. Its neighbour limit, the column plus the reach, is int.MaxValue, so an int loop
        // over the neighbours never ended: every column index is at most int.MaxValue. No column lies within its reach,
        // so it answers the reach, two cells.
        var far = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: 2147483645.5), Y: FixedQ4816.FromDouble(value: 0.5), Z: FixedQ4816.FromDouble(value: 0.5)));
        var query = Task.Run(function: () => (solid.TryDistance(distance: out var distance, material: out _, position: far), distance), cancellationToken: TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(task1: query, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 30)));

        Assert.True(condition: ReferenceEquals(objA: finished, objB: query), userMessage: "the point query near int.MaxValue's column never finished");

        var (answered, answer) = await query;

        Assert.True(condition: answered);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 2), actual: answer);
        Assert.True(condition: solid.TryDistanceBounds(distance: out var bounds, lower: far, upper: far));
        Assert.True(condition: bounds.Contains(value: answer), userMessage: $"the answer {Read(value: answer)} lies outside {bounds}");
    }
    [Fact]
    public void TheLatticePointQueryAndItsBoundsShareOneArithmeticAtTheCarriersEnds() {
        // The review's witness (R1): a column whose top is the carrier's greatest value, queried from its least. The point
        // query once subtracted in wrapping Q48.16, so min.y - p.y wrapped to -131073 raws and p.y - max.y to one raw,
        // and it answered one raw, while the exact bounds answered the reach: two units that exclude it. Both are exact
        // now, and the column, 2⁶⁴ raws away, lies past the reach.
        var lattice = UnitLattice(cell: FixedQ4816.One, origin: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromRawBits(value: (long.MaxValue - 65536L)), Z: FixedQ4816.Zero));
        var solid = new FieldLatticeSolid(lattice: lattice);
        var half = FixedQ4816.FromDouble(value: 0.5);
        var point = FixedPosition.FromLocal(local: new FixedVector3(X: half, Y: FixedQ4816.MinValue, Z: half));

        Assert.True(condition: solid.TryDistance(distance: out var distance, material: out _, position: point));
        Assert.True(condition: solid.TryDistanceBounds(distance: out var bounds, lower: point, upper: point));
        Assert.True(condition: bounds.Contains(value: distance), userMessage: $"the point answer {distance.Value} raws lies outside {bounds}");
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 2), actual: distance);
    }
    [Fact]
    public void ALatticeWhoseReachTheCarrierCannotHoldIsRefusedByName() {
        // The review's witness (R2): a cell of 7·2⁴⁴ units, whose two-cell reach wraps to -2⁴⁵. The point query at the
        // column's centre answers -C/2, but every gap the bounds clamped to the wrapped reach went negative, and the
        // bounds read [R, R], which exclude it. The reach is the lattice's own constant, so a lattice whose reach the
        // carrier cannot hold is refused when its solid is built, never wrapped.
        var cell = FixedQ4816.FromInteger(value: (7L << 44));
        var lattice = UnitLattice(cell: cell, origin: FixedVector3.Zero);
        var refusal = Record.Exception(testCode: () => new FieldLatticeSolid(lattice: lattice));

        if (refusal is null) {
            var solid = new FieldLatticeSolid(lattice: lattice);
            var centre = FixedPosition.FromLocal(local: new FixedVector3(X: (cell / FixedQ4816.FromInteger(value: 2)), Y: FixedQ4816.Zero, Z: (cell / FixedQ4816.FromInteger(value: 2))));

            Assert.True(condition: solid.TryDistance(distance: out var distance, material: out _, position: centre));
            Assert.True(condition: solid.TryDistanceBounds(distance: out var bounds, lower: centre, upper: centre));
            Assert.True(condition: bounds.Contains(value: distance), userMessage: $"the point answer {distance} lies outside {bounds}");
            Assert.Fail(message: "a lattice whose reach the carrier cannot hold was admitted");
        }

        Assert.IsType<ArgumentOutOfRangeException>(@object: refusal);
        Assert.Contains(actualString: refusal.Message, expectedSubstring: "reach");
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
