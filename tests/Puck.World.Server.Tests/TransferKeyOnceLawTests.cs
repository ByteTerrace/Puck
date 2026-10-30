using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A transfer key names one handoff for all time. The source allocates it durably before it reserves under
/// it and never reuses it; the destination leases it at most once, durably, and an answer of
/// <see cref="WorldTransferStatus.Missing"/> durably tombstones it, so no later reservation or commit under it is
/// admitted; and the destination keeps a committed key's outcome until its source can no longer ask. Each law drives the
/// hazard a resent reservation or commit opens when one of those holds only in memory: the federation lane resends
/// every transfer-keyed request (<c>MayResend</c>), so a request answered once can arrive again after the source has
/// acted on the answer. Every law ends with exactly one traveller.</summary>
public sealed class TransferKeyOnceLawTests {
    // Steps long enough for any lease taken during the crossing to expire and for the source to settle.
    private const int PastEveryLease = 90;

    /// <summary>The destination as the source's transport sees it, keeping the last reservation and commit it carried
    /// so a law can deliver them again as the lane would. A commit can be lost on the way (never applied) or kill the
    /// destination inside it; either way the source hears no answer.</summary>
    private sealed class ResendingPeerCall(WorldServer destination) : IWorldPeerCall {
        public WorldTransferReservationRequest? Reservation { get; private set; }
        public List<WorldTransferReservationRequest> Reservations { get; } = [];
        public IReadOnlyList<WorldTransferCommitMember>? CommitMembers { get; private set; }
        public bool LoseEveryCommit { get; set; }
        public Action? AfterReserve { get; set; }

        public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        public void Acknowledge(string sourceAuthority, ulong transferId) => destination.AcknowledgeTransfer(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            CommitMembers = [.. members];
            status = WorldTransferStatus.Missing;
            if (LoseEveryCommit) {
                reason = "the commit was lost on the way";
                return WorldTransferStep.Unreachable;
            }
            try {
                status = destination.CommitTransfer(
                    members: members,
                    reason: out reason,
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } catch (AuthorityCrashedException) {
                reason = "the destination stopped answering";
                return WorldTransferStep.Unreachable;
            }
            return WorldTransferStep.Answered;
        }
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
            Reservation = request;
            Reservations.Add(item: request);
            var reply = destination.ReserveTransfer(request: request);

            AfterReserve?.Invoke();
            return reply;
        }
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = destination.TransferStatus(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
            return true;
        }
    }

