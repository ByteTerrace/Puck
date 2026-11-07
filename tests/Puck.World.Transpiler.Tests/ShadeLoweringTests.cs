using System.Text.Json;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class ShadeLoweringTests {
    private static string World(string body, string parameters = "", string arguments = "", string before = "") => $$"""
        schema: "puck.world.definition.v1"
        documentId: "shade-laws"
        {{before}}
        shade template sample({{parameters}}) { surface { {{body}} } }
        prototypes { prototype "p" { document {
          palette [ { color: "#808080", shade: sample({{arguments}}) } ]
          shape Box "box" { scale [1, 1, 1] }
        } } }
        """;
    private static SdfShadeProgram Compile(string text) {
        var compilation = WorldCompiler.Compile(text, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport());
        var shade = compilation.Json!["prototypes"]![0]!["document"]!["palette"]![0]!["shade"]!;

        return SdfShadeProgram.Compile(definition: shade.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)!);
    }

    [Fact]
    public void RuntimeValuesKeepIdentityAndStructuralValuesChangeIt() {
        string Source(int seed, string color, int octaves) => World(
            "let n = surface.position\n for i in range(0, octaves) { let n = n * 2 }\n albedo = deposit * lattice(n, seed)",
            "seed, deposit, structural octaves = 2", $"{seed}, \"{color}\", {octaves}");
        var a = Compile(text: Source(color: "#888888", octaves: 2, seed: 7));
        var b = Compile(text: Source(color: "#556677", octaves: 2, seed: 91));
        var c = Compile(text: Source(color: "#888888", octaves: 3, seed: 7));

        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(a.Words.ToArray(), b.Words.ToArray());
        Assert.NotEqual(a.ParameterWords.ToArray(), b.ParameterWords.ToArray());
        Assert.NotEqual(a.Identity, c.Identity);
    }
    [InlineData("roughness = surface.noSuchMember", "", "", "", "PUCK126")]
    [InlineData("roughness = missing", "", "", "", "PUCK128")]
    [InlineData("for i in range(0, n) { roughness = i }", "n", "3", "", "PUCK130")]
    [InlineData("roughness = f(1)", "", "", "function f(x) = f(x)", "PUCK048")]
    [InlineData("roughness = lanes.slot[n]", "n", "2", "", "PUCK130")]
    [InlineData("roughness = shape({ type: Sphere, x: n }, surface.position)", "n", "2", "", "PUCK130")]
    [Theory]
    public void RefusalsHaveCompilerCodes(string body, string parameters, string arguments, string before, string code) {
        var compiled = WorldCompiler.Compile(World(arguments: arguments, before: before, body: body, parameters: parameters), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(collection: compiled.Diagnostics, filter: d => (d.Code == code));
    }
    [Fact]
    public void ReadsFollowOnlyLiveOpsAndPhaseInputsAreRestricted() {
        var program = Compile(text: World("roughness = clock\n roughness = lanes.world2\n albedo = material.albedo"));

        Assert.Equal(SdfShadeReads.World2, program.Reads);
        var bad = WorldCompiler.Compile(World("roughness = light.visibility"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(collection: bad.Diagnostics, filter: d => (d.Code == "PUCK126"));
    }
    [Fact]
    public void StructuralSelectorsAndFunctionParametersUseTheSameBindings() {
        var named = Compile(text: World("roughness = lanes.slot2"));
        var indexed = Compile(text: World("roughness = lanes.slot[lane]", "structural lane", "2"));

        Assert.Equal(named.Identity, indexed.Identity);
        var throughLet = Compile(text: World("let lane = 1 + 1\n roughness = lanes.slot[lane]"));

        Assert.Equal(named.Identity, throughLet.Identity);
        var direct = Compile(text: World("roughness = lattice(surface.position, seed)", "seed", "31"));
        var function = Compile(text: World(arguments: "31", before: "function noise(p, s) = lattice(p, s)", body: "roughness = noise(surface.position, seed)", parameters: "seed"));

        Assert.Equal(direct.Identity, function.Identity);
    }
    [Fact]
    public void EveryInstructionAndShapeOperandStaysInsideItsTypedSlice() {
        var program = Compile(text: World("roughness = shape({ type: Sphere, x: 2 }, surface.position) + lanes.slot0"));
        var length = ((uint)(program.Words.Length / 4));

        foreach (var phase in program.Phases) {
            var types = new SdfShadeType?[SdfShadeProgram.MaxRegisters];

            foreach (var op in phase) {
                Assert.All(op.Operands, operand => {
                    Assert.InRange(operand.Index, 0, (SdfShadeProgram.MaxRegisters - 1));
                    Assert.Equal(types[operand.Index], operand.Type);
                });
                types[op.Result.Index] = op.Result.Type;
                if (op.Op is SdfShadeOp.Constant or SdfShadeOp.Shape) {
                    Assert.True(condition: SdfShadeProgram.ContainsRange(length, op.Immediate, ((op.Op == SdfShadeOp.Shape) ? 7u : 1u)));
                }
            }
        }
        var undefined = new SdfShadeDefinition("undefined", [], [new(SdfShadePhase.Surface, "roughness", new("value", Name: "r999"))]);

        Assert.Equal("PUCK128", Assert.Throws<SdfShadeException>(testCode: () => SdfShadeProgram.Compile(definition: undefined)).Code);
        var invalid = new SdfShadeDefinition("invalid", [], [new(SdfShadePhase.Surface, "roughness", new("component", [new("input", Name: "surface.position")], Name: "3"))]);

        Assert.Equal("PUCK127", Assert.Throws<SdfShadeException>(testCode: () => SdfShadeProgram.Compile(definition: invalid)).Code);
    }
    [Fact]
    public void RegistersAreAllocatedByLivenessAndRematerializedWithoutChangingValues() {
        var chain = "clock";

        for (var i = 0; (i < 40); i++) { chain = $"sin({chain})"; }
        var admitted = Compile(text: World($"roughness = {chain}"));

        Assert.True(condition: (admitted.Phases[0].Count > SdfShadeProgram.MaxRegisters));
        Assert.All(admitted.Phases[0], instruction => Assert.InRange(instruction.Result.Index, 0, (SdfShadeProgram.MaxRegisters - 1)));
        Assert.Equal(admitted.Identity, SdfShadeProgram.Compile(definition: admitted.Definition).Identity);
        var values = string.Join(separator: "\n", values: Enumerable.Range(count: 24, start: 0).Select(selector: i => $"let a{i} = sin(clock + {i})"));
        var outputs = ("\nalbedo [a0,a1,a2]\nfill [a3,a4,a5]\nnormal [a6,a7,a8]\nroughness = " + string.Join(separator: " + ", values: Enumerable.Range(count: 24, start: 0).Select(selector: i => $"a{i}")));
        var rematerialized = Compile(World(values + outputs));
        Assert.Equal(rematerialized.Identity, SdfShadeProgram.Compile(rematerialized.Definition).Identity);
        var registers = new System.Numerics.Vector4[SdfShadeProgram.MaxRegisters];
        var actual = new Dictionary<uint, System.Numerics.Vector4>();
        var clock = 0.37f;
        foreach (var instruction in rematerialized.Phases[0]) {
            var operands = instruction.Operands.Select(o => registers[o.Index]).ToArray();
            var value = instruction.Op switch {
                SdfShadeOp.Constant => new System.Numerics.Vector4(BitConverter.UInt32BitsToSingle(rematerialized.ReadWord(instruction.Immediate * 4))),
                SdfShadeOp.Input => new System.Numerics.Vector4(clock),
                SdfShadeOp.Add => operands[0] + operands[1],
                SdfShadeOp.Sin => new System.Numerics.Vector4(MathF.Sin(operands[0].X)),
                SdfShadeOp.Vector3 => new System.Numerics.Vector4(operands[0].X, operands[1].X, operands[2].X, 0),
                _ => throw new InvalidOperationException(instruction.Op.ToString()),
            };
            registers[instruction.Result.Index] = value;
            for (uint output = 0; output < 11; output++) {
                if ((instruction.OutputMask & (1u << (int)output)) != 0) { actual[output] = value; }
            }
        }
        Assert.Equal(Enumerable.Range(0, 24).Sum(i => MathF.Sin(clock + i)), actual[1].X, 4);
        Assert.Equal(new System.Numerics.Vector4(MathF.Sin(clock), MathF.Sin(clock + 1), MathF.Sin(clock + 2), 0), actual[0]);
        var gradient = Compile(text: World("roughness = latticeGradient(surface.position, seed).x", "seed", "7"));

        Assert.Contains(collection: gradient.Phases[0], filter: instruction => (instruction.Op == SdfShadeOp.LatticeGradient));
    }
    [Fact]
    public void RangeAndWorkArithmeticAreClosedAtEdges() {
        foreach (var length in new uint[] { 0, 1, 16, uint.MaxValue }) {
            foreach (var offset in new uint[] { 0, 1, 16, uint.MaxValue }) {
                foreach (var count in new uint[] { 0, 1, 16, uint.MaxValue }) {
                    Assert.Equal(((((ulong)offset) + count) <= length), SdfShadeProgram.ContainsRange(length: count, offset: offset, sliceLength: length));
                }
            }
        }
        var program = Compile(text: World("roughness = material.roughness * 0.5"));
        var previous = 0UL;

        for (uint lights = 0; (lights <= SdfShadeProgram.MaxLights); lights++) {
            var work = program.PixelWork(lights: lights);

            Assert.True(condition: (work >= previous));
            Assert.True(condition: (checked((64UL * work)) <= SdfShadeProgram.MaxWork));
            previous = work;
        }
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => program.PixelWork(lights: uint.MaxValue));
        Assert.Equal(0u, program.ReadWord(relativeWord: uint.MaxValue));
        Assert.True(condition: (SdfShadeProgram.MaxOps <= SdfShadeProgram.MaxWork));
    }
    [Fact]
    public void CapChargesHelperWorkAndSweepStrands() {
        var compiled = WorldCompiler.Compile(World(
            "let n = surface.position\n for i in range(0, 200) { let n = n * lattice(n, uint(7)) }\n albedo = n"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(collection: compiled.Diagnostics, filter: d => (d.Code == "PUCK129"));
        var curve = new SdfSweepParameters(new(x: 0, y: 0, z: 0), new(x: 0, y: 1, z: 0), new(x: 1, y: 2, z: 0), 0.1f, 0.2f, 0.1f, 1, 1, 0.1f);
        var one = SdfShadeCost.Shape(descriptor: new(SdfSolidPrimitive.Sweep, Curve: curve));
        var two = SdfShadeCost.Shape(descriptor: new(SdfSolidPrimitive.Sweep, Curve: curve with { Strands = 2 }));

        Assert.True(condition: (two > one));
        Assert.True(condition: (one >= ((Puck.SignedDistance.Queries.SdfFieldEvaluator.SweepSamples + 3u) + (2u * Puck.SignedDistance.Queries.SdfFieldEvaluator.SweepRounds))));
    }

    [Fact]
    public void RuntimeParametersHaveStableTypedViewsThroughBindingsAndFunctions() {
        var mixed = Compile(World("roughness = seed + lattice(surface.position, seed)", "seed", "7"));
        Assert.Contains(SdfShadeType.Float, mixed.ParameterLayout);
        Assert.Contains(SdfShadeType.UInt, mixed.ParameterLayout);
        Assert.Equal(mixed.Identity, SdfShadeProgram.Compile(mixed.Definition).Identity);
        var direct = Compile(World("roughness = lattice(surface.position, seed)", "seed", "7"));
        foreach (var spelling in new[] { "select(clock, seed, seed)", "clock ? seed : seed", "identity(seed)" }) {
            var through = Compile(World($"roughness = lattice(surface.position, {spelling})", "seed", "7", "function identity(x) = x"));
            Assert.All(through.ParameterLayout, type => Assert.Equal(SdfShadeType.UInt, type));
        }
        var hash = Compile(World("roughness = select(hash(seed).x == uint(0), 0.2, 0.8)", "seed", "[1,2,3]"));
        Assert.Equal([SdfShadeType.UInt3], hash.ParameterLayout);
        Assert.Equal([1u, 2u, 3u, 0u], hash.ParameterWords.ToArray());
        Assert.Equal([SdfShadeType.UInt], direct.ParameterLayout);
        var shadowed = Compile(World("roughness = identity(seed)", "seed", "7", "function identity(seed) = seed"));
        Assert.Equal([SdfShadeType.Float], shadowed.ParameterLayout);
        var loop = Compile(World("let n = clock\n for i in range(0, 3) { let n = n + i }\n roughness = n"));
        var unrolled = Compile(World("roughness = ((clock + 0) + 1) + 2"));
        Assert.Equal(unrolled.Identity, loop.Identity);
        var equalBits = Compile(World("roughness = uint(2143289344) == uint(2143289344)"));
        var one = Compile(World("roughness = 1"));
        Assert.Equal(one.Identity, equalBits.Identity);
    }

    [Fact]
    public void RawDefinitionsRefuseMissingMembersAtAdmission() {
        var definitions = new SdfShadeDefinition[] {
            null!, new("null", null!, []), new("null", [], null!), new("null", [null!], []),
            new("null", [new("p", SdfShadeType.Float, null!)], []),
            new("null", [], [null!]), new("null", [], [], [null!]),
            new("null", [], [new(SdfShadePhase.Surface, "roughness", null!)]),
            new("null", [], [new(SdfShadePhase.Surface, "roughness", new("sin", [null!]))]),
        };
        foreach (var definition in definitions) {
            Assert.Equal("PUCK127", Assert.Throws<SdfShadeException>(() => SdfShadeProgram.Compile(definition)).Code);
        }
    }

    [InlineData("rounding: -1")]
    [InlineData("rounding: 100")]
    [InlineData("chamfer: -1")]
    [Theory]
    public void StructuralShapesUsePrimitiveAdmission(string controls) {
        var result = WorldCompiler.Compile(World($"roughness = shape({{ type: Box, {controls} }}, surface.position)"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.Diagnostics, d => d.Code == "PUCK127");
    }

    [Fact]
    public void InstructionCapacityAndDispatchWorkAreIndependent() {
        var chain = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"let n{i} = sin({(i == 0 ? "clock" : $"n{i - 1}")})"));
        var program = Compile(World(chain + "\nroughness = n99"));
        Assert.True(program.Phases[0].Count > 63);
        var curve = new SdfSweepParameters(new(0, 0, 0), new(0, 1, 0), new(1, 2, 0), 0.1f, 0.2f, 0.1f, SdfProgramBuilder.MaxSweepStrands, 1, 0.1f);
        var sweep = SdfShadeProgram.Compile(new("sweep", [], [new(SdfShadePhase.Surface, "roughness", new("shape", [new("input", Name: "surface.position")], Shape: new(SdfSolidPrimitive.Sweep, Curve: curve)))]));
        Assert.Equal(2, sweep.Phases[0].Count);
        Assert.True(sweep.Costs[0] > SdfShadeProgram.MaxOps);
        Assert.True(sweep.PixelWork(SdfShadeProgram.MaxLights) * 128UL <= SdfShadeProgram.MaxWork);
        var shapes = Enumerable.Range(0, 4).Select(i => new SdfShadeExpression("shape", [new("+", [
            new("input", Name: "light.position"), new("constant", Type: SdfShadeType.Float3, Bits: [BitConverter.SingleToUInt32Bits(i), 0, 0])])],
            Shape: new(SdfSolidPrimitive.Sweep, Curve: curve))).ToArray();
        var sum = shapes.Aggregate((left, right) => new("+", [left, right]));
        var refused = Assert.Throws<SdfShadeException>(() => SdfShadeProgram.Compile(new("heavy light", [], [new(SdfShadePhase.Light, "attenuation", sum)])));
        Assert.Equal("PUCK129", refused.Code);
        Assert.Contains("dispatch bound", refused.Message);
    }
}
