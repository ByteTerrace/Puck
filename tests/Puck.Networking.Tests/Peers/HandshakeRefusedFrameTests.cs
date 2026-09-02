using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A <see cref="PeerFrameKind.HelloRefused"/> frame whose body does not hold exactly one known refusal
/// byte is a grammar violation like any other the handshake meets: received in place of the peer's offer it is
/// refused as <see cref="PeerRefusal.HandshakeMalformed"/>, and that name is sent to the far side as a
/// <c>HelloRefused</c> frame, the same as every other refusal this side decides after its own offer is written. Only
/// a well-formed refusal is answered silently, because the peer that sent it is already refusing. Runs over an
/// in-memory connection pair with the law itself standing in for the far side, since no <see cref="Peer"/> sends a
/// malformed refusal.</summary>
public sealed class HandshakeRefusedFrameTests {
    private static async Task AssertMalformedHelloRefusedIsAnsweredAsync(byte[] body) {
        using var deadline = Laws.SocketDeadline();

        var identityA = PeerIdentity.Create();

        var (connectionAtA, connectionAtB) = InMemoryPeerConnection.Pair(
            keyProvedByA: identityA.SubjectPublicKeyInfo,
            keyProvedByB: "the key the law's far side would have proved"u8.ToArray()
        );

        await using var peerA = new Peer(
            identity: identityA,
            transport: new FakePeerTransport(dial: _ => connectionAtA)
        );

        var dialing = peerA.DialAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback(port: 2)
        );
        var streamAtB = await connectionAtB.AcceptStreamAsync(ct: deadline.Token);

        Assert.NotNull(@object: streamAtB);

        // A's offer arrives first, exactly as the handshake promises; the far side answers it with a refusal frame
        // that does not decode instead of an offer of its own.
        var offer = await WireFrame.ReadAsync(
            ct: deadline.Token,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: streamAtB
        );

        Assert.True(
            condition: offer.Ok,
            userMessage: $"the dialer's offer did not arrive: {offer.Failure}"
        );
        Assert.Equal(
            expected: ((byte)PeerFrameKind.HelloOffer),
            actual: offer.Kind
        );

        await WireFrame.WriteAsync(
            body: body,
            ct: deadline.Token,
            kind: ((byte)PeerFrameKind.HelloRefused),
            stream: streamAtB
        );

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing.WaitAsync(cancellationToken: deadline.Token));

        Assert.Equal(
            expected: PeerRefusal.HandshakeMalformed,
            actual: thrown.Failure.Refusal
        );
        Assert.Empty(collection: peerA.Links);

        // What crossed the wire before A closed: A's own refusal, naming the grammar violation to the far side.
        var answered = await WireFrame.ReadAsync(
            ct: deadline.Token,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: streamAtB
        );

        Assert.True(
            condition: answered.Ok,
            userMessage: $"the dialer closed without naming its refusal: {answered.Failure}"
        );
        Assert.Equal(
            expected: ((byte)PeerFrameKind.HelloRefused),
            actual: answered.Kind
        );
        Assert.Equal(
            expected: PeerRefusal.HandshakeMalformed,
            actual: ((PeerRefusal)Assert.Single(collection: answered.Body.ToArray()))
        );
        Assert.True(
            condition: connectionAtA.IsDisposed,
            userMessage: "a refused dial must release its connection"
        );
    }

    [Fact]
    public Task Dialer_ReceivingAHelloRefusedWithATrailingByte_IsRefusedHandshakeMalformed_AndSendsThatName() => AssertMalformedHelloRefusedIsAnsweredAsync(body: [((byte)PeerRefusal.ChannelUnbound), 0]);
    [Fact]
    public Task Dialer_ReceivingAHelloRefusedNamingAnUnknownRefusal_IsRefusedHandshakeMalformed_AndSendsThatName() => AssertMalformedHelloRefusedIsAnsweredAsync(body: [0x7f]);
}
