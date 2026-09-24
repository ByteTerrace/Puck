using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeContractTests {
    private static CartridgeStatement Map() => new(
        Kind: "map",
        Row: CartridgeExpressions.Of(constant: 0),
        Column: CartridgeExpressions.Of(constant: 0),
        Tile: CartridgeExpressions.Of(constant: 0)
    );
    // Runs the document once and reads each named slot, two bytes little-endian where the document declares it wide.
    private static int[] Run(CartridgeDocument document, params string[] names) {
        using var probe = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "contract"
        );

        return [.. names.Select(selector: name => ((document.Variables.Single(predicate: variable => (variable.Name == name)).Width == 2)
            ? probe.ReadWide(variable: name)
            : probe.Read(variable: name)))];
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
        Value: ExpressionProgram.Parse(text: expression)
    );

    [InlineData("256 / 2", "does not fit a byte expression")]
    [InlineData("sign(256)", "does not fit a byte expression")]
    [InlineData("wide", "is a wide slot")]
    [Theory]
    public void ByteAssignmentsRefuseWideOperands(string expression, string fragment) => new CartridgeRefusal(
        Name: expression,
        Document: Seed(target: "agb") with {
            Rules = [new(
                "write",
                [Set(
                        expression: expression,
                        name: "out"
                    )]
            )],
        },
        Path: "rules[0].body[0].value",
        Fragment: fragment
    ).Holds();
    public static IEnumerable<object[]> Comparisons() {
        foreach (var target in new[] { "cgb", "agb" }) {
            foreach (var comparison in ExpressionComparisons.All) {
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
                    ExpressionOp.Equal,
                    CartridgeExpressions.Of(constant: 0)
                )
            ),
                new(
                "second",
                [redraw],
                CartridgeExpressions.Gate(
                    CartridgeExpressions.Of("scene"),
                    ExpressionOp.Equal,
                    CartridgeExpressions.Of(constant: 1)
                )
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "against a queue of",
            Name: "a load beside an inferred partition",
            Path: "rules"
        ).Holds();
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
            [258, 17, 19],
            Run(
                document,
                "wide",
                "pad",
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
        var document = Seed(target: "agb") with {
            Variables = [.. variables],
            Rules = [new(
                "overflow",
                [step]
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "against a queue of",
            Name: "nested loops past the queue",
            Path: "rules"
        ).Holds();
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
                "wide"
            )[0]
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
                    ExpressionProgram.Parse(text: "cells[scene]"),
                    ExpressionOp.Equal,
                    CartridgeExpressions.Of(constant: 10)
                )
            )],
        };

        Assert.Equal(
            1,
            Run(
                document,
                "out"
            )[0]
        );
    }
    [MemberData(nameof(Comparisons))]
    [Theory]
    public void WideComparisonsMatchUnsignedIntegerOrder(string target, ExpressionOp comparison, int right) {
        var expected = comparison switch {
            ExpressionOp.Equal => (257 == right),
            ExpressionOp.NotEqual => (257 != right),
            ExpressionOp.Less => (257 < right),
            ExpressionOp.LessOrEqual => (257 <= right),
            ExpressionOp.Greater => (257 > right),
            ExpressionOp.GreaterOrEqual => (257 >= right),
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
            )[0]
        );
    }
}
