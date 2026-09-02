using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A dialer whose transport certificate carries a key no peer identity can be built over — RSA at any
/// size, or an elliptic curve other than P-256 — and who offers that same key at Hello passes channel binding (the key IS the
/// one its transport proved) and must still be refused, by name, before any proof is verified against it: the
/// acceptor records <see cref="PeerRefusal.IdentityKeyInvalid"/>, tells the dialer the same name, opens no link,
/// and goes on accepting honest peers.</summary>
public sealed class IdentityKeyTests {
    private static readonly Oid[] TransportCertificateUsages = [
        new Oid(oid: "1.3.6.1.5.5.7.3.1"),
        new Oid(oid: "1.3.6.1.5.5.7.3.2"),
    ];

    /// <summary>Mints a self-signed certificate over <paramref name="request"/>'s key the way
    /// <see cref="PeerIdentity.CreateTransportCertificate"/> does, so the only difference between it and an honest
    /// transport certificate is the key's algorithm.</summary>
    private static X509Certificate2 SelfSigned(CertificateRequest request) {
        request.CertificateExtensions.Add(item: new X509KeyUsageExtension(
            critical: false,
            keyUsages: X509KeyUsageFlags.DigitalSignature
        ));
        request.CertificateExtensions.Add(item: new X509EnhancedKeyUsageExtension(
            critical: false,
            enhancedKeyUsages: [.. TransportCertificateUsages]
        ));

        var now = DateTimeOffset.UtcNow;

        using var ephemeral = request.CreateSelfSigned(
            notAfter: now.AddDays(days: 1),
            notBefore: now.AddDays(days: -1)
        );

        return X509CertificateLoader.LoadPkcs12(
            data: ephemeral.ExportPkcs12(
                exportParameters: Pkcs12ExportPbeParameters.Default,
                password: null
            ),
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet,
            password: null
        );
    }
    private static X509Certificate2 P384Certificate() {
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP384);

