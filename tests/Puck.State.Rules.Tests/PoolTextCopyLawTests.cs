using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: text copies use typed reads across ordinary rows and generation-checked pool
/// fields, while numeric predicate sites refuse text pool operands at compile time.</summary>
public sealed class PoolTextCopyLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section() => new(
        Spaces: [new StateSpace(Name: Name(value: "pose"), Model: "test", Revision: "1", Dimensions: 8)],
        Rows: [
            new StateRow(Name: Name(value: "source"), Kind: CellKind.Text, Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Text(value: "copied"))]),
            new StateRow(Name: Name(value: "destination"), Kind: CellKind.Text, Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Text(value: "unchanged"))]),
        ],
        Records: [new StateRecord(Name: Name(value: "piece"), Fields: [
            new StatePoolField(Name: Name(value: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: "fresh")),
            new StatePoolField(Name: Name(value: "pose"), Kind: CellKind.Vector, Default: CellValue.Vector(components: new sbyte[8]), Space: Name(value: "pose"), Dimensions: 8),
        ])],
        Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 2, Initial: [
            new StatePoolSeed(Slot: 0),
            new StatePoolSeed(Slot: 1),
        ])]
    );
    private static (StateArena Arena, RuleCompileContext Context, RuleEvaluator Evaluator) Arrange() {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);

        return (arena, context, new RuleEvaluator(host: new ArenaEffectHost(arena: arena)));
    }
    private static string RowText(StateArena arena, string name) => arena.ToRows().Single(predicate: row => (row.Name.Value == name)).Cells!.Single().Value.AsText;

    [Fact]
    public void OrdinaryAndLexicalPoolTextSourcesCopyInBothDirections() {
        var (arena, context, evaluator) = Arrange();
        var rule = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "copy"), Effects: [
            new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "label"), FromState: "source"),
                new ActionEffect.SetState(State: "destination", FromState: StateChannelRef.OfBindingField(binding: "piece", field: "label")),
            ]),
        ]));

        Assert.True(condition: evaluator.Evaluate(rules: [rule], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.Equal(expected: "copied", actual: RowText(arena: arena, name: "destination"));
        var pool = arena.Catalog.Pools[0];

        foreach (var handle in arena.SnapshotPool(poolOrdinal: pool.Ordinal)) {
            Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value));
            Assert.Equal(expected: "copied", actual: value.AsText);
        }
    }
    [Fact]
    public void StaticPoolTextCopiesAndAnAbsentSourceIsANoOp() {
        var (arena, context, evaluator) = Arrange();
        var intoPool = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "intoPool"), Effects: [
            new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label"), FromState: "source"),
            new ActionEffect.SetState(State: "destination", FromState: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label")),
        ]));

        Assert.True(condition: evaluator.Evaluate(rules: [intoPool], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.Equal(expected: "copied", actual: RowText(arena: arena, name: "destination"));

        var pool = arena.Catalog.Pools[0];
        var source = arena.Catalog.CreateInstanceHandle(poolOrdinal: pool.Ordinal, slot: 0, generation: 0L);

        Assert.True(condition: arena.TryRelease(handle: source, reason: out _));
        var reset = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "reset"), Effects: [
            new ActionEffect.SetState(State: "destination", Text: "unchanged"),
        ]));

        Assert.True(condition: evaluator.Evaluate(rules: [reset], latch: new RuleLatch(), stepTicks: 1UL));
        var absent = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "absent"), Effects: [
            new ActionEffect.SetState(State: "destination", FromState: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label")),
        ]));

        Assert.False(condition: evaluator.Evaluate(rules: [absent], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.Equal(expected: "unchanged", actual: RowText(arena: arena, name: "destination"));
        Assert.Empty(collection: evaluator.Diagnostics());

        Assert.True(condition: arena.TryClaim(poolOrdinal: pool.Ordinal, handle: out var replacement, reason: out var claimReason), userMessage: claimReason);
        Assert.Equal(expected: 1L, actual: replacement.Generation);
        Assert.True(condition: evaluator.Evaluate(rules: [intoPool], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: replacement, value: out var replacementLabel));
        Assert.Equal(expected: "copied", actual: replacementLabel.AsText);
        Assert.Equal(expected: "copied", actual: RowText(arena: arena, name: "destination"));
    }
    [Fact]
    public void NumericPredicateSitesRefuseStaticAndLexicalTextPoolOperands() {
        var (_, context, _) = Arrange();
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(
            Name: Name(value: "staticGate"),
            Gate: new ActionPredicate.CompareState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label"), Comparison: ActionStateComparison.Equal, Value: 0m),
            Effects: [new ActionEffect.SetState(State: "destination", Text: "bad")]
        )));
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "lexicalGate"), Effects: [
            new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.If(
                    Condition: new ActionPredicate.CompareState(State: StateChannelRef.OfBindingField(binding: "piece", field: "label"), Comparison: ActionStateComparison.Equal, Value: 0m),
                    Then: [new ActionEffect.SetState(State: "destination", Text: "bad")]
                ),
            ]),
        ])));
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(
            Name: Name(value: "staticVectorGate"),
            Gate: new ActionPredicate.CompareState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "pose"), Comparison: ActionStateComparison.Equal, Value: 0m),
            Effects: [new ActionEffect.SetState(State: "destination", Text: "bad")]
        )));
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "lexicalVectorGate"), Effects: [
            new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.If(
                    Condition: new ActionPredicate.CompareState(State: StateChannelRef.OfBindingField(binding: "piece", field: "pose"), Comparison: ActionStateComparison.Equal, Value: 0m),
                    Then: [new ActionEffect.SetState(State: "destination", Text: "bad")]
                ),
            ]),
        ])));
    }
}
