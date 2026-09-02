using System.Diagnostics;
using System.Numerics;
using System.Text;
using Xunit;

namespace Puck.ShaderVm.Tests;

// The generality tax, measured: the same field as an interpreted Shader VM program and as hand-written scalar C#.
// Agreement bounds the port's fidelity; the timing ratio is what genericity costs on one machine.
public sealed class NullWorldComparison {
    private const int Samples = 200_000;

    [Fact]
    public void Measures() {
        var directory = Environment.GetEnvironmentVariable(variable: "PUCK_SKY_PREVIEW_DIR");

        Assert.SkipWhen(condition: string.IsNullOrEmpty(value: directory), reason: "Opt-in harness: set PUCK_SKY_PREVIEW_DIR to the directory the report is written to.");

        var program = ShaderExpressionCompiler.Compile(root: NullWorldScene.Build(point: ShaderExpression.Input(input: ShaderInput.Coordinate)));
        var statistics = ShaderProgramStatistics.Measure(program: program);
        var points = Points();
        var report = new StringBuilder();

        var worstDistance = 0d;
        var worstAt = Vector3.Zero;
        var materialMismatches = 0;
        var relativeSum = 0d;
        var compared = 0;

        foreach (var point in points) {
            var expected = NullWorldReference.Sample(point: point);
            var context = new ShaderContext(Coordinate: new Vector4(w: 0f, x: point.X, y: point.Y, z: point.Z));
            var actual = ShaderInterpreter.Evaluate(context: in context, parameters: [], program: program);
            var delta = Math.Abs(value: (((double)actual.X) - expected.Distance));

            if (delta > worstDistance) {
                worstDistance = delta;
                worstAt = point;
            }
            if (((int)(actual.Y + 0.5f)) != expected.Material) {
                materialMismatches++;
            }

            var magnitude = Math.Abs(value: expected.Distance);

            if (magnitude > 1e-3f) {
                relativeSum += (delta / magnitude);
                compared++;
            }
        }

        var interpreted = Nanoseconds(evaluate: point => {
            var context = new ShaderContext(Coordinate: new Vector4(w: 0f, x: point.X, y: point.Y, z: point.Z));

            return ShaderInterpreter.Evaluate(context: in context, parameters: [], program: program).X;
        }, points: points);
        var bespoke = Nanoseconds(evaluate: point => NullWorldReference.Sample(point: point).Distance, points: points);

        _ = report.AppendLine(value: $"points={points.Length}");
        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "AGREEMENT (interpreted program vs hand-written C#)");
        _ = report.AppendLine(value: $"  worst absolute distance delta   {worstDistance:E3} at ({worstAt.X:F3}, {worstAt.Y:F3}, {worstAt.Z:F3})");
        _ = report.AppendLine(value: $"  mean relative delta             {((relativeSum / Math.Max(val1: compared, val2: 1))):E3}");
        _ = report.AppendLine(value: $"  material mismatches             {materialMismatches} / {points.Length}");
        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "COST per field evaluation");
        _ = report.AppendLine(value: $"  hand-written C#                 {bespoke,9:F1} ns");
        _ = report.AppendLine(value: $"  Shader VM interpreted           {interpreted,9:F1} ns");
        _ = report.AppendLine(value: $"  ratio                           {((interpreted / bespoke)),9:F1}x");
        _ = report.AppendLine(value: $"  per instruction                 {((interpreted / statistics.InstructionCount)),9:F2} ns");
        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "PROGRAM");
        _ = report.AppendLine(value: $"  instructions                    {statistics.InstructionCount}");
        _ = report.AppendLine(value: $"  constants                       {program.ConstantCount}");
        _ = report.AppendLine(value: $"  packed words                    {program.Words.Length}  ({(program.Words.Length * 4)} bytes)");
        _ = report.AppendLine(value: $"  stack depth / locals            {statistics.StackDepth} / {statistics.LocalCount}");

        File.WriteAllText(
            contents: report.ToString(),
            path: Path.Combine(path1: directory!, path2: "comparison.txt")
        );
        Assert.Equal(actual: materialMismatches, expected: 0);
        Assert.True(condition: (worstDistance < 1e-4), userMessage: $"worst distance delta {worstDistance:E3} at ({worstAt.X}, {worstAt.Y}, {worstAt.Z})");
    }

    // A deterministic spread over the whole authored volume, up past the highest planetoid, so every creation and
    // every domain fold is exercised.
    private static Vector3[] Points() {
        var points = new Vector3[Samples];
        var state = 0x9E3779B9u;

        for (var index = 0; (index < points.Length); index++) {
            points[index] = new Vector3(
                x: ((Next(state: ref state) * 28f) - 14f),
                y: ((Next(state: ref state) * 31f) - 1f),
                z: ((Next(state: ref state) * 28f) - 14f)
            );
        }

        return points;
    }
    private static float Next(ref uint state) {
        unchecked {
            state = ((state * 1664525u) + 1013904223u);
            state ^= (state >> 16);
            state = ((state * 1664525u) + 1013904223u);
        }

        return (((float)state) * ShaderIsa.InverseTwoPow32);
    }
    private static double Nanoseconds(Func<Vector3, float> evaluate, Vector3[] points) {
        var sink = 0f;

        for (var warmup = 0; (warmup < 20_000); warmup++) {
            sink += evaluate(arg: points[(warmup % points.Length)]);
        }

        var best = double.MaxValue;

        for (var repetition = 0; (repetition < 5); repetition++) {
            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; (index < points.Length); index++) {
                sink += evaluate(arg: points[index]);
            }

            stopwatch.Stop();
            best = Math.Min(val1: best, val2: (stopwatch.Elapsed.TotalNanoseconds / points.Length));
        }

        Assert.False(condition: float.IsPositiveInfinity(f: sink));

        return best;
    }
}
