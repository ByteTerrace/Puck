using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The program door's lane contract (<see cref="SdfProgram.LaneContractOf"/>) and the relief step bound it supports:
/// every lane the builder derives is recomputed and compared, every unused lane must be zero, every rotation or normal
/// must be unit, and a relief is charged at its own instruction through its chain's point factor
/// (docs/plans/terrain-pass/relief-mask-design.md, steps 1 and 2). The reviews' witnesses stand here as laws: a forged
/// scale correction (R5) and noise normalization (F6) are refused by name, a later shape no longer takes a relief's
/// charge (R2), a scaled relief is charged in field units (R8), and a relief after a seam-jumping fold is refused (F2).
/// </summary>
public sealed class SdfLaneContractLawTests {
    private static SdfProgramBuilder NewBuilder(out int material) {
        var builder = new SdfProgramBuilder();

        material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }
    // The same program with one instruction replaced, its side tables kept.
    private static SdfProgram With(SdfProgram program, int index, SdfInstruction replacement) => program.Excerpt(
        indices: [.. Enumerable.Range(start: 0, count: program.Instructions.Count)],
        rewrite: (source, instruction) => [((source == index) ? replacement : instruction)]
    );
    private static SdfInstruction Set(SdfInstruction instruction, SdfOperandLanes lane, Func<float, float> data, uint header) => lane switch {
        SdfOperandLanes.Shape => instruction with { Shape = header },
        SdfOperandLanes.Blend => instruction with { Blend = header },
        SdfOperandLanes.Material => instruction with { Material = header },
        SdfOperandLanes.Data0X => instruction with { Data0 = instruction.Data0 with { X = data(arg: instruction.Data0.X) } },
        SdfOperandLanes.Data0Y => instruction with { Data0 = instruction.Data0 with { Y = data(arg: instruction.Data0.Y) } },
        SdfOperandLanes.Data0Z => instruction with { Data0 = instruction.Data0 with { Z = data(arg: instruction.Data0.Z) } },
        SdfOperandLanes.Data0W => instruction with { Data0 = instruction.Data0 with { W = data(arg: instruction.Data0.W) } },
        SdfOperandLanes.Data1X => instruction with { Data1 = instruction.Data1 with { X = data(arg: instruction.Data1.X) } },
        SdfOperandLanes.Data1Y => instruction with { Data1 = instruction.Data1 with { Y = data(arg: instruction.Data1.Y) } },
        SdfOperandLanes.Data1Z => instruction with { Data1 = instruction.Data1 with { Z = data(arg: instruction.Data1.Z) } },
        SdfOperandLanes.Data1W => instruction with { Data1 = instruction.Data1 with { W = data(arg: instruction.Data1.W) } },
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(lane)),
    };
    private static IEnumerable<SdfOperandLanes> Single(SdfOperandLanes lanes) =>
        Enumerable.Range(start: 0, count: 11).Select(selector: static bit => ((SdfOperandLanes)(1 << bit))).Where(predicate: lane => ((lanes & lane) != 0));
    // The largest gradient magnitude the field shows by central differences of step h over the points, in field units
    // per unit; a point the evaluator answers nothing for is skipped.
    private static float SampledSlope(SdfProgram program, IEnumerable<Vector3> points, float h) {
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
        var largest = 0f;

        float? At(Vector3 point) => (field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))) ? ((float)((double)distance)) : null);

        foreach (var point in points) {
            var gradient = Vector3.Zero;
            var complete = true;

            for (var axis = 0; (axis < 3); axis++) {
                var step = Vector3.Zero;

                step[axis] = h;
                if ((At(point: (point + step)) is not { } ahead) || (At(point: (point - step)) is not { } behind)) {
                    complete = false;
                    break;
                }
                gradient[axis] = ((ahead - behind) / (2f * h));
            }
            if (complete) {
                largest = MathF.Max(x: largest, y: gradient.Length());
            }
        }

        return largest;
    }
    private static IEnumerable<Vector3> Box(Vector3 low, Vector3 high, int steps) {
        for (var i = 0; (i <= steps); i++) {
            for (var j = 0; (j <= steps); j++) {
                for (var k = 0; (k <= steps); k++) {
                    yield return (low + ((high - low) * (new Vector3(x: i, y: j, z: k) / steps)));
                }
            }
        }
    }

    // R5: the region (or any shape) under a scale whose packed correction is larger than its smallest axis is steeper
    // than the step analysis, which trusts that lane, assumes.
    [Fact]
    public void AForgedScaleCorrectionIsRefusedByName() {
        var program = NewBuilder(material: out var material).ResetPoint().Scale(scale: new Vector3(value: 2f)).Sphere(material: material, radius: 0.5f).Build();
        var scale = program.Instructions.Select(selector: static (instruction, index) => (instruction, index)).Single(predicate: static entry => (entry.instruction.Op == SdfOp.Scale));

        Assert.Equal(expected: SdfOperandLanes.Data0W, actual: SdfProgram.LaneContractOf(instruction: scale.instruction).Derived);

        var refusal = Assert.Throws<ArgumentException>(testCode: () => With(index: scale.index, program: program, replacement: (scale.instruction with { Data0 = scale.instruction.Data0 with { W = 32f } })));

        Assert.Contains(expectedSubstring: "Data0.w", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "distance correction", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
    // F6: a normalization larger than the octaves' derive lets the relief reach past its amplitude and outrun its bound.
    [Fact]
    public void AForgedNoiseNormalizationIsRefusedByName() {
        var program = NewBuilder(material: out var material).ResetPoint().PushField().ResetPoint().Sphere(material: material, radius: 1f)
            .ResetPoint().NoiseDisplace(amplitude: 1f, frequency: 1f, octaves: 1).PopField().Build();
        var relief = program.Instructions.Select(selector: static (instruction, index) => (instruction, index)).Single(predicate: static entry => (entry.instruction.Op == SdfOp.NoiseDisplace));

        Assert.Equal(expected: 1f, actual: relief.instruction.Data1.X);

        var refusal = Assert.Throws<ArgumentException>(testCode: () => With(index: relief.index, program: program, replacement: (relief.instruction with { Data1 = relief.instruction.Data1 with { X = 32f } })));

        Assert.Contains(expectedSubstring: "normalization", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
    // Every probe call's every instruction: each derived lane nudged by one ulp, each unused lane set to one, and each
    // unit vector doubled is refused, so no lane the contract names can be forged past the door.
    [Fact]
    public void EveryDerivedZeroAndUnitLaneTheProbeEmitsIsChecked() {
        var derived = 0;
        var zero = 0;
        var unit = 0;

        foreach (var call in SdfEncodingProbe.Calls()) {
            var program = SdfEncodingProbe.Build(call: call);

            for (var index = 0; (index < program.Instructions.Count); index++) {
                var instruction = program.Instructions[index];
                var contract = SdfProgram.LaneContractOf(instruction: instruction);

                foreach (var lane in Single(lanes: contract.Derived)) {
                    var forged = Set(data: static value => MathF.BitIncrement(x: value), header: 0u, instruction: instruction, lane: lane);

                    _ = Assert.ThrowsAny<ArgumentException>(testCode: () => With(index: index, program: program, replacement: forged));
                    derived++;
                }
                foreach (var lane in Single(lanes: contract.Zero)) {
                    var forged = Set(data: static _ => 1f, header: 1u, instruction: instruction, lane: lane);

                    _ = Assert.ThrowsAny<ArgumentException>(testCode: () => With(index: index, program: program, replacement: forged));
                    zero++;
                }
                if (contract.Unit != SdfOperandLanes.None) {
                    var forged = Single(lanes: contract.Unit).Aggregate(seed: instruction, func: static (current, lane) => Set(data: static value => (2f * value), header: 0u, instruction: current, lane: lane));

                    _ = Assert.ThrowsAny<ArgumentException>(testCode: () => With(index: index, program: program, replacement: forged));
                    unit++;
                }
            }
        }

        Assert.True(derived > 50, $"{derived} derived lanes");
        Assert.True(zero > 500, $"{zero} zero lanes");
        Assert.True(unit > 3, $"{unit} unit vectors");
    }
    // R2: five masked reliefs, then a later shape in the last relief's chain. Each relief is charged at its own
    // instruction, so the recorded bound covers all five, where the old chain product let the later shape carry one.
    [Fact]
    public void ALaterShapeNoLongerTakesAReliefsCharge() {
        var builder = NewBuilder(material: out var material).ResetPoint().Sphere(material: material, radius: 1f);

        var mask = new SdfReliefMask(band: SdfReliefBand.Of(start: 0f, width: 1f), regions: [SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: new Vector3(value: 0.5f), type: SdfSolidPrimitive.Sphere)]);

        for (var relief = 0; (relief < 5); relief++) {
            builder.ResetPoint().NoiseDisplace(amplitude: 0.25f, frequency: 0f, masks: [mask], octaves: 1, seed: 0u);
        }

        var program = builder.Translate(offset: new Vector3(x: 100f, y: 0f, z: 0f)).Sphere(material: material, radius: 1f).Build();
        var bound = (1f / program.StepScale);
        var sampled = SampledSlope(h: (1f / 512f), program: program, points: Box(high: new Vector3(x: 1.4f, y: 0.2f, z: 0.2f), low: new Vector3(x: 0.6f, y: -0.2f, z: -0.2f), steps: 16));

        // Each relief adds 0.25 · 1.5 of mask slope (its noise is constant at frequency zero), times the regions' stretch.
        Assert.Equal(expected: (1f + (5f * (0.375f * SdfReliefRegion.MaxStretch))), actual: bound, precision: 4);
        Assert.True(sampled > 1.375f, $"the witness reaches past the old bound of 1.375: sampled {sampled}");
        Assert.True(sampled <= bound, $"sampled {sampled} within the bound {bound}");
    }
    // R8: a relief stamped at a scale of 1/64 samples a point the scale stretches 64 times, so its slope in field units
    // is its amplitude times 64 times its noise's; the point factor charges exactly that.
    [Fact]
    public void AScaledReliefIsChargedInFieldUnits() {
        const float Scale = (1f / 64f);
        var program = NewBuilder(material: out var material)
            .ResetPoint().Scale(scale: new Vector3(value: Scale)).Sphere(material: material, radius: 0.5f)
            .ResetPoint().Scale(scale: new Vector3(value: Scale)).NoiseDisplace(amplitude: (0.25f * Scale), frequency: 1f, octaves: 1, seed: 11u)
            .Build();
        var bound = (1f / program.StepScale);
        var expected = SdfProgram.NoiseDisplaceStepFactor(amplitude: 0.25f, frequency: 1f, gain: 0.5f, lacunarity: 2f, octaves: 1);
        var sampled = SampledSlope(h: (1f / 2048f), program: program, points: Box(high: new Vector3(value: 0.012f), low: new Vector3(value: -0.012f), steps: 12));

        Assert.Equal(expected: (1f / expected), actual: program.StepScale);
        Assert.True(sampled > 1.4004f, $"the witness reaches past the old bound of 1.4004: sampled {sampled}");
        Assert.True(sampled <= bound, $"sampled {sampled} within the bound {bound}");
    }
    // F2, without its fold: a plane, then a unit sine relief sampled through a scale of 1/64, is charged 1 + 64.
    [Fact]
    public void AScaledSineReliefIsChargedThroughItsScale() {
        var program = NewBuilder(material: out var material)
            .ResetPoint().Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .ResetPoint().Scale(scale: new Vector3(value: (1f / 64f))).Displace(amplitude: 1f, frequency: Vector3.One)
            .Build();
        var bound = (1f / program.StepScale);
        var sampled = SampledSlope(h: (1f / 4096f), program: program, points: Box(high: new Vector3(x: 0.1f, y: 0.05f, z: 0.1f), low: new Vector3(x: 0f, y: -0.05f, z: 0f), steps: 14));

        Assert.Equal(expected: (1f / 65f), actual: program.StepScale);
        Assert.True(sampled > 2f, $"the witness reaches past the old bound of 2: sampled {sampled}");
        Assert.True(sampled <= (bound * 1.01f), $"sampled {sampled} within the bound {bound}");
    }
    // F2 with its fold, and the lead's ruling: no step bound covers the jump at a fold's seam, so a relief after one is
    // refused by name, as cellular relief is.
    [Theory]
    [InlineData(SdfOp.Displace)]
    [InlineData(SdfOp.NoiseDisplace)]
    [InlineData(SdfOp.CellDisplace)]
    public void AReliefAfterASeamJumpingFoldIsRefused(SdfOp relief) {
        var builder = NewBuilder(material: out var material)
            .ResetPoint().Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .ResetPoint().Scale(scale: new Vector3(value: (1f / 64f))).Repeat(spacing: new Vector3(value: 4f));

        _ = relief switch {
            SdfOp.Displace => builder.Displace(amplitude: 1f, frequency: Vector3.One),
            SdfOp.NoiseDisplace => builder.NoiseDisplace(amplitude: 1f, frequency: 1f),
            _ => builder.CellDisplace(amplitude: 0.1f, frequency: 1f, mode: SdfCellMode.F1, randomness: 0.2f, seed: 0u),
        };

        var refusal = Assert.Throws<ArgumentException>(testCode: builder.Build);

        Assert.Contains(expectedSubstring: $"{relief} at instruction", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Repeat", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
}
