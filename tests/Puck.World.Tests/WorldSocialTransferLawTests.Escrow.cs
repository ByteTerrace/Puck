using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    private static WorldTransferReservationRequest Request(params WorldSocialObserverImport[] imports) => new(
        17, "upstream", 240, 0, 480, "east", null, true, false,
        imports.Select((incoming, slot) => new WorldTransferReservationMember(WorldPrincipal.Console, slot, null,
            IntentSource.Live, default, 0, new(incoming.Observer, 1), incoming.Memory)).ToArray());
    private static WorldSocialObserverImport Incoming(int observer) {
        var address = new WorldEntityAddress("origin", observer, 1);
        var bank = new WorldSocialMemory(CompiledWorldSocialPolicy.Compile(Policy()));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(address, 0)));
        return new(address, bank.Policy, bank.CaptureObserver(address));
    }
    private static WorldTransferCommitMember Arrival() => new(null, false, string.Empty, default, default, default, default);

    [Fact]
    public void EscrowOwnsIncomingHistoriesAndReplySlotsAcrossRetriesAndCheckpointRestore() {
        using var host = Host(); using var row = HostRow.Build("destination", Document()); host.Admit(row.Instance);
        var incoming = Incoming(0); var request = Request(incoming);
        var reply = row.Server.ReserveTransfer(request); Assert.True(reply.Accepted, reply.Reason);
        var reserved = Memory(host, row);
        Assert.Equal(1, reserved.ReservedImpressionCount); Assert.Equal(1, reserved.ReservedReceiptCount);
        Assert.Equal(WorldSocialEvidenceResult.ObserverReserved, reserved.Observe(Evidence(incoming.Observer, 0)));
        if (reply.BodyIndices is IList<int> mutableSlots && !mutableSlots.IsReadOnly) { mutableSlots[0] = 99; }
        ((WorldSocialImpressionCheckpoint[])incoming.Memory.Impressions)[0] = default;
        var retry = row.Server.ReserveTransfer(Request(Incoming(0))); Assert.True(retry.Accepted, retry.Reason);
        Assert.Equal(0, Assert.Single(retry.BodyIndices));
        var captured = Capture(host, row);
        var invalid = captured with { Server = captured.Server with { Social = captured.Server.Social! with { ImportReservations = [] } } };
        var hash = WorldRuntimeStateHash.HashAuthoritative(row.Server, 0);
        Assert.Throws<InvalidOperationException>(() => row.Server.RestoreCheckpoint(invalid));
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(row.Server, 0));
        Assert.Single(Capture(host, row).Escrow.Leases);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(captured), out var decoded, out var reason), reason);
        row.Server.RestoreCheckpoint(decoded!);
        // Mutating a captured request must not mutate the restored lease either.
        ((WorldSocialImpressionCheckpoint[])decoded!.Escrow.Leases[0].Request.Members[0].Social!.Impressions)[0] = default;
        Assert.True(row.Server.CommitTransfer(request.SourceAuthority, request.TransferId, [Arrival()], out reason), reason);
        Assert.True(row.Server.Population.IsActive(0)); Assert.Equal(1, Memory(host, row).ImpressionCount);
        Assert.True(row.Server.CommitTransfer(request.SourceAuthority, request.TransferId, [Arrival()], out reason), reason);
        Assert.Equal(1, Memory(host, row).ReceiptCount); NoHolds(host, row);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RefusedSocialReservationHasNoPartialBodyOrMemoryLease(int refusal) {
        var document = refusal == 0 ? Fixtures.BuildDocument() : Document(refusal == 1 ? 1 : 64);
        if (refusal == 2) { document = document with { StateRaw = new(Social: Policy() with { DirectWeight = 0.5m }) }; }
        using var host = Host(); using var row = HostRow.Build("destination", document); host.Admit(row.Instance);
        var request = Request(Incoming(0), Incoming(1));
        var reply = row.Server.ReserveTransfer(request); Assert.False(reply.Accepted);
        Assert.Contains("social", reply.Reason); Assert.Empty(Capture(host, row).Escrow.Leases);
        Assert.False(row.Server.Population.IsActive(0)); Assert.False(row.Server.Population.IsActive(1));
        if (refusal != 0) { Assert.Equal(0, Memory(host, row).ReservedObserverCount); }
        // Ordinary no-memory arrivals work without a social policy; matching semantics can use different quotas.
        var control = refusal is 0 or 2
            ? request with { Members = [request.Members[0] with { Social = null }] }
            : Request(Incoming(0));
        var admitted = row.Server.ReserveTransfer(control); Assert.True(admitted.Accepted, admitted.Reason);
        row.Server.AbortTransfer(control.SourceAuthority, control.TransferId);
        Assert.Empty(Capture(host, row).Escrow.Leases);
        if (refusal != 0) { NoHolds(host, row); }
    }

    [Fact]
    public void MalformedLastHistoryAndRefusedLastCommitCannotPartiallyLandAParty() {
        using var host = Host(); using var row = HostRow.Build("destination", Document()); host.Admit(row.Instance);
        var request = Request(Incoming(0), Incoming(1));
        var last = request.Members[1];
        var wrong = last.Social! with { Impressions = [last.Social!.Impressions[0] with { Key = Key(new("other", 99, 1)) }] };
        Assert.False(row.Server.ReserveTransfer(request with { Members = [request.Members[0], last with { Social = wrong }] }).Accepted);
        NoHolds(host, row);
        Assert.True(row.Server.ReserveTransfer(request).Accepted);
        Assert.False(row.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
            [Arrival(), Arrival() with { HasMappedArrival = true, BodyMotionProgramName = "missing" }], out _));
        Assert.False(row.Server.Population.IsActive(0)); Assert.False(row.Server.Population.IsActive(1));
        Assert.Equal(0, Memory(host, row).ImpressionCount); NoHolds(host, row);
        Assert.True(row.Server.ReserveTransfer(request).Accepted);
        Assert.True(row.Server.CommitTransfer(request.SourceAuthority, request.TransferId, [Arrival(), Arrival()], out var reason), reason);
        Assert.Equal(2, Memory(host, row).ImpressionCount); NoHolds(host, row);
    }

    [Fact]
    public void FederationReservationWireCarriesPrivateHistoriesAndRefusesEveryTruncatedPrefix() {
        var request = Request(Incoming(0), Incoming(1)); var bytes = WorldFederationCodec.EncodeReservation(request);
        var defaults = Fixtures.BuildDocument().PlayerDefaults;
        Assert.True(WorldFederationCodec.TryDecodeReservation(bytes, defaults, out var decoded, out var failure), failure.ToString());
        Assert.Equal(2, decoded!.Members.Count);
        for (var index = 0; index < 2; index++) {
            Assert.Equal(request.Members[index].Mobility, decoded.Members[index].Mobility);
            Assert.Equal(request.Members[index].Social!.Impressions, decoded.Members[index].Social!.Impressions);
            Assert.Equal(request.Members[index].Social!.Receipts, decoded.Members[index].Social!.Receipts);
        }
        for (var length = 0; length < bytes.Length; length++) {
            Assert.False(WorldFederationCodec.TryDecodeReservation(bytes.AsSpan(0, length), defaults, out _, out _));
        }
        using var host = Host();
        using var denied = HostRow.Build("denied", Document()); host.Admit(denied.Instance);
        Assert.False(denied.Server.ReserveTransfer(decoded).Accepted);
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 2, NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var row = HostRow.Build("destination", document); host.Admit(row.Instance);
        var reserved = row.Server.ReserveTransfer(decoded); Assert.True(reserved.Accepted, reserved.Reason);
        Assert.True(row.Server.CommitTransfer(decoded.SourceAuthority, decoded.TransferId, [Arrival(), Arrival()], out var reason), reason);
        Assert.Equal(2, Memory(host, row).ImpressionCount);
    }
}
