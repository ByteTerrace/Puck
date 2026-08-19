using System.Diagnostics;
using System.Numerics;
using System.Text;
using Puck.ShaderVm.Programs;
using Xunit;

namespace Puck.ShaderVm.Tests;

// Host-interpreter throughput. The GPU interpreter is a different machine; these numbers bound the reference path
// and give the per-instruction cost model the value graph is budgeted against.
public sealed class ShaderVmThroughput {
    private const int Samples = 200_000;

    private static Vector4 s_sink;

    [Fact]
    public void Measures() {
        var directory = Environment.GetEnvironmentVariable(variable: "PUCK_SKY_PREVIEW_DIR");

        Assert.False(condition: string.IsNullOrEmpty(value: directory), userMessage: "Set PUCK_SKY_PREVIEW_DIR to the directory the report is written to.");

        var report = new StringBuilder();

        _ = report.AppendLine(value: $"cores={Environment.ProcessorCount} samples={Samples}");
        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "program                     instr   ns/sample   ns/instr    Minstr/s   1080p ms");
        _ = report.AppendLine(value: "-------------------------- ------ ----------- ---------- ----------- ----------");

        Measure(label: "sky (full)", program: SkyProgram.Compile(), report: report);
        Measure(label: "gradient only", program: Synthetic(kind: "gradient"), report: report);
        Measure(label: "gradient + sun disc", program: Synthetic(kind: "disc"), report: report);
        Measure(label: "stars only", program: Synthetic(kind: "stars"), report: report);
        Measure(label: "clouds only", program: Synthetic(kind: "clouds"), report: report);

        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "layered effects: N independent cloud decks composited over the sky");
        _ = report.AppendLine(value: "layers   instr  stack  locals   ns/sample   scratch B/lane");
        _ = report.AppendLine(value: "------ ------- ------ ------- ----------- ---------------");

        foreach (var layers in (int[])[1, 2, 4, 8, 16, 32]) {
            var program = Layered(layers: layers);
            var statistics = ShaderProgramStatistics.Measure(program: program);

            _ = report.AppendLine(value: string.Format(
                args: [layers, statistics.InstructionCount, statistics.StackDepth, statistics.LocalCount, Nanoseconds(program: program), (((statistics.StackDepth + statistics.LocalCount) * 4) * 4)],
                format: "{0,6} {1,7} {2,6} {3,7} {4,11:F1} {5,15}",
                provider: null
            ));
        }

        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "interpreter demand (what the GPU must provision per lane)");
        _ = report.AppendLine(value: "-------------------------------------------------------------");

        foreach (var (label, program) in ((string, ShaderProgram)[])[("sky (full)", SkyProgram.Compile()), ("stars only", Synthetic(kind: "stars")), ("clouds only", Synthetic(kind: "clouds"))]) {
            var statistics = ShaderProgramStatistics.Measure(program: program);

            _ = report.AppendLine(value: $"{label,-26} stack={statistics.StackDepth,3}  locals={statistics.LocalCount,3}  branches={statistics.Branches,3}  path={statistics.LongestPath,5}");
        }

        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "opcode cost: ns per operation, in excess of one Add");
        _ = report.AppendLine(value: "-------------------------------------------------------------");

        var baseline = Nanoseconds(program: Chain(count: 64, op: ShaderOp.Add));

        _ = report.AppendLine(value: $"{"Add (absolute)",-26} {(baseline / 64d),8:F2}");

        foreach (var op in (ShaderOp[])[ShaderOp.Multiply, ShaderOp.Swizzle, ShaderOp.Dot3, ShaderOp.Lerp, ShaderOp.Select, ShaderOp.SquareRoot, ShaderOp.Sine, ShaderOp.Exponential, ShaderOp.Power, ShaderOp.Hash3, ShaderOp.ValueNoise2]) {
            var cost = ((Nanoseconds(program: Chain(count: 64, op: op)) - baseline) / 64d);

            _ = report.AppendLine(value: $"{op,-26} {cost,8:F2}");
        }
        foreach (var octaves in (int[])[1, 2, 4, 8]) {
            var cost = ((Nanoseconds(program: Chain(count: 64, octaves: octaves, op: ShaderOp.Fbm2)) - baseline) / 64d);

            _ = report.AppendLine(value: $"{$"Fbm2 x{octaves}",-26} {cost,8:F2}");
        }

        File.WriteAllText(
            contents: report.ToString(),
            path: Path.Combine(path1: directory!, path2: "throughput.txt")
        );
    }
    private static void Measure(string label, ShaderProgram program, StringBuilder report) {
        var nanoseconds = Nanoseconds(program: program);
        var perInstruction = (nanoseconds / program.InstructionCount);

        _ = report.AppendLine(value: string.Format(
            args: [label, program.InstructionCount, nanoseconds, perInstruction, (1000d / perInstruction), (((1920d * 1080d) * nanoseconds) / 1_000_000d)],
            format: "{0,-26} {1,6} {2,11:F1} {3,10:F3} {4,11:F1} {5,10:F1}",
            provider: null
        ));
    }
    private static double Nanoseconds(ShaderProgram program) {
        var parameters = new Vector4[SkyParameters.Count];
        var settings = new SkyFrameSettings();

        SkyParameters.Pack(rows: parameters, settings: in settings);

        var accumulator = Vector4.Zero;

        for (var warmup = 0; (warmup < 20_000); warmup++) {
            accumulator += Evaluate(index: warmup, parameters: parameters, program: program);
        }

        // Best of several runs: the minimum is the run least disturbed by the scheduler, which is what a latency
        // measurement wants.
        var best = double.MaxValue;

        for (var repetition = 0; (repetition < 7); repetition++) {
            var stopwatch = Stopwatch.StartNew();

            for (var sample = 0; (sample < Samples); sample++) {
                accumulator += Evaluate(index: sample, parameters: parameters, program: program);
            }

            stopwatch.Stop();
            best = Math.Min(val1: best, val2: (stopwatch.Elapsed.TotalNanoseconds / Samples));
        }

        s_sink = accumulator;

        return best;
    }
    private static Vector4 Evaluate(ShaderProgram program, int index, Vector4[] parameters) {
        var angle = (index * 0.001f);
        var direction = Vector3.Normalize(value: new Vector3(x: MathF.Cos(x: angle), y: MathF.Sin(x: (angle * 0.37f)), z: MathF.Sin(x: angle)));
        var context = new ShaderContext(Coordinate: new Vector4(x: direction.X, y: direction.Y, z: direction.Z, w: 0f));

        return ShaderInterpreter.Evaluate(context: in context, parameters: parameters, program: program);
    }
    // The body is `count` independent applications of one operation over a shared safe input, summed. Subtracting
    // the same shape built from Add isolates the operation's own cost from the accumulation around it.
    private static ShaderProgram Chain(ShaderOp op, int count, int octaves = 4) {
        var input = (ShaderMath.Saturate(value: ShaderExpression.Input(input: ShaderInput.Coordinate)) + ShaderExpression.Constant(value: 0.5f));
        var operand = ((op == ShaderOp.Fbm2) ? checked((uint)octaves) : ((op == ShaderOp.Swizzle) ? ShaderIsa.PackSwizzle(x: 1, y: 2, z: 3, w: 0) : 0u));
        var total = ShaderExpression.Constant(value: 0f);

        for (var step = 0; (step < count); step++) {
            var right = ShaderExpression.Constant(value: (1f + (step * 0.001f)));

            total += (Arity(op: op) switch {
                1 => ShaderExpression.Unary(op: op, operand: operand, value: input),
                2 => ShaderExpression.Binary(left: input, op: op, right: right),
                _ => ShaderExpression.Ternary(a: input, b: ShaderExpression.Constant(value: 0.5f), c: right, op: op),
            });
        }

        return ShaderExpressionCompiler.Compile(root: total);
    }
    private static int Arity(ShaderOp op) => (op switch {
        >= ShaderOp.Lerp and <= ShaderOp.Select => 3,
        >= ShaderOp.Add and <= ShaderOp.Greater => 2,
        _ => 1,
    });
    // The sky with N further cloud decks composited over it: the shape a world layering weather, aurora and haze
    // would actually produce.
    private static ShaderProgram Layered(int layers) {
        var direction = ShaderExpression.Input(input: ShaderInput.Coordinate).Normalized3;
        var color = SkyProgram.Build();

        for (var layer = 0; (layer < layers); layer++) {
            var point = ((direction.Swizzle(x: 0, y: 2, z: 0, w: 2) * (1f + layer)) + ShaderExpression.Constant(value: (layer * 7.3f)));
            var density = ShaderMath.Fbm2(octaves: 4, position: ShaderMath.Seeded(position: point, seed: (4242u + ((uint)layer))));
            var mask = ShaderMath.SmoothStep(edge0: 0.55f, edge1: 0.85f, value: density);

            color = ShaderMath.Lerp(amount: mask, from: color, to: ShaderExpression.Parameter(index: SkyParameters.Clouds));
        }

        return ShaderExpressionCompiler.Compile(root: color);
    }
    private static ShaderProgram Synthetic(string kind) {
        var direction = ShaderExpression.Input(input: ShaderInput.Coordinate).Normalized3;
        var full = SkyProgram.Build();

        return (kind switch {
            "gradient" => ShaderExpressionCompiler.Compile(root: ShaderMath.Lerp(
                amount: ShaderMath.Saturate(value: direction.Y),
                from: ShaderExpression.Parameter(index: SkyParameters.Horizon),
                to: ShaderExpression.Parameter(index: SkyParameters.Zenith)
            )),
            "disc" => ShaderExpressionCompiler.Compile(root: (ShaderMath.Lerp(
                amount: ShaderMath.Saturate(value: direction.Y),
                from: ShaderExpression.Parameter(index: SkyParameters.Horizon),
                to: ShaderExpression.Parameter(index: SkyParameters.Zenith)
            ) + ShaderMath.Pow(
                exponent: ShaderExpression.Parameter(index: SkyParameters.Sun).W,
                value: ShaderMath.Saturate(value: ShaderMath.Dot3(left: direction, right: ShaderExpression.Parameter(index: SkyParameters.Sun)))
            ))),
            "stars" => ShaderExpressionCompiler.Compile(root: StarsOnly(direction: direction)),
            _ => ShaderExpressionCompiler.Compile(root: CloudsOnly(direction: direction)),
        });
    }
    // The star and cloud halves alone, reached through the same public surface the full program is built from.
    private static ShaderExpression StarsOnly(ShaderExpression direction) {
        var stars = ShaderExpression.Parameter(index: SkyParameters.Stars);
        var cell = ShaderMath.Floor(value: (direction.Swizzle(x: 0, y: 1, z: 0, w: 1) * stars.X));
        var hash = ShaderMath.Hash3(value: ShaderMath.Seeded(position: cell, seed: 1337u));
        var unit = ShaderMath.Unit(value: hash);
        var second = ShaderMath.Unit(value: ShaderMath.Hash3(value: hash));
        var third = ShaderMath.Unit(value: ShaderMath.Hash3(value: ShaderMath.Hash3(value: hash)));
        var flicker = ShaderMath.Sin(value: ((third.X * stars.Y) + third.Z));

        return ShaderMath.Select(
            condition: ShaderMath.Step(edge: unit.X, value: 0.08f),
            whenFalse: ShaderExpression.Constant(value: 0f),
            whenTrue: ((second.X * flicker) * ShaderMath.Sqrt(value: ShaderMath.Max(left: second.Y, right: 1e-6f)))
        );
    }
    private static ShaderExpression CloudsOnly(ShaderExpression direction) {
        var shape = ShaderExpression.Parameter(index: SkyParameters.CloudShape);
        var point = (direction.Swizzle(x: 0, y: 2, z: 0, w: 2) / shape.Y);
        var warp = ShaderMath.Fbm2(octaves: 4, position: ShaderMath.Seeded(position: point, seed: 0x9E3779B9u));
        var density = ShaderMath.Fbm2(octaves: 4, position: ShaderMath.Seeded(position: (point + warp), seed: 4242u));

        return ShaderMath.SmoothStep(edge0: 0.6f, edge1: 0.9f, value: density);
    }
}