        return SelfSigned(request: new CertificateRequest(
            hashAlgorithm: HashAlgorithmName.SHA384,
            key: key,
            subjectName: "CN=not-p256"
        ));
    }
    /// <summary>Mints an RSA certificate of <paramref name="keySizeInBits"/>: 2048 bits gives a 294-byte SPKI, under
    /// the attestation profile's 512-byte SPKI cap; 4096 bits gives a 550-byte one, over it — and both must be refused
    /// by the same name, since an oversized key is still an honestly offered wrong key, not a grammar violation.</summary>
    private static X509Certificate2 RsaCertificate(int keySizeInBits) {
        using var key = RSA.Create(keySizeInBits: keySizeInBits);

        return SelfSigned(request: new CertificateRequest(
            hashAlgorithm: HashAlgorithmName.SHA256,
            key: key,
            padding: RSASignaturePadding.Pkcs1,
            subjectName: "CN=not-p256"
        ));
    }
    /// <summary>Dials <paramref name="endpoint"/> over the real QUIC transport presenting <paramref name="certificate"/>,
    /// writes a well-formed Hello offer naming that certificate's own key, and returns the refusal the acceptor
    /// answered with. No <see cref="Peer"/> can do this, because no <see cref="PeerIdentity"/> can hold such a key;
    /// the offer is written by hand for the same reason.</summary>
    private static async Task<PeerRefusal> DialOfferingOwnKeyAsync(X509Certificate2 certificate, IPEndPoint endpoint, CancellationToken ct) {
        var offeredKey = certificate.PublicKey.ExportSubjectPublicKeyInfo();

        // Dialed through the interface, as Peer dials: the platform guard lives in NewTransport.
        await using IPeerTransport transport = PeerTestSupport.NewTransport(certificate: certificate);
        await using var connection = await transport.DialAsync(
            ct: ct,
            endpoint: endpoint
        );

        Assert.True(
            condition: (connection.RemoteTransportKey.Length > 0),
            userMessage: "the acceptor presented no certificate, so the law cannot reach the identity decision"
        );

        await using var stream = await connection.OpenStreamAsync(ct: ct);

        var offer = new WireWriter();

        offer.WriteUInt64(value: PeerWireProtocol.ProtocolKey);
        offer.WriteBlock(value: offeredKey);
        offer.WriteBlock(value: RandomNumberGenerator.GetBytes(count: PeerWireProtocol.ChallengeBytes));

        await WireFrame.WriteAsync(
            body: offer.WrittenMemory,
            ct: ct,
            kind: ((byte)PeerFrameKind.HelloOffer),
            stream: stream
        );

        var peerOffer = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: stream
        );

        Assert.True(
            condition: peerOffer.Ok,
            userMessage: $"the acceptor's own offer did not arrive: {peerOffer.Failure}"
        );
        Assert.Equal(
            expected: ((byte)PeerFrameKind.HelloOffer),
            actual: peerOffer.Kind
        );

        var refused = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: stream
        );

        Assert.True(
            condition: refused.Ok,
            userMessage: $"the acceptor closed without naming a refusal: {refused.Failure}"
        );
        Assert.Equal(
            expected: ((byte)PeerFrameKind.HelloRefused),
            actual: refused.Kind
        );

        return ((PeerRefusal)Assert.Single(collection: refused.Body.ToArray()));
    }
    private static async Task AssertRefusedAsIdentityKeyInvalidAsync(X509Certificate2 certificate) {
        using var deadline = Laws.SocketDeadline();

        await using var acceptor = PeerTestSupport.NewPeer();

        var endpoint = await PeerTestSupport.ListenLoopbackAsync(peer: acceptor);
        var toldToDialer = await DialOfferingOwnKeyAsync(
            certificate: certificate,
            ct: deadline.Token,
            endpoint: endpoint
        );

        Assert.Equal(
            actual: toldToDialer,
            expected: PeerRefusal.IdentityKeyInvalid
        );

        var recorded = await PeerTestSupport.NextHandshakeRefusalAsync(peer: acceptor);

        Assert.Equal(
            expected: PeerRefusal.IdentityKeyInvalid,
            actual: recorded.Failure.Refusal
        );
        Assert.Empty(collection: acceptor.Links);

        // The refusal spent nothing the acceptor needs for the next dialer: an honest peer still gets a link.
        await using var honest = PeerTestSupport.NewPeer();

        var linkHonestToAcceptor = await honest.DialAsync(
            ct: deadline.Token,
            endpoint: endpoint
        );
        var linkAcceptorToHonest = await acceptor.IncomingLinks.ReadAsync(cancellationToken: deadline.Token);

        Assert.Equal(
            expected: honest.Id.Domain,
            actual: linkAcceptorToHonest.RemoteId.Domain
        );
        Assert.Equal(
            expected: acceptor.Id.Domain,
            actual: linkHonestToAcceptor.RemoteId.Domain
        );
        Assert.Single(collection: acceptor.Links);
    }

    [Fact]
    public Task DialerPresentingAnRsa2048Certificate_IsRefusedAsIdentityKeyInvalid_AndAnHonestPeerStillConnects() => AssertRefusedAsIdentityKeyInvalidAsync(certificate: RsaCertificate(keySizeInBits: 2048));
    [Fact]
    public Task DialerPresentingAnRsa4096Certificate_WhoseSpkiIsOverTheAttestationCap_IsRefusedAsIdentityKeyInvalid_AndAnHonestPeerStillConnects() => AssertRefusedAsIdentityKeyInvalidAsync(certificate: RsaCertificate(keySizeInBits: 4096));
    [Fact]
    public Task DialerPresentingAP384Certificate_IsRefusedAsIdentityKeyInvalid_AndAnHonestPeerStillConnects() => AssertRefusedAsIdentityKeyInvalidAsync(certificate: P384Certificate());
}
