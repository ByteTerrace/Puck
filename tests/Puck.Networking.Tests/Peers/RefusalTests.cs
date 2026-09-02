using System.Text;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class RefusalTests {
    private static async Task<(PeerIdentity IdentityB, Peer PeerA, Peer PeerB, PeerLink LinkAtoB, PeerLink LinkBtoA, Stream ControlStreamAtB)> ConnectAsync(CancellationToken ct) {
        var identityB = PeerIdentity.Create();
        var peerA = PeerTestSupport.NewPeer();

        var (peerB, tapB) = PeerTestSupport.NewTappedPeer(identity: identityB);
        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        return (identityB, peerA, peerB, linkAtoB, linkBtoA, Assert.Single(collection: tapB.Streams));
    }
    private static async Task AssertHonestTrafficStillFlowsAsync(PeerLink sender, PeerLink receiver, CancellationToken ct) {
        await sender.SendAsync(
            ct: ct,
            payload: "still honest"u8.ToArray()
        );

        var received = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: receiver));

        Assert.Equal(
            expected: "still honest",
            actual: Encoding.UTF8.GetString(bytes: received.Payload.Span)
        );
    }

    [Fact]
    public async Task UnsignedBytes_AreRefusedAsUnsigned_AndTheLinkStaysOpen() {
        var ct = TestContext.Current.CancellationToken;

        var (_, peerA, peerB, linkAtoB, linkBtoA, controlStreamAtB) = await ConnectAsync(ct: ct);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        await PeerTestSupport.SendRawMessageFrameAsync(
            attestationOrGarbageBytes: "not an attestation at all"u8.ToArray(),
            controlStream: controlStreamAtB,
            ct: ct
        );

        var refused = Assert.IsType<PeerEvent.Refused>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: PeerRefusal.MessageUnsigned,
            actual: refused.Failure.Refusal
        );

        await AssertHonestTrafficStillFlowsAsync(
            ct: ct,
            receiver: linkAtoB,
            sender: linkBtoA
        );
    }
    [Fact]
    public async Task MessageSignedByAnotherKey_IsRefusedAsWrongSigner_AndTheLinkStaysOpen() {
        var ct = TestContext.Current.CancellationToken;

        var (_, peerA, peerB, linkAtoB, linkBtoA, controlStreamAtB) = await ConnectAsync(ct: ct);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        using var impostor = PeerIdentity.Create();

        var forgedClaim = impostor.SignClaim(
            audience: peerA.Id.Domain,
            payload: "I am not B"u8.ToArray(),
            purpose: PeerWireProtocol.MessagePurpose
        );

        await PeerTestSupport.SendRawMessageFrameAsync(
            attestationOrGarbageBytes: PeerWireProtocol.Codec.EncodeAttestation(attestation: forgedClaim),
            controlStream: controlStreamAtB,
            ct: ct
        );

        var refused = Assert.IsType<PeerEvent.Refused>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: PeerRefusal.MessageWrongSigner,
            actual: refused.Failure.Refusal
        );

        await AssertHonestTrafficStillFlowsAsync(
            ct: ct,
            receiver: linkAtoB,
            sender: linkBtoA
        );
    }
    [Fact]
    public async Task TamperedPayload_IsRefusedAsUnverified_AndTheLinkStaysOpen() {
        var ct = TestContext.Current.CancellationToken;

        var (identityB, peerA, peerB, linkAtoB, linkBtoA, controlStreamAtB) = await ConnectAsync(ct: ct);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        // Signed by B under its real identity, so decode and the domain/subject check both succeed — only the
        // signature check must fail, isolating this case from the wrong-signer and unsigned cases above.
        var honestClaim = identityB.SignClaim(
            audience: peerA.Id.Domain,
            payload: "tamper me"u8.ToArray(),
            purpose: PeerWireProtocol.MessagePurpose
        );
        var honestBytes = PeerWireProtocol.Codec.EncodeAttestation(attestation: honestClaim);

        honestBytes[^1] ^= 0xFF;

        await PeerTestSupport.SendRawMessageFrameAsync(
            attestationOrGarbageBytes: honestBytes,
            controlStream: controlStreamAtB,
            ct: ct
        );

        var refused = Assert.IsType<PeerEvent.Refused>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: PeerRefusal.MessageUnverified,
            actual: refused.Failure.Refusal
        );

        await AssertHonestTrafficStillFlowsAsync(
            ct: ct,
            receiver: linkAtoB,
            sender: linkBtoA
        );
    }
}
