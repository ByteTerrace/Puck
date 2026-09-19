using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>transformState</c> effect fires through the evaluator over the resolved
/// transform its rule carries, resolves the one dynamic key and the one live value a transform may spell fresh for
/// each firing, and rewinds with the firing when the transform refuses.</summary>
public sealed class TransformFiringLawTests {
    private static (ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch, RuleCompileContext Context) Arrange(IReadOnlyList<Rule> rules) {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: rules
        );
        var host = TransformFixture.Host(
            context: context,
            section: section
        );

        return (host, new RuleEvaluator(host: host), compiled, new RuleLatch(), context);
    }
    private static Rule Rule(string name, ActionEffect effect) => new(
        Name: TransformFixture.Name(value: name),
        Effects: [effect],
        Mode: ActionTriggerMode.Level
    );

    [Fact]
    public void ATransformStateEffectFiresOverTheTransformItsRuleResolvedOnce() {
        var (host, evaluator, rules, latch, context) = Arrange(rules: [Rule(
                effect: new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                    From: "deck",
                    Selector: ZoneSelector.First,
                    To: "hand"
                )),
                name: "deal"
            )]);

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "hand"
                )
            ),
            expected: "a=1"
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void ATransformsDynamicKeyResolvesFreshForEachFiring() {
        var (host, evaluator, rules, latch, context) = Arrange(rules: [Rule(
                effect: new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                    Row: "target",
                    Set: "scores",
                    SetKey: "$cell:pointer:$value",
                    Value: 1L
                )),
                name: "paint"
            )]);
        var arena = host.Arena;
        var target = TransformFixture.Ordinal(
            context: context,
            name: "target"
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: arena,
                rowOrdinal: target
            ),
            expected: "1,0,1,0"
        );
        Assert.True(condition: arena.TryWriteText(
            key: arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            reason: out var reason,
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "pointer"
            ),
            text: "z"
        ), userMessage: reason);
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: arena,
                rowOrdinal: target
            ),
            expected: "1,0,1,1"
        );
    }
    [Fact]
    public void ASetRayOriginResolvesALiveKeyTheWayATransferKeyDoes() {
        // board reads 5,1,1,0 and the 'ones' pattern accepts a run of 1s, so the origin decides how much of that run
        // the ray covers: from cell 0 the word is 1,1,0 and from cell 1 it is 1,0.
        static string Fired(decimal origin) {
            var (host, evaluator, rules, latch, context) = Arrange(rules: [new Rule(
                    Name: TransformFixture.Name(value: "burn"),
                    Effects: [
                        EvaluatorFixture.Set(
                            row: "rankValue",
                            value: origin
                        ),
                        new ActionEffect.TransformState(Transform: new StateTransform.SetRay(
                            Direction: "E",
                            From: "$cell:rankValue:$value",
                            Pattern: "ones",
                            Row: "board",
                            Value: 9L
                        )),
                    ],
                    Mode: ActionTriggerMode.Level
                )]);

            Assert.True(condition: evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL
            ));

            return TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "board"
                )
            );
        }

        Assert.Equal(
            actual: Fired(origin: 0m),
            expected: "5,9,9,0"
        );
        Assert.Equal(
            actual: Fired(origin: 1m),
            expected: "5,1,9,0"
        );
    }
    [Fact]
    public void APushTakesTheLiveValueSourcesPushStateTakes() {
        var (host, evaluator, rules, latch, context) = Arrange(rules: [new Rule(
                Name: TransformFixture.Name(value: "record"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "rankValue",
                        value: 7m
                    ),
                    new ActionEffect.TransformState(Transform: new StateTransform.Push(
                        FromState: "rankValue",
                        Row: "log"
                    )),
                    new ActionEffect.TransformState(Transform: new StateTransform.Push(
                        Expression: RulesFixture.Program(text: "rankValue + 1"),
                        Row: "log"
                    )),
                    new ActionEffect.TransformState(Transform: new StateTransform.Push(
                        Row: "log",
                        Value: 21L
                    )),
                ],
                Mode: ActionTriggerMode.Level
            )]);

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: TransformFixture.PositionListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "log"
                )
            ),
            expected: "7,8,21"
        );
    }
    [Fact]
    public void APushNamingALiveSourceBesideALiteralRefusesAsAmbiguous() => Assert.Equal(
        actual: RulesFixture.Refusal(rule: RulesFixture.Rule(
            effects: [new ActionEffect.TransformState(Transform: new StateTransform.Push(
                FromState: "score",
                Row: "history",
                Value: 3L
            ))],
            name: "both"
        )),
        expected: RuleRefusal.EffectSourceAmbiguous
    );
    [Fact]
    public void ARefusedTransformRewindsTheWholeFiringAndCountsOneRefusal() {
        var (host, evaluator, rules, latch, context) = Arrange(rules: [new Rule(
                Name: TransformFixture.Name(value: "dealTooMany"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "rankValue",
                        value: 4m
                    ),
                    new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                        Count: 4,
                        From: "deck",
                        Selector: ZoneSelector.First,
                        To: "hand"
                    )),
                ],
                Mode: ActionTriggerMode.Level
            )]);
        var arena = host.Arena;
        var before = arena.ComputeHash();

        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "hand"
                )
            ),
            expected: string.Empty
        );

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal(
            actual: diagnostic.Refusal,
            expected: TransformRefusal.TransferSourceShort
        );
    }
}
