using System.Buffers;
using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>nearest</c> transform's cost is one similarity per admitted candidate plus a
/// k-wide ranking, and nothing on the heap. Through <see cref="ArenaEffectHost.TryTransform"/> over a 256-capacity,
/// 256-dimension table, a warm firing allocates nothing, even after a collection; and the ranking reads each admitted
/// candidate's components exactly once whatever k is, so no firing re-scores a candidate or sorts the whole table by
/// scoring it again.</summary>
/// <remarks>Both laws count work rather than time it, so they hold on a loaded machine. The firing's latency is a
/// measurement, not a law: <c>puck bench kernels --filter '*VectorNearest*'</c> takes it on a quiet machine.</remarks>
public sealed class VectorNearestWorkLawTests {
    private const int Capacity = 256;
    private const int Dimensions = 256;

    // Reads through a candidate's memory are how the ranking reaches its components, so each read is one scoring.
    private sealed class CountingMemory(sbyte[] components) : MemoryManager<sbyte> {
        public int Reads { get; private set; }

        public override Span<sbyte> GetSpan() {
            Reads++;

            return components;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    private static StateSection Section() {
        var cells = new StateCell[Capacity];

        for (var i = 0; (i < Capacity); i++) {
            var components = new sbyte[Dimensions];

            components[(i % Dimensions)] = 127;
            cells[i] = new StateCell(
                Key: RulesFixture.Name(value: $"c{i}"),
                Value: CellValue.Vector(components: components)
            );
        }

        return new StateSection(
            Rows: [
                new StateRow(
                    Name: RulesFixture.Name(value: "source"),
                    Kind: CellKind.Vector,
                    Capacity: Capacity,
                    Cells: cells,
                    Space: "lore"
                ),
                new StateRow(
                    Name: RulesFixture.Name(value: "query"),
                    Kind: CellKind.Vector,
                    Space: "lore"
                ),
                new StateRow(
                    Name: RulesFixture.Name(value: "ranked"),
                    Kind: CellKind.Fixed,
                    Capacity: 8
                ),
            ],
            Spaces: [
                new StateSpace(
                    name: RulesFixture.Name(value: "lore"),
                    model: "model",
                    revision: "r1",
                    dimensions: Dimensions
                ),
            ]
        );
    }
    private static Func<bool> NearestFiring() {
        var section = Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var host = new ArenaEffectHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            generators: null
        );
        var queryComponents = new sbyte[Dimensions];

        queryComponents[3] = 127;
        Assert.True(condition: host.Arena.TryWriteVector(
            components: queryComponents,
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            reason: out var writeReason,
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: "query"
            )
        ), userMessage: writeReason);

        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "source",
                    Into: "ranked",
                    K: 5,
                    Query: "query",
                    Threshold: null
                ))],
                name: "nearest-256"
            )
        );

        Assert.True(condition: ArenaTransforms.TryVectorTransform(
            catalog: context.Catalog,
            effect: compiled.Effects[0],
            reader: host,
            reason: out var resolveReason,
            transform: out var transform
        ), userMessage: resolveReason);

        return () => host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out _,
            refusal: out _,
            transform: transform!
        );
    }

    // Each window opens with a collection, so a cache the firing holds only weakly is rebuilt inside every window and
    // cannot pass as a one-off.
    [Fact]
    public void ANearestOver256x256AllocatesNothingPerFiring() {
        var fire = NearestFiring();

        for (var warm = 0; (warm < 64); warm++) {
            Assert.True(condition: fire());
        }

        var allocated = AllocationWindow.Least(window: () => {
            GC.Collect();
            for (var firing = 0; (firing < 256); firing++) {
                Assert.True(condition: fire());
            }
        });

        Assert.True(condition: (allocated == 0L), userMessage: $"256 warm nearest firings over 256x256 allocated {allocated} bytes in every window");
    }
    [InlineData(1, false, false)]
    [InlineData(5, false, true)]
    [InlineData(5, true, true)]
    [InlineData(64, true, false)]
    [InlineData(StateCapacity.MaxNearestResults, false, true)]
    [Theory]
    public void TheRankingScoresEachAdmittedCandidateExactlyOnce(int k, bool farthest, bool isFixedScore) {
        var memories = new CountingMemory[Capacity];
        var candidates = new NearestCandidate[Capacity];
        var excluded = RulesFixture.Name(value: "c7");

        for (var i = 0; (i < Capacity); i++) {
            var components = new sbyte[Dimensions];

            for (var d = 0; (d < Dimensions); d++) {
                components[d] = ((sbyte)((((i * 31) + (d * 17)) % 255) - 127));
            }

            memories[i] = new CountingMemory(components: components);
            candidates[i] = new NearestCandidate(
                Admitted: ((i % 5) != 0),
                Components: memories[i].Memory,
                Key: RulesFixture.Name(value: $"c{i}")
            );
        }

        var query = new sbyte[Dimensions];

        for (var d = 0; (d < Dimensions); d++) {
            query[d] = ((sbyte)(((d * 7) % 255) - 127));
        }

        // Handing out a manager's memory reads it once to learn its length; only reads after that are scorings.
        var handedOut = memories.Select(selector: static memory => memory.Reads).ToArray();
        var matched = VectorTransforms.SelectNearest(
            candidates: candidates,
            excludeKey: excluded,
            farthest: farthest,
            isFixedScore: isFixedScore,
            k: k,
            query: query,
            results: new VectorTransforms.NearestMatch[k],
            threshold: null
        );
        var eligible = candidates.Count(predicate: candidate => (candidate.Admitted && (candidate.Key != excluded)));

        Assert.Equal(
            expected: Math.Min(
                val1: k,
                val2: eligible
            ),
            actual: matched
        );
        for (var i = 0; (i < Capacity); i++) {
            var scorings = (memories[i].Reads - handedOut[i]);

            Assert.True(
                condition: (scorings == ((candidates[i].Admitted && (candidates[i].Key != excluded)) ? 1 : 0)),
                userMessage: $"candidate c{i} (admitted: {candidates[i].Admitted}) was read {scorings} times by a ranking of k = {k}"
            );
        }
    }
}
