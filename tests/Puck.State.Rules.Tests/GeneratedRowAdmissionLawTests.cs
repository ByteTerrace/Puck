using System.Text;
using System.Text.Json;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: authored names cannot select generated pool storage, even through a channel
/// object. Pool handles still resolve generated field metadata by ordinal for evaluation and costing.</summary>
public sealed class GeneratedRowAdmissionLawTests {
    private static RuleCompileContext Context() {
        var section = new StateSection(
            Rows: [new StateRow(Name: CellName.Parse(candidate: "counter"), Kind: CellKind.Int)],
            Records: [new StateRecord(Name: CellName.Parse(candidate: "unit"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "hp"))])],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "us"), Record: CellName.Parse(candidate: "unit"), Capacity: 3)]
        );

        return new RuleCompileContext(section: section, catalog: StateCatalog.Compile(section: section), tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
    }
    private static StateChannelRef Channel(string name) {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(s: (("{\"channel\":\"" + name[1..]) + "\"}")));

        Assert.True(condition: reader.Read());
        return Assert.IsType<StateChannelRef>(@object: new StateChannelRefJsonConverter().Read(ref reader, typeof(StateChannelRef), JsonSerializerOptions.Default));
    }

    [InlineData("forEach")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("sort")]
    [InlineData("shuffle")]
    [InlineData("arrangementRank")]
    [InlineData("vectorSource")]
    [Theory]
    public void EveryAuthoredConsumerRefusesGeneratedRowsByNameBeforeEvaluation(string consumer) {
        var context = Context();

        foreach (var row in context.Rows.Where(predicate: static row => row.Generated)) {
            var reference = Channel(name: row.Name.Value);

            Assert.Equal(expected: row.Name.Value, actual: reference.Spelling);
            var rule = new Rule(Name: CellName.Parse(candidate: "probe"), Effects: [new ActionEffect.SetState(State: "counter", Value: 1m)]);

            rule = consumer switch {
                "forEach" => rule with { ForEach = reference },
                "read" => rule with { Gate = new ActionPredicate.CompareState(State: reference, Key: "0", Comparison: ActionStateComparison.Equal, Value: 1m) },
                "write" => rule with { Effects = [new ActionEffect.SetState(State: reference, Key: "0", Value: 1m)] },
                "sort" => rule with { Effects = [new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: reference, By: [new SortKey(Row: reference)]))] },
                "shuffle" => rule with { Effects = [new ActionEffect.TransformState(Transform: new StateTransform.Shuffle(Draw: "unused", Row: reference))] },
                "arrangementRank" => rule with { Gate = new ActionPredicate.CompareState(State: StateChannelRef.OfCall(call: new ChannelCall(Channel: "reduce", Arguments: [new ChannelArgument.Word(Text: "arrangementRank"), new ChannelArgument.Word(Text: row.Name.Value)])), Comparison: ActionStateComparison.Equal, Value: 0m) },
                "vectorSource" => rule with { Effects = [new ActionEffect.TransformState(Transform: new StateTransform.Mean(From: row.Name.Value, Into: "counter"))] },
                _ => throw new InvalidOperationException(),
            };
            var refusal = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(rule: rule, context: context));

            Assert.Contains(expectedSubstring: row.Name.Value, actualString: refusal.Message);
        }
    }
    [Fact]
    public void UndoSelectorsRefuseGeneratedRowsButAcceptTheLogicalPool() {
        var context = Context();
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(Name: CellName.Parse(candidate: "probe"), Effects: [new ActionEffect.SetState(State: "counter", Value: 1m)])]);
        var group = new RuleGroupDeclaration(Name: CellName.Parse(candidate: "turn"), Shape: RuleGroupShape.Staged,
            Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "probe"))], Undo: new RuleGroupUndo(Rows: [CellName.Parse(candidate: "us"), CellName.Parse(candidate: "counter")], Depth: 2));

        Assert.Single(collection: RuleCompiler.CompileGroups(context: context, groups: [group], rules: rules));
        foreach (var row in context.Rows.Where(predicate: static row => row.Generated)) {
            var refused = group with { Undo = group.Undo! with { Rows = [row.Name, CellName.Parse(candidate: "counter")] } };
            var exception = Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileGroups(context: context, groups: [refused], rules: rules));

            Assert.Contains(expectedSubstring: row.Name.Value, actualString: exception.Message);
        }
    }
    [Fact]
    public void GeneratedFieldMetadataRemainsAvailableOnlyThroughItsResolvedOrdinal() {
        var context = Context();
        var pool = Assert.Single(collection: context.Catalog.Pools);
        var field = Assert.Single(collection: pool.Fields);
        var metadata = context.FindRowAt(rowOrdinal: field.RowOrdinal);

        Assert.NotNull(@object: metadata);
        Assert.True(condition: metadata.Generated);
        Assert.Null(@object: context.FindRow(name: metadata.Name.Value));
        Assert.Equal(expected: 3L, actual: context.RowCapacity(rowOrdinal: field.RowOrdinal));
    }
}
