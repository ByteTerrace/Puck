using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the whole-long int cell carrier and the modulo, bitwise, comparison, and select expression
/// operators at their compile-time kind proof and their runtime refusal boundaries.</summary>
public sealed class WorldExpressionVocabularyLawTests {
    private static WorldDefinition Document(IReadOnlyList<WorldStateRow> state, IReadOnlyList<WorldRule> rules) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: state),
        Rules = rules,
    };
    private static WorldStateRow FixedSlot(string name, long value) => new(
        Name: Name(value: name),
        Kind: CellKind.Fixed,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: value
            )]
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldRule Rule(string name, string target, IReadOnlyList<ValueToken> tokens) => new(
        Name: Name(value: name),
        Mode: ActionTriggerMode.Edge,
        Effects: [new ActionEffect.SetState(
                State: target,
                Expression: new ValueExpression(Tokens: tokens)
            )]
    );
    private static WorldStateRow Slot(string name, long value) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: value
            )]
    );
    private static long Value(WorldFixture fixture, string row) {
        var declared = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: row
        )!;

        return StateRows.FindCell(
            cells: declared.Cells,
            key: WorldStateRow.SlotKey
        )!.Value;
    }

    [Fact]
    public void BitCensusRotationsAndBoardSymmetriesReadTheCarrierExactly() {
        var board = (1L << 63) | (1L << 9) | 1L; // h8, b2, a1
        var definition = Document(
            state: [Slot(
                    name: "board",
                    value: board
                ), Slot(
                    name: "count",
                    value: 0L
                ), Slot(
                    name: "lowest",
                    value: 0L
                ), Slot(
                    name: "highest",
                    value: 0L
                ), Slot(
                    name: "next",
                    value: 0L
                ), Slot(
                    name: "rest",
                    value: 0L
                ),
                    Slot(
                    name: "flip",
                    value: 0L
                ), Slot(
                    name: "turn",
                    value: 0L
                ), Slot(
                    name: "rot",
                    value: 0L
                ), Slot(
                    name: "neg",
                    value: 0L
                ), Slot(
                    name: "mag",
                    value: -6L
                ), FixedSlot(
                    name: "sgn",
                    value: FixedQ4816.FromInteger(value: -3).Value
                ), FixedSlot(
                    name: "sign",
                    value: 0L
                )],
            rules: [
                Rule(
                    name: "count",
                    target: "count",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.PopCount()]
                ),
                Rule(
                    name: "lowest",
                    target: "lowest",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.TrailingZeroCount()]
                ),
                Rule(
                    name: "highest",
                    target: "highest",
                    tokens: [new ValueToken.Constant(Value: 63m), new ValueToken.State(Name: "board"), new ValueToken.LeadingZeroCount(), new ValueToken.Subtract()]
                ),
                Rule(
                    name: "next",
                    target: "next",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.LowestSetBit()]
                ),
                Rule(
                    name: "rest",
                    target: "rest",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.ClearLowestSetBit()]
                ),
                Rule(
                    name: "flip",
                    target: "flip",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.ByteSwap()]
                ),
                Rule(
                    name: "turn",
                    target: "turn",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.BitReverse()]
                ),
                Rule(
                    name: "rot",
                    target: "rot",
                    tokens: [new ValueToken.State(Name: "board"), new ValueToken.Constant(Value: 1m), new ValueToken.RotateLeft(), new ValueToken.Constant(Value: 1m), new ValueToken.RotateRight()]
                ),
                Rule(
                    name: "neg",
                    target: "neg",
                    tokens: [new ValueToken.State(Name: "count"), new ValueToken.Negate()]
                ),
                Rule(
                    name: "mag",
                    target: "mag",
                    tokens: [new ValueToken.State(Name: "mag"), new ValueToken.Abs()]
                ),
                Rule(
                    name: "sign",
                    target: "sign",
                    tokens: [new ValueToken.State(Name: "sgn"), new ValueToken.Sign(), new ValueToken.Constant(Value: 2.5m), new ValueToken.Constant(Value: 7.5m), new ValueToken.Select()]
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: 3L,
            actual: Value(
                fixture: fixture,
                row: "count"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Value(
                fixture: fixture,
                row: "lowest"
            )
        );
        Assert.Equal(
            expected: 63L,
            actual: Value(
                fixture: fixture,
                row: "highest"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Value(
                fixture: fixture,
                row: "next"
            )
        );
        Assert.Equal(
            expected: board & ~1L,
            actual: Value(
                fixture: fixture,
                row: "rest"
            )
        );
        Assert.Equal(
            expected: (1L << 7) | (1L << 49) | (1L << 56),
            actual: Value(
                fixture: fixture,
                row: "flip"
            )
        );
        Assert.Equal(
            expected: 1L | (1L << 54) | (1L << 63),
            actual: Value(
                fixture: fixture,
                row: "turn"
            )
        );
        Assert.Equal(
            expected: board,
            actual: Value(
                fixture: fixture,
                row: "rot"
            )
        );
        Assert.Equal(
            expected: -3L,
            actual: Value(
                fixture: fixture,
                row: "neg"
            )
        );
        Assert.Equal(
            expected: 6L,
            actual: Value(
                fixture: fixture,
                row: "mag"
            )
        );
        Assert.Equal(
            expected: ((5L * FixedQ4816.One.Value) / 2L),
            actual: Value(
                fixture: fixture,
                row: "sign"
            )
        );
    }
    [Fact]
    public void BitwiseInFixedAndMistypedSelectRefuseAtCompilation() {
        var bitwiseInFixed = Document(
            state: [FixedSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-bitwise"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.BitAnd(),
                ])
                        )]
                )]
        );
        var fixedCondition = Document(
            state: [FixedSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-condition"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Constant(Value: 3m),
                    new ValueToken.Select(),
                ])
                        )]
                )]
        );
        var danglingComparison = Document(
            state: [FixedSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "dangling"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Less(),
                ])
                        )]
                )]
        );
        var underflow = Document(
            state: [Slot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "underflow"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Select(),
                ])
                        )]
                )]
        );
        var control = Document(
            state: [Slot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "control"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m),
                    new ValueToken.Constant(Value: 2m),
                    new ValueToken.Less(),
                    new ValueToken.Constant(Value: 7m),
                    new ValueToken.Constant(Value: 9m),
                    new ValueToken.Select(),
                ])
                        )]
                )]
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: bitwiseInFixed,
            reason: out var bitwiseReason
        ));
        Assert.Contains(
            actualString: bitwiseReason,
            expectedSubstring: "kind=Int expressions only"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: fixedCondition,
            reason: out var conditionReason
        ));
        Assert.Contains(
            actualString: conditionReason,
            expectedSubstring: "Select"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: danglingComparison,
            reason: out var danglingReason
        ));
        Assert.Contains(
            actualString: danglingReason,
            expectedSubstring: "leaves a kind=Int value"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: underflow,
            reason: out var underflowReason
        ));
        Assert.Contains(
            actualString: underflowReason,
            expectedSubstring: "underflows"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
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
            state: [Slot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "all"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: tokens)
                        )]
                )]
        );

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var effect = Assert.IsType<ActionEffect.SetState>(@object: Assert.Single(collection: Assert.Single(collection: (parsed.Rules ?? [])).Effects));
        var round = Assert.IsType<ValueExpression>(@object: effect.Expression).Tokens;

        Assert.Equal(
            expected: tokens.Length,
            actual: round.Count
        );
        for (var index = 0; (index < tokens.Length); index++) {
            Assert.Equal(
                expected: tokens[index].GetType(),
                actual: round[index].GetType()
            );
        }
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: parsed,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void IntCellsSpanTheWholeLongAndBitboardArithmeticIsExact() {
        var definition = Document(
            state: [Slot(
                    name: "board",
                    value: 0L
                ), Slot(
                    name: "big",
                    value: long.MaxValue
                ), Slot(
                    name: "seen",
                    value: 0L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "set-corners"),
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
                    Name: Name(value: "big-holds"),
                    Mode: ActionTriggerMode.Edge,
                    Gate: new ActionPredicate.CompareState(
                        State: "big",
                        Comparison: ActionStateComparison.Greater,
                        Value: 140_737_488_355_327m
                    ),
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

        Assert.Equal(
            expected: long.MinValue | 1L,
            actual: Value(
                fixture: fixture,
                row: "board"
            )
        );
        Assert.Equal(
            expected: (long.MaxValue >>> 1),
            actual: Value(
                fixture: fixture,
                row: "seen"
            )
        );
    }
    [Fact]
    public void ModuloComparisonsAndSelectComposeInBothKinds() {
        var half = FixedQ4816.FromRawBits(value: (FixedQ4816.One.Value / 2L)).Value;
        var definition = Document(
            state: [Slot(
                    name: "pos",
                    value: 37L
                ), Slot(
                    name: "total",
                    value: 15L
                ), Slot(
                    name: "ace",
                    value: 0L
                ), FixedSlot(
                    name: "frac",
                    value: 0L
                ), FixedSlot(
                    name: "pick",
                    value: 0L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "wrap"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "pos",
                            Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "pos"),
                        new ValueToken.Constant(Value: 7m),
                        new ValueToken.Add(),
                        new ValueToken.Constant(Value: 40m),
                        new ValueToken.Modulo(),
                    ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "ace"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
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
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "frac"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "frac",
                            Expression: new ValueExpression(Tokens: [
                        new ValueToken.Constant(Value: 2.5m),
                        new ValueToken.Constant(Value: 1m),
                        new ValueToken.Modulo(),
                    ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "pick"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "pick",
                            Expression: new ValueExpression(Tokens: [
                        new ValueToken.Constant(Value: 0.25m),
                        new ValueToken.Constant(Value: 0.5m),
                        new ValueToken.Greater(),
                        new ValueToken.Constant(Value: 3m),
                        new ValueToken.Constant(Value: 0.5m),
                        new ValueToken.Select(),
                    ])
                        )]
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: 4L,
            actual: Value(
                fixture: fixture,
                row: "pos"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Value(
                fixture: fixture,
                row: "ace"
            )
        );
        Assert.Equal(
            expected: half,
            actual: Value(
                fixture: fixture,
                row: "frac"
            )
        );
        Assert.Equal(
            expected: half,
            actual: Value(
                fixture: fixture,
                row: "pick"
            )
        );
    }
    [Fact]
    public void ParallelBitsAndBitFieldsPackAndUnpackAndRefuseFieldsThatLeaveTheCarrier() {
        var definition = Document(
            state: [Slot(
                    name: "packed",
                    value: 0L
                ), Slot(
                    name: "spread",
                    value: 0L
                ), Slot(
                    name: "field",
                    value: 0L
                ), Slot(
                    name: "inserted",
                    value: 0L
                ), Slot(
                    name: "refused",
                    value: 7L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "pext"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "packed",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 176m), new ValueToken.Constant(Value: 240m), new ValueToken.ParallelBitExtract(),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "pdep"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "spread",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 11m), new ValueToken.Constant(Value: 240m), new ValueToken.ParallelBitDeposit(),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "field"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "field",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 4660m), new ValueToken.Constant(Value: 4m), new ValueToken.Constant(Value: 8m), new ValueToken.BitField(),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "insert"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "inserted",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 4660m), new ValueToken.Constant(Value: 255m), new ValueToken.Constant(Value: 4m), new ValueToken.Constant(Value: 8m), new ValueToken.BitInsert(),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "too-wide"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "refused",
                            Expression: new ValueExpression(Tokens: [
                    new ValueToken.Constant(Value: 1m), new ValueToken.Constant(Value: 60m), new ValueToken.Constant(Value: 8m), new ValueToken.BitField(),
                ])
                        )]
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: 0b1011L,
            actual: Value(
                fixture: fixture,
                row: "packed"
            )
        );
        Assert.Equal(
            expected: 0b1011_0000L,
            actual: Value(
                fixture: fixture,
                row: "spread"
            )
        );
        Assert.Equal(
            expected: 0x23L,
            actual: Value(
                fixture: fixture,
                row: "field"
            )
        );
        Assert.Equal(
            expected: 0x1FF4L,
            actual: Value(
                fixture: fixture,
                row: "inserted"
            )
        );
        Assert.Equal(
            expected: 7L,
            actual: Value(
                fixture: fixture,
                row: "refused"
            )
        );
    }
    [InlineData(8L, 0x80L, true)]
    [InlineData(64L, long.MinValue, true)]
    [InlineData(7L, 1L, false)]
    [InlineData(8L, 256L, false)]
    [Theory]
    public void ReplicationReadsLiveOperandsAndRefusesTheWholeTransaction(long width, long pattern, bool accepted) {
        var definition = Document(
            state: [Slot(
                    name: "width",
                    value: width
                ), Slot(
                    name: "pattern",
                    value: pattern
                ), Slot(
                    name: "mask",
                    value: 5
                ), Slot(
                    name: "repeated",
                    value: 7
                ), Slot(
                    name: "failed",
                    value: 0
                )],
            rules: [new WorldRule(
                    Name: Name(value: "replicate"),
                    Effects: [new ActionEffect.Transaction(
                            Effects: [
                    new ActionEffect.SetState(
                                    State: "mask",
                                    Expression: ValueExpression.Parse(text: "replicationMask(width)")
                                ),
                    new ActionEffect.SetState(
                                    State: "repeated",
                                    Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "pattern"), new ValueToken.State(Name: "width"), new ValueToken.RepeatBits(),
                    ])
                                ),
                ],
                            OnFailure: [new ActionEffect.SetState(
                                    State: "failed",
                                    Value: 1
                                )]
                        )]
                )]
        );
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        using var fixture = Fixtures.FreshServer(parsed);

        fixture.Step();
        Assert.Equal(
            (accepted
            ? 0L
            : 1L),
            Value(
                fixture: fixture,
                row: "failed"
            )
        );
        Assert.Equal(
            (accepted
            ? ((width == 64)
                ? 1L
                : 0x0101010101010101L)
            : 5L),
            Value(
                fixture: fixture,
                row: "mask"
            )
        );
        Assert.Equal(
            (accepted
            ? ((width == 64)
                ? long.MinValue
                : unchecked((long)0x8080808080808080UL))
            : 7L),
            Value(
                fixture: fixture,
                row: "repeated"
            )
        );
    }
    [Fact]
    public void ShiftCountAndZeroDivisorRefuseTransactionallyWhileMinusOneModuloIsZero() {
        var definition = Document(
            state: [Slot(
                    name: "target",
                    value: 5L
                ), Slot(
                    name: "failed",
                    value: 0L
                ), Slot(
                    name: "wrapped",
                    value: long.MinValue
                ), Slot(
                    name: "zero",
                    value: 9L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "bad-shift"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ValueExpression(Tokens: [
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.Constant(Value: 64m),
                            new ValueToken.ShiftLeft(),
                        ])
                                )],
                            OnFailure: [new ActionEffect.SetState(
                                    State: "failed",
                                    Value: 1m
                                )]
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "bad-modulo"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ValueExpression(Tokens: [
                            new ValueToken.Constant(Value: 1m),
                            new ValueToken.Constant(Value: 0m),
                            new ValueToken.Modulo(),
                        ])
                                )],
                            OnFailure: [new ActionEffect.AddState(
                                    State: "failed",
                                    Value: 1m
                                )]
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "min-modulo"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "zero",
                            Expression: new ValueExpression(Tokens: [
                        new ValueToken.State(Name: "wrapped"),
                        new ValueToken.Constant(Value: -1m),
                        new ValueToken.Modulo(),
                    ])
                        )]
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: 5L,
            actual: Value(
                fixture: fixture,
                row: "target"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Value(
                fixture: fixture,
                row: "failed"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Value(
                fixture: fixture,
                row: "zero"
            )
        );
    }
    [Fact]
    public void ZeroCensusSaturatesAtSixtyFourAndCarrierMinimumRefusesNegation() {
        var definition = Document(
            state: [Slot(
                    name: "zero",
                    value: 0L
                ), Slot(
                    name: "lead",
                    value: 0L
                ), Slot(
                    name: "trail",
                    value: 0L
                ), Slot(
                    name: "low",
                    value: 7L
                ), Slot(
                    name: "min",
                    value: long.MinValue
                ), Slot(
                    name: "target",
                    value: 5L
                ), Slot(
                    name: "failed",
                    value: 0L
                )],
            rules: [
                Rule(
                    name: "lead",
                    target: "lead",
                    tokens: [new ValueToken.State(Name: "zero"), new ValueToken.LeadingZeroCount()]
                ),
                Rule(
                    name: "trail",
                    target: "trail",
                    tokens: [new ValueToken.State(Name: "zero"), new ValueToken.TrailingZeroCount()]
                ),
                Rule(
                    name: "low",
                    target: "low",
                    tokens: [new ValueToken.State(Name: "zero"), new ValueToken.LowestSetBit()]
                ),
                new WorldRule(
                    Name: Name(value: "refuse-negate"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "min"), new ValueToken.Negate()])
                                )],
                            OnFailure: [new ActionEffect.AddState(
                                    State: "failed",
                                    Value: 1m
                                )]
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "refuse-abs"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "min"), new ValueToken.Abs()])
                                )],
                            OnFailure: [new ActionEffect.AddState(
                                    State: "failed",
                                    Value: 1m
                                )]
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "refuse-rotate"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ValueExpression(Tokens: [new ValueToken.State(Name: "low"), new ValueToken.Constant(Value: 64m), new ValueToken.RotateLeft()])
                                )],
                            OnFailure: [new ActionEffect.AddState(
                                    State: "failed",
                                    Value: 1m
                                )]
                        )]
                ),
            ]
        );
        var censusInFixed = Document(
            state: [FixedSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-census"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ValueExpression(Tokens: [new ValueToken.Constant(Value: 1m), new ValueToken.PopCount()])
                        )]
                )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: 64L,
            actual: Value(
                fixture: fixture,
                row: "lead"
            )
        );
        Assert.Equal(
            expected: 64L,
            actual: Value(
                fixture: fixture,
                row: "trail"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Value(
                fixture: fixture,
                row: "low"
            )
        );
        Assert.Equal(
            expected: 5L,
            actual: Value(
                fixture: fixture,
                row: "target"
            )
        );
        Assert.Equal(
            expected: 3L,
            actual: Value(
                fixture: fixture,
                row: "failed"
            )
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: censusInFixed,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "kind=Int expressions only"
        );
    }
}
