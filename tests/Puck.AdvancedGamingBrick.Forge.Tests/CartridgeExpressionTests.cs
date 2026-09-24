using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the expression and predicate vocabulary a cartridge shares with the rule engine: a composed operand
/// evaluates on both machines with the compiler allocating whatever it holds in flight, and a gate composes through
/// all, any and not rather than being a conjunction of flat conditions.
/// </summary>
/// <remarks>Every case is authored through the infix spelling, because that is the text a document carries and the
/// only spelling an author writes; building tokens by hand would test a path nothing uses.</remarks>
public sealed class CartridgeExpressionTests {
    private static ActionPredicate Any(string left, string right) =>
        new ActionPredicate.Any(Predicates: [Gate(text: left), Gate(text: right)]);
    private static CartridgeCompilation Compile(string target, string[] slots, int[] seeds, CartridgeStatement[] body, CartridgeArray[]? arrays = null) =>
        Compile(
            document: Document(
                arrays: (arrays ?? []),
                body: body,
                seeds: seeds,
                slots: slots,
                target: target
            ),
            target: target
        );
    private static CartridgeCompilation Compile(CartridgeDocument document, string target) {
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        return CartridgeProbe.Compiler(target: target).Compile(document: document);
    }
    private static CartridgeDocument Document(string target, string[] slots, int[] seeds, CartridgeArray[] arrays, CartridgeStatement[] body) =>
        CartridgeDocuments.Create(
            target: target,
            title: "EXPR"
        ) with {
            Variables = [.. slots.Select(selector: (name, index) => new CartridgeVariable(
                Name: name,
                Initial: seeds[index]
            ))],
            Arrays = arrays,
            Rules = [new CartridgeRule(
                Name: "work",
                Body: body
            )],
        };
    private static ActionPredicate Gate(string text) {
        var parts = text.Split(separator: ' ');

        return CartridgeExpressions.Gate(
            left: ExpressionProgram.Parse(text: parts[0]),
            comparison: ((parts[1] == "==")
            ? ExpressionOp.Equal
            : ExpressionOp.NotEqual),
            right: ExpressionProgram.Parse(text: parts[2])
        );
    }
    private static int Read(CartridgeCompilation result, string slot) {
        using var probe = Run(result: result);

        return probe.Read(variable: slot);
    }
    private static CartridgeProbe Run(CartridgeCompilation result) {
        var probe = new CartridgeProbe(
            label: "expression",
            result: result
        );

        probe.Run(frames: 12);
        return probe;
    }
    private static CartridgeStatement Write(string slot, string value) =>
        new(
            Kind: "set",
            Target: new CartridgeTarget(State: slot),
            Value: ExpressionProgram.Parse(text: value)
        );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AButtonReadComposesUnderNot(string target) {
        // No button is held in the probe, so the negated read is what fires. The point is that input is an operand
        // like any other rather than a condition kind that only conjunction can reach.
        var document = Document(
            target: target,
            slots: ["fired"],
            seeds: [0],
            arrays: [],
            body: [
            Write(
                    slot: "fired",
                    value: "1"
                ),
        ]
        );

        var result = Compile(
            document: document with {
                Rules = [document.Rules[0] with { When = new ActionPredicate.Not(Predicate: CartridgeExpressions.Pressing(
                    button: "a",
                    mode: "held"
                )) }],
            },
            target: target
        );

        Assert.Equal(
            expected: 1,
            actual: Read(
                result: result,
                slot: "fired"
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AComparisonInsideAnExpressionYieldsOneOrZero(string target) {
        var result = Compile(
            target: target,
            slots: ["a", "b", "greater", "lesser"],
            seeds: [9, 4, 0, 0],
            body: [
            Write(
                    slot: "greater",
                    value: "a > b"
                ),
            Write(
                    slot: "lesser",
                    value: "a < b"
                ),
        ]
        );

        using var machine = Run(result: result);

        Assert.Equal(
            expected: 1,
            actual: machine.Read(variable: "greater")
        );
        Assert.Equal(
            expected: 0,
            actual: machine.Read(variable: "lesser")
        );
    }
    // (7 + 3) * 4 is 40; evaluating left to right without the grouping would give 7 + 12, and dividing before
    // subtracting would give something else again, so one expression pins the whole order. The operand order a stack
    // machine is easy to get backwards is the other: 9 - 4 is 5, never 251.
    [InlineData("cgb", "(a + b) * 4", 7, 3, 40)]
    [InlineData("agb", "(a + b) * 4", 7, 3, 40)]
    [InlineData("cgb", "a - b", 9, 4, 5)]
    [InlineData("agb", "a - b", 9, 4, 5)]
    [Theory]
    public void AnExpressionEvaluatesInTheOrderItIsWritten(string target, string expression, int a, int b, int expected) {
        var result = Compile(
            target: target,
            slots: ["a", "b", "out"],
            seeds: [a, b, 0],
            body: [
            Write(
                    slot: "out",
                    value: expression
                ),
        ]
        );

        Assert.Equal(
            expected: expected,
            actual: Read(
                result: result,
                slot: "out"
            )
        );
    }
    [Fact]
    public void AWideSlotInsideAComposedExpressionIsRefused() {
        var document = Document(
            target: "cgb",
            slots: ["out"],
            seeds: [0],
            arrays: [],
            body: [
            Write(
                    slot: "out",
                    value: "score + 1"
                ),
        ]
        ) with {
            Variables = [new CartridgeVariable(
                Name: "out",
                Initial: 0
            ), new CartridgeVariable(
                Initial: 0,
                Max: CartridgeLimits.WideMaximum,
                Name: "score"
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "wide slot",
            Name: "a wide slot inside an expression",
            Path: "rules[0].body[0].value"
        ).Holds();
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AnElementReadsThroughAComputedIndex(string target) {
        var result = Compile(
            target: target,
            slots: ["i", "out"],
            seeds: [1, 0],
            arrays: [new CartridgeArray(
                    Initial: [10, 20, 30, 40],
                    Name: "cells"
                )],
            body: [
            Write(
                    slot: "out",
                    value: "cells[i + 2] + cells[i]"
                ),
        ]
        );

        // cells[3] + cells[1] is 40 + 20. A constant-folded index would read cells[2] and give the wrong sum.
        Assert.Equal(
            expected: 60,
            actual: Read(
                result: result,
                slot: "out"
            )
        );
    }
    [Fact]
    public void AnExpressionDeeperThanTheMachineSpendsIsRefused() {
        // Each nested addition holds one more operand in flight than the last.
        var deep = string.Join(
            separator: " + ",
            values: Enumerable.Range(
                count: 2,
                start: 0
            ).Select(selector: static _ => "1")
        );

        for (var depth = 0; (depth <= CartridgeLimits.NarrowMaximum); ++depth) {
            deep = $"(1 + ({deep}))";
            if (CartridgeExpressions.Depth(expression: ExpressionProgram.Parse(text: deep)) > CartridgeExpressions.MaxDepth) {
                break;
            }
        }

        var document = Document(
            target: "cgb",
            slots: ["out"],
            seeds: [0],
            arrays: [],
            body: [Write(
                    slot: "out",
                    value: deep
                )]
        );

        new CartridgeRefusal(
            Document: document,
            Fragment: "values at once",
            Name: "an expression past the operand depth",
            Path: "rules[0].body[0].value"
        ).Holds();
    }
    [Fact]
    public void AnOperationTheRuleLanguageHasButACartridgeDoesNotIsRefusedByName() {
        var document = Document(
            target: "cgb",
            slots: ["a", "out"],
            seeds: [9, 0],
            arrays: [],
            body: [
            Write(
                    slot: "out",
                    value: "squareRoot(a)"
                ),
        ]
        );

        new CartridgeRefusal(
            Document: document,
            Fragment: "a cartridge does not",
            Name: "a rule-language operation",
            Path: "rules[0].body[0].value"
        ).Holds();
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AnyHoldsWhenEitherArmDoes(string target) {
        var document = Document(
            target: target,
            slots: ["a", "b", "fired"],
            seeds: [0, 0, 0],
            arrays: [],
            body: [
            Write(
                    slot: "fired",
                    value: "1"
                ),
        ]
        );

        // Neither arm holds, so an any gate must refuse: the control that makes the positive case mean something.
        var quiet = Compile(
            document: document with {
                Rules = [document.Rules[0] with { When = Any(
                    left: "a == 1",
                    right: "b == 1"
                ) }],
            },
            target: target
        );

        Assert.Equal(
            expected: 0,
            actual: Read(
                result: quiet,
                slot: "fired"
            )
        );

        // The second arm holds on its own.
        var loud = Compile(
            document: document with {
                Variables = [new CartridgeVariable(
                    Name: "a",
                    Initial: 0
                ), new CartridgeVariable(
                    Name: "b",
                    Initial: 1
                ), new CartridgeVariable(
                    Name: "fired",
                    Initial: 0
                )],
                Rules = [document.Rules[0] with { When = Any(
                    left: "a == 1",
                    right: "b == 1"
                ) }],
            },
            target: target
        );

        Assert.Equal(
            expected: 1,
            actual: Read(
                result: loud,
                slot: "fired"
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void BoundsAndChoicesEvaluateOnBothMachines(string target) {
        var result = Compile(
            target: target,
            slots: ["a", "b", "low", "high", "held", "clamped"],
            seeds: [9, 4, 0, 0, 0, 0],
            body: [
            Write(
                    slot: "low",
                    value: "minimum(a, b)"
                ),
            Write(
                    slot: "high",
                    value: "maximum(a, b)"
                ),
            Write(
                    slot: "held",
                    value: "a > b ? a : b"
                ),
            Write(
                    slot: "clamped",
                    value: "clamp(a, 1, 5)"
                ),
        ]
        );

        using var machine = Run(result: result);

        Assert.Equal(
            expected: 4,
            actual: machine.Read(variable: "low")
        );
        Assert.Equal(
            expected: 9,
            actual: machine.Read(variable: "high")
        );
        Assert.Equal(
            expected: 9,
            actual: machine.Read(variable: "held")
        );
        Assert.Equal(
            expected: 5,
            actual: machine.Read(variable: "clamped")
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void NotInvertsItsArm(string target) {
        var document = Document(
            target: target,
            slots: ["a", "fired"],
            seeds: [0, 0],
            arrays: [],
            body: [
            Write(
                    slot: "fired",
                    value: "1"
                ),
        ]
        );
        var gate = new ActionPredicate.Not(Predicate: Gate(text: "a == 1"));

        var inverted = Compile(
            document: document with { Rules = [document.Rules[0] with { When = gate }] },
            target: target
        );

        Assert.Equal(
            expected: 1,
            actual: Read(
                result: inverted,
                slot: "fired"
            )
        );

        var suppressed = Compile(
            document: document with {
                Variables = [new CartridgeVariable(
                    Name: "a",
                    Initial: 1
                ), new CartridgeVariable(
                    Name: "fired",
                    Initial: 0
                )],
                Rules = [document.Rules[0] with { When = gate }],
            },
            target: target
        );

        Assert.Equal(
            expected: 0,
            actual: Read(
                result: suppressed,
                slot: "fired"
            )
        );
    }
}
