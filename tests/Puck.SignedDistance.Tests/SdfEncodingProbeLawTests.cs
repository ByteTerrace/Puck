using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// Laws for <see cref="SdfEncodingProbe"/>, the instruction set's encoding exercised for its fingerprint: every probe
/// call builds, and together they carry every operation, shape type and blend, both lifts, and every member of each enum
/// an instruction lane carries, in the lane that carries it; the description moves when a call's operands trade places;
/// a call whose operands could not be told apart is refused; and the packer writes each header lane where the model's
/// lane constants place it.
/// </summary>
public sealed class SdfEncodingProbeLawTests {
    private static readonly (SdfEncodingProbeCall Call, SdfProgram Program)[] Built = [.. SdfEncodingProbe.Calls().Select(selector: static call => (call, SdfEncodingProbe.Build(call: call)))];

    private static IEnumerable<SdfInstruction> Emitted(SdfOp op) =>
        Built.SelectMany(selector: static built => built.Program.Instructions).Where(predicate: instruction => (instruction.Op == op));

    [Fact]
    public void TheProbeCarriesEveryOperationShapeTypeAndBlend() {
        Assert.Equal(
            actual: Built.SelectMany(selector: static built => built.Program.Instructions).Select(selector: static instruction => instruction.Op).ToHashSet(),
            expected: Enum.GetValues<SdfOp>().ToHashSet()
        );
        Assert.Equal(
            actual: Emitted(op: SdfOp.ShapeBlend).Select(selector: static instruction => ((SdfShapeType)(instruction.Shape & SdfProgram.ShapeTypeMask))).ToHashSet(),
            expected: Enum.GetValues<SdfShapeType>().ToHashSet()
        );
        Assert.Equal(
            actual: Emitted(op: SdfOp.ShapeBlend).Concat(second: Emitted(op: SdfOp.PopField)).Select(selector: static instruction => ((SdfBlendOp)instruction.Blend)).ToHashSet(),
            expected: Enum.GetValues<SdfBlendOp>().ToHashSet()
        );
        Assert.All(
            action: static lift => Assert.Contains(collection: Built, filter: built => built.Call.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: $" {lift}")),
            collection: Enum.GetValues<SdfLift>()
        );
        Assert.Contains(collection: Emitted(op: SdfOp.ShapeBlend), filter: static instruction => instruction.Detail);
        Assert.Contains(collection: Emitted(op: SdfOp.ShapeBlend), filter: static instruction => !instruction.Secondary);
    }
    [Fact]
    public void TheProbeCarriesEveryMemberOfEachLaneEnumInItsLane() {
        static void Covers<T>(IEnumerable<uint> lanes) where T : struct, Enum =>
            Assert.Equal(
                actual: lanes.Select(selector: static lane => Enum.ToObject(enumType: typeof(T), value: lane)).Cast<T>().ToHashSet(),
                expected: Enum.GetValues<T>().ToHashSet()
            );

        Covers<SdfNoiseFlavor>(lanes: Emitted(op: SdfOp.CellJitter).Select(selector: static instruction => instruction.Blend));
        Covers<SdfCellMode>(lanes: Emitted(op: SdfOp.CellDisplace).Select(selector: static instruction => instruction.Blend));
        Covers<SdfWallpaperGroup>(lanes: Emitted(op: SdfOp.WallpaperFold).Select(selector: static instruction => instruction.Shape));
        Covers<SdfPlane>(lanes: Emitted(op: SdfOp.WallpaperFold).Select(selector: static instruction => instruction.Blend));
        Covers<SdfPlane>(lanes: Emitted(op: SdfOp.RotatePlane).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.RotatePlane).Select(selector: static instruction => instruction.Blend));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.RepeatPolar).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.AxialProfile).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.Shear).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.Shear).Select(selector: static instruction => instruction.Blend));
    }
    // A builder that put a call's operands in each other's lanes describes differently: here the cell displacement's
    // frequency and amplitude trade places.
    [Fact]
    public void OperandsTradingPlacesChangeTheDescription() {
        var calls = SdfEncodingProbe.Calls();
        var index = calls.ToList().FindIndex(match: static call => (call.Name == $"cell-displace {SdfCellMode.F1}"));
        var original = calls[index];
        var traded = original with {
            Emit = static (b, m) => b.ResetPoint().Sphere(material: m, radius: 1f).CellDisplace(amplitude: 0.95f, frequency: 0.02f, mode: SdfCellMode.F1, randomness: 0.15f, seed: 17u),
        };

        Assert.Equal(expected: [0.95f, 0.02f, 17f, 0.15f], actual: original.Markers);
        Assert.NotEqual(
            actual: SdfEncodingProbe.Describe(calls: [.. calls.Take(count: index), traded, .. calls.Skip(count: (index + 1))]),
            expected: SdfEncodingProbe.Describe()
        );
    }
    [Fact]
    public void ACallWhoseOperandsCannotBeToldApartIsRefused() {
        var repeated = new SdfEncodingProbeCall(Emit: static (b, m) => b.ResetPoint().Sphere(material: m, radius: 0.5f), Markers: [0.5f, 0.5f], Name: "repeated");
        var zero = repeated with { Markers = [0f], Name = "zero" };

        _ = Assert.Throws<ArgumentException>(testCode: () => SdfEncodingProbe.Describe(calls: [repeated]));
        _ = Assert.Throws<ArgumentException>(testCode: () => SdfEncodingProbe.Describe(calls: [zero]));
    }
    // The packer writes the program header and every instruction header lane where the model's lane constants, which
    // the kernels read through generated accessors, place them.
    [Fact]
    public void ThePackerWritesEachHeaderLaneWhereTheModelPlacesIt() {
        foreach (var (_, program) in Built) {
            var words = program.Words;

            Assert.Equal(expected: ((uint)program.InstructionCount), actual: words[SdfProgram.ProgramInstructionCountLane]);
            Assert.Equal(expected: ((uint)program.MaterialCount), actual: words[SdfProgram.ProgramMaterialCountLane]);

            for (var index = 0; (index < program.InstructionCount); index++) {
                var instruction = program.Instructions[index];
                var header = ((SdfProgram.ProgramHeaderVectors + index) * 4);
                var data = ((((int)words[SdfProgram.ProgramDataOffsetLane]) + (SdfProgram.InstructionDataVectors * index)) * 4);

                Assert.Equal(expected: ((uint)instruction.Op), actual: words[(header + SdfProgram.InstructionOpLane)]);
                Assert.Equal(expected: instruction.Shape, actual: words[(header + SdfProgram.InstructionShapeLane)] & ((instruction.Op == SdfOp.ShapeBlend) ? SdfProgram.ShapeTypeMask : uint.MaxValue));
                Assert.Equal(expected: instruction.Blend, actual: words[(header + SdfProgram.InstructionBlendLane)]);
                Assert.Equal(expected: instruction.Material, actual: words[(header + SdfProgram.InstructionMaterialLane)]);
                Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: instruction.Data1.X), actual: words[(data + 4)]);
            }
        }
    }
    // The layout program builds, packs a static and a dynamic instance and every material field, and describes the same
    // bytes each time.
    [Fact]
    public void TheLayoutProgramPacksItsWordsTheSameEveryTime() {
        var layout = SdfEncodingProbe.BuildLayout();

        Assert.Equal(expected: 2, actual: layout.Instances.Count);
        Assert.Equal(expected: 2, actual: layout.MaterialCount);
        Assert.Equal(expected: layout.Words.ToArray(), actual: SdfEncodingProbe.BuildLayout().Words.ToArray());
        Assert.Equal(expected: SdfEncodingProbe.Describe(), actual: SdfEncodingProbe.Describe());
    }
}
