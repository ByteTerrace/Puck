using System.Text.Json;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Holds a shade program's <c>gradient</c> to the exact derivative of its height: equal to a central difference of
/// the height's own value across a brick joint's bevel, carried through the one cellular search, and priced as code.</summary>
public sealed class ShadeGradientTests {
    private static string World(string body, string shade = "sample()") => $$"""
        schema: "puck.world.definition.v1"
        documentId: "shade-gradient-laws"
        import "../standard/shading"
        shade template sample() { surface { {{body}} } }
        prototypes { prototype "p" { document {
          palette [ { color: "#808080", shade: {{shade}} } ]
          shape Box "box" { scale [1, 1, 1] }
        } } }
        """;
    private static SdfShadeProgram Compile(string body, string shade = "sample()") {
        // The standard package resolves from the debug world's folder, as a document there imports it.
        var path = RepositoryPaths.Resolve(relativePath: "worlds/debug/shade-gradient-laws.puck");
        var compilation = WorldCompileSession.Shared.Compile(source: World(body: body, shade: shade), sourcePath: path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport());
        var key = compilation.Json!["prototypes"]![0]!["document"]!["palette"]![0]!["shade"]!.GetValue<string>();
        var program = SdfShadeProgram.Compile(definition: compilation.Json!["shades"]![key]!.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)!);

