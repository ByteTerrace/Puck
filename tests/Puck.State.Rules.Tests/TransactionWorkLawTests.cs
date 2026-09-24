using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a transaction fires each of its steps once — a success commits them where they stand,
/// and a refusal rewinds the steps before it and fires the failure branch once — so it is priced at its door, its
/// steps and its failure branch, each once.</summary>
public sealed class TransactionWorkLawTests {
    private const int Members = 4;

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value) => new(Key: Name(value: key), Value: CellValue.Int(value: value));
    private static StateSection Section() => new(Rows: [
        new StateRow(Name: Name(value: "tokens"), Kind: CellKind.Int, Capacity: Members, Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"t{token}", value: token))]),
        new StateRow(Name: Name(value: "deck"), Kind: CellKind.Int, Capacity: Members, Domain: new StateDomain.KeysOf(Ordered: true, Row: Name(value: "tokens")), Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"t{token}", value: token))]),
        new StateRow(Name: Name(value: "hand"), Kind: CellKind.Int, Capacity: Members, Domain: new StateDomain.KeysOf(Ordered: true, Row: Name(value: "tokens"))),
        new StateRow(Name: Name(value: "log"), Kind: CellKind.Int, Domain: new StateDomain.Ring(Capacity: 4, Empty: -1L)),
        new StateRow(Name: Name(value: "coin"), Kind: CellKind.Int, Draw: new Draw(Source: Name(value: "uniform"), Timing: DrawTiming.Event)),
    ]);
    private static GeneratorRow[] Generators() => [new GeneratorRow(Name: Name(value: "uniform"), Generator: new StateGenerator(Source: GeneratorSource.StreamDraw))];

    // Counts every transform and mutation the evaluator hands the host, by kind.
    private sealed class CountingHost(StateArena arena) : ArenaEffectHost(arena: arena, generators: Generators()) {
        public Dictionary<string, int> Fired { get; } = [];

        public override bool Apply(in Mutation mutation, out EffectRefusal refusal) {
            var kind = mutation.Kind.ToString();

            Fired[kind] = (Fired.GetValueOrDefault(key: kind) + 1);

            return base.Apply(
                mutation: in mutation,
                refusal: out refusal
            );
        }
        public override bool TryTransform(ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
            var kind = transform.GetType().Name;

            Fired[kind] = (Fired.GetValueOrDefault(key: kind) + 1);

            return base.TryTransform(
                binding: in binding,
                moved: out moved,
                refusal: out refusal,
                transform: transform
            );
        }
    }

    private static (TransactionEffect Effect, RuleCompileContext Context, CountingHost Host, RuleOutcome Outcome) Fire(params ActionEffect[] main) {
        var section = Section();
        var context = new RuleCompileContext(section: section, catalog: StateCatalog.Compile(section: section), tables: null, patterns: null, generators: Generators(), simulationRateHz: 30, vocabulary: RuleVocabulary.Core);
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "deal"), Effects: [new ActionEffect.Transaction(
            Effects: main,
            OnFailure: [new ActionEffect.PushState(State: "log", Value: 3m)]
        )]));
        var host = new CountingHost(arena: new StateArena(catalog: context.Catalog, options: null, section: section, time: ArenaTime.Origin));
        var outcome = new RuleEvaluator(host: host).FireEffects(
            applied: out _,
            effects: compiled.Effects,
            ruleName: compiled.Name,
            stepTicks: 1UL,
            tick: 1UL
        );

        return (Assert.IsType<TransactionEffect>(@object: Assert.Single(collection: compiled.Effects)), context, host, outcome);
    }
    private static void PricedOnce(TransactionEffect effect, RuleCompileContext context) {
        var steps = RuleWork.Zero;
        var failure = RuleWork.Zero;

        foreach (var step in effect.Effects) {
            steps += step.Cost(context: context);
        }
        foreach (var step in effect.OnFailure) {
            failure += step.Cost(context: context);
        }

        Assert.Equal(expected: ((1L + steps) + failure), actual: effect.Cost(context: context));
    }

    [Fact]
    public void ACommittedTransactionFiresItsTransformOnceAndIsPricedForOne() {
        var (effect, context, host, outcome) = Fire(new ActionEffect.TransformState(Transform: new StateTransform.Shuffle(Draw: "coin", Row: "deck")));

        Assert.Equal(actual: outcome, expected: RuleOutcome.Fired);
        Assert.Equal(expected: 1, actual: host.Fired["Shuffle"]);
        Assert.False(condition: host.Fired.ContainsKey(key: "Push"));
        PricedOnce(context: context, effect: effect);
    }
    [Fact]
    public void ARefusedTransactionFiresItsPrefixOnceAndItsFailureBranchOnce() {
        var (effect, context, host, outcome) = Fire(
            new ActionEffect.TransformState(Transform: new StateTransform.Shuffle(Draw: "coin", Row: "deck")),
            new ActionEffect.TransformState(Transform: new StateTransform.Transfer(From: "hand", To: "deck", Selector: ZoneSelector.First, Count: 1))
        );

        Assert.Equal(actual: outcome, expected: RuleOutcome.Fired);
        Assert.Equal(expected: 1, actual: host.Fired["Shuffle"]);
        Assert.Equal(expected: 1, actual: host.Fired["Transfer"]);
        Assert.Equal(expected: 1, actual: host.Fired["Push"]);
        // The rewound shuffle left its draw site where it was.
        Assert.Equal(expected: 0L, actual: host.Arena.DrawCursor(rowOrdinal: 4));
        PricedOnce(context: context, effect: effect);
    }
}
