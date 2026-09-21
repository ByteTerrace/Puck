using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: pool effects keep lexical handles in distinct registers, use generated typed
/// field rows in dataflow and pricing, and participate in the enclosing firing's atomic journal scope.</summary>
public sealed class PoolRuleLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section() => new(
        Spaces: [new StateSpace(Name: Name(value: "pose"), Model: "test", Revision: "1", Dimensions: 16)],
        Rows: [new StateRow(Name: Name(value: "locked"), Kind: CellKind.Int, Min: 0L, Max: 0L, Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 0L))])],
        Records: [new StateRecord(Name: Name(value: "piece"), Fields: [
            new StatePoolField(Name: Name(value: "count"), Kind: CellKind.Int, Default: CellValue.Int(value: 7L)),
            new StatePoolField(Name: Name(value: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: "fresh")),
            new StatePoolField(Name: Name(value: "pose"), Kind: CellKind.Vector, Default: CellValue.Vector(components: new sbyte[16]), Space: Name(value: "pose"), Dimensions: 16),
        ])],
        Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4)]
    );
    private static (StateArena Arena, RuleCompileContext Context, RuleEvaluator Evaluator, RuleLatch Latch) Arrange() {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);

        return (arena, context, new RuleEvaluator(host: new ArenaEffectHost(arena: arena)), new RuleLatch());
    }

    [Fact]
    public void NestedPoolScopesUseDistinctRegistersAndTypedVectorCopiesContributeFieldRows() {
        var (_, context, _, _) = Arrange();
        var components = new sbyte[16];

        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, error: out _, vector: out var vector));
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "nested"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "outer"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "outer", field: "pose"), Vector: vector!.ToBase64Url()),
                new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "inner"), Effects: [
                    new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "inner", field: "pose"), FromState: StateChannelRef.OfBindingField(binding: "outer", field: "pose")),
                ]),
            ]),
        ]));

        var outer = Assert.IsType<ClaimEffect>(@object: compiled.Effects[0]);
        var inner = Assert.IsType<ClaimEffect>(@object: outer.Effects[1]);
        var copy = Assert.IsType<InstanceFieldVectorWriteEffect>(@object: inner.Effects[0]);

        Assert.NotEqual(expected: outer.BindingSlot, actual: inner.BindingSlot);
        Assert.NotEqual(expected: copy.Pool.DomainRowOrdinal, actual: copy.Field.RowOrdinal);
        Assert.Contains(collection: RuleDataflow.Writes(rule: compiled), filter: access => (access.RowOrdinal == copy.Field.RowOrdinal));
        Assert.True(condition: (compiled.Cost(context: context).Units > copy.Field.Declaration.Dimensions));
    }
    [Fact]
    public void ARefusalAfterAClaimRewindsTheWholeFiringIncludingDefaults() {
        var (arena, context, evaluator, latch) = Arrange();
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "rollback"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), Value: 9m),
                new ActionEffect.SetState(State: "locked", Value: 1m),
            ]),
        ]));

        Assert.False(condition: evaluator.Evaluate(latch: latch, rules: [compiled], stepTicks: 1UL));
        Assert.True(condition: arena.Catalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));
        Assert.Empty(collection: arena.SnapshotPool(poolOrdinal: pool!.Ordinal));
        Assert.Single(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void NestedClaimInitializerRefusalRewindsEveryFieldAndGeneration() {
        var (arena, context, evaluator, latch) = Arrange();
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "nestedRollback"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "outer"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "outer", field: "count"), Value: 19m),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "outer", field: "label"), Text: "outer"),
                new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "inner"), Effects: [
                    new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "inner", field: "count"), Value: 23m),
                    new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "inner", field: "label"), Text: "inner"),
                    new ActionEffect.SetState(State: "locked", Value: 1m),
                ]),
            ]),
        ]));

        Assert.False(condition: evaluator.Evaluate(latch: latch, rules: [compiled], stepTicks: 1UL));
        var pool = Assert.Single(collection: arena.ToPools());

        Assert.Empty(collection: (pool.Snapshot!.Live ?? []));
        Assert.All(collection: pool.Snapshot.Generations, action: generation => Assert.Equal(actual: generation, expected: 0L));
        var descriptor = Assert.Single(collection: arena.Catalog.Pools);

        Assert.All(collection: descriptor.Fields, action: field => Assert.Empty(collection: (arena.ToRow(rowOrdinal: field.RowOrdinal).Cells ?? [])));
    }
    [Fact]
    public void ClaimInstallsTextAndNumericDefaultsThroughTypedFieldStorage() {
        var (arena, context, evaluator, latch) = Arrange();
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "defaults"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), FromState: StateChannelRef.OfBindingField(binding: "piece", field: "count")),
            ]),
        ]));

        Assert.True(condition: evaluator.Evaluate(latch: latch, rules: [compiled], stepTicks: 1UL));
        Assert.True(condition: arena.Catalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));
        var handle = Assert.Single(collection: arena.SnapshotPool(poolOrdinal: pool!.Ordinal));

        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var count));
        Assert.True(condition: arena.TryRead(fieldOrdinal: 1, handle: handle, value: out var label));
        Assert.Equal(expected: 7L, actual: count.AsInt);
        Assert.Equal(expected: "fresh", actual: label.AsText);
    }
    [InlineData(0UL, 60L, 120L)]
    [InlineData(7UL, 67L, 127L)]
    [Theory]
    public void ScheduleStateWritesDueTicksThroughLexicalAndStaticPoolFields(ulong tick, long expectedLexical, long expectedStatic) {
        var section = Section() with { Pools = [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4, Initial: [new StatePoolSeed(Slot: 2)])] };
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "scheduleFields"), Effects: [
            new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.ScheduleState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), DelaySeconds: 1m),
            ]),
            new ActionEffect.ScheduleState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 2, field: "count"), DelaySeconds: 2m),
        ]));
        var host = new ArenaEffectHost(arena: arena);
        var evaluator = new RuleEvaluator(host: host);

        host.Advance(engineTick: tick, tick: tick);

        Assert.True(condition: evaluator.Evaluate(latch: new RuleLatch(), rules: [compiled], stepTicks: 1UL));
        Assert.True(condition: catalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));
        var handles = arena.SnapshotPool(poolOrdinal: pool!.Ordinal);
        var claimed = Assert.Single(collection: handles, predicate: static handle => (handle.Slot == 0));
        var seeded = Assert.Single(collection: handles, predicate: static handle => (handle.Slot == 2));

        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: claimed, value: out var lexicalDue));
        Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: seeded, value: out var staticDue));
        Assert.Equal(expected: expectedLexical, actual: lexicalDue.AsInt);
        Assert.Equal(expected: expectedStatic, actual: staticDue.AsInt);
    }
    [Fact]
    public void SeededGenerationZeroFieldsCanBeReadAndWrittenByLogicalPoolAndSlot() {
        var section = Section() with { Pools = [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4, Initial: [new StatePoolSeed(Slot: 2)])] };
        var catalog = StateCatalog.Compile(section: section);
        // A document-value install may reconstruct an equivalent arena without recompiling unchanged rules.
        // Direct logical handles must therefore bind to the evaluator's current catalog at read/write time.
        var runtimeCatalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: runtimeCatalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var authored = new Rule(
            Name: Name(value: "staticField"),
            Gate: new ActionPredicate.CompareState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 2, field: "count"), Comparison: ActionStateComparison.Equal, Value: 7m),
            Effects: [new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 2, field: "count"), Value: 11m)]
        );
        var compiled = RuleCompiler.Compile(context: context, rule: authored);
        var evaluator = new RuleEvaluator(host: new ArenaEffectHost(arena: arena));

        Assert.True(condition: evaluator.Evaluate(rules: [compiled], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.True(condition: runtimeCatalog.TryGetPool(name: Name(value: "pieces"), pool: out var pool));
        var seeded = runtimeCatalog.CreateInstanceHandle(poolOrdinal: pool!.Ordinal, slot: 2, generation: 0L);

        Assert.True(condition: arena.TryRead(handle: seeded, fieldOrdinal: 0, value: out var value));
        Assert.Equal(expected: 11L, actual: value.AsInt);

        Assert.True(condition: arena.TryRelease(handle: seeded, reason: out _));
        var restored = section with { Pools = arena.ToPools() };
        var restoredCatalog = StateCatalog.Compile(section: restored);
        var restoredContext = new RuleCompileContext(section: restored, catalog: restoredCatalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);

        _ = RuleCompiler.Compile(context: restoredContext, rule: authored);
    }
    [Fact]
    public void TypedPoolReferencesDiagnoseUnknownFieldsSlotsAndReleasedBindings() {
        var (_, context, _, _) = Arrange();

        var unknownField = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(
            Name: Name(value: "unknownField"),
            Effects: [new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 0, field: "missing"), Value: 1m)]
        )));
        var outsideCapacity = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(
            Name: Name(value: "outsideCapacity"),
            Effects: [new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField(pool: "pieces", slot: 4, field: "count"), Value: 1m)]
        )));
        var releasedBinding = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(context: context, rule: new Rule(
            Name: Name(value: "releasedBinding"),
            Effects: [new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.Release(Binding: Name(value: "piece")),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "piece", field: "count"), Value: 1m),
            ])]
        )));

        Assert.Contains(expectedSubstring: "has no field 'missing'", actualString: unknownField.Message);
        Assert.Contains(expectedSubstring: "slot 4 lies outside capacity 4", actualString: outsideCapacity.Message);
        Assert.Contains(expectedSubstring: "not live at this use", actualString: releasedBinding.Message);
    }
    [Fact]
    public void ReleaseAndReclaimDuringIterationDoesNotVisitTheReplacement() {
        var section = Section() with { Pools = [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4, Initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 1)])] };
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var evaluator = new RuleEvaluator(host: new ArenaEffectHost(arena: arena));
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "replaceEach"), Effects: [
            new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "old"), Effects: [
                new ActionEffect.Release(Binding: Name(value: "old")),
                new ActionEffect.Claim(Pool: "pieces", Binding: Name(value: "fresh"), Effects: [
                    new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "fresh", field: "count"), Value: 10m),
                ]),
            ]),
        ]));

        Assert.True(condition: evaluator.Evaluate(rules: [compiled], latch: new RuleLatch(), stepTicks: 1UL));
        var replacements = arena.SnapshotPool(poolOrdinal: 0);

        Assert.Equal(expected: 2, actual: replacements.Count);
        Assert.All(collection: replacements, action: handle => {
            Assert.Equal(expected: 1L, actual: handle.Generation);
            Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var count));
            Assert.Equal(expected: 10L, actual: count.AsInt);
        });
    }
    [Fact]
    public void APoolForEachRewritesEveryOriginallySelectedNounInOneFiring() {
        var section = Section() with {
            Pools = [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4, Initial: [
            new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: Name(value: "count"), Value: CellValue.Int(value: 1L))]),
            new StatePoolSeed(Slot: 1, Values: [new StatePoolValue(Field: Name(value: "count"), Value: CellValue.Int(value: 1L))]),
        ])],
        };
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var field = StateChannelRef.OfBindingField(binding: "noun", field: "count");
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "rewrite"), Effects: [new ActionEffect.ForEachPool(
            Pool: "pieces",
            Binding: Name(value: "noun"),
            Effects: [new ActionEffect.If(
                Condition: new ActionPredicate.CompareState(State: field, Comparison: ActionStateComparison.Equal, Value: 1m),
                Then: [new ActionEffect.SetState(State: field, Value: 2m)]
            )]
        )]));
        var evaluator = new RuleEvaluator(host: new ArenaEffectHost(arena: arena));

        Assert.True(condition: evaluator.Evaluate(rules: [compiled], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.All(collection: arena.SnapshotPool(poolOrdinal: 0), action: handle => {
            Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value));
            Assert.Equal(expected: 2L, actual: value.AsInt);
        });
    }
    [Fact]
    public void ARefusalAfterAPoolRewriteRestoresEveryOriginalNoun() {
        var section = Section() with {
            Pools = [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4, Initial: [
            new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: Name(value: "count"), Value: CellValue.Int(value: 1L))]),
            new StatePoolSeed(Slot: 1, Values: [new StatePoolValue(Field: Name(value: "count"), Value: CellValue.Int(value: 1L))]),
        ])],
        };
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var field = StateChannelRef.OfBindingField(binding: "noun", field: "count");
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "rewriteRollback"), Effects: [new ActionEffect.ForEachPool(
            Pool: "pieces",
            Binding: Name(value: "noun"),
            Effects: [
                new ActionEffect.SetState(State: field, Value: 2m),
                new ActionEffect.SetState(State: "locked", Value: 1m),
            ]
        )]));
        var evaluator = new RuleEvaluator(host: new ArenaEffectHost(arena: arena));
        var before = arena.ComputeHash();

        Assert.False(condition: evaluator.Evaluate(rules: [compiled], latch: new RuleLatch(), stepTicks: 1UL));
        Assert.Equal(expected: before, actual: arena.ComputeHash());
        Assert.All(collection: arena.SnapshotPool(poolOrdinal: 0), action: handle => {
            Assert.True(condition: arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value));
            Assert.Equal(expected: 1L, actual: value.AsInt);
        });
    }
    [Fact]
    public void ReleaseCostIncludesTransitivePairCascades() {
        var record = new StateRecord(Name: Name(value: "link"));
        var direct = new StateSection(
            Records: [record],
            Pools: [new StatePool(Name: Name(value: "nodes"), Record: record.Name, Capacity: 2)],
            PairPools: [new StatePairPool(Name: Name(value: "edges"), Record: record.Name, LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 2)]
        );
        var nested = direct with { PairPools = [.. direct.PairPools!, new StatePairPool(Name: Name(value: "labels"), Record: record.Name, LeftPool: Name(value: "edges"), RightPool: Name(value: "nodes"), MaxLive: 2)] };
        var authored = new Rule(Name: Name(value: "release"), Effects: [new ActionEffect.ForEachPool(
            Pool: "nodes",
            Binding: Name(value: "node"),
            Effects: [new ActionEffect.Release(Binding: Name(value: "node"))]
        )]);

        static long Cost(StateSection section, Rule rule) {
            var catalog = StateCatalog.Compile(section: section);
            var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);

            return RuleCompiler.Compile(context: context, rule: rule).Cost(context: context).Units;
        }

        Assert.True(condition: (Cost(rule: authored, section: nested) > Cost(rule: authored, section: direct)));
    }
}
