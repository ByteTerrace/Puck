using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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
/// (<see cref="WorldAdmissionDoor"/>, <see cref="WorldTcpHost"/>, <c>WorldServer.RemintPeerAdmissionGrants</c>
/// — private, exercised only through <see cref="WorldServer.EnqueueRebuild"/> — and
/// <see cref="TrustListEntry.Validate"/>). Three of the four land here as executable laws (the fourth, Finding 3's
/// concurrent-handshake CEILING, is proven by code review + the deadline law below exercising the same accounting
/// fields — a dedicated ceiling law was judged not worth 64 additional live sockets per run). These drive the REAL
/// wire door (<see cref="WorldTcpHost"/>, a genuine <see cref="TcpClient"/>, a genuine signed attestation claim) rather
/// than poking server-internal state directly — <c>WorldServer.TryAdmitPeerConnection</c> is <c>internal</c>
/// and deliberately has no test-only public seam (CLAUDE.md's IVT ruling: widen the member or don't reach it, and
/// this member should NOT be public — it is a security-relevant door, not a utility), so the only faithful way to
/// seed a "connected, verified peer" is to actually connect and verify one.
/// </summary>
public sealed class AdmissionSecurityLawTests {
    /// <summary>A TCP peer with no Observe grant must not inherit the trusted in-process query surface merely by
    /// completing admission. The control is the same query under an authored Observe/body grant.</summary>
    [Fact]
    public async Task TcpQuery_RequiresObserveOverItsAddressedSubject_ControlObserveGrantReads() {
        var denied = await RunQueryScenarioAsync(observe: false);
        var allowed = await RunQueryScenarioAsync(observe: true);

        Assert.True(condition: denied.Refused, userMessage: $"a zero-grant remote peer read body.where: {denied.Text}");
        Assert.Contains(expectedSubstring: "cannot observe body:4", actualString: denied.Text);
        Assert.False(condition: allowed.Refused, userMessage: $"an Observe/body:4 peer was refused body.where: {allowed.Text}");
        Assert.Contains(expectedSubstring: "body.where: body:4", actualString: allowed.Text);
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
            controlOutcome: static () => RunRevokeAcrossResetScenarioAsync(revoke: false).GetAwaiter().GetResult());
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
            controlOutcome: static () => TryBoot(bytes: ValidSpkiDocumentBytes()));
    }
    /// <summary>Finding 3 (P1): a connection that completes the Hello version door but then withholds its identity
    /// frame entirely must be closed by the server's OWN handshake deadline — never held open indefinitely. The
    /// control, run first against the SAME host, is an ordinary connection that completes the whole handshake
    /// promptly and is admitted — proving the deadline machinery does not interfere with a legitimate peer. The
    /// deadline's exact value is a private implementation constant (<c>WorldTcpHost.HandshakeDeadline</c>, 10s at
    /// the time of writing); this law waits generously past it (20s) rather than pinning the number, per this
    /// suite's own red-line against asserting internal structure.</summary>
    [Fact]
    public async Task StalledPreAdmissionHandshake_ClosesAfterDeadline_ControlPromptHandshakeAdmits() {
        var identity = GenerateIdentity(subject: "deadline-peer");

        try {
            var entry = BuildEntry(grants: [], identity: identity);
            var document = BuildAdmissionDocument(entry: entry);

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldTcpHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);

            var testCt = TestContext.Current.CancellationToken;

            try {
                // CONTROL — an ordinary prompt handshake against this SAME host is admitted, not refused.
                using (var promptCts = Laws.SocketDeadline()) {
                    var admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: promptCts.Token);

                    admitted.Client.Dispose();
                }

                // DENIED — connect, complete ONLY the Hello version door, then send nothing further at all. The
                // server must close this on its own; no further bytes travel in either direction.
                var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);

                using var stalling = new TcpClient();

                await stalling.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: testCt);

                var stallingStream = stalling.GetStream();

                await HandshakeWireFormat.WriteHelloAsync(ct: testCt, key: WorldProtocol.WireProtocolKey, stream: stallingStream);

                var challenge = await WorldTcpWireFormat.TryReadDownstreamAsync(ct: testCt, stream: stallingStream);

                Assert.NotNull(@object: challenge);
                Assert.Equal(expected: WorldTcpWireFormat.DownstreamKind.HelloChallenge, actual: challenge!.Value.Kind);

                var closed = false;
                var probe = new byte[1];

                try {
                    // Longer than WorldTcpHost.HandshakeDeadline: this read is waiting for that deadline to fire.
                    using var waitCts = Laws.SocketDeadline();

                    var read = await stallingStream.ReadAsync(buffer: probe, cancellationToken: waitCts.Token);

                    closed = (read == 0);
                } catch (IOException) {
                    closed = true;
                } catch (SocketException) {
                    closed = true;
                } catch (OperationCanceledException) when (!testCt.IsCancellationRequested) {
                    closed = false;
                }

                Assert.True(condition: closed, userMessage: "a connection that never sent its identity frame was expected to be closed by the handshake deadline within 20s, but it was still open");
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>The same deadline must cover the handoff from completed identity verification to tick-thread
    /// population admission. A paused/rate-0 host may never drain that queue; expiry must close the connection and a
    /// later drain must skip the orphaned work rather than admitting a body with no socket.</summary>
    [Fact]
    public async Task VerifiedIdentityQueuedWithoutTickDrain_ExpiresAndCannotAdmitLater() {
        var identity = GenerateIdentity(subject: "queued-deadline-peer");

        try {
            var document = BuildAdmissionDocument(entry: BuildEntry(grants: [], identity: identity));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldTcpHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var client = new TcpClient();
            // Longer than WorldTcpHost.HandshakeDeadline: the close this law waits for is that deadline firing.
            using var testCts = Laws.SocketDeadline();
            var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);

            await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: testCts.Token);

            var stream = client.GetStream();

            await HandshakeWireFormat.WriteHelloAsync(stream: stream, key: WorldProtocol.WireProtocolKey, ct: testCts.Token);

            var challenge = ((await WorldTcpWireFormat.TryReadDownstreamAsync(stream: stream, ct: testCts.Token))
                ?? throw new InvalidOperationException(message: "connection closed before the challenge"));

            Assert.Equal(actual: challenge.Kind, expected: WorldTcpWireFormat.DownstreamKind.HelloChallenge);

            await WriteIdentityResponseAsync(stream: stream, identity: identity, challenge: challenge.Body, ct: testCts.Token);

            // No DrainPending call occurs before this read. The queue hop itself must therefore expire and close.
            var closed = await WaitForCloseAsync(stream: stream, ct: testCts.Token);

            Assert.True(condition: closed, userMessage: "a fully verified identity remained connected indefinitely while tick-thread admission was not draining");

            // Resuming the tick drain after expiry must discard the canceled admission item.
            host.DrainPending();

            Assert.False(condition: fixture.Server.Population.IsAdmittedPeer(bodyIndex: PeerBodyIndex), userMessage: "the expired queue item admitted an orphaned remote body when draining resumed");
            Assert.Empty(collection: host.Connections);
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
        using var host = new WorldTcpHost(server: fixture.Server);

        host.Start(listen: "127.0.0.1:0");

        using var testCts = Laws.SocketDeadline();
        var testCt = testCts.Token;
        var marker = "ATTACKER-SUPPLIED-MARKER-3ee19c";
        var malformedBody = new byte[] { 5 }.Concat(second: System.Text.Encoding.UTF8.GetBytes(s: marker)).ToArray();

        using (var malformedClient = new TcpClient()) {
            var reply = await SendRawIdentityFrameAsync(body: malformedBody, client: malformedClient, ct: testCt, host: host);
            var text = WorldTcpWireFormat.DecodeText(body: reply.Body.Span);

            Assert.Equal(actual: reply.Kind, expected: WorldTcpWireFormat.DownstreamKind.HelloRefused);
            Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "identity-refused: ");
            Assert.DoesNotContain(actualString: text, expectedSubstring: marker);
        }

        using (var cleanClient = new TcpClient()) {
            var closedSilently = await DisconnectAfterChallengeAsync(client: cleanClient, ct: testCt, host: host);

            Assert.True(condition: closedSilently, userMessage: "a genuine disconnect while awaiting the HelloIdentity frame drew a reply instead of closing silently");
        }
    }
    /// <summary>A HelloIdentity frame that decodes cleanly — a zero-length chain and a well-formed claim attestation —
    /// but carries extra bytes after the claim must draw the same named "identity-refused: …" reply as any other
    /// grammar violation, never reach identity verification with the trailing bytes silently ignored.</summary>
    [Fact]
    public async Task WellFormedFrameWithTrailingBytes_DrawsNamedRefusal() {
        using var fixture = Fixtures.FreshServer();
        using var host = new WorldTcpHost(server: fixture.Server);

        host.Start(listen: "127.0.0.1:0");

        using var testCts = Laws.SocketDeadline();
        var testCt = testCts.Token;
        var claim = new byte[] { 1, 2, 3, 4 };
        var claimEnvelope = new byte[(sizeof(uint) + claim.Length)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: claimEnvelope, value: ((uint)claim.Length));
        claim.CopyTo(array: claimEnvelope, index: sizeof(uint));

        var trailing = new byte[] { 0xAA, 0xBB, 0xCC };
        // chainCount = 0, then the length-prefixed claim attestation — a well-formed frame on its own — followed by
        // bytes the grammar never accounts for.
        var body = new byte[] { 0 }.Concat(second: claimEnvelope).Concat(second: trailing).ToArray();

        using var client = new TcpClient();
        var reply = await SendRawIdentityFrameAsync(body: body, client: client, ct: testCt, host: host);
        var text = WorldTcpWireFormat.DecodeText(body: reply.Body.Span);

        Assert.Equal(actual: reply.Kind, expected: WorldTcpWireFormat.DownstreamKind.HelloRefused);
        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "identity-refused: the frame carries trailing bytes after the claim attestation");
    }
    /// <summary>A length prefix that declares a HelloIdentity frame, followed by a half-close before the body
    /// completes, must draw the same named "identity-refused: …" reply as any other malformed frame — the peer
    /// committed to a frame and then abandoned it, which is not the clean pre-frame disconnect the door's own
    /// comment carves out as silent.</summary>
    [Fact]
    public async Task TruncatedDeclaredFrame_DrawsNamedRefusal_NotSilentDisconnect() {
        using var fixture = Fixtures.FreshServer();
        using var host = new WorldTcpHost(server: fixture.Server);

        host.Start(listen: "127.0.0.1:0");

        using var testCts = Laws.SocketDeadline();
        var testCt = testCts.Token;

        using var client = new TcpClient();
        var reply = await SendTruncatedIdentityFrameAsync(actualBodyBytes: 4, client: client, ct: testCt, declaredBodyLength: 10, host: host);
        var text = WorldTcpWireFormat.DecodeText(body: reply.Body.Span);

        Assert.Equal(actual: reply.Kind, expected: WorldTcpWireFormat.DownstreamKind.HelloRefused);
        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "identity-refused: the connection closed before the declared frame's body completed");
    }
    /// <summary>A policy-added grant becomes part of the next rebuild's revocation baseline. Otherwise a live revoke
    /// after the first rebuild is invisible to the connection-time-only baseline and the second rebuild resurrects
    /// it.</summary>
    [Fact]
    public async Task PolicyAddedGrant_RevokedLive_StaysRevokedAcrossTheFollowingReset() {
        var identity = GenerateIdentity(subject: "successive-rebuild-peer");

        try {
            var body = GrantSubject.Body(index: PeerBodyIndex);
            var drive = new WorldAdmissionGrant(Capability: WorldCapability.Drive, Subject: body, Budget: 100);
            var observe = new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: body, Budget: 100);
            var initial = BuildAdmissionDocument(entry: BuildEntry(grants: [drive], identity: identity));

            using var fixture = Fixtures.FreshServer(definition: initial);
            using var host = new WorldTcpHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);
            AdmittedPeer admitted;

            try {
                using var connectCts = Laws.SocketDeadline();

                admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: connectCts.Token);
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }

            using (admitted.Client) {
                var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);
                var widened = BuildAdmissionDocument(entry: BuildEntry(grants: [drive, observe], identity: identity));
                var widenedHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: widened));

                fixture.Server.EnqueueRebuild(
                    request: new WorldRebuildRequest(ContentHash: widenedHash, Definition: widened, Force: true, Kind: WorldRebuildKind.Load, PathHint: "successive-rebuild.world.json"),
                    principal: WorldPrincipal.Console
                );
                fixture.Step();

                Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: peer, subject: body).IsAllowed, userMessage: "the widened admission policy did not add Observe/body:4 on its first rebuild");

                fixture.Server.Revoke(grant: new WorldGrant(Principal: peer, Capability: WorldCapability.Observe, Subject: body, Exclusive: false), actor: WorldPrincipal.Console);
                fixture.Step();

                Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: peer, subject: body).IsAllowed, userMessage: "the live revoke did not remove the policy-added Observe grant");

                fixture.Server.EnqueueRebuild(request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reset, Definition: null, PathHint: null, Force: false), principal: WorldPrincipal.Console);
                fixture.Step();

                Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: peer, subject: body).IsAllowed, userMessage: "the second rebuild resurrected a policy-added grant that had been explicitly revoked live");
            }
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
            var observe = new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: body, Budget: 100);
            var document = BuildAdmissionDocument(entry: BuildEntry(grants: [observe], identity: identity));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldTcpHost(server: fixture.Server);
            var blocker = new WorldGrant(Principal: WorldPrincipal.Seat(slot: 0), Capability: WorldCapability.Observe, Subject: body, Exclusive: true);

            fixture.Server.Grant(grant: blocker, actor: WorldPrincipal.Console);
            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);
            AdmittedPeer admitted;

            try {
                using var connectCts = Laws.SocketDeadline();

                admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: connectCts.Token);
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }

            using (admitted.Client) {
                var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

                Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: peer, subject: body).IsAllowed, userMessage: "the conflicting exclusive hold was expected to reject the admission mint");

                fixture.Server.Revoke(grant: blocker, actor: WorldPrincipal.Console);
                fixture.Server.EnqueueRebuild(request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reset, Definition: null, PathHint: null, Force: false), principal: WorldPrincipal.Console);
                fixture.Step();

                Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: peer, subject: body).IsAllowed, userMessage: "a grant-door conflict refusal was remembered as though it were an explicit peer-grant revoke");
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
        Fixtures.SkipIfReplayDirectoryUnwritable();

        var identity = GenerateIdentity(subject: "replay-admission-peer");
        var name = $"admission-replay-{Guid.NewGuid():N}";

        try {
            var body = GrantSubject.Body(index: PeerBodyIndex);
            var drive = new WorldAdmissionGrant(Capability: WorldCapability.Drive, Subject: body, Budget: 100);
            var document = BuildAdmissionDocument(entry: BuildEntry(grants: [drive], identity: identity));

            using var fixture = Fixtures.FreshServer(definition: document);
            var transport = new LoopbackTransport(server: fixture.Server);
            var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
            using var host = new WorldTcpHost(server: fixture.Server);

            Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm admission replay: {refusal}");

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token, tape: tape);
            AdmittedPeer admitted;

            try {
                using var connectCts = Laws.SocketDeadline();

                admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: connectCts.Token);
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }

            using (admitted.Client) {
                var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

                fixture.Server.EnqueueRebuild(request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reset, Definition: null, PathHint: null, Force: false), principal: WorldPrincipal.Console);
                fixture.Step();
                tape.NoteTick();

                transport.SubmitCommand(command: new WorldCommand.SnapPose(Principal: peer, EntityIndex: PeerBodyIndex, Position: new Vector3(x: 17f, y: 3f, z: -11f), YawRadians: 0.25f, PitchRadians: -0.125f, RollRadians: 0.0625f, Mode: SnapPoseMode.Pose));
                fixture.Step();
                tape.NoteTick();

                var result = tape.StopRecording();

                Assert.Null(@object: result.VerifyFault);
                Assert.NotNull(@object: result.Verdict);
                Assert.True(condition: result.Verdict!.Value.Match, userMessage: result.Verdict.Value.Describe());
            }
        } finally {
            identity.Key.Dispose();

            var path = WorldReplayTape.PathFor(name: name);

            if (File.Exists(path: path)) {
                File.Delete(path: path);
            }
        }
    }

    private static async Task<QueryAnswer> RunQueryScenarioAsync(bool observe) {
        var identity = GenerateIdentity(subject: (observe ? "query-observer" : "query-no-grants"));

        try {
            var grants = (observe
                ? new[] { new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.Body(index: PeerBodyIndex), Budget: 100) }
                : []);
            var document = BuildAdmissionDocument(entry: BuildEntry(grants: grants, identity: identity));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldTcpHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);

            try {
                using var requestCts = Laws.SocketDeadline();
                var admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: requestCts.Token);

                using (admitted.Client) {
                    return await SubmitQueryAsync(stream: admitted.Client.GetStream(), query: new WorldQuery.PlayerWhere(Index: PeerBodyIndex), ct: requestCts.Token);
                }
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then writes
    /// <paramref name="body"/> as a raw length-prefixed HelloIdentity frame (bypassing
    /// <see cref="HandshakeWireFormat.WriteHelloIdentityAsync"/>'s own grammar so a deliberately malformed shape can
    /// be sent) and returns the door's downstream reply.</summary>
    private static async Task<(WorldTcpWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)> SendRawIdentityFrameAsync(WorldTcpHost host, TcpClient client, byte[] body, CancellationToken ct) {
        var stream = await ConnectPastChallengeAsync(client: client, ct: ct, host: host);
        var frame = new byte[checked((sizeof(uint) + body.Length))];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: frame, value: checked((uint)body.Length));
        body.CopyTo(array: frame, index: sizeof(uint));

        await stream.WriteAsync(buffer: frame, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        var reply = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed with no reply to the malformed HelloIdentity frame"));

        return (reply.Kind, reply.Body);
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then writes a length
    /// prefix declaring <paramref name="declaredBodyLength"/> bytes, writes only
    /// <paramref name="actualBodyBytes"/> of that body, and half-closes the send side — a peer that commits to a
    /// frame and then abandons it. Returns the door's downstream reply.</summary>
    private static async Task<(WorldTcpWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)> SendTruncatedIdentityFrameAsync(WorldTcpHost host, TcpClient client, int declaredBodyLength, int actualBodyBytes, CancellationToken ct) {
        var stream = await ConnectPastChallengeAsync(client: client, ct: ct, host: host);
        var prefix = new byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: prefix, value: ((uint)declaredBodyLength));
        await stream.WriteAsync(buffer: prefix, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        var partialBody = new byte[actualBodyBytes];

        await stream.WriteAsync(buffer: partialBody, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        client.Client.Shutdown(how: SocketShutdown.Send);

        var reply = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed with no reply to the truncated HelloIdentity frame"));

        return (reply.Kind, reply.Body);
    }
    /// <summary>Connects, completes the Hello version door, reads the identity challenge, then half-closes the send
    /// side without ever writing an identity frame — a genuine disconnect. Returns whether the server closed the
    /// connection with no bytes sent back.</summary>
    private static async Task<bool> DisconnectAfterChallengeAsync(WorldTcpHost host, TcpClient client, CancellationToken ct) {
        var stream = await ConnectPastChallengeAsync(client: client, ct: ct, host: host);

        client.Client.Shutdown(how: SocketShutdown.Send);

        return await WaitForCloseAsync(ct: ct, stream: stream);
    }
    private static async Task<NetworkStream> ConnectPastChallengeAsync(WorldTcpHost host, TcpClient client, CancellationToken ct) {
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);

        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        var stream = client.GetStream();

        await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldProtocol.WireProtocolKey, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

        var challenge = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
            ?? throw new InvalidOperationException(message: "connection closed before the Hello challenge arrived"));

        Assert.Equal(actual: challenge.Kind, expected: WorldTcpWireFormat.DownstreamKind.HelloChallenge);

        return stream;
    }
    private static async Task<bool> WaitForCloseAsync(NetworkStream stream, CancellationToken ct) {
        var probe = new byte[1];

        try {
            return (await stream.ReadAsync(buffer: probe, cancellationToken: ct) == 0);
        } catch (Exception exception) when ((exception is IOException or SocketException)) {
            return true;
        }
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
    private static byte[] ValidSpkiDocumentBytes() {
        var identity = GenerateIdentity(subject: "valid-peer");

        try {
            return WorldDefinitionSerialization.Serialize(definition: BuildAdmissionDocument(entry: BuildEntry(grants: [], identity: identity)));
        } finally {
            identity.Key.Dispose();
        }
    }
    private static bool TryBoot(byte[] bytes) {
        try {
            _ = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

            return true;
        } catch (InvalidDataException) {
            return false;
        }
    }
    private static async Task<bool> RunRevokeAcrossResetScenarioAsync(bool revoke) {
        var identity = GenerateIdentity(subject: "reset-peer");

        try {
            var subject = GrantSubject.Body(index: PeerBodyIndex);
            var grant = new WorldAdmissionGrant(Capability: WorldCapability.Drive, Subject: subject, Budget: 100);
            var entry = BuildEntry(grants: [grant], identity: identity);
            var document = BuildAdmissionDocument(entry: entry);

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldTcpHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);
            AdmittedPeer admitted;

            try {
                using var connectCts = Laws.SocketDeadline();

                admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: connectCts.Token).ConfigureAwait(continueOnCapturedContext: false);
            } finally {
                pumpCts.Cancel();
                await pumpTask.ConfigureAwait(continueOnCapturedContext: false);
            }

            using (admitted.Client) {
                var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

                Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: peer, subject: subject).IsAllowed, userMessage: "the admission mint was expected to hold Drive/body:4 immediately after connecting");

                if (revoke) {
                    fixture.Server.Revoke(grant: new WorldGrant(Principal: peer, Capability: WorldCapability.Drive, Subject: subject, Exclusive: false), actor: WorldPrincipal.Console);
                    fixture.Step();

                    Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: peer, subject: subject).IsAllowed, userMessage: "the live revoke was expected to take effect immediately, before any rebuild");
                }

                fixture.Server.EnqueueRebuild(request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reset, Definition: null, PathHint: null, Force: false), principal: WorldPrincipal.Console);
                fixture.Step();

                return fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: peer, subject: subject).IsAllowed;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
}
