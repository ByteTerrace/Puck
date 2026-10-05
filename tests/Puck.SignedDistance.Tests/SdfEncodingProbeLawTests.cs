using Puck.Testing;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// Laws for <see cref="SdfEncodingProbe"/>, the instruction set's encoding exercised for its fingerprint: every probe
/// call builds, and every call stays valid with each input raised; together they carry every operation, shape type and
/// blend, both lifts, the shape flags, and every member of each enum an instruction lane carries, in the lane that
/// carries it (every wallpaper group a program accepts; SdfWallpaperFoldLawTests holds the refused groups' refusal),
/// and they pack every side table and flag (compiled part programs traced independently, a material with
/// every layer, sweep and path tables); the description moves when the builder or packer puts any field elsewhere,
/// whether a float lane, a bitfield inside a word, a material layer's field or a table entry; and the packer writes each
/// header lane where the model's lane constants place it.
/// </summary>
public sealed class SdfEncodingProbeLawTests {
    private static readonly IReadOnlyList<SdfEncodingProbeCall> Calls = SdfEncodingProbe.Calls();
    private static readonly (SdfEncodingProbeCall Call, SdfProgram Program)[] Built = [.. Calls.Select(selector: static call => (call, SdfEncodingProbe.Build(call: call)))];

    private static IEnumerable<SdfInstruction> Emitted(SdfOp op) =>
        Built.SelectMany(selector: static built => built.Program.Instructions).Where(predicate: instruction => (instruction.Op == op));
    private static SdfEncodingProbeCall Named(string name) =>
        Calls.Single(predicate: call => (call.Name == name));
    // The whole probe's description with one call replaced.
    private static string DescribeWith(SdfEncodingProbeCall call) =>
        SdfEncodingProbe.Describe(calls: [.. Calls.Select(selector: other => ((other.Name == call.Name) ? call : other))]);

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
        Covers<SdfIndirectParticipation>(lanes: Built.SelectMany(selector: static built => built.Program.Instances).Select(selector: static instance => ((uint)instance.Indirect)));
        Covers<SdfCellMode>(lanes: Emitted(op: SdfOp.CellDisplace).Select(selector: static instruction => instruction.Blend));
        Assert.Equal(
            actual: Emitted(op: SdfOp.WallpaperFold).Select(selector: static instruction => ((SdfWallpaperGroup)instruction.Shape)).ToHashSet(),
            expected: Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous).ToHashSet()
        );
        Covers<SdfPlane>(lanes: Emitted(op: SdfOp.WallpaperFold).Select(selector: static instruction => instruction.Blend));
        Covers<SdfPlane>(lanes: Emitted(op: SdfOp.RotatePlane).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.RotatePlane).Select(selector: static instruction => instruction.Blend));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.RepeatPolar).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.AxialProfile).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.Shear).Select(selector: static instruction => instruction.Shape));
        Covers<SdfAxis>(lanes: Emitted(op: SdfOp.Shear).Select(selector: static instruction => instruction.Blend));
    }
    // The side-table calls pack what the kernels read: a part table whose header carries the independent-tracing flag
    // over one compiled instance, and sweep and path tables beside their instructions.
    [Fact]
    public void TheProbePacksEverySideTableAndFlag() {
        var parts = SdfEncodingProbe.Build(call: Named(name: "part-programs"));

        Assert.Equal(expected: 0x80000001u, actual: parts.Words[(PartTableOf(program: parts) * 4)]);
        Assert.Contains(collection: SdfEncodingProbe.Build(call: Named(name: "sweep")).Instructions, filter: static instruction => (instruction.Shape == ((uint)SdfShapeType.Sweep)));
        Assert.Contains(collection: SdfEncodingProbe.Build(call: Named(name: "path")).Instructions, filter: static instruction => (instruction.Shape == ((uint)SdfShapeType.Path)));
        Assert.Equal(expected: (1 + 64), actual: SdfEncodingProbe.Build(call: Named(name: "material")).MaterialCount);
    }
    // Every call stays valid with each of its inputs raised, which is how the description finds where each lands, and the
    // description is the same every time.
    [Fact]
    public void EveryCallBuildsWithEachInputRaised() {
        var description = SdfEncodingProbe.Describe();

        Assert.All(
            action: call => Assert.Contains(expectedSubstring: $"{call.Name} words=", actualString: description),
            collection: Calls
        );
        Assert.Equal(expected: description, actual: SdfEncodingProbe.Describe());
    }
    // A builder or packer that put a field elsewhere describes differently. Each trade is spelled at the call, which is
    // what the description sees of the builder writing one field where the other goes: a rotation's Y and W, a sampled
    // region's Y and Z dimension bitfields, a weathering's Edge and Lines, a sweep's start and end radii, and a cell
    // displacement's frequency and amplitude.
    [Fact]
    public void AFieldPutElsewhereChangesTheDescription() {
        var description = SdfEncodingProbe.Describe();

        Assert.Empty(collection: SdfEncodingTrades.Traded(calls: Calls).Where(predicate: traded => (DescribeWith(call: traded) == description)).Select(selector: static traded => traded.Name));
    }
    // A packer that laid words out otherwise describes differently even where one input moves many words: every rigid
    // leaf's rotation with X and Y exchanged (the normalized rotation is written twice, as the instruction's payload and
    // the leaf's), and every stroked path edge with its two radii exchanged.
    [Fact]
    public void APackerThatLaysWordsOutOtherwiseChangesTheDescription() {
        var description = SdfEncodingProbe.Describe();

        foreach (var (name, wordsOf) in ((ReadOnlySpan<(string, Func<SdfProgram, uint[]>)>)[
            ("rotate", SdfEncodingTrades.RigidLeafRotationXySwapped),
            ("path stroke", SdfEncodingTrades.PathRadiiSwapped),
        ])) {
            var program = SdfEncodingProbe.Build(call: Named(name: name));

            Assert.NotEqual(expected: program.Words.ToArray(), actual: wordsOf(arg: program));
            Assert.NotEqual(expected: description, actual: SdfEncodingProbe.Describe(calls: Calls, wordsOf: wordsOf));
        }
    }
    // The part table's header records the independent-tracing flag, so a program whose root keeps parts from tracing
    // alone describes differently.
    [Fact]
    public void TheIndependentTracingFlagIsInTheDescription() {
        var original = Named(name: "part-programs");
        var untraceable = original with {
            Emit = (b, m, v) => {
                original.Emit(arg1: b, arg2: m, arg3: v);
                b.ResetPoint().Sphere(blend: SdfBlendOp.Subtraction, material: m, radius: 0.1f);
            },
        };
        var program = SdfEncodingProbe.Build(call: untraceable);

        Assert.Equal(expected: 1u, actual: program.Words[(PartTableOf(program: program) * 4)]);
        Assert.NotEqual(expected: SdfEncodingProbe.Describe(), actual: DescribeWith(call: untraceable));
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

    // The vector offset of a program's part table: the instance directory header's part-programs lane.
    private static int PartTableOf(SdfProgram program) {
        var words = program.Words;
        var boundsOffset = (((int)words[SdfProgram.ProgramMaterialOffsetLane]) + (SdfProgram.MaterialVectorsPerEntry * ((int)words[SdfProgram.ProgramMaterialCountLane])));
        var segmentOffset = (boundsOffset + (SdfProgram.BoundRecordVectors * program.InstructionCount));
        var instanceOffset = ((segmentOffset + SdfProgram.DirectoryHeaderVectors) + (SdfProgram.BoundRecordVectors * ((int)words[((segmentOffset * 4) + SdfProgram.SegmentCountLane)])));

        return ((int)words[((instanceOffset * 4) + SdfProgram.InstancePartProgramsLane)]);
    }
}
