using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: the capture scope folds a follower's clock. A dynamics cell's live value is its stored target,
/// so two worlds that agree on every value and differ only in a follower's sampled position and velocity are
/// different simulation states and carry different capture digests.</summary>
public sealed class WorldCaptureHashClockLawTests {
    private const string EasedRow = "gauge";

    private static readonly DynamicsRow Slow = new(
        Damping: 1f,
        Frequency: 0.25f,
        Name: "slow",
        Response: 0f
    );

    private static WorldDefinition Document() => (Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(
        Name: CellName.Parse(candidate: EasedRow),
        Kind: CellKind.Int,
        Max: 1000L,
        Min: 0L,
        Dynamics: new StateDynamics(Row: Slow.Name),
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: CellValue.Int(value: 0L)
        )]
    )]) with {
        DynamicsRaw = [.. Fixtures.StandardDynamics, Slow],
    });
    private static long Live(WorldFixture fixture) {
        var catalog = fixture.Server.Definition.StateCatalog;

        Assert.True(condition: catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: EasedRow
        ));

        var time = fixture.Server.Time;

        Assert.True(condition: fixture.Server.Arena.TryReadLiveNumber(
            key: catalog.Keys.Intern(name: WorldStateRow.SlotKey),
            rowOrdinal: handle.Ordinal,
            time: in time,
            value: out var value
        ));

        return value;
    }
    // A set to a new target and a set straight back leave the stored target where it started and the follower's
    // clock kicked, which is the one difference the two worlds are allowed to have.
    private static void KickAndRestore(WorldFixture fixture) {
        foreach (var value in new[] { 500L, 0L }) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.Console,
                Row: EasedRow,
                Key: WorldStateRow.SlotKey.Value,
                Value: value,
                Kind: WorldDocumentWriteKind.Set
            ));
            fixture.Step();
        }
    }

    [Fact]
    public void TwoWorldsDifferingOnlyInAFollowersClockCarryDifferentCaptureHashes() {
        var document = Document();

        using var kicked = Fixtures.FreshServer(definition: document);
        using var rested = Fixtures.FreshServer(definition: document);

        KickAndRestore(fixture: kicked);
        rested.Step();
        rested.Step();

        // Every read of the row answers the same on both worlds: a dynamics cell reads its stored target.
        Assert.Equal(
            actual: Live(fixture: kicked),
            expected: Live(fixture: rested)
        );

        var tick = (kicked.Server.NextInputTick - 1UL);

        Assert.Equal(
            actual: (rested.Server.NextInputTick - 1UL),
            expected: tick
        );

        // The control: the authoritative store's own scope already covers the clock columns, so the two worlds are
        // genuinely in different states.
        Assert.NotEqual(
            actual: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: rested.Server,
                tick: tick
            ),
            expected: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: kicked.Server,
                tick: tick
            )
        );
        Assert.NotEqual(
            actual: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Capture,
                server: rested.Server,
                tick: tick
            ),
            expected: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Capture,
                server: kicked.Server,
                tick: tick
            )
        );
    }
}
