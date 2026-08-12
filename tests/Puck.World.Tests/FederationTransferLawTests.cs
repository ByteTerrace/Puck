using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Adversarial laws for the authority boundary and source-scoped transfer escrow.</summary>
public sealed class FederationTransferLawTests {
    [Fact]
    public void CommitWire_PreservesTheSelectedMotionProgram() {
        var expected = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: true,
            BodyMotionProgramName: "free",
            Position: new FixedVector3(FixedQ4816.One, FixedQ4816.FromInteger(value: 8), -FixedQ4816.One),
            YawRadians: FixedQ4816.FromDouble(value: 0.75),
            PlanarVelocity: new FixedVector3(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.One),
            VerticalVelocity: FixedQ4816.FromInteger(value: 3),
            ActionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "jump", PreviousBit: true)],
                Registers: [new WorldTransferActionRegister(Name: "jumpUses", Kind: ActionStateKind.Counter, Value: FixedQ4816.One, TimerTicks: 0)]));

        var encoded = WorldFederationWireFormat.EncodeCommit(sourceAuthority: "source/world", transferId: 7, members: [expected]);

        Assert.True(condition: WorldFederationWireFormat.TryDecodeCommit(body: encoded, sourceAuthority: out var sourceAuthority, transferId: out var transferId, members: out var members, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: "source/world", actual: sourceAuthority);
        Assert.Equal(expected: 7UL, actual: transferId);
        var member = Assert.Single(collection: members);
        Assert.Equal(expected: "free", actual: member.BodyMotionProgramName);
        Assert.True(condition: Assert.Single(collection: member.ActionContinuity!.Channels).PreviousBit);
        Assert.Equal(expected: FixedQ4816.One, actual: Assert.Single(collection: member.ActionContinuity.Registers).Value);
    }

    [Fact]
    public void SnapshotWire_PreservesOccupantRigIndependentlyOfAuthoritySlot() {
        var expected = new EntitySnapshot(
            Index: 91,
            Position: new Vector3(x: 1f, y: 2f, z: 3f),
            Orientation: Quaternion.Identity,
            BodyColor: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f),
            Active: true,
            Kit: 0,
            Look: 0,
            CatalogRig: 7,
            Continuity: EntityContinuity.Continuous,
            Generation: 11,
            PlacementId: null);
        var snapshot = new WorldSnapshot(Tick: 17, Revision: 19, StepTicks: 210, Entries: new[] { expected }, Authority: "destination/world");

        var decoded = WorldFederationWireFormat.DecodeSnapshot(body: WorldFederationWireFormat.EncodeSnapshot(snapshot: in snapshot));
        var actual = Assert.Single(collection: decoded.Entries.ToArray());

        Assert.Equal(expected: 91, actual: actual.Index);
        Assert.Equal(expected: (byte)7, actual: actual.CatalogRig);
        Assert.NotEqual(expected: (byte)actual.Index, actual: actual.CatalogRig);
    }

    [Fact]
    public void RouteWire_PreservesOneCompleteAuthorityEpoch() {
        var expected = new WorldAuthorityRouteDescription(
            Endpoint: "127.0.0.1:42001",
            Entity: new WorldEntityAddress(Authority: "world/corner-sw", Index: 17, Generation: 23),
            Tick: 987654321UL,
            Position: new FixedVector3(
                X: FixedQ4816.FromDouble(value: -12.25),
                Y: FixedQ4816.FromDouble(value: 3.5),
                Z: FixedQ4816.FromDouble(value: 0.125)),
            Orientation: FixedQuaternion.FromAxisAngle(
                axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero),
                angle: FixedQ4816.FromDouble(value: 1.75)),
            BodyColor: new Vector3(x: 0.25f, y: 0.5f, z: 0.75f),
            Kit: 0,
            Look: 0,
            CatalogRig: 71,
            PlacementId: "traveler-shell",
            Definition: Fixtures.BuildDocument());

        var encoded = WorldFederationWireFormat.EncodeRoute(route: in expected);

        Assert.True(condition: WorldFederationWireFormat.TryDecodeRoute(body: encoded, route: out var actual));
        Assert.Equal(expected: expected.Endpoint, actual: actual.Endpoint);
        Assert.Equal(expected: expected.Entity, actual: actual.Entity);
        Assert.Equal(expected: expected.Tick, actual: actual.Tick);
        Assert.Equal(expected: expected.Position, actual: actual.Position);
        Assert.Equal(expected: expected.Orientation, actual: actual.Orientation);
        Assert.Equal(expected: expected.BodyColor, actual: actual.BodyColor);
        Assert.Equal(expected: expected.Kit, actual: actual.Kit);
        Assert.Equal(expected: expected.Look, actual: actual.Look);
        Assert.Equal(expected: expected.CatalogRig, actual: actual.CatalogRig);
        Assert.Equal(expected: expected.PlacementId, actual: actual.PlacementId);
        Assert.Equal(expected: expected.Definition.DocumentId, actual: actual.Definition.DocumentId);
    }

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
    public void ReservationRefusesAnOutOfCatalogTravelerRigByName() {
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 18, border: "east") with {
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0, Identity: null, Source: default, BodyColor: default, CatalogRig: 128)],
        };

        var reply = fixture.Server.ReserveTransfer(request: request);

        Assert.False(condition: reply.Accepted);
        Assert.Contains(expectedSubstring: "catalog rig 128 is outside 0..127", actualString: reply.Reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MappedCommit_RefusesAMotionProgramTheDestinationDoesNotDeclare() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "source/world", transferId: 19, border: "up") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4)],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: true, BodyMotionProgramName: "not-declared", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason));
        Assert.Contains(expectedSubstring: "unavailable destination motion program 'not-declared'", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: fixture.Server.Population.IsActive(index: Assert.Single(collection: reservation.BodyIndices)));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FederatedIntentStream_RejectsAuthorityRebindingAndUnknownTransferCredentials(bool rebindAuthority) {
        const string sourceAuthority = "player-world/source";
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: sourceAuthority, transferId: 23, border: "east") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4)],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);
        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);

        var secret = Enumerable.Range(start: 1, count: WorldFederationSecurity.SecretBytes).Select(selector: value => checked((byte)value)).ToArray();
        var security = new WorldFederationSecurity(secret: secret);
        using var host = new WorldTcpHost(server: fixture.Server, federationSecurity: security);
        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(value: 5));
        using var client = new TcpClient();
        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
        var stream = client.GetStream();
        await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token);
        var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
        var proof = security.Prove(sourceAuthority: sourceAuthority, challenge: challenge.Body);
        await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Authenticate, body: WorldFederationWireFormat.EncodeAuthentication(sourceAuthority: sourceAuthority, proof: proof), ct: timeout.Token);
        Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Ack, actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);
        await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.IntentStream, body: [], ct: timeout.Token);
        Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Ack, actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);

        var submission = new IntentSubmission(Tick: 1, EntityIndex: 0, Intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One), Principal: WorldPrincipal.Console);
        var carriedAuthority = (rebindAuthority ? "forged-world/source" : sourceAuthority);
        var transferId = (rebindAuthority ? request.TransferId : 999UL);
        var body = WorldFederationWireFormat.EncodeIntent(sourceAuthority: carriedAuthority, transferId: transferId, ordinal: 0, submission: in submission);
        await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Intent, body: body, ct: timeout.Token);

        var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);
        Assert.Equal(expected: (byte)WorldFederationWireFormat.ResponseKind.Refusal, actual: refusal.Kind);
        Assert.Contains(expectedSubstring: "names no committed transfer body", actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void CommitStatus_SurvivesALostAcknowledgement_AndOnlyExactCommitReplays() {
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 29, border: "east");
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

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
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: source, BodyColor: color, CatalogRig: 73)],
        };

        var bytes = WorldFederationWireFormat.EncodeReservation(request: request);
        Assert.True(condition: WorldFederationWireFormat.TryDecodeReservation(body: bytes, defaults: fixture.Server.Definition.PlayerDefaults, request: out var decoded, reason: out var reason), userMessage: reason);
        var member = Assert.Single(collection: decoded!.Members);
        Assert.Null(@object: member.Identity);
        Assert.Equal(expected: source, actual: member.Source);
        Assert.Equal(expected: color, actual: member.BodyColor);
        Assert.Equal(expected: (byte)73, actual: member.CatalogRig);
        Assert.True(condition: decoded.PeerAdmission);
    }

    [Fact]
    public void AutonomousTransfer_UsesEntityTableWithoutBecomingAHumanPeer() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "machine-a/boot", transferId: 37, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Idle, BodyColor: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f), CatalogRig: 73)],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);
        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        Assert.True(condition: fixture.Server.Population.IsActive(index: bodyIndex));
        Assert.False(condition: fixture.Server.Population.IsAdmittedPeer(bodyIndex: bodyIndex));
        Assert.Equal(expected: IntentSource.Idle, actual: fixture.Server.Population.EntryBody(index: bodyIndex)!.Source);
        Assert.Equal(expected: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f), actual: fixture.Server.Population.BodyColor(index: bodyIndex));
        Assert.Equal(expected: (byte)73, actual: fixture.Server.Population.CatalogRig(index: bodyIndex));
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
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Producer(name: "not-declared"), BodyColor: default, CatalogRig: 4)],
        };

        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.False(condition: reservation.Accepted);
        Assert.Contains(expectedSubstring: "declares no parameters for producer 'not-declared'", actualString: reservation.Reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FederatedIntentStream_HoldsDeviceStateAcrossFasterDestinationTicks_AndReleasesByLease() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "slow-player-world", transferId: 43, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4)],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, ordinal: 0, principal: out var principal));
        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var body = fixture.Server.Population.EntryBody(index: bodyIndex)!;
        var start = body.FixedPosition;
        var heldForward = default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One);
        var submission = new IntentSubmission(Tick: 1, EntityIndex: bodyIndex, Intent: heldForward, Principal: principal);

        // One sparse network update must mean "stick remains held", not "one destination-tick impulse".
        fixture.Server.PublishFederatedIntent(leaseId: 77, submission: in submission);
        for (var tick = 0; tick < 120; tick++) {
            fixture.Step();
        }

        Assert.True(condition: (body.FixedPosition - start).Length > FixedQ4816.FromDouble(value: 2.5), userMessage: $"sparse held input only moved {(double)(body.FixedPosition - start).Length:0.###}");
        Assert.True(condition: body.PlanarSpeed > 3.5f, userMessage: $"held stream settled at only {body.PlanarSpeed:0.###}");

        fixture.Server.ReleaseFederatedIntents(leaseId: 77);
        for (var tick = 0; tick < 30; tick++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0f, actual: body.PlanarSpeed, precision: 2);
    }

    [Fact]
    public void FederatedNeutralDeviceState_DoesNotMaskAnAuthorityAcceptedTapeSegment() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(sourceAuthority: "player-world", transferId: 47, border: "seam") with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4)],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, ordinal: 0, principal: out var principal));
        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var body = fixture.Server.Population.EntryBody(index: bodyIndex)!;
        var start = body.FixedPosition;
        var run = default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One);

        fixture.Server.ApplyCommand(command: new WorldCommand.EnqueueSegment(Principal: principal, EntityIndex: bodyIndex, Intent: run, Seconds: 1f));
        var neutral = new IntentSubmission(Tick: 1, EntityIndex: bodyIndex, Intent: default, Principal: principal);
        fixture.Server.PublishFederatedIntent(leaseId: 79, submission: in neutral);
        for (var tick = 0; tick < 60; tick++) {
            fixture.Step();
        }

        Assert.True(condition: (body.FixedPosition - start).Length > FixedQ4816.FromDouble(value: 0.75), userMessage: $"the held neutral device image masked the accepted tape segment: delta={(double)(body.FixedPosition - start).Length:0.###}, speed={body.PlanarSpeed:0.###}");
    }

    private static WorldTransferReservationRequest Reservation(string sourceAuthority, ulong transferId, string border) =>
        new(TransferId: transferId, SourceAuthority: sourceAuthority, SourceRateHz: 240, SourceTick: 0, DeadlineSourceTick: 60, Border: border, BorderCapacity: null, PartyAllOrNothing: true, PeerAdmission: false, Members: [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0, Identity: null, Source: default, BodyColor: default, CatalogRig: 0)]);

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
