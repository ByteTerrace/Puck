using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

// The checkpoint-2 review's reproductions on real authorities, each asserting the protocol's behaviour.
public sealed partial class CompanionCrossingLawTests {
    // A checkpoint's custody records are its own: changing a captured record changes neither the live history nor what
    // another restore of the same capture derives.
    [Fact]
    public void ACapturedCustodyHistoryIsACopy() {
        using var rows = new Rows();
        var incarnation = rows.Companions[0];

        _ = rows.Home.Server.AuthorizeOwnHop(destination: rows.B.Server.AuthorityIdentity, incarnations: [incarnation], key: new WorldTransferKey(SourceAuthority: rows.Home.Server.AuthorityIdentity, TransferId: 993UL));

        var captured = rows.Home.Server.ExecuteAuthorityOperation(operation: () => rows.Home.Server.TransferEscrow.Capture());
        var members = Assert.IsType<WorldCustodyHopMember[]>(captured.Custody.OfType<WorldCrossingRecord.Hop>().Single().Members);

        members[0] = new WorldCustodyHopMember(Epoch: 7UL, Incarnation: incarnation, Next: 8UL);

        var live = rows.Home.Server.ExecuteAuthorityOperation(operation: () => rows.Home.Server.TransferEscrow.Custody.History);

        Assert.Equal(actual: ((WorldCrossingRecord.Hop)live[0]).Members[0].Next, expected: 1UL);
        Assert.Equal(actual: rows.Title(companion: incarnation).MaxIssued, expected: 1UL);
    }
    // A destination's recovery lands a colocated player-and-companion arrival again: the companion lands in a peer index
    // through the entity door, whatever door its player used.
    [Fact]
    public void AColocatedCompanionArrivalRecovers() {
        using var rows = new Rows();

        rows.Cross(from: rows.Home, to: rows.B);

        var arrival = rows.Records(row: rows.B).OfType<WorldCrossingRecord.Arrival>().Single().Value;

        using var restarted = HostRow.Build(definition: rows.B.Server.Definition, name: "row-b");

        Assert.True(condition: restarted.Server.ExecuteAuthorityOperation(operation: () => restarted.Server.TransferEscrow.TryReland(arrival: arrival, reason: out var reason, recorded: null)));
        Assert.True(condition: Rows.SlotOf(row: restarted, incarnation: rows.Companions[0]) >= 0);
    }
    // A reservation naming one member id twice is refused whole: members are decided, leased and settled by id.
    [Fact]
    public void AReservationRepeatingAMemberIdIsRefused() {
        using var rows = new Rows(bDeclaresRole: false);
        var request = ForgedRequest(rows: rows, key: new WorldTransferKey(SourceAuthority: rows.C.Server.AuthorityIdentity, TransferId: 992UL), ticketEpoch: 1UL);

        request = request with { Members = [request.Members[0], request.Members[1] with { MemberId = 0 }] };

        var reply = rows.B.Server.ReserveTransfer(request: request);

        Assert.False(condition: reply.Accepted);
        Assert.Contains(expectedSubstring: "repeats member id", actualString: reply.Reason);
    }
    // A partial landing's verdict outlives the destination's receipt: a source that asks after the landed player's
    // receipt was superseded by a later arrival still learns the companion did not land, and restores it home.
    [Fact]
    public void APartialVerdictOutlivesASupersededReceipt() {
        using var rows = new Rows(bLandingRefusal: static ordinal => ((ordinal == 1) ? "refuse companion" : null));
        var fault = new HoldLanded(destination: rows.B.Server);

        rows.Host.SetPeerCallFault(fault: fault, instanceName: "row-b");
        rows.Cross(from: rows.Home, to: rows.B);

        var original = rows.Records(row: rows.B).OfType<WorldCrossingRecord.Arrival>().Single().Value;

        // The landed player moves on and comes back under a later epoch before the source has its answer.
        Assert.True(condition: rows.B.Server.ExecuteAuthorityOperation(operation: () => rows.B.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: original.Slots[0])), userMessage: $"slots {string.Join(',', original.Slots)}; player at {Rows.SlotOf(row: rows.B, incarnation: rows.Player)}; in doubt {rows.Host.CaptureRow(rows.Home.Instance).InDoubtTransfers.Count}");

