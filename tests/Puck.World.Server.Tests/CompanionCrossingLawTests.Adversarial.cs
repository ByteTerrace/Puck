using Puck.Commands;
using Puck.World.Protocol;
using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

public sealed partial class CompanionCrossingLawTests {
    [Fact]
    public void ARollbackNeverRetainsMembersItAlreadyRestored() {
        using var rows = new Rows(retained: true);
        var companion = rows.Companions[0];
        rows.Host.SetPeerCallFault(instanceName: "row-b", fault: new BeforeCommit(
            Destination: rows.B.Server,
            Hook: () => {
                Assert.True(rows.Home.Server.ApplySession(new SessionRequest.Join(
                    IdentityName: null, Principal: Principal.Seat(0), Slot: 0,
                    WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
                var key = rows.Records(rows.B).OfType<WorldCrossingRecord.Lease>().Last().Key;
                rows.B.Server.AbortTransfer(key.SourceAuthority, key.TransferId);
            }));
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.True(Rows.SlotOf(rows.Home, companion) >= 0);
        var recovery = Assert.Single(rows.Host.CaptureRow(rows.Home.Instance).InDoubtTransfers);
        Assert.DoesNotContain(recovery.Landed, member => member.Mobility.Incarnation == companion);
        Assert.True(recovery.RestoreOnly);
        Assert.True(rows.Home.Server.ExecuteAuthorityOperation(() => rows.Home.Server.Population.TryDetachSeatForTransfer(0, out _)));
        rows.Step(2);
        rows.Cross(rows.Home, rows.C);
        rows.Step(2);
        Assert.Equal(-1, Rows.SlotOf(rows.Home, companion));
        Assert.True(Rows.SlotOf(rows.C, companion) >= 0);
    }

    [Fact]
    public void AConfirmationFromAnotherHomeCannotLeaseTheVictimsCompanion() {
        using var rows = new Rows();
        var key = new WorldTransferKey(rows.C.Server.AuthorityIdentity, 992UL);
        var request = ForgedRequest(rows, key, 1UL);
        var victim = request.Members[1];
        var other = victim with {
            MemberId = 2,
            Mobility = victim.Mobility!.Value with { Incarnation = new WorldEntityAddress("malicious", 8, 1, true) },
            Companion = victim.Companion! with {
                Home = "malicious",
                Ticket = victim.Companion.Ticket!.Value with { Home = "malicious" },
            },
        };
        var peers = rows.B.Server.CustodyPeers!;
        rows.B.Server.CustodyPeers = authority => authority == "malicious" ? new ForgingHome() : peers(authority);
        var reply = rows.B.Server.ReserveTransfer(request with { Members = [.. request.Members, other] });
        Assert.True(reply.Accepted);
        Assert.Equal([0], reply.AcceptedMembers);
        Assert.Equal(WorldCompanionRefusal.Unverified, reply.Refusals.Single(refusal => refusal.MemberId == 1).Reason);
    }

    [Fact]
    public void HomeIsNotReapedWhileItsTitleIsAway() {
        using var rows = new Rows();
        Assert.True(rows.Home.Server.ExecuteAuthorityOperation(() => rows.Home.Server.Population.TryDetachSeatForTransfer(1, out _)));
        rows.Cross(rows.Home, rows.B);
        Assert.Contains("row-a", rows.Host.Names);
        Assert.Equal(WorldCustodyKind.Away, rows.Title(rows.Companions[0]).Kind);
    }

    [Fact]
    public void AConfirmedCommitCannotBeRolledBackByALaterMissingAnswer() {
        using var rows = new Rows(retained: true);
        rows.Host.SetPeerCallFault(instanceName: "row-b", fault: new ContradictingDestination(rows.B.Server));
        rows.Cross(rows.Home, rows.B);
        Assert.Equal(-1, Rows.SlotOf(rows.Home, rows.Companions[0]));
        Assert.True(Rows.SlotOf(rows.B, rows.Companions[0]) >= 0);
        var pending = Assert.Single(rows.Host.CaptureRow(rows.Home.Instance).InDoubtTransfers);
        Assert.True(pending.CommitConfirmed);
        Assert.False(pending.RollbackOnly);
        rows.Checkpoint("row-a");
        rows.Restart("row-a");
        Assert.Equal(-1, Rows.SlotOf(rows.Home, rows.Companions[0]));
    }

    [Fact]
    public void APartialSettlementKeepsTheLandedPlayersForwardingAcrossRecovery() {
        using var rows = new Rows(retained: true, admitHumans: true, cLandingRefusal: ordinal => ordinal == 1 ? "refuse companion" : null);
        rows.Home.Server.Body(index: 0)!.SetIntentSource(source: IntentSource.Idle);
        rows.Cross(rows.Home, rows.B);
        rows.Checkpoint("row-b");
        var playerSlot = Rows.SlotOf(rows.B, rows.Player);
        _ = rows.Host.EnqueueTransfer(
            actingPrincipal: rows.B.Server.Population.PeerPrincipal(index: playerSlot),
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-c"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-b",
            sourceSlot: playerSlot);
        rows.Step(3);
        Assert.True(Rows.SlotOf(rows.C, rows.Player) >= 0);
        Assert.True(Rows.SlotOf(rows.B, rows.Companions[0]) >= 0);
        Assert.Single(rows.Host.CaptureRow(rows.B.Instance).ForwardedBodies);
        var settlement = Assert.Single(rows.Records(rows.B).OfType<WorldCrossingRecord.Settlement>());
        Assert.Single(settlement.Forwarded);
        rows.Restart("row-b");
        Assert.Single(rows.Host.CaptureRow(rows.B.Instance).ForwardedBodies);
    }
    private sealed class ContradictingDestination(WorldServer destination) : IWorldPeerCall {
        public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(sourceAuthority, transferId);
        public void Acknowledge(string sourceAuthority, ulong transferId) => destination.AcknowledgeTransfer(sourceAuthority, transferId);
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => destination.ReserveTransfer(request);
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            status = destination.CommitTransfer(sourceAuthority, transferId, members, out reason);
            return WorldTransferStep.Answered;
        }
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = WorldTransferStatus.Missing;
            return true;
        }
        public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
            landed = null;
            return false;
        }
    }

    private sealed class ForgingHome : IWorldCustodyPeer {
        public IReadOnlyList<ulong?> ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination, WorldHopPhase phase) => incarnations.Select(_ => (ulong?)1UL).ToArray();
        public IReadOnlyList<WorldHopAnswer> RequestHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) => [];
        public WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker) => WorldCustodyAnswer.Uncertain;
        public bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source) => false;
    }
}
