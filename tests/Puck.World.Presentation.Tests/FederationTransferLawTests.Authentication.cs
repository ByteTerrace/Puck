using System.Net;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Server;
using Xunit;
using static Puck.World.Presentation.Tests.FederationSigning;

namespace Puck.World.Presentation.Tests;

public sealed partial class FederationTransferLawTests {
    /// <summary>A TLS peer can name any authority in its acknowledgement. Only a trusted proof of that namespace
    /// on this connection authenticates it; the same real wire exchange with the pinned signer succeeds.</summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("unsigned")]
    [InlineData("unknown-key")]
    [InlineData("wrong-subject")]
    public async Task RemoteAuthority_RequiresTheDestinationToProveItsNamespace(string kind) {
        using var fixture = Fixtures.FreshServer();
        using var clientOracle = LocalOracle(subject: "source/world");
        using var destinationOracle = LocalOracle(subject: fixture.Server.AuthorityIdentity);
        using var otherOracle = LocalOracle(subject: (kind == "wrong-subject" ? "other/world" : fixture.Server.AuthorityIdentity));
        var serverOracle = kind switch {
            "unsigned" => null,
            "valid" => destinationOracle,
            _ => otherOracle,
        };
        var serverSecurity = Authenticator(oracle: serverOracle, trustEntries: () => [TrustEntryFor(oracle: clientOracle)]);
        var clientSecurity = Authenticator(oracle: clientOracle, trustEntries: () => (kind == "wrong-subject"
            ? [TrustEntryFor(oracle: destinationOracle), TrustEntryFor(oracle: otherOracle)]
            : [TrustEntryFor(oracle: destinationOracle)]));
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: serverSecurity,
            timeProvider: new VirtualClock(), transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);
        PeerTestClient.StartOrSkip(host: host);
        using var network = new WorldPeerNetwork(timeProvider: new VirtualClock(),
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);
        using var remote = new WorldRemoteAuthority(endpoint: host.ListenEndpoint!, placeholder: fixture.Server.Definition,
            security: clientSecurity, observerAuthority: clientOracle.Subject, network: network,
            expectedAuthority: fixture.Server.AuthorityIdentity);
        var ct = TestContext.Current.CancellationToken;
        await using var stream = await network.ConnectAsync(endpoint: IPEndPoint.Parse(s: host.ListenEndpoint!), ct: ct);
        await HandshakeWireFormat.WriteHelloAsync(stream: stream, key: WorldFederationCodec.WireKey, ct: ct);

        if (kind == "valid") {
            await remote.AuthenticateAsync(stream: stream, ct: ct);
            Assert.Equal(expected: fixture.Server.AuthorityIdentity, actual: remote.PeerAuthority);
        } else {
            var error = await Assert.ThrowsAsync<IOException>(testCode: () => remote.AuthenticateAsync(stream: stream, ct: ct));
            Assert.Contains(expectedSubstring: "destination did not prove its authority", actualString: error.Message);
        }
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
        await HandshakeWireFormat.WriteHelloAsync(stream: stream, key: WorldFederationCodec.WireKey, ct: ct);
        var challenge = await RequireFrameAsync(stream: stream, ct: ct);
        var clientChallenge = changed switch {
            "short-client-nonce" => new byte[security.ChallengeBytes - 1],
            "long-client-nonce" => new byte[security.ChallengeBytes + 1],
            _ => security.NewChallenge(),
        };
        var proof = security.Prove(challenge: WorldFederationCodec.BindProofChallenge(
            challenge: (changed == "server-nonce" ? security.NewChallenge() : challenge.Body.Span),
            counterChallenge: (changed == "client-nonce" ? security.NewChallenge() : clientChallenge),
            clientKeyHash: (changed == "client-key" ? new string(c: '0', count: 64) : client.KeyHash),
            serverKeyHash: (changed == "server-key" ? new string(c: '0', count: 64) : stream.Link.RemoteId.KeyHash),
            serverProof: (changed == "reflection")));
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate,
            body: WorldFederationCodec.EncodeAuthentication(proof: proof, challenge: clientChallenge), ct: ct);
        var result = await RequireFrameAsync(stream: stream, ct: ct);
        Assert.Equal(expected: ((byte)(changed == "valid" ? WorldFederationResponse.Authenticated : WorldFederationResponse.Refusal)), actual: result.Kind);
        if (changed != "valid") {
            Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.AuthenticationFailed),
                actualString: System.Text.Encoding.UTF8.GetString(bytes: result.Body.Span));
        }
    }
}
