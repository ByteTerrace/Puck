using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
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
    // THE LAW: a recording that spans a local seat arriving into the recorded world verifies tick for tick. The live
    // escrow lands the seat (WorldTransferEscrow.LandSeat) and reports it (WorldServer.ArrivalTap), the tape records it as
    // an arrival entry, and the re-drive lands it through the same LandSeat at the same pre-step position, mapped pose
    // and accumulated turn included. The red leg is today's tape, which records no arrival: with the tap detached the
    // re-drive diverges at tick 0, the tick the seat arrived on.
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [Theory]
    public void ARecordingAcrossALocalSeatsArrivalVerifiesTickForTick(bool mapped, bool taped) {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-arrival-");
        using var fixture = Fixtures.FreshServer();
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );
        var name = $"local-arrival-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );

        if (!taped) {
            fixture.Server.ArrivalTap = null;
        }

        var request = Reservation(
            border: "door",
            sourceAuthority: "machine-a/garden",
            transferId: 81
        );
        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.True(
            condition: fixture.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: mapped,
                    BodyMotionProgramName: "grounded",
                    Position: new FixedVector3(
                        X: FixedQ4816.FromInteger(value: 3),
                        Y: FixedQ4816.Zero,
                        Z: FixedQ4816.FromInteger(value: -4)
                    ),
                    YawRadians: FixedQ4816.FromDouble(value: 1.25),
                    PlanarVelocity: default,
                    VerticalVelocity: default,
                    TravelTurn: FixedQ4816.FromDouble(value: 2.25)
                )],
                reason: out var reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            userMessage: reason
        );

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        _ = tape.StopRecording();

        using (var stream = File.OpenRead(path: tape.PathFor(name: name))) {
            var arrivals = WorldReplaySnapshot.Read(stream: stream).Ticks[0].Authority.Count(predicate: static entry => (entry.GetType().Name == "Arrival"));

            Assert.Equal(
                actual: arrivals,
                expected: (taped ? 1 : 0)
            );
        }

        Assert.Equal(
            actual: tape.Verify(name: name).DivergedAt,
            expected: (taped ? -1 : 0)
        );
    }
}
