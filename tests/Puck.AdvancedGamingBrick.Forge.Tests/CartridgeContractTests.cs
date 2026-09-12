using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeContractTests {
    public static IEnumerable<object[]> Comparisons() {
        foreach (var target in new[] { "cgb", "agb" }) {
            foreach (var comparison in Enum.GetValues<ActionStateComparison>()) {
                foreach (var right in new[] { 1, 256, 257, 258, 513 }) {
                    yield return [target, comparison, right];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void WideComparisonsMatchUnsignedIntegerOrder(string target, ActionStateComparison comparison, int right) {
        var expected = comparison switch {
            ActionStateComparison.Equal => 257 == right,
            ActionStateComparison.NotEqual => 257 != right,
            ActionStateComparison.Less => 257 < right,
            ActionStateComparison.LessOrEqual => 257 <= right,
            ActionStateComparison.Greater => 257 > right,
            ActionStateComparison.GreaterOrEqual => 257 >= right,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };
        var document = Seed(target) with {
            Rules = [new("check", [Set("out", "1")], CartridgeExpressions.Gate(
                CartridgeExpressions.Of("wide"), comparison, CartridgeExpressions.Of(right)))],
        };
        Assert.Equal(expected ? 1 : 0, Run(document, "out"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void MixedWidthSlotsPreserveTheirNeighbours(string target) {
        var document = Seed(target) with {
            Variables = [new("pad", 17), new("wide", 257, 65535), new("out", 19)],
            Rules = [new("write", [Set("wide", "258")])],
        };
        Assert.Equal(258, Run(document, "wide", wide: true));
        Assert.Equal(17, Run(document, "pad"));
        Assert.Equal(19, Run(document, "out"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void SaveRestoresTheWholeWideSlot(string target) {
        var document = Seed(target) with {
            Save = new(1, ["wide"], []),
            Rules = [new("restore", [Set("wide", "257"), new(Kind: "save"), Set("wide", "512"), new(Kind: "load")])],
        };
        Assert.Equal(257, Run(document, "wide", wide: true));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void SceneReadsInsideIndicesUseTheFrameSnapshot(string target) {
        var document = Seed(target) with {
            Scene = "scene",
            Rules = [new("advance", [Set("scene", "1")]), new("observe", [Set("out", "1")],
                CartridgeExpressions.Gate(ValueExpression.Parse("cells[scene]"), ActionStateComparison.Equal, CartridgeExpressions.Of(10)))],
        };
        Assert.Equal(1, Run(document, "out"));
    }

    [Theory]
    [InlineData("256 / 2")]
    [InlineData("sign(256)")]
    [InlineData("wide")]
    public void ByteAssignmentsRefuseWideOperands(string expression) {
        var document = Seed("agb") with { Rules = [new("write", [Set("out", expression)])] };
        Assert.Contains(CartridgeDocuments.Validate(document), error => error.Path.EndsWith(".value", StringComparison.Ordinal));
    }

    [Fact]
    public void QueueBoundsCannotOverflowThroughNestedLoops() {
        var step = Map();
        var variables = new List<CartridgeVariable>();
        for (var index = 0; index < 4; ++index) {
            var name = $"i{index}";
            variables.Add(new(name, 0));
            step = new(Kind: "repeat", Count: 255, Index: name, Body: [step]);
        }
        var document = Seed("agb") with { Variables = [.. variables], Rules = [new("overflow", [step])] };
        Assert.Contains(CartridgeDocuments.Validate(document), error => error.Message.Contains("against a queue of", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadingStateInvalidatesAnInferredPhasePartition() {
        var redraw = new CartridgeStatement(Kind: "repeat", Count: 16, Index: "out", Body: [Map()]);
        var document = Seed("agb") with {
            Save = new(1, ["scene"], []),
            Rules = [new("first", [redraw, new(Kind: "load")], CartridgeExpressions.Gate(CartridgeExpressions.Of("scene"), ActionStateComparison.Equal, CartridgeExpressions.Of(0))),
                new("second", [redraw], CartridgeExpressions.Gate(CartridgeExpressions.Of("scene"), ActionStateComparison.Equal, CartridgeExpressions.Of(1)))],
        };
        Assert.Contains(CartridgeDocuments.Validate(document), error => error.Message.Contains("against a queue of", StringComparison.Ordinal));
        Assert.Empty(CartridgeDocuments.Validate(document with { Scene = "scene" }));
    }

    private static CartridgeStatement Map() => new(Kind: "map", Row: CartridgeExpressions.Of(0), Column: CartridgeExpressions.Of(0), Tile: CartridgeExpressions.Of(0));

    private static CartridgeStatement Set(string name, string expression) => new(Kind: "set", Target: new(name), Value: ValueExpression.Parse(expression));

    private static CartridgeDocument Seed(string target) => CartridgeDocuments.Create(target, "CONTRACT") with {
        Variables = [new("wide", 257, 65535), new("out", 0), new("scene", 0)],
        Arrays = [new("cells", [10, 20, 30])],
    };

    private static int Run(CartridgeDocument document, string name, bool wide = false) {
        ICartridgeCompiler compiler = document.Target == "cgb" ? new HgbCartridgeCompiler() : new AgbCartridgeCompiler();
        var compiled = compiler.Compile(document);
        var address = compiled.Variables[name];
        if (document.Target == "cgb") {
            using var probe = new VerifyMachineDriver(compiled.Rom, "contract");
            probe.RunFrames(JoypadButtons.None, 12);
            return probe.Read((ushort)address) + (wide ? probe.Read((ushort)(address + 1)) * 256 : 0);
        }
        using var advanced = new AgbVerifyMachineDriver(compiled.Rom, "contract");
        advanced.RunFrames(AgbKeys.None, 12);
        return advanced.ReadByte(address) + (wide ? advanced.ReadByte(address + 1) * 256 : 0);
    }
}
