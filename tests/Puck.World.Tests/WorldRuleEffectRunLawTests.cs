using Xunit;

namespace Puck.World.Tests;

/// <summary>Every top-level state effect in a rule evaluates its own expression and is its own boundary: a later
/// effect refused by its destination's envelope leaves the earlier writes applied and is reported by rule and effect.
/// Only a transaction groups effects atomically.</summary>
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
                        new ValueToken.Constant(Value: 1m)
                    ), Set(
                        "ownOcc",
                        new ValueToken.Constant(Value: 4m)
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
    private static ActionEffect.SetState Set(string state, params ValueToken[] tokens) => new(
        State: state,
        Expression: new ValueExpression(Tokens: tokens)
    );
    private static WorldStateRow Slot(string name, long value, long max) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Min: 0,
            Max: max,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    value
                )]
        );
    private static ValueToken[] Tz(string row) => [new ValueToken.State(row), new ValueToken.TrailingZeroCount()];
    private static long Value(WorldFixture fixture, string row) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                row
            )!.Cells,
            key: WorldStateRow.SlotKey
        )!.Value;

    [Fact]
    public void ALaterRefusedEffectLeavesEarlierWritesAppliedAndIsReported() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            asTransaction: false,
            capturedMax: 16
        ));

        fixture.Step();

        Assert.Equal(
            2L,
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
            RuleEffectRefusal.MutationRejected,
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
    public void ATransactionIsTheOneAtomicGroup() {
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
