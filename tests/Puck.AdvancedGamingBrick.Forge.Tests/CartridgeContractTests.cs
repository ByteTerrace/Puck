using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeContractTests {
    private static CartridgeStatement Map() => new(
        Kind: "map",
        Row: CartridgeExpressions.Of(constant: 0),
        Column: CartridgeExpressions.Of(constant: 0),
        Tile: CartridgeExpressions.Of(constant: 0)
    );
    private static int Run(CartridgeDocument document, string name, bool wide = false) {
        ICartridgeCompiler compiler = ((document.Target == "cgb")
            ? new HgbCartridgeCompiler()
            : new AgbCartridgeCompiler()
        );
        var compiled = compiler.Compile(document: document);
        var address = compiled.Variables[name];

        if (document.Target == "cgb") {
            using var probe = new VerifyMachineDriver(
                compiled.Rom,
                "contract"
            );

            probe.RunFrames(
                buttons: JoypadButtons.None,
                frames: 12
            );
            return (probe.Read(address: ((ushort)address)) + (wide
                ? (probe.Read(address: ((ushort)(address + 1))) * 256)
                : 0));
        }
        using var advanced = new AgbVerifyMachineDriver(
            compiled.Rom,
            "contract"
        );

        advanced.RunFrames(
            frames: 12,
            keys: AgbKeys.None
        );
        return (advanced.ReadByte(address: address) + (wide
            ? (advanced.ReadByte(address: (address + 1)) * 256)
            : 0));
    }
    private static CartridgeDocument Seed(string target) => CartridgeDocuments.Create(
        target: target,
        title: "CONTRACT"
    ) with {
        Variables = [new(
            Initial: 257,
            Max: 65535,
            Name: "wide"
        ), new(
            "out",
            0
        ), new(
            "scene",
            0
        )],
        Arrays = [new(
            Initial: [10, 20, 30],
            Name: "cells"
        )],
    };
    private static CartridgeStatement Set(string name, string expression) => new(
        Kind: "set",
        Target: new(name),
        Value: ValueExpression.Parse(text: expression)
    );

    [InlineData("256 / 2")]
    [InlineData("sign(256)")]
    [InlineData("wide")]
    [Theory]
    public void ByteAssignmentsRefuseWideOperands(string expression) {
        var document = Seed(target: "agb") with { Rules = [new(
                "write",
                [Set(
                        expression: expression,
                        name: "out"
                    )]
            )] };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".value"
            )
        );
    }
    public static IEnumerable<object[]> Comparisons() {
        foreach (var target in new[] { "cgb", "agb" }) {
            foreach (var comparison in Enum.GetValues<ActionStateComparison>()) {
                foreach (var right in new[] { 1, 256, 257, 258, 513 }) {
                    yield return [target, comparison, right];
                }
            }
        }
    }
    [Fact]
    public void LoadingStateInvalidatesAnInferredPhasePartition() {
        var redraw = new CartridgeStatement(
            Kind: "repeat",
            Count: 16,
            Index: "out",
            Body: [Map()]
        );
        var document = Seed(target: "agb") with {
            Save = new(
            Arrays: [],
            Variables: ["scene"],
            Version: 1
        ),
            Rules = [new(
                "first",
                [redraw, new(Kind: "load")],
                CartridgeExpressions.Gate(
                    CartridgeExpressions.Of("scene"),
                    ActionStateComparison.Equal,
                    CartridgeExpressions.Of(constant: 0)
                )
            ),
                new(
                "second",
                [redraw],
                CartridgeExpressions.Gate(
                    CartridgeExpressions.Of("scene"),
                    ActionStateComparison.Equal,
                    CartridgeExpressions.Of(constant: 1)
                )
            )],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "against a queue of"
            )
        );
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document with { Scene = "scene" }));
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void MixedWidthSlotsPreserveTheirNeighbours(string target) {
        var document = Seed(target: target) with {
            Variables = [new(
                "pad",
                17
            ), new(
                Initial: 257,
                Max: 65535,
                Name: "wide"
            ), new(
                "out",
                19
            )],
            Rules = [new(
                "write",
                [Set(
                        expression: "258",
                        name: "wide"
                    )]
            )],
        };

        Assert.Equal(
            258,
            Run(
                document,
                "wide",
                wide: true
            )
        );
        Assert.Equal(
            17,
            Run(
                document,
                "pad"
            )
        );
        Assert.Equal(
            19,
            Run(
                document,
                "out"
            )
        );
    }
    [Fact]
    public void QueueBoundsCannotOverflowThroughNestedLoops() {
        var step = Map();
        var variables = new List<CartridgeVariable>();

        for (var index = 0; (index < 4); ++index) {
            var name = $"i{index}";

            variables.Add(item: new(
                name,
                0
            ));
            step = new(
                Kind: "repeat",
                Count: 255,
                Index: name,
                Body: [step]
            );
        }
        var document = Seed(target: "agb") with { Variables = [.. variables], Rules = [new(
                "overflow",
                [step]
            )] };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "against a queue of"
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void SaveRestoresTheWholeWideSlot(string target) {
        var document = Seed(target: target) with {
            Save = new(
            Arrays: [],
            Variables: ["wide"],
            Version: 1
        ),
            Rules = [new(
                "restore",
                [Set(
                        expression: "257",
                        name: "wide"
                    ), new(Kind: "save"), Set(
                        expression: "512",
                        name: "wide"
                    ), new(Kind: "load")]
            )],
        };

        Assert.Equal(
            257,
            Run(
                document,
                "wide",
                wide: true
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void SceneReadsInsideIndicesUseTheFrameSnapshot(string target) {
        var document = Seed(target: target) with {
            Scene = "scene",
            Rules = [new(
                "advance",
                [Set(
                        expression: "1",
                        name: "scene"
                    )]
            ), new(
                "observe",
                [Set(
                        expression: "1",
                        name: "out"
                    )],
                CartridgeExpressions.Gate(
                    ValueExpression.Parse(text: "cells[scene]"),
                    ActionStateComparison.Equal,
                    CartridgeExpressions.Of(constant: 10)
                )
            )],
        };

        Assert.Equal(
            1,
            Run(
                document,
                "out"
            )
        );
    }
    [MemberData(nameof(Comparisons))]
    [Theory]
    public void WideComparisonsMatchUnsignedIntegerOrder(string target, ActionStateComparison comparison, int right) {
        var expected = comparison switch {
            ActionStateComparison.Equal => (257 == right),
            ActionStateComparison.NotEqual => (257 != right),
            ActionStateComparison.Less => (257 < right),
            ActionStateComparison.LessOrEqual => (257 <= right),
            ActionStateComparison.Greater => (257 > right),
            ActionStateComparison.GreaterOrEqual => (257 >= right),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(comparison)),
        };
        var document = Seed(target: target) with {
            Rules = [new(
                "check",
                [Set(
                        expression: "1",
                        name: "out"
                    )],
                CartridgeExpressions.Gate(
                    CartridgeExpressions.Of("wide"),
                    comparison,
                    CartridgeExpressions.Of(constant: right)
                )
            )],
        };

        Assert.Equal(
            (expected
            ? 1
            : 0),
            Run(
                document,
                "out"
            )
        );
    }
}
