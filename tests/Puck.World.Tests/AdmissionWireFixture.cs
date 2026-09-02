using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Xunit;

using Puck.Attestation;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>One generated P-256 test identity: the signing key, the key-hash domain it authorizes under, the
/// subject it claims, and the SPKI an admission entry publishes.</summary>
internal readonly record struct TestIdentity(ECDsa Key, string Domain, string Subject, byte[] Spki);
/// <summary>A peer that completed the real wire door: the still-open socket plus the body index and generation
/// <see cref="WorldAdmissionDoor"/> admitted it onto.</summary>
internal readonly record struct AdmittedPeer(TcpClient Client, int PeerIndex, int Generation);
/// <summary>
/// The raw-TCP admission harness every wire-door suite drives: generate an identity, author the one-peer admission
/// document, pump the host's tick-thread queue, complete the Hello/challenge/HelloIdentity/HelloAccepted exchange
/// against a genuine <see cref="TcpClient"/>, and submit a <see cref="WorldQuery"/> over the socket. One home, the
/// same pattern <see cref="Fixtures"/> and <see cref="MarketFixtures"/> already follow, so no suite re-implements
/// the door.
/// </summary>
internal static class AdmissionWireFixture {
    /// <summary>The 0-based body index every admission law admits its remote peer onto — the ONE peer slot
    /// <see cref="BuildAdmissionDocument"/> adds beyond the four local seats.</summary>
    public const int PeerBodyIndex = WorldBodiesLimits.LocalSeatCount;

    public static TestIdentity GenerateIdentity(string subject) {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);

        return new TestIdentity(Domain: domain, Key: key, Spki: spki, Subject: subject);
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
    /// <summary>Overlays ONE admission entry onto <see cref="Fixtures.BuildDocument"/>'s shared shape, widening
    /// population capacity by exactly one peer slot (body index 4) and admitting exactly one remote human — the
    /// smallest document a wire-door law needs. Every other section is the compiler-maintained fixture's own
    /// literal, untouched.</summary>
    public static WorldDefinition BuildAdmissionDocument(WorldAdmissionEntry entry) {
        var baseDocument = Fixtures.BuildDocument();
        var population = (baseDocument.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1), NetworkPlayers = 1 });

        return (baseDocument with { PopulationRaw = population, Admission = [entry] });
    }
    /// <summary>Drains <see cref="WorldTcpHost"/>'s tick-thread work queue and steps the fixture at a short, fixed
    /// cadence — the SAME pairing the composition root's own per-tick loop performs
    /// (<see cref="WorldTcpHost.DrainPending"/>'s own remarks: "MUST run on the tick thread, before
    /// <c>WorldServer.Step</c>"), reproduced here since this test project has no composition-root loop to borrow.
    /// Callers MUST stop this (cancel, then await) before making any further direct <see cref="WorldFixture.Step"/>
    /// call themselves — <see cref="Server.WorldServer"/> carries no lock, so two threads stepping it concurrently
    /// is a real race, not a theoretical one.</summary>
    public static async Task RunPumpAsync(WorldFixture fixture, WorldTcpHost host, CancellationToken ct, WorldReplayTape? tape = null) {
        try {
            while (!ct.IsCancellationRequested) {
                host.DrainPending();
                fixture.Step();
                tape?.NoteTick();

                await Task.Delay(delay: TimeSpan.FromMilliseconds(value: 5), cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (OperationCanceledException) {
            // Expected teardown — the caller cancelled ct once it no longer needs the pump.
        }
    }
    /// <summary>Drives the REAL wire door end to end: connects a raw <see cref="TcpClient"/> to
    /// <paramref name="host"/>, completes <see cref="WorldHelloDoor"/>'s version check, answers
    /// <see cref="WorldAdmissionDoor"/>'s challenge with a genuine <see cref="AttestationSigner.SignClaim"/> claim
    /// signed by <paramref name="identity"/>'s own key, and returns the admitted peer's body index and generation.
    /// Throws <see cref="InvalidOperationException"/> naming the refusal on anything other than a clean admit — this
    /// helper is the "ordinary positive outcome" path, never itself a refusal probe.</summary>
    public static async Task<AdmittedPeer> ConnectAndAdmitAsync(WorldTcpHost host, TestIdentity identity, CancellationToken ct) {
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        var client = new TcpClient();

        try {
            await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

            var stream = client.GetStream();

            await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldProtocol.WireProtocolKey, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

            var challengeFrame = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the Hello challenge arrived"));

            if (challengeFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloChallenge) {
                throw new InvalidOperationException(message: $"expected HelloChallenge, got {challengeFrame.Kind}: {WorldTcpWireFormat.DecodeText(body: challengeFrame.Body.Span)}");
            }

            var challenge = challengeFrame.Body;

            await WriteIdentityResponseAsync(challenge: challenge, ct: ct, identity: identity, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

            var acceptedFrame = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the admission verdict arrived"));

            if (acceptedFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloAccepted) {
                throw new InvalidOperationException(message: $"admission refused: {WorldTcpWireFormat.DecodeText(body: acceptedFrame.Body.Span)}");
            }

            var body = acceptedFrame.Body.Span;
            var peerIndex = BinaryPrimitives.ReadInt32LittleEndian(source: body);
            var generation = BinaryPrimitives.ReadInt32LittleEndian(source: body[sizeof(int)..]);
            var admitted = client;

            client = null!;

            return new AdmittedPeer(Client: admitted, Generation: generation, PeerIndex: peerIndex);
        } finally {
            client?.Dispose();
        }
    }
    public static Task WriteIdentityResponseAsync(NetworkStream stream, TestIdentity identity, ReadOnlyMemory<byte> challenge, CancellationToken ct) {
        var codec = new CborAttestationCodec();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claim = AttestationSigner.SignClaim(
            codec: codec,
            domain: identity.Domain,
            subject: identity.Subject,
            signerKey: identity.Key,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            purpose: WorldAdmissionDoor.Purpose,
            notBefore: (now - 60L),
            notAfter: (now + 60L),
            audience: WorldAdmissionDoor.Audience,
            sequence: null,
            claimBytes: challenge
        );

        return HandshakeWireFormat.WriteHelloIdentityAsync(stream: stream, chain: [], claim: codec.EncodeAttestation(attestation: claim), ct: ct);
    }
    /// <summary>Encodes <paramref name="query"/>, writes it over the admitted socket, and decodes the
    /// Completion-lane reply — the wire round trip itself, refusals included.</summary>
    public static async Task<QueryAnswer> SubmitQueryAsync(NetworkStream stream, WorldQuery query, CancellationToken ct) {
        Assert.True(condition: WorldFrameCodec.TryEncode(payload: new WorldSubmissionPayload.Query(Value: query), frame: out var frame, failure: out var failure), userMessage: $"query codec refused: {failure}");

        await stream.WriteAsync(buffer: frame, cancellationToken: ct);
        await stream.FlushAsync(cancellationToken: ct);

        var reply = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream))
            ?? throw new InvalidOperationException(message: "connection closed before the query reply"));

        Assert.Equal(actual: reply.Kind, expected: WorldTcpWireFormat.DownstreamKind.Query);
        Assert.True(
            condition: WorldTcpWireFormat.TryReadResult(kind: reply.Kind, body: reply.Body.Span, result: out var result, reason: out var reason),
            userMessage: $"the query reply failed to decode: {reason}"
        );

        return ((WorldSubmissionResult.Query)result!).Answer;
    }
}
