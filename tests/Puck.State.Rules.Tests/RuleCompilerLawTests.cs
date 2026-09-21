using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>What the compiler admits and what it refuses, over the arena-addressed program every rule lowers to.</summary>
public sealed class RuleCompilerLawTests {
    [Fact]
    public void ACompiledCellReferenceCarriesTheRowsCatalogOrdinalAndAnInternedKey() {
        var context = RulesFixture.Context();
        var rule = RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Greater,
                Key: "a",
                State: "codes",
                Value: 0m
            ),
            name: "gate"
        );
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: rule
        );
        var operand = Assert.IsType<StateCellOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "codes"
        ));
        Assert.Equal(
            actual: operand.RowOrdinal,
            expected: handle.Ordinal
        );
        Assert.True(condition: operand.Key.IsValid);
        Assert.Equal(
            actual: context.Catalog.Keys[operand.Key].Value,
            expected: "a"
        );
    }
    [Fact]
    public void ACompiledEffectAddressesItsDestinationByOrdinal() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.AddState(
                    State: "score",
                    Value: 3m
                )],
                name: "bump"
            )
        );
        var effect = Assert.IsType<WriteEffect>(@object: compiled.Effects[0]);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.Equal(
            actual: effect.RowOrdinal,
            expected: handle.Ordinal
        );
        Assert.Equal(
            actual: effect.Write,
            expected: StateWriteKind.Add
        );
    }
    [Fact]
    public void AGateNestsAllAnyAndNotIntoOnePostfixProgram() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.All(Predicates: [
                new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Greater,
                    State: "score",
                    Value: 0m
                ),
                new ActionPredicate.Not(Predicate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Equal,
                    State: "score",
                    Value: 9m
                )),
            ]),
            name: "nested"
        ));

        Assert.Equal(
            actual: compiled.Gate.Length,
            expected: 4
        );
        Assert.Equal(
            actual: compiled.Gate[^1].Op,
            expected: GateOp.All
        );
        Assert.Equal(
            actual: compiled.Gate[^1].Arity,
            expected: 2
        );
    }
    [Fact]
    public void AKeyedRowReadWithoutAKeyIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "codes",
                Value: 0m
            ),
            name: "bare"
        )),
        expected: RuleRefusal.StateCellUnaddressable
    );
    [Fact]
    public void AnUndeclaredCellReadIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                Key: "zz",
                State: "codes",
                Value: 0m
            ),
            name: "ghost"
        )),
        expected: RuleRefusal.StateCellUndeclared
    );
    // The evaluator holds a rule's bound values in one fixed buffer, so the ceiling prices every binding that lands in
    // it: the declared ones and the one each computed key inside a declared binding's expression mints.
    [Fact]
    public void DeclaredBindingsAndTheKeysTheirExpressionsMintArePricedTogether() {
        RuleLocal Bind(string name, string text) => new(
            Expression: RulesFixture.Program(text: text),
            Kind: CellKind.Int,
            Name: RulesFixture.Name(value: name)
        );
        Rule Declaring(int computed) => new(
            Locals: [
                Bind(
                    name: "k",
                    text: "0"
                ),
                .. Enumerable.Range(
                    count: computed,
                    start: 1
                ).Select(selector: index => Bind(
                    name: $"b{index}",
                    text: $"codes[($local:k)] + {index}"
                )),
            ],
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "$local:b1"),
                State: "score"
            )],
            Name: RulesFixture.Name(value: "crowded")
        );

        // One declared value, the one key every later binding reads through, and two fewer declared bindings than
        // the ceiling fill the buffer exactly.
        Assert.Equal(
            actual: RulesFixture.Compile(rule: Declaring(computed: (RuleCapacity.MaxLocalsPerRule - 2))).Locals!.Length,
            expected: RuleCapacity.MaxLocalsPerRule
        );
        // One more declared binding still passes the authored count, and the shared key puts it one past the ceiling.
        Assert.Equal(
            actual: RulesFixture.Refusal(rule: Declaring(computed: (RuleCapacity.MaxLocalsPerRule - 1))),
            expected: RuleRefusal.EffectKindInadmissible
        );
    }
    // A local that declares no kind takes the one its expression compiles under: Int where it does, Fixed otherwise.
    // A fractional literal compared against an Int row is an Int expression, which a reading of the literals alone
    // would call Fixed.
    [InlineData("score + 1", CellKind.Int)]
    [InlineData("3", CellKind.Int)]
    [InlineData("score < 2.5 ? 1 : 0", CellKind.Int)]
    [InlineData("ratio * 2", CellKind.Fixed)]
    [InlineData("ratio + 0.5", CellKind.Fixed)]
    [InlineData("1.5 + 1", CellKind.Fixed)]
    [InlineData("1.5 == 1.5", CellKind.Int)]
    [InlineData("sign(0.5)", CellKind.Int)]
    [Theory]
    public void ALocalThatDeclaresNoKindTakesTheKindItsExpressionCompilesUnder(string expression, CellKind kind) => Assert.Equal(
        actual: RulesFixture.Compile(rule: new Rule(
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "1"),
                State: "score"
            )],
            Locals: [new RuleLocal(
                Expression: RulesFixture.Program(text: expression),
                Name: RulesFixture.Name(value: "value")
            )],
            Name: RulesFixture.Name(value: "inferred")
        )).Locals![0].Kind,
        expected: kind
    );
    // The local's numeric carrier is Fixed for the fractional literal, while comparison and sign are Int results.
    // Both therefore retain the literal's exact value before leaving the integer a later effect reads.
    [Theory]
    [InlineData("1.5 == 1.5")]
    [InlineData("sign(0.5)")]
    public void AnInferredLocalRetainsFractionalOperandsWhenItLeavesAnIntResult(string text) {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Effects: [new ActionEffect.SetState(
                    Expression: RulesFixture.Program(text: "$local:value"),
                    State: "score"
                )],
                Locals: [new RuleLocal(
                    Expression: RulesFixture.Program(text: text),
                    Name: RulesFixture.Name(value: "value")
                )],
                Name: RulesFixture.Name(value: "inferred")
            )]
        );
        var local = Assert.Single(collection: rules[0].Locals!);

        Assert.Equal(
            expected: CellKind.Int,
            actual: local.Kind
        );
        Assert.Equal(
            expected: CellKind.Fixed,
            actual: local.CarrierKind
        );
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            )
        );
    }
    [Fact]
    public void AnIntResultFromAFixedCarrierFeedsALaterLocalAndGate() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Effects: [new ActionEffect.SetState(
                    Expression: RulesFixture.Program(text: "$local:next"),
                    State: "score"
                )],
                Gate: new ActionPredicate.CompareValue(
                    Kind: CellKind.Int,
                    Comparison: ActionStateComparison.Equal,
                    Left: RulesFixture.Program(text: "$local:next"),
                    Right: RulesFixture.Program(text: "2")
                ),
                Locals: [
                    new RuleLocal(
                        Expression: RulesFixture.Program(text: "sign(0.5)"),
                        Name: RulesFixture.Name(value: "sign")
                    ),
                    new RuleLocal(
                        Expression: RulesFixture.Program(text: "$local:sign + 1"),
                        Name: RulesFixture.Name(value: "next")
                    ),
                ],
                Name: RulesFixture.Name(value: "chained")
            )]
        );

        var (sign, next) = (rules[0].Locals![0], rules[0].Locals![1]);

        Assert.Equal(expected: CellKind.Fixed, actual: sign.CarrierKind);
        Assert.Equal(expected: CellKind.Int, actual: sign.Kind);
        Assert.Equal(expected: CellKind.Int, actual: next.CarrierKind);
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            expected: 2L,
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            )
        );
    }
    [Fact]
    public void ALocalThatMixesAnIntRowAndAFixedRowIsRefusedForItsIntReading() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: new Rule(
            Effects: [],
            Locals: [new RuleLocal(
                Expression: RulesFixture.Program(text: "score + ratio"),
                Name: RulesFixture.Name(value: "value")
            )],
            Name: RulesFixture.Name(value: "mixed")
        )),
        expected: RuleRefusal.EffectSourceKindMismatch
    );
    [Fact]
    public void ABindingIsReadBackByItsOrdinal() {
        var compiled = RulesFixture.Compile(rule: new Rule(
            Locals: [new RuleLocal(
                Expression: RulesFixture.Program(text: "score + 1"),
                Kind: CellKind.Int,
                Name: RulesFixture.Name(value: "next")
            )],
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "$local:next"),
                State: "score"
            )],
            Name: RulesFixture.Name(value: "bound")
        ));
        var effect = Assert.IsType<WriteEffect>(@object: compiled.Effects[0]);
        var operand = Assert.IsType<LocalOperand>(@object: effect.Source.Expression![0].Operand);

        Assert.Equal(
            actual: operand.Ordinal,
            expected: 0
        );
        Assert.Equal(
            actual: (compiled.Locals?.Length ?? 0),
            expected: 1
        );
    }
    [Fact]
    public void AnExpressionKeyBecomesAnImplicitBindingTheCellReferenceReadsBack() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                Key: "$expr:score + 0",
                State: "codes",
                Value: 1m
            )],
            name: "keyed"
        ));
        var effect = Assert.IsType<WriteEffect>(@object: compiled.Effects[0]);

        Assert.NotNull(@object: effect.KeyFrom);
        Assert.IsType<LocalKeyFact>(@object: effect.KeyFrom!.Value.Custom);
        Assert.Equal(
            actual: (compiled.Locals?.Length ?? 0),
            expected: 1
        );
    }
    [Fact]
    public void AForEachRowLeavesItsCatalogOrdinalOnTheCompiledRule() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.SetState(
                    Key: "$each",
                    State: "codes",
                    Value: 1m
                )],
                forEach: "tokens",
                name: "each"
            )
        );

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "tokens"
        ));
        Assert.Equal(
            actual: compiled.ForEachOrdinal,
            expected: handle.Ordinal
        );
        Assert.False(condition: compiled.ForEachZones);
    }
    [Fact]
    public void AZoneTableCompilesItsEntriesToOrdinalsAndItsIndicesToKeys() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                forEach: RuleFacts.ForEachZones,
                name: "zoned",
                zones: ["deck", "hand"]
            )
        );
        var zones = Assert.IsType<ZoneTable>(@object: compiled.Zones);

        Assert.True(condition: compiled.ForEachZones);
        Assert.Equal(
            actual: zones.Ordinals.Count,
            expected: 2
        );
        Assert.All(
            action: ordinal => Assert.True(condition: (ordinal >= 0)),
            collection: zones.Ordinals
        );
        Assert.Equal(
            actual: zones.Indices.Length,
            expected: 2
        );
        Assert.Equal(
            actual: zones.TokenDomain,
            expected: "tokens"
        );
    }
    [Fact]
    public void ALiveZoneReadResolvesThroughTheRulesOwnTable() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Greater,
                Key: "a",
                State: "$zones[$each]",
                Value: 0m
            ),
            forEach: "tokens",
            name: "live",
            zones: ["deck", "hand"]
        ));
        var operand = Assert.IsType<StateCellOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.NotNull(@object: operand.RowFrom);
        Assert.Equal(
            actual: operand.RowOrdinal,
            expected: -1
        );
        Assert.NotNull(@object: operand.RowFrom!.Table);
    }
    [Fact]
    public void AFamilyIndexSelectsAMemberRowLiveWhereverAZoneIndexIsAccepted() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Greater,
                    Key: StateRow.SlotKey.Value,
                    State: "slot[$each]",
                    Value: 0m
                ),
                forEach: "tokens",
                name: "family"
            )
        );
        var operand = Assert.IsType<StateCellOperand>(@object: compiled.Gate[0].LeftSource.Operand);
        var family = operand.RowFrom?.Family;

        Assert.NotNull(@object: family);
        Assert.Equal(
            actual: family!.Value.Count,
            expected: 2
        );
        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "slot0"
        ));
        Assert.Equal(
            actual: family.Value.FirstOrdinal,
            expected: handle.Ordinal
        );
    }
    [Fact]
    public void AReductionCarriesItsRowOrdinalAndItsFilterOrdinal() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Greater,
                    State: "$reduce:count:codes:where:tokens",
                    Value: 0m
                ),
                name: "reduce"
            )
        );
        var operand = Assert.IsType<ReductionOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var codes,
            lane: StateLane.Document,
            name: "codes"
        ));
        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var tokens,
            lane: StateLane.Document,
            name: "tokens"
        ));
        Assert.Equal(
            actual: operand.RowOrdinal,
            expected: codes.Ordinal
        );
        Assert.Equal(
            actual: operand.FilterOrdinal,
            expected: tokens.Ordinal
        );
        Assert.Equal(
            actual: operand.ValueKind,
            expected: CellKind.Int
        );
    }
    [Fact]
    public void ATransactionCompilesToASavepointAndNeverNests() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.Transaction(
                Effects: [new ActionEffect.SetState(
                    State: "score",
                    Value: 1m
                )],
                OnFailure: [new ActionEffect.SetState(
                    State: "score",
                    Value: 0m
                )]
            )],
            name: "atomic"
        ));
        var savepoint = Assert.IsType<TransactionEffect>(@object: compiled.Effects[0]);

        Assert.Single(collection: savepoint.Effects);
        Assert.Single(collection: savepoint.OnFailure);
        Assert.Equal(
            actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
                effects: [new ActionEffect.Transaction(Effects: [new ActionEffect.Transaction(Effects: [new ActionEffect.SetState(
                    State: "score",
                    Value: 1m
                )])])],
                name: "nested"
            )),
            expected: RuleRefusal.EffectKindInadmissible
        );
    }
    [Fact]
    public void AnEffectFamilyCarriesNoTransactionOrBranchAdmission() {
        var family = typeof(EffectFamily);

        Assert.Null(@object: family.GetProperty(name: "AllowsTransaction"));
        Assert.Null(@object: family.GetProperty(name: "AllowsInsideBranch"));
    }
    [Fact]
    public void AnIfBranchCompilesBothArmsIntoOneEffect() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.If(
                Condition: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Greater,
                    State: "score",
                    Value: 0m
                ),
                Else: [new ActionEffect.SetState(
                    State: "score",
                    Value: 0m
                )],
                Then: [new ActionEffect.SetState(
                    State: "score",
                    Value: 2m
                )]
            )],
            name: "branch"
        ));
        var branch = Assert.IsType<IfEffect>(@object: compiled.Effects[0]);

        Assert.Single(collection: branch.Then);
        Assert.Single(collection: branch.Else);
        Assert.Single(collection: branch.Condition);
    }
    [Fact]
    public void AValueSecondsThatIsNotAWholeEngineTickIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                State: "cooldown",
                ValueSeconds: 0.0000001m
            )],
            name: "inexact"
        )),
        expected: RuleRefusal.DurationNotExactEngineTicks
    );
    [Fact]
    public void AWholeEngineTickDurationCompilesToItsExactTickCount() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                State: "cooldown",
                ValueSeconds: 1m
            )],
            name: "exact"
        ));
        var effect = Assert.IsType<WriteEffect>(@object: compiled.Effects[0]);

        Assert.Equal(
            actual: effect.Source.RawValue,
            expected: ((long)Puck.Maths.FixedTickConversion.TicksPerSecond)
        );
    }
    [Fact]
    public void AConstantSubtreeFoldsBeforeTheProgramRuns() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "2 + 3"),
                State: "score"
            )],
            name: "folded"
        ));
        var effect = Assert.IsType<WriteEffect>(@object: compiled.Effects[0]);

        Assert.Single(collection: effect.Source.Expression!);
        Assert.Equal(
            actual: effect.Source.Expression![0].Constant,
            expected: 5L
        );
    }
    [Fact]
    public void AMatchOperandCarriesItsCompiledPatternAndAttributeOrdinal() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Equal,
                    State: "$match:run:deck",
                    Value: 1m
                ),
                name: "match"
            )
        );
        var operand = Assert.IsType<PatternOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var codes,
            lane: StateLane.Document,
            name: "codes"
        ));
        Assert.Equal(
            actual: operand.AttributeOrdinal,
            expected: codes.Ordinal
        );
        Assert.Equal(
            actual: operand.Pattern.Source.Name.Value,
            expected: "run"
        );
    }
    [Fact]
    public void ABoardQueryCompilesAgainstItsDeclaredTopology() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.GreaterOrEqual,
                Key: "0",
                State: "$board:neighbour:board:E",
                Value: 0m
            ),
            name: "board"
        ));
        var operand = Assert.IsType<BoardOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.Equal(
            actual: operand.Board.Topology.CellCount,
            expected: 4
        );
    }
    [Fact]
    public void AGenerateOnABootTimedSiteIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            effects: [new ActionEffect.Generate(Row: "boot")],
            name: "redraw"
        )),
        expected: RuleRefusal.GeneratorUnknown
    );
    [Fact]
    public void AGenerateOnAnEventTimedSiteCompilesToItsRowOrdinal() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.Generate(Row: "deal")],
                name: "draw"
            )
        );
        var effect = Assert.IsType<GenerateEffect>(@object: compiled.Effects[0]);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "deal"
        ));
        Assert.Equal(
            actual: effect.RowOrdinal,
            expected: handle.Ordinal
        );
    }
    [Fact]
    public void ATransferCompilesItsEndsToOrdinals() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                    From: "deck",
                    Selector: ZoneSelector.First,
                    To: "hand"
                ))],
                name: "move"
            )
        );
        var effect = Assert.IsType<TransformStateEffect>(@object: compiled.Effects[0]);
        var writes = new List<CellAccess>();

        effect.CollectWrites(into: writes);

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var deck,
            lane: StateLane.Document,
            name: "deck"
        ));
        Assert.Contains(
            collection: writes,
            filter: write => (write.RowOrdinal == deck.Ordinal)
        );
    }
    [Fact]
    public void AMixedKindComparisonIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                ComparandState: "ratio",
                State: "score"
            ),
            name: "mixed"
        )),
        expected: RuleRefusal.ComparandKindMismatch
    );
    [Fact]
    public void ATextRowComparedAsANumberIsRefusedByName() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "label",
                Value: 0m
            ),
            name: "text"
        )),
        expected: RuleRefusal.StateCellUnaddressable
    );
}
