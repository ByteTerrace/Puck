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
    // A hosted origin names no file, so a checkpoint can name the journal base.
    private static readonly WorldRebuildOrigin HostedOrigin = new WorldRebuildOrigin.Store(Owner: Guid.Empty, World: SafeName.Parse(candidate: "credential-eviction"));

    private static void InstallDefinition(WorldFixture fixture, WorldDefinition candidate, WorldRebuildOrigin? origin = null) {
        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: candidate,
                Origin: (origin ?? new WorldRebuildOrigin.File(Path: "credential-eviction.world.json")),
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
            clock.Advance(WorldPeerHost.CredentialSweepInterval);
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
            clock.Advance(WorldPeerHost.CredentialSweepInterval);
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
            trustIdentity: new(WorldAdmissionTrustMode.SignsDirectly, identity.Domain, identity.Subject,
                Puck.Attestation.AttestationAlgorithms.EcdsaP256Sha256, identity.Domain),
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
    [Fact]
    public async Task DynamicArrivalGrantThenRemovalEvictsAuthenticatedFederationStream() {
        var identity = GenerateIdentity(subject: "dynamic-arrival-authority");
        using var key = identity.Key;
        var signingEntry = BuildEntry(grants: [], identity: identity);
        var arrivalEntry = new WorldAdmissionEntry(
            Domain: identity.Subject,
            Mode: WorldAdmissionTrustMode.FederatedAuthority,
            Grants: [],
            Disclosure: WorldDisclosureTier.Replica
        );
        var document = BuildAdmissionDocument(entry: signingEntry);
        using var fixture = Fixtures.FreshServer(definition: document);
        var clock = new VirtualClock(start: ClaimInstant);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => fixture.Server.Definition.Admission, now: clock.GetUtcNow);
        using var oracle = new LocalKeySigningOracle(key: identity.Key, subject: identity.Subject, validity: TimeSpan.FromMinutes(value: 1), now: clock.GetUtcNow);
        using var host = new WorldPeerHost(server: fixture.Server, authenticator: verifier, timeProvider: clock, transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

        PeerTestClient.StartOrSkip(host: host);
        var authenticated = await AuthenticateFederationAsync(host: host, oracle: oracle);
        using var client = authenticated.Client;

        Assert.Equal(actual: authenticated.Kind, expected: ((byte)WorldFederationResponse.Authenticated));
        await OpenIntentStreamAsync(client: client);
        var closed = WaitForCloseAsync(stream: client.GetStream(), ct: TestToken);

        // Grant arrival authority dynamically after the session already connected, let reverification observe it,
        // then remove the arrival row: the session must now be evicted rather than consulting a connect-time latch.
        InstallDefinition(fixture: fixture, candidate: (fixture.Server.Definition with { Admission = [signingEntry, arrivalEntry] }));
        clock.Advance(by: WorldPeerHost.CredentialSweepInterval);
        Assert.False(condition: closed.IsCompleted);

        InstallDefinition(fixture: fixture, candidate: (fixture.Server.Definition with { Admission = [signingEntry] }));
        clock.Advance(by: WorldPeerHost.CredentialSweepInterval);
        Assert.True(condition: await closed.WaitAsync(cancellationToken: TestToken));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ForwardedTravelerProjectionEndsImmediatelyWhenIntermediaryNarrowsOrRemovesArrivalPolicy(bool removeArrival) {
        var forwarder = new StallingForwarder();
        using var rig = CreateForwardedRig(forwarder: forwarder);

        await rig.ObserveAsync();

        var updatedAdmission = (removeArrival
            ? new[] { rig.SigningEntry }
            : new[] { rig.SigningEntry, (rig.ArrivalEntry with { Disclosure = WorldDisclosureTier.Presentation }) });

        InstallDefinition(fixture: rig.Fixture, candidate: (rig.Fixture.Server.Definition with { Admission = updatedAdmission }));
        var invalidated = await WorldFederationCodec.ReadResponseAsync(stream: rig.Stream, ct: TestToken);

        Assert.True(condition: invalidated.Ok);
        Assert.Equal(expected: ((byte)WorldFederationResponse.ProjectionInvalidated), actual: invalidated.Kind);
        await forwarder.Canceled.Task.WaitAsync(cancellationToken: TestToken);

        if (removeArrival) {
            var observedMobility = rig.ObservedMobility;
            using var routeProbe = (await AuthenticateFederationAsync(host: rig.Host, oracle: rig.Oracle)).Client;

            await using (StartMailboxPump(host: rig.Host)) {
                await WorldFederationCodec.WriteRequestAsync(
                    body: WorldFederationCodec.EncodeRouteCredential(sourceAuthority: rig.Identity.Subject, mobility: in observedMobility),
                    ct: TestToken,
                    kind: WorldFederationRequest.Route,
                    stream: routeProbe.GetStream()
                );
                var routeReply = await WorldFederationCodec.ReadResponseAsync(stream: routeProbe.GetStream(), ct: TestToken);

                Assert.True(condition: routeReply.Ok);
                Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: routeReply.Kind);
                Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.CredentialUnknown),
                    actualString: System.Text.Encoding.UTF8.GetString(bytes: routeReply.Body.Span));
            }
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ForwardedTravelerProjectionWritesNoFrameAfterACheckpointRestoreNarrowsOrRemovesArrivalPolicy(bool removeArrival) {
        // The forwarder ignores its token, as a relay stalled in a write would, so only the policy read at the frame
        // boundary can keep a frame from reaching the downstream peer.
        var forwarder = new FrameAfterSignalForwarder();
        using var rig = CreateForwardedRig(forwarder: forwarder);
        var server = rig.Fixture.Server;
        var wide = server.Definition.Admission;

        // The checkpoint holds the narrowed policy, so restoring it replaces the admission section with no install.
        var narrowed = (removeArrival
            ? new[] { rig.SigningEntry }
            : new[] { rig.SigningEntry, (rig.ArrivalEntry with { Disclosure = WorldDisclosureTier.Presentation }) });

        InstallDefinition(fixture: rig.Fixture, candidate: (server.Definition with { Admission = narrowed }), origin: HostedOrigin);
        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        InstallDefinition(fixture: rig.Fixture, candidate: (server.Definition with { Admission = wide }), origin: HostedOrigin);

        await rig.ObserveAsync();
        server.RestoreCheckpoint(checkpoint: checkpoint!);
        Assert.NotSame(expected: wide, actual: server.Definition.Admission);
        forwarder.WriteFrame.SetResult();

        var first = await WorldFederationCodec.ReadResponseAsync(stream: rig.Stream, ct: TestToken);

        Assert.True(condition: first.Ok);
        Assert.Equal(expected: ((byte)WorldFederationResponse.ProjectionInvalidated), actual: first.Kind);
        Assert.False(condition: (await forwarder.FrameWritten.Task.WaitAsync(cancellationToken: TestToken)), userMessage: "a frame reached the downstream peer after the policy narrowed");
    }

    private static ForwardedRig CreateForwardedRig(ProbeForwarder forwarder) {
        var identity = GenerateIdentity(subject: "forwarded-projection-source");
        var signingEntry = BuildEntry(grants: [], identity: identity);
        var arrivalEntry = new WorldAdmissionEntry(
            Domain: identity.Subject,
            Mode: WorldAdmissionTrustMode.FederatedAuthority,
            Grants: [
                new WorldAdmissionGrant(Capability: WorldCapability.Drive, Budget: 100),
                new WorldAdmissionGrant(Capability: WorldCapability.Observe, Budget: 100),
            ],
            Disclosure: WorldDisclosureTier.Replica
        );
        var baseDocument = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        var document = (baseDocument with { Admission = [signingEntry, arrivalEntry] });
        var fixture = Fixtures.FreshServer(definition: document);

        try {
            fixture.Server.TransferForwarder = forwarder;
            var mobility = new WorldMobilityIdentity(
                DepartedFrom: new WorldEntityAddress(Authority: identity.Subject, Generation: 1, Index: 4),
                Epoch: 0UL,
                Incarnation: new WorldEntityAddress(Authority: identity.Subject, Generation: 1, Index: 4)
            );
            var reservation = fixture.Server.ReserveTransfer(request: new WorldTransferReservationRequest(
                Border: "seam",
                BorderCapacity: null,
                DeadlineSourceTick: 60,
                Members: [new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 4,
                    Identity: null,
                    Mobility: mobility,
                    PreferredSlot: 4,
                    Principal: Principal.Console,
                    Source: IntentSource.Live
                )],
                PartyAllOrNothing: true,
                PeerAdmission: true,
                SourceAuthority: identity.Subject,
                SourceRateHz: 240,
                SourceTick: 0,
                TransferId: 11
            ));

            Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
            Assert.Equal(
                expected: WorldTransferStatus.Committed,
                actual: fixture.Server.CommitTransfer(
                    members: [new WorldTransferCommitMember(
                        BodyMotionProgramName: "grounded",
                        Arrival: WorldTransferArrival.Spawn,
                        PlanarVelocity: default,
                        Position: default,
                        Profile: null,
                        VerticalVelocity: default,
                        YawRadians: default
                    )],
                    reason: out var commitReason,
                    sourceAuthority: identity.Subject,
                    transferId: 11
                )
            );
            Assert.True(condition: string.IsNullOrEmpty(value: commitReason), userMessage: commitReason);
            var observedMobility = mobility.Advance();

            Assert.True(condition: fixture.Server.TryTransferredPrincipal(mobility: in observedMobility, principal: out var traveler, sourceAuthority: identity.Subject));
            // Detach the local body so B acts solely as an onward forwarding intermediary (B -> C).
            Assert.True(condition: fixture.Server.Population.TryDetachSeatForTransfer(slot: traveler.Index, profile: out _));

            var clock = new VirtualClock(start: ClaimInstant);
            var verifier = new WorldAttestedAuthenticator(trustEntries: () => fixture.Server.Definition.Admission, now: clock.GetUtcNow);
            var oracle = new LocalKeySigningOracle(key: identity.Key, subject: identity.Subject, validity: TimeSpan.FromMinutes(value: 1), now: clock.GetUtcNow);
            var host = new WorldPeerHost(server: fixture.Server, authenticator: verifier, timeProvider: clock, transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);

            PeerTestClient.StartOrSkip(host: host);
            return new ForwardedRig(arrivalEntry, fixture, forwarder, host, identity, observedMobility, oracle, signingEntry);
        } catch {
            fixture.Dispose();
            identity.Key.Dispose();
            throw;
        }
    }

    /// <summary>A server acting as an onward forwarding intermediary for one committed traveler, with a listening peer host.</summary>
    private sealed class ForwardedRig : IDisposable {
        private readonly ProbeForwarder m_forwarder;
        private PeerTestClient? m_client;
        private Puck.Networking.Peers.PeerStream? m_stream;

        internal ForwardedRig(WorldAdmissionEntry arrivalEntry, WorldFixture fixture, ProbeForwarder forwarder, WorldPeerHost host, Puck.World.Testing.TestIdentity identity, WorldMobilityIdentity observedMobility, LocalKeySigningOracle oracle, WorldAdmissionEntry signingEntry) {
            ArrivalEntry = arrivalEntry;
            Fixture = fixture;
            Host = host;
            Identity = identity;
            ObservedMobility = observedMobility;
            Oracle = oracle;
            SigningEntry = signingEntry;
            m_forwarder = forwarder;
        }

        public WorldAdmissionEntry ArrivalEntry { get; }
        public WorldFixture Fixture { get; }
        public WorldPeerHost Host { get; }
        public Puck.World.Testing.TestIdentity Identity { get; }
        public WorldMobilityIdentity ObservedMobility { get; }
        public LocalKeySigningOracle Oracle { get; }
        public WorldAdmissionEntry SigningEntry { get; }
        public Puck.Networking.Peers.PeerStream Stream => m_stream!;

        /// <summary>Authenticates a downstream peer and asks for the traveler's view, returning once the forwarder holds the request.</summary>
        public async Task ObserveAsync() {
            var authenticated = await AuthenticateFederationAsync(host: Host, oracle: Oracle);

            m_client = authenticated.Client;
            Assert.Equal(actual: authenticated.Kind, expected: ((byte)WorldFederationResponse.Authenticated));
            m_stream = m_client.GetStream();
            await using (StartMailboxPump(host: Host)) {
                await WorldFederationCodec.WriteRequestAsync(
                    body: WorldFederationCodec.EncodeTravelerObservation(request: new WorldTravelerObservation(
                        Ceiling: WorldDisclosureTier.Replica,
                        Mobility: ObservedMobility,
                        SourceAuthority: Identity.Subject
                    )),
                    ct: TestToken,
                    kind: WorldFederationRequest.ObserveTraveler,
                    stream: m_stream
                );
                await m_forwarder.Started.Task.WaitAsync(cancellationToken: TestToken);
            }
        }
        public void Dispose() {
            m_client?.Dispose();
            Host.Dispose();
            Oracle.Dispose();
            Fixture.Dispose();
            Identity.Key.Dispose();
        }
    }
    private abstract class ProbeForwarder : IWorldTransferForwarder {
        public TaskCompletionSource Started { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeForwardingAsync(WorldServer source, WorldMobilityIdentity mobility) =>
            ValueTask.FromResult<(WorldAuthorityRouteDescription?, string)>(result: (null, "unused"));
        public ValueTask<byte[]?> FetchForwardedPrototypeAsync(WorldServer source, WorldTravelerObservation request, Puck.Assets.ContentPin pin) =>
            ValueTask.FromResult<byte[]?>(result: null);
        public bool HasOnwardRoute(WorldServer source, in WorldMobilityIdentity mobility) => true;
        public abstract Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct);
        public string? TimelineResetRefusal(WorldServer source) => null;
        public bool TryForwardIntent(WorldServer source, in WorldMobilityIdentity mobility, in IntentSubmission submission, out string reason) {
            reason = string.Empty;
            return true;
        }
        public bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) {
            reason = string.Empty;
            return false;
        }
    }
    private sealed class StallingForwarder : ProbeForwarder {
        public TaskCompletionSource Canceled { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct) {
            var stalled = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = ct.Register(callback: () => {
                Canceled.TrySetResult();
                stalled.TrySetCanceled(cancellationToken: ct);
            });

            Started.TrySetResult();
            await stalled.Task.ConfigureAwait(continueOnCapturedContext: false);
            return null;
        }
    }
    /// <summary>Waits for a signal, ignoring its token, then writes one frame and records whether the write was allowed.</summary>
    private sealed class FrameAfterSignalForwarder : ProbeForwarder {
        public TaskCompletionSource<bool> FrameWritten { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteFrame { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct) {
            Started.TrySetResult();
            await WriteFrame.Task.ConfigureAwait(continueOnCapturedContext: false);
            try {
                await WorldFederationCodec.WriteResponseAsync(body: new byte[] { 1 }, ct: CancellationToken.None, kind: WorldFederationResponse.Snapshot, stream: output).ConfigureAwait(continueOnCapturedContext: false);
                FrameWritten.TrySetResult(result: true);
            } catch (OperationCanceledException) {
                FrameWritten.TrySetResult(result: false);
                throw;
            }
            return null;
        }
    }
}
