using Puck.Commands;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

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

    private static WorldStateRow Slot(string name, long value) =>
        new(CellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static ValueExpression Expr(string text) => ValueExpression.Parse(text);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;

    // strike: dealt = min(damage, hp); hp -= dealt while hp > 0. The second evaluation binds dealt = 0 and its write
    // cannot move hp, which is the skipped outcome; the third closes the gate.
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [Slot("damage", 30L), Slot("hp", 20L), Slot("hits", 0L)]),
        Rules = [new WorldRule(
            CellName.Parse("strike"),
            [
                new ActionEffect.AddState(State: "hp", Expression: Expr("-$bind:dealt")),
                new ActionEffect.AddState(State: "hits", Value: 1m),
            ],
            Gate: new ActionPredicate.CompareState(State: "hp", Comparison: ActionStateComparison.GreaterOrEqual, Value: 0m),
            Bindings: [new WorldRuleBinding(CellName.Parse("dealt"), CellKind.Int, Expr("min(damage, hp)"))]
        )],
    };

    [Fact]
    public void ACaptureRecordsBindingsConjunctsAndEffectOutcomesAndStopsAtItsCount() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        Assert.True(fixture.Server.TryArmRuleTrace(rule: "strike", evaluations: 2, refusal: out var refusal), refusal);

        fixture.Step();
        fixture.Step();
        fixture.Step();

        var lines = fixture.Server.DescribeRuleTrace().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("[world.rule.trace strike: 2/2 evaluation(s) captured, complete]", lines[0]);
        Assert.Contains("bind [dealt=20]", lines[1], StringComparison.Ordinal);
        Assert.Contains("gate=open: hp >= 0: 20 >= 0 -> true", lines[1], StringComparison.Ordinal);
        Assert.Contains("= -20: applied", lines[1], StringComparison.Ordinal);
        Assert.Contains("hits", lines[1], StringComparison.Ordinal);
        Assert.Contains("bind [dealt=0]", lines[2], StringComparison.Ordinal);
        Assert.Contains("= 0: skipped (could not move the destination)", lines[2], StringComparison.Ordinal);
        Assert.Equal(0L, Value(fixture, "hp"));
        Assert.Equal(3L, Value(fixture, "hits"));
    }

    [Fact]
    public void AClosedGateAndARefusedBindingAreBothVisible() {
        var closed = Document() with {
            StateRaw = new WorldStateSection(World: [Slot("damage", 30L), Slot("hp", -1L), Slot("hits", 0L)]),
        };
        using var fixture = Fixtures.FreshServer(definition: closed);
        Assert.True(fixture.Server.TryArmRuleTrace(rule: "strike", evaluations: 1, refusal: out _));
        fixture.Step();
        var line = fixture.Server.DescribeRuleTrace().Split(Environment.NewLine)[1];
        Assert.Contains("gate=closed: hp >= 0: -1 >= 0 -> false", line, StringComparison.Ordinal);
        Assert.DoesNotContain("->", line.Split("-> false")[1], StringComparison.Ordinal);

        var dividing = Document() with {
            StateRaw = new WorldStateSection(World: [Slot("damage", 30L), Slot("hp", 20L), Slot("hits", 0L), Slot("zero", 0L)]),
            Rules = [Document().Rules![0] with {
                Bindings = [new WorldRuleBinding(CellName.Parse("dealt"), CellKind.Int, Expr("damage / zero"))],
            }],
        };
        using var refused = Fixtures.FreshServer(definition: dividing);
        Assert.True(refused.Server.TryArmRuleTrace(rule: "strike", evaluations: 1, refusal: out _));
        refused.Step();
        Assert.Contains("bind [dealt=refused] gate=closed", refused.Server.DescribeRuleTrace(), StringComparison.Ordinal);
    }

    [Fact]
    public void TracingIsAnObserverThatLeavesTheStateHashAlone() {
        using var traced = Fixtures.FreshServer(definition: Document());
        using var control = Fixtures.FreshServer(definition: Document());
        Assert.True(traced.Server.TryArmRuleTrace(rule: "strike", evaluations: WorldServer.MaxRuleTraceEvaluations, refusal: out _));
        for (var step = 0; step < 4; step++) {
            traced.Step();
            control.Step();
        }
        Assert.Equal(
            WorldRuntimeStateHash.Hash(scope: WorldStateHashScope.Authoritative, server: control.Server, tick: control.Server.CompletedEngineTicks),
            WorldRuntimeStateHash.Hash(scope: WorldStateHashScope.Authoritative, server: traced.Server, tick: traced.Server.CompletedEngineTicks)
        );
    }

    [Fact]
    public void ArmingIsRefusedByNameAndDisarmingForgetsTheCapture() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        Assert.False(fixture.Server.TryArmRuleTrace(rule: "nothing", evaluations: 1, refusal: out var unknown));
        Assert.Contains("no rule or interaction named 'nothing'", unknown, StringComparison.Ordinal);
        Assert.False(fixture.Server.TryArmRuleTrace(rule: "strike", evaluations: WorldServer.MaxRuleTraceEvaluations + 1, refusal: out var tooMany));
        Assert.Contains($"1..{WorldServer.MaxRuleTraceEvaluations}", tooMany, StringComparison.Ordinal);
        Assert.False(fixture.Server.DisarmRuleTrace());

        Assert.True(fixture.Server.TryArmRuleTrace(rule: "strike", evaluations: 4, refusal: out _));
        fixture.Step();
        Assert.StartsWith("[world.rule.trace strike: 1/4 evaluation(s) captured, armed]", fixture.Server.DescribeRuleTrace(), StringComparison.Ordinal);
        Assert.True(fixture.Server.DisarmRuleTrace());
        Assert.StartsWith("[world.rule.trace: none armed", fixture.Server.DescribeRuleTrace(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbArmsReadsBackAndDisarms() {
        using var row = HostRow.Build(name: "boot", definition: Document());
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link, echoes: new WorldDeferredVerbEchoes())]);

        Assert.Equal("[world.rule.trace: none armed — world.rule.trace <rule> [evaluations] arms one]", registry.Submit(line: "world.rule.trace").Output);
        Assert.Equal("[world.rule.trace strike: armed for 3 evaluation(s) — world.wait, then world.rule.trace reads them back]", registry.Submit(line: "world.rule.trace strike 3").Output);
        Assert.True(registry.Submit(line: "world.rule.trace nothing").IsError);
        Assert.True(registry.Submit(line: "world.rule.trace strike 0").IsError);
        Assert.Equal("[world.rule.trace: disarmed]", registry.Submit(line: "world.rule.trace off").Output);
        Assert.Equal("[world.rule.trace: none armed]", registry.Submit(line: "world.rule.trace off").Output);

        var budget = registry.Submit(line: "world.budget.rules 1").Output.Split(Environment.NewLine);
        Assert.Equal("[world.budget.rules: 1 line(s), showing 1]", budget[0]);
        Assert.StartsWith("[world.budget.rules strike x1 unit=", budget[1], StringComparison.Ordinal);
    }
}
