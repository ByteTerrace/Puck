using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Xunit;

using Puck.Attestation;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves the seven <see cref="WorldQuery"/> leaves the link-query seam added (<c>Client.PlayerRoster</c>'s replacement
/// for a live <c>Server.WorldServer</c> reference) reach an admitted peer over the REAL wire door
/// (<see cref="WorldTcpHost"/>, a genuine <see cref="TcpClient"/>): each query's Completion-lane answer arrives over
/// the socket, not merely in-process. Follows the same real-wire-over-server-internals discipline as
/// <see cref="AdmissionSecurityLawTests"/>.
/// </summary>
/// <remarks>
/// Six of the seven leaves (every one except <see cref="WorldQuery.GrantAllows"/>) resolve
/// <see cref="WorldQuery.ObservationSubject"/> to <see cref="GrantSubject.All"/>, and
/// <c>Server.WorldGrants.IsLegitimateSubject</c> admits Observe/all only for <c>PrincipalKind.Console</c> or
/// <c>PrincipalKind.Seat</c> — a remote peer is neither, so no admission grant can ever satisfy that gate for those
/// six. That is deliberate security architecture (an untrusted socket peer must never hold a whole-world observe
/// wildcard), not a gap this seam owes a fix for; the six laws below prove the QUERY still round-trips the wire
/// (decodes, reaches the server, and comes back as a named Completion-lane refusal) rather than hanging, crashing, or
/// getting silently dropped. <see cref="WorldQuery.GrantAllows"/> alone carries its own subject, so a peer holding
/// Observe over a concrete body can read it — proven separately below with a non-refused answer.
/// </remarks>
public sealed class LinkQuerySeamWireLawTests {
    [Fact]
    public async Task GrantAllows_AnswersOverTheWire_WhenThePeerObservesTheConcreteSubject() {
        var answer = await RunQueryAsync(query: peer => new WorldQuery.GrantAllows(
            Principal: WorldPrincipal.Seat(slot: 0),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: peer.Index)
        ));

        Assert.False(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "grant.allows:", actualString: answer.Text);
    }
    [Fact]
    public async Task GrantHandleMint_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.GrantHandleMint(
            Principal: WorldPrincipal.Seat(slot: 0),
            Capability: WorldCapability.Observe,
            Index: 0
        ));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task GrantHandleResolve_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var handle = new WorldHandle(
            Index: 0,
            Generation: 0,
            TablePrincipal: WorldPrincipal.Seat(slot: 0),
            TableCapability: WorldCapability.Observe
        );
        var answer = await RunQueryAsync(query: _ => new WorldQuery.GrantHandleResolve(Handle: handle));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task PopulationChannels_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.PopulationChannels());

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task ProfileCatalog_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.ProfileCatalog());

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task FindProfile_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.FindProfile(Name: "no-such-profile"));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task PreferredControllerProfile_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var device = new InputDeviceId(Value: Guid.NewGuid(), Persistence: InputDeviceIdentityPersistence.Reconnect);
        var answer = await RunQueryAsync(query: _ => new WorldQuery.PreferredControllerProfile(Device: device));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }

    // Admits one peer holding Observe over ONLY its own body (never all), builds the caller's query against the
    // admitted peer's own principal, and submits it over a genuine TCP connection. The grant is deliberately narrow
    // so GrantAllows' concrete-subject success and the other six leaves' Observe/all refusal are proven under the
    // SAME admitted session, not two different setups.
    private static async Task<QueryAnswer> RunQueryAsync(Func<WorldPrincipal, WorldQuery> query) {
        var identity = GenerateIdentity(subject: "link-query-seam-peer");

        try {
            var grants = new[] { new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.Body(index: PeerBodyIndex), Budget: 100) };
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
                    var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

                    return await SubmitQueryAsync(stream: admitted.Client.GetStream(), query: query(arg: peer), ct: requestCts.Token);
                }
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    private static async Task<QueryAnswer> SubmitQueryAsync(NetworkStream stream, WorldQuery query, CancellationToken ct) {
        Assert.True(condition: WorldFrameCodec.TryEncode(payload: new WorldSubmissionPayload.Query(Value: query), frame: out var frame, failure: out var failure), userMessage: $"query codec refused: {failure}");

        await stream.WriteAsync(buffer: frame, cancellationToken: ct);
        await stream.FlushAsync(cancellationToken: ct);

        var reply = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream))
            ?? throw new InvalidOperationException(message: "connection closed before the query reply"));

        Assert.Equal(actual: reply.Kind, expected: WorldTcpWireFormat.DownstreamKind.Query);

        var offset = 1;
        var refused = (reply.Body[0] != 0);
        var text = WorldTcpWireFormat.ReadLengthPrefixedString(body: reply.Body, offset: ref offset, ok: out var ok);

        Assert.True(condition: ok, userMessage: "the query reply's length-prefixed text field is truncated");

        return new QueryAnswer(Text: text, Refused: refused);
    }

    // ---- Shared scaffolding — the same shapes AdmissionSecurityLawTests carries, duplicated rather than shared
    // across suites (each suite owns its own admitted-peer document/harness). ----

    private readonly record struct TestIdentity(ECDsa Key, string Domain, string Subject, byte[] Spki);
    private readonly record struct AdmittedPeer(TcpClient Client, int PeerIndex, int Generation);

    private const int PeerBodyIndex = WorldPopulationLimits.LocalSeatCount;

    private static TestIdentity GenerateIdentity(string subject) {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);

        return new TestIdentity(Domain: domain, Key: key, Spki: spki, Subject: subject);
    }
    private static WorldAdmissionEntry BuildEntry(TestIdentity identity, IReadOnlyList<WorldAdmissionGrant> grants) =>
        new(
            Domain: identity.Domain,
            Subject: identity.Subject,
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: identity.Spki),
            Grants: grants
        );
    private static WorldDefinition BuildAdmissionDocument(WorldAdmissionEntry entry) {
        var baseDocument = Fixtures.BuildDocument();
        var population = (baseDocument.Population with { CapacityRaw = (WorldPopulationLimits.LocalSeatCount + 1), NetworkPlayers = 1 });

        return (baseDocument with { PopulationRaw = population, Admission = [entry] });
    }
    private static async Task RunPumpAsync(WorldFixture fixture, WorldTcpHost host, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                host.DrainPending();
                fixture.Step();

                await Task.Delay(delay: TimeSpan.FromMilliseconds(value: 5), cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (OperationCanceledException) {
            // Expected teardown — the caller cancelled ct once it no longer needs the pump.
        }
    }
    private static async Task<AdmittedPeer> ConnectAndAdmitAsync(WorldTcpHost host, TestIdentity identity, CancellationToken ct) {
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        var client = new TcpClient();

        try {
            await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

            var stream = client.GetStream();

            await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldProtocol.WireProtocolKey, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

            var challengeFrame = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the Hello challenge arrived"));

            if (challengeFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloChallenge) {
                throw new InvalidOperationException(message: $"expected HelloChallenge, got {challengeFrame.Kind}: {WorldTcpWireFormat.DecodeText(body: challengeFrame.Body)}");
            }

            var challenge = challengeFrame.Body;

            await WriteIdentityResponseAsync(challenge: challenge, ct: ct, identity: identity, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

            var acceptedFrame = ((await WorldTcpWireFormat.TryReadDownstreamAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "connection closed before the admission verdict arrived"));

            if (acceptedFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloAccepted) {
                throw new InvalidOperationException(message: $"admission refused: {WorldTcpWireFormat.DecodeText(body: acceptedFrame.Body)}");
            }

            var body = acceptedFrame.Body;
            var peerIndex = BinaryPrimitives.ReadInt32LittleEndian(source: body);
            var generation = BinaryPrimitives.ReadInt32LittleEndian(source: body.AsSpan(start: sizeof(int)));
            var admitted = client;

            client = null!;

            return new AdmittedPeer(Client: admitted, Generation: generation, PeerIndex: peerIndex);
        } finally {
            client?.Dispose();
        }
    }
    private static Task WriteIdentityResponseAsync(NetworkStream stream, TestIdentity identity, byte[] challenge, CancellationToken ct) {
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
}
