using System.Net;
using Puck.Commands;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

using static Puck.World.Testing.AdmissionWireFixture;

namespace Puck.World.Server.Tests;

/// <summary>Real admitted sockets lose their authority and close when their signed window or live trust ends.</summary>
public sealed class CredentialEvictionLawTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static void InstallDefinition(WorldFixture fixture, WorldDefinition candidate) {
        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: candidate,
                Origin: new WorldRebuildOrigin.File(Path: "credential-eviction.world.json"),
                Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate))
            ),
            principal: Principal.Console
        );
        fixture.Step();
    }
    private static async Task<(PeerTestClient Client, byte Kind, ReadOnlyMemory<byte> Body)> AuthenticateFederationAsync(WorldPeerHost host, LocalKeySigningOracle oracle) {
        var client = new PeerTestClient();

        try {
            var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);

            await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: TestToken);
            var stream = client.GetStream();

            await HandshakeWireFormat.WriteHelloAsync(stream: stream, key: WorldFederationCodec.WireKey, ct: TestToken);
            var challenge = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: TestToken);

            Assert.True(condition: challenge.Ok);
            Assert.Equal(expected: ((byte)WorldFederationResponse.Challenge), actual: challenge.Kind);
            var counterChallenge = new byte[WorldAdmissionDoor.ChallengeBytes];

            System.Security.Cryptography.RandomNumberGenerator.Fill(data: counterChallenge);
            var proof = oracle.Sign(challenge: WorldFederationCodec.BindProofChallenge(
                challenge: challenge.Body.Span,
                counterChallenge: counterChallenge,
                clientKeyHash: client.KeyHash,
                serverKeyHash: stream.Link.RemoteId.KeyHash,
                serverProof: false
            ), cancellationToken: TestToken);

            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate,
                body: WorldFederationCodec.EncodeAuthentication(challenge: counterChallenge, proof: proof), ct: TestToken);
            var reply = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: TestToken);

            Assert.True(condition: reply.Ok);
            return (client, reply.Kind, reply.Body);
        } catch {
            client.Dispose();
            throw;
        }
    }
    private static async Task OpenIntentStreamAsync(PeerTestClient client) {
        var stream = client.GetStream();

        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.IntentStream, body: default, ct: TestToken);
        var opened = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: TestToken);

        Assert.True(condition: opened.Ok);
        Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: opened.Kind);
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AnIdleAdmittedPeerClosesAndLosesItsGrantsWhenCredentialExpiresOrAdmissionIsRemoved(bool removeAdmission) {
        var identity = GenerateIdentity(subject: "evicted-peer");
        using var key = identity.Key;
        var body = GrantSubject.Body(index: PeerBodyIndex);
        var document = BuildAdmissionDocument(entry: BuildEntry(identity: identity, grants: [
            new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: body, Budget: 100)
        ]));
        using var fixture = Fixtures.FreshServer(definition: document);
        using var host = StartHost(server: fixture.Server, clock: out var clock);
        var admitted = await AdmitAsync(fixture: fixture, host: host, identity: identity);
        using var client = admitted.Client;
        var principal = Principal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

        Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: principal, subject: body).IsAllowed);
        var closed = WaitForCloseAsync(stream: client.GetStream(), ct: TestToken);

        if (removeAdmission) {
            InstallDefinition(fixture: fixture, candidate: fixture.Server.Definition with { Admission = [] });
            host.DrainPending();
        } else {
            // The fixture signs through second +1, inclusively. The host's idle sweep reaches the first expired
            // second without any client request or simulation step waking it.
            clock.Advance(by: TimeSpan.FromSeconds(value: 2));
        }

        Assert.True(condition: await closed.WaitAsync(cancellationToken: TestToken));
        // The removal branch may already have drained the disconnect while closing the stream.
        // Wait only while its observable retirement is still outstanding.
        while ((host.Connections.Count != 0) || !fixture.Server.Population.IsParked(index: admitted.PeerIndex)) {
            await host.WaitForPendingWorkAsync(cancellationToken: TestToken);
            host.DrainPending();
        }
        fixture.Step();
        Assert.Empty(collection: host.Connections);
        Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: principal, subject: body).IsAllowed,
            userMessage: "the evicted session retained its Observe grant");
        Assert.True(condition: fixture.Server.Population.IsParked(index: admitted.PeerIndex),
            userMessage: "the evicted session did not enter the world's authored reconnect grace");
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public async Task AnIdleAuthenticatedFederationIntentStreamClosesWhenCredentialExpiresOrAdmissionIsRemoved(int revocation) {
        var identity = GenerateIdentity(subject: "evicted-authority");
        var signingEntry = BuildEntry(grants: [], identity: identity);
        var document = BuildAdmissionDocument(entry: signingEntry);

        document = document with {
            Admission = [signingEntry, new WorldAdmissionEntry(
            Domain: identity.Subject, Mode: WorldAdmissionTrustMode.FederatedAuthority, Grants: [], Disclosure: WorldDisclosureTier.Replica
        )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);
        var clock = new VirtualClock(start: ClaimInstant);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => fixture.Server.Definition.Admission, now: clock.GetUtcNow);
        using var oracle = new LocalKeySigningOracle(key: identity.Key, subject: identity.Subject, validity: TimeSpan.FromSeconds(value: 1), now: clock.GetUtcNow);
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: verifier, timeProvider: clock, transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

        PeerTestClient.StartOrSkip(host: host);
        var authenticated = await AuthenticateFederationAsync(host: host, oracle: oracle);
        using var client = authenticated.Client;

        Assert.Equal(actual: authenticated.Kind, expected: ((byte)WorldFederationResponse.Authenticated));

        // Opening the long-lived intent lane proves the host finished registering this session. No more bytes
        // are sent: eviction must also interrupt an idle streaming reader and release its leases.
        await OpenIntentStreamAsync(client: client);
        var closed = WaitForCloseAsync(stream: client.GetStream(), ct: TestToken);

        if (revocation != 0) {
            InstallDefinition(fixture: fixture, candidate: fixture.Server.Definition with { Admission = ((revocation == 1) ? [] : [signingEntry]) });
            host.DrainPending();
        } else {
            clock.Advance(by: TimeSpan.FromSeconds(value: 2));
        }
        Assert.True(condition: await closed.WaitAsync(cancellationToken: TestToken));
    }
    [Fact]
    public async Task AuthenticatedFederationConnectionLimitRefusesOverflowAndReleasesCapacityOnDisconnect() {
        var identity = GenerateIdentity(subject: "federation-capacity");
        using var fixture = Fixtures.FreshServer(definition: BuildAdmissionDocument(entry: BuildEntry(grants: [], identity: identity)));
        var clock = new VirtualClock(start: ClaimInstant);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => fixture.Server.Definition.Admission, now: clock.GetUtcNow);
        using var oracle = new LocalKeySigningOracle(key: identity.Key, subject: identity.Subject, validity: TimeSpan.FromMinutes(value: 1), now: clock.GetUtcNow);
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: verifier, timeProvider: clock, transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

        PeerTestClient.StartOrSkip(host: host);
        var clients = new List<PeerTestClient>();

        try {
            for (var index = 0; (index < WorldPeerHost.MaximumFederationConnections); index++) {
                var admitted = await AuthenticateFederationAsync(host: host, oracle: oracle);

                clients.Add(item: admitted.Client);
                Assert.Equal(actual: admitted.Kind, expected: ((byte)WorldFederationResponse.Authenticated));
                // The ack is the registration barrier: every preceding session occupies a slot before the next dial.
                await OpenIntentStreamAsync(client: admitted.Client);
            }
            var overflow = await AuthenticateFederationAsync(host: host, oracle: oracle);

            using (overflow.Client) {
                Assert.Equal(actual: overflow.Kind, expected: ((byte)WorldFederationResponse.Refusal));
                Assert.Contains(expectedSubstring: $"connection limit {WorldPeerHost.MaximumFederationConnections}",
                    actualString: System.Text.Encoding.UTF8.GetString(bytes: overflow.Body.Span));
                Assert.True(condition: await ExpireUntilClosedAsync(clock: clock, stream: overflow.Client.GetStream(), handshakeDeadline: false, ct: TestToken));
            }

            // Handing off one intent lane completes its handler, releasing the slot before the outer connection
            // drain closes. Expire that virtual drain before checking recovery; a half-close alone does not end
            // a PeerStream's link, and both ends would otherwise wait for one another indefinitely.
            var departing = clients[0];

            await WorldFederationCodec.WriteRequestAsync(stream: departing.GetStream(), kind: WorldFederationRequest.IntentStreamHandoff, body: default, ct: TestToken);
            var handoff = await WorldFederationCodec.ReadResponseAsync(stream: departing.GetStream(), ct: TestToken);

            Assert.True(condition: handoff.Ok);
            Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: handoff.Kind);
            await departing.CompleteWritesAsync(ct: TestToken);
            Assert.True(condition: await ExpireUntilClosedAsync(clock: clock, stream: departing.GetStream(), handshakeDeadline: false, ct: TestToken));

            var replacement = await AuthenticateFederationAsync(host: host, oracle: oracle);

            using (replacement.Client) {
                Assert.Equal(actual: replacement.Kind, expected: ((byte)WorldFederationResponse.Authenticated));
                await OpenIntentStreamAsync(client: replacement.Client);
            }
        } finally {
            foreach (var client in clients) { client.Dispose(); }
        }
    }
    [Fact]
    public void RapidAdmissionRemovalAndRegrantCannotResumeBeforeTheOldConnectionOwnersCleanup() {
        var identity = GenerateIdentity(subject: "rapid-regrant");
        using var key = identity.Key;
        var body = GrantSubject.Body(index: PeerBodyIndex);
        var document = BuildAdmissionDocument(entry: BuildEntry(identity: identity, grants: [
            new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: body, Budget: 100)
        ]));
        using var fixture = Fixtures.FreshServer(definition: document);
        var server = fixture.Server;

        Assert.True(condition: WorldAdmissionDoor.TryMatchEntry(entries: server.Definition.Admission,
            domain: identity.Domain, subject: identity.Subject, verdict: out var verdict));
        Assert.True(condition: server.TryAdmitPeerConnection(verdict: verdict, expectedAdmissionEntries: server.Definition.Admission,
            admitted: out var oldConnection, refusal: out var refusal), userMessage: refusal);

        // Neither rebuild is the connection owner's cleanup. If removal also disconnects/parks here, regrant can
        // resume the same generation before that old owner's delayed disconnect and expose it to stale cleanup.
        InstallDefinition(fixture: fixture, candidate: server.Definition with { Admission = [] });
        InstallDefinition(candidate: document, fixture: fixture);
        Assert.False(condition: server.TryAdmitPeerConnection(verdict: verdict, expectedAdmissionEntries: server.Definition.Admission,
            admitted: out _, refusal: out _), userMessage: "a fresh connection resumed before the old owner had disconnected");

        server.DisconnectPeerConnection(peer: oldConnection);
        Assert.True(condition: server.TryAdmitPeerConnection(verdict: verdict, expectedAdmissionEntries: server.Definition.Admission,
            admitted: out var resumed, refusal: out refusal), userMessage: refusal);
        Assert.Equal(expected: oldConnection.Generation, actual: resumed.Generation);
        fixture.Step();
        Assert.False(condition: server.Population.IsParked(index: resumed.BodyIndex));
        Assert.True(condition: server.Grants.Allows(principal: resumed.Identity, capability: WorldCapability.Observe, subject: body).IsAllowed);
    }
}
