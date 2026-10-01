using System.Text;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>Counts the ops, shapes and blends each studied world's composed render program carries. A measurement,
/// run on request: <c>dotnet test tests/Puck.World.Tests -c Release --filter FullyQualifiedName~FidgetStudy -- xUnit.Explicit=on</c>.</summary>
public sealed class SdfStudyCensusExperiment {
    [Fact(Explicit = true)]
    public void CensusOfTheStudiedPrograms() {
        var report = new StringBuilder();

        foreach (var (name, path) in SdfStudyScene.Worlds) {
            var scene = SdfStudyScene.Load(name: name, relativePath: path);
            var program = scene.Program;
            var ops = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
            var shapes = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
            var blends = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);

            foreach (var instruction in program.Instructions) {
                Bump(counts: ops, key: instruction.Op.ToString());

                if (instruction.Op == SdfOp.ShapeBlend) {
                    Bump(counts: shapes, key: ((SdfShapeType)(instruction.Shape & SdfProgram.ShapeTypeMask)).ToString());
                    Bump(counts: blends, key: ((SdfBlendOp)instruction.Blend).ToString());
                } else if (instruction.Op == SdfOp.PopField) {
                    Bump(counts: blends, key: ("pop:" + ((SdfBlendOp)instruction.Blend).ToString()));
                }
            }
            report.AppendLine(value: $"== {name}: {program.InstructionCount} instructions, {program.Instances.Count} instances, stepScale {program.StepScale}, transforms {scene.Transforms.Length}");
            report.AppendLine(value: ("  ops: " + string.Join(separator: ", ", values: ops.Select(selector: pair => $"{pair.Key}={pair.Value}"))));
            report.AppendLine(value: ("  shapes: " + string.Join(separator: ", ", values: shapes.Select(selector: pair => $"{pair.Key}={pair.Value}"))));
            report.AppendLine(value: ("  blends: " + string.Join(separator: ", ", values: blends.Select(selector: pair => $"{pair.Key}={pair.Value}"))));
            var tape = new SdfStudyTape(program: program, transforms: scene.Transforms);
            var scales = tape.Instructions.Where(predicate: instruction => (instruction.Op == SdfOp.Scale)).ToArray();

            report.AppendLine(value: $"  segments: {tape.Segments.Length}, rigid {tape.Segments.Count(predicate: segment => segment.Rigid)}, bounded {tape.Segments.Count(predicate: segment => (segment.Bound.Mode != SdfProgram.BoundModeNone))}; shapes bounded {tape.ShapeBounds.Count(predicate: bound => (bound.Mode != SdfProgram.BoundModeNone))}");
            var slots = tape.Instructions.Where(predicate: instruction => (instruction.Op == SdfOp.TransformDynamic)).Select(selector: instruction => ((int)instruction.Data0.X)).Distinct().ToArray();

            report.AppendLine(value: $"  dynamic slots referenced: {slots.Length}, parked (y <= -500): {slots.Count(predicate: slot => (tape.Transforms[slot].Position.Y <= -500f))}");
            report.AppendLine(value: $"  scale ops: {scales.Length}, identity {scales.Count(predicate: scale => ((scale.Data0.X == 1.0) && (scale.Data0.Y == 1.0) && (scale.Data0.Z == 1.0)))}, uniform {scales.Count(predicate: scale => ((scale.Data0.X == scale.Data0.Y) && (scale.Data0.Y == scale.Data0.Z)))}");
        }
        SdfStudyReport.Write(name: "census.txt", text: report.ToString());

        static void Bump(SortedDictionary<string, int> counts, string key) => counts[key] = (counts.GetValueOrDefault(key: key) + 1);
    }
}
/// <summary>Writes a study report beside the test assembly and to the test's output.</summary>
public static class SdfStudyReport {
    /// <summary>Writes one report file under <c>fidget-study/</c> in the test output directory.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="text">The report text.</param>
    public static void Write(string name, string text) {
        var directory = Path.Combine(path1: AppContext.BaseDirectory, path2: "fidget-study");

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(path: Path.Combine(path1: directory, path2: name), contents: text);
        TestContext.Current.TestOutputHelper?.WriteLine(message: text);
    }
}
