using System.Text;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class RestartTests {
    [Fact]
    public async Task AfterBRestartsWithTheSameKey_ARedialsAndDeliveryResumes() {
        var ct = TestContext.Current.CancellationToken;

        await using var peerA = PeerTestSupport.NewPeer();

        byte[] identityBBytes;

        using (var seed = PeerIdentity.Create()) {
            identityBBytes = seed.ExportPkcs8PrivateKey();
        }

        var peerB = PeerTestSupport.NewPeer(identity: PeerIdentity.FromPkcs8PrivateKey(pkcs8PrivateKey: identityBBytes));
        var originalDomain = peerB.Id.Domain;
        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        await linkAtoB.SendAsync(
            ct: ct,
            payload: "before restart"u8.ToArray()
        );

        var received = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: "before restart",
            actual: Encoding.UTF8.GetString(bytes: received.Payload.Span)
        );

        // Simulate the process dying: dispose B without ever telling A.
        await peerB.DisposeAsync();

        Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        await using var restartedPeerB = PeerTestSupport.NewPeer(identity: PeerIdentity.FromPkcs8PrivateKey(pkcs8PrivateKey: identityBBytes));

        await restartedPeerB.ListenAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback(port: endpointB.Port)
        );

        Assert.Equal(
            expected: originalDomain,
            actual: restartedPeerB.Id.Domain
        );

        var redialedLink = await peerA.DialAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback(port: endpointB.Port)
        );
        var linkAtRestartedB = await restartedPeerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        Assert.Equal(
            expected: originalDomain,
            actual: redialedLink.RemoteId.Domain
        );

        await redialedLink.SendAsync(
            ct: ct,
            payload: "after restart"u8.ToArray()
        );

        var resumed = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkAtRestartedB));

        Assert.Equal(
            expected: "after restart",
            actual: Encoding.UTF8.GetString(bytes: resumed.Payload.Span)
        );
    }
}
