using Puck.Abstractions.Counting;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>world.boot</c> work source declares its kinds and counts a boot's work into the ledger
/// a flow attributes, and a boot's work is pinned: the Parlot lineup source (a small <c>.puck</c> world over one
/// basis) and the shipped island from its source tree (a JSON root that imports its <c>.puck</c> games and
/// modules and proves four neighbours) each load, parse, validate and compile within the counts below, parse each neighbour once however
/// many times its border is proved, and compile nothing on a second boot. The laws load exactly what the game's boot
/// loads — the compile cache, then <see cref="WorldSourceLoader"/> for a source, and
/// <see cref="WorldDefinitionLoader.TryResolve"/> for a document — and share the serialized composition collection
/// because held composed images belong to the process.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class WorldBootWorkLawTests {
    private static readonly string[] KindNames = [
        "world.boot.loads",
        "world.boot.documents-read",
        "world.boot.compositions",
        "world.boot.compositions-shared",
        "world.boot.compile-documents-read",
        "world.boot.compile-compositions",
        "world.boot.compile-compositions-shared",
        "world.boot.compiles",
        "world.boot.puck-parses",
        "world.boot.puck-cache-hits",
        "world.boot.parses",
        "world.boot.validations",
        "world.boot.rule-compilations",
        "world.boot.neighbour-resolves",
        "world.boot.curve-compiles",
        "world.boot.shape-builds",
        "world.boot.asset-loads",
        "world.boot.compiled-hits",
        "world.boot.chunk-derivations",
    ];

    private static Dictionary<string, long> Counts(WorldBootWork work) {
        IWorkCounterSource source = work;
        var counts = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var kind in source.WorkKinds) {
            Assert.True(condition: source.TryRead(kind: kind, value: out var value), userMessage: kind.Name);
            counts[kind.Name] = value;
        }

        return counts;
    }
    // The compile cache is the process's, so whether a source compiles in this boot or was compiled by an earlier
    // law is not this law's to know: every ask is one or the other, so their sum is exact and the compiles are a
    // ceiling. Every other count is exact, since the collection runs alone after the held images are dropped.
    private static void AssertPinned(Dictionary<string, long> counts, long asks, long compileCeiling, params (string Kind, long Count)[] exact) {
        var described = string.Join(separator: ", ", values: counts.Select(selector: static pair => $"{pair.Key}={pair.Value}"));

        Assert.True(condition: (counts["world.boot.compiles"] <= compileCeiling), userMessage: described);
        Assert.True(condition: (counts["world.boot.puck-parses"] == counts["world.boot.compiles"]), userMessage: described);
        Assert.True(condition: ((counts["world.boot.compiles"] + counts["world.boot.puck-cache-hits"]) == asks), userMessage: described);
        foreach (var (kind, count) in exact) {
            Assert.True(condition: (counts[kind] == count), userMessage: $"{kind} expected {count}: {described}");
        }
    }

    [Fact]
    public void TheSourceIsNamedAndDeclaresItsKindsInReportOrder() {
        IWorkCounterSource source = new WorldBootWork();

        Assert.Equal(expected: "world.boot", actual: WorldBootWork.Process.Name);
        Assert.Equal(expected: KindNames, actual: source.WorkKinds.ToArray().Select(selector: static kind => kind.Name));
        Assert.All(collection: source.WorkKinds.ToArray(), action: static kind => Assert.Equal(expected: "count", actual: kind.Unit));
        Assert.All(collection: Counts(work: ((WorldBootWork)source)).Values, action: static value => Assert.Equal(actual: value, expected: 0L));
        Assert.False(condition: source.TryRead(kind: new WorkKind(name: "world.boot.other", unit: "count", workClass: WorkClass.Deterministic), value: out var other));
        Assert.Equal(actual: other, expected: 0L);
    }
    [Fact]
    public void WorkCountsIntoTheAttributedLedgerAndNowhereElse() {
        var outer = new WorldBootWork();
        var inner = new WorldBootWork();
        var process = WorldBootWork.Process.Read(kind: WorldBootWork.Loads);

        using (WorldBootWork.Attribute(work: outer)) {
            WorldBootWork.Count(kind: WorldBootWork.Loads);

            using (WorldBootWork.Attribute(work: inner)) {
                WorldBootWork.Count(kind: WorldBootWork.Loads);
                WorldBootWork.Count(kind: WorldBootWork.Loads);
            }

            WorldBootWork.Count(kind: WorldBootWork.Loads);
        }

        Assert.Equal(expected: 2L, actual: outer.Read(kind: WorldBootWork.Loads));
        Assert.Equal(expected: 2L, actual: inner.Read(kind: WorldBootWork.Loads));
        Assert.Equal(expected: process, actual: WorldBootWork.Process.Read(kind: WorldBootWork.Loads));
        Assert.Throws<ArgumentException>(testCode: () => outer.Add(amount: 1L, kind: new WorkKind(name: "world.boot.other", unit: "count", workClass: WorkClass.Deterministic)));
    }
}
