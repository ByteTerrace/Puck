using System.Net;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Server;
using Xunit;
using static Puck.World.Presentation.Tests.FederationSigning;

namespace Puck.World.Presentation.Tests;

public sealed partial class FederationTransferLawTests {
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [Theory]
    public void RemoteAuthorityCannotDialWithoutAnIntendedNamespace(string? expectedAuthority) {
        using var fixture = Fixtures.FreshServer();
        using var oracle = LocalOracle(subject: "source/world");
        using var network = new WorldPeerNetwork(timeProvider: new VirtualClock());

        Assert.Throws<ArgumentException>(testCode: () => new WorldRemoteAuthority(endpoint: "127.0.0.1:1",
            placeholder: fixture.Server.Definition, security: Authenticator(oracle: oracle, trustEntries: () => []),
            observerAuthority: oracle.Subject, network: network, expectedAuthority: expectedAuthority!));
    }
    /// <summary>A TLS peer can name any authority in its acknowledgement. Only a trusted proof of that namespace
    /// on this connection authenticates it; the same real wire exchange with the pinned signer succeeds.</summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("unsigned")]
    [InlineData("unknown-key")]
    [InlineData("wrong-subject")]
    [InlineData("replayed-proof")]
    [InlineData("valid-verifier-without-pin")]
    [InlineData("unsigned-verifier-without-pin")]
    [InlineData("trusted-substitute")]
    public async Task RemoteAuthority_RequiresTheDestinationToProveItsNamespace(string kind) {
        using var fixture = Fixtures.FreshServer();
        using var clientOracle = LocalOracle(subject: "source/world");
        using var destinationOracle = LocalOracle(subject: fixture.Server.AuthorityIdentity);
        using var otherOracle = LocalOracle(subject: ((kind == "wrong-subject") ? "other/world" : fixture.Server.AuthorityIdentity));
        var serverOracle = kind switch {
            "unsigned" or "unsigned-verifier-without-pin" => null,
            "valid" or "valid-verifier-without-pin" or "replayed-proof" or "trusted-substitute" => destinationOracle,
            _ => otherOracle,
        };
        IAuthenticator serverSecurity = Authenticator(oracle: serverOracle, trustEntries: () => [TrustEntryFor(oracle: clientOracle)]);
        IAuthenticator clientSecurity = Authenticator(oracle: clientOracle, trustEntries: () => ((kind == "wrong-subject")
            ? [TrustEntryFor(oracle: destinationOracle), TrustEntryFor(oracle: otherOracle)]
            : [TrustEntryFor(oracle: destinationOracle)]));

        if (kind == "replayed-proof") {
            serverSecurity = new ReplayedDestinationProof(inner: serverSecurity);
        }
        if (kind.EndsWith(comparisonType: StringComparison.Ordinal, value: "verifier-without-pin")) {
            clientSecurity = new VerifierWithoutPin(inner: clientSecurity);
        }
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: serverSecurity,
            timeProvider: new VirtualClock(), transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

        PeerTestClient.StartOrSkip(host: host);
        using var network = new WorldPeerNetwork(timeProvider: new VirtualClock(),
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);
        using var remote = new WorldRemoteAuthority(endpoint: host.ListenEndpoint!, placeholder: fixture.Server.Definition,
            security: clientSecurity, observerAuthority: clientOracle.Subject, network: network,
            expectedAuthority: ((kind == "trusted-substitute") ? "intended/world" : fixture.Server.AuthorityIdentity));
        var ct = TestContext.Current.CancellationToken;

        if (kind == "replayed-proof") {
            // Capture a genuine proof that passed, then make the destination replay those exact bytes on the next
            // stream. The identity, transport keys and namespace stay the same; only the handshake is fresh.
            await using var original = await network.ConnectAsync(endpoint: IPEndPoint.Parse(s: host.ListenEndpoint!), ct: ct);

            await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldFederationCodec.WireKey, stream: original);
            await remote.AuthenticateAsync(ct: ct, stream: original);
            Assert.Equal(expected: fixture.Server.AuthorityIdentity, actual: remote.PeerAuthority);
        }
        await using var stream = await network.ConnectAsync(endpoint: IPEndPoint.Parse(s: host.ListenEndpoint!), ct: ct);

