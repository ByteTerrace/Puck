using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The composition half of <c>SdfProgram.AnalyzeLipschitz</c>: a chamfer blend's bevel plane is the one
/// <c>blendShape</c> arm whose Lipschitz bound can exceed BOTH operands, so the bound must fold per COMPOSITION —
/// <c>L = max(La, Lb, (La + Lb)/√2)</c> — not once per chain.
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
    // The shipped marcher's contract, on the CPU: step by the field's own value scaled by the program's baked
    // stepScale, accept inside the hit epsilon. Returns the y it accepted at, or null when the ray ran past the plate.
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

        var hit = MarchDown(
            evaluator: evaluator,
            stepScale: program.StepScale,
            startY: 6.0,
            stopY: -6.0
        );

        Assert.NotNull(@object: hit);
        Assert.Equal(
            expected: top,
            actual: hit!.Value,
            tolerance: 0.01
        );
        // The falsifier: the same march at the latched step scale steps straight THROUGH the plate. Without this the
        // law would pass against the defect it exists to refuse.
        Assert.Null(@object: MarchDown(
            evaluator: evaluator,
            stepScale: LatchedStepScale,
            startY: 6.0,
            stopY: -6.0
        ));
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
