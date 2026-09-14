using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves an explicit write keeps the value it composed on a cell with a value-over-time behavior, and that
/// switching a dynamics follower off freezes it at its sampled position rather than its target.</summary>
public sealed class StateBehaviorWriteLawTests {
    private static readonly WorldPrincipal Actor = WorldPrincipal.Seat(slot: 0);
    private static readonly DynamicsRow KickZero = new(
        Damping: 1f,
        Frequency: 1f,
        Name: "kickZero",
        Response: 0f
    );

    private static WorldDefinition BuildDocument(StateCell cell) =>
        (Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "gauge"),
                Kind: CellKind.Int,
                Capacity: 8,
                Cells: [cell]
            ),
        ]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickZero],
        });
    private static StateCell StoredCell(WorldDefinition definition) =>
        WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: "gauge"
        )!.Cells!.Single(predicate: static cell => (cell.Key.Value == "0"));
    private static void Write(WorldFixture fixture, long value, WorldDocumentWriteKind kind) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor,
            Row: "gauge",
            Key: "0",
            Value: value,
            Kind: kind
        ));
        fixture.Step();
    }

    [Theory]
    [InlineData(WorldDocumentWriteKind.Set, 100L, 100L)]
    [InlineData(WorldDocumentWriteKind.Add, 5L, 15L)]
    public void ExplicitWriteToAnAdvancingCell_KeepsTheComposedValue(WorldDocumentWriteKind kind, long operand, long expected) {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(cell: new StateCell(
            Key: CellName.Parse(candidate: "0"),
            Value: 10,
            Advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 0
            )
        )));

        Write(
            fixture: fixture,
            kind: kind,
            value: operand
        );

        Assert.Equal(
            actual: StoredCell(definition: fixture.Server.Definition).Value,
            expected: expected
        );
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: "0",
            rawValue: out var live,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));
        Assert.Equal(
            actual: live,
            expected: expected
        );
    }

    [Fact]
    public void SwitchingDynamicsOff_FreezesAtTheSampledPositionRatherThanTheTarget() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(cell: new StateCell(
            Key: CellName.Parse(candidate: "0"),
            Value: 20,
            Dynamics: new StateDynamics(Row: "kickZero"),
            Clock: new StateCellClock(Y0: (20L << Puck.Maths.FixedQ4816.FractionBitCount))
        )));

        Write(
            fixture: fixture,
            kind: WorldDocumentWriteKind.Set,
            value: 100
        );

        for (var index = 0; (index < 3); index++) {
            fixture.Step();
        }

        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: "0",
            rawValue: out var easedBefore,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));
        var clockBefore = StoredCell(definition: fixture.Server.Definition).Clock;

        Assert.True(
            condition: (easedBefore > 20L) && (easedBefore < 100L),
            userMessage: $"eased={easedBefore} value={StoredCell(definition: fixture.Server.Definition).Value} clock={clockBefore} next={fixture.Server.NextInputTick}"
        );

        var row = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "gauge"
        )!;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: (row with { Cells = [(StoredCell(definition: fixture.Server.Definition) with { Dynamics = null })] })
        ));
        fixture.Step();

        var frozen = StoredCell(definition: fixture.Server.Definition);

        Assert.Null(@object: frozen.Dynamics);
        Assert.InRange(
            actual: frozen.Value,
            high: 99L,
            low: 21L
        );

        fixture.Step();

        Assert.Equal(
            actual: StoredCell(definition: fixture.Server.Definition).Value,
            expected: frozen.Value
        );
    }

    [Fact]
    public void SwitchingAnAdvancingCellToDynamics_RestsTheTargetOnTheLiveValue() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(cell: new StateCell(
            Key: CellName.Parse(candidate: "0"),
            Value: 10,
            Advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 50400
            ),
            Clock: new StateCellClock()
        )));

        for (var index = 0; (index < 3); index++) {
            fixture.Step();
        }

        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: "0",
            rawValue: out var liveBefore,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));
        Assert.True(condition: (liveBefore > 10L));

        var row = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "gauge"
        )!;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: (row with { Cells = [(StoredCell(definition: fixture.Server.Definition) with { Advance = null, Dynamics = new StateDynamics(Row: "kickZero") })] })
        ));
        fixture.Step();

        var switched = StoredCell(definition: fixture.Server.Definition);

        Assert.True(
            condition: (switched.Value >= liveBefore),
            userMessage: $"target={switched.Value} liveBefore={liveBefore}"
        );
        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: "0",
            rawValue: out var eased,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));
        Assert.Equal(
            actual: eased,
            expected: switched.Value
        );
    }

    [Fact]
    public void RetuningADynamicsFollower_KeepsTheTargetItWasEasingToward() {
        using var fixture = Fixtures.FreshServer(definition: (BuildDocument(cell: new StateCell(
            Key: CellName.Parse(candidate: "0"),
            Value: 20,
            Dynamics: new StateDynamics(Row: "kickZero"),
            Clock: new StateCellClock(Y0: (20L << Puck.Maths.FixedQ4816.FractionBitCount))
        )) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickZero, KickZero with { Name = "kickSlow", Frequency = 0.5f }],
        }));

        Write(
            fixture: fixture,
            kind: WorldDocumentWriteKind.Set,
            value: 100
        );
        fixture.Step();

        var row = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "gauge"
        )!;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: (row with { Cells = [(StoredCell(definition: fixture.Server.Definition) with { Dynamics = new StateDynamics(Row: "kickSlow") })] })
        ));
        fixture.Step();

        Assert.Equal(
            actual: StoredCell(definition: fixture.Server.Definition).Value,
            expected: 100L
        );
    }
}
