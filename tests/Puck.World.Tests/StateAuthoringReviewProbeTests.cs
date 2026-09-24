using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class StateAuthoringReviewProbeTests {
    [Fact]
    public void DisablingDynamicsFreezesTheSampleInsteadOfTheTarget() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewGauge"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "probe"),
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 100), Clock: new StateCellClock(Y0: (20L * 65536L)))]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: row with { Dynamics = null }
        ));
        fixture.Step();
        var actual = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "reviewGauge")!;

        Assert.Equal(expected: 20L, actual: actual.Cells![0].Value.Raw);
    }
    [InlineData(WorldDocumentWriteKind.Set, 100L, 100L)]
    [InlineData(WorldDocumentWriteKind.Add, 5L, 15L)]
    [Theory]
    public void ExplicitWriteToAdvancingCellKeepsWrittenValue(WorldDocumentWriteKind kind, long operand, long expected) {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewCounter"),
            Kind: CellKind.Int,
            Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 1),
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 10))]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "reviewCounter",
            Key: StateRow.SlotKey,
            Value: operand,
            Kind: kind
        ));
        fixture.Step();
        var actual = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "reviewCounter")!;

        Assert.Equal(expected: expected, actual: actual.Cells![0].Value.Raw);
    }
}
