using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldStateDocumentValuesLawTests {
    private static DocumentIdentifier Bound(string reference = "state.label") =>
        JsonSerializer.Deserialize<DocumentIdentifier>(json: JsonSerializer.Serialize(value: reference))!;

    private static WorldDefinition Source() => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [
            new WorldStateRow(Name: CellName.Parse(candidate: "label"), Kind: CellKind.Text,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Text: "resolved")]),
        ])
    );

    [Fact]
    public void LiteralBranchesArePrunedWhileSiblingReferencesRemainVisible() {
        var literal = new LiteralBranch();
        var graph = new Pair<LiteralBranch, DocumentIdentifier>(literal, new DocumentIdentifier(value: "literal"));

        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: graph));
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph with { Right = Bound() }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new[] { literal, literal }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new List<LiteralBranch> { literal }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new Dictionary<string, LiteralBranch> { ["key"] = literal }));
        Assert.Equal(expected: 0, actual: literal.Reads);
    }

    [Fact]
    public void PolymorphicMembersCollectionsAndBoxedValuesRetainReferences() {
        var bound = Bound();
        object[] graphs = [
            new Pair<object, int>(bound, 0),
            new Pair<IDocumentStateValue, int>(bound, 0),
            new Pair<Base, int>(new Derived(bound), 0),
            new Base[] { new Derived(bound) },
            new List<Base> { new Derived(bound) },
            new Pair<IReadOnlyList<Base>, int>(new Base[] { new Derived(bound) }, 0),
            new Boxed(bound),
            new Dictionary<string, DocumentIdentifier> { ["key"] = bound },
            new Dictionary<DocumentIdentifier, string> { [bound] = "value" },
            new DifferentEnumerations(bound),
        ];
        var source = Source();

        foreach (var graph in graphs) {
            Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
            Assert.True(condition: WorldStateDocumentValues.ReferencesRow(definition: source, graph: graph, rowName: "label"));
            Assert.False(condition: WorldStateDocumentValues.ReferencesRow(definition: source, graph: graph, rowName: "other"));
        }
    }

    [Fact]
    public void ExpandingGenericShapesDoNotRequireUnboundedMetadataDiscovery() {
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new Expanding<int>()));
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: new Expanding<object> { Value = Bound() }));
    }

    [Fact]
    public void CachedShapesDoNotCacheMutableContentsAndCyclesTerminate() {
        var first = new Node();
        var second = new Node { Next = first };
        first.Next = second;
        var values = new List<object> { first };

        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: values));
        second.Value = Bound();
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: values));
        second.Value = null;
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: values));
        values.Add(item: Bound());
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: values));
    }

    [Fact]
    public void UnconditionalIgnoreIsExcludedAndConditionalIgnoreStillResolves() {
        var graph = new IgnoredValues();
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: graph));
        graph.Optional = Bound();
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
        Assert.True(condition: WorldStateDocumentValues.TryFlatten(source: Source(), graph: graph, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: "resolved", actual: graph.Optional.Value);
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: graph));
    }

    [Fact]
    public void FlattenResolvesNestedValuesAndPreservesNamedRefusals() {
        var bound = Bound();
        var literal = new DocumentIdentifier(value: "untouched");
        var graph = new Pair<object, DocumentIdentifier>(new Boxed(bound), literal);

        Assert.True(condition: WorldStateDocumentValues.TryFlatten(source: Source(), graph: graph, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: "resolved", actual: bound.Value);
        Assert.Null(@object: bound.Reference);
        Assert.Equal(expected: "untouched", actual: literal.Value);

        var missing = new Pair<int, DocumentIdentifier>(0, Bound(reference: "state.missing"));
        Assert.False(condition: WorldStateDocumentValues.TryFlatten(source: Source(), graph: missing, reason: out reason));
        Assert.Contains(expectedSubstring: "document.right reference 'state.missing'", actualString: reason);
        Assert.NotNull(@object: missing.Right.Reference);
    }

    private sealed record Pair<TLeft, TRight>(TLeft Left, TRight Right);
    private sealed class Expanding<T> {
        public Expanding<Expanding<T>>? Next { get; set; }
        public T? Value { get; set; }
    }
    private readonly record struct Boxed(DocumentIdentifier Value);
    private class Base;
    private sealed class Derived(DocumentIdentifier value) : Base {
        public DocumentIdentifier Value { get; } = value;
    }
    private sealed class LiteralBranch {
        public int Reads;
        public int Value { get { Reads++; return 42; } }
    }
    private sealed class Node {
        public Node? Next { get; set; }
        public object? Value { get; set; }
    }
    private sealed class IgnoredValues {
        [JsonIgnore]
        public DocumentIdentifier Derived => throw new InvalidOperationException(message: "Derived data must not be visited.");
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DocumentIdentifier? Optional { get; set; }
    }
    private sealed class DifferentEnumerations(DocumentIdentifier value) : IEnumerable<int> {
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)new[] { 1 }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => new[] { value }.GetEnumerator();
    }
}
