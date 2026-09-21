using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Pool iteration retains its generation snapshot storage across speculative candidates.</summary>
public sealed class PoolIterationAllocationLawTests {
    [Fact]
    public void WarmedPoolIterationAndRewindAllocateNothing() {
        static CellName Name(string value) => CellName.Parse(candidate: value);
        var section = new StateSection(
            Records: [new StateRecord(Name: Name(value: "piece"), Fields: [new StatePoolField(Name: Name(value: "score"))])],
            Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "piece"), Capacity: 4,
                Initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 2)])]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null,
            generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(Name: Name(value: "visit"), Effects: [
            new ActionEffect.ForEachPool(Pool: "pieces", Binding: Name(value: "piece"), Effects: [
                new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "piece", field: "score"), Value: 1m)])])]);
        var evaluator = new RuleEvaluator(host: new ArenaEffectHost(arena: arena));
        var latch = new RuleLatch();

        void Candidate() {
            var mark = arena.BeginScope();

            latch.Reset();
            _ = evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL);
            arena.Rewind(mark: mark);
        }
        for (var iteration = 0; (iteration < 128); iteration++) {
            Candidate();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; (iteration < 256); iteration++) {
            Candidate();
        }
        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.True(condition: arena.TryRead(handle: catalog.CreateInstanceHandle(poolOrdinal: 0, slot: 2, generation: 0),
            fieldOrdinal: 0, value: out var value));
        Assert.Equal(expected: 0L, actual: value.AsInt);
    }
}
