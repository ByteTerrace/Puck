using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Adversarial laws for the authority boundary and source-scoped transfer escrow.</summary>
public sealed class FederationTransferLawTests {
    [Fact]
    public void ReservationIdentity_IsSourceScoped_AndOnlyExactReplayIsIdempotent() {
        using var fixture = Fixtures.FreshServer();
        var sourceA = Reservation(sourceAuthority: "machine-a/boot", transferId: 17, border: "east");

        var first = fixture.Server.ReserveTransfer(request: sourceA);
        var exactReplay = fixture.Server.ReserveTransfer(request: sourceA with { Members = [.. sourceA.Members] });
        var conflictingReplay = fixture.Server.ReserveTransfer(request: sourceA with { Border = "west" });
        var otherSource = fixture.Server.ReserveTransfer(request: Reservation(sourceAuthority: "machine-b/boot", transferId: 17, border: "east"));

        Assert.True(condition: first.Accepted);
        Assert.True(condition: exactReplay.Accepted);
        Assert.Equal(expected: first.BodyIndices, actual: exactReplay.BodyIndices);
        Assert.False(condition: conflictingReplay.Accepted);
        Assert.Contains(expectedSubstring: "different reservation", actualString: conflictingReplay.Reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: otherSource.Accepted);
        Assert.NotEqual(expected: first.BodyIndices[0], actual: otherSource.BodyIndices[0]);
        Assert.Equal(expected: WorldTransferStatus.Reserved, actual: fixture.Server.TransferStatus(sourceAuthority: sourceA.SourceAuthority, transferId: sourceA.TransferId));
        Assert.Equal(expected: WorldTransferStatus.Reserved, actual: fixture.Server.TransferStatus(sourceAuthority: "machine-b/boot", transferId: sourceA.TransferId));
    }

    [Fact]
    public async Task FederationDoor_RejectsBadProof_AndAuthorityRebinding() {
        using var fixture = Fixtures.FreshServer();
        var secret = Enumerable.Range(start: 0, count: WorldFederationSecurity.SecretBytes).Select(selector: value => checked((byte)value)).ToArray();
        var security = new WorldFederationSecurity(secret: secret);
        using var host = new WorldTcpHost(server: fixture.Server, federationSecurity: security);
        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(value: 5));

        using (var attacker = new TcpClient()) {
            await attacker.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = attacker.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Challenge, actual: challenge.Kind);

            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Authenticate, body: WorldFederationWireFormat.EncodeAuthentication(sourceAuthority: "machine-a/boot", proof: new byte[WorldFederationSecurity.ProofBytes]), ct: timeout.Token);
            var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Refusal, actual: refusal.Kind);
            Assert.Contains(expectedSubstring: "authentication failed", actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
        }

        using (var authenticated = new TcpClient()) {
            await authenticated.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = authenticated.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            var proof = security.Prove(sourceAuthority: "machine-a/boot", challenge: challenge.Body);
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Authenticate, body: WorldFederationWireFormat.EncodeAuthentication(sourceAuthority: "machine-a/boot", proof: proof), ct: timeout.Token);
            var accepted = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Ack, actual: accepted.Kind);

            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Status, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: "machine-b/boot", transferId: 17), ct: timeout.Token);
            var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Refusal, actual: refusal.Kind);
            Assert.Contains(expectedSubstring: "does not match", actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommitStatus_SurvivesALostAcknowledgement_AndOnlyExactCommitReplays() {
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 29, border: "east");
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var firstReason), userMessage: firstReason);
        Assert.Equal(expected: WorldTransferStatus.Committed, actual: fixture.Server.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var replayReason), userMessage: replayReason);

        var altered = member with { HasMappedArrival = true };
        Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [altered], reason: out var alteredReason));
        Assert.Contains(expectedSubstring: "different commit", actualString: alteredReason, comparisonType: StringComparison.Ordinal);
        fixture.Server.AbortTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
        Assert.Equal(expected: WorldTransferStatus.Committed, actual: fixture.Server.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationWire_PreservesAnonymousAutonomousIntentSource(bool producer) {
        using var fixture = Fixtures.FreshServer();
        var source = (producer ? IntentSource.Producer(name: "wander") : IntentSource.Idle);
        var color = new Vector3(x: 0.125f, y: 0.5f, z: 0.875f);
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 31, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: source, BodyColor: color)],
        };

        var bytes = WorldFederationWireFormat.EncodeReservation(request: request);
        Assert.True(condition: WorldFederationWireFormat.TryDecodeReservation(body: bytes, defaults: fixture.Server.Definition.PlayerDefaults, request: out var decoded, reason: out var reason), userMessage: reason);
        var member = Assert.Single(collection: decoded!.Members);
        Assert.Null(@object: member.Identity);
        Assert.Equal(expected: source, actual: member.Source);
        Assert.Equal(expected: color, actual: member.BodyColor);
        Assert.True(condition: decoded.PeerAdmission);
    }

    [Fact]
    public void AutonomousTransfer_UsesEntityTableWithoutBecomingAHumanPeer() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 37, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Source: IntentSource.Idle, BodyColor: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f))],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);
        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        Assert.True(condition: fixture.Server.Population.IsActive(index: bodyIndex));
        Assert.False(condition: fixture.Server.Population.IsAdmittedPeer(bodyIndex: bodyIndex));
        Assert.Equal(expected: IntentSource.Idle, actual: fixture.Server.Population.EntryBody(index: bodyIndex)!.Source);
        Assert.Equal(expected: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f), actual: fixture.Server.Population.BodyColor(index: bodyIndex));
        Assert.True(condition: fixture.Server.Population.TryCaptureTransferredEntity(index: bodyIndex, peer: out var captured));
        Assert.True(condition: captured.AuthorityTransferred);

        Assert.Equal(expected: 1, actual: fixture.Server.Population.SetSimulatedCount(count: 1));
        Assert.True(condition: fixture.Server.Population.IsActive(index: bodyIndex));
        Assert.Equal(expected: IntentSource.Idle, actual: fixture.Server.Population.EntryBody(index: bodyIndex)!.Source);
        Assert.Equal(expected: 2, actual: fixture.Server.Population.ActiveCount());
    }

    [Fact]
    public void AutonomousTransfer_RefusesAnUnsupportedProducerBeforeCommit() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 41, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Source: IntentSource.Producer(name: "not-declared"))],
        };

        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.False(condition: reservation.Accepted);
        Assert.Contains(expectedSubstring: "declares no parameters for producer 'not-declared'", actualString: reservation.Reason, comparisonType: StringComparison.Ordinal);
    }

    private static WorldTransferReservationRequest Reservation(string sourceAuthority, ulong transferId, string border) =>
        new(TransferId: transferId, SourceAuthority: sourceAuthority, SourceRateHz: 240, SourceTick: 0, DeadlineSourceTick: 60, Border: border, BorderCapacity: null, PartyAllOrNothing: true, PeerAdmission: false, Members: [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0)]);

    private static WorldDefinition TransferPopulationDocument() {
        var document = Fixtures.BuildDocument();
        return document with {
            Population = document.Population with {
                Capacity = WorldPopulation.LocalSeatCount + 2,
                NetworkPlayers = 2,
            },
        };
    }

    private static async Task<(byte Kind, byte[] Body)> RequireFrameAsync(NetworkStream stream, CancellationToken ct) =>
        (await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct)) ?? throw new Xunit.Sdk.XunitException("federation peer closed before its required response");
}
