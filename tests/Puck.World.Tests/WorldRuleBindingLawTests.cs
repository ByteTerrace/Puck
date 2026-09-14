using Xunit;

namespace Puck.World.Tests;

/// <summary>A rule's bindings are computed once per evaluation, before the gate, in declared order: every effect
/// reads the same bound value even after an earlier effect changed the cells it was computed from; a binding may read
/// only the bindings declared before it; and a binding that cannot evaluate closes the gate and is reported.</summary>
public sealed class WorldRuleBindingLawTests {
    // dealt = min(damage, hp); hp -= dealt; recoil -= dealt / 4. Without the binding the second effect would recompute
    // min(damage, hp) against the already-reduced hp.
    private static WorldDefinition Document(bool bound) {
        var dealt = (bound
            ? Expr(State(row: "$bind:dealt"))
            : Expr(
                State(row: "damage"),
                State(row: "hp"),
                new ValueToken.Min()
            )
        );

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot(
                name: "damage",
                value: 30L
            ), Slot(
                name: "hp",
                value: 20L
            ), Slot(
                name: "attacker",
                value: 100L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "strike"),
                [
                    new ActionEffect.AddState(
                        State: "hp",
                        Expression: Expr([.. dealt.Tokens, new ValueToken.Negate()])
                    ),
                    new ActionEffect.AddState(
                        State: "attacker",
                        Expression: Expr([.. dealt.Tokens, new ValueToken.Constant(Value: 4m), new ValueToken.Divide(), new ValueToken.Negate()])
                    ),
                ],
                Bindings: (bound
            ? [new RuleBinding(
                            CellName.Parse(candidate: "dealt"),
                            CellKind.Int,
                            Expr(
                                State(row: "damage"),
                                State(row: "hp"),
                                new ValueToken.Min()
                            )
                        )]
            : null)
            )],
        };
    }
    private static ValueExpression Expr(params ValueToken[] tokens) => new(Tokens: tokens);
    private static WorldStateRow Slot(string name, long value) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    value
                )]
        );
    private static ValueToken State(string row) => new ValueToken.State(row);
    private static long Value(WorldFixture fixture, string row) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                row
            )!.Cells,
            key: WorldStateRow.SlotKey
        )!.Value;

    [Fact]
    public void ABindingReadsOnlyEarlierBindingsAndAppearsInTheReadBack() {
        var later = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot(
                name: "a",
                value: 1L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "a",
                        Expression: Expr(State(row: "$bind:x"))
                    )],
                Bindings: [
                new RuleBinding(
                        CellName.Parse(candidate: "x"),
                        CellKind.Int,
                        Expr(State(row: "$bind:y"))
                    ),
                new RuleBinding(
                        CellName.Parse(candidate: "y"),
                        CellKind.Int,
                        Expr(State(row: "a"))
                    ),
            ]
            )],
        };
        var refusal = Assert.Throws<RuleException>(testCode: () => WorldRuleCompiler.CompileAll(definition: later));

        Assert.Contains(
            "$bind:y",
            refusal.Message,
            StringComparison.Ordinal
        );

        var ordered = later with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "a",
                        Expression: Expr(State(row: "$bind:x"))
                    )],
                Bindings: [
                new RuleBinding(
                        CellName.Parse(candidate: "y"),
                        CellKind.Int,
                        Expr(State(row: "a"))
                    ),
                new RuleBinding(
                        CellName.Parse(candidate: "x"),
                        CellKind.Int,
                        Expr(
                            State(row: "$bind:y"),
                            new ValueToken.Constant(Value: 2m),
                            new ValueToken.Multiply()
                        )
                    ),
            ]
            )],
        };
        var compiled = Assert.Single(collection: WorldRuleCompiler.CompileAll(definition: ordered));

        Assert.Equal(
            ["y", "x"],
            compiled.Bindings!.Select(selector: b => b.Name)
        );
    }
    [Fact]
    public void ABindingThatCannotEvaluateClosesTheGateAndIsReported() {
        var document = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [Slot(
                name: "zero",
                value: 0L
            ), Slot(
                name: "hit",
                value: 0L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hit",
                        Value: 1m
                    )],
                Bindings: [new RuleBinding(
                        CellName.Parse(candidate: "q"),
                        CellKind.Int,
                        Expr(
                            new ValueToken.Constant(Value: 1m),
                            State(row: "zero"),
                            new ValueToken.Divide()
                        )
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "hit"
            )
        );
        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.Arithmetic,
            diagnostic.Refusal
        );
        Assert.Contains(
            "binding 'q'",
            diagnostic.Effect,
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void ABoundValueIsReadByEveryEffectAsComputedBeforeTheFirstWrite() {
        using var fixture = Fixtures.FreshServer(definition: Document(bound: true));

        fixture.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "hp"
            )
        );
        Assert.Equal(
            95L,
            Value(
                fixture: fixture,
                row: "attacker"
            )
        );
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());

        using var control = Fixtures.FreshServer(definition: Document(bound: false));

        control.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: control,
                row: "hp"
            )
        );
        Assert.Equal(
            100L,
            Value(
                fixture: control,
                row: "attacker"
            )
        );
    }
}
