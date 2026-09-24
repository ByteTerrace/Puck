using Puck.Commands;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: the server's arena resync loads at its own time, so a state-value install that changes nothing
/// about a cycling cell leaves it turning by the same tick arithmetic — the observable the browser's rebind pins on
/// its own host.</summary>
public sealed class ServerResyncClockLawTests {
    private const string OutRow = "out";
    private const string SpinRow = "spin";
    private const long StepTicks = 20L;

    private static WorldDefinition Document() => (Fixtures.BuildDocumentAtRate(rateHz: Fixtures.RecordedTraceRateHz).WithWorldState(rows: [
        new WorldStateRow(
        Name: CellName.Parse(candidate: SpinRow),
        Kind: CellKind.Int,
        Cycle: new StateCycle(
            Output: CycleOutput.Step,
            TicksPerStep: StepTicks
        ),
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    ),
        new WorldStateRow(
        Name: CellName.Parse(candidate: OutRow),
        Kind: CellKind.Int,
        Max: 1000L,
        Min: 0L,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    ),
    ]) with {
        Rules = [new WorldRule(
            Name: CellName.Parse(candidate: "copy"),
            Mode: ActionTriggerMode.Level,
            Effects: [new ActionEffect.SetState(
                FromState: SpinRow,
                State: OutRow
            )]
        )],
    });
    private static long Read(WorldFixture fixture) => fixture.Row(name: OutRow).Cells![0].Value.Raw;

    [Fact]
    public void AResyncAtTickZeroLeavesACyclingCellTurningFromTheOrigin() {
        var document = Document();

        using var resynced = Fixtures.FreshServer(definition: document);
        using var untouched = Fixtures.FreshServer(definition: document);

        // A state-value install re-seeds the arena through the one import door, which is the server's own resync.
        resynced.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: OutRow,
            Key: WorldStateRow.SlotKey.Value,
            Value: 0,
            Kind: WorldDocumentWriteKind.Set
        ));

        for (var step = 0; (step < 160); step++) {
            resynced.Step();
            untouched.Step();
        }

        // Eight steps of twenty ticks each.
        Assert.Equal(
            actual: Read(fixture: untouched),
            expected: 8L
        );
        Assert.Equal(
            actual: Read(fixture: resynced),
            expected: 8L
        );
    }
}
