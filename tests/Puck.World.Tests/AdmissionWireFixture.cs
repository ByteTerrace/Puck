using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Xunit;

using Puck.Attestation;
using Puck.Networking;
using Puck.Networking.Peers;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>One generated P-256 test identity: the signing key, the key-hash domain it authorizes under, the
/// subject it claims, and the SPKI an admission entry publishes.</summary>
internal readonly record struct TestIdentity(ECDsa Key, string Domain, string Subject, byte[] Spki);
/// <summary>A peer that completed the real wire door: the still-open socket plus the body index and generation
/// <see cref="WorldAdmissionDoor"/> admitted it onto.</summary>
internal readonly record struct AdmittedPeer(PeerTestClient Client, int PeerIndex, int Generation);
/// <summary>
/// The raw-TCP admission harness every wire-door suite drives: generate an identity, author the one-peer admission
/// document, pump the host's tick-thread queue, complete the Hello/challenge/HelloIdentity/HelloAccepted exchange
/// against a genuine <see cref="PeerTestClient"/>, and submit a <see cref="WorldQuery"/> over the socket. One home, the
/// same pattern <see cref="Fixtures"/> already follows, so no suite re-implements the door.
/// </summary>
internal static class AdmissionWireFixture {
    /// <summary>The 0-based body index every admission law admits its remote peer onto — the ONE peer slot
    /// <see cref="BuildAdmissionDocument"/> adds beyond the four local seats.</summary>
    public const int PeerBodyIndex = WorldBodiesLimits.LocalSeatCount;

    /// <summary>The admission instant every host from <see cref="StartHost"/> verifies claims at, and the instant
    /// <see cref="WriteIdentityResponseAsync"/> signs its one-second-either-side validity window around.</summary>
    public static readonly DateTimeOffset ClaimInstant = new(
        day: 1,
        hour: 0,
        minute: 0,
        month: 1,
        offset: TimeSpan.Zero,
        second: 0,
        year: 2026
    );

