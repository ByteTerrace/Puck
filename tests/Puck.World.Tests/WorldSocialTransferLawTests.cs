using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed partial class WorldSocialTransferLawTests {
    private static WorldSocialPolicy Policy(int capacity = 64) => new([new(WorldCellName.Parse("helpfulness"))],
        ImpressionCapacity: capacity, ImpressionsPerObserver: capacity, ReceiptCapacity: 128,
        EvidenceAttemptsPerTick: 128, ExpiredReceiptsPerTick: 128);
    private static WorldDefinition Document(int capacity = 64) => Fixtures.BuildDocument() with { StateRaw = new(Social: Policy(capacity)) };
    private static WorldInstanceHost Host(IWorldEmbodiedSeats? seats = null) => new(applicationStopping: CancellationToken.None, admitsSpawn: true, machineId: Guid.NewGuid(),
        resolver: new WorldSessionResolver(), seats: seats ?? WorldEmbodiedSeats.None, stateRoot: Directory.CreateTempSubdirectory("puck-social-host-").FullName);
    private static void Join(WorldServer server, int slot) => Assert.True(server.ApplySession(
        new SessionRequest.Join(WorldPrincipal.Seat(slot), slot, null, WorldProtocol.WireProtocolKey)).Accepted);
    private static WorldAuthorityCheckpoint Capture(WorldInstanceHost host, HostRow row) {
        Assert.True(row.Server.TryCaptureCheckpoint(host.CaptureRow(row.Instance), out var state, out var reason), reason);
        return state!;
    }
    private static WorldSocialMemory Memory(WorldInstanceHost host, HostRow row) => WorldSocialMemory.Restore(
        CompiledWorldSocialPolicy.Compile(row.Server.Definition.StateRaw!.Social!), Capture(host, row).Server.Social!);
    private static WorldSocialImpressionKey Key(WorldEntityAddress observer) => new(observer, new("witness", 9, 1), 0);
    private static WorldSocialEvidence Evidence(WorldEntityAddress observer, ulong tick) => new(Key(observer),
        new(new("witness", 9, 1), "help", 1), tick, FixedQ4816.One.Value, FixedQ4816.One.Value);
    private static WorldEntityAddress Seed(WorldInstanceHost host, HostRow row, int slot) {
        var mobility = row.Server.Population.EnsureMobility(slot, row.Server.AuthorityIdentity);
        var state = Capture(host, row); var memory = Memory(host, row);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, memory.Observe(Evidence(mobility.Incarnation, memory.EngineTick)));
        row.Server.RestoreCheckpoint(state with { Server = state.Server with { Social = memory.Capture() } });
        return mobility.Incarnation;
    }
    private static ulong Transfer(WorldInstanceHost host, string from, string to, int slot = 0,
        bool party = false, bool atomic = true, int? forceRefusal = null) {
        var id = host.EnqueueTransfer(from, party ? WorldInstanceHost.TransferScope.Party : WorldInstanceHost.TransferScope.Body,
            slot, WorldInstanceHost.TransferDestination.Existing(to), WorldPrincipal.Console,
            testForceJoinRefusalOrdinal: forceRefusal, partyAllOrNothing: atomic, fullPolicy: WorldTransferFullPolicy.Refuse);
        host.DrainPendingTransfers(); return id;
    }
    private static void NoHolds(WorldInstanceHost host, HostRow row) {
        var state = Capture(host, row);
        Assert.Empty(state.Server.Social!.FrozenObservers!); Assert.Empty(state.Server.Social.ImportReservations!);
        Assert.Empty(state.Escrow.Leases); Assert.Empty(state.HostRow.InDoubtTransfers);
    }

    [Fact]
    public void RepeatedOutAndBackPreservesIdentityEvidenceAndOtherObservers() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Join(a.Server, 1);
        Join(b.Server, 1); // Keep both worlds occupied so their ordinary empty-instance reaper does not retire them.
        var traveler = Seed(host, a, 0); var resident = Seed(host, a, 1);
        for (var round = 0; round < 64; round++) {
            Transfer(host, "a", "b");
            Assert.False(a.Server.Population.IsActive(0)); Assert.True(b.Server.Population.IsActive(0));
            Assert.Equal(traveler, b.Server.Population.ResolveIncarnation(0, b.Server.AuthorityIdentity));
            var away = Memory(host, b); Assert.True(away.TryRead(Key(traveler), out var impression));
            Assert.True(impression.Known); Assert.Equal(1UL, impression.IndependentEvents);
            Assert.Equal(WorldSocialEvidenceResult.Duplicate, away.Observe(Evidence(traveler, 0)));
            Assert.Equal(resident, Assert.Single(Memory(host, a).Capture().Impressions).Key.Observer);
            Transfer(host, "b", "a");
            Assert.Equal(2, Memory(host, a).ImpressionCount); Assert.Equal(0, Memory(host, b).ImpressionCount);
            NoHolds(host, a); NoHolds(host, b);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PartyCapacityDistinguishesAtomicFromIndependentMembers(bool atomic) {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Join(a.Server, 1);
        var first = Seed(host, a, 0); var second = Seed(host, a, 1);
        for (var slot = 1; slot < WorldBodiesLimits.LocalSeatCount; slot++) { Join(b.Server, slot); }
        Transfer(host, "a", "b", party: true, atomic: atomic);
        Assert.Equal(atomic, a.Server.Population.IsActive(0)); Assert.True(a.Server.Population.IsActive(1));
        Assert.Equal(!atomic, b.Server.Population.IsActive(0));
        Assert.Equal(atomic ? 2 : 1, Memory(host, a).ImpressionCount);
        Assert.Equal(atomic ? 0 : 1, Memory(host, b).ImpressionCount);
        Assert.Contains(Memory(host, a).Capture().Impressions, row => row.Key.Observer == second);
        if (!atomic) { Assert.Equal(first, Assert.Single(Memory(host, b).Capture().Impressions).Key.Observer); }
        NoHolds(host, a); NoHolds(host, b);
    }

    [Fact]
    public void LatePartyRefusalRestoresEveryBodyAndMemoryAndReleasesDestinationQuota() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Join(a.Server, 1);
        Seed(host, a, 0); Seed(host, a, 1); var before = Memory(host, a).Capture();
        Transfer(host, "a", "b", party: true, forceRefusal: 1);
        Assert.True(a.Server.Population.IsActive(0)); Assert.True(a.Server.Population.IsActive(1));
        Assert.False(b.Server.Population.IsActive(0)); Assert.False(b.Server.Population.IsActive(1));
        Assert.Equal(before.Impressions, Memory(host, a).Capture().Impressions);
        Assert.Equal(before.Receipts, Memory(host, a).Capture().Receipts);
        Assert.Equal(0, Memory(host, b).ImpressionCount); NoHolds(host, a); NoHolds(host, b);
        Transfer(host, "a", "b", party: true);
        Assert.Equal(2, Memory(host, b).ImpressionCount); Assert.Equal(0, Memory(host, a).ImpressionCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostCommitAnswerRetainsFrozenSourceUntilExactResolution(bool applied) {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); var observer = Seed(host, a, 0);
        var fault = new LostAnswer(b.Server, applied); host.SetPeerCallFault("b", fault);
        Transfer(host, "a", "b");
        var frozen = Memory(host, a); Assert.True(frozen.IsObserverFrozen(observer));
        Assert.Equal(WorldSocialEvidenceResult.ObserverFrozen, frozen.Observe(Evidence(observer, 0)));
        Assert.Single(Capture(host, a).HostRow.InDoubtTransfers);
        Assert.Equal(applied, b.Server.Population.IsActive(0));
        Assert.Equal(applied ? 1 : 0, Memory(host, b).ImpressionCount);
        // Current-format checkpoints must retain both sides' transaction ownership.
        foreach (var row in new[] { a, b }) {
            Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(Capture(host, row)), out var decoded, out var reason), reason);
            row.Server.RestoreCheckpoint(decoded!);
        }
        host.DrainPendingTransfers();
        Assert.True(b.Server.Population.IsActive(0)); Assert.Equal(1, Memory(host, b).ImpressionCount);
        Assert.Equal(0, Memory(host, a).ImpressionCount); Assert.Equal(applied ? 1 : 2, fault.CommitCalls);
        NoHolds(host, a); NoHolds(host, b);
    }

    [Fact]
    public void MissingDestinationDoesNotDiscardAnOccupiedSourceSlotsRecoveryOrRetryCommit() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Join(a.Server, 1);
        var first = Seed(host, a, 0); var second = Seed(host, a, 1);
        var fault = new LostAnswer(b.Server, false); host.SetPeerCallFault("b", fault);
        var id = Transfer(host, "a", "b", party: true);
        b.Server.AbortTransfer(a.Server.AuthorityIdentity, id);
        Join(a.Server, 1); // A new occupant must not be overwritten by rollback.
        host.DrainPendingTransfers();
        var pending = Assert.Single(Capture(host, a).HostRow.InDoubtTransfers);
        Assert.True(pending.RollbackOnly); Assert.Equal(2, pending.MemberCount);
        Assert.Equal(1, Assert.Single(pending.Landed).SourceSlot); Assert.Single(pending.CommitMembers);
        Assert.False(Memory(host, a).IsObserverFrozen(first)); Assert.True(Memory(host, a).IsObserverFrozen(second));
        Assert.True(a.Server.Population.IsActive(0)); Assert.Equal(1, fault.CommitCalls);
        fault.StatusOverride = WorldTransferStatus.Committed;
        host.DrainPendingTransfers(); host.DrainPendingTransfers();
        Assert.Single(Capture(host, a).HostRow.InDoubtTransfers);
        Assert.True(Memory(host, a).IsObserverFrozen(second)); Assert.Equal(1, fault.CommitCalls);
        // Round-trip the partial rollback through the authority wire and host reconstruction.
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(Capture(host, a)), out var decoded, out var reason), reason);
        using var restoredHost = Host(); using var restoredA = HostRow.Build("a", Document()); using var restoredB = HostRow.Build("b", Document());
        restoredA.Server.RestoreCheckpoint(decoded!); restoredB.Server.RestoreCheckpoint(Capture(host, b));
        restoredHost.Admit(restoredA.Instance); restoredHost.Admit(restoredB.Instance);
        var restoredFault = new LostAnswer(restoredB.Server, false); restoredHost.SetPeerCallFault("b", restoredFault);
        restoredHost.RestoreRow(restoredA.Instance, decoded!.HostRow);
        Assert.Single(Capture(restoredHost, restoredA).HostRow.InDoubtTransfers);
        Assert.True(restoredA.Server.Population.TryDetachSeatForTransfer(1, out _));
        restoredHost.DrainPendingTransfers();
        Assert.Equal(0, restoredFault.CommitCalls); Assert.True(restoredA.Server.Population.IsActive(1));
        Assert.Equal(second, restoredA.Server.Population.ResolveIncarnation(1, restoredA.Server.AuthorityIdentity));
        Assert.Equal(2, Memory(restoredHost, restoredA).ImpressionCount);
        NoHolds(restoredHost, restoredA); NoHolds(restoredHost, restoredB);
    }

    private sealed class LostAnswer(WorldServer destination, bool applyFirst) : IWorldPeerCall {
        public int CommitCalls { get; private set; }
        public WorldTransferStatus? StatusOverride { get; set; }
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => destination.ReserveTransfer(request);
        public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(sourceAuthority, transferId);
        public void Acknowledge(string sourceAuthority, ulong transferId) => destination.AcknowledgeTransfer(sourceAuthority, transferId);
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = StatusOverride ?? destination.TransferStatus(sourceAuthority, transferId); return true;
        }
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members,
            out bool accepted, out string reason) {
            CommitCalls++;
            accepted = false; reason = "injected lost answer";
            if (applyFirst || CommitCalls > 1) { accepted = destination.CommitTransfer(sourceAuthority, transferId, members, out reason); }
            return CommitCalls == 1 ? WorldTransferStep.Unreachable : WorldTransferStep.Answered;
        }
    }
}
