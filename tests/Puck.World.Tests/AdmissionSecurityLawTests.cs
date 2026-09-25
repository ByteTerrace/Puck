using Puck.Commands;
using Puck.Testing;
using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;

using Xunit;

using Puck.Attestation;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

using static Puck.World.Tests.AdmissionWireFixture;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the four findings a Codex security review returned against the authenticated-game-socket door
/// (<see cref="WorldAdmissionDoor"/>, <see cref="WorldPeerHost"/>, <c>WorldServer.RemintPeerAdmissionGrants</c>
/// — private, exercised only through <see cref="WorldServer.EnqueueRebuild"/> — and
/// <see cref="TrustListEntry.Validate"/>). Three of the four land here as executable laws (the fourth, Finding 3's
/// concurrent-handshake CEILING, is proven by code review + the deadline law below exercising the same accounting
/// fields — a dedicated ceiling law was judged not worth 64 additional live sockets per run). These drive the REAL
/// wire door (<see cref="WorldPeerHost"/>, a genuine <see cref="PeerTestClient"/>, a genuine signed attestation claim) rather
/// than seeding socket state directly. TryAdmitPeerConnection is also used by the trusted OAuth host adapter;
/// the socket laws still exercise the complete cryptographic Hello door. OAuth admission has separate laws.
/// </summary>
public sealed class AdmissionSecurityLawTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static async Task<(Stream Stream, ReadOnlyMemory<byte> Challenge)> ConnectPastChallengeAsync(WorldPeerHost host, PeerTestClient client, CancellationToken ct) {
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);

        await client.ConnectAsync(
            address: endpoint.Address,
            port: endpoint.Port,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        var stream = client.GetStream();

        await HandshakeWireFormat.WriteHelloAsync(
            ct: ct,
            key: WorldProtocol.WireProtocolKey,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        var challenge = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed before the Hello challenge arrived"));

        Assert.Equal(
            actual: challenge.Kind,
            expected: WorldPeerWireFormat.DownstreamKind.HelloChallenge
        );

        return (stream, challenge.Body);
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then half-closes the send
    /// side without ever writing an identity frame — a genuine disconnect. Returns whether the server closed the
    /// connection with no bytes sent back once its refusal drain expired on <paramref name="clock"/>.</summary>
    private static async Task<bool> DisconnectAfterChallengeAsync(WorldPeerHost host, VirtualClock clock, PeerTestClient client, CancellationToken ct) {
        var (stream, _) = await ConnectPastChallengeAsync(
            client: client,
            ct: ct,
            host: host
        );

        await client.CompleteWritesAsync(ct: ct);

        return await ExpireUntilClosedAsync(
            clock: clock,
            ct: ct,
            handshakeDeadline: false,
            stream: stream
        );
    }
    private static byte[] MalformedSpkiDocumentBytes() {
        var garbage = new byte[91]; // a real P-256 SPKI's typical DER length — plausible garbage, not a short-circuit

        RandomNumberGenerator.Fill(data: garbage);

        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: garbage);
        var entry = new WorldAdmissionEntry(
            Domain: domain,
            Subject: "attacker",
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: garbage),
            Grants: []
        );

        return WorldDefinitionSerialization.Serialize(definition: BuildAdmissionDocument(entry: entry));
    }
    private static async Task<QueryAnswer> RunQueryScenarioAsync(bool observe) {
        var identity = GenerateIdentity(subject: (observe
            ? "query-observer"
            : "query-no-grants"));

        try {
            var grants = (observe
                ? new[] { new WorldAdmissionGrant(
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.Body(index: PeerBodyIndex),
                    Budget: 100
                ) }
                : []
            );
            var document = BuildAdmissionDocument(entry: BuildEntry(
                grants: grants,
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );

            await using (StartPump(
                fixture: fixture,
                host: host
            )) {
                var admitted = await ConnectAndAdmitAsync(
                    host: host,
                    identity: identity,
                    ct: TestToken
                );

                using (admitted.Client) {
                    return await SubmitQueryAsync(
                        stream: admitted.Client.GetStream(),
                        query: new WorldQuery.PlayerWhere(Index: PeerBodyIndex),
                        ct: TestToken
                    );
                }
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    private static async Task<bool> RunRevokeAcrossResetScenarioAsync(bool revoke) {
        var identity = GenerateIdentity(subject: "reset-peer");

        try {
            var subject = GrantSubject.Body(index: PeerBodyIndex);
            var grant = new WorldAdmissionGrant(
                Capability: WorldCapability.Drive,
                Subject: subject,
                Budget: 100
            );
            var entry = BuildEntry(
                grants: [grant],
                identity: identity
            );
            var document = BuildAdmissionDocument(entry: entry);

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
            var admitted = await AdmitAsync(
                fixture: fixture,
                host: host,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            using (admitted.Client) {
                var peer = Principal.Peer(
                    index: admitted.PeerIndex,
                    generation: admitted.Generation
                );

                Assert.True(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Drive,
                        principal: peer,
                        subject: subject
                    ).IsAllowed,
                    userMessage: "the admission mint was expected to hold Drive/body:4 immediately after connecting"
                );

                if (revoke) {
                    fixture.Server.Revoke(
                        grant: new WorldGrant(
                            Grantee: peer,
                            Capability: WorldCapability.Drive,
                            Subject: subject,
                            Exclusive: false
                        ),
                        actor: Principal.Console
                    );
                    fixture.Step();

                    Assert.False(
                        condition: fixture.Server.Grants.Allows(
                            capability: WorldCapability.Drive,
                            principal: peer,
                            subject: subject
                        ).IsAllowed,
                        userMessage: "the live revoke was expected to take effect immediately, before any rebuild"
                    );
                }

                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(
                        Kind: WorldRebuildKind.Reset,
                        Definition: null,
                        PathHint: null,
                        Force: false
                    ),
                    principal: Principal.Console
                );
                fixture.Step();

                return fixture.Server.Grants.Allows(
                    capability: WorldCapability.Drive,
                    principal: peer,
                    subject: subject
                ).IsAllowed;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then writes
    /// <paramref name="body"/> as a raw length-prefixed HelloIdentity frame (bypassing
    /// <see cref="HandshakeWireFormat.WriteHelloIdentityAsync"/>'s own grammar so a deliberately malformed shape can
    /// be sent) and returns the door's downstream reply.</summary>
    private static async Task<(WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)> SendRawIdentityFrameAsync(WorldPeerHost host, PeerTestClient client, byte[] body, CancellationToken ct) {
        var (stream, _) = await ConnectPastChallengeAsync(
            client: client,
            ct: ct,
            host: host
        );
        var frame = new byte[checked((sizeof(uint) + body.Length))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: frame,
            value: checked((uint)body.Length)
        );
        body.CopyTo(
            array: frame,
            index: sizeof(uint)
        );

        await stream.WriteAsync(
            buffer: frame,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        var reply = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed with no reply to the malformed HelloIdentity frame"));

        return (reply.Kind, reply.Body);
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then writes a length
    /// prefix declaring <paramref name="declaredBodyLength"/> bytes, writes only
    /// <paramref name="actualBodyBytes"/> of that body, and half-closes the send side — a peer that commits to a
    /// frame and then abandons it. Returns the door's downstream reply.</summary>
    private static async Task<(WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)> SendTruncatedIdentityFrameAsync(WorldPeerHost host, PeerTestClient client, int declaredBodyLength, int actualBodyBytes, CancellationToken ct) {
        var (stream, _) = await ConnectPastChallengeAsync(
            client: client,
            ct: ct,
            host: host
        );
        var prefix = new byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: prefix,
            value: ((uint)declaredBodyLength)
        );
        await stream.WriteAsync(
            buffer: prefix,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        var partialBody = new byte[actualBodyBytes];

        await stream.WriteAsync(
            buffer: partialBody,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        await client.CompleteWritesAsync(ct: ct);

        var reply = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed with no reply to the truncated HelloIdentity frame"));

        return (reply.Kind, reply.Body);
    }
    private static bool TryBoot(byte[] bytes) {
        try {
            _ = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

            return true;
        } catch (InvalidDataException) {
            return false;
        }
    }
    private static byte[] ValidSpkiDocumentBytes() {
        var identity = GenerateIdentity(subject: "valid-peer");

        try {
            return WorldDefinitionSerialization.Serialize(definition: BuildAdmissionDocument(entry: BuildEntry(
                grants: [],
                identity: identity
            )));
        } finally {
            identity.Key.Dispose();
        }
    }

    /// <summary>An admission row rejected by the live grant table was never held and therefore was never explicitly
    /// revoked. Once the conflicting exclusive reservation disappears, the next rebuild must retry the CURRENT
    /// admission policy and install it. Treating every baseline absence as a revoke makes this stay denied forever.</summary>
    [Fact]
    public async Task AdmissionMintRejectedByConflict_IsRetriedAfterTheConflictIsRemoved() {
        var identity = GenerateIdentity(subject: "conflict-retry-peer");

        try {
            var body = GrantSubject.Body(index: PeerBodyIndex);
            var observe = new WorldAdmissionGrant(
                Capability: WorldCapability.Observe,
                Subject: body,
                Budget: 100
            );
            var document = BuildAdmissionDocument(entry: BuildEntry(
                grants: [observe],
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: document);
            var blocker = new WorldGrant(
                Grantee: Principal.Seat(slot: 0),
                Capability: WorldCapability.Observe,
                Subject: body,
                Exclusive: true
            );

            fixture.Server.Grant(
                grant: blocker,
                actor: Principal.Console
            );

            using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
            var admitted = await AdmitAsync(
                fixture: fixture,
                host: host,
                identity: identity
            );

            using (admitted.Client) {
                var peer = Principal.Peer(
                    index: admitted.PeerIndex,
                    generation: admitted.Generation
                );

                Assert.False(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Observe,
                        principal: peer,
                        subject: body
                    ).IsAllowed,
                    userMessage: "the conflicting exclusive hold was expected to reject the admission mint"
                );

                fixture.Server.Revoke(
                    grant: blocker,
                    actor: Principal.Console
                );
                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(
                        Kind: WorldRebuildKind.Reset,
                        Definition: null,
                        PathHint: null,
                        Force: false
                    ),
                    principal: Principal.Console
                );
                fixture.Step();

                Assert.True(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Observe,
                        principal: peer,
                        subject: body
                    ).IsAllowed,
                    userMessage: "a grant-door conflict refusal was remembered as though it were an explicit peer-grant revoke"
                );
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>A wire-shape-malformed HelloIdentity frame must draw a named "identity-refused: …" reply, never a
    /// silent close — the door's own comment claims every refusal here is spelled by name, and a malformed frame is
    /// exactly the case a shared null-for-everything read used to miss (a malformed frame and a genuine disconnect
    /// were indistinguishable, so both closed silently). The refusal reason must never echo the attacker-supplied
    /// bytes that made the frame malformed. The control, run against the same host, is a peer that disconnects
    /// cleanly right after the challenge — that must stay silent, which is correct behavior, not a regression to fix.</summary>
    [Fact]
    public async Task MalformedHelloIdentityFrame_DrawsNamedRefusal_ControlCleanDisconnectStaysSilent() {
        using var fixture = Fixtures.FreshServer();
        using var host = StartHost(
            clock: out var clock,
            server: fixture.Server
        );
        var testCt = TestToken;
        var marker = "ATTACKER-SUPPLIED-MARKER-3ee19c";
        var malformedBody = new byte[] { 5 }.Concat(second: System.Text.Encoding.UTF8.GetBytes(s: marker)).ToArray();

        using (var malformedClient = new PeerTestClient()) {
            var reply = await SendRawIdentityFrameAsync(
                body: malformedBody,
                client: malformedClient,
                ct: testCt,
                host: host
            );
            var text = WorldPeerWireFormat.DecodeText(body: reply.Body.Span);

            Assert.Equal(
                actual: reply.Kind,
                expected: WorldPeerWireFormat.DownstreamKind.HelloRefused
            );
            Assert.Contains(
                actualString: text,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "identity-refused: "
            );
            Assert.DoesNotContain(
                actualString: text,
                expectedSubstring: marker
            );
            // The refused connection stays open for its drain, then closes with nothing after the refusal.
            Assert.True(condition: await ExpireUntilClosedAsync(
                clock: clock,
                ct: testCt,
                handshakeDeadline: false,
                stream: malformedClient.GetStream()
            ));
        }

        using (var cleanClient = new PeerTestClient()) {
            var closedSilently = await DisconnectAfterChallengeAsync(
                client: cleanClient,
                clock: clock,
                ct: testCt,
                host: host
            );

            Assert.True(
                condition: closedSilently,
                userMessage: "a genuine disconnect while awaiting the HelloIdentity frame drew a reply instead of closing silently"
            );
        }
    }
    /// <summary>Finding 4 (P2): an <c>admission</c> entry whose <c>publicKey</c> base64-decodes but does not import
    /// as a usable key on the algorithm's own curve must refuse AT BOOT, by name — never merely at the first live
    /// connection attempt. The control is the identical document with a REAL, freshly generated P-256 key in the
    /// same slot, which must boot clean.</summary>
    [Fact]
    public void MalformedSpkiAdmissionEntry_RefusesAtBoot_ControlBootsClean() {
        Laws.RefusalWithControl(
            lawId: "admission.malformed-spki-refuses-at-boot",
            deniedOutcome: static () => TryBoot(bytes: MalformedSpkiDocumentBytes()),
            controlOutcome: static () => TryBoot(bytes: ValidSpkiDocumentBytes())
        );
    }
    /// <summary>Finding 1 (P1): a peer's admission-minted grant, explicitly revoked live, must stay revoked across
    /// <c>world.reset</c> — the rebuild's re-authorization must consult the CURRENT admission policy and the
    /// pre-wipe live grant table, never blindly replay the connection-time templates. The control is the SAME
    /// scenario with the revoke step skipped: an un-revoked peer's grant is expected to survive the reset via the
    /// ordinary re-authorization path (proving the fix does not just refuse to remint anything).
    /// <para><b>Break-once evidence (recorded, not re-run by CI):</b> reverting
    /// <c>WorldServer.RemintPeerAdmissionGrants</c> to its pre-fix shape (an unconditional replay of
    /// <c>WorldPopulation.PeerAdmissionInstalledGrantTemplates</c>, ignoring both the current policy and any live revoke)
    /// turns the denied leg red — the revoked grant resurrects, and this law catches it — while the control leg
    /// stays green either way, which is exactly why the pair, not either leg alone, is the proof.</para>
    /// </summary>
    [Fact]
    public void PeerRevokedGrant_StaysRevokedAcrossWorldReset_ControlUnrevokedPeerKeepsGrants() {
        Laws.RefusalWithControl(
            lawId: "admission.peer-revoke-survives-world-reset",
            deniedOutcome: static () => RunRevokeAcrossResetScenarioAsync(revoke: true).GetAwaiter().GetResult(),
            controlOutcome: static () => RunRevokeAcrossResetScenarioAsync(revoke: false).GetAwaiter().GetResult()
        );
    }
    /// <summary>A policy-added grant becomes part of the next rebuild's revocation baseline. Otherwise a live revoke
    /// after the first rebuild is invisible to the connection-time-only baseline and the second rebuild resurrects
    /// it.</summary>
    [Fact]
    public async Task PolicyAddedGrant_RevokedLive_StaysRevokedAcrossTheFollowingReset() {
        var identity = GenerateIdentity(subject: "successive-rebuild-peer");

        try {
            var body = GrantSubject.Body(index: PeerBodyIndex);
            var drive = new WorldAdmissionGrant(
                Capability: WorldCapability.Drive,
                Subject: body,
                Budget: 100
            );
            var observe = new WorldAdmissionGrant(
                Capability: WorldCapability.Observe,
                Subject: body,
                Budget: 100
            );
            var initial = BuildAdmissionDocument(entry: BuildEntry(
                grants: [drive],
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: initial);
            using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
            var admitted = await AdmitAsync(
                fixture: fixture,
                host: host,
                identity: identity
            );

            using (admitted.Client) {
                var peer = Principal.Peer(
                    index: admitted.PeerIndex,
                    generation: admitted.Generation
                );
                var widened = BuildAdmissionDocument(entry: BuildEntry(
                    grants: [drive, observe],
                    identity: identity
                ));
                var widenedHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: widened));

                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(
                        ContentHash: widenedHash,
                        Definition: widened,
                        Force: true,
                        Kind: WorldRebuildKind.Load,
                        PathHint: "successive-rebuild.world.json"
                    ),
                    principal: Principal.Console
                );
                fixture.Step();

                Assert.True(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Observe,
                        principal: peer,
                        subject: body
                    ).IsAllowed,
                    userMessage: "the widened admission policy did not add Observe/body:4 on its first rebuild"
                );

                fixture.Server.Revoke(
                    grant: new WorldGrant(
                        Grantee: peer,
                        Capability: WorldCapability.Observe,
                        Subject: body,
                        Exclusive: false
                    ),
                    actor: Principal.Console
                );
                fixture.Step();

                Assert.False(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Observe,
                        principal: peer,
                        subject: body
                    ).IsAllowed,
                    userMessage: "the live revoke did not remove the policy-added Observe grant"
                );

                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(
                        Kind: WorldRebuildKind.Reset,
                        Definition: null,
                        PathHint: null,
                        Force: false
                    ),
                    principal: Principal.Console
                );
                fixture.Step();

                Assert.False(
                    condition: fixture.Server.Grants.Allows(
                        capability: WorldCapability.Observe,
                        principal: peer,
                        subject: body
                    ).IsAllowed,
                    userMessage: "the second rebuild resurrected a policy-added grant that had been explicitly revoked live"
                );
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>The persisted peer-admission event must restore verified identity during offline replay. The recorded
    /// reset re-authorizes that peer, after which a peer-principal SnapPose proves the grant still exists on both live
    /// and replay sides. Omitting identity metadata makes the replay drop Drive at reset and diverge on the command.</summary>
    [Fact]
    public async Task RecordedRemoteAdmissionFollowedByReset_ReplaysWithTheSameAuthorization() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");

        var identity = GenerateIdentity(subject: "replay-admission-peer");
        var name = $"admission-replay-{Guid.NewGuid():N}";

        try {
            var body = GrantSubject.Body(index: PeerBodyIndex);
            var drive = new WorldAdmissionGrant(
                Capability: WorldCapability.Drive,
                Subject: body,
                Budget: 100
            );
            var document = BuildAdmissionDocument(entry: BuildEntry(
                grants: [drive],
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: document);
            var transport = new LoopbackTransport(server: fixture.Server);
            var tape = new WorldReplayTape(
                stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
                liveServer: fixture.Server,
                profiles: fixture.Server.Profiles,
                transport: transport,
                engines: [],
                machineHostFactory: Fixtures.MachineHostFactory,
                addonHostFactory: static (_, _) => new NullAddonHost()
            );

            Assert.True(
                condition: tape.TryBeginRecording(
                    name: name,
                    refusal: out var refusal
                ),
                userMessage: $"refused to arm admission replay: {refusal}"
            );

            using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
            var admitted = await AdmitAsync(
                fixture: fixture,
                host: host,
                identity: identity,
                tape: tape
            );

            using (admitted.Client) {
                var peer = Principal.Peer(
                    index: admitted.PeerIndex,
                    generation: admitted.Generation
                );

                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(
                        Kind: WorldRebuildKind.Reset,
                        Definition: null,
                        PathHint: null,
                        Force: false
                    ),
                    principal: Principal.Console
                );
                fixture.Step();
                tape.NoteTick();

                transport.SubmitCommand(command: new WorldCommand.SnapPose(
                    Principal: peer,
                    EntityIndex: PeerBodyIndex,
                    Position: new Vector3(
                        x: 17f,
                        y: 3f,
                        z: -11f
                    ),
                    YawRadians: 0.25f,
                    PitchRadians: -0.125f,
                    RollRadians: 0.0625f,
                    Mode: SnapPoseMode.Pose
                ));
                fixture.Step();
                tape.NoteTick();

                var result = tape.StopRecording();

                Assert.Null(@object: result.VerifyFault);
                Assert.NotNull(@object: result.Verdict);
                Assert.True(
                    condition: result.Verdict!.Value.Match,
                    userMessage: result.Verdict.Value.Describe()
                );
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>Finding 3 (P1): a connection that completes the Hello version door but then withholds its identity
    /// frame entirely must be closed by the server's OWN handshake deadline — never held open indefinitely. The
    /// control, run first against the SAME host, is an ordinary connection that completes the whole handshake
    /// promptly and is admitted — proving the deadline machinery does not interfere with a legitimate peer. The
    /// deadline uses a controlled timer, so the law observes expiry without waiting through the production timeout.</summary>
    [Fact]
    public async Task StalledPreAdmissionHandshake_ClosesAfterDeadline_ControlPromptHandshakeAdmits() {
        var identity = GenerateIdentity(subject: "deadline-peer");

        try {
            var entry = BuildEntry(
                grants: [],
                identity: identity
            );
            var document = BuildAdmissionDocument(entry: entry);

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = StartHost(
                clock: out var clock,
                server: fixture.Server
            );
            var testCt = TestToken;

            await using (StartPump(
                fixture: fixture,
                host: host
            )) {
                // CONTROL — an ordinary prompt handshake against this SAME host is admitted, not refused.
                var admitted = await ConnectAndAdmitAsync(
                    ct: testCt,
                    host: host,
                    identity: identity
                );

                using var admittedClient = admitted.Client;

                // DENIED — connect, complete ONLY the Hello version door, then send nothing further at all. The
                // server must close this on its own; no further bytes travel in either direction.
                using var stalling = new PeerTestClient();

                var (stallingStream, _) = await ConnectPastChallengeAsync(
                    client: stalling,
                    ct: testCt,
                    host: host
                );

                // The handshake deadline elapses for every connection this host has opened, the admitted one included:
                // an admitted connection is no longer subject to it, so only the stalled connection may close.
                Assert.True(
                    condition: await ExpireUntilClosedAsync(
                        clock: clock,
                        ct: testCt,
                        handshakeDeadline: true,
                        stream: stallingStream
                    ),
                    userMessage: "a connection that never sent its identity frame was expected to be closed by the handshake deadline when its deadline expired, but it was still open"
                );
                Assert.Equal(
                    actual: Assert.Single(collection: host.Connections).PeerIndex,
                    expected: admitted.PeerIndex
                );
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>A TCP peer with no Observe grant must not inherit the trusted in-process query surface merely by
    /// completing admission. The control is the same query under an authored Observe/body grant.</summary>
    [Fact]
    public async Task TcpQuery_RequiresObserveOverItsAddressedSubject_ControlObserveGrantReads() {
        var denied = await RunQueryScenarioAsync(observe: false);
        var allowed = await RunQueryScenarioAsync(observe: true);

        Assert.True(
            condition: denied.Refused,
            userMessage: $"a zero-grant remote peer read body.where: {denied.Text}"
        );
        Assert.Contains(
            expectedSubstring: "cannot observe body:4",
            actualString: denied.Text
        );
        Assert.False(
            condition: allowed.Refused,
            userMessage: $"an Observe/body:4 peer was refused body.where: {allowed.Text}"
        );
        Assert.Contains(
            expectedSubstring: "body.where: body:4",
            actualString: allowed.Text
        );
    }
    /// <summary>A length prefix that declares a HelloIdentity frame, followed by a half-close before the body
    /// completes, must draw the same named "identity-refused: …" reply as any other malformed frame — the peer
    /// committed to a frame and then abandoned it, which is not the clean pre-frame disconnect the door's own
    /// comment carves out as silent.</summary>
    [Fact]
    public async Task TruncatedDeclaredFrame_DrawsNamedRefusal_NotSilentDisconnect() {
        using var fixture = Fixtures.FreshServer();
        using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
        var testCt = TestToken;

        using var client = new PeerTestClient();
        var reply = await SendTruncatedIdentityFrameAsync(
            actualBodyBytes: 4,
            client: client,
            ct: testCt,
            declaredBodyLength: 10,
            host: host
        );
        var text = WorldPeerWireFormat.DecodeText(body: reply.Body.Span);

        Assert.Equal(
            actual: reply.Kind,
            expected: WorldPeerWireFormat.DownstreamKind.HelloRefused
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "identity-refused: the connection closed before the declared frame's body completed"
        );
    }
    /// <summary>The same deadline must cover the handoff from completed identity verification to tick-thread
    /// population admission. A paused/rate-0 host may never drain that queue; expiry must close the connection and a
    /// later drain must skip the orphaned work rather than admitting a body with no socket.</summary>
    [Fact]
    public async Task VerifiedIdentityQueuedWithoutTickDrain_ExpiresAndCannotAdmitLater() {
        var identity = GenerateIdentity(subject: "queued-deadline-peer");

        try {
            var document = BuildAdmissionDocument(entry: BuildEntry(
                grants: [],
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = StartHost(
                clock: out var clock,
                server: fixture.Server
            );
            using var client = new PeerTestClient();
            var testCt = TestToken;

            var (stream, challenge) = await ConnectPastChallengeAsync(
                client: client,
                ct: testCt,
                host: host
            );
            var admissionQueued = host.WaitForPendingWorkAsync(cancellationToken: testCt);

            Assert.False(
                condition: admissionQueued.IsCompleted,
                userMessage: "tick-thread work was queued before the identity frame was sent"
            );
            await WriteIdentityResponseAsync(
                challenge: challenge,
                ct: testCt,
                identity: identity,
                stream: stream
            );
            // The verified admission reaches the tick-thread queue; its own clock then expires without a drain.
            await admissionQueued;
            var closed = await ExpireUntilClosedAsync(
                clock: clock,
                ct: testCt,
                handshakeDeadline: true,
                stream: stream
            );

            Assert.True(
                condition: closed,
                userMessage: "a fully verified identity remained connected indefinitely while tick-thread admission was not draining"
            );

            // Resuming the tick drain after expiry must discard the canceled admission item.
            host.DrainPending();

            Assert.False(
                condition: fixture.Server.Population.IsAdmittedPeer(bodyIndex: PeerBodyIndex),
                userMessage: "the expired queue item admitted an orphaned remote body when draining resumed"
            );
            Assert.Empty(collection: host.Connections);
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>A HelloIdentity frame that decodes cleanly — a zero-length chain and a well-formed claim attestation —
    /// but carries extra bytes after the claim must draw the same named "identity-refused: …" reply as any other
    /// grammar violation, never reach identity verification with the trailing bytes silently ignored.</summary>
    [Fact]
    public async Task WellFormedFrameWithTrailingBytes_DrawsNamedRefusal() {
        using var fixture = Fixtures.FreshServer();
        using var host = StartHost(
            clock: out _,
            server: fixture.Server
        );
        var testCt = TestToken;
        var claim = new byte[] { 1, 2, 3, 4 };
        var claimEnvelope = new byte[(sizeof(uint) + claim.Length)];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: claimEnvelope,
            value: ((uint)claim.Length)
        );
        claim.CopyTo(
            array: claimEnvelope,
            index: sizeof(uint)
        );

        var trailing = new byte[] { 0xAA, 0xBB, 0xCC };
        // chainCount = 0, then the length-prefixed claim attestation — a well-formed frame on its own — followed by
        // bytes the grammar never accounts for.
        var body = new byte[] { 0 }.Concat(second: claimEnvelope).Concat(second: trailing).ToArray();

        using var client = new PeerTestClient();
        var reply = await SendRawIdentityFrameAsync(
            body: body,
            client: client,
            ct: testCt,
            host: host
        );
        var text = WorldPeerWireFormat.DecodeText(body: reply.Body.Span);

        Assert.Equal(
            actual: reply.Kind,
            expected: WorldPeerWireFormat.DownstreamKind.HelloRefused
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "identity-refused: the frame carries trailing bytes after the claim attestation"
        );
    }
}