        Assert.Equal(expected: program.Identity, actual: SdfShadeProgram.Compile(definition: program.Definition).Identity);
        return program;
    }

    [Fact]
    public void BrickMortarGradientEqualsItsCentralDifferenceAcrossTheJoint() {
        const string Height = "shadeBrick(q, [0.4, 0.2], 0.025, uint(3)).x";
        var program = Compile(body: $"roughness = {Height.Replace(oldValue: "(q,", newValue: "(surface.position,", comparisonType: StringComparison.Ordinal)}\n albedo = gradient(surface.position, q => {Height})");
        var slopes = 0;

        // A vertical joint at x = 0.4 in the first course: the bevel ramps from 6.25 mm to 18.75 mm off the joint's line.
        for (var x = 0.37f; (x <= 0.43f); x += 0.0005f) {
            var point = new System.Numerics.Vector3(x, 0.05f, 0f);
            var gradient = Evaluate(program: program, output: "albedo", point: point);
            const float Step = 2.0e-5f;
            var ahead = Evaluate(program: program, output: "roughness", point: (point + new System.Numerics.Vector3(Step, 0f, 0f))).X;
            var behind = Evaluate(program: program, output: "roughness", point: (point - new System.Numerics.Vector3(Step, 0f, 0f))).X;
            var difference = ((ahead - behind) / (2f * Step));

            Assert.Equal(expected: 0f, actual: gradient.Z);
            // Inside the bevel the difference spans no kink; at its two ends one sample may cross the clamp.
            if (MathF.Abs(x: difference) > 1f) {
                slopes++;
                Assert.InRange(actual: gradient.X, low: (difference - (0.05f * MathF.Abs(x: difference)) - 2f), high: (difference + (0.05f * MathF.Abs(x: difference)) + 2f));
                TestContext.Current.TestOutputHelper?.WriteLine(message: FormattableString.Invariant($"x {x:F4}  mortar {Evaluate(program: program, output: "roughness", point: point).X:F4}  gradient {gradient.X:F2}  difference {difference:F2}"));
            }
        }
        // Both bevels of the joint carry slope.
        Assert.True(condition: (slopes >= 8), userMessage: $"only {slopes} samples carried slope");
    }

    [Fact]
    public void CrackGradientReadsTheCellularSearchOnce() {
        var program = Compile(body: "roughness = shadeCellBorder(surface.position * 3, uint(5), 0.06)\n albedo = gradient(surface.position, q => shadeCellBorder(q * 3, uint(5), 0.06))");
        var surface = program.Straight.Phases[0];

        // The border's value reads the F2 - F1 channel's gradient op, so the program runs one search.
        Assert.Single(collection: surface, predicate: static i => (i.Op == SdfShadeOp.CellsGradient));
        Assert.Equal(expected: 1u, actual: surface.Single(predicate: static i => (i.Op == SdfShadeOp.CellsGradient)).Immediate);
        Assert.DoesNotContain(collection: surface, filter: static i => (i.Op == SdfShadeOp.Cells));
        Assert.DoesNotContain(collection: surface, filter: static i => (i.Op == SdfShadeOp.Lattice));
    }

    [Fact]
    public void HeightsWithoutDerivativesAreRefused() {
        var compilation = WorldCompileSession.Shared.Compile(source: World(body: "albedo = [1, 1, 1] * gradient(surface.position, q => shape({ type: \"Sphere\" }, q)).x"),
            sourcePath: RepositoryPaths.Resolve(relativePath: "worlds/debug/shade-gradient-laws.puck"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: compilation.Success);
        Assert.Contains(collection: compilation.Diagnostics, filter: static d => (d.Code == "PUCK126"));
    }

    [Fact]
    public void ReliefIsPricedAgainstItsPattern() {
        (string Name, string Height)[] patterns = [
            ("fbm", "0.01 * shadeFbm(q * 4, uint(7))"),
            ("ridged", "0.01 * shadeRidged(q * 4, uint(7))"),
            ("cracks", "-0.004 * shadeCellBorder(q * 3, uint(5), 0.06)"),
            ("bricks", "-0.008 * shadeBrick(q, [0.4, 0.2], 0.025, uint(3)).x"),
        ];

        foreach (var (name, height) in patterns) {
            var value = height.Replace(oldValue: "(q", newValue: "(surface.position", comparisonType: StringComparison.Ordinal);
            var plain = Compile(body: $"roughness = 0.5 + {value}");
            var bumped = Compile(body: $"roughness = 0.5 + {value}\n normal = tilt(surface.normal, gradient(surface.position, q => {height}) * -1, 1.2)");

            Assert.True(condition: (bumped.Costs[0] > plain.Costs[0]));
            TestContext.Current.TestOutputHelper?.WriteLine(message: FormattableString.Invariant(
                $"{name}: plain {plain.Straight.Phases[0].Count} values, work {plain.Costs[0]} interpreted / {plain.Straight.Costs[0]} straight; bumped {bumped.Straight.Phases[0].Count} values, work {bumped.Costs[0]} interpreted / {bumped.Straight.Costs[0]} straight"));
        }
        var flow = Compile(body: "", shade: "flow([1, 0, 0], 4, 2, 8, 0.3, 0.5, 0.1, [0.9, 0.9, 1], 0.2, 0, 7)");

        TestContext.Current.TestOutputHelper?.WriteLine(message: FormattableString.Invariant(
            $"flow: {flow.Straight.Phases[0].Count} values, work {flow.Costs[0]} interpreted / {flow.Straight.Costs[0]} straight"));
    }

    // The surface phase's straight form on the host, for the ops a brick height lowers to, at one point.
    private static System.Numerics.Vector4 Evaluate(SdfShadeProgram program, string output, System.Numerics.Vector3 point) {
        var values = new List<float[]>();
        var phase = program.Straight.Phases[0];

        foreach (var instruction in phase) {
            float[] Arg(int i) => values[instruction.Operands[i].Index];
            var width = SdfShadeOps.Width(type: instruction.Result.Type);
            float[] Each(Func<int, float> lane) => Enumerable.Range(start: 0, count: width).Select(selector: lane).ToArray();
            float Word(uint vector, int lane) => BitConverter.UInt32BitsToSingle(value: program.ReadWord(relativeWord: ((vector * 4) + ((uint)lane))));

            values.Add(item: instruction.Op switch {
                SdfShadeOp.Constant => Each(lane => Word(vector: instruction.Immediate, lane: lane)),
                SdfShadeOp.Parameter => Each(lane => BitConverter.UInt32BitsToSingle(value: program.ParameterWords[((int)(instruction.Immediate * 4)) + lane])),
                SdfShadeOp.Input => SdfShadeOps.Inputs[((int)instruction.Immediate)].Name switch {
                    "surface.position" => [point.X, point.Y, point.Z],
                    "surface.footprint" => [0f],
                    var name => throw new NotSupportedException(message: name),
                },
                SdfShadeOp.Component => [Arg(0)[instruction.Immediate]],
                SdfShadeOp.Vector2 or SdfShadeOp.Vector3 or SdfShadeOp.Vector4 => Each(lane => Arg(lane)[0]),
                SdfShadeOp.Add => Each(lane => (Lane(Arg(0), lane) + Lane(Arg(1), lane))),
                SdfShadeOp.Subtract => Each(lane => (Lane(Arg(0), lane) - Lane(Arg(1), lane))),
                SdfShadeOp.Multiply => Each(lane => (Lane(Arg(0), lane) * Lane(Arg(1), lane))),
                SdfShadeOp.Divide => Each(lane => (Lane(Arg(0), lane) / Lane(Arg(1), lane))),
                SdfShadeOp.Min => Each(lane => MathF.Min(x: Lane(Arg(0), lane), y: Lane(Arg(1), lane))),
                SdfShadeOp.Max => Each(lane => MathF.Max(x: Lane(Arg(0), lane), y: Lane(Arg(1), lane))),
                SdfShadeOp.Negate => Each(lane => -Lane(Arg(0), lane)),
                SdfShadeOp.Floor => Each(lane => MathF.Floor(x: Lane(Arg(0), lane))),
                SdfShadeOp.Less => [((Arg(0)[0] < Arg(1)[0]) ? 1f : 0f)],
                SdfShadeOp.Equal => [((Arg(0)[0] == Arg(1)[0]) ? 1f : 0f)],
                SdfShadeOp.Select => ((Arg(0)[0] != 0f) ? Arg(1) : Arg(2)),
                SdfShadeOp.Saturate => Each(lane => Math.Clamp(value: Lane(Arg(0), lane), min: 0f, max: 1f)),
                SdfShadeOp.Smoothstep => Each(lane => {
                    var t = Math.Clamp(value: ((Lane(Arg(2), lane) - Lane(Arg(0), lane)) / (Lane(Arg(1), lane) - Lane(Arg(0), lane))), min: 0f, max: 1f);

                    return ((t * t) * (3f - (2f * t)));
                }),
                var op => throw new NotSupportedException(message: op.ToString()),
            });
        }
        var result = values[program.Straight.Outputs.Single(predicate: o => ((o.Phase == SdfShadePhase.Surface) && (o.Name == output))).Value.Index];

        return new(Lane(result, 0), Lane(result, 1), Lane(result, 2), 0f);

        static float Lane(float[] value, int lane) => value[Math.Min(val1: lane, val2: (value.Length - 1))];
    }
}
