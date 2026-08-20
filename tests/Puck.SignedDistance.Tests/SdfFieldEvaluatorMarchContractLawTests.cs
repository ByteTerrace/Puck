using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The contracts <see cref="SdfFieldEvaluator"/>'s march entry points carry beyond "walk the field": what a
/// non-converged march may assert — which differs per verb, because the verbs assert different things — how far a
/// march always reaches before it may report non-convergence, and which point a query actually evaluates.
/// </summary>
public sealed class SdfFieldEvaluatorMarchContractLawTests {
    private static readonly FixedVector3 Down = new(
        X: FixedQ4816.Zero,
        Y: -FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 East = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );

    // A ground plane plus one sphere. The plane throttles every march step to the ray's own height above it, so a ray
    // grazing just above the plane spends its whole iteration budget before reaching anything.
    private static SdfFieldEvaluator BuildGrazingScene(float sphereX) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder
            .Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: sphereX,
            y: 0.25f,
            z: 0f
        ))
            .Sphere(
            radius: 0.25f,
            material: material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static SdfFieldEvaluator BuildUnitSphere() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(
            radius: 1f,
            material: material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedQ4816 Distance(SdfFieldEvaluator evaluator, FixedPosition position) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: position
        ));

        return distance;
    }
    // A position whose Y is an exact number of Q48.16 ticks — the fixed-point tie cases a double literal cannot name.
    private static FixedPosition FromRawY(long raw) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromRawBits(value: raw),
            Z: FixedQ4816.Zero
        ));
    private static FixedPosition Local(double x, double y, double z) =>
        FixedPosition.FromLocal(local: Vector(
            x: x,
            y: y,
            z: z
        ));
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

    [Fact]
    public void ExhaustedMarchReportsAnObstructionRatherThanAClearLine() {
        var evaluator = BuildGrazingScene(sphereX: 0.7f);
        var origin = Local(
            x: 0.0,
            y: 0.0011,
            z: 0.0
        );

        // There IS something on this ray: the field at the sphere's centre height along the grazing line reads inside.
        Assert.True(condition: (Distance(
            evaluator: evaluator,
            position: Local(
                x: 0.7,
                y: 0.0011,
                z: 0.0
            )
        ) < FixedQ4816.Zero));

        // The plane throttles each step to ~0.0011, so 512 iterations cover ~0.56 — short of the sphere at 0.68. The
        // march cannot decide, and an undecided march may not assert the half of the contract that changes state:
        // "clear".
        Assert.True(condition: evaluator.Raycast(
            dir: Vector(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 5L),
            origin: origin
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Bounded,
            actual: hit.Confidence
        );
        Assert.False(condition: evaluator.LineOfSight(
            from: origin,
            to: Local(
                x: 2.0,
                y: 0.0011,
                z: 0.0
            )
        ));
    }

    [Fact]
    public void ConvergedMarchesStillResolveExactly() {
        // A cast under the 0.512-unit exhaustion floor cannot reach the conservative branch: it must still answer with
        // a measured hit at the sphere's real entry point. Without this the law above would pass on a blanket "always
        // blocked".
        var near = BuildGrazingScene(sphereX: 0.32f);

        Assert.True(condition: near.Raycast(
            dir: Vector(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: 0.5),
            origin: Local(
                x: 0.0,
                y: 0.0011,
                z: 0.0
            )
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Exact,
            actual: hit.Confidence
        );
        Assert.Equal(
            expected: 0.2878,
            actual: ((double)hit.Distance),
            tolerance: 0.01
        );

        // And a LONG line through open space — far past the exhaustion floor — still reads clear, so the conservative
        // rule fires on non-convergence, never on distance.
        Assert.True(condition: near.LineOfSight(
            from: Local(
                x: 0.0,
                y: 2.0,
                z: 0.0
            ),
            to: Local(
                x: 4.0,
                y: 2.0,
                z: 0.0
            )
        ));
    }

    [Fact]
    public void ShapeFreeProgramMissesRatherThanReportingAnObstruction() {
        // "Nothing declared" is an answer, not a non-convergence: a program with no shape must not be folded into the
        // conservative branch, or an empty world would read as solid everywhere.
        var evaluator = new SdfFieldEvaluator(program: new SdfProgramBuilder().Build());

        Assert.False(condition: evaluator.TryDistance(
            distance: out _,
            material: out _,
            position: FixedPosition.Zero
        ));
        Assert.False(condition: evaluator.Raycast(
            dir: Vector(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out _,
            maxDist: FixedQ4816.FromInteger(value: 5L),
            origin: FixedPosition.Zero
        ));
    }

    [Fact]
    public void OverlapTreatsAnUnrepresentableWorldPointAsObstructed() {
        var outsideCarrier = new FixedPosition(
            cellX: long.MaxValue,
            cellY: 0L,
            cellZ: 0L,
            local: FixedVector3.Zero
        );
        var evaluator = BuildUnitSphere();

        Assert.False(condition: evaluator.TryDistance(
            distance: out _,
            material: out _,
            position: outsideCarrier
        ));

        Assert.True(condition: evaluator.Overlap(
            center: outsideCarrier,
            radius: FixedQ4816.Zero
        ));

        // Control: a shape-free program still means an empty world, not an undecidable point in a populated one.
        Assert.False(condition: new SdfFieldEvaluator(program: new SdfProgramBuilder().Build()).Overlap(
            center: outsideCarrier,
            radius: FixedQ4816.Zero
        ));
    }

    [Fact]
    public void QueryEvaluatesTheWholeHierarchicalPositionNotTheCellLocalOffset() {
        var evaluator = BuildUnitSphere();
        var cellSize = ((double)(1L << FixedPosition.CellSizeLog2));

        // One cell along X is 1,048,576 world units from a unit sphere at the origin. Reading only .Local aliases the
        // field with that period and answers "inside" for every cell.
        foreach (var cell in (long[])[1L, -3L,]) {
            var position = new FixedPosition(
                cellX: cell,
                cellY: 0L,
                cellZ: 0L,
                local: FixedVector3.Zero
            );

            Assert.Equal(
                expected: ((Math.Abs(value: cell) * cellSize) - 1.0),
                actual: ((double)Distance(
                    evaluator: evaluator,
                    position: position
                )),
                tolerance: 0.01
            );
            Assert.False(condition: evaluator.Overlap(
                center: position,
                radius: FixedQ4816.FromDouble(value: 0.5)
            ));
        }

        // Past the SDF_FAR_DISTANCE seed (1e9) the accumulator's own Union saturates, exactly as mapCore's does. That
        // is an UNDERestimate of a huge true distance, which only shortens a march step — never the "inside" the alias
        // produced.
        var distant = new FixedPosition(
            cellX: 1_000_000L,
            cellY: 0L,
            cellZ: 0L,
            local: FixedVector3.Zero
        );

        Assert.True(condition: (Distance(
            evaluator: evaluator,
            position: distant
        ) >= FixedQ4816.FromInteger(value: 1_000_000L)));
        Assert.False(condition: evaluator.Overlap(
            center: distant,
            radius: FixedQ4816.FromDouble(value: 0.5)
        ));

        // No caller has to construct a cell by hand to reach that: FromLocal carries one itself past half a cell.
        Assert.Equal(
            expected: (cellSize - 1.0),
            actual: ((double)Distance(
                evaluator: evaluator,
                position: Local(
                    x: cellSize,
                    y: 0.0,
                    z: 0.0
                )
            )),
            tolerance: 0.01
        );
    }

    [Fact]
    public void GroundHeightRefusesAnUnconvergedProbeRatherThanFabricatingTerrain() {
        // A single VERTICAL plane: there is no downward intersection anywhere in the column, at any depth. A probe
        // hugging it is throttled to its own distance from the wall, so the descent exhausts.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(
            normal: Vector3.UnitX,
            offset: 0f,
            material: material
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var probe = Local(
            x: 0.0011,
            y: 9.0,
            z: 0.0
        );

        // The march really does exhaust rather than miss — the cast verb, whose true half asserts an OBSTRUCTION, takes
        // the conservative branch on the same descent. Without this the refusal below would be indistinguishable from
        // an ordinary miss and would prove nothing.
        Assert.True(condition: evaluator.Raycast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 50L),
            origin: probe
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Bounded,
            actual: hit.Confidence
        );

        // The denial: the same descent read as GROUND — whose true half asserts a SURFACE — must refuse. Folding
        // exhaustion to "hit" here would hand a caller a Y from the middle of open air and ground a body on it.
        Assert.False(condition: evaluator.TryGroundHeight(
            groundY: out _,
            position: probe,
            probeDown: FixedQ4816.FromInteger(value: 50L),
            probeUp: FixedQ4816.FromDouble(value: 0.5)
        ));

        // The control: a real floor is still found, so the verb has not simply been turned off.
        var floorBuilder = new SdfProgramBuilder();
        var floorMaterial = floorBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = floorBuilder.Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: floorMaterial
        );

        Assert.True(condition: new SdfFieldEvaluator(program: floorBuilder.Build()).TryGroundHeight(
            groundY: out var groundY,
            position: Local(
                x: 0.0,
                y: 2.0,
                z: 0.0
            ),
            probeDown: FixedQ4816.FromInteger(value: 50L),
            probeUp: FixedQ4816.FromDouble(value: 0.5)
        ));
        Assert.Equal(
            expected: 0.0,
            actual: ((double)groundY),
            tolerance: 0.002
        );
    }

    [Fact]
    public void ExhaustionReachIsInvariantUnderTheStepScale() {
        // The step clamp shortens every advance, so a fixed iteration budget would shorten the distance a march covers
        // in proportion — turning casts that resolved into conservative obstructions purely because the program grew a
        // chamfer. The budget derives from the clamp so the reach stays put.
        foreach (var chamferCount in (int[])[0, 3,]) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Plane(
                normal: Vector3.UnitY,
                offset: 0f,
                material: material
            );

            // Chamfer-unioned slabs far below the grazing line: they move the analyzed step scale without putting any
            // geometry on the ray, so the two legs differ ONLY in the clamp.
            for (var index = 0; (index < chamferCount); index++) {
                _ = builder
                    .ResetPoint()
                    .Translate(offset: new Vector3(
                    x: 0f,
                    y: (-20f - (index * 0.03f)),
                    z: 0f
                ))
                    .Box(
                    halfExtents: new Vector3(
                        x: 10f,
                        y: 1f,
                        z: 10f
                    ),
                    round: 0f,
                    material: material,
                    blend: SdfBlendOp.ChamferUnion,
                    smooth: 0.4f
                );
            }

            var program = builder.Build();
            var evaluator = new SdfFieldEvaluator(program: program);
            var origin = Local(
                x: 0.0,
                y: 0.0011,
                z: 0.0
            );

            Assert.Equal(
                expected: (chamferCount == 0),
                actual: (program.StepScale == 1.0f)
            );

            // A half-unit grazing cast through open space resolves CLEAR on both legs. At a flat budget the clamped leg
            // covers only ~0.29 units and would answer "obstructed" here.
            Assert.False(condition: evaluator.Raycast(
                dir: East,
                hit: out _,
                maxDist: FixedQ4816.FromDouble(value: 0.5),
                origin: origin
            ));

            // The control that the reach is a BUDGET and not a scene with nothing to exhaust on: a long enough cast on
            // the same ray still runs out and takes the conservative branch.
            Assert.True(condition: evaluator.Raycast(
                dir: East,
                hit: out var far,
                maxDist: FixedQ4816.FromInteger(value: 50L),
                origin: origin
            ));
            Assert.Equal(
                expected: WorldQueryConfidence.Bounded,
                actual: far.Confidence
            );
        }
    }

    [Fact]
    public void SphereCastDoesNotAdvanceThroughTheStepScaleClearanceGap() {
        // The gap: raw clearance (f - r) is well outside HitEpsilon, but the SCALED advance (f*s - r) is NEGATIVE,
        // because scaling the field shrinks it below the radius it must still clear. Advancing from here would cross a
        // region the Lipschitz proof has not shown clear, so the sweep must report a bounded obstruction at the last
        // safely reached point rather than manufacture an Exact contact after walking through it.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // A floor to sweep onto, plus a 2:1 ellipsoid parked far below purely to move the analyzed step scale to 1/2
        // without putting geometry near the cast.
        _ = builder
            .Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: 0f,
            y: -100f,
            z: 0f
        ))
            .Ellipsoid(
            radii: new Vector3(
                x: 2f,
                y: 1f,
                z: 1f
            ),
            material: material
        );

        var program = builder.Build();

        Assert.Equal(
            expected: 0.5f,
            actual: program.StepScale
        );

        var evaluator = new SdfFieldEvaluator(program: program);
        var radius = FixedQ4816.FromDouble(value: 0.5);
        var origin = Local(
            x: 0.0,
            y: 0.55,
            z: 0.0
        );

        // The origin really is inside the gap: 0.55 clears the radius by 0.05 (fifty HitEpsilons), while 0.55*0.5
        // leaves the scaled advance at -0.225.
        Assert.Equal(
            expected: 0.55,
            actual: ((double)Distance(
                evaluator: evaluator,
                position: origin
            )),
            tolerance: 1.0e-4
        );

        Assert.True(condition: evaluator.SphereCast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 2L),
            origin: origin,
            radius: radius
        ));
        Assert.Equal(expected: WorldQueryConfidence.Bounded, actual: hit.Confidence);
        Assert.Equal(expected: FixedQ4816.Zero, actual: hit.Distance);

        // The control, and the reason the pin above is about the GAP rather than about sphere casts: the same sweep
        // started ABOVE the gap advances until the scaled bound stops clearing the radius, which for f = y and a
        // half-unit radius at step scale 1/2 is y = 1 — three units of travel, half a unit short of the true contact
        // at y = 0.5, on the conservative side. A marcher that exhausted at every origin would report zero here too.
        Assert.True(condition: evaluator.SphereCast(
            dir: Down,
            hit: out var clear,
            maxDist: FixedQ4816.FromInteger(value: 4L),
            origin: Local(
                x: 0.0,
                y: 4.0,
                z: 0.0
            ),
            radius: radius
        ));
        Assert.Equal(expected: WorldQueryConfidence.Bounded, actual: clear.Confidence);
        Assert.Equal(
            expected: 3.0,
            actual: ((double)clear.Distance),
            tolerance: 0.002
        );
    }

    [Fact]
    public void GroundIsFoundThroughALowStepScaleProgram() {
        // A descending probe's advance shrinks with its own clearance, so at a low step scale the last stretch before
        // the surface falls under the fixed-point progress floor. Treating that as a non-convergence makes the verb
        // answer "no ground" over EVERY column of such a program — a whole world nothing can stand on.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // A 20:1 disc: eccentricity IS the analyzed Lipschitz factor, so this one shape sets the step scale to 1/20.
        _ = builder.Ellipsoid(
            radii: new Vector3(
                x: 20f,
                y: 1f,
                z: 20f
            ),
            material: material
        );

        var program = builder.Build();

        Assert.Equal(
            expected: 0.05f,
            actual: program.StepScale
        );

        // On the polar axis the ellipsoid's approximate field is exact, so the top surface sits at Y = 1.
        Assert.True(condition: new SdfFieldEvaluator(program: program).TryGroundHeight(
            groundY: out var groundY,
            position: Local(
                x: 0.0,
                y: 2.0,
                z: 0.0
            ),
            probeDown: FixedQ4816.FromInteger(value: 50L),
            probeUp: FixedQ4816.FromDouble(value: 0.5)
        ));
        Assert.Equal(
            expected: 1.0,
            actual: ((double)groundY),
            tolerance: 0.01
        );

        // Past the crossing, which is the half of this the leg above cannot see. The accept arm tests the RAW field
        // against HitEpsilon (raw 66) while the advance is the SCALED field; below step scale 978/65536 (~0.0149) the
        // last advance the proof supports rounds away one tick ABOVE the accept band, and the descent stops on raw 67
        // with the surface already inside HitEpsilon + one tick. A 67:1 disc is the first eccentricity that reaches
        // it: 1/67 floors to raw 978, and floor(67 * 978 / 65536) is zero.
        var edgeProgram = new SdfProgramBuilder();
        var edgeMaterial = edgeProgram.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = edgeProgram.Ellipsoid(
            radii: new Vector3(
                x: 67f,
                y: 1f,
                z: 67f
            ),
            material: edgeMaterial
        );

        var edge = new SdfFieldEvaluator(program: edgeProgram.Build());

        Assert.True(condition: edge.TryGroundHeight(
            groundY: out var edgeGroundY,
            position: Local(
                x: 0.0,
                y: 2.0,
                z: 0.0
            ),
            probeDown: FixedQ4816.FromInteger(value: 50L),
            probeUp: FixedQ4816.FromDouble(value: 0.5)
        ));
        Assert.Equal(
            expected: 1.0,
            actual: ((double)edgeGroundY),
            tolerance: 0.01
        );

        // The control: the same disc probed over a column its rim does not reach still answers false, so neither leg
        // above is a verb that has been turned into a constant.
        Assert.False(condition: edge.TryGroundHeight(
            groundY: out _,
            position: Local(
                x: 200.0,
                y: 2.0,
                z: 0.0
            ),
            probeDown: FixedQ4816.FromInteger(value: 50L),
            probeUp: FixedQ4816.FromDouble(value: 0.5)
        ));
    }

    [Fact]
    public void AnUnrepresentableStepScaleNeverRoundsUpIntoAnUnsafeAdvance() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // 1e-5 sits in [half-ULP, ULP) of Q48.16, the ONE band where the two conversion policies disagree: nearest
        // rounds it to raw 1, a directed floor to raw 0. An eccentricity twice this large scales below half a ULP,
        // where both policies produce zero and no fixture can tell them apart.
        _ = builder.Ellipsoid(
            radii: new Vector3(
                x: 100_000f,
                y: 1f,
                z: 100_000f
            ),
            material: material
        );

        var program = builder.Build();

        Assert.InRange(
            actual: program.StepScale,
            low: (((float)((double)FixedQ4816.Epsilon)) * 0.5f),
            high: ((float)((double)FixedQ4816.Epsilon))
        );

        var evaluator = new SdfFieldEvaluator(program: program);
        // Twenty thousand units of clearance above the disc's pole. A scale rounded up to one raw tick authorizes an
        // advance of f/65536 per iteration — three hundred metres a step out here — so the whole budget marches
        // thousands of units on a proof that authorizes none. At the floored scale nothing is authorized at all and
        // the march can only creep at the format's own minimum, whose total is the point-cast reach.
        const double PointMarchReach = 0.515625; // 512 iterations * HitEpsilon, which quantizes to raw 66.

        var origin = Local(
            x: 0.0,
            y: 20_000.0,
            z: 0.0
        );

        Assert.True(condition: evaluator.Raycast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 20_000L),
            origin: origin
        ));
        Assert.Equal(expected: WorldQueryConfidence.Bounded, actual: hit.Confidence);
        Assert.InRange(
            actual: ((double)hit.Distance),
            high: PointMarchReach,
            low: 0.0
        );
        // The same claim on the verb with no march in it: with no authorized scaled clearance, twenty thousand units
        // of open sky reads as occupied rather than as a proof of separation.
        Assert.True(condition: evaluator.Overlap(
            center: origin,
            radius: FixedQ4816.Zero
        ));
    }

    [Fact]
    public void TheScaledClearanceFloorsRatherThanRoundingToTheNearestTick() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // A ground plane, whose field on the Y axis is Y to the raw bit, plus a 2:1 ellipsoid parked far below to set
        // the analyzed step scale to exactly 1/2 without putting geometry near the probes.
        _ = builder
            .Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: 0f,
            y: -100f,
            z: 0f
        ))
            .Ellipsoid(
            radii: new Vector3(
                x: 2f,
                y: 1f,
                z: 1f
            ),
            material: material
        );

        var program = builder.Build();

        Assert.Equal(
            expected: 0.5f,
            actual: program.StepScale
        );

        var evaluator = new SdfFieldEvaluator(program: program);
        var radius = FixedQ4816.FromRawBits(value: 3L);
        // An ODD number of raw ticks halved lands exactly between two ticks. FixedQ4816 multiplication resolves that
        // tie to even (7/2 -> 4), which rounds a Lipschitz LOWER bound UP: the scaled clearance would then clear a
        // three-tick radius and report open space where the proof shows none. The directed floor gives 3.
        var tie = FromRawY(raw: 7L);

        Assert.Equal(
            expected: FixedQ4816.FromRawBits(value: 7L),
            actual: Distance(
                evaluator: evaluator,
                position: tie
            )
        );
        Assert.True(condition: evaluator.Overlap(
            center: tie,
            radius: radius
        ));

        // The control: one more tick of field halves to exactly 4 with no tie to resolve, and four ticks of proven
        // clearance really is clear of a three-tick sphere. The verb is not wired to answer occupied.
        var exact = FromRawY(raw: 8L);

        Assert.Equal(
            expected: FixedQ4816.FromRawBits(value: 8L),
            actual: Distance(
                evaluator: evaluator,
                position: exact
            )
        );
        Assert.False(condition: evaluator.Overlap(
            center: exact,
            radius: radius
        ));
    }

    [Fact]
    public void InCellQueriesAreUnchangedByTheRebase() {
        // The control: rebasing against the world origin is the IDENTITY inside cell (0,0,0), which is every position
        // room- and arena-scale content produces. A law that only proved the cross-cell half could be satisfied by a
        // provider that answered wrong everywhere.
        var evaluator = BuildUnitSphere();

        Assert.Equal(
            expected: -1.0,
            actual: ((double)Distance(
                evaluator: evaluator,
                position: FixedPosition.Zero
            )),
            tolerance: 1.0e-4
        );
        Assert.True(condition: evaluator.Overlap(
            center: FixedPosition.Zero,
            radius: FixedQ4816.FromDouble(value: 0.5)
        ));

        // 524,287 is the last offset FromLocal keeps in cell 0; it read correctly before the rebase and still does.
        Assert.Equal(
            expected: 524_286.0,
            actual: ((double)Distance(
                evaluator: evaluator,
                position: Local(
                    x: 524_287.0,
                    y: 0.0,
                    z: 0.0
                )
            )),
            tolerance: 1.0
        );
    }
}
