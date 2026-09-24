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
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldRule Rule(string name, string target, IReadOnlyList<Instruction> tokens) => new(
        Name: Name(value: name),
        Mode: ActionTriggerMode.Edge,
        Effects: [new ActionEffect.SetState(
                State: target,
                Expression: new ExpressionProgram(Instructions: tokens)
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
        )!.Value.Raw;
    }

    [Fact]
    public void BitCensusRotationsAndBoardSymmetriesReadTheCarrierExactly() {
        var board = (1L << 63) | (1L << 9) | 1L; // h8, b2, a1
        var definition = Document(
            state: [StateFixtures.IntSlot(
                    name: "board",
                    value: board
                ), StateFixtures.IntSlot(
                    name: "count",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "lowest",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "highest",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "next",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "rest",
                    value: 0L
                ),
                    StateFixtures.IntSlot(
                    name: "flip",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "turn",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "rot",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "neg",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "mag",
                    value: -6L
                ), StateFixtures.FixedSlot(
                    name: "sgn",
                    rawBits: FixedQ4816.FromInteger(value: -3).Value
                ), StateFixtures.FixedSlot(
                    name: "sign",
                    rawBits: 0L
                )],
            rules: [
                Rule(
                    name: "count",
                    target: "count",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.SetBitCount)]
                ),
                Rule(
                    name: "lowest",
                    target: "lowest",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.TrailingZeroCount)]
                ),
                Rule(
                    name: "highest",
                    target: "highest",
                    tokens: [Instruction.Constant(value: 63m), Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.LeadingZeroCount), Instruction.Of(operation: ExpressionOp.Subtract)]
                ),
                Rule(
                    name: "next",
                    target: "next",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.LowestSetBit)]
                ),
                Rule(
                    name: "rest",
                    target: "rest",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.ClearLowestSetBit)]
                ),
                Rule(
                    name: "flip",
                    target: "flip",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.ByteSwap)]
                ),
                Rule(
                    name: "turn",
                    target: "turn",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Of(operation: ExpressionOp.ReverseBits)]
                ),
                Rule(
                    name: "rot",
                    target: "rot",
                    tokens: [Instruction.Operand(name: "board"), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.RotateLeft), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.RotateRight)]
                ),
                Rule(
                    name: "neg",
                    target: "neg",
                    tokens: [Instruction.Operand(name: "count"), Instruction.Of(operation: ExpressionOp.Negate)]
                ),
                Rule(
                    name: "mag",
                    target: "mag",
                    tokens: [Instruction.Operand(name: "mag"), Instruction.Of(operation: ExpressionOp.Absolute)]
                ),
                Rule(
                    name: "sign",
                    target: "sign",
                    tokens: [Instruction.Operand(name: "sgn"), Instruction.Of(operation: ExpressionOp.Sign), Instruction.Constant(value: 2.5m), Instruction.Constant(value: 7.5m), Instruction.Of(operation: ExpressionOp.Select)]
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
            state: [StateFixtures.FixedSlot(
                    name: "target",
                    rawBits: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-bitwise"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m),
                    Instruction.Constant(value: 2m),
                    Instruction.Of(operation: ExpressionOp.BitAnd),
                ])
                        )]
                )]
        );
        var fixedCondition = Document(
            state: [StateFixtures.FixedSlot(
                    name: "target",
                    rawBits: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-condition"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m),
                    Instruction.Constant(value: 2m),
                    Instruction.Constant(value: 3m),
                    Instruction.Of(operation: ExpressionOp.Select),
                ])
                        )]
                )]
        );
        var danglingComparison = Document(
            state: [StateFixtures.FixedSlot(
                    name: "target",
                    rawBits: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "dangling"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m),
                    Instruction.Constant(value: 2m),
                    Instruction.Of(operation: ExpressionOp.Less),
                ])
                        )]
                )]
        );
        var underflow = Document(
            state: [StateFixtures.IntSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "underflow"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m),
                    Instruction.Constant(value: 2m),
                    Instruction.Of(operation: ExpressionOp.Select),
                ])
                        )]
                )]
        );
        var control = Document(
            state: [StateFixtures.IntSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "control"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m),
                    Instruction.Constant(value: 2m),
                    Instruction.Of(operation: ExpressionOp.Less),
                    Instruction.Constant(value: 7m),
                    Instruction.Constant(value: 9m),
                    Instruction.Of(operation: ExpressionOp.Select),
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
        Instruction[] tokens = [
            Instruction.Constant(value: 1m), Instruction.Constant(value: 2m), Instruction.Of(operation: ExpressionOp.Remainder),
            Instruction.Constant(value: 3m), Instruction.Of(operation: ExpressionOp.BitAnd), Instruction.Constant(value: 4m), Instruction.Of(operation: ExpressionOp.BitOr),
            Instruction.Constant(value: 5m), Instruction.Of(operation: ExpressionOp.BitXor), Instruction.Of(operation: ExpressionOp.BitNot),
            Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.ShiftLeft), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.ShiftRight),
            Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.ShiftRightLogical),
            Instruction.Constant(value: 6m), Instruction.Of(operation: ExpressionOp.Equal), Instruction.Constant(value: 0m), Instruction.Of(operation: ExpressionOp.NotEqual),
            Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.Less), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.LessOrEqual),
            Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.Greater), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.GreaterOrEqual),
            Instruction.Constant(value: 8m), Instruction.Constant(value: 9m), Instruction.Of(operation: ExpressionOp.Select),
            Instruction.Of(operation: ExpressionOp.SetBitCount), Instruction.Of(operation: ExpressionOp.LeadingZeroCount), Instruction.Of(operation: ExpressionOp.TrailingZeroCount),
            Instruction.Of(operation: ExpressionOp.LowestSetBit), Instruction.Of(operation: ExpressionOp.ClearLowestSetBit),
            Instruction.Constant(value: 3m), Instruction.Of(operation: ExpressionOp.RotateLeft), Instruction.Constant(value: 3m), Instruction.Of(operation: ExpressionOp.RotateRight),
            Instruction.Of(operation: ExpressionOp.ByteSwap), Instruction.Of(operation: ExpressionOp.ReverseBits), Instruction.Of(operation: ExpressionOp.Negate), Instruction.Of(operation: ExpressionOp.Absolute), Instruction.Of(operation: ExpressionOp.Sign),
            Instruction.Constant(value: 12m), Instruction.Of(operation: ExpressionOp.ParallelBitExtract), Instruction.Constant(value: 12m), Instruction.Of(operation: ExpressionOp.ParallelBitDeposit),
            Instruction.Constant(value: 1m), Instruction.Constant(value: 2m), Instruction.Of(operation: ExpressionOp.BitField),
            Instruction.Constant(value: 1m), Instruction.Constant(value: 1m), Instruction.Constant(value: 2m), Instruction.Of(operation: ExpressionOp.BitInsert),
        ];
        var definition = Document(
            state: [StateFixtures.IntSlot(
                    name: "target",
                    value: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "all"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: tokens)
                        )]
                )]
        );

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var effect = Assert.IsType<ActionEffect.SetState>(@object: Assert.Single(collection: Assert.Single(collection: (parsed.Rules ?? [])).Effects));
        var round = Assert.IsType<ExpressionProgram>(@object: effect.Expression).Instructions;

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
            state: [StateFixtures.IntSlot(
                    name: "board",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "big",
                    value: long.MaxValue
                ), StateFixtures.IntSlot(
                    name: "seen",
                    value: 0L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "set-corners"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "board",
                            Expression: new ExpressionProgram(Instructions: [
                            Instruction.Constant(value: 1m),
                            Instruction.Constant(value: 63m),
                            Instruction.Of(operation: ExpressionOp.ShiftLeft),
                            Instruction.Constant(value: 1m),
                            Instruction.Of(operation: ExpressionOp.BitOr),
                            Instruction.Of(operation: ExpressionOp.BitNot),
                            Instruction.Of(operation: ExpressionOp.BitNot),
                        ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "big-holds"),
                    Mode: ActionTriggerMode.Edge,
                    Gate: new ActionPredicate.CompareState(
                        State: "big",
                        Comparison: ExpressionOp.Greater,
                        Value: 140_737_488_355_327m
                    ),
                    Effects: [new ActionEffect.SetState(
                            State: "seen",
                            Expression: new ExpressionProgram(Instructions: [
                            Instruction.Operand(name: "big"),
                            Instruction.Constant(value: 1m),
                            Instruction.Of(operation: ExpressionOp.ShiftRightLogical),
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
            state: [StateFixtures.IntSlot(
                    name: "pos",
                    value: 37L
                ), StateFixtures.IntSlot(
                    name: "total",
                    value: 15L
                ), StateFixtures.IntSlot(
                    name: "ace",
                    value: 0L
                ), StateFixtures.FixedSlot(
                    name: "frac",
                    rawBits: 0L
                ), StateFixtures.FixedSlot(
                    name: "pick",
                    rawBits: 0L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "wrap"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "pos",
                            Expression: new ExpressionProgram(Instructions: [
                        Instruction.Operand(name: "pos"),
                        Instruction.Constant(value: 7m),
                        Instruction.Of(operation: ExpressionOp.Add),
                        Instruction.Constant(value: 40m),
                        Instruction.Of(operation: ExpressionOp.Remainder),
                    ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "ace"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "ace",
                            Expression: new ExpressionProgram(Instructions: [
                        Instruction.Operand(name: "total"),
                        Instruction.Constant(value: 11m),
                        Instruction.Of(operation: ExpressionOp.Add),
                        Instruction.Constant(value: 21m),
                        Instruction.Of(operation: ExpressionOp.LessOrEqual),
                        Instruction.Constant(value: 11m),
                        Instruction.Constant(value: 1m),
                        Instruction.Of(operation: ExpressionOp.Select),
                    ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "frac"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "frac",
                            Expression: new ExpressionProgram(Instructions: [
                        Instruction.Constant(value: 2.5m),
                        Instruction.Constant(value: 1m),
                        Instruction.Of(operation: ExpressionOp.Remainder),
                    ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "pick"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "pick",
                            Expression: new ExpressionProgram(Instructions: [
                        Instruction.Constant(value: 0.25m),
                        Instruction.Constant(value: 0.5m),
                        Instruction.Of(operation: ExpressionOp.Greater),
                        Instruction.Constant(value: 3m),
                        Instruction.Constant(value: 0.5m),
                        Instruction.Of(operation: ExpressionOp.Select),
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
            state: [StateFixtures.IntSlot(
                    name: "packed",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "spread",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "field",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "inserted",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "refused",
                    value: 7L
                )],
            rules: [
                new WorldRule(
                    Name: Name(value: "pext"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "packed",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 176m), Instruction.Constant(value: 240m), Instruction.Of(operation: ExpressionOp.ParallelBitExtract),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "pdep"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "spread",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 11m), Instruction.Constant(value: 240m), Instruction.Of(operation: ExpressionOp.ParallelBitDeposit),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "field"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "field",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 4660m), Instruction.Constant(value: 4m), Instruction.Constant(value: 8m), Instruction.Of(operation: ExpressionOp.BitField),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "insert"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "inserted",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 4660m), Instruction.Constant(value: 255m), Instruction.Constant(value: 4m), Instruction.Constant(value: 8m), Instruction.Of(operation: ExpressionOp.BitInsert),
                ])
                        )]
                ),
                new WorldRule(
                    Name: Name(value: "too-wide"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.SetState(
                            State: "refused",
                            Expression: new ExpressionProgram(Instructions: [
                    Instruction.Constant(value: 1m), Instruction.Constant(value: 60m), Instruction.Constant(value: 8m), Instruction.Of(operation: ExpressionOp.BitField),
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
    [InlineData(8L, 0x80L, true, 0x0101010101010101L, unchecked((long)0x8080808080808080UL))]
    [InlineData(64L, long.MinValue, true, 1L, long.MinValue)]
    [InlineData(7L, 1L, true, unchecked((long)0x8102040810204081UL), unchecked((long)0x8102040810204081UL))]
    [InlineData(65L, 1L, false, 5L, 7L)]
    [InlineData(8L, 256L, false, 5L, 7L)]
    [Theory]
    public void ReplicationReadsLiveOperandsAndRefusesTheWholeTransaction(long width, long pattern, bool accepted, long mask, long repeated) {
        var definition = Document(
            state: [StateFixtures.IntSlot(
                    name: "width",
                    value: width
                ), StateFixtures.IntSlot(
                    name: "pattern",
                    value: pattern
                ), StateFixtures.IntSlot(
                    name: "mask",
                    value: 5
                ), StateFixtures.IntSlot(
                    name: "repeated",
                    value: 7
                ), StateFixtures.IntSlot(
                    name: "failed",
                    value: 0
                )],
            rules: [new WorldRule(
                    Name: Name(value: "replicate"),
                    Effects: [new ActionEffect.Transaction(
                            Effects: [
                    new ActionEffect.SetState(
                                    State: "mask",
                                    Expression: ExpressionProgram.Parse(text: "replicationMask(width)")
                                ),
                    new ActionEffect.SetState(
                                    State: "repeated",
                                    Expression: new ExpressionProgram(Instructions: [
                        Instruction.Operand(name: "pattern"), Instruction.Operand(name: "width"), Instruction.Of(operation: ExpressionOp.RepeatBits),
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
            mask,
            Value(
                fixture: fixture,
                row: "mask"
            )
        );
        Assert.Equal(
            repeated,
            Value(
                fixture: fixture,
                row: "repeated"
            )
        );
    }
    [Fact]
    public void ShiftCountAndZeroDivisorRefuseTransactionallyWhileMinusOneModuloIsZero() {
        var definition = Document(
            state: [StateFixtures.IntSlot(
                    name: "target",
                    value: 5L
                ), StateFixtures.IntSlot(
                    name: "failed",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "wrapped",
                    value: long.MinValue
                ), StateFixtures.IntSlot(
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
                                    Expression: new ExpressionProgram(Instructions: [
                            Instruction.Constant(value: 1m),
                            Instruction.Constant(value: 64m),
                            Instruction.Of(operation: ExpressionOp.ShiftLeft),
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
                                    Expression: new ExpressionProgram(Instructions: [
                            Instruction.Constant(value: 1m),
                            Instruction.Constant(value: 0m),
                            Instruction.Of(operation: ExpressionOp.Remainder),
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
                            Expression: new ExpressionProgram(Instructions: [
                        Instruction.Operand(name: "wrapped"),
                        Instruction.Constant(value: -1m),
                        Instruction.Of(operation: ExpressionOp.Remainder),
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
            state: [StateFixtures.IntSlot(
                    name: "zero",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "lead",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "trail",
                    value: 0L
                ), StateFixtures.IntSlot(
                    name: "low",
                    value: 7L
                ), StateFixtures.IntSlot(
                    name: "min",
                    value: long.MinValue
                ), StateFixtures.IntSlot(
                    name: "target",
                    value: 5L
                ), StateFixtures.IntSlot(
                    name: "failed",
                    value: 0L
                )],
            rules: [
                Rule(
                    name: "lead",
                    target: "lead",
                    tokens: [Instruction.Operand(name: "zero"), Instruction.Of(operation: ExpressionOp.LeadingZeroCount)]
                ),
                Rule(
                    name: "trail",
                    target: "trail",
                    tokens: [Instruction.Operand(name: "zero"), Instruction.Of(operation: ExpressionOp.TrailingZeroCount)]
                ),
                Rule(
                    name: "low",
                    target: "low",
                    tokens: [Instruction.Operand(name: "zero"), Instruction.Of(operation: ExpressionOp.LowestSetBit)]
                ),
                new WorldRule(
                    Name: Name(value: "refuse-negate"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.Transaction(
                            Effects: [new ActionEffect.SetState(
                                    State: "target",
                                    Expression: new ExpressionProgram(Instructions: [Instruction.Operand(name: "min"), Instruction.Of(operation: ExpressionOp.Negate)])
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
                                    Expression: new ExpressionProgram(Instructions: [Instruction.Operand(name: "min"), Instruction.Of(operation: ExpressionOp.Absolute)])
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
                                    Expression: new ExpressionProgram(Instructions: [Instruction.Operand(name: "low"), Instruction.Constant(value: 64m), Instruction.Of(operation: ExpressionOp.RotateLeft)])
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
            state: [StateFixtures.FixedSlot(
                    name: "target",
                    rawBits: 0L
                )],
            rules: [new WorldRule(
                    Name: Name(value: "fixed-census"),
                    Effects: [new ActionEffect.SetState(
                            State: "target",
                            Expression: new ExpressionProgram(Instructions: [Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.SetBitCount)])
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
