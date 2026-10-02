using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    // THE LAW: a replayed transferred occupant keeps its catalog rig. The reservation names the rig the traveler wore
    // at its source; the PeerAdmitted event the tape records (ServerEventTap) carries it, and a fresh server that applies
    // the event as the replay driver does wears the same rig as the live destination.
    [Fact]
    public void AReplayedTransferredOccupantKeepsItsCatalogRig() {
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 2);
        using var live = Fixtures.FreshServer(definition: document);
        var recorded = new List<WorldServerEvent.PeerAdmitted>();

        live.Server.ServerEventTap = serverEvent => {
            if (serverEvent is WorldServerEvent.PeerAdmitted admitted) {
                recorded.Add(item: admitted);
            }
        };

        var request = Reservation(
            border: "seam",
            sourceAuthority: "machine-a/boot",
            transferId: 71
        ) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: 4,
                Identity: null,
                Source: IntentSource.Idle,
                BodyColor: Vector3.One,
                CatalogRig: 73,
                Mobility: Mobility(index: 4)
            )],
        };
        var reservation = live.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.True(
            condition: live.Server.CommitTransfer(
                members: [Turned(travelTurn: FixedQ4816.Zero) with { HasMappedArrival = false }],
                reason: out var reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            userMessage: reason
        );

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var admitted = Assert.Single(collection: recorded);

        Assert.Equal(
            actual: live.Server.Population.CatalogRig(index: bodyIndex),
            expected: ((byte)73)
        );
        Assert.Equal(
            actual: Assert.Single(collection: admitted.Entries).CatalogRig,
            expected: ((byte)73)
        );

        using var replay = Fixtures.FreshServer(definition: document);

        replay.Server.ApplyServerEvent(serverEvent: admitted);
        Assert.Equal(
            actual: replay.Server.Population.CatalogRig(index: bodyIndex),
            expected: ((byte)73)
        );
    }
}
