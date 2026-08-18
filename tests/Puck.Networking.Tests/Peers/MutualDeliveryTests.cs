using System.Text;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class MutualDeliveryTests {
    [Fact]
    public async Task DialedLink_DeliversInBothDirections_RegardlessOfWhoDialed() {
        var ct = TestContext.Current.CancellationToken;

        await using var peerA = PeerTestSupport.NewPeer();
        await using var peerB = PeerTestSupport.NewPeer();

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);

        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        Assert.Equal(
            expected: peerB.Id.Domain,
            actual: linkAtoB.RemoteId.Domain
        );
        Assert.Equal(
            expected: peerA.Id.Domain,
            actual: linkBtoA.RemoteId.Domain
        );

        await linkAtoB.SendAsync(
            ct: ct,
            payload: "hello from A"u8.ToArray()
        );

        var receivedAtB = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: "hello from A",
            actual: Encoding.UTF8.GetString(bytes: receivedAtB.Payload.Span)
        );

        await linkBtoA.SendAsync(
            ct: ct,
            payload: "hello from B"u8.ToArray()
        );

        var receivedAtA = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: "hello from B",
            actual: Encoding.UTF8.GetString(bytes: receivedAtA.Payload.Span)
        );
    }
}
