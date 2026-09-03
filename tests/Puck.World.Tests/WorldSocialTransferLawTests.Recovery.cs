using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    private static WorldAuthorityCheckpoint WireRoundTrip(WorldAuthorityCheckpoint checkpoint) {
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint), out var decoded, out var reason), reason);
        return decoded!;
    }

    [Theory]
    [InlineData(WorldTransferStatus.Reserved)]
    [InlineData(WorldTransferStatus.Committed)]
    [InlineData(WorldTransferStatus.Missing)]
    public void LateDestinationResolvesAfterRepeatedRestartsWithoutLosingFrozenMemory(WorldTransferStatus status) {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0);
        var observer = Seed(host, a, 0);
        host.SetPeerCallFault("b", new LostAnswer(b.Server, status == WorldTransferStatus.Committed));
        var id = Transfer(host, "a", "b");
        if (status == WorldTransferStatus.Missing) { b.Server.AbortTransfer(a.Server.AuthorityIdentity, id); }
        var checkpoint = WireRoundTrip(Capture(host, a));
        var targetCheckpoint = WireRoundTrip(Capture(host, b));
        for (var restart = 0; restart < 16; restart++) {
            using var waitingHost = Host(); using var waitingA = HostRow.Build("a", Document());
            waitingA.Server.RestoreCheckpoint(checkpoint); waitingHost.Admit(waitingA.Instance);
            waitingHost.RestoreRow(waitingA.Instance, checkpoint.HostRow);
            // Installing the same slice twice replaces its own records, never duplicates a transaction.
            waitingHost.RestoreRow(waitingA.Instance, checkpoint.HostRow);
            for (var tick = 0; tick < 32; tick++) { waitingHost.DrainPendingTransfers(); }
            Assert.Single(Capture(waitingHost, waitingA).HostRow.InDoubtTransfers);
            Assert.False(waitingA.Server.Population.IsActive(0)); Assert.True(Memory(waitingHost, waitingA).IsObserverFrozen(observer));
            var recaptured = Capture(waitingHost, waitingA);
            Assert.Equal(WorldAuthorityCheckpointCodec.Encode(checkpoint), WorldAuthorityCheckpointCodec.Encode(recaptured));
            checkpoint = WireRoundTrip(recaptured);
        }
        using var restoredHost = Host(); using var restoredA = HostRow.Build("a", Document()); using var restoredB = HostRow.Build("b", Document());
        restoredA.Server.RestoreCheckpoint(checkpoint); restoredB.Server.RestoreCheckpoint(targetCheckpoint);
        restoredHost.Admit(restoredA.Instance); restoredHost.RestoreRow(restoredA.Instance, checkpoint.HostRow);
        restoredHost.DrainPendingTransfers(); Assert.Single(Capture(restoredHost, restoredA).HostRow.InDoubtTransfers);
        restoredHost.Admit(restoredB.Instance); restoredHost.RestoreRow(restoredB.Instance, targetCheckpoint.HostRow);
        restoredHost.DrainPendingTransfers();
        var committed = status != WorldTransferStatus.Missing;
        Assert.Equal(committed, restoredB.Server.Population.IsActive(0)); Assert.Equal(!committed, restoredA.Server.Population.IsActive(0));
        var owner = committed ? restoredB : restoredA;
        Assert.Equal(observer, owner.Server.Population.ResolveIncarnation(0, owner.Server.AuthorityIdentity));
        Assert.Equal(1UL, Assert.Single(Memory(restoredHost, owner).Capture().Impressions).IndependentEvents);
        NoHolds(restoredHost, restoredA); NoHolds(restoredHost, restoredB);
    }

    [Fact]
    public void UnavailableRemoteAddressAndExactPayloadSurviveWithoutPerDrainAllocation() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); var observer = Seed(host, a, 0);
        host.SetPeerCallFault("b", new LostAnswer(b.Server, false)); Transfer(host, "a", "b");
        var state = Capture(host, a); var pending = Assert.Single(state.HostRow.InDoubtTransfers) with {
            TargetAuthority = "unavailable-remote", TargetEndpoint = "127.0.0.1:49123", TargetName = "remote-destination",
            TargetDefinitionJson = WorldDefinitionSerialization.Serialize(b.Server.Definition)
        };
        pending = pending with { Continuation = pending.Continuation! with { DestinationName = "authored-destination", ScopeKey = "group:companions", GenerationId = 37 } };
        state = WireRoundTrip(state with { HostRow = state.HostRow with { InDoubtTransfers = [pending] } });
        using var waiting = Host(); using var source = HostRow.Build("a", Document());
        source.Server.RestoreCheckpoint(state); waiting.Admit(source.Instance); waiting.RestoreRow(source.Instance, state.HostRow);
        for (var warm = 0; warm < 32; warm++) { waiting.DrainPendingTransfers(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < 4096; tick++) { waiting.DrainPendingTransfers(); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        var recaptured = WireRoundTrip(Capture(waiting, source));
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(state), WorldAuthorityCheckpointCodec.Encode(recaptured));
        Assert.True(Memory(waiting, source).IsObserverFrozen(observer)); Assert.False(source.Server.Population.IsActive(0));
    }

    [Fact]
    public void SameRegistryNameCannotResolveAnotherAuthoritysRecovery() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); var observer = Seed(host, a, 0);
        host.SetPeerCallFault("b", new LostAnswer(b.Server, false)); Transfer(host, "a", "b");
        var state = WireRoundTrip(Capture(host, a)); var targetState = WireRoundTrip(Capture(host, b));
        using var waiting = Host(); using var source = HostRow.Build("a", Document()); using var impostor = HostRow.Build("different-authority", Document());
        using var wrongName = HostRow.Wrap("b", impostor.Server, impostor.Machines);
        source.Server.RestoreCheckpoint(state); waiting.Admit(source.Instance); waiting.Admit(wrongName.Instance);
        waiting.RestoreRow(source.Instance, state.HostRow); waiting.DrainPendingTransfers();
        Assert.Single(Capture(waiting, source).HostRow.InDoubtTransfers);
        Assert.True(Memory(waiting, source).IsObserverFrozen(observer)); Assert.False(source.Server.Population.IsActive(0));
        using var actual = HostRow.Build("b", Document()); actual.Server.RestoreCheckpoint(targetState);
        using var renamed = HostRow.Wrap("late-b", actual.Server, actual.Machines);
        waiting.Admit(renamed.Instance); waiting.DrainPendingTransfers();
        Assert.True(actual.Server.Population.IsActive(0)); Assert.False(impostor.Server.Population.IsActive(0));
        Assert.Empty(Capture(waiting, source).HostRow.InDoubtTransfers); Assert.Equal(0, Memory(waiting, source).ImpressionCount);
    }

    [Fact]
    public void SourceBoundarySurvivesCaptureAndClampsAnAbortedCrossingAfterRestart() {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Seed(host, a, 0);
        var point = new FixedVector3(FixedQ4816.FromInteger(17), FixedQ4816.FromInteger(3), FixedQ4816.FromInteger(-9));
        var frame = new WorldFaceFrame(point, new(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero),
            new(FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.Zero), new(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One),
            FixedQ4816.One, FixedQ4816.One, FixedQ4816.Zero);
        host.SetPeerCallFault("b", new LostAnswer(b.Server, false));
        var id = host.EnqueueTransfer("a", WorldInstanceHost.TransferScope.Body, 0, WorldInstanceHost.TransferDestination.Existing("b"),
            WorldPrincipal.Console, adjacencyCounterpart: "reciprocal", sourceCrossingPoint: point, sourceFrame: frame, border: "adjacency/exit");
        host.DrainPendingTransfers(); b.Server.AbortTransfer(a.Server.AuthorityIdentity, id);
        var state = WireRoundTrip(Capture(host, a));
        var context = Assert.Single(state.HostRow.InDoubtTransfers).Continuation!;
        Assert.Equal(frame, context.SourceFrame); Assert.Equal(point, context.SourceCrossingPoint);
        Assert.Equal("reciprocal", context.AdjacencyCounterpart); Assert.Equal("adjacency/exit", context.Border);
        using var waiting = Host(); using var source = HostRow.Build("a", Document()); using var target = HostRow.Build("b", Document());
        source.Server.RestoreCheckpoint(state); target.Server.RestoreCheckpoint(Capture(host, b));
        waiting.Admit(source.Instance); waiting.Admit(target.Instance);
        waiting.RestoreRow(source.Instance, state.HostRow); waiting.DrainPendingTransfers();
        Assert.Equal(point - frame.Normal * FixedQ4816.FromRawBits(1), source.Server.Population.EntryBody(0)!.FixedPosition);
        NoHolds(waiting, source);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void MalformedRecoveryRefusesBeforeReplacingHostState(int fault) {
        using var host = Host(); using var a = HostRow.Build("a", Document()); using var b = HostRow.Build("b", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); Join(a.Server, 0); Seed(host, a, 0);
        host.SetPeerCallFault("b", new LostAnswer(b.Server, false)); Transfer(host, "a", "b");
        var state = Capture(host, a); var good = Assert.Single(state.HostRow.InDoubtTransfers);
        var bad = fault switch {
            0 => good with { CommitMembers = [] },
            1 => good with { SourceInstance = "wrong-source" },
            2 => good with { Landed = [good.Landed[0] with { SourceSlot = -1 }] },
            3 => good with { TargetEndpoint = "not-an-endpoint" },
            4 => good with { Continuation = good.Continuation! with { CohortSlots = [1] } },
            6 => good with { RollbackOnly = true, CommitConfirmed = true },
            7 => good with { Landed = [good.Landed[0] with { FollowedSeatMask = 0x80 }] },
            8 => good with { TargetEndpoint = "127.0.0.1:49123", TargetDefinitionJson = "{"u8.ToArray() },
            _ => good
        };
        var invalid = state.HostRow with { IsPaused = true, NextTransferId = 9876, InDoubtTransfers = fault == 5 ? [good, bad] : [bad] };
        Assert.Throws<ArgumentException>(() => host.RestoreRow(a.Instance, invalid));
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(state), WorldAuthorityCheckpointCodec.Encode(Capture(host, a)));
        // Positive control: a well-formed reinstall preserves the pending transaction and can still commit it.
        host.RestoreRow(a.Instance, state.HostRow); host.DrainPendingTransfers();
        Assert.True(b.Server.Population.IsActive(0)); NoHolds(host, a); NoHolds(host, b);
    }
}
