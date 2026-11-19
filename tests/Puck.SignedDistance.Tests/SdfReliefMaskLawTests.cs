using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The row form of a relief mask (<see cref="SdfReliefMask"/>, <see cref="SdfOp.ReliefRow"/>), read through the program's
/// door and the fixed-point evaluator: outside every band the masked field is the unmasked field bit for bit; on and
/// inside a region of inside weight zero the relief is exactly zero; removing the rows leaves every analysis output equal
/// and moves the step bound by exactly the masks' charge; and the door refuses each malformed run by name.
/// </summary>
public sealed class SdfReliefMaskLawTests {
    private const float Amplitude = 0.3f;
    private const float Frequency = 1.5f;
    private const float Fade = 0.6f;
    private static readonly Vector3 Bowl = new(x: 0f, y: 1.1f, z: 0f);
    private const float BowlRadius = 0.5f;

    private static SdfReliefMask BowlMask(float scale = 1f) => new(
        band: SdfReliefBand.Of(start: 0f, width: Fade),
        regions: [SdfReliefRegion.Lower(position: (Bowl / scale), rotation: Quaternion.Identity, scale: new Vector3(value: (BowlRadius / scale)), type: SdfSolidPrimitive.Sphere)]
    );
    // A slab with a bowl pressed into its top, relieved, in one scope; `mask` gives the relief the bowl's region with inside
    // weight zero, `relief` leaves the relief out. `scale` stamps the creation at that scale, as CreationStampEmitter does.
    private static SdfProgram Slab(bool mask, bool relief = true, float scale = 1f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().PushField()
            .ResetPoint().Box(halfExtents: new Vector3(x: 2f, y: 0.5f, z: 2f), material: material, round: 0f)
            .ResetPoint().Translate(offset: Bowl).Sphere(blend: SdfBlendOp.Subtraction, material: material, radius: BowlRadius);
        if (relief) {
            builder.ResetPoint().Scale(scale: new Vector3(value: scale)).NoiseDisplace(amplitude: (Amplitude * scale), frequency: Frequency, masks: (mask ? [BowlMask(scale: scale)] : null), octaves: 3);
        }

        return builder.PopField().Build();
    }
    private static FixedQ4816 Distance(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));

        return distance;
    }
    private static IEnumerable<Vector3> Lattice(float step) {
        for (var x = -2.5f; (x <= 2.5f); x += step) {
            for (var y = -0.8f; (y <= 1.8f); y += step) {
                for (var z = -2.5f; (z <= 2.5f); z += step) {
                    yield return new Vector3(x: x, y: y, z: z);
                }
            }
        }
    }
    // The same program with its instructions rewritten (an excerpt of all of them).
    private static SdfProgram Rewritten(SdfProgram program, Func<int, SdfInstruction, IEnumerable<SdfInstruction>> rewrite) =>
        program.Excerpt(indices: [.. Enumerable.Range(start: 0, count: program.Instructions.Count)], rewrite: rewrite);
    private static int ReliefIndex(SdfProgram program) => program.Instructions.ToList().FindIndex(match: static instruction => (instruction.Op == SdfOp.NoiseDisplace));

    [Fact]
    public void OutsideEveryBandTheMaskedFieldIsTheUnmaskedFieldBitForBit() {
        var masked = new SdfFieldEvaluator(program: Slab(mask: true));
        var unmasked = new SdfFieldEvaluator(program: Slab(mask: false));
        var compared = 0;

        foreach (var point in Lattice(step: 0.23f)) {
            // A margin past the band covers the region's fixed-point rounding.
            if ((Vector3.Distance(value1: point, value2: Bowl) - BowlRadius) <= (Fade + 0.05f)) { continue; }

            Assert.Equal(expected: Distance(field: unmasked, point: point), actual: Distance(field: masked, point: point));
            compared++;
        }

        Assert.True(compared > 1000);
    }

    [Fact]
    public void OnAndInsideARegionOfInsideWeightZeroTheReliefIsExactlyZero() {
        var masked = new SdfFieldEvaluator(program: Slab(mask: true));
        var unrelieved = new SdfFieldEvaluator(program: Slab(mask: false, relief: false));
        var compared = 0;

        foreach (var point in Lattice(step: 0.07f)) {
            // Strictly inside the region, past its fixed-point rounding.
            if ((Vector3.Distance(value1: point, value2: Bowl) - BowlRadius) > -0.01f) { continue; }

            Assert.Equal(expected: Distance(field: unrelieved, point: point), actual: Distance(field: masked, point: point));
            compared++;
        }

        Assert.True(compared > 100);
    }

    // The twin check: removing the rows and zeroing the relief's count leaves every analysis output equal, and moves the
    // relief's scope clamp by exactly the amplitude times the masks' slope times the point factor.
    [Theory]
    [InlineData(1f)]
    [InlineData(0.25f)]
    public void RemovingTheRowsChangesOnlyTheStepBoundByTheMasksCharge(float scale) {
        var masked = Slab(mask: true, scale: scale);
        var relief = ReliefIndex(program: masked);
        var rows = ((int)masked.Instructions[relief].Material);
        var twin = Rewritten(program: masked, rewrite: (index, instruction) => ((instruction.Op == SdfOp.ReliefRow)
            ? []
            : [((index == relief) ? (instruction with { Material = 0u }) : instruction)]));

        Assert.Equal(expected: 3, actual: rows);
        Assert.Equal(expected: (masked.Instructions.Count - rows), actual: twin.Instructions.Count);
        Assert.Equal(expected: Slab(mask: false, scale: scale).Instructions, actual: twin.Instructions);
        Assert.Equal(expected: twin.SkipSegmentCount, actual: masked.SkipSegmentCount);
        Assert.Equal(expected: twin.StepScale, actual: masked.StepScale);
        Assert.Equal(expected: twin.StepScaleBinder, actual: masked.StepScaleBinder);
        Assert.Equal(expected: twin.RequiredDynamicTransformCapacity, actual: masked.RequiredDynamicTransformCapacity);

        var bound = (1f / Assert.Single(collection: masked.FieldScopeClamps).StepScale);
        var twinBound = (1f / Assert.Single(collection: twin.FieldScopeClamps).StepScale);
        var charge = (((Amplitude * scale) * SdfReliefMask.SlopeOf(masks: [BowlMask(scale: scale)])) / scale);

        Assert.Equal(expected: (twinBound + charge), actual: bound, precision: 4);
    }

    [Fact]
    public void AnUnscopedReliefBindsTheStepScaleAndIsNamed() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.ResetPoint().Sphere(material: material, radius: 1f).NoiseDisplace(amplitude: 0.5f, frequency: 1f, masks: [BowlMask()]).Build();
        Assert.NotNull(@object: program.StepScaleBinder);

        var binder = program.StepScaleBinder.Value;

        Assert.True(program.StepScale < 1f);
        Assert.Equal(expected: SdfOp.NoiseDisplace, actual: binder.Op);
        Assert.Null(binder.Shape);
        Assert.Equal(expected: (1f / binder.Factor), actual: program.StepScale, precision: 6);
    }

    [Fact]
    public void AnExcerptCarriesAReliefsRowsAndRefusesARowNamedAlone() {
        var program = Slab(mask: true);
        var relief = ReliefIndex(program: program);
        var rows = ((int)program.Instructions[relief].Material);
        var all = Enumerable.Range(start: 0, count: program.Instructions.Count).ToArray();
        var withoutRows = all.Where(predicate: index => ((index < (relief - rows)) || (index >= relief))).ToArray();
        var carried = program.Excerpt(indices: withoutRows);
        var point = new Vector3(x: 0.4f, y: 0.9f, z: 0.3f);

        Assert.Equal(expected: program.Instructions, actual: carried.Instructions);
        Assert.Equal(expected: Distance(field: new SdfFieldEvaluator(program: program), point: point), actual: Distance(field: new SdfFieldEvaluator(program: carried), point: point));
        Assert.Throws<ArgumentException>(testCode: () => program.Excerpt(indices: [.. all.Where(predicate: index => (index != relief))]));
        Assert.Throws<ArgumentException>(testCode: () => Rewritten(program: program, rewrite: (index, instruction) => ((index == (relief - 1)) ? [] : [instruction])));
    }

    [Fact]
    public void TheDoorRefusesEachMalformedRunByName() {
        var program = Slab(mask: true);
        var relief = ReliefIndex(program: program);
        var band = (relief - 3);
        var pose = (relief - 2);
        var primitive = (relief - 1);
        var instructions = program.Instructions;
        var box = program.Instructions.ToList().FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend));

        void Refused(string because, Func<int, SdfInstruction, IEnumerable<SdfInstruction>> rewrite) {
            var exception = Assert.ThrowsAny<ArgumentException>(testCode: () => Rewritten(program: program, rewrite: rewrite));

            Assert.Contains(expectedSubstring: because, actualString: exception.Message);
        }

        // A row with no relief after its run, and a count that is not the run's length.
        Refused(because: "followed by instruction", rewrite: (index, instruction) => ((index == relief) ? [instruction, instructions[primitive]] : [instruction]));
        Refused(because: "counts", rewrite: (index, instruction) => [((index == relief) ? (instruction with { Material = 2u }) : instruction)]);
        // A run out of order: a pose before its band.
        Refused(because: "out of place", rewrite: (index, instruction) => [((index == band) ? instructions[pose] : ((index == pose) ? instructions[band] : instruction))]);
        // A type off the list, a band off the grid and past its limit, a turn of length two and a plane normal of 32.
        Refused(because: "a region's primitive", rewrite: (index, instruction) => [((index == primitive) ? (instruction with { Shape = ((uint)SdfShapeType.Torus) }) : instruction)]);
        Refused(because: "2^-12", rewrite: (index, instruction) => [((index == band) ? (instruction with { Data0 = instruction.Data0 with { Y = 0.1f } }) : instruction)]);
        Refused(because: "4096", rewrite: (index, instruction) => [((index == band) ? (instruction with { Data0 = instruction.Data0 with { X = 5000f } }) : instruction)]);
        Refused(because: "region pose quaternion", rewrite: (index, instruction) => [((index == pose) ? (instruction with { Data1 = new Vector4(x: 0f, y: 0f, z: 0f, w: 2f) }) : instruction)]);
        Refused(because: "plane normal", rewrite: (index, instruction) => [((index == primitive) ? (instruction with { Data0 = new Vector4(x: 0f, y: 32f, z: 0f, w: 0f), Data1 = Vector4.Zero, Shape = ((uint)SdfShapeType.Plane) }) : instruction)]);
        // A shape's finite capsule endpoint whose squared length overflows float, and a row magnitude Q48.16 would saturate.
        Refused(because: "finite squared length", rewrite: (index, instruction) => [((index == box) ? (instruction with { Data0 = new Vector4(x: 1e20f, y: 0f, z: 0f, w: 1f), Data1 = Vector4.Zero, Shape = ((uint)SdfShapeType.Capsule) }) : instruction)]);
        Refused(because: "magnitude of at most", rewrite: (index, instruction) => [((index == pose) ? (instruction with { Data0 = new Vector4(x: 1e14f, y: 0f, z: 0f, w: 0f) }) : instruction)]);
    }

    [Fact]
    public void ARunSplitByAnInstanceBoundaryIsRefused() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.ResetPoint().Sphere(material: material, radius: 1f).NoiseDisplace(amplitude: 0.1f, frequency: 1f, masks: [BowlMask()]).Build();
        var instructions = program.Instructions.ToArray();
        var relief = Array.FindIndex(array: instructions, match: static instruction => (instruction.Op == SdfOp.NoiseDisplace));

        // The relief alone in an instance, its rows outside it.
        var exception = Assert.ThrowsAny<ArgumentException>(testCode: () => SdfProgram.Of(appearance: program.Appearance, instances: [new SdfInstanceRange(Center: Vector3.Zero, End: (relief + 1), First: relief, IsDynamic: false, Radius: SdfBoundAlgebra.Unbounded, Slot: 0)], instructions: instructions));

        Assert.Contains(expectedSubstring: "instruction owner", actualString: exception.Message);
    }

    [Fact]
    public void MoreThanTheCapsOfMasksOrRegionsIsRefused() {
        var region = SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: Vector3.One, type: SdfSolidPrimitive.Box);
        var band = SdfReliefBand.Of(start: 0f, width: 1f);

        Assert.Throws<ArgumentException>(testCode: () => SdfReliefMask.RowCount(masks: [.. Enumerable.Repeat(element: new SdfReliefMask(band: band, regions: [region]), count: (SdfReliefMask.MaxMasks + 1))]));
        Assert.Throws<ArgumentException>(testCode: () => SdfReliefMask.RowCount(masks: [new SdfReliefMask(band: band, regions: [.. Enumerable.Repeat(element: region, count: 16)]), new SdfReliefMask(band: band, regions: [.. Enumerable.Repeat(element: region, count: 17)])]));
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Box, SdfShapeType.Box)]
    [InlineData(SdfSolidPrimitive.Sphere, SdfShapeType.Superellipsoid)]
    [InlineData(SdfSolidPrimitive.Ellipsoid, SdfShapeType.Superellipsoid)]
    [InlineData(SdfSolidPrimitive.Superellipsoid, SdfShapeType.Superellipsoid)]
    [InlineData(SdfSolidPrimitive.Capsule, SdfShapeType.Capsule)]
    [InlineData(SdfSolidPrimitive.Plane, SdfShapeType.Plane)]
    public void EachListedTypeLowersToOneShapeAtAUniformAndANonUniformScale(SdfSolidPrimitive type, SdfShapeType packed) {
        foreach (var scale in (Vector3[])[new Vector3(value: 0.7f), new Vector3(x: 0.7f, y: 1.3f, z: 0.7f)]) {
            Assert.Equal(expected: packed, actual: SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: scale, type: type).Shape);
        }
    }

    [Fact]
    public void ARegionOffTheListOrWithNoNativeArmIsRefused() {
        Assert.Throws<ArgumentException>(testCode: () => SdfReliefRegion.Lower(chamfer: 0.1f, position: Vector3.Zero, rotation: Quaternion.Identity, scale: Vector3.One, type: SdfSolidPrimitive.Box));
        Assert.Throws<ArgumentException>(testCode: () => SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: new Vector3(x: 1f, y: 2f, z: 3f), type: SdfSolidPrimitive.Capsule));
        Assert.Throws<ArgumentException>(testCode: () => SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: Vector3.One, type: SdfSolidPrimitive.Torus));
        Assert.Throws<ArgumentException>(testCode: () => SdfReliefRegion.Lower(position: Vector3.Zero, rotation: Quaternion.Identity, scale: Vector3.One, type: SdfSolidPrimitive.Cylinder));
    }

    // R1: a fold before a masked relief is refused, as before an unmasked one.
    [Fact]
    public void AFoldBeforeAMaskedReliefIsRefused() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().PushField().ResetPoint().Sphere(material: material, radius: 1f)
            .ResetPoint().Repeat(spacing: new Vector3(value: 4f)).NoiseDisplace(amplitude: Amplitude, frequency: Frequency, masks: [BowlMask()])
            .PopField();

        Assert.Throws<ArgumentException>(testCode: () => builder.Build());
    }

    // R7: a dilated sphere far from its region reads the same sample with its mask and without.
    [Fact]
    public void ADilatedSphereReadsTheSameFarFromItsRegion() {
        SdfProgram Dilated(bool mask) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            return builder.ResetPoint().PushField().ResetPoint().Translate(offset: new Vector3(x: 5f, y: 0f, z: 0f)).Sphere(material: material, radius: 1f).Dilate(radius: 0.2f)
                .ResetPoint().NoiseDisplace(amplitude: 0.1f, frequency: 1f, masks: (mask ? [BowlMask()] : null)).PopField().Build();
        }

        var point = new Vector3(x: 6.3f, y: 0.2f, z: 0f);

        Assert.Equal(expected: Distance(field: new SdfFieldEvaluator(program: Dilated(mask: false)), point: point), actual: Distance(field: new SdfFieldEvaluator(program: Dilated(mask: true)), point: point));
    }

    [Fact]
    public void ABandSnapsToItsGridsAndAWeightOutsideTheUnitIntervalIsRefused() {
        var band = SdfReliefBand.Of(start: 0.1f, width: 0.06f);

        Assert.Equal(expected: (410f / 4096f), actual: band.Start);
        Assert.Equal(expected: (246f / 4096f), actual: band.Width);
        Assert.Equal(expected: (656f / 4096f), actual: band.End);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfReliefBand.Of(outside: 1.5f, start: 0f, width: Fade));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfReliefBand.Of(start: 0f, width: 0f));
    }
}
