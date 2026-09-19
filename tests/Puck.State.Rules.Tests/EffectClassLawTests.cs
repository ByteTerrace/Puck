using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: an arm that leaves the arena is one of two kinds, and a firing's all-or-nothing
/// promise covers one of them. A <see cref="EffectNeeds.Transactional"/> arm is preflighted with the rest, never fired
/// on its own, prepared by the host as one unit before the commit, where a refusal rewinds the firing, and installed
/// between the commit and the first delivery. Every other <see cref="EffectNeeds.Irreversible"/> arm is delivered
/// after the commit in authored order, and a delivery that refuses is counted, undoes nothing, and stops nothing
/// that follows it.</summary>
public sealed class EffectClassLawTests {
    private sealed record Seal : ActionEffect;
    private sealed record Post(string Name, bool Refuses = false) : ActionEffect;

    private sealed class Ledger {
        public List<string> Log { get; } = [];
        public bool RefuseUnit { get; set; }
    }
    private sealed class LedgerHost(StateArena arena, Ledger ledger) : ArenaEffectHost(arena: arena) {
        public override void Committed(int scope) => ledger.Log.Add(item: "committed");
        public override void CommitTransactional(in EffectFiring firing) => ledger.Log.Add(item: "unit");
        public override bool PrepareTransactional(in EffectFiring firing, out EffectRefusal refusal) {
            ledger.Log.Add(item: "prepare");
            refusal = (ledger.RefuseUnit
                ? EffectRefusal.Of(
                    code: RuleEffectRefusal.MutationRejected,
                    reason: "the unit cannot be installed"
                )
                : EffectRefusal.None
            );

            return !ledger.RefuseUnit;
        }
        public override void Preflighting() => ledger.Log.Add(item: "preflighting");
    }
    private sealed class Arm<TEffect>(Ledger ledger, string discriminator, EffectNeeds needs) : EffectFamily where TEffect : ActionEffect {
        public override string Discriminator => discriminator;
        public override Type EffectType => typeof(TEffect);

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => new Compiled(
            ledger: ledger,
            name: ((effect as Post)?.Name ?? "seal"),
            needs: needs,
            refuses: ((effect as Post)?.Refuses ?? false)
        );
    }
    private sealed class Compiled(Ledger ledger, string name, EffectNeeds needs, bool refuses) : RuleEffect(describe: name) {
        public override EffectNeeds Needs => needs;
        public override bool SubmitsMutation => false;

        public override RuleWork Cost(IRuleCostContext context) => 1L;
        public override bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
            ledger.Log.Add(item: $"{(firing.Preflight ? "preflight" : "fire")} {Describe}");

            var refused = (refuses && !firing.Preflight);

            refusal = (refused
                ? EffectRefusal.Of(
                    code: RuleEffectRefusal.IrreversibleArmFailed,
                    reason: $"{Describe} could not be delivered"
                )
                : EffectRefusal.None
            );

            return !refused;
        }
    }

    private static (Ledger Ledger, LedgerHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules) Arrange(params ActionEffect[] effects) {
        var ledger = new Ledger();
        var section = EvaluatorFixture.Section();
        var context = EvaluatorFixture.Context(
            section: section,
            vocabulary: new RuleVocabulary(
                effects: [
                    new Arm<Seal>(
                        discriminator: "seal",
                        ledger: ledger,
                        needs: (EffectNeeds.Irreversible | EffectNeeds.Transactional)
                    ),
                    new Arm<Post>(
                        discriminator: "post",
                        ledger: ledger,
                        needs: EffectNeeds.Irreversible
                    ),
                ],
                keys: [],
                operands: [],
                predicates: []
            )
        );
        var host = new LedgerHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            ledger: ledger
        );

        return (ledger, host, new RuleEvaluator(host: host), RuleCompiler.CompileAll(
            context: context,
            rules: [new Rule(
                Name: RulesFixture.Name(value: "mixed"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "score",
                        value: 3m
                    ),
                    .. effects,
                ]
            )]
        ));
    }

    [Fact]
    public void ATransactionalArmIsInstalledWithTheCommitAndNeverDelivered() {
        var (ledger, _, evaluator, rules) = Arrange(
            new Post(Name: "first"),
            new Seal(),
            new Post(Name: "second")
        );

        _ = evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: ledger.Log,
            expected: [
                "preflighting",
                "preflight first",
                "preflight seal",
                "preflight second",
                "prepare",
                "committed",
                "unit",
                "fire first",
                "fire second",
            ]
        );
    }
    [Fact]
    public void AFiringWithNoTransactionalArmAsksForNoUnit() {
        var (ledger, _, evaluator, rules) = Arrange(new Post(Name: "only"));

        _ = evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        );

        Assert.DoesNotContain(
            collection: ledger.Log,
            expected: "prepare"
        );
        Assert.DoesNotContain(
            collection: ledger.Log,
            expected: "unit"
        );
    }
    [Fact]
    public void ADeliveryThatRefusesIsCountedAndStopsNothingAfterIt() {
        var (ledger, host, evaluator, rules) = Arrange(
            new Post(
                Name: "first",
                Refuses: true
            ),
            new Post(Name: "second")
        );

        _ = evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Contains(
            collection: ledger.Log,
            expected: "fire second"
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 3L
        );
        Assert.Equal(
            actual: Assert.Single(collection: evaluator.Diagnostics()).Refusal,
            expected: RuleEffectRefusal.IrreversibleArmFailed
        );
    }
    [Fact]
    public void AUnitTheHostCannotPrepareRewindsTheFiringBeforeAnythingLands() {
        var (ledger, host, evaluator, rules) = Arrange(
            new Seal(),
            new Post(Name: "after")
        );

        ledger.RefuseUnit = true;

        _ = evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: ledger.Log,
            expected: [
                "preflighting",
                "preflight seal",
                "preflight after",
                "prepare",
            ]
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: Assert.Single(collection: evaluator.Diagnostics()).Refusal,
            expected: RuleEffectRefusal.MutationRejected
        );
    }
}