    private static WorldTransferReservationRequest Reservation(ulong transferId, ulong floor = 0UL, int traveller = 0) => new(
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
                DepartedFrom: Incarnation(index: traveller),
                Epoch: transferId,
                Incarnation: Incarnation(index: traveller)
            )
        )],
        SourceFloor: floor
    );

    private static WorldEntityAddress Incarnation(int index) => new(
        Authority: "machine-a/boot",
        Generation: 1,
        Index: index
    );
    private static readonly WorldTransferCommitMember Member = new(
        Profile: null,
        Arrival: WorldTransferArrival.Spawn,
        BodyMotionProgramName: "grounded",
        Position: default,
        YawRadians: default,
        PlanarVelocity: default,
        VerticalVelocity: default
    );

    // Delivers the transport's last reservation and commit again, as the lane resends them, steps, and asserts that
    // exactly one traveller exists and that the destination admitted neither step.
    private static void AssertResentStepsAreRefused(CrossingWorld world, ResendingPeerCall transport) {
        var destination = world.Destination.Server;
        var request = transport.Reservation!;
        var reserved = destination.ReserveTransfer(request: request);
        var committed = destination.CommitTransfer(
            members: transport.CommitMembers!,
            reason: out _,
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId
        );

        world.Step(ticks: 4);
        Assert.Equal(
            expected: 1,
            actual: world.CountTraveler()
        );
        Assert.False(
            condition: reserved.Accepted,
            userMessage: "a resent reservation leased its key a second time"
        );
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: committed
        );
    }

    // THE LAW (lease once): the destination dies inside the commit, before its arrival is durable, and restarts from a
    // checkpoint taken before the reservation. Its crossing log says the key was leased, so the recovered destination
    // answers the source's status query Missing and refuses the lane's resent reservation and commit; the source
    // restores the traveller, and nothing lands it a second time. Red at the tip: recovery found no record of the lease,
    // so the resent reservation leased the key again and the resent commit landed a second traveller.
    [Fact]
    public void ADestinationThatLostItsLeaseInACrashNeverLeasesTheKeyAgain() {
        using var world = CrossingWorld.Build();
        var transport = new ResendingPeerCall(destination: world.Destination.Server);

        world.Host.SetPeerCallFault(
            fault: transport,
            instanceName: "row-b"
        );
        world.DestinationLog.CrashBefore = typeof(WorldCrossingRecord.Arrival);
        _ = world.Cross();
        Assert.Single(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );

        restarted.Step(ticks: PastEveryLease);
        Assert.True(condition: restarted.TravelerAt(row: restarted.Source));
        Assert.Empty(collection: restarted.Host.CaptureRow(row: restarted.Source.Instance).InDoubtTransfers);

        AssertResentStepsAreRefused(
            transport: transport,
            world: restarted
        );
    }

    // THE LAW (Missing is final): every commit is lost on the way, so the lease runs out, the source aborts, asks, hears
    // Missing and restores the traveller. The lane then delivers the reservation and the commit again. The destination
    // tombstoned the key before it answered Missing, so it leases and commits nothing under it, and the tombstone
    // outlives a destination restart from a checkpoint that still held the live lease. Red at the tip: the resent
    // reservation leased the key again (or the restored checkpoint's lease took the commit) and a second traveller
    // landed.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AKeyAnsweredMissingIsNeverLeasedOrCommittedAgain(bool destinationRestarts) {
        using var world = CrossingWorld.Build();
        var transport = new ResendingPeerCall(destination: world.Destination.Server) {
            LoseEveryCommit = true,
        };

        // The destination's durable image holds the live lease, as a checkpoint taken mid-crossing would.
        transport.AfterReserve = world.CheckpointDestination;
        world.Host.SetPeerCallFault(
            fault: transport,
            instanceName: "row-b"
        );
        _ = world.Cross();
        world.Step(ticks: PastEveryLease);
        Assert.True(condition: world.TravelerAt(row: world.Source));
        Assert.Empty(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);

        using var restarted = (destinationRestarts
            ? world.Restart(
                destinationDied: true,
                sourceDied: false
            )
            : null);

        AssertResentStepsAreRefused(
            transport: transport,
            world: (restarted ?? world)
        );
    }

    public enum MissingAnswer {
        // The status of a key the destination never leased.
        StatusOfAnUnknownKey,
        // A commit under a key the destination never leased.
        CommitOfAnUnknownKey,
        // A commit whose lease ran out first.
        CommitAfterTheLeaseExpired,
    }

    // THE LAW (Missing is final, at the escrow): however the destination came to answer Missing for a key — a status
    // query or a commit for a key it never leased, or a commit whose lease expired — a later reservation and commit under
    // that key land nothing, beside a control key that reserves and commits. Red at the tip: none of the three answers
    // left a trace, so the later reservation was accepted and its commit landed.
    [InlineData(MissingAnswer.StatusOfAnUnknownKey)]
    [InlineData(MissingAnswer.CommitOfAnUnknownKey)]
    [InlineData(MissingAnswer.CommitAfterTheLeaseExpired)]
    [Theory]
    public void EveryMissingAnswerTombstonesItsKey(MissingAnswer answer) {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;
        var request = Reservation(transferId: 5);

        switch (answer) {
            case MissingAnswer.StatusOfAnUnknownKey:
                Assert.Equal(
                    expected: WorldTransferStatus.Missing,
                    actual: destination.TransferStatus(
                        sourceAuthority: request.SourceAuthority,
                        transferId: request.TransferId
                    )
                );
                break;
            case MissingAnswer.CommitOfAnUnknownKey:
                Assert.Equal(
                    expected: WorldTransferStatus.Missing,
                    actual: destination.CommitTransfer(
                        members: [Member],
                        reason: out _,
                        sourceAuthority: request.SourceAuthority,
                        transferId: request.TransferId
                    )
                );
                break;
            default: {
                    var first = destination.ReserveTransfer(request: request);

                    Assert.True(condition: first.Accepted, userMessage: first.Reason);
                    world.Step(ticks: 8);
                    Assert.Equal(
                        expected: WorldTransferStatus.Missing,
                        actual: destination.CommitTransfer(
                            members: [Member],
                            reason: out _,
                            sourceAuthority: request.SourceAuthority,
                            transferId: request.TransferId
                        )
                    );
                    break;
                }
        }

        var again = destination.ReserveTransfer(request: request);

        Assert.False(condition: again.Accepted, userMessage: "a key answered Missing was leased");
        Assert.NotEqual(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(
                members: [Member],
                reason: out _,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: destination.TransferStatus(
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );
        Assert.False(condition: destination.Population.IsActive(index: 0));

        // The control: the next key reserves and commits.
        var control = Reservation(transferId: 6);
        var reserved = destination.ReserveTransfer(request: control);

        Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(
                members: [Member],
                reason: out var committedReason,
                sourceAuthority: control.SourceAuthority,
                transferId: control.TransferId
            )
        );
        Assert.True(condition: destination.Population.IsActive(index: 0), userMessage: committedReason);
    }

    // THE LAW (allocated before use): the source dies after its reservation is accepted and before its departure is
    // durable, then restarts from a checkpoint taken before the crossing. The orphaned lease expires at its deadline.
    // The source's log says the key was allocated, so its next crossing carries a new key, which the destination leases
    // afresh, and the traveller arrives exactly once. Red at the tip: the restarted source minted the same key again;
    // once the destination keeps its leased keys dead, that reuse is refused and the traveller never arrives.
    [Fact]
    public void ASourceThatDiesBetweenItsReservationAndItsDepartureNeverReusesTheKey() {
        using var world = CrossingWorld.Build();
        var transport = new ResendingPeerCall(destination: world.Destination.Server);

        world.Host.SetPeerCallFault(
            fault: transport,
            instanceName: "row-b"
        );
        world.SourceLog.CrashBefore = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        var used = transport.Reservation!.TransferId;

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        restarted.Step(ticks: PastEveryLease);
        Assert.NotEqual(
            expected: used,
            actual: restarted.Cross()
        );
        restarted.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 1,
            actual: restarted.CountTraveler()
        );
        Assert.True(condition: restarted.TravelerAt(row: restarted.Destination));
    }

    // THE LAW (outcomes retained): a committed traveller leaves the destination before its source has settled the
    // commit. The destination still answers Committed for the key, to a status query and to a resent commit, so a source
    // whose commit answer was lost never restores a traveller that arrived. Red at the tip: retiring the traveller's
    // mobility retired the committed record with it, and the key answered Missing.
    [Fact]
    public void ACommittedKeyStillAnswersCommittedAfterItsTravellerLeaves() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;
        var request = Reservation(transferId: 9);
        var reserved = destination.ReserveTransfer(request: request);

        Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(
                members: [Member],
                reason: out _,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );

        destination.RetireTransferredMobility(mobility: request.Members[0].Mobility!.Value.Advance());

        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.TransferStatus(
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(
                members: [Member],
                reason: out var resentReason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );
        Assert.Empty(collection: resentReason);

        // An acknowledgement, and a resent one, retire nothing further.
        destination.AcknowledgeTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
        destination.AcknowledgeTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.TransferStatus(
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            )
        );
        Assert.False(condition: destination.ReserveTransfer(request: request).Accepted);
    }

    // THE LAW (retirement): a traveller retired here while a reservation for it is pending kills that lease, and its key
    // is dead: the commit is refused, the status answers Missing, and the key is never leased again.
    [Fact]
    public void RetiringATravellerKillsTheLeaseHeldForIt() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;
        var request = Reservation(transferId: 4);

        Assert.True(condition: destination.ReserveTransfer(request: request).Accepted);
        destination.RetireTransferredMobility(mobility: request.Members[0].Mobility!.Value);
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: destination.CommitTransfer(members: [Member], reason: out _, sourceAuthority: request.SourceAuthority, transferId: request.TransferId)
        );
        Assert.False(condition: destination.ReserveTransfer(request: request).Accepted);
        Assert.False(condition: destination.Population.IsActive(index: 0));
    }

    private static int LedgerRows(WorldServer destination) => destination.ExecuteAuthorityOperation(operation: () => destination.TransferEscrow.Counts.TransferKeys);

    // THE LAW (bounded ledger): an honest source's settled floor rises with its reservations, so the destination keeps
    // a row only for keys at or above it, however many keys the source has spent. A key below the floor is dead without
    // a row: it is never leased, a status query answers Missing without a tombstone, and a commit under a lease left
    // below it is refused. A floor above its own key is refused, a lower floor never lowers it, and the ledger survives
    // a checkpoint round trip.
    [Fact]
    public void ASourcesSettledFloorBoundsTheLedger_AndEveryKeyBelowItIsDead() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        for (var id = 0UL; (id < 8UL); id++) {
            var reserved = destination.ReserveTransfer(request: Reservation(floor: id, transferId: id));

            Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
            destination.AbortTransfer(sourceAuthority: "machine-a/boot", transferId: id);
            Assert.InRange(actual: LedgerRows(destination: destination), high: 2, low: 1);
        }

        var below = destination.ReserveTransfer(request: Reservation(floor: 3, transferId: 3));

        Assert.False(condition: below.Accepted);
        Assert.Contains(actualString: below.Reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "settled floor");
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 3));
        Assert.Equal(expected: 2, actual: LedgerRows(destination: destination));

        // A lease the source left behind below its floor takes no commit.
        var left = destination.ReserveTransfer(request: Reservation(floor: 7, transferId: 10));

        Assert.True(condition: left.Accepted, userMessage: left.Reason);
        var next = destination.ReserveTransfer(request: Reservation(floor: 11, traveller: 1, transferId: 11));

        Assert.True(condition: next.Accepted, userMessage: next.Reason);
        Assert.Equal(
            expected: WorldTransferStatus.Missing,
            actual: destination.CommitTransfer(members: [Member], reason: out var belowReason, sourceAuthority: "machine-a/boot", transferId: 10)
        );
        Assert.Contains(actualString: belowReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "settled floor");
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 10));

        var above = destination.ReserveTransfer(request: Reservation(floor: 21, traveller: 2, transferId: 20));

        Assert.False(condition: above.Accepted);
        Assert.Contains(actualString: above.Reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "above its own id");

        var lower = destination.ReserveTransfer(request: Reservation(floor: 5, traveller: 2, transferId: 30));

        Assert.True(condition: lower.Accepted, userMessage: lower.Reason);
        destination.AbortTransfer(sourceAuthority: "machine-a/boot", transferId: 30);
        Assert.False(condition: destination.ReserveTransfer(request: Reservation(floor: 11, traveller: 3, transferId: 10)).Accepted);

        // A rising floor drops the settled rows below it and keeps the rows above it; the live lease 11 keeps its row.
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: 11, traveller: 4, transferId: 40)).Accepted);
        destination.AbortTransfer(sourceAuthority: "machine-a/boot", transferId: 40);
        Assert.Equal(expected: 4, actual: LedgerRows(destination: destination));
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: 35, traveller: 5, transferId: 50)).Accepted);
        destination.AbortTransfer(sourceAuthority: "machine-a/boot", transferId: 50);
        Assert.Equal(expected: 4, actual: LedgerRows(destination: destination));
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 40));
        Assert.False(condition: destination.ReserveTransfer(request: Reservation(floor: 35, traveller: 6, transferId: 40)).Accepted);

        var rows = LedgerRows(destination: destination);

        using var restarted = world.Restart(destinationDied: false, sourceDied: false);
        var recovered = restarted.Destination.Server;

        Assert.Equal(expected: rows, actual: LedgerRows(destination: recovered));
        Assert.False(condition: recovered.ReserveTransfer(request: Reservation(floor: 11, traveller: 3, transferId: 30)).Accepted);
        Assert.Equal(expected: WorldTransferStatus.Reserved, actual: recovered.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 11));
    }

    // Asks after `count` keys the destination never leased, from one source, tombstoning each.
    private static void Tombstone(WorldServer destination, string source, int count) {
        for (var id = 0; (id < count); id++) {
            Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: source, transferId: (1000UL + (ulong)id)));
        }
    }

    // THE LAW (no eviction): a full ledger evicts nothing to admit new work. A source that has filled its own share —
    // here by asking after keys it never reserved — gets Uncertain for an unknown key, since the destination cannot
    // tombstone it, and has its reservations refused, while another source's keys are still answered; a reservation whose
    // settled floor drops the source's rows is admitted. The ledger as a whole is bounded the same way across sources.
    [Fact]
    public void AFullLedgerEvictsNothing_AndARisingFloorFreesIt() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        Tombstone(count: WorldTransferLimits.TransferKeysPerSource, destination: destination, source: "machine-a/boot");
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 1));
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 1000));
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-c/boot", transferId: 1));

        var refused = destination.ReserveTransfer(request: Reservation(transferId: 2));

        Assert.False(condition: refused.Accepted);
        Assert.Contains(actualString: refused.Reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is full");

        var freed = destination.ReserveTransfer(request: Reservation(floor: 70_000, transferId: 70_000));

        Assert.True(condition: freed.Accepted, userMessage: freed.Reason);
        // The source's floor, its live lease and the other source's tombstone.
        Assert.Equal(expected: 3, actual: LedgerRows(destination: destination));

        // Every source's share filled together fills the ledger: a new source is answered Uncertain and refused.
        for (var source = 1; (source < (WorldTransferLimits.TransferKeys / WorldTransferLimits.TransferKeysPerSource)); source++) {
            Tombstone(count: WorldTransferLimits.TransferKeysPerSource, destination: destination, source: $"machine-{source}/boot");
        }
        var counts = destination.ExecuteAuthorityOperation(operation: () => destination.TransferEscrow.Counts);

        Tombstone(count: (WorldTransferLimits.TransferKeys - counts.TransferKeys), destination: destination, source: "machine-last/boot");
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: destination.TransferStatus(sourceAuthority: "machine-z/boot", transferId: 1));
        Assert.False(condition: destination.ReserveTransfer(request: Reservation(transferId: 3) with { SourceAuthority = "machine-z/boot" }).Accepted);
    }

    // THE LAW (write ahead): a destination whose log refuses a lease record accepts no reservation and changes nothing,
    // so the key's first durable lease is still its only one; one whose log does not take a tombstone answers Uncertain
    // rather than Missing, and Missing once the tombstone lands, including through an in-place recovery that finds it.
    [Fact]
    public void ALeaseOrTombstoneTheLogDoesNotTakeIsNeverActedOn() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        world.DestinationLog.LoseAnswerOn = typeof(WorldCrossingRecord.Lease);
        world.DestinationLog.LostAnswer = WorldCrossingDurability.Refused;
        var refused = destination.ReserveTransfer(request: Reservation(transferId: 1));

        Assert.False(condition: refused.Accepted);
        Assert.Contains(actualString: refused.Reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "could not be made durable");
        Assert.Equal(expected: 0, actual: LedgerRows(destination: destination));
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(transferId: 1)).Accepted);
        Assert.Equal(expected: WorldTransferStatus.Reserved, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 1));

        world.DestinationLog.LoseAnswerOn = typeof(WorldCrossingRecord.Tombstone);
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 3));
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 3));

        // The store loses its answer to a tombstone that lands: Uncertain until recovery finds it.
        world.DestinationLog.LoseAnswerOn = typeof(WorldCrossingRecord.Tombstone);
        world.DestinationLog.LostAnswer = WorldCrossingDurability.Uncertain;
        world.DestinationLog.LostAnswerLands = true;
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 2));
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: destination.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 2));
        world.RecoverDestination();
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: world.Destination.Server.TransferStatus(sourceAuthority: "machine-a/boot", transferId: 2));
        Assert.False(condition: world.Destination.Server.ReserveTransfer(request: Reservation(transferId: 2)).Accepted);
    }

    // THE LAW (one door, failed appends): a reservation whose lease record the log refuses changes nothing, so a peer
    // sending fresh keys, each claiming its own id as its settled floor, against a destination whose log refuses every
    // append grows no ledger row. Red before the one admission door: each refused append left a spent row and raised no
    // floor, so the rows grew with every request.
    [Fact]
    public void AReservationWhoseLeaseIsRefusedTakesNoRow() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        // A newer activation fences the destination's writer: every append is refused.
        _ = world.DestinationLog.Activate();
        for (var id = 1UL; (id <= 16UL); id++) {
            var reply = destination.ReserveTransfer(request: Reservation(floor: id, transferId: id));

            Assert.False(condition: reply.Accepted);
            Assert.Contains(actualString: reply.Reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "could not be made durable");
        }
        Assert.Equal(expected: 0, actual: LedgerRows(destination: destination));
    }

    // THE LAW (one door, every row counted): a source's share counts every row it holds, its live leases included. A
    // source that holds one lease and asks after a share's worth of keys it never reserved is answered Uncertain for the
    // last of them, and after the lease is aborted it still holds exactly its share. Red before the one admission door:
    // the share counted only settled keys, so every key was tombstoned and the aborted lease left the source one row past
    // its share.
    [Fact]
    public void ASourcesShareCountsItsLiveLeases() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;
        var request = Reservation(transferId: 1);

        Assert.True(condition: destination.ReserveTransfer(request: request).Accepted);
        var answers = new List<WorldTransferStatus>();

        for (var id = 0; (id < WorldTransferLimits.TransferKeysPerSource); id++) {
            answers.Add(item: destination.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: (1000UL + (ulong)id)));
        }
        destination.AbortTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);

        Assert.Equal(expected: (WorldTransferLimits.TransferKeysPerSource - 1), actual: answers.Count(predicate: static answer => (answer == WorldTransferStatus.Missing)));
        Assert.Equal(expected: WorldTransferStatus.Uncertain, actual: answers[^1]);
        Assert.Equal(expected: WorldTransferLimits.TransferKeysPerSource, actual: LedgerRows(destination: destination));
    }

    // THE LAW (a committed key's fate): a committed key answers Committed to every later commit. A commit carrying a
    // different payload lands nothing and names the difference, and the original commit and the status query still
    // answer Committed, so the key never answers Missing after Committed. Red before: the different payload answered
    // Missing, a non-final Missing between two Committed answers.
    [Fact]
    public void ACommittedKeyAnswersCommittedWhateverPayloadArrives() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;
        var request = Reservation(transferId: 12);
        var other = Member with { Position = new Puck.Maths.FixedVector3(X: Puck.Maths.FixedQ4816.One, Y: default, Z: default) };

        Assert.True(condition: destination.ReserveTransfer(request: request).Accepted);
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(members: [Member], reason: out _, sourceAuthority: request.SourceAuthority, transferId: request.TransferId)
        );
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(members: [other], reason: out var otherReason, sourceAuthority: request.SourceAuthority, transferId: request.TransferId)
        );
        Assert.Contains(actualString: otherReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "different commit");
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(members: [Member], reason: out var sameReason, sourceAuthority: request.SourceAuthority, transferId: request.TransferId)
        );
        Assert.Empty(collection: sameReason);
        Assert.Equal(expected: WorldTransferStatus.Committed, actual: destination.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
        Assert.True(condition: destination.Population.IsActive(index: 0));
        Assert.False(condition: destination.Population.IsActive(index: 1));
    }

    // THE LAW (allocation write ahead): a source whose log does not take the allocation of a transfer's id sends no
    // reservation under it, and the traveller stays home.
    [Fact]
    public void ASourceWhoseLogDoesNotTakeAnAllocationSendsNoReservation() {
        using var world = CrossingWorld.Build();
        var transport = new ResendingPeerCall(destination: world.Destination.Server);

        world.Host.SetPeerCallFault(
            fault: transport,
            instanceName: "row-b"
        );
        world.SourceLog.LoseAnswerOn = typeof(WorldCrossingRecord.Allocation);
        world.SourceLog.LostAnswer = WorldCrossingDurability.Refused;
        var sink = new RecordingNarrationSink();

        using (world.Host.AttachNarrationSink(sink: sink)) {
            _ = world.Cross();
        }
        world.Step(ticks: 4);
        Assert.Null(@object: transport.Reservation);
        Assert.Equal(expected: 1, actual: world.CountTraveler());
        Assert.True(condition: world.TravelerAt(row: world.Source));
        Assert.Contains(collection: sink.Narrations, filter: static narration => narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "no reservation was sent"));
    }

    // THE LAW (floor): a source's settled floor never passes a transfer it still holds in doubt. One traveller's commit
    // is lost, so its transfer stays in doubt; the next traveller's reservation carries the in-doubt id as its floor, so
    // the destination keeps the first key's row and the first transfer can still settle.
    [Fact]
    public void ASourcesFloorNeverPassesATransferItHoldsInDoubt() {
        using var world = CrossingWorld.Build();
        var transport = new ResendingPeerCall(destination: world.Destination.Server) {
            LoseEveryCommit = true,
        };

        world.Host.SetPeerCallFault(
            fault: transport,
            instanceName: "row-b"
        );
        var first = world.Cross();

        Assert.Single(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);
        _ = world.Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 1
        );
        world.Host.DrainPendingTransfers();

        Assert.Equal(expected: 2, actual: transport.Reservations.Count);
        Assert.NotEqual(expected: first, actual: transport.Reservations[1].TransferId);
        Assert.Equal(expected: first, actual: transport.Reservations[1].SourceFloor);
    }
}
