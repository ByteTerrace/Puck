using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The composition half of <c>SdfProgram.AnalyzeLipschitz</c>, and the CPU marcher that has to consume it. A chamfer
/// blend's bevel plane is the one <c>blendShape</c> arm whose Lipschitz bound can exceed BOTH operands, so the bound
/// must fold per COMPOSITION — <c>L = max(La, Lb, (La + Lb)/√2)</c> — not once per chain; and
/// <see cref="SdfFieldEvaluator"/>'s casts must advance by the analyzed <c>StepScale</c>, since a chamfer rides the
/// blend tail where the interpreted op subset's 1-Lipschitz guarantee does not reach.
/// </summary>
public sealed class SdfLipschitzCompositionLawTests {
    // Three parallel slabs, chamfer-unioned and then carved by a plane, leaving a thin top plate. Distinct (not
    // coincident) centres, so the geometry is ordinary stacked-panel authoring rather than a degenerate duplicate.
    private static readonly Vector3[] SlabCenters = [
        new(x: 0f, y: 0f, z: 0f),
        new(x: 0f, y: -0.03f, z: 0f),
        new(x: 0f, y: -0.06f, z: 0f),
    ];
    private static readonly Vector3 SlabHalfExtents = new(x: 10f, y: 1f, z: 10f);

    private static readonly FixedVector3 Down = new(
        X: FixedQ4816.Zero,
        Y: -FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    private const float PlateBevel = 0.4f;
    // The step scale AnalyzeLipschitz produced while the chamfer factor was a per-chain latch: one √2 however many
    // chamfer compositions the chain held. Kept here ONLY as the falsifier — each march law below runs at it and
    // requires the opposite outcome, which is what proves the law can fail.
    private const float LatchedStepScale = (1.0f / 1.41421356f);

    private static SdfProgram BuildChamferStack(int slabCount, float plateBottomY) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var index = 0; (index < slabCount); index++) {
            _ = builder
                .ResetPoint()
                .Translate(offset: SlabCenters[index])
                .Box(
                halfExtents: SlabHalfExtents,
                round: 0f,
                material: material,
                blend: SdfBlendOp.ChamferUnion,
                smooth: PlateBevel
            );
        }

        _ = builder
            .ResetPoint()
            .Plane(
            normal: Vector3.UnitY,
            offset: -plateBottomY,
            material: material,
            blend: SdfBlendOp.Subtraction
        );

