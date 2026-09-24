using System.Text;
using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeCompilerTests {
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AuthoredRulesExecuteOnBothNativeMachines(string target) {
        var draft = new CartridgeDraft(document: CartridgeDocuments.Create(
            target: target,
            title: "INPUT"
        ));

        draft.Set(
            json: """[{"name":"x","initial":255},{"name":"released","initial":0},{"name":"compared","initial":0}]""",
            pointer: "/variables"
        );
        draft.Set(
            json: """
            [
              {"name":"press","when":{"$type":"compareValue","left":"$key:right:pressed","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"x"},"operation":"Add","value":"2"}]},
              {"name":"release","when":{"$type":"compareValue","left":"$key:right:released","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"released"},"operation":"Add","value":"1"}]},
              {"name":"compare","when":{"$type":"compareValue","left":"x","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"compared"},"value":"42"}]}
            ]
            """,
            pointer: "/rules"
        );
        draft.Set(
            json: """{"name":"solid","pixels":["11111111","11111111","11111111","11111111","11111111","11111111","11111111","11111111"]}""",
            pointer: "/tiles/-"
        );
        draft.Set(
            json: """{"name":"cursor","tile":"1","x":"x","y":"24","visible":"1"}""",
            pointer: "/sprites/-"
        );
        var doc = draft.Check();
        var compiler = CartridgeProbe.Compiler(target: target);
        var result = compiler.Compile(document: doc);

        Assert.Equal(
            expected: result.Rom,
            actual: compiler.Compile(document: CartridgeDocuments.Parse(utf8: CartridgeDocuments.Canonicalize(document: doc).Bytes)).Rom
        );
        using var machine = new CartridgeProbe(
            label: "document",
            result: result
        );

        machine.Run(frames: 12);
        Assert.Equal(
            expected: 255,
            actual: machine.Read(variable: "x")
        );
        machine.Run(
            frames: 5,
            keys: AgbKeys.Right
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(variable: "x")
        );
        Assert.Equal(
            expected: 42,
            actual: machine.Read(variable: "compared")
        );
        machine.Run(frames: 5);
        Assert.Equal(
            expected: 1,
            actual: machine.Read(variable: "released")
        );
        var spriteX = ((target == "agb")
            ? 0x07000002u
            : 0xFE01u
        );

        Assert.Equal(
            expected: ((target == "agb")
            ? 1
            : 9),
            actual: machine.Read(address: spriteX)
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: ((target == "agb")
            ? 0x07000004u
            : 0xFE02u))
        );
        Assert.Equal(
            expected: ((target == "agb")
            ? 0x11
            : 0xFF),
            actual: machine.Read(address: ((target == "agb")
            ? 0x06000020u
            : 0x8010u))
        );
        Assert.NotEqual(
            expected: machine.Pixel(
                x: 0,
                y: 0
            ),
            actual: machine.Pixel(
                x: 1,
                y: 24
            )
        );
    }
    [Fact]
    public void ByteOperationsUseCurrentVariablesAndWrapOnBothTargets() {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = CartridgeDocuments.Create(
                target: target,
                title: "OPERATIONS"
            ) with {
                Variables = [new CartridgeVariable(
                    Name: "add",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "sub",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "and",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "or",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "xor",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "done",
                    Initial: 0
                )],
                Rules = [new CartridgeRule(
                    Name: "once",
                    When: CartridgeExpressions.Gate(
                        left: CartridgeExpressions.Of(state: "done"),
                        comparison: ExpressionOp.Equal,
                        right: CartridgeExpressions.Of(constant: 0)
                    ),
                    Body: [
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "add"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 255)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "add"),
                            Operation: ExpressionOp.Add,
                            Value: CartridgeExpressions.Of(constant: 2)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "sub"),
                            Operation: ExpressionOp.Subtract,
                            Value: CartridgeExpressions.Of(state: "add")
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "and"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 240)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "and"),
                            Operation: ExpressionOp.BitAnd,
                            Value: CartridgeExpressions.Of(constant: 60)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "or"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 240)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "or"),
                            Operation: ExpressionOp.BitOr,
                            Value: CartridgeExpressions.Of(constant: 60)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "xor"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 240)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "xor"),
                            Operation: ExpressionOp.BitXor,
                            Value: CartridgeExpressions.Of(constant: 60)
                        ),
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "done"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 1)
                        ),
                ]
                )],
            };
            using var machine = CartridgeProbe.Boot(
                document: document,
                frames: 12,
                label: "document"
            );

            Assert.Equal(
                expected: 1,
                actual: machine.Read(variable: "add")
            );
            Assert.Equal(
                expected: 255,
                actual: machine.Read(variable: "sub")
            );
            Assert.Equal(
                expected: 48,
                actual: machine.Read(variable: "and")
            );
            Assert.Equal(
                expected: 252,
                actual: machine.Read(variable: "or")
            );
            Assert.Equal(
                expected: 204,
                actual: machine.Read(variable: "xor")
            );
        }
    }
    [InlineData(ExpressionOp.Equal, 128, 128, true)]
    [InlineData(ExpressionOp.Equal, 128, 127, false)]
    [InlineData(ExpressionOp.NotEqual, 128, 128, false)]
    [InlineData(ExpressionOp.NotEqual, 128, 127, true)]
    [InlineData(ExpressionOp.Less, 127, 128, true)]
    [InlineData(ExpressionOp.Less, 128, 128, false)]
    [InlineData(ExpressionOp.LessOrEqual, 128, 128, true)]
    [InlineData(ExpressionOp.LessOrEqual, 129, 128, false)]
    [InlineData(ExpressionOp.Greater, 255, 128, true)]
    [InlineData(ExpressionOp.Greater, 128, 128, false)]
    [InlineData(ExpressionOp.GreaterOrEqual, 127, 128, false)]
    [InlineData(ExpressionOp.GreaterOrEqual, 128, 128, true)]
    [Theory]
    public void ComparisonsAreUnsignedAndAgree(ExpressionOp comparison, int left, int right, bool matches) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = CartridgeDocuments.Create(
                target: target,
                title: "COMPARE"
            ) with {
                Variables = [new CartridgeVariable(
                    Name: "result",
                    Initial: 0
                )],
                Rules = [new CartridgeRule(
                    Name: "rule",
                    When: CartridgeExpressions.Gate(
                        left: CartridgeExpressions.Of(constant: left),
                        comparison: comparison,
                        right: CartridgeExpressions.Of(constant: right)
                    ),
                    Body: [new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "result"),
                            Operation: null,
                            Value: CartridgeExpressions.Of(constant: 1)
                        )]
                )],
            };
            using var machine = CartridgeProbe.Boot(
                document: document,
                frames: 12,
                label: "document"
            );

            Assert.Equal(
                expected: (matches
                ? 1
                : 0),
                actual: machine.Read(variable: "result")
            );
        }
    }
    [Fact]
    public void DraftEditsAreAtomicAndValidationNamesBadReferences() {
        var draft = new CartridgeDraft(document: CartridgeDocuments.Create(
            target: "agb",
            title: "DRAFT"
        ));
        var before = draft.Show();

        Assert.Throws<ArgumentException>(testCode: () => draft.Set(
            json: "{}",
            pointer: "/tiles/99"
        ));
        Assert.Equal(
            expected: before,
            actual: draft.Show()
        );
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => draft.Set(
            json: """{"tokens":[],"tokens":[]}""",
            pointer: "/scrollX"
        ));
        Assert.Equal(
            expected: before,
            actual: draft.Show()
        );
        draft.Set(
            json: "\"missing\"",
            pointer: "/scrollX"
        );
        Assert.Throws<Puck.Assets.Documents.DocumentValidationException>(testCode: () => draft.Check());
        draft.Undo();
        Assert.Equal(
            expected: before,
            actual: draft.Show()
        );
        draft.Set(
            json: "\"EDITED\"",
            pointer: "/title"
        );
        Assert.Equal(
            expected: "EDITED",
            actual: draft.Check().Title
        );
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => CartridgeDocuments.Parse(utf8: Encoding.UTF8.GetBytes(s: draft.Show().Replace(
            newValue: "\"typo\":",
            oldValue: "\"schema\":"
        ))));
    }
}
