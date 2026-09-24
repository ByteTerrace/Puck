using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>THE LAW: a rebind loads the arena at the session's own time, so a cycling cell keeps turning by the
/// same tick arithmetic the server's resync gives it — a no-op rebind at tick 0 leaves the cell reading its
/// unrebound value, and a rebind mid-session re-births the clock where the session stands.</summary>
public sealed class BrowserRebindClockLawTests {
    private const string OutRow = "out";
    private const string SpinRow = "spin";
    private const long StepTicks = 20L;

    private static WorldDefinition Document() =>
        new(
            Rules: [new WorldRule(
                Name: CellName.Parse(candidate: "copy"),
                Mode: ActionTriggerMode.Level,
                Effects: [new ActionEffect.SetState(
                        FromState: SpinRow,
                        State: OutRow
                    )]
            )],
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [
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
            ])
        );
    private static string Read(BrowserSession session, ulong tick) {
        _ = session.Judge(tick: tick);

        return (session.ReadRow(
            key: WorldStateRow.SlotKey.Value,
            row: OutRow
        ).Value ?? string.Empty);
    }

    [Fact]
    public void ANoOpRebindAtTickZeroLeavesACyclingCellTurningFromTheOrigin() {
        var document = Document();
        var rebound = new BrowserSession(definition: document);
        var untouched = new BrowserSession(definition: document);

        Assert.True(
            condition: rebound.TryRebind(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );

        // Eight steps of twenty ticks each, from an epoch the rebind left at the origin.
        Assert.Equal(
            actual: Read(
                session: rebound,
                tick: 160UL
            ),
            expected: "8"
        );
        Assert.Equal(
            actual: Read(
                session: untouched,
                tick: 160UL
            ),
            expected: "8"
        );
    }
    /// <summary>The arm the session's own time decides: a rebind part-way through re-births the clock where the
    /// session stands, so the cell turns from the rebind rather than from the origin.</summary>
    [Fact]
    public void ARebindMidSessionRebirthsTheClockAtTheSessionsOwnTick() {
        var document = Document();
        var session = new BrowserSession(definition: document);

        Assert.Equal(
            actual: Read(
                session: session,
                tick: 100UL
            ),
            expected: "5"
        );
        Assert.True(
            condition: session.TryRebind(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: Read(
                session: session,
                tick: 160UL
            ),
            expected: "3"
        );
    }
}
