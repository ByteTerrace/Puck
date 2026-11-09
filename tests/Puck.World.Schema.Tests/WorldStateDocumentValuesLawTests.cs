using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldStateDocumentValuesLawTests {
    private sealed class SpanHolder {
        public ReadOnlySpan<int> Numbers => [1, 2];
        public DocumentIdentifier Identifier { get; init; } = Bound();
    }

    [Fact]
    public void NonBoxablePropertiesDoNotHideTheGraphsBoundValues() =>
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: new SpanHolder()));

    private static DocumentIdentifier Bound(string reference = "state.label") =>
        JsonSerializer.Deserialize<DocumentIdentifier>(json: JsonSerializer.Serialize(value: reference))!;
    private static WorldStateRow TextRow(string name, string text) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Text,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Text(value: text)
            )]
    );
    private static WorldDefinition Source(string spot = "[1,2,3]") => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [
            TextRow(name: "label", text: "resolved"),
            TextRow(name: "spot", text: spot),
        ])
    );
    // A document with one placement whose position reads the spot cell and one whose position is a literal.
    private static WorldDefinition Placed(string spot = "[1,2,3]") => Source(spot: spot) with {
        PlacementsRaw = new WorldPlacementsSection(Rows: [
            new WorldPlacement(Id: "bound", PrototypeId: "p", Position: JsonSerializer.Deserialize<DocumentVector3>(json: "\"state.spot\"", options: DocumentJsonOptions.Shared)!, YawDegrees: 0f, Scale: 1f),
            new WorldPlacement(Id: "literal", PrototypeId: "p", Position: new DocumentVector3(x: 1f, y: 2f, z: 3f), YawDegrees: 0f, Scale: 1f),
        ]),
    };

    [Fact]
    public void BindingCopiesOnlyThePathToEachReferenceAndKeepsWhatItAlreadyBound() {
        var definition = Placed();
        var live = new WorldLiveValues(clock: static () => ArenaTime.Origin) { Arena = WorldStateReader.Seed(definition: definition) };
        var bound = WorldStateDocumentValues.BindLive(definition: definition, live: live);

        Assert.NotSame(expected: definition.Placements[0], actual: bound.Placements[0]);
        Assert.Same(expected: definition.Placements[1], actual: bound.Placements[1]);
        Assert.Same(expected: definition.StateRaw, actual: bound.StateRaw);
        Assert.Same(expected: live, actual: bound.Placements[0].Position.Binder);
        Assert.Null(@object: definition.Placements[0].Position.Binder);
        Assert.Equal(expected: new System.Numerics.Vector3(x: 1f, y: 2f, z: 3f), actual: bound.PlacementFrames[bound.Placements[0].Id].Position);

        var again = WorldStateDocumentValues.BindLive(definition: bound, live: live);

        Assert.NotSame(actual: again, expected: bound);
        Assert.Same(expected: bound.PlacementsRaw, actual: again.PlacementsRaw);
    }
    [Fact]
    public void ARewriteRebuildsEveryShapeOnThePathAndRefusesAClassItCannotCopy() {
        var bound = Bound();
        object[] graphs = [
            new Pair<Boxed[], int>(Left: [new Boxed(Value: bound)], Right: 0),
            new Pair<List<DocumentIdentifier>, int>(Left: [new DocumentIdentifier(value: "kept"), bound], Right: 0),
            new Pair<Dictionary<string, DocumentIdentifier>, int>(Left: new(comparer: StringComparer.Ordinal) { ["key"] = bound }, Right: 0),
            new Pair<object, int>(Left: new Boxed(Value: bound), Right: 0),
        ];

        foreach (var graph in graphs) {
            Assert.True(
                condition: WorldStateDocumentValues.TryFlatten(flattened: out var flattened, graph: graph, reason: out var reason, source: Source()),
                userMessage: reason
            );
            Assert.False(condition: WorldStateDocumentValues.HasReference(graph: flattened));
            Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
        }

        var refused = Assert.Throws<NotSupportedException>(testCode: () => WorldStateDocumentValues.TryFlatten<Pair<Base, int>>(
            flattened: out _,
            graph: new Pair<Base, int>(Left: new Derived(value: bound), Right: 0),
            reason: out _,
            source: Source()
        ));

        Assert.Contains(expectedSubstring: "left", actualString: refused.Message);
    }
    [Fact]
    public void ABoundValueReadsTheArenaItsServerHoldsWhenItIsRead() {
        var first = Placed(spot: "[1,2,3]");
        var live = new WorldLiveValues(clock: static () => ArenaTime.Origin) { Arena = WorldStateReader.Seed(definition: first) };
        var bound = WorldStateDocumentValues.BindLive(definition: first, live: live);

        Assert.Equal(expected: new System.Numerics.Vector3(x: 1f, y: 2f, z: 3f), actual: bound.PlacementFrames[bound.Placements[0].Id].Position);
        live.Arena = WorldStateReader.Seed(definition: Placed(spot: "[4,5,6]"));
        Assert.Equal(expected: new System.Numerics.Vector3(x: 4f, y: 5f, z: 6f), actual: bound.PlacementFrames[bound.Placements[0].Id].Position);
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
    public void ExpandingGenericShapesDoNotRequireUnboundedMetadataDiscovery() {
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new Expanding<int>()));
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: new Expanding<object> { Value = Bound() }));
    }
    [Fact]
    public void FlattenResolvesNestedValuesAndPreservesNamedRefusals() {
        var bound = Bound();
        var literal = new DocumentIdentifier(value: "untouched");
        var graph = new Pair<object, DocumentIdentifier>(
            Left: new Boxed(Value: bound),
            Right: literal
        );

        Assert.True(
            condition: WorldStateDocumentValues.TryFlatten(
                flattened: out var flattened,
                source: Source(),
                graph: graph,
                reason: out var reason
            ),
            userMessage: reason
        );
        var resolved = ((Boxed)flattened.Left).Value;

        Assert.Equal(
            expected: "resolved",
            actual: resolved.Value
        );
        Assert.Null(@object: resolved.Reference);
        Assert.Same(expected: literal, actual: flattened.Right);
        Assert.Equal(
            expected: "state.label",
            actual: bound.Reference
        );

        var missing = new Pair<int, DocumentIdentifier>(
            Left: 0,
            Right: Bound(reference: "state.missing")
        );

        Assert.False(condition: WorldStateDocumentValues.TryFlatten(
            flattened: out var unflattened,
            source: Source(),
            graph: missing,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "document.right reference 'state.missing'"
        );
        Assert.NotNull(@object: missing.Right.Reference);
    }
    [Fact]
    public void LiteralBranchesArePrunedWhileSiblingReferencesRemainVisible() {
        var literal = new LiteralBranch();
        var graph = new Pair<LiteralBranch, DocumentIdentifier>(
            Left: literal,
            Right: new DocumentIdentifier(value: "literal")
        );

        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: graph));
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph with { Right = Bound() }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new[] { literal, literal }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new List<LiteralBranch> { literal }));
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: new Dictionary<string, LiteralBranch> { ["key"] = literal }));
        Assert.Equal(
            actual: literal.Reads,
            expected: 0
        );
    }
    [Fact]
    public void AReferenceThatNamesNoRowIsStillFoundAndRefusedByName() {
        var graph = new List<object> { JsonSerializer.Deserialize<DocumentVector3>(json: "\"elsewhere\"")! };

        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
        Assert.False(condition: WorldStateDocumentValues.ReferencesRow(
            definition: Source(),
            graph: graph,
            rowName: "label"
        ));
        Assert.False(condition: WorldStateDocumentValues.TryResolveGraph(
            graph: graph,
            reason: out var reason,
            resolved: out var unresolved,
            source: Source()
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "reference 'elsewhere' must be state.<row>[.<key>]"
        );
    }
    [Fact]
    public void PolymorphicMembersCollectionsAndBoxedValuesRetainReferences() {
        var bound = Bound();
        object[] graphs = [
            new Pair<object, int>(
                Left: bound,
                Right: 0
            ),
            new Pair<IDocumentStateValue, int>(
                Left: bound,
                Right: 0
            ),
            new Pair<Base, int>(
                Left: new Derived(value: bound),
                Right: 0
            ),
            new Base[] { new Derived(value: bound) },
            new List<Base> { new Derived(value: bound) },
            new Pair<IReadOnlyList<Base>, int>(
                Left: new Base[] { new Derived(value: bound) },
                Right: 0
            ),
            new Boxed(Value: bound),
            new Dictionary<string, DocumentIdentifier> { ["key"] = bound },
            new Dictionary<DocumentIdentifier, string> { [bound] = "value" },
            new DifferentEnumerations(value: bound),
        ];
        var source = Source();

        foreach (var graph in graphs) {
            Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
            Assert.True(condition: WorldStateDocumentValues.ReferencesRow(
                definition: source,
                graph: graph,
                rowName: "label"
            ));
            Assert.False(condition: WorldStateDocumentValues.ReferencesRow(
                definition: source,
                graph: graph,
                rowName: "other"
            ));
        }
    }
    [Fact]
    public void ResolveAgainstASourceFillsTheValueAndKeepsTheReference() {
        var bound = Bound();
        var graph = new Pair<object, DocumentIdentifier>(
            Left: new Boxed(Value: bound),
            Right: new DocumentIdentifier(value: "untouched")
        );

        Assert.True(
            condition: WorldStateDocumentValues.TryResolveGraph(
                resolved: out var resolvedGraph,
                source: Source(),
                graph: graph,
                reason: out var reason
            ),
            userMessage: reason
        );
        var resolved = ((Boxed)resolvedGraph.Left).Value;

        Assert.Equal(
            expected: "resolved",
            actual: resolved.Value
        );
        Assert.Equal(
            expected: "state.label",
            actual: resolved.Reference
        );
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: resolvedGraph));
        Assert.Throws<InvalidOperationException>(testCode: () => bound.Value);

        var missing = new Pair<int, DocumentIdentifier>(
            Left: 0,
            Right: Bound(reference: "state.missing")
        );

        Assert.False(condition: WorldStateDocumentValues.TryResolveGraph(
            resolved: out var unresolvedMissing,
            source: Source(),
            graph: missing,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "document.right reference 'state.missing'"
        );
    }
    [Fact]
    public void UnconditionalIgnoreIsExcludedAndConditionalIgnoreStillResolves() {
        var graph = new IgnoredValues();

        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: graph));
        graph = (graph with { Optional = Bound() });
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
        Assert.True(
            condition: WorldStateDocumentValues.TryFlatten(
                flattened: out var flattened,
                source: Source(),
                graph: graph,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: "resolved",
            actual: flattened.Optional!.Value
        );
        Assert.False(condition: WorldStateDocumentValues.HasReference(graph: flattened));
        Assert.True(condition: WorldStateDocumentValues.HasReference(graph: graph));
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
    private sealed record IgnoredValues {
        [JsonIgnore]
        public DocumentIdentifier Derived => throw new InvalidOperationException(message: "Derived data must not be visited.");
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DocumentIdentifier? Optional { get; init; }
    }
    private sealed class DifferentEnumerations(DocumentIdentifier value) : IEnumerable<int> {
        IEnumerator IEnumerable.GetEnumerator() => new[] { value }.GetEnumerator();

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)new[] { 1 }).GetEnumerator();
    }
}