        var tick = (rows.B.Server.NextInputTick - 1UL);
        var request = original.Request with {
            DeadlineSourceTick = (tick + 30UL),
            Members = [original.Request.Members[0] with { MemberId = 0, Mobility = original.Request.Members[0].Mobility!.Value with { Epoch = 2UL } }],
            SourceAuthority = rows.C.Server.AuthorityIdentity,
            SourceTick = tick,
            TransferId = 995UL,
        };

        Assert.True(condition: rows.B.Server.ReserveTransfer(request: request).Accepted);
        Assert.Equal(actual: rows.B.Server.CommitTransfer(members: [original.Members[0] with { MemberId = 0 }], reason: out _, sourceAuthority: request.SourceAuthority, transferId: request.TransferId), expected: WorldTransferStatus.Committed);
        fault.Release = true;
        rows.Step(ticks: 3);

        Assert.True(condition: Rows.SlotOf(row: rows.Home, incarnation: rows.Companions[0]) >= 0);
        Assert.Equal(actual: rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Settlement>().Single().Landed, expected: [0]);
    }

    // A companion's incarnation carries its title mark: a source that strips the companion leaf cannot land home's
    // companion as an ordinary traveller, and the same address without the mark names no title of home's.
    [Fact]
    public void AStrippedCompanionLeafLandsNoTitledIncarnation() {
        using var rows = new Rows();
        var companion = rows.Companions[0];
        var key = new WorldTransferKey(SourceAuthority: rows.C.Server.AuthorityIdentity, TransferId: 991UL);
        var forged = ForgedRequest(rows: rows, key: key, ticketEpoch: 1UL);
        var stripped = forged with { Members = [forged.Members[0], forged.Members[1] with { Companion = null }] };

        Assert.True(condition: companion.Titled);
        Assert.False(condition: rows.Player.Titled);

        var reply = rows.B.Server.ReserveTransfer(request: stripped);

        Assert.False(condition: reply.Accepted);
        Assert.Contains(expectedSubstring: "without the companion leaf", actualString: reply.Reason);
        // Unmarked, the address is another incarnation: home holds no title of it and grants it no hop.
        var unmarked = companion with { Titled = false };

        Assert.Null(@object: rows.Home.Server.ExecuteAuthorityOperation(operation: () => rows.Home.Server.TransferEscrow.Custody.TitleOf(incarnation: unmarked)));
        Assert.Equal(actual: Assert.Single(collection: rows.Home.Server.RequestHop(destination: rows.B.Server.AuthorityIdentity, key: key, members: [new WorldHopRequestMember(Epoch: 0UL, Incarnation: unmarked)], source: rows.C.Server.AuthorityIdentity)).Verdict, expected: WorldHopVerdict.NotMyTitle);
    }
    // A commit that landed members and could not make its arrival durable burns their slots' generations, and the
    // tombstone names them: a destination restarted from its image and log keeps those generations, so the slots' next
    // occupants are new generations there too.
    [Fact]
    public void ARestartedDestinationKeepsTheGenerationsARefusedLandingBurned() {
        using var rows = new Rows();
        var before = Enumerable.Range(start: 0, count: rows.B.Server.Population.Capacity).Select(selector: slot => rows.B.Server.Population.Generation(index: slot)).ToArray();

        rows.Logs["row-b"].LoseAnswerOn = typeof(WorldCrossingRecord.Arrival);
        rows.Logs["row-b"].LostAnswer = WorldCrossingDurability.Refused;
        rows.Cross(from: rows.Home, to: rows.B);

        var tombstone = Assert.Single(collection: rows.Records(row: rows.B).OfType<WorldCrossingRecord.Tombstone>());

        Assert.Equal(actual: tombstone.Burned.Count, expected: 2);
        rows.Restart(name: "row-b");

        foreach (var slot in tombstone.Burned) {
            Assert.True(condition: (rows.B.Server.Population.Generation(index: slot) > before[slot]), userMessage: $"body:{slot} is back at generation {before[slot]} after the restart");
        }
    }
    // Forwards every call to the destination, holding the landed verdict back until released.
    private sealed class HoldLanded(WorldServer destination) : IWorldPeerCall {
        public bool Release { get; set; }
        public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public void Acknowledge(string sourceAuthority, ulong transferId) => destination.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => destination.ReserveTransfer(request: request);
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            status = destination.CommitTransfer(members: members, reason: out reason, sourceAuthority: sourceAuthority, transferId: transferId);
            return WorldTransferStep.Answered;
        }
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = destination.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId);
            return true;
        }
        public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
            landed = destination.TransferLanded(sourceAuthority: sourceAuthority, transferId: transferId);
            return Release;
        }
    }
}
