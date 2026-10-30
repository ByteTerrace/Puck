using Puck.Commands;
using Puck.World.Protocol;
using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

/// <summary>Companions crossing with their player under home's custody (docs/plans/companion-redesign.md, build
/// steps 2 and 3) on real authorities in one host: each member is decided by its request-local id, a companion follows
/// the member it names, home authorizes every hop it holds the title for, and a destination lands a companion only
/// under a grant home confirms to it for that very decision. Each law is the engine form of a named trace of the custody
/// model (<c>CompanionCustodyModelLawTests</c>). Row a is home; its companions follow its seat 0.</summary>
public sealed partial class CompanionCrossingLawTests {

    // Model traces H2_H10_AdmissionAndBindingArePerMemberById and Epochs: the companion crosses with its player as a
    // member of its own, named by id; home records the hop before anything is sent, the destination lands the companion
    // one epoch above the one it left, bound by id to the player member landed in the same commit, and home settles its
    // own hop from its own settlement.
    [Fact]
    public void ACompanionFollowsItsPlayerByMemberIdAndLandsAtTheGrantsEpoch() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: -1);
        Assert.Equal(actual: Rows.EpochAt(row: rows.B, incarnation: companion), expected: 1UL);

        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity, 1UL));
        Assert.True(condition: rows.B.Server.ExecuteAuthorityOperation(operation: () => rows.B.Server.TransferEscrow.Custody.TryBinding(
            arrival: out _,
            binding: out var binding,
            carried: out var carried,
            follows: out var follows,
            incarnation: companion
        ) && (follows == rows.Player) && (binding.Slot == Rows.SlotOf(row: rows.B, incarnation: rows.Player)) && (carried?.Role == "companion")));

        // Home's records, in order: the key, the hop, the departure, the settlement, then the hop's own settlement.
        var kinds = rows.Records(row: rows.Home).Select(selector: static record => record.GetType()).ToArray();

        Assert.Equal(
            actual: kinds,
            expected: [typeof(WorldCrossingRecord.Allocation), typeof(WorldCrossingRecord.Hop), typeof(WorldCrossingRecord.Departure), typeof(WorldCrossingRecord.Settlement), typeof(WorldCrossingRecord.HopDone)]
        );
    }
    // A destination that declares nothing for the role refuses the companion alone: the player crosses, the companion
    // stays home, and home settles its hop as not arrived, so the title is home at the epoch it never left.
    [Fact]
    public void ADestinationThatDeclaresNoRoleRefusesOnlyTheCompanion() {
        using var rows = new Rows(bDeclaresRole: false);
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);
        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: companion) >= 0);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Epoch), expected: (WorldCustodyKind.Home, 0UL));
        Assert.Contains(collection: rows.Records(row: rows.Home), filter: record => record is WorldCrossingRecord.HopDone { Arrived: false } done && (done.Incarnation == companion));
    }
    // Model trace AFullCustodianRefusesTheSecondCompanion: a destination with one free body for companions leases the
    // first and refuses the second for capacity; each is decided on its own.
    [Fact]
    public void AFullCustodianRefusesTheSecondCompanion() {
        using var rows = new Rows(companions: 2, bPeerSlots: 1);

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Companions[0]) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: rows.Companions[1]), expected: -1);
        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Companions[1]) >= 0);
        Assert.Equal(actual: rows.Title(companion: rows.Companions[0]).Kind, expected: WorldCustodyKind.Away);
        Assert.Equal(actual: rows.Title(companion: rows.Companions[1]).Kind, expected: WorldCustodyKind.Home);
    }
    // Model trace AStrangerFillsTheCapAndThePlayersOwnAdmissionIsRefused: when the player's own admission fails, the
    // whole reservation is refused and nothing departs, companions included.
    [Fact]
    public void ThePlayersOwnRefusalRefusesTheWholeCohort() {
        using var rows = new Rows();

        for (var slot = 0; (slot < WorldBodiesLimits.LocalSeatCount); slot++) {
            Assert.True(condition: rows.B.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
        rows.Cross(from: rows.Home, to: rows.B);
        // A full destination is retried a bounded number of drains before the attempt is abandoned on record.
        rows.Step(ticks: 12);

        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Companions[0]) >= 0);
        Assert.Equal(actual: rows.Title(companion: rows.Companions[0]).Kind, expected: WorldCustodyKind.Home);
        Assert.DoesNotContain(collection: rows.Records(row: rows.Home), filter: static record => record is WorldCrossingRecord.Departure);
    }
    // Model trace TwoCompanionsSettlePerMemberAndPartialLandingRestoresTheOther: the destination refuses one leased
    // companion at the landing itself; the rest land, the source's settlement names who landed, the other companion is
    // restored home, and each title settles on its own.
    [Fact]
    public void TwoCompanionsSettlePerMemberAndPartialLandingRestoresTheOther() {
        using var rows = new Rows(companions: 2, bLandingRefusal: static ordinal => ((ordinal == 1) ? "refused at the landing" : null));

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Companions[1]) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: rows.Companions[0]), expected: -1);
        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Companions[0]) >= 0);

        var settlement = Assert.Single(collection: rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Settlement>());

        Assert.Equal(actual: settlement.Landed.Order().ToArray(), expected: [0, 2]);
        Assert.Equal(actual: rows.Title(companion: rows.Companions[0]).Kind, expected: WorldCustodyKind.Home);
        Assert.Equal(actual: (rows.Title(companion: rows.Companions[1]).Kind, rows.Title(companion: rows.Companions[1]).Epoch), expected: (WorldCustodyKind.Away, 1UL));
        // The arrival names only what landed, with the generation each took.
        var arrival = Assert.Single(collection: rows.Records(row: rows.B).OfType<WorldCrossingRecord.Arrival>()).Value;

        Assert.Equal(actual: arrival.Members.Count, expected: 2);
        Assert.Equal(actual: arrival.Generations.Count, expected: 2);
    }
    // Model trace AFailedLandingStillAdvancesTheSlotGeneration: a commit whose arrival record is refused rolls its
    // landings back, and the slots keep the generations those landings took; the cohort is restored home and home's hop
    // settles as not arrived.
    [Fact]
    public void AFailedLandingStillAdvancesTheSlotGeneration() {
        using var rows = new Rows();
        var before = Enumerable.Range(start: 0, count: rows.B.Server.Population.Capacity).Select(selector: slot => rows.B.Server.Population.Generation(index: slot)).ToArray();

        rows.Logs["row-b"].LoseAnswerOn = typeof(WorldCrossingRecord.Arrival);
        rows.Logs["row-b"].LostAnswer = WorldCrossingDurability.Refused;
        rows.Cross(from: rows.Home, to: rows.B);

        var after = Enumerable.Range(start: 0, count: rows.B.Server.Population.Capacity).Select(selector: slot => rows.B.Server.Population.Generation(index: slot)).ToArray();

        Assert.Equal(actual: Enumerable.Range(start: 0, count: before.Length).Count(predicate: slot => after[slot] > before[slot]), expected: 2);
        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Companions[0]) >= 0);
        Assert.Equal(actual: (rows.Title(companion: rows.Companions[0]).Kind, rows.Title(companion: rows.Companions[0]).Epoch), expected: (WorldCustodyKind.Home, 0UL));
    }
    // Model trace ADestinationVerifiesHomesHopAuthorization: a reservation carrying a companion whose ticket home never
    // recorded (a custodian forging a move) is refused Unverified at the destination, while its player is leased.
    [Fact]
    public void ADestinationVerifiesHomesHopAuthorization() {
        using var rows = new Rows();
        var key = new WorldTransferKey(SourceAuthority: rows.C.Server.AuthorityIdentity, TransferId: 77UL);
        var reply = rows.B.Server.ReserveTransfer(request: ForgedRequest(rows: rows, key: key, ticketEpoch: 1UL));

        Assert.True(condition: reply.Accepted);
        Assert.Equal(actual: reply.AcceptedMembers, expected: [0]);
        Assert.Equal(actual: Assert.Single(collection: reply.Refusals).Reason, expected: WorldCompanionRefusal.Unverified);
    }
    // Model trace ACancelledAuthorizationDoesNotVerifyAForgedHop: a hop home recorded and then settled (cancelled) is no
    // grant: a reservation presenting its ticket is refused Unverified.
    [Fact]
    public void ACancelledAuthorizationDoesNotVerifyAForgedHop() {
        using var rows = new Rows();
        var companion = rows.Companions[0];
        var key = new WorldTransferKey(SourceAuthority: rows.C.Server.AuthorityIdentity, TransferId: 78UL);

        Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Hop(
            From: rows.C.Server.AuthorityIdentity,
            Key: key,
            Members: [new WorldCustodyHopMember(Epoch: 0UL, Incarnation: companion, Next: 1UL)],
            To: rows.B.Server.AuthorityIdentity
        )), expected: WorldCrossingDurability.Durable);
        // The hop is settled as not arrived before the custodian presents its ticket: the record is history, not a grant.
        Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.HopDone(Arrived: false, Incarnation: companion, Key: key)), expected: WorldCrossingDurability.Durable);

        var reply = rows.B.Server.ReserveTransfer(request: ForgedRequest(rows: rows, key: key, ticketEpoch: 1UL));

        Assert.Equal(actual: reply.AcceptedMembers, expected: [0]);
        Assert.Equal(actual: Assert.Single(collection: reply.Refusals).Reason, expected: WorldCompanionRefusal.Unverified);
    }
    // The control: the same reservation under a hop home currently grants to this destination is leased whole.
    [Fact]
    public void AGrantedHopIsLeasedWhole() {
        using var rows = new Rows();
        var key = new WorldTransferKey(SourceAuthority: rows.C.Server.AuthorityIdentity, TransferId: 81UL);

        Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Hop(
            From: rows.C.Server.AuthorityIdentity,
            Key: key,
            Members: [new WorldCustodyHopMember(Epoch: 0UL, Incarnation: rows.Companions[0], Next: 1UL)],
            To: rows.B.Server.AuthorityIdentity
        )), expected: WorldCrossingDurability.Durable);

        var reply = rows.B.Server.ReserveTransfer(request: ForgedRequest(rows: rows, key: key, ticketEpoch: 1UL));

        Assert.Equal(actual: reply.AcceptedMembers, expected: [0, 1]);
        Assert.Empty(collection: reply.Refusals);
        // A ticket naming another epoch than the grant's is refused even under the granted key.
        rows.B.Server.AbortTransfer(sourceAuthority: key.SourceAuthority, transferId: key.TransferId);

        var other = key with { TransferId = 82UL };

        Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Hop(
            From: rows.C.Server.AuthorityIdentity,
            Key: other,
            Members: [new WorldCustodyHopMember(Epoch: 0UL, Incarnation: rows.Companions[0], Next: 1UL)],
            To: rows.B.Server.AuthorityIdentity
        )), expected: WorldCrossingDurability.Durable);
        Assert.Equal(actual: Assert.Single(collection: rows.B.Server.ReserveTransfer(request: ForgedRequest(rows: rows, key: other, ticketEpoch: 5UL)).Refusals).Reason, expected: WorldCompanionRefusal.Unverified);
    }
    // Model traces ACommitLandsOnlyUnderTheGrantAsItStandsAtTheCommit, ADelayedVerificationAnswerCannotAuthorizeARevokedHop
    // and ADuplicatedReservationAnswerCannotSatisfyTheCommitRecheckAfterARelease: the destination asks home again for
    // the commit, and only that answer counts; a release recorded between the reservation and the commit leaves the
    // companion out while the player lands, and the companion is restored home.
    [Fact]
    public void ACommitLandsOnlyUnderTheGrantAsItStandsAtTheCommit() {
        using var rows = new Rows();
        var companion = rows.Companions[0];
        var questions = 0;
        var homes = rows.B.Server.CustodyPeers!;

        rows.B.Server.CustodyPeers = authority => new CountingHome(Counted: () => questions++, Inner: homes(arg: authority)!);
        rows.Host.SetPeerCallFault(
            fault: new BeforeCommit(
                Destination: rows.B.Server,
                Hook: () => Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Release(Incarnation: companion)), expected: WorldCrossingDurability.Durable)
            ),
            instanceName: "row-b"
        );
        rows.Cross(from: rows.Home, to: rows.B);

        Assert.Equal(actual: questions, expected: 2);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Released);
    }
    // Model trace Epochs and step 3's onward hop: abroad, the companion follows its player by the member id it arrived
    // with; the custodian asks home for the onward hop, home records it, and the next destination lands the companion
    // one epoch higher again, verified with home.
    [Fact]
    public void AnOnwardHopIsAuthorizedByHomeAndLandsOneEpochHigher() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        rows.Cross(from: rows.B, to: rows.C);

        Assert.True(condition: Rows.SlotOf(row: rows.C, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.C, incarnation: companion) >= 0);
        Assert.Equal(actual: Rows.EpochAt(row: rows.C, incarnation: companion), expected: 2UL);

        var hop = rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Hop>().Last();

        Assert.Equal(actual: (hop.From, hop.To, hop.Members.Single().Epoch, hop.Members.Single().Next), expected: (rows.B.Server.AuthorityIdentity, rows.C.Server.AuthorityIdentity, 1UL, 2UL));
        // The destination's word settled the hop: the title is with the new custodian at the epoch home issued.
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Epoch), expected: (WorldCustodyKind.Away, 2UL));
    }

    // ---- step 4: settlement by the destination's word ----------------------------------------------------------------

    // Model trace HomeResolvesAPendingHopByAnAuthorizedCustodyQuery: the custodian's settlement notice makes home ask the
    // hop's destination, as the companion's title authority, what it holds; home settles the hop from that answer, and
    // the custodian records home's acknowledgement. Only the title authority may ask.
    [Fact]
    public void HomeResolvesAPendingHopByAnAuthorizedCustodyQuery() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        rows.Cross(from: rows.B, to: rows.C);

        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Away, rows.C.Server.AuthorityIdentity, 2UL));
        Assert.Contains(collection: rows.Records(row: rows.B), filter: record => record is WorldCrossingRecord.HopAcked acked && (acked.Incarnation == companion));
        Assert.Contains(collection: rows.Records(row: rows.Home), filter: record => record is WorldCrossingRecord.HopDone { Arrived: true } done && (done.Incarnation == companion) && (done.Key.SourceAuthority == rows.B.Server.AuthorityIdentity));

        var hop = rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Hop>().Last();

        Assert.Equal(actual: rows.C.Server.QueryCustody(asker: rows.B.Server.AuthorityIdentity, incarnation: companion, key: hop.Key), expected: WorldCustodyAnswer.Unauthorized);
        Assert.Equal(actual: rows.C.Server.QueryCustody(asker: rows.Home.Server.AuthorityIdentity, incarnation: companion, key: hop.Key), expected: WorldCustodyAnswer.Landed);
    }
    // Model trace H9_TheSettlementPayloadNamesWhoLandedSoALateGrantMovesNoCustody: the onward destination leaves the
    // companion out of its commit; the custodian restores it, and home, asking the destination, settles the hop as not
    // arrived, so the title stays with the custodian at the epoch it held.
    [Fact]
    public void AnOnwardHopTheDestinationLeftOutSettlesBackToItsCustodian() {
        using var rows = new Rows(cLandingRefusal: static ordinal => ((ordinal == 1) ? "refused at the landing" : null));
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        rows.Cross(from: rows.B, to: rows.C);

        Assert.True(condition: Rows.SlotOf(row: rows.C, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);

        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity, 1UL));
    }
    // A granted onward hop the destination refuses at reservation never departs: the custodian gives it up, home asks
    // the destination, and the hop settles back to the custodian.
    [Fact]
    public void AnOnwardHopThatNeverDepartsIsGivenUp() {
        using var rows = new Rows(cDeclaresRole: false);
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        rows.Cross(from: rows.B, to: rows.C);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);

        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity, 1UL));
    }
    // Model trace ASettlementNoticeThatOvertakesItsAuthorizationIsRemembered: a notice that reaches home before the hop
    // it settles is recorded, so the delayed request is answered Dead and no title is left pending.
    [Fact]
    public void ASettlementNoticeThatOvertakesItsAuthorizationIsRemembered() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);

        var key = new WorldTransferKey(SourceAuthority: rows.B.Server.AuthorityIdentity, TransferId: 99UL);

        Assert.True(condition: rows.Home.Server.SettleHop(incarnation: companion, key: key, source: rows.B.Server.AuthorityIdentity));

        var answer = Assert.Single(collection: rows.Home.Server.RequestHop(
            destination: rows.C.Server.AuthorityIdentity,
            key: key,
            members: [new WorldHopRequestMember(Epoch: 1UL, Incarnation: companion)],
            source: rows.B.Server.AuthorityIdentity
        ));

        Assert.Equal(actual: answer.Verdict, expected: WorldHopVerdict.Dead);
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Away);
    }
    // Model trace HomeHoldsTheSlotOfItsOwnDepartureInDoubt (its settlement half): home's own hop settles from home's
    // own records alone; a notice from another authority does not settle it.
    [Fact]
    public void HomesOwnHopSettlesOnlyFromItsOwnRecords() {
        using var rows = new Rows();
        var companion = rows.Companions[0];
        var key = new WorldTransferKey(SourceAuthority: rows.Home.Server.AuthorityIdentity, TransferId: 98UL);

        Assert.Equal(actual: Assert.Single(collection: rows.Home.Server.AuthorizeOwnHop(destination: rows.B.Server.AuthorityIdentity, incarnations: [companion], key: key)).Verdict, expected: WorldHopVerdict.Granted);
        Assert.False(condition: rows.Home.Server.SettleHop(incarnation: companion, key: key, source: rows.B.Server.AuthorityIdentity));
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Pending);
    }

    // A reservation from row-c carrying the player and home's companion with a ticket for this key at `ticketEpoch`.
    private static WorldTransferReservationRequest ForgedRequest(Rows rows, WorldTransferKey key, ulong ticketEpoch) {
        var companion = rows.Companions[0];
        var origin = new WorldEntityAddress(Authority: rows.C.Server.AuthorityIdentity, Index: 0, Generation: 1);
        var tick = (rows.B.Server.NextInputTick - 1UL);

        return new WorldTransferReservationRequest(
            Border: "forged",
            BorderCapacity: null,
            DeadlineSourceTick: (tick + 30UL),
            Members: [
                new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Identity: null,
                    MemberId: 0,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: origin, Epoch: 3UL, Incarnation: origin),
                    PreferredSlot: 0,
                    Principal: Principal.Seat(slot: 0),
                    Source: IntentSource.Idle
                ),
                new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Companion: new WorldTransferCompanion(
                        Driver: null,
                        FollowsMember: 0,
                        Home: companion.Authority,
                        Role: "companion",
                        Ticket: new WorldCustodyTicket(Epoch: ticketEpoch, Home: companion.Authority, Hop: key, Incarnation: companion)
                    ),
                    Identity: null,
                    MemberId: 1,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: origin, Epoch: (ticketEpoch - 1UL), Incarnation: companion),
                    PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                    Principal: Principal.Console,
                    Source: IntentSource.Idle
                ),
            ],
            PartyAllOrNothing: true,
            PeerAdmission: false,
            SourceAuthority: key.SourceAuthority,
            SourceRateHz: rows.B.Server.Definition.SimulationRateHz,
            SourceTick: tick,
            TransferId: key.TransferId
        );
    }
    private sealed record CountingHome(Action Counted, IWorldCustodyPeer Inner) : IWorldCustodyPeer {
        public IReadOnlyList<ulong?>? ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination, WorldHopPhase phase) {
            Counted();
            return Inner.ConfirmHops(destination: destination, incarnations: incarnations, key: key, phase: phase);
        }
        public IReadOnlyList<WorldHopAnswer> RequestHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) => Inner.RequestHop(destination: destination, key: key, members: members, source: source);
        public WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker) => Inner.QueryCustody(asker: asker, incarnation: incarnation, key: key);
        public bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source) => Inner.SettleHop(incarnation: incarnation, key: key, source: source);
    }
    // Forwards every call to the destination's own server, running a hook just before the commit reaches it.
    private sealed record BeforeCommit(WorldServer Destination, Action Hook) : IWorldPeerCall {
        public void Abort(string sourceAuthority, ulong transferId) => Destination.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public void Acknowledge(string sourceAuthority, ulong transferId) => Destination.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            Hook();
            status = Destination.CommitTransfer(members: members, reason: out reason, sourceAuthority: sourceAuthority, transferId: transferId);
            return WorldTransferStep.Answered;
        }
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => Destination.ReserveTransfer(request: request);
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = Destination.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId);
            return true;
        }
        public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
            landed = Destination.TransferLanded(sourceAuthority: sourceAuthority, transferId: transferId);
            return true;
        }
    }
}
