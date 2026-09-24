using Puck.Abstractions.Counting;
using Puck.World.Machines;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>world.boot</c> work source declares its kinds and counts a boot's work into the ledger
/// a flow attributes, and a boot's work is pinned: the Parlot lineup source (a small <c>.puck</c> world over one
/// basis) and the shipped island from its source tree (a JSON root that imports eight <c>.puck</c> games and proves
/// four neighbours) each load, parse, validate and compile within the counts below, parse each neighbour once however
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
    private static Dictionary<string, long> BootLineup(WorldMachineCatalog catalog) {
        var work = new WorldBootWork();
        var path = RepositoryPaths.Resolve(relativePath: "worlds/parlor/lineup.puck");

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(
                condition: WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out var failure, path: path),
                userMessage: failure?.Diagnostics.FormatReport(filePath: path)
            );
            Assert.True(
                condition: WorldSourceLoader.TryLoadForAdmission(
                    admission: out _,
                    catalog: catalog,
                    catalogFingerprint: catalog.CompositionFingerprint,
                    document: compiled!.Document!,
                    path: path,
                    reason: out var reason
                ),
                userMessage: reason
            );
        }

        return Counts(work: work);
    }
    private static Dictionary<string, long> BootIsland(WorldMachineCatalog catalog) {
        var work = new WorldBootWork();
        var path = RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/puck.world.json");

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(
                condition: WorldDefinitionLoader.TryResolve(
                    catalog: catalog,
                    catalogFingerprint: catalog.CompositionFingerprint,
                    explicitPath: path,
                    failure: out var failure,
                    source: out var source
                ),
                userMessage: failure
            );
            // The boot completes its admission once its command vocabulary and neighbour transports compose, proving
            // the same four borders again through a resolver of its own.
            Assert.True(
                condition: WorldDefinitionValidator.TryCompleteAdmission(
                    admission: source.Admission!,
                    machines: catalog,
                    neighbours: new WorldFileNeighbourResolver(
                        baseDirectory: () => Path.GetDirectoryName(path: path)!,
                        catalog: catalog,
                        catalogFingerprint: catalog.CompositionFingerprint
                    ),
                    reason: out var reason
                ),
                userMessage: reason
            );
        }

        return Counts(work: work);
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
    [Fact]
    public void TheLineupBootsWithinItsCountsAndItsSecondBootCompilesNothing() {
        var catalog = TestHookInstaller.CreateMachineCatalog();

        WorldDefinitionFileSource.ForgetComposedDocuments();

        // A compile of the lineup reads the enums its basis declares, one more ask of the cache than a held lineup
        // makes, so the lineup is held before the boot and every boot asks the same two: the lineup and, once, its
        // basis.
        Assert.True(condition: WorldCompileCache.Shared.TryCompile(
            compiled: out _,
            failure: out var failure,
            path: RepositoryPaths.Resolve(relativePath: "worlds/parlor/lineup.puck")
        ), userMessage: failure?.Diagnostics.FormatReport(filePath: "lineup.puck"));

        // The lineup and its basis are the two sources the boot reads; one load reads the basis, merges both, and
        // parses, validates and compiles the rules of the one document it admits.
        AssertPinned(
            asks: 2L,
            compileCeiling: 2L,
            counts: BootLineup(catalog: catalog),
            exact: [
                ("world.boot.loads", 1L),
                ("world.boot.documents-read", 1L),
                ("world.boot.compositions", 2L),
                ("world.boot.compositions-shared", 0L),
                ("world.boot.parses", 1L),
                ("world.boot.validations", 1L),
                ("world.boot.rule-compilations", 1L),
                ("world.boot.neighbour-resolves", 0L),
            ]
        );
        // The second boot compiles nothing and answers the whole graph from the image the first one held.
        AssertPinned(
            asks: 2L,
            compileCeiling: 0L,
            counts: BootLineup(catalog: catalog),
            exact: [
                ("world.boot.loads", 1L),
                ("world.boot.documents-read", 0L),
                ("world.boot.compositions", 0L),
                ("world.boot.compositions-shared", 1L),
                ("world.boot.parses", 1L),
                ("world.boot.validations", 1L),
                ("world.boot.rule-compilations", 1L),
            ]
        );
    }
    [Fact]
    public void TheIslandBootsWithinItsCountsParsesEachNeighbourOnceAndItsSecondBootCompilesNothing() {
        var catalog = TestHookInstaller.CreateMachineCatalog();

        WorldDefinitionFileSource.ForgetComposedDocuments();

        // Twenty-four documents — the island, its basis, its games and modules, and the four shards its borders
        // name — are read and merged once each; eleven of them are .puck sources. The island is parsed once and each
        // shard once, though admission and its completion each prove all four borders.
        var first = BootIsland(catalog: catalog);

        AssertPinned(
            asks: 99L,
            compileCeiling: 11L,
            counts: first,
            exact: [
                ("world.boot.loads", 1L),
                ("world.boot.documents-read", 24L),
                ("world.boot.compositions", 24L),
                ("world.boot.compositions-shared", 4L),
                ("world.boot.parses", 5L),
                ("world.boot.validations", 1L),
                ("world.boot.rule-compilations", 1L),
                ("world.boot.neighbour-resolves", 8L),
            ]
        );
        Assert.True(condition: (first["world.boot.curve-compiles"] <= 2L));
        // The second boot compiles nothing, merges nothing, and parses only the island itself.
        AssertPinned(
            asks: 99L,
            compileCeiling: 0L,
            counts: BootIsland(catalog: catalog),
            exact: [
                ("world.boot.loads", 1L),
                ("world.boot.documents-read", 1L),
                ("world.boot.compositions", 0L),
                ("world.boot.compositions-shared", 1L),
                ("world.boot.parses", 1L),
                ("world.boot.validations", 1L),
                ("world.boot.rule-compilations", 1L),
                ("world.boot.neighbour-resolves", 8L),
                ("world.boot.curve-compiles", 0L),
            ]
        );
    }
}