    /// <summary>Overlays ONE admission entry onto <see cref="Fixtures.BuildDocument"/>'s shared shape, widening
    /// population capacity by exactly one peer slot (body index 4) and admitting exactly one remote human — the
    /// smallest document a wire-door law needs. Every other section is the compiler-maintained fixture's own
    /// literal, untouched.</summary>
    public static WorldDefinition BuildAdmissionDocument(WorldAdmissionEntry entry) {
        var baseDocument = Fixtures.BuildDocument();
        var population = (baseDocument.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1), NetworkPlayers = 1 });

        return (baseDocument with { PopulationRaw = population, Admission = [entry] });
    }
    public static WorldAdmissionEntry BuildEntry(TestIdentity identity, IReadOnlyList<WorldAdmissionGrant> grants) =>
        new(
            Domain: identity.Domain,
            Subject: identity.Subject,
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: identity.Spki),
            Grants: grants
        );
    /// <summary>Drives the REAL wire door end to end: connects a raw <see cref="PeerTestClient"/> to
    /// <paramref name="host"/>, completes <see cref="WorldHelloDoor"/>'s version check, answers
    /// <see cref="WorldAdmissionDoor"/>'s challenge with a genuine <see cref="AttestationSigner.SignClaim"/> claim
    /// signed by <paramref name="identity"/>'s own key, and returns the admitted peer's body index and generation.
    /// Throws <see cref="InvalidOperationException"/> naming the refusal on anything other than a clean admit — this
    /// helper is the "ordinary positive outcome" path, never itself a refusal probe.</summary>
    public static async Task<AdmittedPeer> ConnectAndAdmitAsync(WorldPeerHost host, TestIdentity identity, CancellationToken ct) {
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        var client = new PeerTestClient();

        try {
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

            var challengeFrame = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the Hello challenge arrived"));

            if (challengeFrame.Kind != WorldPeerWireFormat.DownstreamKind.HelloChallenge) {
                throw new InvalidOperationException(message: $"expected HelloChallenge, got {challengeFrame.Kind}: {WorldPeerWireFormat.DecodeText(body: challengeFrame.Body.Span)}");
            }

            var challenge = challengeFrame.Body;

            await WriteIdentityResponseAsync(
                challenge: challenge,
                ct: ct,
                identity: identity,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            var acceptedFrame = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the admission verdict arrived"));

            if (acceptedFrame.Kind != WorldPeerWireFormat.DownstreamKind.HelloAccepted) {
                throw new InvalidOperationException(message: $"admission refused: {WorldPeerWireFormat.DecodeText(body: acceptedFrame.Body.Span)}");
            }

            var body = acceptedFrame.Body.Span;
            var peerIndex = BinaryPrimitives.ReadInt32LittleEndian(source: body);
            var generation = BinaryPrimitives.ReadInt32LittleEndian(source: body[sizeof(int)..]);
            var admitted = client;

            client = null!;

            return new AdmittedPeer(
                Client: admitted,
                Generation: generation,
                PeerIndex: peerIndex
            );
        } finally {
            client?.Dispose();
        }
    }
    /// <summary>Admits <paramref name="identity"/> through <see cref="ConnectAndAdmitAsync"/> under a pump that is
    /// stopped before this returns, so the caller may step <paramref name="fixture"/> directly afterwards.</summary>
    /// <returns>The admitted peer; the caller owns its client.</returns>
    public static async Task<AdmittedPeer> AdmitAsync(WorldFixture fixture, WorldPeerHost host, TestIdentity identity, WorldReplayTape? tape = null) {
        await using (StartPump(
            fixture: fixture,
            host: host,
            tape: tape
        )) {
            return await ConnectAndAdmitAsync(
                ct: TestContext.Current.CancellationToken,
                host: host,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    public static TestIdentity GenerateIdentity(string subject) {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);

        return new TestIdentity(
            Domain: domain,
            Key: key,
            Spki: spki,
            Subject: subject
        );
    }
    /// <summary>Drains <see cref="WorldPeerHost"/>'s tick-thread work queue and then steps the fixture once, each time
    /// a connection queues work — the SAME drain-then-step pairing the composition root's own per-tick loop performs
    /// (<see cref="WorldPeerHost.DrainPending"/>'s own remarks: "MUST run on the tick thread, before
    /// <c>WorldServer.Step</c>"), driven by <see cref="WorldPeerHost.WaitForPendingWorkAsync"/> rather than a
    /// cadence, since this test project has no composition-root loop to borrow. The step after each drain is the
    /// tick that resolves a submission queued by that drain. Callers MUST stop this (dispose it) before
    /// making any further direct <see cref="WorldFixture.Step"/> call themselves — <see cref="Server.WorldServer"/>
    /// carries no lock, so two threads stepping it concurrently is a real race, not a theoretical one.</summary>
    /// <returns>The running pump; disposing it cancels the loop and awaits its exit.</returns>
    public static IAsyncDisposable StartPump(WorldFixture fixture, WorldPeerHost host, WorldReplayTape? tape = null) =>
        new Pump(
            fixture: fixture,
            host: host,
            tape: tape
        );

    private static async Task RunPumpAsync(WorldFixture fixture, WorldPeerHost host, WorldReplayTape? tape, CancellationToken ct) {
        try {
            while (true) {
                await host.WaitForPendingWorkAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
                host.DrainPending();
                fixture.Step();
                tape?.NoteTick();
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Expected teardown — the caller cancelled ct once it no longer needs the pump.
        }
    }

    /// <summary>Starts a listening <see cref="WorldPeerHost"/> over <paramref name="server"/> on an ephemeral loopback
    /// port, on a host clock that reads <see cref="ClaimInstant"/> until a law advances it — so neither a claim's
    /// validity, the handshake deadline, nor the refusal drain is decided by how long a law runs.</summary>
    /// <param name="server">The authoritative server the host admits into.</param>
    /// <param name="clock">The host's clock, which only the law advances.</param>
    /// <returns>The started host; the caller owns disposal.</returns>
    public static WorldPeerHost StartHost(WorldServer server, out VirtualClock clock) {
        clock = new VirtualClock(start: ClaimInstant);

        var host = new WorldPeerHost(
            server: server,
            timeProvider: clock,
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout
        );

        host.Start(listen: "127.0.0.1:0");

        return host;
    }
    /// <summary>Waits for the server to close <paramref name="stream"/> while expiring, on <paramref name="clock"/>,
    /// what holds it open: first <see cref="WorldPeerHost.HandshakeDeadline"/> when <paramref name="handshakeDeadline"/>
    /// is set (a connection that never finishes its pre-admission handshake), then the
    /// <see cref="PeerWireProtocol.RefusalDrainTimeout"/> drain the host waits out for the peer to close first. Each is
    /// expired through <see cref="VirtualClock.ExpireAsync"/>, so the connection is still open one tick before each due
    /// instant and the close comes exactly when the drain expires.</summary>
    /// <param name="clock">The host's clock from <see cref="StartHost"/>.</param>
    /// <param name="stream">The client's stream, whose inbound direction the server has not yet closed.</param>
    /// <param name="handshakeDeadline">Whether the handshake deadline must expire before the drain begins.</param>
    /// <param name="ct">The test's own cancellation.</param>
    /// <returns>Whether the server closed the connection without sending any further bytes.</returns>
    public static async Task<bool> ExpireUntilClosedAsync(VirtualClock clock, Stream stream, bool handshakeDeadline, CancellationToken ct) {
        var closed = WaitForCloseAsync(
            ct: ct,
            stream: stream
        );

        if (handshakeDeadline) {
            await clock.ExpireAsync(
                ct: ct,
                dueTime: WorldPeerHost.HandshakeDeadline,
                pending: closed
            ).ConfigureAwait(continueOnCapturedContext: false);
        }

        await clock.ExpireAsync(
            ct: ct,
            dueTime: PeerWireProtocol.RefusalDrainTimeout,
            pending: closed
        ).ConfigureAwait(continueOnCapturedContext: false);

        return await closed.ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Encodes <paramref name="query"/>, writes it over the admitted socket, and decodes the
    /// Completion-lane reply — the wire round trip itself, refusals included.</summary>
    public static async Task<QueryAnswer> SubmitQueryAsync(Stream stream, WorldQuery query, CancellationToken ct) {
        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                payload: new WorldSubmissionPayload.Query(Value: query),
                frame: out var frame,
                failure: out var failure
            ),
            userMessage: $"query codec refused: {failure}"
        );

        await stream.WriteAsync(
            buffer: frame,
            cancellationToken: ct
        );
        await stream.FlushAsync(cancellationToken: ct);

        var reply = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
            ct: ct,
            stream: stream
        ))
            ?? throw new InvalidOperationException(message: "connection closed before the query reply"));

        Assert.Equal(
            actual: reply.Kind,
            expected: WorldPeerWireFormat.DownstreamKind.Query
        );
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                kind: reply.Kind,
                body: reply.Body.Span,
                result: out var result,
                reason: out var reason
            ),
            userMessage: $"the query reply failed to decode: {reason}"
        );

        return ((WorldSubmissionResult.Query)result!).Answer;
    }
    /// <summary>Reads one byte from <paramref name="stream"/> to learn how the server ended the connection.</summary>
    /// <param name="stream">The client's stream.</param>
    /// <param name="ct">The test's own cancellation.</param>
    /// <returns>Whether the server closed the connection without sending any further bytes.</returns>
    public static async Task<bool> WaitForCloseAsync(Stream stream, CancellationToken ct) {
        var probe = new byte[1];

        try {
            return (await stream.ReadAsync(
                buffer: probe,
                cancellationToken: ct
            ).ConfigureAwait(continueOnCapturedContext: false) == 0);
        } catch (Exception exception) when ((exception is IOException or SocketException)) {
            return true;
        }
    }
    public static Task WriteIdentityResponseAsync(Stream stream, TestIdentity identity, ReadOnlyMemory<byte> challenge, CancellationToken ct) {
        var codec = new CborAttestationCodec();
        var now = ClaimInstant.ToUnixTimeSeconds();
        var claim = AttestationSigner.SignClaim(
            codec: codec,
            domain: identity.Domain,
            subject: identity.Subject,
            signerKey: identity.Key,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            purpose: WorldAdmissionDoor.Purpose,
            notBefore: (now - 1L),
            notAfter: (now + 1L),
            audience: WorldAdmissionDoor.Audience,
            sequence: null,
            claimBytes: challenge
        );

        return HandshakeWireFormat.WriteHelloIdentityAsync(
            stream: stream,
            chain: [],
            claim: codec.EncodeAttestation(attestation: claim),
            ct: ct
        );
    }

    private sealed class Pump : IAsyncDisposable {
        private readonly CancellationTokenSource m_stop = new();
        private readonly Task m_loop;

        public Pump(WorldFixture fixture, WorldPeerHost host, WorldReplayTape? tape) {
            m_loop = RunPumpAsync(
                ct: m_stop.Token,
                fixture: fixture,
                host: host,
                tape: tape
            );
        }

        public async ValueTask DisposeAsync() {
            await m_stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            await m_loop.ConfigureAwait(continueOnCapturedContext: false);
            m_stop.Dispose();
        }
    }
}
