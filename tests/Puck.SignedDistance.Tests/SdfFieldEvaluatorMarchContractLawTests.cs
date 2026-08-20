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
    public void OverlapRefusesAnUnrepresentableWorldPointRatherThanAnsweringIt() {
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

        // Neither answer is true of a point with no world coordinate, and BOTH change authoritative state: a false
        // "occupied" refuses a legal spawn, a false "clear" places a body nowhere. The verb refuses instead, on the
        // same parameter and with the same exception type BakedWorldQuery's own range guard raises.
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => evaluator.Overlap(
            center: outsideCarrier,
            radius: FixedQ4816.Zero
        ));

        Assert.Equal(
            expected: "center",
            actual: refusal.ParamName
        );

        // Control: a shape-free program still means an empty world, not an undecidable point in a populated one — the
        // two collapsed causes of a failed TryDistance are told apart, not folded together.
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
    public void SphereCastResolvesFromInsideTheStepScaleClearanceGap() {
        // The gap: raw clearance (f - r) is well outside HitEpsilon, but the SCALED advance (f*s - r) is NEGATIVE,
        // because scaling the field shrinks it below the radius it must still clear. Treating an advance that small as
        // a non-convergence lets the sweep report contact at Distance 0 — a body claiming it is touching a wall a
        // twentieth of a unit away, at the exact point it started from. The floored advance walks the gap instead.
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
        Assert.Equal(
            expected: WorldQueryConfidence.Exact,
            actual: hit.Confidence
        );

        // The measured answer: the sweep stops with its centre one radius above the floor, having actually travelled.
        Assert.Equal(
            expected: 0.049,
            actual: ((double)hit.Distance),
            tolerance: 0.005
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
