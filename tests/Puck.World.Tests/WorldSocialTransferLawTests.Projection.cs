using System.Security.Cryptography;
using Puck.Attestation;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Fact]
    public async Task LocalTravelerProjectionSeedsCurrentOwnerAndInvalidatesOnTransfer() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = 5, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        using var c = HostRow.Build("c", document);
        host.Admit(a.Instance); host.Admit(b.Instance); host.Admit(c.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        Transfer(host, "a", "b", 4);
        var route = Assert.Single(Capture(host, a).HostRow.ForwardedBodies);
        using var arm = new WorldLocalForwardedAuthority(b.Server, "b", route.SourceAuthority, route.Mobility);
        using var output = new OneWayProjectionStream();
        using var deadline = Laws.SocketDeadline();
        Assert.NotNull(await arm.StreamProjectionAsync(output, WorldDisclosureTier.Frames, 64, deadline.Token));
        Assert.NotNull(await arm.StreamProjectionAsync(output, WorldDisclosureTier.Replica, 0, deadline.Token));
        Assert.Equal(0, output.Length);
        var streaming = arm.StreamProjectionAsync(output, WorldDisclosureTier.Replica, 64, deadline.Token);
        Assert.False(streaming.IsCompleted);
        host.EnqueueTransfer("b", WorldInstanceHost.TransferScope.Body, 4, WorldInstanceHost.TransferDestination.Existing("c"), b.Server.Population.PeerPrincipal(4));
        host.DrainPendingTransfers(); host.StepInstances(Fixtures.StepTicks);
        Assert.Null(await streaming.WaitAsync(deadline.Token));
        using var input = new MemoryStream(output.ToArray());
        var seed = await WorldFederationCodec.ReadResponseAsync(input, deadline.Token);
        Assert.Equal((byte)WorldFederationResponse.Route, seed.Kind);
        Assert.True(WorldFederationCodec.TryDecodeRoute(seed.Body.Span, out var described, out _));
        Assert.Equal(b.Server.AuthorityIdentity, described.Entity.Authority);
        Assert.Equal((byte)WorldFederationResponse.Definition, (await WorldFederationCodec.ReadResponseAsync(input, deadline.Token)).Kind);
        Assert.Equal((byte)WorldFederationResponse.Snapshot, (await WorldFederationCodec.ReadResponseAsync(input, deadline.Token)).Kind);
        Assert.Equal((byte)WorldFederationResponse.ProjectionInvalidated, (await WorldFederationCodec.ReadResponseAsync(input, deadline.Token)).Kind);
    }

    private sealed class OneWayProjectionStream : MemoryStream {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task TravelerProjectionFollowsLocalOnwardTransfersAndDisposalStopsDelivery() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = 5, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        using var c = HostRow.Build("c", document); using var d = HostRow.Build("d", document);
        foreach (var row in new[] { a, b, c, d }) { host.Admit(row.Instance); }
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var mobility = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        Transfer(host, "a", "b", 4);
        using var oracle = new LocalKeySigningOracle(ECDsa.Create(ECCurve.NamedCurves.nistP256), a.Server.AuthorityIdentity, TimeSpan.FromMinutes(5));
        var trust = new WorldAdmissionEntry(oracle.Domain, oracle.Subject, WorldAdmissionTrustMode.SignsDirectly,
            AttestationAlgorithms.EcdsaP256Sha256, Convert.ToBase64String(oracle.PublicKeySubjectPublicKeyInfo), []);
        var security = new WorldAttestedAuthenticator(() => [trust], oracle);
        using var door = new WorldPeerHost(b.Server, authenticator: security); door.Start("127.0.0.1:0");
        using var entry = new WorldRemoteAuthority(door.ListenEndpoint!, document, security, a.Server.AuthorityIdentity,
            expectedAuthority: b.Server.AuthorityIdentity);
        var credential = new WorldRemoteRouteCredential(4, a.Server.AuthorityIdentity, mobility.Advance());
        var sink = new TravelerProjectionCapture();
        using var projection = new WorldRemoteAuthority(door.ListenEndpoint!, document, security, a.Server.AuthorityIdentity,
            entry, credential);
        using var lease = projection.AttachSink(sink);
        using var deadline = Laws.SocketDeadline();
        async Task WaitFor(HostRow row) {
            while (sink.Latest?.Authority != row.Server.AuthorityIdentity) {
                deadline.Token.ThrowIfCancellationRequested(); door.DrainPending(); host.StepInstances(Fixtures.StepTicks);
                await Task.Delay(5, deadline.Token);
            }
            Assert.Equal(row.Server.Population.Generation(4), Assert.Single(sink.Latest!.Entries.ToArray(), e => e.Index == 4).Generation);
            Assert.Equal(row.Server.AuthorityIdentity, projection.Authority);
            Assert.Equal(door.ListenEndpoint, projection.Endpoint);
        }
        await WaitFor(b);
        foreach (var (from, to) in new[] { (b, c), (c, d) }) {
            host.EnqueueTransfer(from.Instance.Name, WorldInstanceHost.TransferScope.Body, 4,
                WorldInstanceHost.TransferDestination.Existing(to.Instance.Name), from.Server.Population.PeerPrincipal(4));
            host.DrainPendingTransfers();
            await WaitFor(to);
        }
        lease.Dispose();
        // Let any already-dispatched callback finish; subsequent ticks must not reach the retired lease.
        await Task.Delay(100, deadline.Token);
        var stopped = sink.Latest;
        for (var i = 0; i < 20; i++) { host.StepInstances(Fixtures.StepTicks); await Task.Delay(5, deadline.Token); }
        Assert.Same(stopped, sink.Latest);
    }

    [Theory]
    [InlineData(0, 1, true)] [InlineData(1, 64, true)] [InlineData(2, 1, true)]
    [InlineData(3, 1, false)] [InlineData(255, 1, false)] [InlineData(1, 0, false)] [InlineData(1, 65, false)]
    public void TravelerObservationCodecChecksTierHopLimitAndCompleteConsumption(byte tier, byte hops, bool valid) {
        var request = new WorldTravelerObservation("source", new(new("source", 4, 1), 1), (WorldDisclosureTier)tier, hops);
        var bytes = WorldFederationCodec.EncodeTravelerObservation(in request);
        Assert.Equal(valid, WorldFederationCodec.TryDecodeTravelerObservation(bytes, out var decoded, out _));
        if (valid) { Assert.Equal(request, decoded); }
        Assert.False(WorldFederationCodec.TryDecodeTravelerObservation([.. bytes, 0], out _, out _));
        Assert.False(WorldFederationCodec.TryDecodeTravelerObservation(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
    }

    private sealed class TravelerProjectionCapture : IClientSink {
        private SnapshotImage? m_latest;
        public SnapshotImage? Latest => Volatile.Read(ref m_latest);
        public void DeliverSnapshot(in WorldSnapshot snapshot) => Volatile.Write(ref m_latest, new(snapshot.Authority, snapshot.Entries.ToArray()));
        public void DeliverDefinition(WorldDefinition definition) { }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
    }
    private sealed record SnapshotImage(string Authority, EntitySnapshot[] Entries);
}
