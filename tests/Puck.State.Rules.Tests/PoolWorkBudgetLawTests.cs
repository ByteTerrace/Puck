using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: typed-pool work bounds include fixed-slot mutations, journal payload widths,
/// snapshot scans, and every row a cascading release can mutate.</summary>
public sealed class PoolWorkBudgetLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section(int dimensions) => new(
        Spaces: [new StateSpace(Name: Name(value: "pose"), Model: "test", Revision: "1", Dimensions: dimensions)],
        Records: [new StateRecord(Name: Name(value: "piece"), Fields: [
            new StatePoolField(Name: Name(value: "count"), Kind: CellKind.Int, Default: CellValue.Int(value: 0L)),
            new StatePoolField(Name: Name(value: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: string.Empty)),
            new StatePoolField(Name: Name(value: "pose"), Kind: CellKind.Vector, Default: CellValue.Vector(components: new sbyte[dimensions]), Space: Name(value: "pose"), Dimensions: dimensions),
        ])],
        Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 8)]
    );
    private static (CompiledRule Rule, RuleCompileContext Context, StateCatalog Catalog) Compile(StateSection section, params ActionEffect[] effects) {
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);

        return (RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "poolWork"), Effects: effects)), context, catalog);
    }
    private static long Cost(StateSection section, params ActionEffect[] effects) {
        var compiled = Compile(section: section, effects: effects);

        return compiled.Rule.Cost(context: compiled.Context).Units;
    }

    [Fact]
    public void ClaimAndReleaseBoundsGrowWithVectorJournalWidth() {
        static ActionEffect Claim() => new ActionEffect.Claim(
            Pool: "pieces",
            Binding: Name(value: "piece"),
            Effects: [new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), Value: 1m)]
        );
        static ActionEffect Release() => new ActionEffect.ForEachPool(
            Pool: "pieces",
            Binding: Name(value: "piece"),
            Effects: [new ActionEffect.Release(Binding: Name(value: "piece"))]
        );

        Assert.True(condition: (Cost(section: Section(dimensions: 64), Claim()) > Cost(section: Section(dimensions: 8), Claim())));
        Assert.True(condition: (Cost(section: Section(dimensions: 64), Release()) > Cost(section: Section(dimensions: 8), Release())));
    }
    [Fact]
    public void FixedSlotPricesCoverWidePayloadsWithoutChargingForOtherLiveInstances() {
        long? releaseCost = null;
        long? claimWithoutScan = null;

        foreach (var capacity in new[] { 4, 65, 4096 }) {
            var section = Section(dimensions: 1024);

            section = section with {
                Records = [section.Records![0] with { Fields = [section.Records[0].Fields![2]] }],
                Pools = [section.Pools![0] with { Capacity = capacity }],
            };
            var compiled = Compile(section: section,
                new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "piece"), Effects: [new ActionEffect.Release(Binding: Name(value: "piece"))]));
            var claim = new ClaimEffect(pool: compiled.Catalog.Pools[0], bindingSlot: 0, effects: [], describe: "claim storage cost without initializer work");
            var each = Assert.IsType<ForEachPoolEffect>(@object: compiled.Rule.Effects[0]);
            var release = Assert.IsType<ReleaseEffect>(@object: Assert.Single(collection: each.Effects));
            var arena = new StateArena(catalog: compiled.Catalog, section: section, time: ArenaTime.Origin);
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason), userMessage: reason);
            var claimBytes = arena.Journal.Bytes;

            Assert.True(condition: (claim.Cost(context: compiled.Context).Units >= claimBytes));
            Assert.True(condition: arena.TryRelease(handle: handle, reason: out reason), userMessage: reason);
            Assert.True(condition: (release.Cost(context: compiled.Context).Units >= (arena.Journal.Bytes - claimBytes)));
            arena.Rewind(mark: mark);

            releaseCost ??= release.Cost(context: compiled.Context).Units;
            claimWithoutScan ??= (claim.Cost(context: compiled.Context).Units - ((capacity + 63L) / 64L));
            Assert.Equal(expected: releaseCost.Value, actual: release.Cost(context: compiled.Context).Units);
            Assert.Equal(expected: claimWithoutScan.Value, actual: (claim.Cost(context: compiled.Context).Units - ((capacity + 63L) / 64L)));
        }
    }
    [Fact]
    public void QualifiedWritesChargeTheirTypedJournalAndSourceWidths() {
        const int Dimensions = 64;
        var components = new sbyte[Dimensions];

        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, error: out _, vector: out var vector));
        var compiled = Compile(section: Section(dimensions: Dimensions), new ActionEffect.Claim(
            Pool: "pieces",
            Binding: Name(value: "piece"),
            Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), Value: 1m),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "label"), Text: "named"),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "pose"), Vector: vector!.ToBase64Url()),
            ]
        ));
        var claim = Assert.IsType<ClaimEffect>(@object: Assert.Single(collection: compiled.Rule.Effects));
        var scalar = claim.Effects[0].Cost(context: compiled.Context).Units;
        var text = claim.Effects[1].Cost(context: compiled.Context).Units;
        var vectorCost = claim.Effects[2].Cost(context: compiled.Context).Units;

        Assert.True(condition: (text > scalar));
        Assert.True(condition: (vectorCost > scalar));
        Assert.True(condition: (vectorCost >= (ArenaJournal.EntryBytes + Dimensions)));
    }
    [Fact]
    public void StaticAndLexicalTextWritesChargeTheAuthoredPayloadBytes() {
        var section = Section(dimensions: 8);
        var lexical = Compile(section: section, new ActionEffect.Claim(
            Pool: "pieces",
            Binding: Name(value: "piece"),
            Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "label"), Text: "a"),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "label"), Text: "a longer label"),
            ]
        ));
        var claim = Assert.IsType<ClaimEffect>(@object: Assert.Single(collection: lexical.Rule.Effects));

        Assert.True(condition: (claim.Effects[1].Cost(context: lexical.Context).Units > claim.Effects[0].Cost(context: lexical.Context).Units));

        var seeded = section with { Pools = [section.Pools![0] with { Initial = [new StatePoolSeed(Slot: 0)] }] };
        var shortStatic = Compile(section: seeded, new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label"), Text: "a"));
        var longStatic = Compile(section: seeded, new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "label"), Text: "a longer label"));

        Assert.True(condition: (longStatic.Rule.Effects[0].Cost(context: longStatic.Context).Units > shortStatic.Rule.Effects[0].Cost(context: shortStatic.Context).Units));
    }
    [Fact]
    public void ReleaseDeclaresEveryTransitiveCascadeRowAsAWriteHazard() {
        var record = new StateRecord(Name: Name(value: "link"), Fields: [
            new StatePoolField(Name: Name(value: "weight"), Kind: CellKind.Int, Default: CellValue.Int(value: 0L)),
        ]);
        var section = new StateSection(
            Records: [record],
            Pools: [new StatePool(Name: Name(value: "nodes"), Record: record.Name, Capacity: 2)],
            PairPools: [
                new StatePairPool(Name: Name(value: "edges"), Record: record.Name, LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 2),
                new StatePairPool(Name: Name(value: "labels"), Record: record.Name, LeftPool: Name(value: "edges"), RightPool: Name(value: "nodes"), MaxLive: 2),
            ]
        );
        var compiled = Compile(section: section, new ActionEffect.ForEachPool(
            Pool: "nodes",
            Binding: Name(value: "node"),
            Effects: [new ActionEffect.Release(Binding: Name(value: "node"))]
        ));
        var writes = RuleDataflow.Writes(rule: compiled.Rule);

        foreach (var name in new[] { "edges", "labels" }) {
            Assert.True(condition: compiled.Catalog.TryGetPool(name: Name(value: name), pool: out var pool));
            Assert.Contains(collection: writes, filter: access => (access.RowOrdinal == pool!.DomainRowOrdinal));
            Assert.Contains(collection: writes, filter: access => (access.RowOrdinal == pool!.GenerationRowOrdinal));
            Assert.Contains(collection: writes, filter: access => (access.RowOrdinal == pool!.Fields[0].RowOrdinal));
        }
    }
    [Fact]
    public void ReleaseRepeatsEveryCascadeDomainScanForSparseGrandchildren() {
        var record = new StateRecord(Name: Name(value: "link"));
        var directSection = new StateSection(
            Records: [record],
            Pools: [new StatePool(Name: Name(value: "nodes"), Record: record.Name, Capacity: 4)],
            PairPools: [new StatePairPool(Name: Name(value: "edges"), Record: record.Name, LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 8)]
        );
        var nestedSection = directSection with {
            PairPools = [
            .. directSection.PairPools!,
            new StatePairPool(Name: Name(value: "labels"), Record: record.Name, LeftPool: Name(value: "edges"), RightPool: Name(value: "nodes"), MaxLive: 1),
        ],
        };

        static (ReleaseEffect Effect, CompiledRule Rule, RuleCompileContext Context, StatePoolDescriptor Sparse) Release(StateSection section) {
            var compiled = Compile(section: section, new ActionEffect.ForEachPool(
                Pool: "nodes",
                Binding: Name(value: "node"),
                Effects: [new ActionEffect.Release(Binding: Name(value: "node"))]
            ));
            var each = Assert.IsType<ForEachPoolEffect>(@object: Assert.Single(collection: compiled.Rule.Effects));

            Assert.True(condition: (compiled.Catalog.TryGetPool(name: Name(value: "labels"), pool: out var sparse) || compiled.Catalog.TryGetPool(name: Name(value: "edges"), pool: out sparse)));
            return (Assert.IsType<ReleaseEffect>(@object: Assert.Single(collection: each.Effects)), compiled.Rule, compiled.Context, sparse!);
        }

        var direct = Release(section: directSection);
        var nested = Release(section: nestedSection);
        var directCost = direct.Effect.Cost(context: direct.Context).Units;
        var nestedCost = nested.Effect.Cost(context: nested.Context).Units;
        var sparseRelease = (512L + (23L * ArenaJournal.EntryBytes));

        // Direct bound: 9 releases * (8 handles + 1 occupancy word) = 81. Nested bound:
        // 10 releases * (9 handles + 2 occupancy words) = 110. Include the sparse pool's own release.
        Assert.Equal(actual: (nestedCost - directCost), expected: (sparseRelease + 29L));
        Assert.Equal(
            expected: (directSection.Pools![0].Capacity * (sparseRelease + 29L)),
            actual: (nested.Rule.Cost(context: nested.Context).Units - direct.Rule.Cost(context: direct.Context).Units)
        );
    }
}
