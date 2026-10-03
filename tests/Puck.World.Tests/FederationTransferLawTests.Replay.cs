using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    private static WorldTransferReservationRequest PeerCohort(ulong transferId, params int[] slots) => (Reservation(
        border: "door",
        sourceAuthority: "machine-a/garden",
        transferId: transferId
    ) with {
        PeerAdmission = true,
        Members = [.. slots.Select(selector: static slot => new WorldTransferReservationMember(
            Principal: Principal.Console,
            PreferredSlot: slot,
            Identity: null,
            Source: IntentSource.Idle,
            BodyColor: Vector3.One,
            CatalogRig: 73,
            Mobility: Mobility(index: slot)
        ))],
    });

    // THE LAW: a transferred peer whose commit rolls back is neither kept nor stranded at its destination. The first
    // traveler of a two-entity cohort is admitted (its PeerAdmitted event applies and mints its grants) and landed,
    // the second's admission is refused inside the landing loop, and the escrow rolls the first back. Once the ordered
    // queue has drained, its index is empty, its admission grants are gone, the census is what it was, and the same
    // traveler, at the same mobility epoch, reserves and commits again.
    [Fact]
    public void ARolledBackPeerLeavesItsDestinationAndCanArriveAgain() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2),
            landingRefusal: static ordinal => ((ordinal == 1) ? "forced landing refusal at member 1" : null));
        var census = fixture.Server.Population.ActiveCount();
        var request = PeerCohort(91, 4, 5);
        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );

        Assert.Equal(
            actual: fixture.Server.CommitTransfer(
                members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)],
                reason: out var reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            expected: WorldTransferStatus.Missing
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "forced landing refusal at member 1"
        );

        // The first traveler landed once, so its index's generation advanced and outlives the rollback.
        var first = reservation.BodyIndices[0];

        Assert.Equal(
            actual: fixture.Server.Population.Generation(index: first),
            expected: 1
        );

        fixture.Step();
        fixture.Step();

        Assert.False(condition: fixture.Server.Population.IsActive(index: first));
        Assert.Empty(collection: fixture.Server.GrantRows(principal: Principal.Peer(
            generation: 1,
            index: first
        )));
        Assert.Equal(
            actual: fixture.Server.Population.ActiveCount(),
            expected: census
        );

        var retry = PeerCohort(92, first);
        var retried = fixture.Server.ReserveTransfer(request: retry);

        Assert.True(
            condition: retried.Accepted,
            userMessage: retried.Reason
        );
        Assert.True(
            condition: (fixture.Server.CommitTransfer(
                members: [Turned(travelTurn: FixedQ4816.One)],
                reason: out var retryReason,
                sourceAuthority: retry.SourceAuthority,
                transferId: retry.TransferId
            ) == WorldTransferStatus.Committed),
            userMessage: retryReason
        );
        Assert.True(condition: fixture.Server.Population.IsActive(index: Assert.Single(collection: retried.BodyIndices)));
    }
    // THE LAW: a commit that rolls back replays as it ran, for a cohort of local seats or of transferred entities. The
    // commit lands its first traveler and is refused at its second: a seat cohort's second principal loses its grants
    // on the tape, and an entity cohort's second admission is refused inside the landing loop (the escrow's landing
    // refusal policy, which no production composition passes). The escrow rolls the first back. The landing outlives
    // itself in its index's advanced generation, so the recording holds the commit's one arrival entry with an outcome
    // of one landing rolled back, and no admission entry beside it; the first index is empty again, and the re-drive
    // relands it through the shadow's own escrow, stops at the recorded traveler and rolls back through the same undo,
    // verifying tick for tick. The red leg is the tape without arrival entries: the re-drive diverges on the commit's
    // tick.
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [Theory]
    public void ARolledBackCommitReplaysAsItRan(bool peer, bool taped) {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-rollback-");
        using var fixture = (peer
            ? Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2),
                landingRefusal: static ordinal => ((ordinal == 1) ? "forced landing refusal at member 1" : null))
            : Fixtures.FreshServer());
        var request = (peer
            ? PeerCohort(91, 4, 5)
            : Reservation(
                border: "door",
                sourceAuthority: "machine-a/garden",
                transferId: 91
            ));

        if (!peer) {
            request = request with {
                Members = [request.Members[0], (request.Members[0] with {
                    Mobility = Mobility(index: 1),
                    PreferredSlot = 1,
                    Principal = Principal.Seat(slot: 1),
                })],
            };
        }

        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
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

        var commitTick = 0;

        if (!peer) {
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
            commitTick = 1;
        }

        Assert.Equal(
            actual: fixture.Server.CommitTransfer(
                members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)],
                reason: out var reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            expected: WorldTransferStatus.Missing
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "refused reserved commit"
        );


        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        Assert.False(condition: fixture.Server.Population.IsActive(index: reservation.BodyIndices[0]));

        _ = tape.StopRecording();

        var recorded = ReadArrivalTape(
            name: name,
            tape: tape
        );
        var entries = recorded.Ticks.SelectMany(selector: static tick => tick.Authority).ToArray();
        var arrivals = entries.OfType<WorldReplayEntry.Arrival>().ToArray();

        Assert.Equal(
            actual: arrivals.Length,
            expected: (taped ? 1 : 0)
        );
        Assert.DoesNotContain(
            collection: entries,
            filter: static entry => (entry.GetType().Name == "PeerAdmitted")
        );

        if (taped) {
            Assert.True(condition: arrivals[0].Outcome.RolledBack);
            Assert.Single(collection: arrivals[0].Outcome.Generations);
        }

        Assert.Equal(
            actual: tape.Verify(name: name).Primary.DivergedAt,
            expected: (taped ? -1 : commitTick)
        );
    }
    // THE LAW: a recording that spans an arrival into the recorded world verifies tick for tick, whatever arrives: a
    // local seat joining at its reserved seat, a transferred entity admitted at its reserved peer index, or a live peer
    // admitted there by the arrival verdict the destination's admission door decided at reservation (its minted grants
    // included), mapped onto its counterpart's pose or set down at its spawn. The live escrow lands the arrival and
    // reports it once the commit stands (WorldServer.ArrivalTap); the tape records it as one arrival entry, the
    // admission included, at the commit's position; the re-drive relands it through the shadow's own escrow, the same
    // landing, pose, motion and accumulated turn included. The red leg is a tape with no arrival: with the tap
    // detached a mapped arrival's re-drive diverges at tick 0, the tick the traveler arrived on, for a seat or a peer
    // alike.
    [InlineData("seat", false, true)]
    [InlineData("seat", true, true)]
    [InlineData("seat", true, false)]
    [InlineData("entity", false, true)]
    [InlineData("entity", true, true)]
    [InlineData("entity", true, false)]
    [InlineData("peer", false, true)]
    [InlineData("peer", true, true)]
    [InlineData("peer", true, false)]
    [Theory]
    public void ARecordingAcrossAnArrivalVerifiesTickForTick(string kind, bool mapped, bool taped) {
        var peer = (kind != "seat");
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
                    Source: ((kind == "peer")
                    ? IntentSource.Live
                    : IntentSource.Idle),
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
            condition: (fixture.Server.CommitTransfer(
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
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        _ = tape.StopRecording();

        var recorded = ReadArrivalTape(
            name: name,
            tape: tape
        );

        Assert.Equal(
            actual: recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>().Count(),
            expected: (taped ? 1 : 0)
        );
        Assert.DoesNotContain(
            collection: recorded.Ticks[0].Authority,
            filter: static entry => (entry.GetType().Name == "PeerAdmitted")
        );
        Assert.Equal(
            actual: tape.Verify(name: name).Primary.DivergedAt,
            expected: (taped ? -1 : 0)
        );

        if (!taped) {
            return;
        }

        // The relanded occupant is the live one: a fresh session that relands the recording's arrival holds it at the
        // same index with the same pose, catalog rig, accumulated turn and grants.
        var slot = Assert.Single(collection: reservation.BodyIndices);
        var live = (
            Position: fixture.Server.Population.EntryBody(index: slot)!.FixedPosition,
            Rig: fixture.Server.Population.CatalogRig(index: slot),
            Turn: fixture.Server.Population.TravelTurn(index: slot)
        );
        var occupant = (peer
            ? Principal.Peer(
                generation: fixture.Server.Population.Generation(index: slot),
                index: slot
            )
            : Principal.Seat(slot: slot));
        var liveGrants = fixture.Server.GrantRows(principal: occupant).Select(selector: static grant => $"{grant.Capability} {grant.Subject}").Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Equal(
            actual: (kind == "peer"),
            expected: fixture.Server.Population.IsAdmittedPeer(bodyIndex: slot)
        );

        if (kind == "peer") {
            Assert.NotEmpty(collection: liveGrants);
        }

        using var isolated = (peer
            ? Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2))
            : Fixtures.FreshServer());

        RelandRecordedArrivals(
            fixture: isolated,
            recorded: recorded
        );

        Assert.True(condition: isolated.Server.Population.IsActive(index: slot));
        Assert.Equal(expected: fixture.Server.Population.SimulatedCount, actual: isolated.Server.Population.SimulatedCount);
        Assert.Equal(
            actual: (
                Position: isolated.Server.Population.EntryBody(index: slot)!.FixedPosition,
                Rig: isolated.Server.Population.CatalogRig(index: slot),
                Turn: isolated.Server.Population.TravelTurn(index: slot)
            ),
            expected: live
        );
        Assert.Equal(
            actual: isolated.Server.GrantRows(principal: occupant).Select(selector: static grant => $"{grant.Capability} {grant.Subject}").Order(comparer: StringComparer.Ordinal).ToArray(),
            expected: liveGrants
        );
        Assert.Equal(
            actual: live.Turn,
            expected: FixedQ4816.FromDouble(value: 2.25)
        );
    }
}
