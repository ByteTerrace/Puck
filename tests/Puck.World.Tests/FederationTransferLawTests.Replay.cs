using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    // THE LAW: a commit that rolls back replays as it ran. A two-seat cohort reserves seats 0 and 1, and seat 1's
    // principal loses its grants on the tape. The commit lands the first traveler, the second's join is refused, and the
    // escrow rolls the first back (WorldTransferEscrow.RollBackArrival). The landing outlives itself in its seat's
    // advanced generation, so the recording holds that one arrival with its rollback; seat 0 is empty again, and the
    // re-drive lands and rolls it back through the same escrow doors, verifying tick for tick. The red leg is today's
    // tape, which records no arrival: with the tap detached the re-drive diverges at tick 1, the commit's tick.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ARolledBackCommitReplaysAsItRan(bool taped) {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-rollback-");
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(
            border: "door",
            sourceAuthority: "machine-a/garden",
            transferId: 91
        );

        request = request with {
            Members = [request.Members[0], (request.Members[0] with {
                Mobility = Mobility(index: 1),
                PreferredSlot = 1,
                Principal = Principal.Seat(slot: 1),
            })],
        };

        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.Equal(
            actual: reservation.BodyIndices,
            expected: [0, 1]
        );


        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            transport: transport
        );
        var name = $"rollback-{Guid.NewGuid():N}";

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

        // Seat 1's principal loses its grants on the tape after the reservation, so its join is refused inside the
        // commit.
        foreach (var grant in fixture.Server.GrantRows(principal: Principal.Seat(slot: 1))) {
            _ = transport.SubmitEnvelope(
                payload: new WorldSubmissionPayload.Revoke(Value: grant),
                principal: Principal.Console
            );
        }

        fixture.Step();
        tape.NoteTick();
        Assert.False(condition: fixture.Server.CommitTransfer(
            members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)],
            reason: out var reason,
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "body:1 refused reserved commit"
        );

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        Assert.False(condition: fixture.Server.Population.IsActive(index: 0));

        _ = tape.StopRecording();

        using (var stream = File.OpenRead(path: tape.PathFor(name: name))) {
            Assert.Equal(
                actual: WorldReplaySnapshot.Read(stream: stream).Ticks.SelectMany(selector: static tick => tick.Authority).Count(predicate: static entry => (entry.GetType().Name == "Arrival")),
                expected: (taped ? 1 : 0)
            );
        }

        Assert.Equal(
            actual: tape.Verify(name: name).DivergedAt,
            expected: (taped ? -1 : 1)
        );
    }
    // THE LAW: a recording that spans an arrival into the recorded world verifies tick for tick, whatever arrives: a
    // local seat joining at its reserved seat, or a transferred entity admitted at its reserved peer index, mapped onto
    // its counterpart's pose or set down at its spawn. The live escrow lands the arrival and reports it once the commit
    // stands (WorldServer.ArrivalTap); the tape records it as an arrival entry, behind a peer's own PeerAdmitted entry;
    // the re-drive lands it through the same escrow landing at the same pre-step position, pose, motion and accumulated
    // turn included. The red leg is today's tape, which records no arrival: with the tap detached a mapped arrival's
    // re-drive diverges at tick 0, the tick the traveler arrived on, for a seat or a peer alike.
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [Theory]
    public void ARecordingAcrossAnArrivalVerifiesTickForTick(bool peer, bool mapped, bool taped) {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-arrival-");
        using var fixture = (peer
            ? Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2))
            : Fixtures.FreshServer());
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );
        var name = $"arrival-{Guid.NewGuid():N}";

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

        if (peer) {
            request = request with {
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
        }

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

        if (!taped) {
            return;
        }

        // The re-driven occupant is the live one: an isolated session driven from the recording's boot image holds it at
        // the same index with the same pose, catalog rig and accumulated turn.
        var slot = Assert.Single(collection: reservation.BodyIndices);
        var live = (
            Position: fixture.Server.Population.EntryBody(index: slot)!.FixedPosition,
            Rig: fixture.Server.Population.CatalogRig(index: slot),
            Turn: fixture.Server.Population.TravelTurn(index: slot)
        );

        using var isolated = (peer
            ? Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2))
            : Fixtures.FreshServer());
        var drive = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: isolated.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: isolated.Server.Profiles,
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            transport: new LoopbackTransport(server: isolated.Server)
        );

        Assert.True(
            condition: drive.TryBeginDrive(
                documentPath: null,
                forkName: null,
                name: name,
                refusal: out var driveRefusal,
                toTick: null
            ),
            userMessage: $"refused to drive: {driveRefusal}"
        );

        while (drive.Mode == WorldReplayMode.Replaying) {
            drive.InjectDriveTick();
            isolated.Step();
            drive.NoteTick();
        }

        Assert.True(condition: isolated.Server.Population.IsActive(index: slot));
        Assert.Equal(
            actual: (
                Position: isolated.Server.Population.EntryBody(index: slot)!.FixedPosition,
                Rig: isolated.Server.Population.CatalogRig(index: slot),
                Turn: isolated.Server.Population.TravelTurn(index: slot)
            ),
            expected: live
        );
        Assert.Equal(
            actual: live.Turn,
            expected: FixedQ4816.FromDouble(value: 2.25)
        );
    }
}
