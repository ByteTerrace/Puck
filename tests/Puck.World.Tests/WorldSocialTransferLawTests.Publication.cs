using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public void ConfirmedCommitSurvivesPartialPublicationAndRestart(bool ambiguous, bool restart, bool throwAfterWrite) {
        var seats = new PublicationSeats { ThrowAfterWrite = throwAfterWrite };
        using var host = Host(seats); using var a = HostRow.Build("boot", Document()); using var b = HostRow.Build("b", Document());
        host.AdmitBoot(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Join(a.Server, 1);
        Seed(host, a, 0); Seed(host, a, 1);
        var fault = new PublicationPeer(b.Server) { LoseFirstCommit = ambiguous };
        host.SetPeerCallFault("b", fault);
        seats.FailPublication = true;
        Transfer(host, "boot", "b", party: true);
        if (ambiguous) { host.DrainPendingTransfers(); }
        var state = WireRoundTrip(Capture(host, a));
        var pending = Assert.Single(state.HostRow.InDoubtTransfers);
        Assert.True(pending.CommitConfirmed);
        Assert.False(pending.RollbackOnly);
        Assert.Equal(new byte[] { 1, 2 }, pending.Landed.Select(static member => member.FollowedSeatMask));
        Assert.Equal(2, state.Server.Social!.FrozenObservers!.Count);
        Assert.Equal("b", seats.RoutedEndpoint(0)!.Identity);
        Assert.Equal(throwAfterWrite ? "b" : "boot", seats.RoutedEndpoint(1)!.Identity);
        Assert.True(seats.IsOccupied(0)); Assert.True(seats.IsOccupied(1));
        Assert.Equal(0, fault.Acknowledgements);
        var duplicateFollower = pending with { Landed = [pending.Landed[0], pending.Landed[1] with { FollowedSeatMask = 1 }] };
        Assert.Throws<ArgumentException>(() => host.RestoreRow(a.Instance, state.HostRow with { InDoubtTransfers = [duplicateFollower] }));
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(state), WorldAuthorityCheckpointCodec.Encode(Capture(host, a)));
        if (!throwAfterWrite) {
            var statusesBeforeRetries = fault.StatusCalls;
            var commitsBeforeRetries = fault.CommitCalls;
            fault.RefuseFurtherProtocol = true;
            for (var attempt = 0; attempt < 64; attempt++) { host.DrainPendingTransfers(); }
            Assert.Equal(statusesBeforeRetries, fault.StatusCalls);
            Assert.Equal(commitsBeforeRetries, fault.CommitCalls);
            Assert.Equal(0, seats.Vacates);
            Assert.Equal(WorldAuthorityCheckpointCodec.Encode(state), WorldAuthorityCheckpointCodec.Encode(Capture(host, a)));
        }

        if (restart) {
            var resumedSeats = new PublicationSeats();
            using var resumed = Host(resumedSeats); using var source = HostRow.Build("boot", Document()); using var target = HostRow.Build("b", Document());
            source.Server.RestoreCheckpoint(state); target.Server.RestoreCheckpoint(WireRoundTrip(Capture(host, b)));
            resumed.AdmitBoot(source.Instance); resumed.Admit(target.Instance);
            var neverRequery = new PublicationPeer(target.Server) { RefuseFurtherProtocol = true };
            resumed.SetPeerCallFault("b", neverRequery);
            resumed.RestoreRow(source.Instance, state.HostRow);
            resumed.DrainPendingTransfers();
            Assert.Equal(0, neverRequery.StatusCalls); Assert.Equal(0, neverRequery.CommitCalls);
            Assert.Equal(1, neverRequery.Acknowledgements);
            AssertPublication(resumed, source, target, resumedSeats);
        } else {
            var statuses = fault.StatusCalls; var commits = fault.CommitCalls;
            fault.RefuseFurtherProtocol = true;
            seats.FailPublication = false;
            host.DrainPendingTransfers();
            Assert.Equal(statuses, fault.StatusCalls); Assert.Equal(commits, fault.CommitCalls);
            Assert.Equal(1, fault.Acknowledgements);
            AssertPublication(host, a, b, seats);
        }
    }

    private static void AssertPublication(WorldInstanceHost host, HostRow source, HostRow target, PublicationSeats seats) {
        NoHolds(host, source); NoHolds(host, target);
        Assert.Equal(0, Memory(host, source).ImpressionCount); Assert.Equal(2, Memory(host, target).ImpressionCount);
        Assert.Equal(0, seats.Vacates);
        for (var slot = 0; slot < 2; slot++) {
            Assert.True(seats.IsOccupied(slot)); Assert.Equal("b", seats.RoutedEndpoint(slot)!.Identity);
            Assert.False(source.Server.Population.IsActive(slot)); Assert.True(target.Server.Population.IsActive(slot));
        }
    }

    private sealed class PublicationPeer(WorldServer destination) : IWorldPeerCall {
        public bool LoseFirstCommit { get; init; }
        public bool RefuseFurtherProtocol { get; set; }
        public int CommitCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int Acknowledgements { get; private set; }
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => destination.ReserveTransfer(request);
        public void Abort(string sourceAuthority, ulong transferId) => throw new InvalidOperationException("Confirmed commit must not abort.");
        public void Acknowledge(string sourceAuthority, ulong transferId) { Acknowledgements++; destination.AcknowledgeTransfer(sourceAuthority, transferId); }
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            StatusCalls++;
            status = RefuseFurtherProtocol ? WorldTransferStatus.Missing : destination.TransferStatus(sourceAuthority, transferId);
            return true;
        }
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
            CommitCalls++;
            if (RefuseFurtherProtocol) { throw new InvalidOperationException("Confirmed commit must not recommit."); }
            accepted = destination.CommitTransfer(sourceAuthority, transferId, members, out reason);
            return LoseFirstCommit && CommitCalls == 1 ? WorldTransferStep.Unreachable : WorldTransferStep.Answered;
        }
    }

    private sealed class PublicationSeats : IWorldEmbodiedSeats {
        private readonly WorldAuthorityEndpoint?[] m_endpoints = new WorldAuthorityEndpoint?[2];
        private readonly WorldEntityAddress[] m_entities = new WorldEntityAddress[2];
        private readonly bool[] m_occupied = [true, true];
        public int SeatCount => 2;
        public bool FailPublication { get; set; }
        public bool ThrowAfterWrite { get; init; }
        public int Vacates { get; private set; }
        public WorldAuthorityEndpoint? RoutedEndpoint(int slot) => m_endpoints[slot];
        public WorldEntityAddress RoutedEntity(int slot) => m_entities[slot];
        public bool IsOccupied(int slot) => m_occupied[slot];
        public void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity) {
            var fail = FailPublication && slot == 1 && endpoint.Identity != "boot";
            if (fail && !ThrowAfterWrite) { throw new IOException("injected route publication failure"); }
            m_endpoints[slot] = endpoint; m_entities[slot] = entity;
            if (fail) { throw new IOException("injected post-publication failure"); }
        }
        public bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement) {
            if (!ReferenceEquals(m_endpoints[slot], expectedEndpoint)) { return false; }
            m_entities[slot] = replacement; return true;
        }
        public bool VacateSeat(int slot) { Vacates++; m_occupied[slot] = false; return true; }
        public bool OccupySeat(int slot, WorldIdentity? profile) { m_occupied[slot] = true; return true; }
        public void ClearHeld(int slot) { }
        public void ClearAnalog() { }
        public void AdvanceSeatViews(float deltaSeconds) { }
        public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) { }
        public void ConfigureLeave(Func<int, WorldPrincipal, bool> leave) { }
    }
}
