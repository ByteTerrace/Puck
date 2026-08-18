using System.Text;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class ThreePeerTests {
    [Fact]
    public async Task TwoPeersDialedToOneHub_CarryTwoIndependentLinks_WithNoRelay() {
        var ct = TestContext.Current.CancellationToken;

        await using var peerA = PeerTestSupport.NewPeer();
        await using var peerB = PeerTestSupport.NewPeer();
        await using var peerC = PeerTestSupport.NewPeer();

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);

        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkCtoB = await peerC.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);
        var linkBtoC = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        // The two accepts race, so pair each accepted link with the identity that dialed it rather than by order.
        if (linkBtoA.RemoteId.Domain != peerA.Id.Domain) {
            (linkBtoA, linkBtoC) = (linkBtoC, linkBtoA);
        }

        Assert.Equal(
            expected: peerA.Id.Domain,
            actual: linkBtoA.RemoteId.Domain
        );
        Assert.Equal(
            expected: peerC.Id.Domain,
            actual: linkBtoC.RemoteId.Domain
        );
        Assert.Equal(
            expected: 2,
            actual: peerB.Links.Count
        );
        Assert.Single(collection: peerA.Links);
        Assert.Single(collection: peerC.Links);

        await linkAtoB.SendAsync(
            ct: ct,
            payload: "from A"u8.ToArray()
        );

        var atB = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: "from A",
            actual: Encoding.UTF8.GetString(bytes: atB.Payload.Span)
        );

        await linkCtoB.SendAsync(
            ct: ct,
            payload: "from C"u8.ToArray()
        );

        var alsoAtB = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoC));

        Assert.Equal(
            expected: "from C",
            actual: Encoding.UTF8.GetString(bytes: alsoAtB.Payload.Span)
        );

        await linkBtoA.SendAsync(
            ct: ct,
            payload: "to A"u8.ToArray()
        );
        await linkBtoC.SendAsync(
            ct: ct,
            payload: "to C"u8.ToArray()
        );

        var atA = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));
        var atC = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkCtoB));

        Assert.Equal(
            expected: "to A",
            actual: Encoding.UTF8.GetString(bytes: atA.Payload.Span)
        );
        Assert.Equal(
            expected: "to C",
            actual: Encoding.UTF8.GetString(bytes: atC.Payload.Span)
        );

        // No routing exists: A and C never learn about each other, so C's only link stays B.
        Assert.Equal(
            expected: peerB.Id.Domain,
            actual: Assert.Single(collection: peerC.Links).RemoteId.Domain
        );
    }
}
