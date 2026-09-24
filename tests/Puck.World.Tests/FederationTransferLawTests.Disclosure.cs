using Puck.Commands;
using System.Net;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for what a federated traveler is handed of its destination's hidden state: every federation egress
/// that knows the traveler composes the document for it, through the one disclosure door.</summary>
public sealed partial class FederationTransferLawTests {
    private const string DisclosureSource = "player-world/source";
    private const string GateOpen = "gateOpen";
    private const string GateShut = "gateShut";
    private const string TasteSpace = "taste";

    // Bodies 4 and 5 arrive as travelers. Each owns a slot and a vector only it reads, whose reader list is a keyed
    // text row naming its principal, and a gate that slot opens. Both vectors share one declared space, which only a
    // traveler handed one of them needs.
    private static WorldDefinition TwoTravelerDocument() {
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 2);
        var slot = StateRow.SlotKey;

        WorldStateRow Aim(int body) => new(
            Cells: [new StateCell(Key: slot, Value: CellValue.Vector(components: Aimed(body: body).Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: $"aim{body}"),
            Space: TasteSpace,
            Visibility: new StateVisibility(ReadersFrom: $"owner{body}")
        );

        WorldStateRow Owner(int body) => new(
            Capacity: 1,
            Domain: StateDomain.Keys.Instance,
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: $"owner{body}")
        );
        WorldStateRow Mine(int body) => new(
            Cells: [new StateCell(Key: slot, Value: CellValue.Int(value: 0L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: $"mine{body}"),
            Visibility: new StateVisibility(ReadersFrom: $"owner{body}")
        );
        WorldPlacement Gate(int body) => new(
            Id: $"gate{body}",
            PrototypeId: GateShut,
            Position: new DocumentVector3(value: Vector3.Zero),
            YawDegrees: 0f,
            Scale: 1f,
            Respond: [new WorldPlacementResponse(
                When: new WorldPlacementResponseCondition.StateCondition(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    State: $"mine{body}",
                    Value: 1f
                ),
                PrototypeId: GateOpen
            )]
        );

        return document with {
            CreationsRaw = [.. document.Creations, CreationFixtures.UnitSphere(id: GateShut), CreationFixtures.UnitSphere(id: GateOpen)],
            PlacementRowsRaw = [.. document.Placements, Gate(body: 4), Gate(body: 5)],
            StateRaw = new WorldStateSection(
                Spaces: [new StateSpace(dimensions: 8, model: "test-model", name: CellName.Parse(candidate: TasteSpace), revision: "1")],
                World: [.. document.AuthoredState, Owner(body: 4), Owner(body: 5), Mine(body: 4), Mine(body: 5), Aim(body: 4), Aim(body: 5)]
            ),
        };
    }
    // Each traveler's own unit vector: all its weight on one component of the eight.
    private static StateVector Aimed(int body) {
        var components = new sbyte[8];

        components[(body - 4)] = 127;

        return StateVector.CreateUnchecked(components: components);
    }
    private static void SetCell(WorldFixture fixture, string row, string key, long value, string? text = null) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: Principal.Console,
        Row: row,
        Key: key,
        Value: value,
        Kind: WorldDocumentWriteKind.Set,
        Text: text
    ));
    private static async Task<Stream> AuthenticatedLaneAsync(PeerTestClient client, IPEndPoint endpoint, WorldAttestedAuthenticator security, CancellationToken ct) {
        await client.ConnectAsync(
            address: endpoint.Address,
            port: endpoint.Port,
            cancellationToken: ct
        );

        var stream = client.GetStream();

        await HandshakeWireFormat.WriteHelloAsync(
            ct: ct,
            key: WorldFederationCodec.WireKey,
            stream: stream
        );

        var challenge = await RequireFrameAsync(
            ct: ct,
            stream: stream
        );

        await WorldFederationCodec.WriteRequestAsync(
            body: WorldFederationCodec.EncodeAuthentication(proof: security.Prove(challenge: challenge.Body.Span)),
            ct: ct,
            kind: WorldFederationRequest.Authenticate,
            stream: stream
        );
        Assert.Equal(
            actual: (await RequireFrameAsync(
                ct: ct,
                stream: stream
            )).Kind,
            expected: ((byte)WorldFederationResponse.Authenticated)
        );

        return stream;
    }
    // Reads an observation stream up to its first document and parses it as the projection it carries.
    private static async Task<WorldProjectionDocument> FirstProjectionAsync(Stream stream, CancellationToken ct) {
        while (true) {
            var frame = await RequireFrameAsync(
                ct: ct,
                stream: stream
            );

            if (frame.Kind != ((byte)WorldFederationResponse.Definition)) {
                continue;
            }

            Assert.Equal(
                actual: frame.Body.Span[0],
                expected: ((byte)WorldDisclosureTier.Presentation)
            );
            Assert.True(condition: WorldProjection.TryDeserialize(
                projection: out var projection,
                reason: out var reason,
                utf8Json: frame.Body.Span[1..]
            ), userMessage: reason);

            return projection!;
        }
    }
    private static string PrototypeOf(IReadOnlyList<WorldPlacement> placements, int body) =>
        WorldDefinitionRows.FindPlacement(id: $"gate{body}", placements: placements)!.ShownPrototypeId;
    private static long? ObservedSlot(WorldProjectionDocument projection, int body) =>
        ((projection.Observations?.FirstOrDefault(predicate: row => (row.Name == $"mine{body}")) is { } row)
            ? row.Cells.Single().Value
            : null);
    // The hidden slot as the remote client's rebuilt document holds it.
    private static long? HydratedSlot(WorldDefinition definition, int body) =>
        ((WorldDefinitionRows.FindStateRow(name: $"mine{body}", rows: definition.State) is { } row)
            ? row.Cells!.Single().Value.AsInt
            : null);
    // The hidden vector as the remote client's rebuilt document holds it, checked against the space it names.
    private static StateVector? HydratedAim(WorldDefinition definition, int body) {
        if (WorldDefinitionRows.FindStateRow(name: $"aim{body}", rows: definition.State) is not { } row) {
            return null;
        }

        Assert.Equal(expected: TasteSpace, actual: row.Space);
        Assert.Contains(collection: (definition.StateRaw!.Spaces ?? []), filter: space => (space.Name.Value == TasteSpace));

        return StateVector.CreateUnchecked(components: row.Cells!.Single().Value.AsVector.Span);
    }

