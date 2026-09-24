using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers nested branches, counted loops and loop exits, and pins the per-frame reservation against real hardware.</summary>
public sealed class CartridgeControlFlowTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a break outside a repeat",
            Document: Refused(statement: new CartridgeStatement(Kind: "break")),
            Path: "rules[0].body[0]",
            Fragment: "inside a repeat"
        ),
        new(
            Name: "a repeat of zero",
            Document: Refused(statement: Repeat(
                count: 0,
                index: "i",
                body: [Set(
                        target: "x",
                        operation: ExpressionOp.Add,
                        value: CartridgeExpressions.Of(constant: 1)
                    )]
            )),
            Path: "rules[0].body[0].count",
            Fragment: "iteration count"
        ),
        new(
            Name: "an unknown loop index",
            Document: Refused(statement: Repeat(
                count: 4,
                index: "missing",
                body: [Set(
                        target: "x",
                        operation: ExpressionOp.Add,
                        value: CartridgeExpressions.Of(constant: 1)
                    )]
            )),
            Path: "rules[0].body[0].index",
            Fragment: "Unknown state variable"
        ),
        new(
            Name: "a count on a set step",
            Document: Refused(statement: new CartridgeStatement(
                Kind: "set",
                Target: new CartridgeTarget(State: "x"),
                Operation: null,
                Value: CartridgeExpressions.Of(constant: 1),
                Count: 3
            )),
            Path: "rules[0].body[0].count",
            Fragment: "cannot carry 'count'"
        ),
        new(
            Name: "an unknown step kind",
            Document: Refused(statement: new CartridgeStatement(Kind: "loop")),
            Path: "rules[0].body[0].kind",
            Fragment: "Expected set, if, repeat, break, map, blit, plot, save, load, play, stop, clock, fade or blend"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Refused(CartridgeStatement statement) => Blank(
        target: "cgb",
        title: "REFUSE"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "i",
            Initial: 0
        ), new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
        Rules = [Rule(body: [statement])],
    };
    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(
        target: target,
        title: title
    );
    private static int LargestSweepInsideTheReservation(string target) {
        var low = 1;
        var high = CartridgeLimits.RepeatCount;

        while (low < high) {
            var probe = (((low + high) + 1) / 2);

            var (frame, reservation) = CartridgeDocuments.Estimate(document: Sweep(
                count: probe,
                target: target
            ));
            if (
                frame.IsKnown &&
                (frame.Cycles <= reservation)
            ) { low = probe; } else { high = (probe - 1); }
        }

        return low;
    }
    // Guards the body behind a "done" latch so the measured state is the first frame's result, not a per-frame rerun.
    private static CartridgeRule Once(string name, CartridgeStatement[] body) => new(
        Name: name,
        When: CartridgeExpressions.Gate(
            left: CartridgeExpressions.Of(state: "done"),
            comparison: ExpressionOp.Equal,
            right: CartridgeExpressions.Of(constant: 0)
        ),
        Body: [.. body, Set(
                target: "done",
                operation: null,
                value: CartridgeExpressions.Of(constant: 1)
            )]
    );
    private static CartridgeStatement Repeat(int count, string index, CartridgeStatement[] body) =>
        new(
            Kind: "repeat",
            Count: count,
            Index: index,
            Body: body
        );
    private static CartridgeRule Rule(CartridgeStatement[] body) => new(
        Name: "rule",
        Body: body
    );
    private static CartridgeStatement Set(string target, ExpressionOp? operation, ExpressionProgram value) =>
        new(
            Kind: "set",
            Target: new CartridgeTarget(State: target),
            Operation: operation,
            Value: value
        );
    private static CartridgeDocument Sweep(string target, int count) => Blank(
        target: target,
        title: "SWEEP"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "slot",
            Initial: 0
        ), new CartridgeVariable(
            Name: "band",
            Initial: 0
        ), new CartridgeVariable(
            Name: "sink",
            Initial: 0
        )],
        Arrays = [new CartridgeArray(
            Initial: new int[180],
            Name: "cells"
        )],
        Rules = [new CartridgeRule(
            Name: "work",
            Body: [
            Repeat(
                    count: count,
                    index: "band",
                    body: [
                Repeat(
                            count: SweepInner,
                            index: "slot",
                            body: [
                    Set(
                                    target: "sink",
                                    operation: ExpressionOp.Add,
                                    value: CartridgeExpressions.Of(
                                        state: "cells",
                                        key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "slot"))
                                    )
                                ),
                ]
                        ),
            ]
                ),
        ]
        )],
    };

    /// <summary>
    /// Calibrates <see cref="CartridgeCostProfile.FrameUnits"/> against hardware: a document the estimate puts at the
    /// reservation must still complete one rule pass per hardware frame, or the advice is wrong in the direction that
    /// matters. The estimate gates nothing, so this is what keeps it honest.
    /// </summary>
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ADocumentAtTheBudgetKeepsUpWithTheFrameRate(string target) {
        const int Frames = 40;
        var accepted = LargestSweepInsideTheReservation(target: target);

        // The bisection must have stopped on the reservation, not on the schema's own cap for one count.
        Assert.NotEqual(
            actual: accepted,
            expected: CartridgeLimits.RepeatCount
        );

        var document = Blank(
            target: target,
            title: "BUDGET"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "slot",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "band",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "ticks",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "sink",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: new int[180],
                Name: "cells"
            )],
            Rules = [new CartridgeRule(
                Name: "work",
                Body: [
                Repeat(
                        count: accepted,
                        index: "band",
                        body: [
                    Repeat(
                                count: SweepInner,
                                index: "slot",
                                body: [
                        Set(
                                        target: "sink",
                                        operation: ExpressionOp.Add,
                                        value: CartridgeExpressions.Of(
                                            state: "cells",
                                            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "slot"))
                                        )
                                    ),
                    ]
                            ),
                ]
                    ),
                Set(
                        target: "ticks",
                        operation: ExpressionOp.Add,
                        value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: Frames,
            label: "control"
        );
        var ticks = machine.Read(variable: "ticks");

        Assert.InRange(
            actual: ticks,
            high: Frames,
            low: (Frames - 4)
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void BreakLeavesTheIndexAtTheIterationThatBroke(string target) {
        var document = Blank(
            target: target,
            title: "BREAK"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "slot",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "seen",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: [4, 4, 0, 4, 4],
                Name: "cells"
            )],
            Rules = [Once(
                name: "scan",
                body: [
                Repeat(
                        count: 5,
                        index: "slot",
                        body: [
                    new CartridgeStatement(
                                Kind: "if",
                                When: CartridgeExpressions.Gate(
                                    left: CartridgeExpressions.Of(
                                        state: "cells",
                                        key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "slot"))
                                    ),
                                    comparison: ExpressionOp.Equal,
                                    right: CartridgeExpressions.Of(constant: 0)
                                ),
                                Then: [new CartridgeStatement(Kind: "break")]
                            ),
                    Set(
                                target: "seen",
                                operation: ExpressionOp.Add,
                                value: CartridgeExpressions.Of(constant: 1)
                            ),
                ]
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "control"
        );

        Assert.Equal(
            expected: 2,
            actual: machine.Read(variable: "slot")
        );
        Assert.Equal(
            expected: 2,
            actual: machine.Read(variable: "seen")
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void NestedLoopsAndElseArmsAgree(string target) {
        var document = Blank(
            target: target,
            title: "NESTED"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "outer",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "inner",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "cursor",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "evens",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "odds",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: new int[12],
                Name: "grid"
            )],
            Rules = [Once(
                name: "fill",
                body: [
                // grid[cursor] = outer * 4 + inner, walked by a running cursor rather than a per-cell multiply.
                Repeat(
                        count: 3,
                        index: "outer",
                        body: [
                    Repeat(
                                count: 4,
                                index: "inner",
                                body: [
                        new CartridgeStatement(
                                        Kind: "set",
                                        Target: new CartridgeTarget(
                                            State: "grid",
                                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "cursor"))
                                        ),
                                        Operation: null,
                                        Value: CartridgeExpressions.Of(state: "cursor")
                                    ),
                        new CartridgeStatement(
                                        Kind: "if",
                                        When: CartridgeExpressions.Gate(
                                            left: CartridgeExpressions.Of(state: "inner"),
                                            comparison: ExpressionOp.Less,
                                            right: CartridgeExpressions.Of(constant: 2)
                                        ),
                                        Then: [Set(
                                                target: "evens",
                                                operation: ExpressionOp.Add,
                                                value: CartridgeExpressions.Of(constant: 1)
                                            )],
                                        Else: [Set(
                                                target: "odds",
                                                operation: ExpressionOp.Add,
                                                value: CartridgeExpressions.Of(constant: 1)
                                            )]
                                    ),
                        Set(
                                        target: "cursor",
                                        operation: ExpressionOp.Add,
                                        value: CartridgeExpressions.Of(constant: 1)
                                    ),
                    ]
                            ),
                ]
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "control"
        );

        Assert.Equal(
            expected: 12,
            actual: machine.Read(variable: "cursor")
        );
        Assert.Equal(
            expected: 6,
            actual: machine.Read(variable: "evens")
        );
        Assert.Equal(
            expected: 6,
            actual: machine.Read(variable: "odds")
        );
        for (var cell = 0; (cell < 12); ++cell) {
            Assert.Equal(
                expected: cell,
                actual: machine.Read(address: (machine.Result.Arrays["grid"] + ((uint)cell)))
            );
        }
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void RepeatWalksItsIndexAndSumsAnArray(string target) {
        var document = Blank(
            target: target,
            title: "LOOP"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "row",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "total",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: [1, 2, 3, 4, 5, 6],
                Name: "values"
            )],
            Rules = [Once(
                name: "sum",
                body: [
                Repeat(
                        count: 6,
                        index: "row",
                        body: [
                    Set(
                                target: "total",
                                operation: ExpressionOp.Add,
                                value: CartridgeExpressions.Of(
                                    state: "values",
                                    key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "row"))
                                )
                            ),
                ]
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 12,
            label: "control"
        );

        Assert.Equal(
            expected: 21,
            actual: machine.Read(variable: "total")
        );
        Assert.Equal(
            expected: 6,
            actual: machine.Read(variable: "row")
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationRefusesUnboundedAndMalformedControlFlow(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );

    // The widest sweep the estimate still puts inside the reservation, found by bisection rather than a pinned number that would drift
    // the moment a cost constant moves. The sweep nests: one count is bounded at 255 by the schema, which is well
    // inside either reservation, so a single loop would measure that bound instead of the reservation.
    // Inner iterations per band. Small enough that the outer count lands well inside the schema's cap on either
    // machine, so the bisection is bounded by the reservation.
    private const int SweepInner = 20;
}
