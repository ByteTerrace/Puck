using Puck.Physics.Motion;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the whole-long int cell carrier and the modulo, bitwise, comparison, and select expression
/// operators at their compile-time kind proof and their runtime refusal boundaries.</summary>
public sealed class WorldExpressionVocabularyLawTests {
    [Fact]
    public void IntCellsSpanTheWholeLongAndBitboardArithmeticIsExact() {
        var definition = Document(
            state: [Slot("board", 0L), Slot("big", long.MaxValue), Slot("seen", 0L)],
            rules: [
                new WorldRule(
                    Name: Name("set-corners"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                        State: "board",
                        Expression: new ValueExpression(Tokens: [
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.Constant(Value: 63m),
                            new ValueToken.ShiftLeft(),
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.BitOr(),
                            new ValueToken.BitNot(),
                            new ValueToken.BitNot(),
                        ])
                    )]
                ),
                new WorldRule(
                    Name: Name("big-holds"),
                    Mode: ActionTriggerMode.Edge,
                    Gate: new ActionPredicate.CompareState(State: "big", Comparison: ActionStateComparison.Greater, Value: 140_737_488_355_327m),
                    Effects: [new ActionEffect.SetState(
                        State: "seen",
                        Expression: new ValueExpression(Tokens: [
                            new ValueToken.State(Name: "big"),
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.ShiftRightLogical(),
                        ])
                    )]
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: (long.MinValue | 1L), actual: Value(fixture: fixture, row: "board"));
        Assert.Equal(expected: (long.MaxValue >>> 1), actual: Value(fixture: fixture, row: "seen"));
    }

    [Fact]
    public void ParallelBitsAndBitFieldsPackAndUnpackAndRefuseFieldsThatLeaveTheCarrier() {
        var definition = Document(
            state: [Slot("packed", 0L), Slot("spread", 0L), Slot("field", 0L), Slot("inserted", 0L), Slot("refused", 7L)],
            rules: [
                new WorldRule(Name: Name("pext"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(State: "packed", Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 176m), new ValueToken.Constant(Value: 240m), new ValueToken.ParallelBitExtract(),
                ]))]),
                new WorldRule(Name: Name("pdep"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(State: "spread", Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 11m), new ValueToken.Constant(Value: 240m), new ValueToken.ParallelBitDeposit(),
                ]))]),
                new WorldRule(Name: Name("field"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(State: "field", Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 4660m), new ValueToken.Constant(Value: 4m), new ValueToken.Constant(Value: 8m), new ValueToken.BitField(),
                ]))]),
                new WorldRule(Name: Name("insert"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(State: "inserted", Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 4660m), new ValueToken.Constant(Value: 255m), new ValueToken.Constant(Value: 4m), new ValueToken.Constant(Value: 8m), new ValueToken.BitInsert(),
                ]))]),
                new WorldRule(Name: Name("too-wide"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(State: "refused", Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 60m), new ValueToken.Constant(Value: 8m), new ValueToken.BitField(),
                ]))]),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(expected: 0b1011L, actual: Value(fixture: fixture, row: "packed"));
        Assert.Equal(expected: 0b1011_0000L, actual: Value(fixture: fixture, row: "spread"));
        Assert.Equal(expected: 0x23L, actual: Value(fixture: fixture, row: "field"));
        Assert.Equal(expected: 0x1FF4L, actual: Value(fixture: fixture, row: "inserted"));
        Assert.Equal(expected: 7L, actual: Value(fixture: fixture, row: "refused"));
    }

    [Fact]
    public void ModuloComparisonsAndSelectComposeInBothKinds() {
        var half = FixedQ4816.FromRawBits(value: (FixedQ4816.One.Value / 2L)).Value;
        var definition = Document(
            state: [Slot("pos", 37L), Slot("total", 15L), Slot("ace", 0L), FixedSlot("frac", 0L), FixedSlot("pick", 0L)],
            rules: [
                new WorldRule(Name: Name("wrap"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(
                    State: "pos",
                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "pos"),
                        new ValueToken.Constant(Value: 7m),
                        new ValueToken.Add(),
                        new ValueToken.Constant(Value: 40m),
                        new ValueToken.Modulo(),
                    ])
                )]),
                new WorldRule(Name: Name("ace"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(
                    State: "ace",
                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "total"),
                        new ValueToken.Constant(Value: 11m),
                        new ValueToken.Add(),
                        new ValueToken.Constant(Value: 21m),
                        new ValueToken.LessOrEqual(),
                        new ValueToken.Constant(Value: 11m),
                        new ValueToken.Constant(Value: 1m),
                        new ValueToken.Select(),
                    ])
                )]),
                new WorldRule(Name: Name("frac"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(
                    State: "frac",
                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.Constant(Value: 2.5m),
                        new ValueToken.Constant(Value: 1m),
                        new ValueToken.Modulo(),
                    ])
                )]),
                new WorldRule(Name: Name("pick"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(
                    State: "pick",
                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.Constant(Value: 0.25m),
                        new ValueToken.Constant(Value: 0.5m),
                        new ValueToken.Greater(),
                        new ValueToken.Constant(Value: 3m),
                        new ValueToken.Constant(Value: 0.5m),
                        new ValueToken.Select(),
                    ])
                )]),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 4L, actual: Value(fixture: fixture, row: "pos"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "ace"));
        Assert.Equal(expected: half, actual: Value(fixture: fixture, row: "frac"));
        Assert.Equal(expected: half, actual: Value(fixture: fixture, row: "pick"));
    }

    [Fact]
    public void ShiftCountAndZeroDivisorRefuseTransactionallyWhileMinusOneModuloIsZero() {
        var definition = Document(
            state: [Slot("target", 5L), Slot("failed", 0L), Slot("wrapped", long.MinValue), Slot("zero", 9L)],
            rules: [
                new WorldRule(Name: Name("bad-shift"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(
                        State: "target",
                        Expression: new ValueExpression(Tokens: [
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.Constant(Value: 64m),
                            new ValueToken.ShiftLeft(),
                        ])
                    )],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]),
                new WorldRule(Name: Name("bad-modulo"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(
                        State: "target",
                        Expression: new ValueExpression(Tokens: [
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.Constant(Value: 0m),
                            new ValueToken.Modulo(),
                        ])
                    )],
                    OnFailure: [new WorldTransactionStep.AddCell(State: "failed", Value: 1m)]
                )]),
                new WorldRule(Name: Name("min-modulo"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.SetState(
                    State: "zero",
                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "wrapped"),
                        new ValueToken.Constant(Value: -1m),
                        new ValueToken.Modulo(),
                    ])
                )]),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 5L, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 2L, actual: Value(fixture: fixture, row: "failed"));
        Assert.Equal(expected: 0L, actual: Value(fixture: fixture, row: "zero"));
    }

    [Fact]
    public void BitwiseInFixedAndMistypedSelectRefuseAtCompilation() {
        var bitwiseInFixed = Document(
            state: [FixedSlot("target", 0L)],
            rules: [new WorldRule(Name: Name("fixed-bitwise"), Effects: [new ActionEffect.SetState(
                State: "target",
                Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.BitAnd(),
                ])
            )])]
        );
        var fixedCondition = Document(
            state: [FixedSlot("target", 0L)],
            rules: [new WorldRule(Name: Name("fixed-condition"), Effects: [new ActionEffect.SetState(
                State: "target",
                Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Constant(Value: 3m),
                    new ValueToken.Select(),
                ])
            )])]
        );
        var danglingComparison = Document(
            state: [FixedSlot("target", 0L)],
            rules: [new WorldRule(Name: Name("dangling"), Effects: [new ActionEffect.SetState(
                State: "target",
                Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Less(),
                ])
            )])]
        );
        var underflow = Document(
            state: [Slot("target", 0L)],
            rules: [new WorldRule(Name: Name("underflow"), Effects: [new ActionEffect.SetState(
                State: "target",
                Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Select(),
                ])
            )])]
        );
        var control = Document(
            state: [Slot("target", 0L)],
            rules: [new WorldRule(Name: Name("control"), Effects: [new ActionEffect.SetState(
                State: "target",
                Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Less(),
                    new ValueToken.Constant(Value: 7m),
                    new ValueToken.Constant(Value: 9m),
                    new ValueToken.Select(),
                ])
            )])]
        );

        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: bitwiseInFixed, reason: out var bitwiseReason));
        Assert.Contains(expectedSubstring: "kind=int expressions only", actualString: bitwiseReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: fixedCondition, reason: out var conditionReason));
        Assert.Contains(expectedSubstring: "Select", actualString: conditionReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: danglingComparison, reason: out var danglingReason));
        Assert.Contains(expectedSubstring: "leaves a kind=int value", actualString: danglingReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: underflow, reason: out var underflowReason));
        Assert.Contains(expectedSubstring: "underflows", actualString: underflowReason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), controlReason);
    }

    [Fact]
    public void BitCensusRotationsAndBoardSymmetriesReadTheCarrierExactly() {
        var board = (1L << 63) | (1L << 9) | 1L; // h8, b2, a1
        var definition = Document(
            state: [Slot("board", board), Slot("count", 0L), Slot("lowest", 0L), Slot("highest", 0L), Slot("next", 0L), Slot("rest", 0L),
                    Slot("flip", 0L), Slot("turn", 0L), Slot("rot", 0L), Slot("neg", 0L), Slot("mag", -6L), FixedSlot("sgn", FixedQ4816.FromInteger(value: -3).Value), FixedSlot("sign", 0L)],
            rules: [
                Rule("count", "count", [new ValueToken.State(Name: "board"), new ValueToken.PopCount()]),
                Rule("lowest", "lowest", [new ValueToken.State(Name: "board"), new ValueToken.TrailingZeroCount()]),
                Rule("highest", "highest", [new ValueToken.Constant(Value: 63m), new ValueToken.State(Name: "board"), new ValueToken.LeadingZeroCount(), new ValueToken.Subtract()]),
                Rule("next", "next", [new ValueToken.State(Name: "board"), new ValueToken.LowestSetBit()]),
                Rule("rest", "rest", [new ValueToken.State(Name: "board"), new ValueToken.ClearLowestSetBit()]),
                Rule("flip", "flip", [new ValueToken.State(Name: "board"), new ValueToken.ByteSwap()]),
                Rule("turn", "turn", [new ValueToken.State(Name: "board"), new ValueToken.BitReverse()]),
                Rule("rot", "rot", [new ValueToken.State(Name: "board"), new ValueToken.Constant(Value: 1m), new ValueToken.RotateLeft(), new ValueToken.Constant(Value: 1m), new ValueToken.RotateRight()]),
                Rule("neg", "neg", [new ValueToken.State(Name: "count"), new ValueToken.Negate()]),
                Rule("mag", "mag", [new ValueToken.State(Name: "mag"), new ValueToken.Abs()]),
                Rule("sign", "sign", [new ValueToken.State(Name: "sgn"), new ValueToken.Sign(), new ValueToken.Constant(Value: 2.5m), new ValueToken.Constant(Value: 7.5m), new ValueToken.Select()]),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 3L, actual: Value(fixture: fixture, row: "count"));
        Assert.Equal(expected: 0L, actual: Value(fixture: fixture, row: "lowest"));
        Assert.Equal(expected: 63L, actual: Value(fixture: fixture, row: "highest"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "next"));
        Assert.Equal(expected: board & ~1L, actual: Value(fixture: fixture, row: "rest"));
        Assert.Equal(expected: (1L << 7) | (1L << 49) | (1L << 56), actual: Value(fixture: fixture, row: "flip"));
        Assert.Equal(expected: 1L | (1L << 54) | (1L << 63), actual: Value(fixture: fixture, row: "turn"));
        Assert.Equal(expected: board, actual: Value(fixture: fixture, row: "rot"));
        Assert.Equal(expected: -3L, actual: Value(fixture: fixture, row: "neg"));
        Assert.Equal(expected: 6L, actual: Value(fixture: fixture, row: "mag"));
        Assert.Equal(expected: (5L * FixedQ4816.One.Value) / 2L, actual: Value(fixture: fixture, row: "sign"));
    }

    [Fact]
    public void ZeroCensusSaturatesAtSixtyFourAndCarrierMinimumRefusesNegation() {
        var definition = Document(
            state: [Slot("zero", 0L), Slot("lead", 0L), Slot("trail", 0L), Slot("low", 7L), Slot("min", long.MinValue), Slot("target", 5L), Slot("failed", 0L)],
            rules: [
                Rule("lead", "lead", [new ValueToken.State(Name: "zero"), new ValueToken.LeadingZeroCount()]),
                Rule("trail", "trail", [new ValueToken.State(Name: "zero"), new ValueToken.TrailingZeroCount()]),
                Rule("low", "low", [new ValueToken.State(Name: "zero"), new ValueToken.LowestSetBit()]),
                new WorldRule(Name: Name("refuse-negate"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(State: "target", Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "min"), new ValueToken.Negate()]))],
                    OnFailure: [new WorldTransactionStep.AddCell(State: "failed", Value: 1m)]
                )]),
                new WorldRule(Name: Name("refuse-abs"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(State: "target", Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "min"), new ValueToken.Abs()]))],
                    OnFailure: [new WorldTransactionStep.AddCell(State: "failed", Value: 1m)]
                )]),
                new WorldRule(Name: Name("refuse-rotate"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(State: "target", Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "low"), new ValueToken.Constant(Value: 64m), new ValueToken.RotateLeft()]))],
                    OnFailure: [new WorldTransactionStep.AddCell(State: "failed", Value: 1m)]
                )]),
            ]
        );
        var censusInFixed = Document(
            state: [FixedSlot("target", 0L)],
            rules: [new WorldRule(Name: Name("fixed-census"), Effects: [new ActionEffect.SetState(State: "target", Expression: new ValueExpression(Tokens: [new ValueToken.Constant(Value: 1m), new ValueToken.PopCount()]))])]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 64L, actual: Value(fixture: fixture, row: "lead"));
        Assert.Equal(expected: 64L, actual: Value(fixture: fixture, row: "trail"));
        Assert.Equal(expected: 0L, actual: Value(fixture: fixture, row: "low"));
        Assert.Equal(expected: 5L, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 3L, actual: Value(fixture: fixture, row: "failed"));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: censusInFixed, reason: out var reason));
        Assert.Contains(expectedSubstring: "kind=int expressions only", actualString: reason);
    }

    private static WorldRule Rule(string name, string target, IReadOnlyList<ValueToken> tokens) => new(
        Name: Name(name),
        Mode: ActionTriggerMode.Edge,
        Effects: [new ActionEffect.SetState(State: target, Expression: new ValueExpression(Tokens: tokens))]
    );

    [Fact]
    public void EveryOperatorRoundTripsThroughTheStrictWireShape() {
        ValueToken[] tokens = [
            new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 2m), new ValueToken.Modulo(),
            new ValueToken.Constant(Value: 3m), new ValueToken.BitAnd(), new ValueToken.Constant(Value: 4m), new ValueToken.BitOr(),
            new ValueToken.Constant(Value: 5m), new ValueToken.BitXor(), new ValueToken.BitNot(),
            new ValueToken.Constant(Value: 1m), new ValueToken.ShiftLeft(), new ValueToken.Constant(Value: 1m), new ValueToken.ShiftRight(),
            new ValueToken.Constant(Value: 1m), new ValueToken.ShiftRightLogical(),
            new ValueToken.Constant(Value: 6m), new ValueToken.Equal(), new ValueToken.Constant(Value: 0m), new ValueToken.NotEqual(),
            new ValueToken.Constant(Value: 1m), new ValueToken.Less(), new ValueToken.Constant(Value: 1m), new ValueToken.LessOrEqual(),
            new ValueToken.Constant(Value: 1m), new ValueToken.Greater(), new ValueToken.Constant(Value: 1m), new ValueToken.GreaterOrEqual(),
            new ValueToken.Constant(Value: 8m), new ValueToken.Constant(Value: 9m), new ValueToken.Select(),
            new ValueToken.PopCount(), new ValueToken.LeadingZeroCount(), new ValueToken.TrailingZeroCount(),
            new ValueToken.LowestSetBit(), new ValueToken.ClearLowestSetBit(),
            new ValueToken.Constant(Value: 3m), new ValueToken.RotateLeft(), new ValueToken.Constant(Value: 3m), new ValueToken.RotateRight(),
            new ValueToken.ByteSwap(), new ValueToken.BitReverse(), new ValueToken.Negate(), new ValueToken.Abs(), new ValueToken.Sign(),
            new ValueToken.Constant(Value: 12m), new ValueToken.ParallelBitExtract(), new ValueToken.Constant(Value: 12m), new ValueToken.ParallelBitDeposit(),
            new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 2m), new ValueToken.BitField(),
            new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 2m), new ValueToken.BitInsert(),
        ];
        var definition = Document(
            state: [Slot("target", 0L)],
            rules: [new WorldRule(Name: Name("all"), Effects: [new ActionEffect.SetState(State: "target", Expression: new ValueExpression(Tokens: tokens))])]
        );

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var effect = Assert.IsType<ActionEffect.SetState>(@object: Assert.Single(collection: Assert.Single(collection: parsed.Rules ?? []).Effects));
        var round = Assert.IsType<ValueExpression>(@object: effect.Expression).Tokens;

        Assert.Equal(expected: tokens.Length, actual: round.Count);
        for (var index = 0; index < tokens.Length; index++) {
            Assert.Equal(expected: tokens[index].GetType(), actual: round[index].GetType());
        }
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: parsed, reason: out var reason), reason);
    }

    private static WorldDefinition Document(IReadOnlyList<WorldStateRow> state, IReadOnlyList<WorldRule> rules) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: state),
        Rules = rules,
    };
    private static WorldStateRow Slot(string name, long value) => new(Name: Name(name), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)]);
    private static WorldStateRow FixedSlot(string name, long value) => new(Name: Name(name), Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)]);
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static long Value(WorldFixture fixture, string row) {
        var declared = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: row)!;

        return WorldDefinitionRows.FindCell(cells: declared.Cells, key: WorldStateRow.SlotKey)!.Value;
    }
}