    // Two travelers from one source own one hidden slot each, and both slots have opened their gates. Over the
    // federation lanes, the route traveler 4 is answered and the observation stream it opens carry its own slot and
    // gate and none of traveler 5's, and the document the traveler rebuilds from either holds its own slot as state;
    // traveler 5's route is the mirror image. The seatless observe lane carries neither.
    [Fact]
    public async Task ARemoteTravelerIsHandedItsOwnHiddenStateAndNoneOfAnothers() {
        using var fixture = Fixtures.FreshServer(definition: TwoTravelerDocument());
        var request = Reservation(
            border: "east",
            sourceAuthority: DisclosureSource,
            transferId: 41
        ) with {
            PeerAdmission = true,
            Members = ((int[])[4, 5]).Select(selector: index => new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: index,
                Identity: null,
                Source: IntentSource.Live,
                BodyColor: default,
                CatalogRig: 4,
                Mobility: Mobility(index: index)
            )).ToArray(),
        };
        var reservation = fixture.Server.ReserveTransfer(request: request);
        var member = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: false,
            BodyMotionProgramName: "grounded",
            Position: default,
            YawRadians: default,
            PlanarVelocity: default,
            VerticalVelocity: default
        );

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(
            members: [member, member],
            reason: out var reason,
            sourceAuthority: DisclosureSource,
            transferId: request.TransferId
        ), userMessage: reason);

        foreach (var body in ((int[])[4, 5])) {
            SetCell(fixture: fixture, key: "reader", row: $"owner{body}", text: fixture.Server.Population.PeerPrincipal(index: body).Describe(), value: 0L);
            SetCell(fixture: fixture, key: StateRow.SlotKey.Value, row: $"mine{body}", value: 1L);
        }

        fixture.Step();
        fixture.Step();

        // The control: both gates stand open on the server, each a reading of its owner's hidden slot.
        Assert.Equal(expected: GateOpen, actual: PrototypeOf(body: 4, placements: fixture.Server.Definition.Placements));
        Assert.Equal(expected: GateOpen, actual: PrototypeOf(body: 5, placements: fixture.Server.Definition.Placements));

        using var oracle = LocalOracle(subject: DisclosureSource);
        var security = new WorldAttestedAuthenticator(
            trustEntries: () => [TrustEntryFor(oracle: oracle)],
            oracle: oracle
        );
        using var host = new WorldPeerHost(
            server: fixture.Server,
            authenticator: security,
            timeProvider: new VirtualClock(),
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout
        );

        host.Start(listen: "127.0.0.1:0");

        var endpoint = IPEndPoint.Parse(s: host.ListenEndpoint!);
        var ct = TestContext.Current.CancellationToken;
        using var travelerClient = new PeerTestClient();
        var lane = await AuthenticatedLaneAsync(
            client: travelerClient,
            ct: ct,
            endpoint: endpoint,
            security: security
        );

        foreach (var (body, other) in ((ValueTuple<int, int>[])[(4, 5), (5, 4)])) {
            var mobility = request.Members[(body - 4)].Mobility!.Value.Advance();

            await WorldFederationCodec.WriteRequestAsync(
                body: WorldFederationCodec.EncodeRouteCredential(
                    mobility: in mobility,
                    sourceAuthority: DisclosureSource
                ),
                ct: ct,
                kind: WorldFederationRequest.Route,
                stream: lane
            );

            var answer = await RequireFrameAsync(
                ct: ct,
                stream: lane
            );

            Assert.Equal(expected: ((byte)WorldFederationResponse.Route), actual: answer.Kind);
            Assert.True(condition: WorldFederationCodec.TryDecodeRoute(
                body: answer.Body.Span,
                failure: out var failure,
                route: out var route
            ), userMessage: failure.ToString());
            Assert.Equal(expected: GateOpen, actual: PrototypeOf(body: body, placements: route.Definition.Placements));
            Assert.Equal(expected: GateShut, actual: PrototypeOf(body: other, placements: route.Definition.Placements));
            Assert.Equal(expected: 1L, actual: HydratedSlot(body: body, definition: route.Definition));
            Assert.Null(@object: HydratedSlot(body: other, definition: route.Definition));
            Assert.Equal(expected: Aimed(body: body), actual: HydratedAim(body: body, definition: route.Definition));
            Assert.Null(@object: HydratedAim(body: other, definition: route.Definition));
        }

        var travelerFour = request.Members[0].Mobility!.Value.Advance();

        await WorldFederationCodec.WriteRequestAsync(
            body: WorldFederationCodec.EncodeTravelerObservation(request: new WorldTravelerObservation(
                Mobility: travelerFour,
                SourceAuthority: DisclosureSource
            )),
            ct: ct,
            kind: WorldFederationRequest.ObserveTraveler,
            stream: lane
        );

        var observed = await FirstProjectionAsync(
            ct: ct,
            stream: lane
        );

        Assert.Equal(expected: 1L, actual: ObservedSlot(body: 4, projection: observed));
        Assert.Null(@object: ObservedSlot(body: 5, projection: observed));
        Assert.Equal(expected: GateOpen, actual: PrototypeOf(body: 4, placements: observed.Placements));
        Assert.Equal(expected: GateShut, actual: PrototypeOf(body: 5, placements: observed.Placements));
        Assert.True(condition: WorldProjection.TryToDefinition(
            definition: out var rebuilt,
            projection: observed,
            reason: out var rebuildReason
        ), userMessage: rebuildReason);
        Assert.Equal(expected: 1L, actual: HydratedSlot(body: 4, definition: rebuilt!));
        Assert.Null(@object: HydratedSlot(body: 5, definition: rebuilt!));
        Assert.Equal(expected: Aimed(body: 4), actual: HydratedAim(body: 4, definition: rebuilt!));
        Assert.Null(@object: HydratedAim(body: 5, definition: rebuilt!));

        var loadedAt = ArenaTime.At(engineTick: 0UL, tick: 0UL);

        // The rebuilt rows load into a store like any document's.
        Assert.True(condition: StateArena.TryCreate(
            arena: out _,
            catalog: rebuilt!.StateCatalog,
            options: null,
            reason: out var loadReason,
            section: rebuilt.StateRaw,
            time: in loadedAt
        ), userMessage: loadReason);

        using var observerClient = new PeerTestClient();
        var observer = await AuthenticatedLaneAsync(
            client: observerClient,
            ct: ct,
            endpoint: endpoint,
            security: security
        );

        await WorldFederationCodec.WriteRequestAsync(
            body: default,
            ct: ct,
            kind: WorldFederationRequest.Observe,
            stream: observer
        );

        var seatless = await FirstProjectionAsync(
            ct: ct,
            stream: observer
        );

        Assert.Null(@object: ObservedSlot(body: 4, projection: seatless));
        Assert.Null(@object: ObservedSlot(body: 5, projection: seatless));
        // No vector row reaches the public observer, so neither does the space.
        Assert.Null(@object: seatless.Spaces);
        Assert.Equal(expected: GateShut, actual: PrototypeOf(body: 4, placements: seatless.Placements));
        Assert.Equal(expected: GateShut, actual: PrototypeOf(body: 5, placements: seatless.Placements));
    }
}
