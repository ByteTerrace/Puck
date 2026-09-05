using Xunit;

namespace Puck.World.Tests;

/// <summary>A rule's bindings are computed once per evaluation, before the gate, in declared order: every effect
/// reads the same bound value even after an earlier effect changed the cells it was computed from; a binding may read
/// only the bindings declared before it; and a binding that cannot evaluate closes the gate and is reported.</summary>
public sealed class WorldRuleBindingLawTests {
    private static WorldStateRow Slot(string name, long value) =>
        new(CellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static ValueToken State(string row) => new ValueToken.State(row);
    private static ValueExpression Expr(params ValueToken[] tokens) => new(tokens);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;

    // dealt = min(damage, hp); hp -= dealt; recoil -= dealt / 4. Without the binding the second effect would recompute
    // min(damage, hp) against the already-reduced hp.
    private static WorldDefinition Document(bool bound) {
        var dealt = bound
            ? Expr(State("$bind:dealt"))
            : Expr(State("damage"), State("hp"), new ValueToken.Min());
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot("damage", 30L), Slot("hp", 20L), Slot("attacker", 100L)]),
            Rules = [new WorldRule(
                CellName.Parse("strike"),
                [
                    new ActionEffect.AddState(State: "hp", Expression: Expr([.. dealt.Tokens, new ValueToken.Negate()])),
                    new ActionEffect.AddState(State: "attacker", Expression: Expr([.. dealt.Tokens, new ValueToken.Constant(4m), new ValueToken.Divide(), new ValueToken.Negate()])),
                ],
                Bindings: bound ? [new WorldRuleBinding(CellName.Parse("dealt"), CellKind.Int, Expr(State("damage"), State("hp"), new ValueToken.Min()))] : null
            )],
        };
    }

    [Fact]
    public void ABoundValueIsReadByEveryEffectAsComputedBeforeTheFirstWrite() {
        using var fixture = Fixtures.FreshServer(definition: Document(bound: true));
        fixture.Step();
        Assert.Equal(0L, Value(fixture, "hp"));
        Assert.Equal(95L, Value(fixture, "attacker"));
        Assert.Empty(fixture.Server.RuleRuntimeDiagnostics());

        using var control = Fixtures.FreshServer(definition: Document(bound: false));
        control.Step();
        Assert.Equal(0L, Value(control, "hp"));
        Assert.Equal(100L, Value(control, "attacker"));
    }

    [Fact]
    public void ABindingReadsOnlyEarlierBindingsAndAppearsInTheReadBack() {
        var later = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot("a", 1L)]),
            Rules = [new WorldRule(CellName.Parse("r"), [new ActionEffect.SetState(State: "a", Expression: Expr(State("$bind:x")))], Bindings: [
                new WorldRuleBinding(CellName.Parse("x"), CellKind.Int, Expr(State("$bind:y"))),
                new WorldRuleBinding(CellName.Parse("y"), CellKind.Int, Expr(State("a"))),
            ])],
        };
        var refusal = Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAll(later));
        Assert.Contains("$bind:y", refusal.Message, StringComparison.Ordinal);

        var ordered = later with {
            Rules = [new WorldRule(CellName.Parse("r"), [new ActionEffect.SetState(State: "a", Expression: Expr(State("$bind:x")))], Bindings: [
                new WorldRuleBinding(CellName.Parse("y"), CellKind.Int, Expr(State("a"))),
                new WorldRuleBinding(CellName.Parse("x"), CellKind.Int, Expr(State("$bind:y"), new ValueToken.Constant(2m), new ValueToken.Multiply())),
            ])],
        };
        var compiled = Assert.Single(WorldRuleCompiler.CompileAll(ordered));
        Assert.Equal(["y", "x"], compiled.Bindings!.Select(b => b.Name));
    }

    [Fact]
    public void ABindingThatCannotEvaluateClosesTheGateAndIsReported() {
        var document = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot("zero", 0L), Slot("hit", 0L)]),
            Rules = [new WorldRule(
                CellName.Parse("r"),
                [new ActionEffect.SetState(State: "hit", Value: 1m)],
                Bindings: [new WorldRuleBinding(CellName.Parse("q"), CellKind.Int, Expr(new ValueToken.Constant(1m), State("zero"), new ValueToken.Divide()))]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);
        fixture.Step();
        Assert.Equal(0L, Value(fixture, "hit"));
        var diagnostic = Assert.Single(fixture.Server.RuleRuntimeDiagnostics());
        Assert.Equal(WorldRuleEffectRefusal.Arithmetic, diagnostic.Refusal);
        Assert.Contains("binding 'q'", diagnostic.Effect, StringComparison.Ordinal);
    }
}
