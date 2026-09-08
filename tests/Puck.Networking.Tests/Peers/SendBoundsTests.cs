using System.Text;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary><see cref="PeerLink.SendAsync"/> refuses what it cannot deliver before it touches the wire: a payload
/// over <see cref="PeerWireProtocol.MaxMessagePayloadBytes"/> is a caller bug thrown before signing, with the link
/// untouched, a send on a closed link is a named <see cref="PeerRefusedException"/> rather than a transport
/// exception paid for with a signature, and a send whose signing key was disposed under it (the last step of the
/// owning peer's disposal) is refused by the same name rather than escaping as the key's own exception.</summary>
public sealed class SendBoundsTests {
    [Fact]
    public async Task SendAsync_OneByteOverTheCap_ThrowsBeforeSendingAnything_AndTheLinkStaysOpen() {
        using var deadline = Laws.SocketDeadline();

        var (peerA, peerB, linkAtoB, linkBtoA) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(testCode: () => linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: new byte[(PeerWireProtocol.MaxMessagePayloadBytes + 1)]
        ));

        Assert.Equal(
            expected: "payload",
            actual: thrown.ParamName
        );
        Assert.True(condition: linkAtoB.IsOpen);

        // Nothing reached the wire: the receiver's very next event is the honest message that follows, not a
        // refusal of an oversized frame and not a closure.
        await linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "still open"u8.ToArray()
        );

        var received = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: "still open",
            actual: Encoding.UTF8.GetString(bytes: received.Payload.Span)
        );
    }
    /// <summary>The control for the cap: a payload of exactly <see cref="PeerWireProtocol.MaxMessagePayloadBytes"/>
    /// is delivered, so the constant names the largest payload that fits the frame, not one past it.</summary>
    [Fact]
    public async Task SendAsync_ExactlyAtTheCap_IsDelivered() {
        using var deadline = Laws.SocketDeadline();

        var (peerA, peerB, linkAtoB, linkBtoA) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        var payload = new byte[PeerWireProtocol.MaxMessagePayloadBytes];

        Random.Shared.NextBytes(buffer: payload);

        await linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: payload
        );

        var received = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: payload,
            actual: received.Payload.ToArray()
        );
    }
    [Fact]
    public async Task SendAsync_OnAClosedLink_ThrowsConnectionClosed() {
        using var deadline = Laws.SocketDeadline();

        var (peerA, peerB, linkAtoB, _) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        await linkAtoB.DisposeAsync();

        Assert.False(condition: linkAtoB.IsOpen);

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "too late"u8.ToArray()
        ));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: thrown.Failure.Refusal
        );
    }
    [Fact]
    public async Task SendAsync_WhoseIdentityWasDisposedUnderAnOpenLink_ThrowsConnectionClosed_NotObjectDisposedException() {
        using var deadline = Laws.SocketDeadline();

        var identityA = PeerIdentity.Create();
        var peerA = PeerTestSupport.NewPeer(identity: identityA);
        var peerB = PeerTestSupport.NewPeer();

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var linkAtoB = await peerA.DialAsync(
            ct: deadline.Token,
            endpoint: endpointB
        );

        _ = await peerB.IncomingLinks.ReadAsync(cancellationToken: deadline.Token);

        // The state a send reaches when it passed the open check just before Peer.DisposeAsync closed the links and
        // then disposed the identity: the link still reports open, and the key it signs with is gone.
        identityA.Dispose();

        Assert.True(condition: linkAtoB.IsOpen);

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "signed with a disposed key"u8.ToArray()
        ));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: thrown.Failure.Refusal
        );
        Assert.Contains(
            actualString: thrown.Failure.Detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(ObjectDisposedException)
        );
    }
}
