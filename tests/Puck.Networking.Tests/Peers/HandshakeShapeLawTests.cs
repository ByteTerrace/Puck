using System.Security.Cryptography;
using Puck.Networking.Peers;
using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>The Hello offer names the shape of the peer wire contract after its protocol key, and a peer that offers this
/// build's key under another shape is refused <see cref="PeerRefusal.ProtocolMismatch"/> before its key or challenge is
/// looked at. Runs over an in-memory connection pair with the law standing in for the far side.</summary>
public sealed class HandshakeShapeLawTests {
    [Fact]
    public async Task TheOfferCarriesTheLedgersShapeAndAnotherShapeIsRefusedAsAProtocolMismatch() {
        var ct = TestContext.Current.CancellationToken;
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
            ct: ct,
            endpoint: PeerTestSupport.Loopback(port: 2)
        );
        var streamAtB = await connectionAtB.AcceptStreamAsync(ct: ct);

        Assert.NotNull(@object: streamAtB);

        var offer = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: streamAtB
        );

        Assert.True(
            condition: offer.Ok,
            userMessage: $"the dialer's offer did not arrive: {offer.Failure}"
        );

        var reader = new WireReader(bytes: offer.Body.Span);

        Assert.Equal(
            expected: PeerWireProtocol.ProtocolKey,
            actual: reader.ReadUInt64()
        );
        Assert.Equal(
            expected: FormatLedgerShapes.Of(id: "PeerWireProtocol.ProtocolKey"),
            actual: reader.ReadRequiredString(field: "protocol shape")
        );

        var other = new WireWriter();

        other.WriteUInt64(value: PeerWireProtocol.ProtocolKey);
        other.WriteString(value: "0000000000000000");
        other.WriteBlock(value: identityA.SubjectPublicKeyInfo);
        other.WriteBlock(value: RandomNumberGenerator.GetBytes(count: PeerWireProtocol.ChallengeBytes));
        await WireFrame.WriteAsync(
            body: other.WrittenMemory,
            ct: ct,
            kind: ((byte)PeerFrameKind.HelloOffer),
            stream: streamAtB
        );

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing.WaitAsync(cancellationToken: ct));

        Assert.Equal(
            expected: PeerRefusal.ProtocolMismatch,
            actual: thrown.Failure.Refusal
        );
        Assert.Contains(
            actualString: thrown.Failure.Detail,
            expectedSubstring: "protocol shape 0000000000000000"
        );
        Assert.Empty(collection: peerA.Links);
    }
}
