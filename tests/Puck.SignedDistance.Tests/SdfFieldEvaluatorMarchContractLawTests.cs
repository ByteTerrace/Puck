using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The two contracts <see cref="SdfFieldEvaluator"/>'s march entry points carry beyond "walk the field": what a
/// non-converged march may assert, and which point a query actually evaluates.
/// </summary>
public sealed class SdfFieldEvaluatorMarchContractLawTests {
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
