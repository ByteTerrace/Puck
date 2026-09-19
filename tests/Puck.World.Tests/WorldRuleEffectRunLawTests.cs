using Xunit;

namespace Puck.World.Tests;

/// <summary>Every top-level state effect in a rule evaluates its own expression, and a firing is one boundary: a
/// later effect refused by its destination's envelope rewinds the whole firing and is reported by rule and
/// effect.</summary>
public sealed class WorldRuleEffectRunLawTests {
    private static WorldDefinition Document(long capturedMax, bool asTransaction) {
        ActionEffect[] writes = [Set(
                "fromCell",
                Tz(row: "ownVac")
            ), Set(
                "toCell",
                Tz(row: "ownOcc")
            ), Set(
                "capturedCell",
                Tz(row: "otherVac")
            )];

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                Slot(
                max: 65535,
                name: "ownVac",
                value: 0L
            ), Slot(
                max: 65535,
                name: "ownOcc",
                value: 0L
            ), Slot(
                max: 65535,
                name: "otherVac",
                value: 0L
            ),
                Slot(
                max: 16,
                name: "fromCell",
                value: 0L
            ), Slot(
                max: 16,
                name: "toCell",
                value: 0L
            ), Slot(
                max: capturedMax,
                name: "capturedCell",
                value: 0L
            ),
            ]),
            Rules = [
                new WorldRule(
                CellName.Parse(candidate: "masks"),
                [Set(
                        "ownVac",
                        Instruction.Constant(value: 1m)
                    ), Set(
                        "ownOcc",
                        Instruction.Constant(value: 4m)
                    )]
            ),
                new WorldRule(
                CellName.Parse(candidate: "cells"),
                (asTransaction
            ? [new ActionEffect.Transaction(Effects: writes)]
            : writes)
            ),
            ],
        };
    }
    private static ActionEffect.SetState Set(string state, params Instruction[] tokens) => new(
        State: state,
        Expression: new ExpressionProgram(Instructions: tokens)
    );
    private static WorldStateRow Slot(string name, long value, long max) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Min: 0,
            Max: max,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: value)
                )]
        );
    private static Instruction[] Tz(string row) => [Instruction.Operand(name: row), Instruction.Of(operation: ExpressionOp.TrailingZeroCount)];
    private static long Value(WorldFixture fixture, string row) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                row
            )!.Cells,
            key: WorldStateRow.SlotKey
        )!.Value.Raw;

    [Fact]
    public void ALaterRefusedEffectRewindsTheFiringAndIsReported() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            asTransaction: false,
            capturedMax: 16
        ));

        fixture.Step();

        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "toCell"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "capturedCell"
            )
        );
        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics());

        Assert.Equal<Enum>(
            Puck.State.Rules.RuleEffectRefusal.MutationRejected,
            diagnostic.Refusal
        );
        Assert.Equal(
            "cells",
            diagnostic.Rule
        );
        Assert.Contains(
            "capturedCell",
            diagnostic.Effect,
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void ATransactionGroupsItsEffectsAtomically() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            asTransaction: true,
            capturedMax: 16
        ));

        fixture.Step();

        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "toCell"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "capturedCell"
            )
        );
        Assert.NotEmpty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
    [Fact]
    public void EveryEffectEvaluatesItsOwnExpression() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            asTransaction: false,
            capturedMax: 64
        ));

        fixture.Step();

        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "fromCell"
            )
        );
        Assert.Equal(
            2L,
            Value(
                fixture: fixture,
                row: "toCell"
            )
        );
        Assert.Equal(
            64L,
            Value(
                fixture: fixture,
                row: "capturedCell"
            )
        );
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
}
