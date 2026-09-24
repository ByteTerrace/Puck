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
            ? Expr(State(row: "$local:dealt"))
            : Expr(
                State(row: "damage"),
                State(row: "hp"),
                Instruction.Of(operation: ExpressionOp.Minimum)
            )
        );

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot(
                name: "damage",
                value: 30L
            ), StateFixtures.IntSlot(
                name: "hp",
                value: 20L
            ), StateFixtures.IntSlot(
                name: "attacker",
                value: 100L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "strike"),
                [
                    new ActionEffect.AddState(
                        State: "hp",
                        Expression: Expr([.. dealt.Instructions, Instruction.Of(operation: ExpressionOp.Negate)])
                    ),
                    new ActionEffect.AddState(
                        State: "attacker",
                        Expression: Expr([.. dealt.Instructions, Instruction.Constant(value: 4m), Instruction.Of(operation: ExpressionOp.Divide), Instruction.Of(operation: ExpressionOp.Negate)])
                    ),
                ],
                Locals: (bound
            ? [new RuleLocal(
                            Expression: Expr(
                                State(row: "damage"),
                                State(row: "hp"),
                                Instruction.Of(operation: ExpressionOp.Minimum)
                            ),
                            Kind: CellKind.Int,
                            Name: CellName.Parse(candidate: "dealt")
                        )]
            : null)
            )],
        };
    }
    private static ExpressionProgram Expr(params Instruction[] tokens) => new(Instructions: tokens);
    private static Instruction State(string row) => Instruction.Operand(name: row);

    [Fact]
    public void ABindingReadsOnlyEarlierBindingsAndAppearsInTheReadBack() {
        var later = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot(
                name: "a",
                value: 1L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "a",
                        Expression: Expr(State(row: "$local:x"))
                    )],
                Locals: [
                new RuleLocal(
                        Expression: Expr(State(row: "$local:y")),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "x")
                    ),
                new RuleLocal(
                        Expression: Expr(State(row: "a")),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "y")
                    ),
            ]
            )],
        };
        var refusal = Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: later));

        Assert.Contains(
            "$local:y",
            refusal.Message,
            StringComparison.Ordinal
        );

        var ordered = later with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "a",
                        Expression: Expr(State(row: "$local:x"))
                    )],
                Locals: [
                new RuleLocal(
                        Expression: Expr(State(row: "a")),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "y")
                    ),
                new RuleLocal(
                        Expression: Expr(
                            State(row: "$local:y"),
                            Instruction.Constant(value: 2m),
                            Instruction.Of(operation: ExpressionOp.Multiply)
                        ),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "x")
                    ),
            ]
            )],
        };
        var compiled = Assert.Single(collection: WorldFactsCompiler.CompileAll(definition: ordered));

        Assert.Equal(
            ["y", "x"],
            compiled.Locals!.Select(selector: b => b.Name)
        );
    }
    [Fact]
    public void ABindingThatCannotEvaluateClosesTheGateAndIsReported() {
        var document = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot(
                name: "zero",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "hit",
                value: 0L
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hit",
                        Value: 1m
                    )],
                Locals: [new RuleLocal(
                        Expression: Expr(
                            Instruction.Constant(value: 1m),
                            State(row: "zero"),
                            Instruction.Of(operation: ExpressionOp.Divide)
                        ),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "q")
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "hit"
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
            fixture.SlotValue(row: "hp"
            )
        );
        Assert.Equal(
            95L,
            fixture.SlotValue(row: "attacker"
            )
        );
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());

        using var control = Fixtures.FreshServer(definition: Document(bound: false));

        control.Step();
        Assert.Equal(
            0L,
            control.SlotValue(row: "hp"
            )
        );
        Assert.Equal(
            100L,
            control.SlotValue(row: "attacker"
            )
        );
    }
}
