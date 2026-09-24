using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers addressable array state and the arithmetic neither instruction set supplies directly.</summary>
public sealed class CartridgeMemoryTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a write to an unknown array",
            Document: Writing(
                target: new CartridgeTarget(
                    State: "missing",
                    Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 0))
                ),
                operation: null,
                value: 1
            ),
            Path: "rules[0].body[0].target",
            Fragment: "Unknown array"
        ),
        new(
            Name: "a literal index past the array",
            Document: Writing(
                target: new CartridgeTarget(
                    State: "table",
                    Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 5))
                ),
                operation: null,
                value: 1
            ),
            Path: "rules[0].body[0].target.index",
            Fragment: "outside array"
        ),
        new(
            Name: "an array write without an index",
            Document: Writing(
                target: new CartridgeTarget(State: "table"),
                operation: null,
                value: 1
            ),
            Path: "rules[0].body[0].target",
            Fragment: "requires an index"
        ),
        new(
            Name: "an index on a slot",
            Document: Writing(
                target: new CartridgeTarget(
                    Key: "0",
                    State: "x"
                ),
                operation: null,
                value: 1
            ),
            Path: "rules[0].body[0].target",
            Fragment: "cannot carry an index"
        ),
        new(
            Name: "a literal zero divisor",
            Document: Writing(
                target: new CartridgeTarget(State: "x"),
                operation: ExpressionOp.Divide,
                value: 0
            ),
            Path: "rules[0].body[0].value",
            Fragment: "zero divisor"
        ),
        new(
            Name: "a literal shift of a whole byte",
            Document: Writing(
                target: new CartridgeTarget(State: "x"),
                operation: ExpressionOp.ShiftLeft,
                value: 8
            ),
            Path: "rules[0].body[0].value",
            Fragment: "eight or more"
        ),
        new(
            Name: "an array named like a variable",
            Document: Unsound() with { Arrays = [new CartridgeArray(
                    Initial: [1],
                    Name: "x"
                )] },
            Path: "arrays[0].name",
            Fragment: "reuse a variable name"
        ),
        new(
            Name: "arrays past the state budget",
            Document: Unsound() with { Arrays = [.. Enumerable.Range(
                    count: 29,
                    start: 0
                ).Select(selector: i => new CartridgeArray(
                    Initial: new int[256],
                    Name: $"big{i}"
                ))] },
            Path: "arrays",
            Fragment: "state budget"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    // A slot x and a three-element table, the two shapes every refusal below writes at wrongly.
    private static CartridgeDocument Unsound() => Blank(
        target: "cgb",
        title: "REFUSE"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
        Arrays = [new CartridgeArray(
            Initial: [1, 2, 3],
            Name: "table"
        )],
    };
    private static CartridgeDocument Writing(CartridgeTarget target, ExpressionOp? operation, int value) => Unsound() with {
        Rules = [Once(
            name: "r",
            actions: [new CartridgeStatement(
                    Kind: "set",
                    Target: target,
                    Operation: operation,
                    Value: CartridgeExpressions.Of(constant: value)
                )]
        )],
    };
    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(
        target: target,
        title: title
    );
    private static ExpressionProgram Element(string array, ExpressionProgram index) => CartridgeExpressions.Of(
        state: array,
        key: CartridgeExpressions.Key(index: index)
    );
    // Guards the body behind a "done" latch so the measured state is the first frame's result, not a per-frame rerun.
    private static CartridgeRule Once(string name, CartridgeStatement[] actions) => new(
        Name: name,
        When: CartridgeExpressions.Gate(
            left: CartridgeExpressions.Of(state: "done"),
            comparison: ExpressionOp.Equal,
            right: CartridgeExpressions.Of(constant: 0)
        ),
        Body: [.. actions, new CartridgeStatement(
                Kind: "set",
                Target: new CartridgeTarget(State: "done"),
                Operation: null,
                Value: CartridgeExpressions.Of(constant: 1)
            )]
    );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ArithmeticWritesThroughAnIndexedDestination(string target) {
        var document = Blank(
            target: target,
            title: "INDEXED"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "slot",
                Initial: 2
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: [6, 6, 6, 6],
                Name: "cells"
            )],
            Rules = [Once(
                name: "apply",
                actions: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "cells",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "slot"))
                        ),
                        Operation: ExpressionOp.Multiply,
                        Value: CartridgeExpressions.Of(constant: 7)
                    ),
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "cells",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 1))
                        ),
                        Operation: ExpressionOp.Divide,
                        Value: CartridgeExpressions.Of(constant: 3)
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "memory"
        );

        Assert.Equal(
            expected: 6,
            actual: machine.Read(address: machine.Result.Arrays["cells"])
        );
        Assert.Equal(
            expected: 2,
            actual: machine.Read(address: (machine.Result.Arrays["cells"] + 1))
        );
        Assert.Equal(
            expected: 42,
            actual: machine.Read(address: (machine.Result.Arrays["cells"] + 2))
        );
        Assert.Equal(
            expected: 6,
            actual: machine.Read(address: (machine.Result.Arrays["cells"] + 3))
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ArraysInitializeAndIndexAtRuntime(string target) {
        var document = Blank(
            target: target,
            title: "ARRAYS"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "cursor",
                Initial: 3
            ),
                new CartridgeVariable(
                Name: "read",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "indirect",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [
                new CartridgeArray(
                Initial: [10, 20, 30, 40, 50],
                Name: "table"
            ),
                new CartridgeArray(
                Initial: [4, 3, 2, 1, 0],
                Name: "pointers"
            ),
            ],
            Rules = [Once(
                name: "probe",
                actions: [
                // read = table[cursor]
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "read"),
                        Operation: null,
                        Value: Element(
                            array: "table",
                            index: CartridgeExpressions.Of(state: "cursor")
                        )
                    ),
                // indirect = table[pointers[cursor]]
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "indirect"),
                        Operation: null,
                        Value: Element(
                            array: "table",
                            index: Element(
                                array: "pointers",
                                index: CartridgeExpressions.Of(state: "cursor")
                            )
                        )
                    ),
                // table[0] = table[0] + 5
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "table",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 0))
                        ),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 5)
                    ),
                // table[cursor] = 99
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "table",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "cursor"))
                        ),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 99)
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "memory"
        );

        Assert.Equal(
            expected: 40,
            actual: machine.Read(variable: "read")
        );
        Assert.Equal(
            expected: 20,
            actual: machine.Read(variable: "indirect")
        );
        Assert.Equal(
            expected: 15,
            actual: machine.Read(address: machine.Result.Arrays["table"])
        );
        Assert.Equal(
            expected: 99,
            actual: machine.Read(address: (machine.Result.Arrays["table"] + 3))
        );
        Assert.Equal(
            expected: 50,
            actual: machine.Read(address: (machine.Result.Arrays["table"] + 4))
        );
        Assert.Equal(
            expected: 4,
            actual: machine.Read(address: machine.Result.Arrays["pointers"])
        );
    }
    [InlineData(ExpressionOp.Multiply, 13, 11, 143)]
    [InlineData(ExpressionOp.Multiply, 16, 16, 0)]
    [InlineData(ExpressionOp.Multiply, 200, 3, 88)]
    [InlineData(ExpressionOp.Multiply, 7, 0, 0)]
    [InlineData(ExpressionOp.Multiply, 0, 7, 0)]
    [InlineData(ExpressionOp.Divide, 143, 11, 13)]
    [InlineData(ExpressionOp.Divide, 255, 1, 255)]
    [InlineData(ExpressionOp.Divide, 7, 2, 3)]
    [InlineData(ExpressionOp.Divide, 3, 7, 0)]
    [InlineData(ExpressionOp.Divide, 100, 0, 0)]
    [InlineData(ExpressionOp.Remainder, 143, 11, 0)]
    [InlineData(ExpressionOp.Remainder, 7, 2, 1)]
    [InlineData(ExpressionOp.Remainder, 3, 7, 3)]
    [InlineData(ExpressionOp.Remainder, 255, 16, 15)]
    [InlineData(ExpressionOp.Remainder, 100, 0, 0)]
    [InlineData(ExpressionOp.ShiftLeft, 5, 3, 40)]
    [InlineData(ExpressionOp.ShiftLeft, 255, 1, 254)]
    [InlineData(ExpressionOp.ShiftLeft, 1, 7, 128)]
    [InlineData(ExpressionOp.ShiftLeft, 3, 0, 3)]
    [InlineData(ExpressionOp.ShiftRight, 40, 3, 5)]
    [InlineData(ExpressionOp.ShiftRight, 255, 7, 1)]
    [InlineData(ExpressionOp.ShiftRight, 1, 1, 0)]
    [InlineData(ExpressionOp.ShiftRight, 3, 0, 3)]
    [Theory]
    public void ExtendedArithmeticAgreesAcrossTargets(ExpressionOp operation, int left, int right, int expected) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = Blank(
                target: target,
                title: "MATH"
            ) with {
                Variables = [
                    new CartridgeVariable(
                    Name: "value",
                    Initial: left
                ),
                    new CartridgeVariable(
                    Name: "operand",
                    Initial: right
                ),
                    new CartridgeVariable(
                    Name: "done",
                    Initial: 0
                ),
                ],
                Rules = [Once(
                    name: "apply",
                    actions: [
                    new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(State: "value"),
                            Operation: operation,
                            Value: CartridgeExpressions.Of(state: "operand")
                        ),
                ]
                )],
            };
            using var machine = CartridgeProbe.Boot(
                document: document,
                frames: 12,
                label: "memory"
            );

            Assert.Equal(
                expected: expected,
                actual: machine.Read(variable: "value")
            );
        }
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void OutOfRangeIndexReadsZeroAndDiscardsTheWrite(string target) {
        var document = Blank(
            target: target,
            title: "BOUNDS"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "past",
                Initial: 9
            ),
                new CartridgeVariable(
                Name: "read",
                Initial: 77
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: [1, 2, 3],
                Name: "table"
            )],
            Rules = [Once(
                name: "probe",
                actions: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "read"),
                        Operation: null,
                        Value: Element(
                            array: "table",
                            index: CartridgeExpressions.Of(state: "past")
                        )
                    ),
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "table",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "past"))
                        ),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 200)
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "memory"
        );

        Assert.Equal(
            expected: 0,
            actual: machine.Read(variable: "read")
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: machine.Result.Arrays["table"])
        );
        Assert.Equal(
            expected: 2,
            actual: machine.Read(address: (machine.Result.Arrays["table"] + 1))
        );
        Assert.Equal(
            expected: 3,
            actual: machine.Read(address: (machine.Result.Arrays["table"] + 2))
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationRefusesUnsoundMemoryAndArithmetic(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