        return builder.Build();
    }
    private static FixedQ4816 FieldAt(SdfFieldEvaluator evaluator, double y) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: Position(y: y)
        ));

        return distance;
    }
    // A reference march OUTSIDE the evaluator, parameterized by the step scale the shipped marcher is not free to
    // choose: it exists to show what the same field does when advanced by its RAW value. Returns the y it accepted at,
    // or null when the ray ran past the plate.
    private static double? MarchDown(SdfFieldEvaluator evaluator, float stepScale, double startY, double stopY) {
        var y = startY;

        for (var step = 0; (step < 4096); step++) {
            var clearance = (((double)FieldAt(
                evaluator: evaluator,
                y: y
            )) * stepScale);

            if (clearance < 0.001) {
                return y;
            }

            y -= clearance;

            if (y < stopY) {
                return null;
            }
        }

        return null;
    }
    private static FixedPosition Position(double y) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.Zero
        ));
    // The composite top surface, found by a dense scan of the same field the march reads — the independent ground
    // truth the march's answer is checked against.
    private static double ScanTopSurface(SdfFieldEvaluator evaluator, double startY, double stopY) {
        for (var y = startY; (y > stopY); y -= 0.0001) {
            if (FieldAt(
                evaluator: evaluator,
                y: y
            ) <= FixedQ4816.Zero) {
                return y;
            }
        }

        return double.NaN;
    }

    [Fact]
    public void ChamferFreeProgramKeepsTheUnitStepScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var index = 0; (index < SlabCenters.Length); index++) {
            _ = builder
                .ResetPoint()
                .Translate(offset: SlabCenters[index])
                .Box(
                halfExtents: SlabHalfExtents,
                round: 0f,
                material: material
            );
        }

        // Byte-identity: an isometric, chamfer-free program is exactly 1-Lipschitz and must bake 1.0f to the bit, so
        // every shipped world's rendered pixels are untouched by the composition fold.
        Assert.Equal(
            expected: 1.0f,
            actual: builder.Build().StepScale
        );
    }

    [Fact]
    public void ChamferStepScaleGrowsPerCompositionFromTheThirdChamfer() {
        // One chamfer composes against the SDF_FAR_DISTANCE constant (L = 0), so it is the identity: max(0, 1, 1/√2) = 1.
        Assert.Equal(
            expected: 1.0f,
            actual: BuildChamferStack(
                slabCount: 1,
                plateBottomY: -100f
            ).StepScale
        );
        // Two chamfers reach exactly √2 — the value the retired per-chain latch reported — so shallow-chamfer content
        // keeps its step scale to the bit.
        Assert.Equal(
            expected: LatchedStepScale,
            actual: BuildChamferStack(
                slabCount: 2,
                plateBottomY: -100f
            ).StepScale
        );

        // Three reach 1 + 1/√2 = 1.70711, which the latch cannot express: this is the composition the latch drops.
        var third = BuildChamferStack(
            slabCount: 3,
            plateBottomY: -100f
        ).StepScale;

        Assert.Equal(
            expected: (1.0f / 1.7071067f),
            actual: third,
            tolerance: 1.0e-6f
        );
        Assert.True(condition: (third < LatchedStepScale));
    }

    [Fact]
    public void ChamferStackedPlateIsMarchableAtTheAnalyzedStepScale() {
        var uncarved = new SdfFieldEvaluator(program: BuildChamferStack(
            slabCount: 3,
            plateBottomY: -100f
        ));
        var top = ScanTopSurface(
            evaluator: uncarved,
            startY: 3.0,
            stopY: 0.0
        );

        Assert.False(condition: double.IsNaN(d: top));

        // The chamfer stack's field OVERESTIMATES true distance by the recurrence's own factor: a probe 0.2 above the
        // surface reads ~1.70711x that. This is the quantity the step scale must cancel.
        Assert.Equal(
            expected: (0.2 * 1.70711),
            actual: ((double)FieldAt(
                evaluator: uncarved,
                y: (top + 0.2)
            )),
            tolerance: 0.002
        );

        var plateBottom = (top - 0.016);
        var program = BuildChamferStack(
            slabCount: 3,
            plateBottomY: ((float)plateBottom)
        );
        var evaluator = new SdfFieldEvaluator(program: program);

        // The plate is real and 0.016 thick: solid just under the top, empty just under the bottom.
        Assert.True(condition: (FieldAt(
            evaluator: evaluator,
            y: (top - 0.002)
        ) < FixedQ4816.Zero));
        Assert.True(condition: (FieldAt(
            evaluator: evaluator,
            y: (plateBottom - 0.002)
        ) > FixedQ4816.Zero));

        // The PUBLIC verb, which is what a contact/ground/visibility consumer actually calls: the cast lands ON the
        // plate and reports a MEASURED hit, not the conservative non-convergence branch.
        Assert.True(condition: evaluator.Raycast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 12L),
            origin: Position(y: 6.0)
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Exact,
            actual: hit.Confidence
        );
        Assert.True(condition: hit.Point.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        ));
        Assert.Equal(
            expected: top,
            actual: ((double)world.Y),
            tolerance: 0.01
        );

        // The falsifiers, each an advance the evaluator must NOT take. Raw: the field's own value, which the WHOLE
        // interpreted op subset being 1-Lipschitz does not license, because the chamfer lives in the blend tail. Latch:
        // one √2 for the whole chain, the per-chain fold this recurrence replaced. Both step through the plate.
        Assert.Null(@object: MarchDown(
            evaluator: evaluator,
            stepScale: 1.0f,
            startY: 6.0,
            stopY: -6.0
        ));
        Assert.Null(@object: MarchDown(
            evaluator: evaluator,
            stepScale: LatchedStepScale,
            startY: 6.0,
            stopY: -6.0
        ));
        // The control: the reference march is not simply broken — at the analyzed scale it finds the same plate the
        // public cast did.
        Assert.Equal(
            expected: top,
            actual: MarchDown(
                evaluator: evaluator,
                stepScale: program.StepScale,
                startY: 6.0,
                stopY: -6.0
            )!.Value,
            tolerance: 0.01
        );
    }

    /// <summary>A radius must be subtracted after the field is scaled. Once that lower bound can no longer prove the
    /// sphere is separated, authoritative queries must resolve toward obstruction instead of continuing to a raw-field
    /// threshold that lies inside the true contact envelope.</summary>
    [Fact]
    public void ChamferedFieldUsesTheStepScaleForSphereQueries() {
        var program = BuildChamferStack(
            slabCount: 3,
            plateBottomY: -100f
        );
        var evaluator = new SdfFieldEvaluator(program: program);
        var top = ScanTopSurface(
            evaluator: evaluator,
            startY: 3.0,
            stopY: 0.0
        );
        var radius = FixedQ4816.FromDouble(value: 0.1);
        var overlapCenterY = (top + 0.09);
        var rawDistance = FieldAt(
            evaluator: evaluator,
            y: overlapCenterY
        );

        Assert.True(condition: (rawDistance > radius));
        Assert.True(condition: ((rawDistance * FixedQ4816.FromDouble(value: program.StepScale)) <= radius));
        Assert.True(condition: evaluator.Overlap(
            center: Position(y: overlapCenterY),
            radius: radius
        ));

        var trueContactTravel = (6.0 - (top + 0.1));

        Assert.True(condition: evaluator.SphereCast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: (trueContactTravel + 0.002)),
            origin: Position(y: 6.0),
            radius: radius
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Bounded,
            actual: hit.Confidence
        );
        Assert.True(condition: (((double)hit.Distance) <= (trueContactTravel + 0.001)));
    }

    [Fact]
    public void ChamferFreeCastIsUnchangedByTheStepClamp() {
        // The other control: a warp-free program bakes stepScale 1.0f, so its cast advances by the raw field exactly as
        // it did before the clamp existed — a downward ray from y = 6 onto a box whose top is y = 1 travels 5.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Box(
            halfExtents: SlabHalfExtents,
            round: 0f,
            material: material
        );

        Assert.True(condition: new SdfFieldEvaluator(program: builder.Build()).Raycast(
            dir: Down,
            hit: out var hit,
            maxDist: FixedQ4816.FromInteger(value: 12L),
            origin: Position(y: 6.0)
        ));
        Assert.Equal(
            expected: WorldQueryConfidence.Exact,
            actual: hit.Confidence
        );
        Assert.Equal(
            expected: 5.0,
            actual: ((double)hit.Distance),
            tolerance: 0.002
        );
    }

    [Fact]
    public void ChamferPopFieldComposesThroughTheSameRecurrence() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // Two chamfer-unioned parent shapes (L = √2 by the recurrence), then a scope chamfer-composed back in. A pop is
        // a composition like any other: max(√2, 1, (√2 + 1)/√2) = 1.70711.
        _ = builder
            .Sphere(
            radius: 1f,
            material: material,
            blend: SdfBlendOp.ChamferUnion,
            smooth: 0.2f
        )
            .ResetPoint()
            .Translate(offset: new Vector3(
            x: 1f,
            y: 0f,
            z: 0f
        ))
            .Sphere(
            radius: 1f,
            material: material,
            blend: SdfBlendOp.ChamferUnion,
            smooth: 0.2f
        )
            .ResetPoint()
            .PushField(
            compose: SdfBlendOp.ChamferUnion,
            smooth: 0.2f
        )
            .Translate(offset: new Vector3(
            x: 2f,
            y: 0f,
            z: 0f
        ))
            .Sphere(
            radius: 1f,
            material: material
        )
            .PopField();

        Assert.Equal(
            expected: (1.0f / 1.7071067f),
            actual: builder.Build().StepScale,
            tolerance: 1.0e-6f
        );
    }
}
