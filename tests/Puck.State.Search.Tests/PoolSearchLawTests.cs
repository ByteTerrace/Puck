using Puck.Maths;
using Xunit;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: rule-backed search dataflow includes generated pool storage, and generation rows
/// distinguish a reclaimed slot from its earlier lifetime even when its record fields return to the same defaults.</summary>
public sealed class PoolSearchLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static Position Position() => new(
        rows: SearchFixture.Board(cells: 4, tokens: 1),
        records: [new StateRecord(Name: Name(value: "pieceRecord"), Fields: [
            new StatePoolField(Name: Name(value: "score"), Default: CellValue.Int(value: 0L)),
        ])],
        pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "pieceRecord"), Capacity: 2)]
    );
    private static SearchPlan Plan() => new(
        Name: "moves",
        Tokens: "piece",
        Topology: null,
        Zones: [],
        CellCount: 4,
        Turn: "turn",
        Verdict: "verdict",
        Off: -1L,
        Nodes: 1,
        Work: SearchWork.NodeBounded(judge: 1L),
        Depth: 1,
        Best: "best",
        Shapes: [new SearchShapePlan(
            Kind: SearchShapeKind.Relocate,
            Displace: false,
            Directions: [],
            CompanionIndex: -1
        )],
        Counts: "counts",
        Legal: "legal"
    );

    [Fact]
    public void RuleJudgeDataflowIncludesPoolGenerationAndFieldWrites() {
        var position = Position();
        var rule = new Rule(Name: Name(value: "spawn"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "score"), Value: 3m),
            ]),
        ]);
        var judge = SearchFixture.RuleJudge(position: position, rules: [rule]);

        Assert.True(condition: position.Catalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));

        Assert.Contains(expected: pool!.DomainRowOrdinal, collection: judge.KeyRows);
        Assert.Contains(expected: pool.GenerationRowOrdinal, collection: judge.KeyRows);
        Assert.Contains(expected: pool.Fields[0].RowOrdinal, collection: judge.KeyRows);
    }
    [Fact]
    public void ReclaimChangesTheGenerationRowHashWhenFieldBytesReturnToTheirDefaults() {
        var position = Position();

        Assert.True(condition: position.Catalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));
        Assert.True(condition: position.Arena.TryClaim(poolOrdinal: pool!.Ordinal, handle: out var first, reason: out _));
        var before = Fnv1aHash.Create();

        position.Arena.AddRowTo(hash: ref before, rowOrdinal: pool.GenerationRowOrdinal);
        Assert.True(condition: position.Arena.TryRelease(handle: first, reason: out _));
        Assert.True(condition: position.Arena.TryClaim(poolOrdinal: pool.Ordinal, handle: out var second, reason: out _));
        var after = Fnv1aHash.Create();

        position.Arena.AddRowTo(hash: ref after, rowOrdinal: pool.GenerationRowOrdinal);

        Assert.Equal(expected: first.Slot, actual: second.Slot);
        Assert.NotEqual(expected: first.Generation, actual: second.Generation);
        Assert.NotEqual(expected: before.Value, actual: after.Value);
        Assert.True(condition: position.Arena.TryRead(fieldOrdinal: 0, handle: second, value: out var value));
        Assert.Equal(expected: 0L, actual: value.AsInt);
    }
    [Fact]
    public void SearchPlansCannotNameGeneratedPoolStorageRows() {
        var position = Position();
        var generated = position.Catalog.Descriptors.First(predicate: static descriptor => descriptor.Generated).Name;
        var plans = new[] {
            Plan() with { Verdict = generated },
            Plan() with { Zones = [generated] },
            Plan() with { Shapes = [Plan().Shapes[0] with { Codes = generated }] },
        };

        foreach (var plan in plans) {
            Assert.False(condition: ArenaSearchPlan.TryResolve(
                catalog: position.Catalog,
                plan: plan,
                reason: out var reason,
                resolved: out _
            ));
            Assert.Contains(actualString: reason, expectedSubstring: generated);
            Assert.Contains(actualString: reason, expectedSubstring: "generated row");
        }
    }
}
