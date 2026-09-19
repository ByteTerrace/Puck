using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Every refusal the compiler declares has a producing site: one authored rule (or group) that reaches it.
/// The coverage fact reads the enum itself, so a member added with no case fails the suite.</summary>
public sealed class RuleRefusalLawTests {
    private static readonly Dictionary<RuleRefusal, Action> Producers = new() {
        [RuleRefusal.NameMissing] = static () => RuleCompiler.CompileAll(
            context: RulesFixture.Context(),
            rules: [new Rule(
                Effects: [Write()],
                Name: default
            )]
        ),
        [RuleRefusal.NameDuplicated] = static () => RuleCompiler.CompileAll(
            context: RulesFixture.Context(),
            rules: [
                RulesFixture.Rule(name: "twice"),
                RulesFixture.Rule(name: "twice"),
            ]
        ),
        [RuleRefusal.NameReserved] = static () => RuleCompiler.CompileAll(
            context: RulesFixture.Context(),
            rules: [new Rule(
                Effects: [Write()],
                Name: CellName.Parse(candidate: "$minted")
            )]
        ),
        [RuleRefusal.PredicateKindInadmissible] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.Any(Predicates: []),
            name: "empty"
        )),
        [RuleRefusal.EffectKindInadmissible] = static () => Compile(rule: RulesFixture.Rule(
            effects: [],
            name: "none"
        )),
        [RuleRefusal.StateRowUnknown] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                State: "ghost",
                Value: 1m
            )],
            name: "unknown"
        )),
        [RuleRefusal.StateCellUnaddressable] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "codes",
                Value: 0m
            ),
            name: "bare"
        )),
        [RuleRefusal.ComparandAmbiguous] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "score"
            ),
            name: "neither"
        )),
        [RuleRefusal.ComparandKindMismatch] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                ComparandState: "ratio",
                State: "score"
            ),
            name: "mixed"
        )),
        [RuleRefusal.TargetInadmissible] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                State: "score",
                Target: ActionTarget.ProducerTarget,
                Value: 1m
            )],
            name: "target"
        )),
        [RuleRefusal.GeneratorUnknown] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.Generate(Row: "score")],
            name: "nodraw"
        )),
        [RuleRefusal.EffectSourceAmbiguous] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(State: "score")],
            name: "sourceless"
        )),
        [RuleRefusal.EffectSourceKindMismatch] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                FromState: "ratio",
                State: "score"
            )],
            name: "copykind"
        )),
        [RuleRefusal.DurationNotExactEngineTicks] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.SetState(
                State: "cooldown",
                ValueSeconds: 0.0000001m
            )],
            name: "inexact"
        )),
        [RuleRefusal.DurationEngineTicksOutOfRange] = static () => Compile(rule: RulesFixture.Rule(
            effects: [new ActionEffect.ScheduleState(
                DelaySeconds: 1_000_000_000_000_000_000m,
                State: "cooldown"
            )],
            name: "faraway"
        )),
        [RuleRefusal.StateCellUndeclared] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                Key: "zz",
                State: "codes",
                Value: 0m
            ),
            name: "ghostcell"
        )),
        [RuleRefusal.ReduceChannelMalformed] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "$reduce:median:codes",
                Value: 0m
            ),
            name: "reduce"
        )),
        [RuleRefusal.ZoneTableMalformed] = static () => Compile(rule: RulesFixture.Rule(
            name: "zones",
            zones: []
        )),
        [RuleRefusal.SymmetryChannelMalformed] = static () => Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: "$symmetry:spiral:node",
                Value: 0m
            ),
            name: "symmetry"
        )),
        [RuleRefusal.ArgRowNotKeyed] = static () => Compile(rule: RulesFixture.Rule(
            forEach: "score",
            name: "iterate"
        )),
        [RuleRefusal.RuleGroupMalformed] = static () => RuleCompiler.CompileGroups(
            context: RulesFixture.Context(),
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "loop"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: []
            )],
            rules: []
        ),
        [RuleRefusal.IrreversibleResultRead] = static () => Compile(
            context: IrreversibleFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [
                    new IrreversibleFixture.Stamp(),
                    new ActionEffect.SetState(
                        FromState: "score",
                        State: "score"
                    ),
                ],
                name: "deferred"
            )
        ),
        [RuleRefusal.VectorSpaceMismatch] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.SetState(
                    FromState: "narrow",
                    State: "wide"
                )],
                name: "space"
            )
        ),
        [RuleRefusal.VectorOperandNotVector] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Mean(
                    From: "score",
                    Into: "wide"
                ))],
                name: "notvector"
            )
        ),
        [RuleRefusal.VectorMixTerms] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Mix(
                    Into: "wide",
                    Terms: []
                ))],
                name: "mixterms"
            )
        ),
        [RuleRefusal.VectorFilterShape] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Mean(
                    From: "bank",
                    Into: "wide",
                    Where: "score"
                ))],
                name: "filter"
            )
        ),
        [RuleRefusal.VectorExcludeKey] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    Exclude: "a.b",
                    From: "bank",
                    Into: "ranks",
                    K: 1,
                    Query: "wide"
                ))],
                name: "exclude"
            )
        ),
        [RuleRefusal.VectorNearestShape] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "bank",
                    Into: "score",
                    K: 1,
                    Query: "wide"
                ))],
                name: "nearest"
            )
        ),
        [RuleRefusal.VectorRememberShape] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    From: "wide",
                    Into: "wide",
                    Key: "a",
                    UnlessWithin: "0.5"
                ))],
                name: "remember"
            )
        ),
        [RuleRefusal.VectorEffectNotAdmitted] = static () => Compile(
            context: VectorFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.AddState(
                    State: "wide",
                    Value: 1m
                )],
                name: "vectoradd"
            )
        ),
    };

    private static void Compile(Rule rule) => Compile(
        context: RulesFixture.Context(),
        rule: rule
    );
    private static void Compile(Rule rule, RuleCompileContext context) => RuleCompiler.Compile(
        context: context,
        rule: rule
    );
    private static ActionEffect Write() => new ActionEffect.SetState(
        State: "score",
        Value: 1m
    );

    [Fact]
    public void EveryDeclaredRefusalHasAProducingSite() {
        foreach (var refusal in Enum.GetValues<RuleRefusal>()) {
            Assert.True(
                condition: Producers.ContainsKey(key: refusal),
                userMessage: $"{refusal} declares no producing site in this suite."
            );
        }
    }
    [MemberData(memberName: nameof(Refusals))]
    [Theory]
    public void ARefusalIsRaisedByItsProducingSite(RuleRefusal refusal) {
        var exception = Assert.Throws<RuleException>(testCode: () => Producers[key: refusal]());

        Assert.Equal(
            actual: exception.Refusal,
            expected: refusal
        );
    }
    public static TheoryData<RuleRefusal> Refusals() {
        var data = new TheoryData<RuleRefusal>();

        foreach (var refusal in Enum.GetValues<RuleRefusal>()) {
            data.Add(row: refusal);
        }

        return data;
    }
}
