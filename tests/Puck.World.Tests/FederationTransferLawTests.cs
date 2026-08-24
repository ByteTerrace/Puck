using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using Puck.Attestation;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>Adversarial laws for the authority boundary and source-scoped transfer escrow.</summary>
public sealed class FederationTransferLawTests {
    [Fact]
    public void CommitWire_PreservesTheSelectedMotionProgramAndTheCommitTimeProfile() {
        using var fixture = Fixtures.FreshServer();
        // The commit-time profile is the discriminating field: a colocated crossing hands this object straight to
        // the destination, so a codec that never writes it gives federated crossings different semantics from
        // colocated ones for the same transfer.
        var profile = fixture.Server.Profiles.BootProfile;
        var expected = new WorldTransferCommitMember(
            Profile: profile,
            HasMappedArrival: true,
            BodyMotionProgramName: "free",
            Position: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromInteger(value: 8), Z: -FixedQ4816.One),
            YawRadians: FixedQ4816.FromDouble(value: 0.75),
            PlanarVelocity: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.One),
            VerticalVelocity: FixedQ4816.FromInteger(value: 3),
            ActionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "jump", PreviousBit: true, HeldValue: FixedQ4816.One)],
                Registers: [new WorldTransferActionRegister(Name: "jumpUses", Kind: ActionStateKind.Counter, Value: FixedQ4816.One, TimerTicks: 0)]),
            Continuum: new WorldContinuumTrajectory(
                PreviousPosition: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 8), Z: -FixedQ4816.One),
                SourceTick: 91,
                ContinuumStartEngineTick: 18_900,
                ContinuumEndEngineTick: 19_110,
                ConsumedThroughEngineTick: 19_110,
                BoundaryEvents: 3));

        var encoded = WorldFederationCodec.EncodeCommit(members: [expected], sourceAuthority: "source/world", transferId: 7);

        Assert.True(condition: WorldFederationCodec.TryDecodeCommit(body: encoded, defaults: fixture.Server.Definition.PlayerDefaults, sourceAuthority: out var sourceAuthority, transferId: out var transferId, members: out var members, failure: out var failure), userMessage: failure.ToString());
        Assert.Equal(actual: sourceAuthority, expected: "source/world");
        Assert.Equal(actual: transferId, expected: 7UL);
        var member = Assert.Single(collection: members);

        Assert.Equal(expected: "free", actual: member.BodyMotionProgramName);
        Assert.Equal(expected: profile.Id, actual: member.Profile?.Id);
        Assert.Equal(expected: profile.Name, actual: member.Profile?.Name);
        Assert.True(condition: Assert.Single(collection: member.ActionContinuity!.Channels).PreviousBit);
        Assert.Equal(expected: FixedQ4816.One, actual: Assert.Single(collection: member.ActionContinuity.Channels).HeldValue);
        Assert.Equal(expected: FixedQ4816.One, actual: Assert.Single(collection: member.ActionContinuity.Registers).Value);
        Assert.Equal(expected: expected.Continuum, actual: member.Continuum);
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
            PlacementId: null,
            Heading: 1.25f);
        var snapshot = new WorldSnapshot(Authority: "destination/world", Entries: new[] { expected }, Revision: 19, StepTicks: 210, Tick: 17);

        Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(body: WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot), snapshot: out var decoded, failure: out var failure), userMessage: failure.ToString());
        var actual = Assert.Single(collection: decoded.Entries.ToArray());

        Assert.Equal(expected: 91, actual: actual.Index);
        Assert.Equal(expected: ((byte)7), actual: actual.CatalogRig);
        Assert.Equal(expected: 1.25f, actual: actual.Heading);
        Assert.NotEqual(expected: ((byte)actual.Index), actual: actual.CatalogRig);
    }
    [Fact]
    public void RouteWire_PreservesOneCompleteAuthorityEpoch() {
        var expected = new WorldAuthorityRouteDescription(
            Endpoint: "127.0.0.1:42001",
            Entity: new WorldEntityAddress(Authority: "world/corner-sw", Generation: 23, Index: 17),
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

        var encoded = WorldFederationCodec.EncodeRoute(authority: "world/corner-sw", revision: 0, route: in expected, tier: WorldDisclosureTier.Replica);

        Assert.True(condition: WorldFederationCodec.TryDecodeRoute(body: encoded, failure: out var failure, route: out var actual), userMessage: failure.ToString());
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
        var sourceA = Reservation(border: "east", sourceAuthority: "machine-a/boot", transferId: 17);

        var first = fixture.Server.ReserveTransfer(request: sourceA);
        var exactReplay = fixture.Server.ReserveTransfer(request: sourceA with { Members = [.. sourceA.Members] });
        var conflictingReplay = fixture.Server.ReserveTransfer(request: sourceA with { Border = "west" });
        var otherSourceRequest = Reservation(border: "east", sourceAuthority: "machine-b/boot", transferId: 17) with {
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 1, Identity: null, Source: default, BodyColor: default, CatalogRig: 0, Mobility: Mobility(index: 1))],
        };
        var otherSource = fixture.Server.ReserveTransfer(request: otherSourceRequest);

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
        var request = Reservation(border: "east", sourceAuthority: "machine-a/boot", transferId: 18) with {
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0, Identity: null, Source: default, BodyColor: default, CatalogRig: 128, Mobility: Mobility(index: 0))],
        };

        var reply = fixture.Server.ReserveTransfer(request: request);

        Assert.False(condition: reply.Accepted);
        Assert.Contains(expectedSubstring: "catalog rig 128 is outside 0..127", actualString: reply.Reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void MappedCommit_RefusesAMotionProgramTheDestinationDoesNotDeclare() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(border: "up", sourceAuthority: "source/world", transferId: 19) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4, Mobility: Mobility(index: 4))],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: true, BodyMotionProgramName: "not-declared", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "unavailable destination motion program 'not-declared'");
        Assert.False(condition: fixture.Server.Population.IsActive(index: Assert.Single(collection: reservation.BodyIndices)));
    }
    [Fact]
    public async Task FederationDoor_RejectsBadProof_AndAuthorityRebinding() {
        using var fixture = Fixtures.FreshServer();
        using var oracle = LocalOracle(subject: "machine-a/boot");
        var security = new WorldAttestedAuthenticator(trustEntries: () => [TrustEntryFor(oracle: oracle)], oracle: oracle);
        using var host = new WorldTcpHost(server: fixture.Server, authenticator: security);

        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = Laws.SocketDeadline();

        using (var attacker = new TcpClient()) {
            await attacker.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = attacker.GetStream();

            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Challenge), actual: challenge.Kind);

            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: new byte[64]), ct: timeout.Token);
            var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: refusal.Kind);
            Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.AuthenticationFailed), actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
        }

        using (var authenticated = new TcpClient()) {
            await authenticated.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = authenticated.GetStream();

            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
            var proof = security.Prove(challenge: challenge.Body);

            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: proof), ct: timeout.Token);
            var accepted = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: accepted.Kind);

            // The control leg: the same lane answers a well-formed status for its OWN namespace, and stays open.
            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Status, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: "machine-a/boot", transferId: 17), ct: timeout.Token);
            var status = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Status), actual: status.Kind);
            Assert.Equal(expected: ((byte)WorldTransferStatus.Missing), actual: Assert.Single(collection: status.Body));

            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Status, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: "machine-b/boot", transferId: 17), ct: timeout.Token);
            var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: refusal.Kind);
            Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.SourceAuthorityMismatch), actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
        }

        Assert.Contains(collection: host.FederationRefusals, filter: row => ((row.Refusal == WorldFederationRefusal.AuthenticationFailed) && (row.Count == 1)));
        Assert.Contains(collection: host.FederationRefusals, filter: row => ((row.Refusal == WorldFederationRefusal.SourceAuthorityMismatch) && (row.Count == 1)));
    }
    /// <summary>The P1 fix: a proof genuinely signed for authority X, presented alongside no claimed namespace at all
    /// (the wire carries none — see <see cref="WorldFederationCodec.EncodeAuthentication"/>'s own remarks), can never
    /// be admitted as authority Y — because there is no Y to admit as; the door names the connection's identity
    /// solely from what the proof itself verifies to. Falsifier: reintroducing a claimed-namespace wire field the
    /// server trusts without binding it to the verified subject turns this red.</summary>
    [Fact]
    public async Task FederationDoor_NeverAdmitsAVerifiedProofUnderAnyNamespaceOtherThanItsOwnVerifiedSubject() {
        using var fixture = Fixtures.FreshServer();
        using var oracleX = LocalOracle(subject: "authority-x");
        using var oracleY = LocalOracle(subject: "authority-y");
        // The door trusts BOTH keys' own pinned subjects — a claim genuinely signed by Y's key still only ever
        // verifies as "authority-y", never as "authority-x", regardless of what an attacker might wish it named.
        var security = new WorldAttestedAuthenticator(trustEntries: () => [TrustEntryFor(oracle: oracleX), TrustEntryFor(oracle: oracleY)], oracle: oracleX);
        using var host = new WorldTcpHost(server: fixture.Server, authenticator: security);

        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = Laws.SocketDeadline();
        using var client = new TcpClient();

        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
        var stream = client.GetStream();

        await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
        var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
        // Y signs the proof — never X's own key — so the connection can only ever be recorded as "authority-y".
        var proof = oracleY.Sign(challenge: challenge.Body, cancellationToken: CancellationToken.None);

        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: proof), ct: timeout.Token);
        Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);

        // A status frame claiming "authority-x" (the OTHER trusted key) is refused — this connection verified as Y.
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Status, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: "authority-x", transferId: 1), ct: timeout.Token);
        var refusedAsX = await RequireFrameAsync(stream: stream, ct: timeout.Token);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: refusedAsX.Kind);
        Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.SourceAuthorityMismatch), actualString: Encoding.UTF8.GetString(bytes: refusedAsX.Body), comparisonType: StringComparison.Ordinal);

        // The control: a status frame naming Y's own verified identity is admitted.
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Status, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: "authority-y", transferId: 1), ct: timeout.Token);
        var admittedAsY = await RequireFrameAsync(stream: stream, ct: timeout.Token);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Status), actual: admittedAsY.Kind);
    }
    /// <summary>A claim replayed against a fresh challenge — the exact bytes a genuinely admitted connection just
    /// presented — never verifies a second time: the claim's opaque payload is bound to the FIRST challenge, and the
    /// second connection issues its own, different one. Falsifier: a door that stops binding the payload to the
    /// live challenge (or that reuses challenges) turns this green for the wrong reason.</summary>
    [Fact]
    public async Task FederationDoor_RefusesAReplayedProofAgainstAFreshChallenge() {
        using var fixture = Fixtures.FreshServer();
        using var oracle = LocalOracle(subject: "machine-a/boot");
        var security = new WorldAttestedAuthenticator(trustEntries: () => [TrustEntryFor(oracle: oracle)], oracle: oracle);
        using var host = new WorldTcpHost(server: fixture.Server, authenticator: security);

        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = Laws.SocketDeadline();
        byte[] capturedProof;

        using (var first = new TcpClient()) {
            await first.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = first.GetStream();

            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            capturedProof = security.Prove(challenge: challenge.Body);
            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: capturedProof), ct: timeout.Token);
            Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);
        }

        using (var replay = new TcpClient()) {
            await replay.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
            var stream = replay.GetStream();

            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
            var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: capturedProof), ct: timeout.Token);
            var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: refusal.Kind);
            Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.AuthenticationFailed), actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
        }
    }
    /// <summary>The federation door carries no authenticator-scheme width of its own — <see cref="WorldTcpHost"/>
    /// sizes the challenge and accepts whatever proof the configured <see cref="IAuthenticator"/> verifies, never a
    /// fixed 32-byte shape. A scheme whose challenge and proof are NOT 32 bytes must still authenticate.
    /// Falsifier: reinstating a fixed proof-length check in <c>WorldFederationCodec.TryDecodeAuthentication</c>
    /// turns this red.</summary>
    [Fact]
    public async Task FederationDoor_AcceptsAnAuthenticatorWhoseChallengeAndProofAreNotThirtyTwoBytes() {
        using var fixture = Fixtures.FreshServer();
        var security = new OddWidthAuthenticator();
        using var host = new WorldTcpHost(server: fixture.Server, authenticator: security);

        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = Laws.SocketDeadline();
        using var client = new TcpClient();

        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
        var stream = client.GetStream();

        await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
        var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Challenge), actual: challenge.Kind);
        Assert.Equal(expected: OddWidthAuthenticator.ChallengeWidth, actual: challenge.Body.Length);

        var proof = security.Prove(challenge: challenge.Body);

        Assert.NotEqual(expected: 32, actual: proof.Length);
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: proof), ct: timeout.Token);
        var accepted = await RequireFrameAsync(stream: stream, ct: timeout.Token);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: accepted.Kind);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task FederatedIntentStream_RejectsAuthorityRebindingAndUnknownTransferCredentials(bool rebindAuthority) {
        const string sourceAuthority = "player-world/source";
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(border: "east", sourceAuthority: sourceAuthority, transferId: 23) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4, Mobility: Mobility(index: 4))],
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason), userMessage: reason);

        using var oracle = LocalOracle(subject: sourceAuthority);
        var security = new WorldAttestedAuthenticator(trustEntries: () => [TrustEntryFor(oracle: oracle)], oracle: oracle);
        using var host = new WorldTcpHost(server: fixture.Server, authenticator: security);

        host.Start(listen: "127.0.0.1:0");
        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        using var timeout = Laws.SocketDeadline();
        using var client = new TcpClient();

        await client.ConnectAsync(address: endpoint.Address, port: endpoint.Port, cancellationToken: timeout.Token);
        var stream = client.GetStream();

        await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: timeout.Token);
        var challenge = await RequireFrameAsync(stream: stream, ct: timeout.Token);
        var proof = security.Prove(challenge: challenge.Body);

        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(proof: proof), ct: timeout.Token);
        Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.IntentStream, body: default, ct: timeout.Token);
        Assert.Equal(expected: ((byte)WorldFederationResponse.Ack), actual: (await RequireFrameAsync(stream: stream, ct: timeout.Token)).Kind);

        var submission = new IntentSubmission(Tick: 1, EntityIndex: 0, Intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One), Principal: WorldPrincipal.Console);
        var carriedAuthority = (rebindAuthority ? "forged-world/source" : sourceAuthority);
        var mobility = request.Members[0].Mobility!.Value.Advance();

        if (!rebindAuthority) {
            mobility = mobility with { Epoch = (mobility.Epoch + 99UL) };
        }
        var body = WorldFederationCodec.EncodeIntent(mobility: in mobility, sourceAuthority: carriedAuthority, submission: in submission);

        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Intent, body: body, ct: timeout.Token);

        var refusal = await RequireFrameAsync(stream: stream, ct: timeout.Token);
        var expectedRefusal = (rebindAuthority ? WorldFederationRefusal.SourceAuthorityMismatch : WorldFederationRefusal.CredentialUnknown);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: refusal.Kind);
        Assert.StartsWith(expectedStartString: expectedRefusal.ToString(), actualString: Encoding.UTF8.GetString(bytes: refusal.Body), comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void CommitStatus_SurvivesALostAcknowledgement_AndOnlyExactCommitReplays() {
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(border: "east", sourceAuthority: "machine-a/boot", transferId: 29);
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: reservation.Accepted);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var firstReason), userMessage: firstReason);
        Assert.Equal(expected: WorldTransferStatus.Committed, actual: fixture.Server.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var replayReason), userMessage: replayReason);

        var altered = member with { HasMappedArrival = true };

        Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [altered], reason: out var alteredReason));
        Assert.Contains(actualString: alteredReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "different commit");
        fixture.Server.AbortTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
        Assert.Equal(expected: WorldTransferStatus.Committed, actual: fixture.Server.TransferStatus(sourceAuthority: request.SourceAuthority, transferId: request.TransferId));
    }
    [Fact]
    public void CommitStatus_DoesNotFabricateAnEarlierMissingTransferFromALaterCommit() {
        using var fixture = Fixtures.FreshServer();
        const string sourceAuthority = "machine-a/boot";
        var missing = Reservation(border: "east", sourceAuthority: sourceAuthority, transferId: 50);
        var later = Reservation(border: "east", sourceAuthority: sourceAuthority, transferId: 51) with {
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 1, Identity: null, Source: default, BodyColor: default, CatalogRig: 0, Mobility: Mobility(index: 1))],
        };

        Assert.True(condition: fixture.Server.ReserveTransfer(request: missing).Accepted);
        fixture.Server.AbortTransfer(sourceAuthority: sourceAuthority, transferId: missing.TransferId);
        Assert.Equal(WorldTransferStatus.Missing, fixture.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: missing.TransferId));

        Assert.True(condition: fixture.Server.ReserveTransfer(request: later).Accepted);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: later.TransferId, members: [member], reason: out var reason), userMessage: reason);

        Assert.Equal(WorldTransferStatus.Missing, fixture.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: missing.TransferId));
        Assert.Equal(WorldTransferStatus.Committed, fixture.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: later.TransferId));
    }
    [Fact]
    public void MobilityEpochLease_AllowsOnlyOneReservationAndReleasesOnAbort() {
        using var fixture = Fixtures.FreshServer();
        const string sourceAuthority = "machine-a/boot";
        var first = Reservation(border: "east", sourceAuthority: sourceAuthority, transferId: 52);
        var competing = first with { TransferId = 53 };

        var firstReply = fixture.Server.ReserveTransfer(request: first);
        var competingReply = fixture.Server.ReserveTransfer(request: competing);

        Assert.True(condition: firstReply.Accepted, userMessage: firstReply.Reason);
        Assert.False(condition: competingReply.Accepted);
        Assert.Contains("already leased", competingReply.Reason, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Server.TransferTableCounts.MobilityLeases);

        fixture.Server.AbortTransfer(sourceAuthority: sourceAuthority, transferId: first.TransferId);
        Assert.Equal(0, fixture.Server.TransferTableCounts.MobilityLeases);
        Assert.True(condition: fixture.Server.ReserveTransfer(request: competing).Accepted);
    }
    [Fact]
    public void MobilityEpochLease_RejectsTheSameEpochAfterACommit() {
        using var fixture = Fixtures.FreshServer();
        const string sourceAuthority = "machine-a/boot";
        var first = Reservation(border: "east", sourceAuthority: sourceAuthority, transferId: 54);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: fixture.Server.ReserveTransfer(request: first).Accepted);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: first.TransferId, members: [member], reason: out var reason), userMessage: reason);
        fixture.Server.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: first.TransferId);
        Assert.True(condition: fixture.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0));

        var stale = fixture.Server.ReserveTransfer(request: first with { TransferId = 55 });

        Assert.False(condition: stale.Accepted);
        Assert.Contains("stale", stale.Reason, StringComparison.Ordinal);
    }
    [Fact]
    public void Commit_RejectsAnInvalidContinuumBeforeLandingTheTraveler() {
        using var fixture = Fixtures.FreshServer();
        var request = Reservation(border: "east", sourceAuthority: "machine-a/boot", transferId: 30);
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: true,
            BodyMotionProgramName: "grounded",
            Position: default,
            YawRadians: default,
            PlanarVelocity: default,
            VerticalVelocity: default,
            Continuum: new WorldContinuumTrajectory(
                BoundaryEvents: 1,
                ConsumedThroughEngineTick: 840,
                ContinuumEndEngineTick: 840,
                ContinuumStartEngineTick: 840,
                PreviousPosition: default,
                SourceTick: 1));

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, members: [member], reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "invalid continuum");
        Assert.Null(@object: fixture.Server.Body(index: 0));
    }
    [Fact]
    public void MobilityCredential_ChurnIsBounded_AndGenerationBound() {
        using var fixture = Fixtures.FreshServer();
        const string sourceAuthority = "machine-a/boot";
        var mobility = Mobility(index: 0);

        for (ulong crossing = 1; (crossing <= 100); crossing++) {
            var request = Reservation(border: "seam", sourceAuthority: sourceAuthority, transferId: crossing) with {
                Members = [new WorldTransferReservationMember(
                    Principal: WorldPrincipal.Console,
                    PreferredSlot: 0,
                    Identity: null,
                    Source: IntentSource.Live,
                    BodyColor: default,
                    CatalogRig: 0,
                    Mobility: mobility)],
            };
            var reservation = fixture.Server.ReserveTransfer(request: request);

            Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
            var commit = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

            Assert.True(condition: fixture.Server.CommitTransfer(members: [commit], reason: out var reason, sourceAuthority: sourceAuthority, transferId: crossing), userMessage: reason);

            mobility = mobility.Advance();
            Assert.True(condition: fixture.Server.TryTransferredPrincipal(mobility: in mobility, principal: out var principal, sourceAuthority: sourceAuthority));
            var forgedFuture = mobility with { Epoch = (mobility.Epoch + 1UL) };

            Assert.False(condition: fixture.Server.TryTransferredPrincipal(mobility: in forgedFuture, principal: out _, sourceAuthority: sourceAuthority));
            var recycled = mobility with { Incarnation = mobility.Incarnation with { Generation = (mobility.Incarnation.Generation + 1) } };

            Assert.False(condition: fixture.Server.TryTransferredPrincipal(mobility: in recycled, principal: out _, sourceAuthority: sourceAuthority));

            fixture.Server.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: crossing);
            Assert.Equal(WorldTransferStatus.Missing, fixture.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: crossing));
            Assert.Equal(0, fixture.Server.TransferTableCounts.ActiveTransactions);
            Assert.Equal(1, fixture.Server.TransferTableCounts.MobilityCredentials);
            Assert.Equal(0, fixture.Server.TransferTableCounts.MobilityLeases);
            Assert.True(condition: fixture.Server.Population.TryDetachSeatForTransfer(slot: principal.Index, profile: out _));
            mobility = mobility.Advance(); // Model the intervening authority's commit before this traveler returns.
        }

        fixture.Server.RetireTransferredMobility(mobility: in mobility);
        Assert.Equal(0, fixture.Server.TransferTableCounts.MobilityCredentials);
    }
    [Fact]
    public void LostAcknowledgementOutcomes_AreSupersededPerMobilityIdentity() {
        using var fixture = Fixtures.FreshServer();
        const string sourceAuthority = "machine-a/boot";
        var mobility = Mobility(index: 0);
        var commit = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        for (ulong crossing = 60; (crossing < 80); crossing++) {
            var request = Reservation(border: "seam", sourceAuthority: sourceAuthority, transferId: crossing) with {
                Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 0, Mobility: mobility)],
            };

            Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
            Assert.True(condition: fixture.Server.CommitTransfer(members: [commit], reason: out var reason, sourceAuthority: sourceAuthority, transferId: crossing), userMessage: reason);
            Assert.Equal(1, fixture.Server.TransferTableCounts.ActiveTransactions);
            if (crossing > 60) {
                Assert.Equal(WorldTransferStatus.Missing, fixture.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: (crossing - 1UL)));
            }

            mobility = mobility.Advance();
            Assert.True(condition: fixture.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0));
            mobility = mobility.Advance(); // Model the other authority's ownership commit before returning.
        }

        fixture.Server.RetireTransferredMobility(mobility: in mobility);
        Assert.Equal(0, fixture.Server.TransferTableCounts.ActiveTransactions);
        Assert.Equal(0, fixture.Server.TransferTableCounts.MobilityCredentials);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ReservationWire_PreservesAnonymousAutonomousIntentSource(bool producer) {
        using var fixture = Fixtures.FreshServer();
        var source = (producer ? IntentSource.Producer(name: "wander") : IntentSource.Idle);
        var color = new Vector3(x: 0.125f, y: 0.5f, z: 0.875f);
        var request = Reservation(border: "seam", sourceAuthority: "machine-a/boot", transferId: 31) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: source, BodyColor: color, CatalogRig: 73, Mobility: Mobility(index: 4))],
        };

        var bytes = WorldFederationCodec.EncodeReservation(request: request);

        Assert.True(condition: WorldFederationCodec.TryDecodeReservation(body: bytes, defaults: fixture.Server.Definition.PlayerDefaults, request: out var decoded, failure: out var failure), userMessage: failure.ToString());
        var member = Assert.Single(collection: decoded!.Members);

        Assert.Null(@object: member.Identity);
        Assert.Equal(expected: source, actual: member.Source);
        Assert.Equal(expected: color, actual: member.BodyColor);
        Assert.Equal(expected: ((byte)73), actual: member.CatalogRig);
        Assert.True(condition: decoded.PeerAdmission);
    }
    [Fact]
    public void AutonomousTransfer_UsesEntityTableWithoutBecomingAHumanPeer() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(border: "seam", sourceAuthority: "machine-a/boot", transferId: 37) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Idle, BodyColor: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f), CatalogRig: 73, Mobility: Mobility(index: 4))],
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
        Assert.Equal(expected: ((byte)73), actual: fixture.Server.Population.CatalogRig(index: bodyIndex));
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
        var request = Reservation(border: "seam", sourceAuthority: "machine-a/boot", transferId: 41) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Producer(name: "not-declared"), BodyColor: default, CatalogRig: 4, Mobility: Mobility(index: 4))],
        };

        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.False(condition: reservation.Accepted);
        Assert.Contains(expectedSubstring: "declares no parameters for producer 'not-declared'", actualString: reservation.Reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void FederatedIntentStream_HoldsDeviceStateAcrossFasterDestinationTicks_AndReleasesByLease() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(border: "seam", sourceAuthority: "slow-player-world", transferId: 43) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4, Mobility: Mobility(index: 4))],
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
        for (var tick = 0; (tick < 120); tick++) {
            fixture.Step();
        }

        Assert.True(condition: ((body.FixedPosition - start).Length > FixedQ4816.FromDouble(value: 2.5)), userMessage: $"sparse held input only moved {((double)(body.FixedPosition - start).Length):0.###}");
        Assert.True(condition: (body.PlanarSpeed > 3.5f), userMessage: $"held stream settled at only {body.PlanarSpeed:0.###}");

        fixture.Server.ReleaseFederatedIntents(leaseId: 77);
        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0f, actual: body.PlanarSpeed, precision: 2);
    }
    [Fact]
    public void FederatedNeutralDeviceState_DoesNotMaskAnAuthorityAcceptedTapeSegment() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var request = Reservation(border: "seam", sourceAuthority: "player-world", transferId: 47) with {
            PeerAdmission = true,
            Members = [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 4, Identity: null, Source: IntentSource.Live, BodyColor: default, CatalogRig: 4, Mobility: Mobility(index: 4))],
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

        fixture.Server.ApplyCommand(command: new WorldCommand.EnqueueSegment(EntityIndex: bodyIndex, Intent: run, Principal: principal, Seconds: 1f));
        var neutral = new IntentSubmission(Tick: 1, EntityIndex: bodyIndex, Intent: default, Principal: principal);

        fixture.Server.PublishFederatedIntent(leaseId: 79, submission: in neutral);
        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step();
        }

        Assert.True(condition: ((body.FixedPosition - start).Length > FixedQ4816.FromDouble(value: 0.75)), userMessage: $"the held neutral device image masked the accepted tape segment: delta={((double)(body.FixedPosition - start).Length):0.###}, speed={body.PlanarSpeed:0.###}");
    }
    [Fact]
    public void SnapshotWire_RefusesAnEntryCountPastThePopulationCeilingByName() {
        var writer = new WireWriter();

        writer.WriteUInt64(value: 17UL);
        writer.WriteInt32(value: 19);
        writer.WriteUInt64(value: 210UL);
        writer.WriteString(value: "destination/world");
        writer.WriteInt32(value: (WorldBodiesLimits.CapacityCeiling + 1));

        Assert.False(condition: WorldFederationCodec.TryDecodeSnapshot(body: writer.ToArray(), snapshot: out _, failure: out var failure));
        Assert.Equal(expected: WireRefusal.CountOutOfRange, actual: failure.Refusal);
        Assert.Contains(expectedSubstring: "snapshot entry count", actualString: failure.Detail, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotWire_RefusesATruncatedRecordByName() {
        var snapshot = new WorldSnapshot(
            Tick: 17,
            Revision: 19,
            StepTicks: 210,
            Entries: new[] { new EntitySnapshot(Index: 3, Position: Vector3.Zero, Orientation: Quaternion.Identity, BodyColor: Vector3.One, Active: true, Kit: 0, Look: 0, CatalogRig: 2, Continuity: EntityContinuity.Continuous, Generation: 1, PlacementId: null) },
            Authority: "destination/world");
        var encoded = WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot);

        // The control: the whole record decodes with no refusal at all.
        Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(body: encoded, failure: out var control, snapshot: out var whole), userMessage: control.ToString());
        Assert.Equal(expected: WireRefusal.None, actual: control.Refusal);
        Assert.Single(collection: whole.Entries.ToArray());

        Assert.False(condition: WorldFederationCodec.TryDecodeSnapshot(body: encoded.AsSpan(start: 0, length: (encoded.Length - 6)), snapshot: out _, failure: out var failure));
        Assert.Equal(expected: WireRefusal.PayloadTruncated, actual: failure.Refusal);
    }
    [Fact]
    public void CommitWire_RefusesTrailingBytesAndAnOverCountedCohortByName() {
        using var fixture = Fixtures.FreshServer();
        var defaults = fixture.Server.Definition.PlayerDefaults;
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);
        var encoded = WorldFederationCodec.EncodeCommit(members: [member], sourceAuthority: "source/world", transferId: 7);

        Assert.True(condition: WorldFederationCodec.TryDecodeCommit(body: encoded, defaults: defaults, failure: out var control, members: out _, sourceAuthority: out _, transferId: out _), userMessage: control.ToString());

        Assert.False(condition: WorldFederationCodec.TryDecodeCommit(body: [.. encoded, 0], defaults: defaults, failure: out var trailing, members: out _, sourceAuthority: out _, transferId: out _));
        Assert.Equal(expected: WireRefusal.PayloadTrailingBytes, actual: trailing.Refusal);

        var writer = new WireWriter();

        writer.WriteString(value: "source/world");
        writer.WriteUInt64(value: 7UL);
        writer.WriteInt32(value: (WorldBodiesLimits.CapacityCeiling + 1));

        Assert.False(condition: WorldFederationCodec.TryDecodeCommit(body: writer.ToArray(), defaults: defaults, sourceAuthority: out _, transferId: out _, members: out _, failure: out var overCounted));
        Assert.Equal(expected: WireRefusal.CountOutOfRange, actual: overCounted.Refusal);
    }
    [Fact]
    public void ReservationWire_RefusesAnUndecodableTravelerIdentityByName() {
        using var fixture = Fixtures.FreshServer();
        var writer = new WireWriter();

        writer.WriteUInt64(value: 31UL);
        writer.WriteString(value: "machine-a/boot");
        writer.WriteInt32(value: 240);
        writer.WriteUInt64(value: 0UL);
        writer.WriteUInt64(value: 60UL);
        writer.WriteString(value: "seam");
        writer.WriteBoolean(value: false);
        writer.WriteBoolean(value: true);
        writer.WriteBoolean(value: true);
        writer.WriteInt32(value: 1);
        writer.WriteInt32(value: 0);
        writer.WriteString(value: "origin/world");
        writer.WriteInt32(value: 0);
        writer.WriteInt32(value: 7);
        writer.WriteUInt64(value: 0UL);
        writer.WriteByte(value: 1);
        writer.WriteVector(value: Vector3.Zero);
        writer.WriteByte(value: 0);
        // The traveler declares an identity and then supplies no id for it.
        writer.WriteBoolean(value: true);
        writer.WriteString(value: string.Empty);

        Assert.False(condition: WorldFederationCodec.TryDecodeReservation(body: writer.ToArray(), defaults: fixture.Server.Definition.PlayerDefaults, request: out var request, failure: out var failure));
        Assert.Null(@object: request);
        Assert.Contains(expectedSubstring: "identity id", actualString: failure.Detail, comparisonType: StringComparison.Ordinal);
    }

    /// <summary>A fresh, throwaway <see cref="LocalKeySigningOracle"/> for one test's own SignsDirectly identity.</summary>
    private static LocalKeySigningOracle LocalOracle(string subject) => new(
        key: ECDsa.Create(curve: ECCurve.NamedCurves.nistP256),
        subject: subject,
        validity: TimeSpan.FromMinutes(value: 5)
    );
    /// <summary>The SignsDirectly admission row a peer must author to trust <paramref name="oracle"/>'s own key.</summary>
    private static WorldAdmissionEntry TrustEntryFor(LocalKeySigningOracle oracle) => new(
        Domain: oracle.Domain,
        Subject: oracle.Subject,
        Mode: WorldAdmissionTrustMode.SignsDirectly,
        Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        PublicKey: Convert.ToBase64String(inArray: oracle.PublicKeySubjectPublicKeyInfo),
        Grants: []);
    private static WorldTransferReservationRequest Reservation(string sourceAuthority, ulong transferId, string border) =>
        new(TransferId: transferId, SourceAuthority: sourceAuthority, SourceRateHz: 240, SourceTick: 0, DeadlineSourceTick: 60, Border: border, BorderCapacity: null, PartyAllOrNothing: true, PeerAdmission: false, Members: [new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: 0, Identity: null, Source: default, BodyColor: default, CatalogRig: 0, Mobility: Mobility(index: 0))]);
    private static WorldMobilityIdentity Mobility(int index, ulong epoch = 0) =>
        new(Incarnation: new WorldEntityAddress(Authority: "origin/world", Generation: 7, Index: index), Epoch: epoch);
    private static WorldDefinition TransferPopulationDocument() {
        var document = Fixtures.BuildDocument();

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2),
                NetworkPlayers = 2,
            },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
    }
    // A budget expiry and a refusal must never read alike: the first says the machine never scheduled this exchange
    // within Laws.SocketBudget, the second is the law's own subject matter.
    private static async Task<WireFrameRead> RequireFrameAsync(NetworkStream stream, CancellationToken ct) {
        WireFrameRead read;

        try {
            read = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: stream);
        } catch (OperationCanceledException) {
            throw new Xunit.Sdk.XunitException(userMessage: $"no federation response frame arrived within the {Laws.SocketBudget.TotalSeconds:0}s socket budget");
        }

        return (read.Ok ? read : throw new Xunit.Sdk.XunitException(userMessage: $"federation peer answered no response frame ({read.Failure})"));
    }
}

/// <summary>An <see cref="IAuthenticator"/> whose challenge and proof widths deliberately differ from a 32-byte
/// scheme, proving the federation door carries no scheme-specific width of its own.</summary>
file sealed class OddWidthAuthenticator : IAuthenticator {
    public const int ChallengeWidth = 5;

    private const string Subject = "odd-width/source";

    private static byte[] Compute(ReadOnlySpan<byte> challenge) {
        var authority = Encoding.UTF8.GetBytes(s: Subject);
        var proof = new byte[(authority.Length + challenge.Length)];

        authority.CopyTo(array: proof, index: 0);
        challenge.CopyTo(destination: proof.AsSpan(start: authority.Length));

        return proof;
    }

    public int ChallengeBytes => ChallengeWidth;
    public bool IsConfigured => true;

    public byte[] NewChallenge() => [1, 2, 3, 4, 5];
    public byte[] Prove(ReadOnlySpan<byte> challenge) => Compute(challenge: challenge);
    public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
        sourceAuthority = null;

        if (!proof.SequenceEqual(other: Compute(challenge: challenge))) {
            return false;
        }

        sourceAuthority = Subject;

        return true;
    }
}
