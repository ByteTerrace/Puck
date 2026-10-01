using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The handoff token — the source-scoped transfer key, fenced by the traveler's ownership epoch, the
/// destination's lease deadline, and the destination log's activation — holds across a destination restart. An exact
/// replay of a committed token is answered as committed by a destination rebuilt from its checkpoint and crossing log;
/// a different commit under the same token, a stale ownership epoch, an expired lease, and a destination whose log
/// activation was superseded are each refused, beside a control that the same call succeeds when the fence holds.</summary>
public sealed class CrossingHandoffTokenLawTests {
    private static WorldTransferReservationRequest Reservation(ulong transferId, ulong epoch) => new(
        TransferId: transferId,
        SourceAuthority: "machine-a/boot",
        SourceRateHz: WorldDefinition.UnauthoredSimulationRateHz,
        SourceTick: 0,
        DeadlineSourceTick: 4,
        Border: "east",
        BorderCapacity: null,
        PartyAllOrNothing: true,
        PeerAdmission: false,
        Members: [new WorldTransferReservationMember(
            Principal: Principal.Console,
            PreferredSlot: 0,
            Identity: null,
            Source: default,
            BodyColor: default,
            CatalogRig: 0,
            Mobility: new WorldMobilityIdentity(
                DepartedFrom: new WorldEntityAddress(
                    Authority: "machine-a/boot",
                    Generation: 1,
                    Index: 0
                ),
                Epoch: epoch,
                Incarnation: new WorldEntityAddress(
                    Authority: "machine-a/boot",
                    Generation: 1,
                    Index: 0
                )
            )
        )]
    );

    private static readonly WorldTransferCommitMember Member = new(
        Profile: null,
        HasMappedArrival: false,
        BodyMotionProgramName: "grounded",
        Position: default,
        YawRadians: default,
        PlanarVelocity: default,
        VerticalVelocity: default
    );

    [Fact]
    public void AnExactReplayOfACommittedTokenIsIdempotentAcrossADestinationRestart_AndADifferentCommitIsRefused() {
        using var world = CrossingWorld.Build();

        world.Host.SetPeerCallFault(
            fault: new DyingDestinationPeerCall(destination: world.Destination.Server) { LoseCommitAnswer = true },
            instanceName: "row-b"
        );
        _ = world.Cross();

        var arrival = Assert.IsType<WorldCrossingRecord.Arrival>(@object: Assert.Single(collection: world.DestinationLog.Read(defaults: world.Destination.Server.Definition.PlayerDefaults)).Record).Value;

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: true
        );
        var destination = restarted.Destination.Server;

        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.TransferStatus(
                sourceAuthority: arrival.Request.SourceAuthority,
                transferId: arrival.Request.TransferId
            )
        );
        Assert.True(
            condition: destination.CommitTransfer(
                members: arrival.Members,
                reason: out var replayReason,
                sourceAuthority: arrival.Request.SourceAuthority,
                transferId: arrival.Request.TransferId
            ),
            userMessage: replayReason
        );

        // The red leg: the same token carrying a different commit is refused, and lands nothing a second time.
        Assert.False(condition: destination.CommitTransfer(
            members: [arrival.Members[0] with { Position = (arrival.Members[0].Position + new Puck.Maths.FixedVector3(
                X: Puck.Maths.FixedQ4816.One,
                Y: default,
                Z: default
            )) }],
            reason: out var alteredReason,
            sourceAuthority: arrival.Request.SourceAuthority,
            transferId: arrival.Request.TransferId
        ));
        Assert.Contains(
            actualString: alteredReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "different commit"
        );
        restarted.Step(ticks: 4);
        Assert.Equal(
            expected: 1,
            actual: restarted.CountTraveler()
        );
    }
    [Fact]
    public void AStaleEpochTokenIsRefusedByADestinationRebuiltFromItsLog_AndANewerEpochIsAdmitted() {
        using var world = CrossingWorld.Build();

        _ = world.Cross();

        var arrival = Assert.IsType<WorldCrossingRecord.Arrival>(@object: Assert.Single(collection: world.DestinationLog.Read(defaults: world.Destination.Server.Definition.PlayerDefaults)).Record).Value;
        var departedEpoch = arrival.Request.Members[0].Mobility!.Value;

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );
        var destination = restarted.Destination.Server;
        var stale = arrival.Request with {
            TransferId = (arrival.Request.TransferId + 7UL),
            Members = [arrival.Request.Members[0] with { Mobility = departedEpoch }],
        };
        var staleReply = destination.ReserveTransfer(request: stale);

        Assert.False(condition: staleReply.Accepted);
        Assert.Contains(
            actualString: staleReply.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "stale"
        );

        // The control: the epoch past the one this destination consumed is admitted.
        var newer = stale with {
            TransferId = (stale.TransferId + 1UL),
            Members = [stale.Members[0] with { Mobility = departedEpoch.Advance().Advance() }],
        };
        var newerReply = destination.ReserveTransfer(request: newer);

        Assert.True(
            condition: newerReply.Accepted,
            userMessage: newerReply.Reason
        );
    }
    [Fact]
    public void AnExpiredLeaseTokenIsRefused_AndALiveOneCommits() {
        using var expired = Fixtures.FreshServer();
        var request = Reservation(
            epoch: 0,
            transferId: 31
        );

        Assert.True(condition: expired.Server.ReserveTransfer(request: request).Accepted);
        for (var tick = 0; (tick < 8); tick++) {
            expired.Step();
        }
        Assert.False(condition: expired.Server.CommitTransfer(
            members: [Member],
            reason: out var expiredReason,
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId
        ));
        Assert.Contains(
            actualString: expiredReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no live reservation"
        );
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: expired.Server.TransferStatus(
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );

        using var live = Fixtures.FreshServer();

        Assert.True(condition: live.Server.ReserveTransfer(request: request).Accepted);
        live.Step();
        Assert.True(
            condition: live.Server.CommitTransfer(
                members: [Member],
                reason: out var liveReason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            userMessage: liveReason
        );
    }
    [Fact]
    public void ADestinationWhoseLogActivationWasSupersededCannotCommit_AndTheTravelerNeverLeaves() {
        using var world = CrossingWorld.Build();

        // A newer activation of the destination's log fences the one this destination still writes through.
        _ = world.DestinationLog.Activate();
        var transferId = world.Cross();

        Assert.True(condition: world.TravelerAt(row: world.Source));
        Assert.False(condition: world.TravelerAt(row: world.Destination));
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: world.Destination.Server.TransferStatus(
                sourceAuthority: world.Source.Server.AuthorityIdentity,
                transferId: transferId
            )
        );
        Assert.Equal(
            expected: 0,
            actual: world.DestinationLog.Count
        );
        Assert.Empty(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);

        // The control: the current activation commits the same crossing.
        using var control = CrossingWorld.Build();

        _ = control.Cross();
        Assert.True(condition: control.TravelerAt(row: control.Destination));
        Assert.Equal(
            expected: 1,
            actual: control.CountTraveler()
        );
    }
}