        await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldFederationCodec.WireKey, stream: stream);

        if (kind is "valid" or "valid-verifier-without-pin") {
            await remote.AuthenticateAsync(ct: ct, stream: stream);
            Assert.Equal(expected: fixture.Server.AuthorityIdentity, actual: remote.PeerAuthority);
        } else {
            var error = await Assert.ThrowsAsync<IOException>(testCode: () => remote.AuthenticateAsync(ct: ct, stream: stream));

            Assert.Contains(expectedSubstring: ((kind == "trusted-substitute") ? "authority" : "destination did not prove its authority"), actualString: error.Message);
        }
    }

    private sealed class ReplayedDestinationProof(IAuthenticator inner) : IAuthenticator {
        private byte[]? m_proof;

        public int ChallengeBytes => inner.ChallengeBytes;
        public bool IsConfigured => inner.IsConfigured;

        public byte[] NewChallenge() => inner.NewChallenge();
        public byte[] Prove(ReadOnlySpan<byte> challenge) => (m_proof ??= inner.Prove(challenge: challenge));
        public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) =>
            inner.TryVerify(challenge: challenge, proof: proof, sourceAuthority: out sourceAuthority);
    }
    private sealed class VerifierWithoutPin(IAuthenticator inner) : IAuthenticator, IRemoteIdentityVerifier {
        public int ChallengeBytes => inner.ChallengeBytes;
        public bool HasRemoteIdentityPin => false;
        public bool IsConfigured => inner.IsConfigured;

        // An older verifier-only implementation accepted every transport. The presence of that interface and a
        // positive answer must not substitute for a configured pin or waive the destination's signed proof.
        public bool AcceptsRemoteIdentity(string keyHash) => true;
        public byte[] NewChallenge() => inner.NewChallenge();
        public byte[] Prove(ReadOnlySpan<byte> challenge) => inner.Prove(challenge: challenge);
        public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) =>
            inner.TryVerify(challenge: challenge, proof: proof, sourceAuthority: out sourceAuthority);
    }

    /// <summary>A trusted source's proof elicited on another peer transport cannot be relayed to this door, even
    /// with this door's exact current nonce. The legitimate binding on a fresh lane still admits.</summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("server-key")]
    [InlineData("client-key")]
    [InlineData("server-nonce")]
    [InlineData("client-nonce")]
    [InlineData("short-client-nonce")]
    [InlineData("long-client-nonce")]
    [InlineData("reflection")]
    public async Task FederationDoor_BindsTheSourceProofToTheCompleteTranscript(string changed) {
        using var fixture = Fixtures.FreshServer();
        using var oracle = LocalOracle(subject: "source/world");
        IAuthenticator security = Authenticator(oracle: oracle, trustEntries: () => [TrustEntryFor(oracle: oracle)]);
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: security,
            timeProvider: new VirtualClock(), transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

        PeerTestClient.StartOrSkip(host: host);
        using var client = new PeerTestClient();
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        var ct = TestContext.Current.CancellationToken;

        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: ct);
        var stream = client.GetStream();

        await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldFederationCodec.WireKey, stream: stream);
        var challenge = await RequireFrameAsync(ct: ct, stream: stream);
        var clientChallenge = changed switch {
            "short-client-nonce" => new byte[(security.ChallengeBytes - 1)],
            "long-client-nonce" => new byte[(security.ChallengeBytes + 1)],
            _ => security.NewChallenge(),
        };
        var proof = security.Prove(challenge: WorldFederationCodec.BindProofChallenge(
            challenge: ((changed == "server-nonce") ? security.NewChallenge() : challenge.Body.Span),
            counterChallenge: ((changed == "client-nonce") ? security.NewChallenge() : clientChallenge),
            clientKeyHash: ((changed == "client-key") ? new string(c: '0', count: 64) : client.KeyHash),
            serverKeyHash: ((changed == "server-key") ? new string(c: '0', count: 64) : stream.Link.RemoteId.KeyHash),
            serverProof: (changed == "reflection")));

        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate,
            body: WorldFederationCodec.EncodeAuthentication(challenge: clientChallenge, proof: proof), ct: ct);
        var result = await RequireFrameAsync(ct: ct, stream: stream);

        Assert.Equal(expected: ((byte)((changed == "valid") ? WorldFederationResponse.Authenticated : WorldFederationResponse.Refusal)), actual: result.Kind);
        if (changed != "valid") {
            Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.AuthenticationFailed),
                actualString: System.Text.Encoding.UTF8.GetString(bytes: result.Body.Span));
        }
    }
}
