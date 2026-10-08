using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class SdfTapeInventoryLawTests(ITestOutputHelper output) {
    [InlineData("nexus")]
    [InlineData("courtyard")]
    [Theory]
    public void ComposedCountersProgramsExposeEveryCandidateCertificate(string workload) {
        var frame = ComposedSdfWorldFixture.Capture(relativePath: $"tests/Puck.Counters/{workload}.world.json");
        var program = frame.Program;
        var shapes = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
        var missing = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
        var ops = new List<string>();
        var count = 0;
        var finite = 0;
        var omissible = 0;
        var dynamic = 0;
        var scoped = 0;
        var conditional = 0;
        var scopeDepth = 0;
        var isDynamic = false;

        for (var index = 0; (index < program.InstructionCount); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op == SdfOp.ResetPoint) { ops.Clear(); isDynamic = false; }
            if (instruction.Op == SdfOp.PushField) { scopeDepth++; }
            if (instruction.Op == SdfOp.PopField) { scopeDepth--; }
            if (instruction.Op == SdfOp.TransformDynamic) { isDynamic = true; }
            if (instruction.Op != SdfOp.ShapeBlend) {
                ops.Add(item: instruction.Op.ToString());
                continue;
            }
            count++;
            dynamic += (isDynamic ? 1 : 0);
            scoped += ((scopeDepth > 0) ? 1 : 0);
            conditional += ((instruction.Detail || !instruction.Secondary) ? 1 : 0);
            var certificate = program.TapeCertificate(instruction: index);

            finite += (((certificate.Flags & SdfTapeCertificate.Certified) != 0) ? 1 : 0);
            omissible += (((certificate.Flags & SdfTapeCertificate.OmissibleSegment) != 0) ? 1 : 0);
            var shape = ((SdfShapeType)instruction.Shape).ToString();

            if (instruction.Shape == ((uint)SdfShapeType.Superellipsoid)) { shape += $"(e={instruction.Data0.W})"; }
            var description = $"{shape} {((SdfBlendOp)instruction.Blend)} flags={certificate.Flags}";

            shapes[description] = (shapes.GetValueOrDefault(key: description) + 1);
            if (certificate.Flags == 0) {
                var chain = $"{shape}: {string.Join(separator: ',', values: ops)}";

                missing[chain] = (missing.GetValueOrDefault(key: chain) + 1);
            }
        }
        var words = program.Words;
        var segmentHeader = checked((((int)((words[SdfProgram.ProgramMaterialOffsetLane]
            + (((uint)SdfProgram.MaterialVectorsPerEntry) * words[SdfProgram.ProgramMaterialCountLane]))
            + (((uint)SdfProgram.BoundRecordVectors) * words[SdfProgram.ProgramInstructionCountLane]))) * 4));
        var instanceHeader = (segmentHeader + ((1 + (SdfProgram.BoundRecordVectors * program.SkipSegmentCount)) * 4));
        var rigidOffset = checked((((int)words[(segmentHeader + SdfProgram.SegmentRigidPlanLane)]) * 4));
        var rigidSegments = 0;
        var rigidLeaves = 0;

        for (var segment = 0; (segment < program.SkipSegmentCount); segment++) {
            var leaves = ((int)words[((rigidOffset + (4 * segment)) + 1)]);

            rigidSegments += ((leaves > 0) ? 1 : 0);
            rigidLeaves += leaves;
        }
        var parts = ((int)words[(instanceHeader + SdfProgram.InstancePartProgramsLane)]);
        var partInstances = ((parts == 0) ? 0u : words[(parts * 4)] & 0x7FFFFFFFu);
        var independent = ((parts != 0) && ((words[(parts * 4)] & 0x80000000u) != 0));

        output.WriteLine(message: $"{workload}: instructions={program.InstructionCount} shapes={count} finite={finite} omissible={omissible} dynamic={dynamic} scoped={scoped} conditional={conditional} segments={program.SkipSegmentCount} rigidSegments={rigidSegments} rigidLeaves={rigidLeaves} compiledInstances={partInstances} independentParts={independent} transforms={frame.DynamicTransforms.Count}");
        foreach (var entry in shapes) { output.WriteLine(message: $"shape {entry.Value}: {entry.Key}"); }
        foreach (var entry in missing) { output.WriteLine(message: $"missing {entry.Value}: {entry.Key}"); }
        Assert.True(condition: (count > 30), userMessage: "The counters workload must retain its dense composed scene.");
        Assert.True(condition: (finite > 0), userMessage: "The production certificate packer must admit ordinary candidates.");
        Assert.Equal(expected: count, actual: shapes.Values.Sum());
    }
}
