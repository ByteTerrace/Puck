using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A destination whose store loses its answer to the arrival record cannot say whether the traveler arrived.
/// It answers <see cref="WorldTransferStatus.Uncertain"/>: it embodies none of the cohort, refuses every later step for
/// the transfer and its traveler, and records nothing more. The source keeps its doubt, neither completing nor
/// returning the cohort, until the destination recovers from its crossing log; the log then decides. Every crash point
/// between the source's departure record, the arrival record, the destination's recovery and the source's settlement
/// ends with exactly one traveler, on the destination exactly when the record landed. The red legs answer the lost
/// answer as a refusal: the source settles home while the record may be durable, and a recovered destination lands the
/// traveler a second time.</summary>
public sealed class CrossingUncertaintyLawTests {
    // Steps long enough for any destination lease taken during the crossing to expire.
    private const int PastEveryLease = 90;

    public enum Recovery {
        // The destination's activation is replaced inside the same host while the source lives on.
        InPlace,
        // The destination restarts from its durable image; the source carries its live state across.
        DestinationRestarts,
        // Both authorities restart from their durable images.
        BothRestart,
    }

    private static CrossingWorld Recover(CrossingWorld world, Recovery recovery) {
        switch (recovery) {
            case Recovery.InPlace:
                world.RecoverDestination();
                return world;
            case Recovery.DestinationRestarts:
                return world.Restart(
                    destinationDied: true,
                    sourceDied: false
                );
            default:
                return world.Restart(
                    destinationDied: true,
                    sourceDied: true
                );
        }
    }
    private static void AssertConverged(CrossingWorld world, bool arrived) {
        world.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 1,
            actual: world.CountTraveler()
        );
        Assert.Equal(
            expected: arrived,
            actual: world.TravelerAt(row: world.Destination)
        );
        Assert.Empty(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);
    }
    private static CrossingWorld CrossUncertain(bool lands, out ulong transferId) {
        var world = CrossingWorld.Build();

        world.DestinationLog.LoseAnswerOn = typeof(WorldCrossingRecord.Arrival);
        world.DestinationLog.LostAnswerLands = lands;
        transferId = world.Cross();
        return world;
    }
    private static WorldTransferReservationRequest Reservation(string sourceAuthority, ulong transferId, WorldEntityAddress incarnation) => new(
        TransferId: transferId,
        SourceAuthority: sourceAuthority,
        SourceRateHz: WorldDefinition.UnauthoredSimulationRateHz,
        SourceTick: 0,
        DeadlineSourceTick: 4,
        Border: "east",
        BorderCapacity: null,
        PartyAllOrNothing: true,
        PeerAdmission: false,
        Members: [new WorldTransferReservationMember(
            Principal: Principal.Console,
            PreferredSlot: 3,
            Identity: null,
            Source: default,
            BodyColor: default,
            CatalogRig: 0,
            Mobility: new WorldMobilityIdentity(
                DepartedFrom: incarnation,
                Epoch: 0,
                Incarnation: incarnation
            )
        )]
    );

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AnUncertainArrivalSuspendsTheTraveler_AndTheSourceKeepsItsDoubt(bool lands) {
        using var world = CrossUncertain(
            lands: lands,
            transferId: out var transferId
        );
        var destination = world.Destination.Server;
        var sourceAuthority = world.Source.Server.AuthorityIdentity;

        // Nobody embodies the traveler, and the source has recorded its departure and nothing after it.
        Assert.Equal(
            expected: 0,
            actual: world.CountTraveler()
        );
        Assert.Equal(
            expected: WorldTransferStatus.Uncertain,
            actual: destination.TransferStatus(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            )
        );
        Assert.Equal(
            expected: 0UL,
            actual: destination.UncertainCrossing
        );
        world.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 0,
            actual: world.CountTraveler()
        );
        Assert.Single(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);
        Assert.Equal(
            expected: 1,
            actual: world.SourceLog.Count
        );

        // A retried commit lands nothing, and no reservation for the traveler is taken.
        Assert.Equal(
            expected: WorldTransferStatus.Uncertain,
            actual: destination.CommitTransfer(
                members: [],
                reason: out _,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            )
        );
        var traveler = Reservation(
            incarnation: world.Traveler,
            sourceAuthority: sourceAuthority,
            transferId: (transferId + 1UL)
        );
        var refused = destination.ReserveTransfer(request: traveler);

        Assert.False(condition: refused.Accepted);
        Assert.Contains(
            actualString: refused.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "suspended"
        );

        // The control: another traveler reserves, but its arrival is not recorded behind the uncertain one.
        var other = Reservation(
            incarnation: new WorldEntityAddress(
                Authority: "machine-a/boot",
                Generation: 1,
                Index: 0
            ),
            sourceAuthority: "machine-a/boot",
            transferId: 7
        );
        var reserved = destination.ReserveTransfer(request: other);

        Assert.True(
            condition: reserved.Accepted,
            userMessage: reserved.Reason
        );
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: destination.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: false,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default
                )],
                reason: out var latched,
                sourceAuthority: other.SourceAuthority,
                transferId: other.TransferId
            )
        );
        Assert.Contains(
            actualString: latched,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is uncertain"
        );
        Assert.Equal(
            expected: (lands ? 1 : 0),
            actual: world.DestinationLog.Count
        );
    }
    [InlineData(true, Recovery.InPlace)]
    [InlineData(false, Recovery.InPlace)]
    [InlineData(true, Recovery.DestinationRestarts)]
    [InlineData(false, Recovery.DestinationRestarts)]
    [InlineData(true, Recovery.BothRestart)]
    [InlineData(false, Recovery.BothRestart)]
    [Theory]
    public void TheDestinationsRecoverySettlesAnUncertainArrival_WithExactlyOneTraveler(bool lands, Recovery recovery) {
        using var world = CrossUncertain(
            lands: lands,
            transferId: out _
        );
        var recovered = Recover(
            recovery: recovery,
            world: world
        );

        try {
            AssertConverged(
                arrived: lands,
                world: recovered
            );
        } finally {
            if (!ReferenceEquals(
                objA: recovered,
                objB: world
            )) {
                recovered.Dispose();
            }
        }
    }
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [Theory]
    public void TheSourceDiesSettlingAnUncertainArrivalTheDestinationResolved_WithExactlyOneTraveler(bool lands, bool settlementDurable) {
        using var world = CrossUncertain(
            lands: lands,
            transferId: out _
        );

        world.RecoverDestination();
        if (settlementDurable) {
            world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Settlement);
        } else {
            world.SourceLog.CrashBefore = typeof(WorldCrossingRecord.Settlement);
        }
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Step(ticks: 1));

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        AssertConverged(
            arrived: lands,
            world: restarted
        );
    }
    [InlineData(WorldTransferStatus.Committed)]
    [InlineData(WorldTransferStatus.Uncertain)]
    [InlineData(WorldTransferStatus.Missing)]
    [Theory]
    public void TheFederationCommitReplyCarriesTheVerdict(WorldTransferStatus verdict) {
        Assert.True(
            condition: WorldFederationCodec.TryDecodeCommitReply(
                body: WorldFederationCodec.EncodeCommitReply(
                    reason: "detail",
                    status: verdict
                ),
                failure: out var failure,
                reason: out var reason,
                status: out var decoded
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: decoded,
            expected: verdict
        );
        Assert.Equal(
            actual: reason,
            expected: "detail"
        );
    }
    [InlineData(((byte)WorldTransferStatus.Reserved))]
    [InlineData(((byte)9))]
    [Theory]
    public void AFederationCommitReplyNamingNoCommitVerdictIsRefused(byte verdict) {
        var body = WorldFederationCodec.EncodeCommitReply(
            reason: string.Empty,
            status: WorldTransferStatus.Missing
        );

        body[0] = verdict;
        Assert.False(condition: WorldFederationCodec.TryDecodeCommitReply(
            body: body,
            failure: out _,
            reason: out _,
            status: out _
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => WorldFederationCodec.EncodeCommitReply(
            reason: string.Empty,
            status: ((WorldTransferStatus)verdict)
        ));
    }
    [InlineData(Recovery.InPlace)]
    [InlineData(Recovery.BothRestart)]
    [Theory]
    public void ARefusalForADurableArrival_DuplicatesTheTravelerOnceTheDestinationRecovers(Recovery recovery) {
        using var world = CrossingWorld.Build();

        // The red leg: the lost answer is reported as a refusal, so the source settles home and restores the traveler
        // while the arrival record is already durable.
        world.DestinationLog.LoseAnswerOn = typeof(WorldCrossingRecord.Arrival);
        world.DestinationLog.LostAnswerLands = true;
        world.DestinationLog.LostAnswer = WorldCrossingDurability.Refused;
        _ = world.Cross();
        Assert.True(condition: world.TravelerAt(row: world.Source));

        var recovered = Recover(
            recovery: recovery,
            world: world
        );

        try {
            recovered.Step(ticks: PastEveryLease);
            Assert.Equal(
                expected: 2,
                actual: recovered.CountTraveler()
            );
        } finally {
            if (!ReferenceEquals(
                objA: recovered,
                objB: world
            )) {
                recovered.Dispose();
            }
        }
    }
}
