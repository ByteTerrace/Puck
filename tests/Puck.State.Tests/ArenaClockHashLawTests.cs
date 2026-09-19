using Xunit;

namespace Puck.State.Tests;

/// <summary>Proves <see cref="StateArena.ComputeHash"/> folds a cell's clock columns, so a follower's sampled position
/// and velocity are simulation state the authoritative hash covers even though no arena read eases.</summary>
public sealed class ArenaClockHashLawTests {
    [Fact]
    public void AuthoritativeHashFoldsFollowerClockState() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "eased"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "ease"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arenaA = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var arenaB = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        var slot = ArenaFixture.SlotKey(catalog: catalog);

        // Before kicking, both are identical.
        Assert.Equal(
            actual: arenaB.ComputeHash(),
            expected: arenaA.ComputeHash()
        );

        // Mutating only the follower's clock (y0, v0) on arenaB moves the authoritative hash.
        Assert.True(condition: arenaB.TryWriteClock(
            epochEngineTick: 0L,
            epochTick: 0L,
            key: slot,
            reason: out var reason,
            rowOrdinal: 0,
            substepTicks: 0L,
            v0: 100L,
            y0: 50L
        ), userMessage: reason);

        Assert.NotEqual(
            actual: arenaB.ComputeHash(),
            expected: arenaA.ComputeHash()
        );
    }
}
