using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldTransferTurnRecoveryLawTests {
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void AnAbortedTransferRestoresTheDepartureTurnAfterCheckpointOrSlotReuse(bool mapped, bool reuseSlot) {
        var machineId = Guid.NewGuid();
        using var stateRoot = new TemporaryDirectory(prefix: "puck-turn-recovery-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: stateRoot.RootPath)
        );
        var door = PortalFixtures.BuildDoorCreation();
        var document = Fixtures.BuildDocument() with {
            CreationsRaw = [door],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "arrival",
                PrototypeId: door.Id,
                Position: new Vector3(x: 10f, y: 0f, z: 0f),
                YawDegrees: 270f,
                Scale: 1f,
                FaceSources: [new WorldPlacementFace(Face: "door", Source: new WorldScreenSource.None())]
            )],
        };
        using var rowA = HostRow.Build(name: "row-a");
        using var rowB = HostRow.Build(definition: document, name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);
        host.SetPeerCallFault(fault: new FaultingPeerCall(destination: rowB.Server), instanceName: "row-b");
        for (var slot = 0; (slot < 2); slot++) {
            Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
        var departureTurn = FixedQ4816.FromDouble(value: 2.25);

        rowA.Server.Population.SetTravelTurn(slot: 0, travelTurn: departureTurn);
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

        var transferId = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0,
            arrival: (mapped ? WorldPortalArrival.Mapped : WorldPortalArrival.Spawn),
            counterpart: (mapped ? "arrival/door" : null),
            sourceFrame: WindowCrossingFixtures.Frame(face: WindowCrossingFixtures.Face(
                origin: Vector3.Zero,
                yawDegrees: 0f
            ))
        );

        host.DrainPendingTransfers();
        Assert.False(condition: rowA.Server.Population.IsActive(index: 0));
        var pending = Assert.Single(collection: host.CaptureRow(row: rowA.Instance).InDoubtTransfers);
        var commit = Assert.Single(collection: pending.CommitMembers);

        Assert.Equal(expected: mapped, actual: commit.HasMappedArrival);
        if (mapped) {
            Assert.NotEqual(expected: departureTurn, actual: commit.TravelTurn);
        }

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);

        Assert.DoesNotContain(collection: checkpointA.Population.Entries, filter: entry => (entry.Index == 0));

        var (restoredHost, restoredA, restoredB, restoredRoot) = HostRoundtripFixture.RestoreBoth(
            machineId: machineId,
            checkpointA: HostRoundtripFixture.EncodeDecode(checkpoint: checkpointA),
            checkpointB: HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB)
        );
        using var disposeRestoredHost = restoredHost;
        using var disposeRestoredRoot = restoredRoot;
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        void AbortAndCheck(WorldInstanceHost owner, HostRow source, HostRow destination) {
            if (reuseSlot) {
                Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
                    IdentityName: null,
                    Principal: Principal.Seat(slot: 0),
                    Slot: 0,
                    WireProtocolKey: WorldProtocol.WireProtocolKey
                )).Accepted);
                Assert.True(condition: source.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0));
            }

            destination.Server.AbortTransfer(sourceAuthority: source.Server.AuthorityIdentity, transferId: transferId);
            owner.DrainPendingTransfers();

            Assert.Empty(collection: owner.CaptureRow(row: source.Instance).InDoubtTransfers);
            Assert.True(condition: source.Server.Population.IsActive(index: 0));
            Assert.False(condition: destination.Server.Population.IsActive(index: 0));
            Assert.Equal(expected: departureTurn, actual: source.Server.Population.TravelTurn(index: 0));

            // An observer that already followed this occupant takes no turn for the aborted crossing.
            var turns = new WorldRoutedSeatTurns(seatCount: 1);

            turns.Hold(slot: 0, travelTurn: departureTurn);
            Assert.Equal(expected: FixedQ4816.Zero, actual: turns.Follow(
                slot: 0,
                travelTurn: source.Server.ExecuteAuthorityOperation(operation: () => WorldLocalForwardedAuthority.DescribeRoute(
                    bodyIndex: 0,
                    endpoint: source.Instance.Name,
                    server: source.Server
                )).TravelTurn
            ));
        }

        AbortAndCheck(destination: rowB, owner: host, source: rowA);
        AbortAndCheck(destination: restoredB, owner: restoredHost, source: restoredA);
    }
}
