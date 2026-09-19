using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

using RuleEvaluator = Puck.State.Rules.RuleEvaluator;

namespace Puck.World.Tests;

/// <summary>An armed rule trace captures each evaluation's bindings, every gate conjunct with the values it
/// compared, and each effect's outcome, without touching simulation state; it stops at the count it was armed for,
/// refuses a name that is not a rule, and disarms on request.</summary>
public sealed class WorldRuleTraceLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;
            return true;
        }
    }

    // strike: dealt = minimum(damage, hp); hp -= dealt while hp > 0. The second evaluation binds dealt = 0 and its write
    // cannot move hp, which is the skipped outcome; the third closes the gate.
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [Slot(
            name: "damage",
            value: 30L
        ), Slot(
            name: "hp",
            value: 20L
        ), Slot(
            name: "hits",
            value: 0L
        )]),
        Rules = [new WorldRule(
            CellName.Parse(candidate: "strike"),
            [
                new ActionEffect.AddState(
                    State: "hp",
                    Expression: Expr(text: "-$local:dealt")
                ),
                new ActionEffect.AddState(
                    State: "hits",
                    Value: 1m
                ),
            ],
            Gate: new ActionPredicate.CompareState(
                State: "hp",
                Comparison: ActionStateComparison.GreaterOrEqual,
                Value: 0m
            ),
            Locals: [new RuleLocal(
                    CellName.Parse(candidate: "dealt"),
                    CellKind.Int,
                    Expr(text: "minimum(damage, hp)")
                )]
        )],
    };
    private static ExpressionProgram Expr(string text) => ExpressionProgram.Parse(text: text);
    private static WorldStateRow Slot(string name, long value) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: value)
                )]
        );
    private static long Value(WorldFixture fixture, string row) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                row
            )!.Cells,
            key: WorldStateRow.SlotKey
        )!.Value.Raw;

    [Fact]
    public void ACaptureRecordsBindingsConjunctsAndEffectOutcomesAndStopsAtItsCount() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        Assert.True(
            condition: fixture.Server.TryArmRuleTrace(
                evaluations: 2,
                refusal: out var refusal,
                rule: "strike"
            ),
            userMessage: refusal
        );

        fixture.Step();
        fixture.Step();
        fixture.Step();

        var lines = fixture.Server.DescribeRuleTrace().Split(Environment.NewLine);

        Assert.Equal(
            3,
            lines.Length
        );
        Assert.Equal(
            "[world.rule.trace strike: 2/2 evaluation(s) captured, complete]",
            lines[0]
        );
        Assert.Contains(
            "local [dealt=20]",
            lines[1],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "gate=open: hp >= 0: 20 >= 0 -> true",
            lines[1],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "= -20: applied",
            lines[1],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "hits",
            lines[1],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "local [dealt=0]",
            lines[2],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "= 0: skipped (could not move the destination)",
            lines[2],
            StringComparison.Ordinal
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "hp"
            )
        );
        Assert.Equal(
            3L,
            Value(
                fixture: fixture,
                row: "hits"
            )
        );
    }
    [Fact]
    public void AClosedGateAndARefusedBindingAreBothVisible() {
        var closed = Document() with {
            StateRaw = new WorldStateSection(World: [Slot(
                name: "damage",
                value: 30L
            ), Slot(
                name: "hp",
                value: -1L
            ), Slot(
                name: "hits",
                value: 0L
            )]),
        };
        using var fixture = Fixtures.FreshServer(definition: closed);

        Assert.True(condition: fixture.Server.TryArmRuleTrace(
            evaluations: 1,
            refusal: out _,
            rule: "strike"
        ));
        fixture.Step();
        var line = fixture.Server.DescribeRuleTrace().Split(Environment.NewLine)[1];

        Assert.Contains(
            actualString: line,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate=closed: hp >= 0: -1 >= 0 -> false"
        );
        Assert.DoesNotContain(
            "->",
            line.Split("-> false")[1],
            StringComparison.Ordinal
        );

        var dividing = Document() with {
            StateRaw = new WorldStateSection(World: [Slot(
                name: "damage",
                value: 30L
            ), Slot(
                name: "hp",
                value: 20L
            ), Slot(
                name: "hits",
                value: 0L
            ), Slot(
                name: "zero",
                value: 0L
            )]),
            Rules = [Document().Rules![0] with {
                Locals = [new RuleLocal(
                    CellName.Parse(candidate: "dealt"),
                    CellKind.Int,
                    Expr(text: "damage / zero")
                )],
            }],
        };
        using var refused = Fixtures.FreshServer(definition: dividing);

        Assert.True(condition: refused.Server.TryArmRuleTrace(
            evaluations: 1,
            refusal: out _,
            rule: "strike"
        ));
        refused.Step();
        Assert.Contains(
            "local [dealt=refused] gate=closed",
            refused.Server.DescribeRuleTrace(),
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void ArmingIsRefusedByNameAndDisarmingForgetsTheCapture() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        Assert.False(condition: fixture.Server.TryArmRuleTrace(
            evaluations: 1,
            refusal: out var unknown,
            rule: "nothing"
        ));
        Assert.Contains(
            actualString: unknown,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no rule or interaction named 'nothing'"
        );
        Assert.False(condition: fixture.Server.TryArmRuleTrace(
            evaluations: (RuleEvaluator.MaxTraceEvaluations + 1),
            refusal: out var tooMany,
            rule: "strike"
        ));
        Assert.Contains(
            actualString: tooMany,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"1..{RuleEvaluator.MaxTraceEvaluations}"
        );
        Assert.False(condition: fixture.Server.DisarmRuleTrace());

        Assert.True(condition: fixture.Server.TryArmRuleTrace(
            evaluations: 4,
            refusal: out _,
            rule: "strike"
        ));
        fixture.Step();
        Assert.StartsWith(
            "[world.rule.trace strike: 1/4 evaluation(s) captured, armed]",
            fixture.Server.DescribeRuleTrace(),
            StringComparison.Ordinal
        );
        Assert.True(condition: fixture.Server.DisarmRuleTrace());
        Assert.StartsWith(
            "[world.rule.trace: none armed",
            fixture.Server.DescribeRuleTrace(),
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void TheVerbArmsReadsBackAndDisarms() {
        using var row = HostRow.Build(
            name: "boot",
            definition: Document()
        );
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
                authority: new FakeConsoleAuthority(instance: row.Instance),
                link: row.Instance.Link,
                echoes: new WorldDeferredVerbEchoes()
            )]);

        Assert.Equal(
            "[world.rule.trace: none armed — world.rule.trace <rule> [evaluations] arms one]",
            registry.Submit(line: "world.rule.trace").Output
        );
        Assert.Equal(
            "[world.rule.trace strike: armed for 3 evaluation(s) — world.wait, then world.rule.trace reads them back]",
            registry.Submit(line: "world.rule.trace strike 3").Output
        );
        Assert.True(condition: registry.Submit(line: "world.rule.trace nothing").IsError);
        Assert.True(condition: registry.Submit(line: "world.rule.trace strike 0").IsError);
        Assert.Equal(
            "[world.rule.trace: disarmed]",
            registry.Submit(line: "world.rule.trace off").Output
        );
        Assert.Equal(
            "[world.rule.trace: none armed]",
            registry.Submit(line: "world.rule.trace off").Output
        );

        var budget = registry.Submit(line: "world.budget.rules 1").Output.Split(Environment.NewLine);

        Assert.Equal(
            "[world.budget.rules: 1 line(s), showing 1]",
            budget[0]
        );
        Assert.StartsWith(
            "[world.budget.rules strike x1 unit=",
            budget[1],
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void TracingIsAnObserverThatLeavesTheStateHashAlone() {
        using var traced = Fixtures.FreshServer(definition: Document());
        using var control = Fixtures.FreshServer(definition: Document());

        Assert.True(condition: traced.Server.TryArmRuleTrace(
            evaluations: RuleEvaluator.MaxTraceEvaluations,
            refusal: out _,
            rule: "strike"
        ));
        for (var step = 0; (step < 4); step++) {
            traced.Step();
            control.Step();
        }
        Assert.Equal(
            WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Authoritative,
                server: control.Server,
                tick: control.Server.CompletedEngineTicks
            ),
            WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Authoritative,
                server: traced.Server,
                tick: traced.Server.CompletedEngineTicks
            )
        );
    }
}
