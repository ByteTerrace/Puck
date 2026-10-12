using System.Security.Cryptography;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>The Hello offer opens with the peer wire contract's protocol key, and a peer that offers another key is
/// refused <see cref="PeerRefusal.ProtocolMismatch"/> before its key or challenge is looked at. Runs over an in-memory
/// connection pair with the law standing in for the far side.</summary>
public sealed class HandshakeProtocolKeyLawTests {
    [Fact]
    public async Task TheOfferCarriesTheProtocolKeyAndAnotherKeyIsRefusedAsAProtocolMismatch() {
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

        var other = new WireWriter();

        other.WriteUInt64(value: (PeerWireProtocol.ProtocolKey ^ 1UL));
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
            expectedSubstring: "offered protocol key"
        );
        Assert.Empty(collection: peerA.Links);
    }
}
