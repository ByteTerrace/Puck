using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

// body.pose spawn:<id> carrying a seat home (WorldInstanceHost.TryEnqueuePoseHome): the traveller is named by its
// mobility incarnation, so transfers drained ahead of the pose can move it or refill its old slot without the pose
// sending another body home.
public sealed class PoseHomeTransferLawTests {
    private static readonly FixedSpawnPoint Home = new(
        Position: new FixedVector3(
            X: FixedQ4816.FromDouble(value: 3.5),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: -2.25)
        ),
        YawRadians: FixedQ4816.FromDegrees(degrees: 90.0)
    );

    private static WorldInstanceHost Host(TemporaryDirectory stateRoot) => new(
        applicationStopping: CancellationToken.None,
        admitsSpawn: true,
        machineHostFactory: Fixtures.MachineHostFactory,
        machineId: Guid.NewGuid(),
        resolver: new WorldSessionResolver(),
        seats: WorldEmbodiedSeats.None,
        stateRoot: new WorldStateRoot(path: stateRoot.RootPath)
    );
    private static void Join(HostRow row, int slot) => Assert.True(condition: row.Server.ApplySession(request: new SessionRequest.Join(
        IdentityName: null,
        Principal: Principal.Seat(slot: slot),
        Slot: slot,
        WireProtocolKey: WorldProtocol.WireProtocolKey
    )).Accepted);
    private static WorldEntityAddress Address(HostRow row, int slot) => new(
        Authority: row.Server.AuthorityIdentity,
        Index: slot,
        Generation: row.Server.Population.Generation(index: slot)
    );
    private static WorldEntityAddress? Incarnation(HostRow row, int slot) => row.Server.Population.ResolveIncarnation(
        authority: row.Server.AuthorityIdentity,
        index: slot
    );

    // Queued ahead of the pose: the traveller crosses A -> B, and C's body crosses into the A slot it left.
    [Fact]
    public void APoseQueuedBehindTransfersCarriesItsTravellerHomeAndLeavesItsOldSlotsNewOccupant() {
        using var stateRoot = new TemporaryDirectory(prefix: "puck-pose-home-");
        using var host = Host(stateRoot: stateRoot);
        using var boot = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        using var rowA = HostRow.Build(name: "row-a");
        using var rowB = HostRow.Build(name: "row-b");
        using var rowC = HostRow.Build(name: "row-c");
        var narration = new RecordingNarrationSink();

        host.Admit(row: boot.Instance);
        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);
        host.Admit(row: rowC.Instance);
        using var lease = host.AttachNarrationSink(sink: narration);
        Join(row: rowA, slot: 0);
        // A second occupant keeps row-a from reaping once the traveller leaves it.
        Join(row: rowA, slot: 1);
        Join(row: rowC, slot: 0);
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

        var traveller = Incarnation(row: rowA, slot: 0);
        var other = Incarnation(row: rowC, slot: 0);

        _ = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );
        _ = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-a"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-c",
            sourceSlot: 0
        );
        Assert.True(
            condition: host.TryEnqueuePoseHome(
                actingPrincipal: Principal.Console,
                arrivalPose: Home,
                reason: out var reason,
                sourceInstance: "row-a",
                transferId: out _,
                traveller: Address(row: rowA, slot: 0)
            ),
            userMessage: reason
        );
        host.DrainPendingTransfers();

        Assert.True(
            condition: (Incarnation(row: rowA, slot: 0) == other),
            userMessage: string.Join(separator: "\n", values: narration.Narrations.Select(selector: static line => line.Text))
        );
        Assert.False(condition: rowB.Server.Population.IsActive(index: 0));
        Assert.Equal(expected: traveller, actual: Incarnation(row: boot, slot: 0));

        var body = boot.Server.Population.EntryBody(index: 0)!;

        Assert.Equal(expected: Home.Position, actual: body.FixedPosition);
        Assert.Equal(expected: Home.YawRadians, actual: body.FixedYaw);
        Assert.Contains(
            collection: narration.Narrations,
            filter: line => line.Text.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "follows its traveller"
            )
        );
    }
    // A traveller that another queued transfer already carried home is not carried again, and nothing else moves.
    [Fact]
    public void APoseWhoseTravellerIsAlreadyHomeIsRefusedByName() {
        using var stateRoot = new TemporaryDirectory(prefix: "puck-pose-home-");
        using var host = Host(stateRoot: stateRoot);
        using var boot = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        using var rowA = HostRow.Build(name: "row-a");
        var narration = new RecordingNarrationSink();

        host.Admit(row: boot.Instance);
        host.Admit(row: rowA.Instance);
        using var lease = host.AttachNarrationSink(sink: narration);
        Join(row: rowA, slot: 0);
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

        _ = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: WorldInstanceHost.BootInstanceName),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );
        Assert.True(condition: host.TryEnqueuePoseHome(
            actingPrincipal: Principal.Console,
            arrivalPose: Home,
            reason: out _,
            sourceInstance: "row-a",
            transferId: out _,
            traveller: Address(row: rowA, slot: 0)
        ));
        host.DrainPendingTransfers();

        Assert.True(condition: boot.Server.Population.IsActive(index: 0));
        Assert.NotEqual(expected: Home.Position, actual: boot.Server.Population.EntryBody(index: 0)!.FixedPosition);
        Assert.Contains(
            collection: narration.Narrations,
            filter: line => line.Text.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "is already home"
            )
        );
    }
    // A seat whose body a federated authority owns has no local instance to leave: the pose refuses by name rather
    // than minting a transfer id for an instance this host does not run, and a stale route is refused the same way.
    [Fact]
    public void APoseFromAFederatedOrStaleRouteQueuesNothing() {
        using var stateRoot = new TemporaryDirectory(prefix: "puck-pose-home-");
        using var host = Host(stateRoot: stateRoot);
        using var boot = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        using var rowA = HostRow.Build(name: "row-a");

        host.Admit(row: boot.Instance);
        host.Admit(row: rowA.Instance);
        Join(row: rowA, slot: 0);

        Assert.False(condition: host.TryEnqueuePoseHome(
            actingPrincipal: Principal.Console,
            arrivalPose: Home,
            reason: out var federated,
            sourceInstance: "$traveler/remote/0",
            transferId: out _,
            traveller: new WorldEntityAddress(Authority: "remote/world", Index: 0, Generation: 1)
        ));
        Assert.Contains(actualString: federated, comparisonType: StringComparison.Ordinal, expectedSubstring: "federated authority");
        Assert.False(condition: host.TryEnqueuePoseHome(
            actingPrincipal: Principal.Console,
            arrivalPose: Home,
            reason: out var stale,
            sourceInstance: "row-a",
            transferId: out _,
            traveller: Address(row: rowA, slot: 0) with { Generation = (Address(row: rowA, slot: 0).Generation + 1) }
        ));
        Assert.Contains(actualString: stale, comparisonType: StringComparison.Ordinal, expectedSubstring: "no longer the occupant");
        host.DrainPendingTransfers();
        Assert.True(condition: rowA.Server.Population.IsActive(index: 0));
        Assert.False(condition: boot.Server.Population.IsActive(index: 0));
    }
}
