using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every body that can travel and walks into a portal face crosses to the portal's destination
/// and arrives mapped: a local seat, an admitted peer's traveller, and a body the world's own census authors alike. Two
/// file-backed rows run under one <see cref="WorldInstanceHost"/>: row A's door carries a <see cref="WorldPlacementPortal"/>
/// with <see cref="WorldPortalArrival.Mapped"/> arrival whose counterpart is row B's door, placed elsewhere and turned a
/// quarter. The body walks forward, under ordinary intent and ordinary host stepping, into the door's one-sided
/// aperture: the per-step portal scan (<c>WorldInstanceHost.ScanInstancePortals</c>), which reads the same traveller
/// set the seam scan does, mints the transfer, and the body then stands in row B, gone from row A, at exactly the pose
/// <see cref="WorldFrameIsometry.MapArrival"/> maps its last source pose to through the two doors' derived face frames.
/// A scan limited to local seats leaves the peer and the census body walking through the door in row A. The control
/// walks the same way beside the door, outside its aperture, and stays in row A. A party door a traveller that is no
/// local seat enters carries that traveller alone: a local seat standing in row A stays there.
/// </summary>
public sealed class PortalWalkThroughLawTests {
    /// <summary>The kind of body a walk carries through the door.</summary>
    public enum Traveller {
        /// <summary>A local seat, joined through a session.</summary>
        Seat,
        /// <summary>An admitted peer's traveller, committed through federation.</summary>
        Peer,
        /// <summary>A body the world's own census authors.</summary>
        Census,
    }

    private const ulong ArrivalTransferId = 4_201UL;
    private const string DoorPrototype = "door";
    private const float DoorScale = 5f;
    private const string Face = "door";
    private const int ForwardOrdinal = 0;
    private const string SourceAuthority = "peer-world/source";
    private const int WalkBound = 120;

    private static WorldPlacement Door(string id, Vector3 position, float yawDegrees, WorldPlacementPortal? portal) => new(
        FaceSources: [new WorldPlacementFace(
            Face: Face,
            Portal: portal,
            Source: new WorldScreenSource.None()
        )],
        Id: id,
        Position: position,
        PrototypeId: DoorPrototype,
        Scale: DoorScale,
        YawDegrees: yawDegrees
    );
    // One row: a document with room for peers and census bodies, the door creation, one door, and the other row as
    // its one destination.
    private static WorldDefinition Row(string neighbourPath, WorldPlacement door) => (Fixtures.PeerPopulationDocument(networkPlayers: 2) with {
        CreationsRaw = [PortalArrivalValidationLawTests.BuildDoorCreation()],
        Destinations = [new WorldDestination(
            Durability: WorldDestinationDurability.Persisted,
            Name: SafeName.Parse(candidate: "neighbour"),
            Reference: "neighbour",
            Scope: WorldDestinationScope.Global
        )],
        PlacementRowsRaw = [door],
        References = [new WorldReference(
            Document: WorldDocumentName.OfDocumentFile(path: neighbourPath),
            Name: SafeName.Parse(candidate: "neighbour")
        )],
    });
    // Commits one traveller into a row the way an authenticated peer authority does, and answers its body.
    private static int AdmitPeer(WorldServer server, int ordinal = 0) {
        var origin = new WorldEntityAddress(
            Authority: SourceAuthority,
            Generation: 3,
            Index: (WorldBodiesLimits.LocalSeatCount + ordinal)
        );
        var reservation = server.ReserveTransfer(request: new WorldTransferReservationRequest(
            Border: "door",
            BorderCapacity: null,
            DeadlineSourceTick: 60,
            Members: [new WorldTransferReservationMember(
                BodyColor: default,
                CatalogRig: 4,
                Identity: null,
                Mobility: new WorldMobilityIdentity(
                    DepartedFrom: origin,
                    Epoch: 0,
                    Incarnation: origin
                ),
                PreferredSlot: origin.Index,
                Principal: Principal.Console,
                Source: IntentSource.Live
            )],
            PartyAllOrNothing: true,
            PeerAdmission: true,
            SourceAuthority: SourceAuthority,
            SourceRateHz: 240,
            SourceTick: 0,
            TransferId: (ArrivalTransferId + ((ulong)ordinal))
        ));

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.True(
            condition: server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    BodyMotionProgramName: "grounded",
                    HasMappedArrival: false,
                    PlanarVelocity: default,
                    Position: default,
                    Profile: null,
                    VerticalVelocity: default,
                    YawRadians: default
                )],
                reason: out var reason,
                sourceAuthority: SourceAuthority,
                transferId: (ArrivalTransferId + ((ulong)ordinal))
            ),
            userMessage: reason
        );

        var index = Assert.Single(collection: reservation.BodyIndices);

        Assert.True(condition: server.Population.IsAdmittedPeer(bodyIndex: index));

        return index;
    }
    // Raises the row's census by one body and answers it.
    private static int RaiseCensusBody(WorldServer server) {
        var population = server.Population;

        Assert.Equal(
            actual: population.SetSimulatedCount(count: 1),
            expected: 1
        );

        for (var index = population.LocalSeatCount; (index < population.Capacity); index++) {
            if (population.IsActive(index: index) && !population.IsAdmittedPeer(bodyIndex: index)) {
                // The census leaves its bodies idle here; the walk drives this one the way a producer would.
                server.Body(index: index)!.SetIntentSource(source: IntentSource.Live);

                return index;
            }
        }

        throw new InvalidOperationException(message: "the census raised no body");
    }
    // The one active body a row holds, or -1.
    private static int OnlyActive(WorldPopulation population) {
        var found = -1;

        for (var index = 0; (index < population.Capacity); index++) {
            if (population.IsActive(index: index)) {
                Assert.Equal(
                    actual: found,
                    expected: -1
                );
                found = index;
            }
        }

        return found;
    }

    [InlineData(Traveller.Seat, 0f, WorldPortalTravel.Body, true)]
    [InlineData(Traveller.Seat, 8f, WorldPortalTravel.Body, false)]
    [InlineData(Traveller.Peer, 0f, WorldPortalTravel.Body, true)]
    [InlineData(Traveller.Peer, 8f, WorldPortalTravel.Body, false)]
    [InlineData(Traveller.Census, 0f, WorldPortalTravel.Body, true)]
    [InlineData(Traveller.Peer, 0f, WorldPortalTravel.Party, true)]
    [InlineData(Traveller.Peer, 0f, WorldPortalTravel.Body, true, true)]
    [InlineData(Traveller.Peer, 0f, WorldPortalTravel.Party, true, true)]
    [InlineData(Traveller.Census, 0f, WorldPortalTravel.Body, true, true)]
    [Theory]
    public void ABodyWalkingIntoAPortalArrivesMappedInItsDestination(Traveller traveller, float startX, WorldPortalTravel travel, bool shouldCross, bool companion = false) {
        using var files = new TemporaryDirectory(prefix: "puck-portal-walk-files-");
        var rowAPath = Path.Combine(
            path1: files.RootPath,
            path2: "row-a.world.json"
        );
        var rowBPath = Path.Combine(
            path1: files.RootPath,
            path2: "row-b.world.json"
        );
        // Row A's door stands ahead of the spawn, turned a half so its face looks back along +Z toward the body: its
        // aperture is the slab reaching from the placement toward the body, which a walk along -Z enters from the front.
        // Row B's door stands elsewhere, turned a quarter, so the mapping moves and turns the traveler.
        var rowADefinition = Row(
            door: Door(
                id: "door-a",
                portal: new WorldPlacementPortal(
                    Arrival: WorldPortalArrival.Mapped,
                    Counterpart: $"door-b/{Face}",
                    Destination: "neighbour",
                    Travel: travel
                ),
                position: new Vector3(x: 0f, y: 0f, z: -10f),
                yawDegrees: 180f
            ),
            neighbourPath: rowBPath
        );
        var rowBDefinition = Row(
            door: Door(
                id: "door-b",
                portal: null,
                position: new Vector3(x: 30f, y: 0f, z: 30f),
                yawDegrees: 90f
            ),
            neighbourPath: rowAPath
        );

        FileBackedRows.Write((rowAPath, rowADefinition), (rowBPath, rowBDefinition));

        Assert.True(condition: WorldFaceCatalog.For(definition: rowADefinition).TryFind(placementId: "door-a", faceName: Face, out var sourceFace));
        Assert.True(condition: WorldFaceCatalog.For(definition: rowBDefinition).TryFind(placementId: "door-b", faceName: Face, out var counterpartFace));

        using var hostStateRoot = new TemporaryDirectory(prefix: "puck-portal-walk-host-");
        using var host = new WorldInstanceHost(
            admitsSpawn: true,
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostStateRoot.RootPath)
        );

        var (rowAInstance, rowAServer, rowAStateDirectory) = FileBackedRows.Build(
            definition: rowADefinition,
            name: "row-a",
            path: rowAPath
        );
        var (rowBInstance, rowBServer, rowBStateDirectory) = FileBackedRows.Build(
            definition: rowBDefinition,
            name: "row-b",
            path: rowBPath
        );

        try {
            host.Admit(row: rowAInstance);
            host.Admit(row: rowBInstance);

            var seat = Principal.Seat(slot: 0);
            // A party door is entered by a traveller that is no local seat while a local seat stands in row A, so the
            // seat is joined for that row as well as for the seat's own walk.
            var joinsSeat = ((traveller == Traveller.Seat) || (travel == WorldPortalTravel.Party));

            if (joinsSeat) {
                Assert.True(condition: rowAServer.ApplySession(request: new SessionRequest.Join(
                    IdentityName: null,
                    Principal: seat,
                    Slot: seat.Index,
                    WireProtocolKey: WorldProtocol.WireProtocolKey
                )).Accepted);
            }

            var walker = traveller switch {
                Traveller.Peer => AdmitPeer(server: rowAServer),
                Traveller.Census => RaiseCensusBody(server: rowAServer),
                _ => seat.Index,
            };
            var walkers = (companion
                ? new[] { walker, AdmitPeer(ordinal: 1, server: rowAServer) }
                : new[] { walker });

            for (var tick = 0; (tick < 5); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }

            // Stand the body a short walk in front of the door's aperture (or, for the control, as far to the side
            // of it), facing it: yaw zero faces -Z. It stands high enough that a body whose kit carries no collider,
            // and so falls as it walks, is still above the door's crossing floor when it reaches the aperture.
            foreach (var bodyIndex in walkers) {
                rowAServer.Body(index: bodyIndex)!.Pose(
                    pitchRadians: 0f,
                    rollRadians: 0f,
                    x: startX,
                    y: 4.5f,
                    yawRadians: 0f,
                    z: -3f
                );
            }

            var sourcePosition = default(FixedVector3);
            var sourceYaw = FixedQ4816.Zero;
            var crossedOnTick = -1;

            for (var tick = 0; (tick < WalkBound); tick++) {
                host.DrainPendingTransfers();

                if (!rowAServer.Population.IsActive(index: walker)) {
                    crossedOnTick = tick;

                    break;
                }

                foreach (var bodyIndex in walkers) {
                    rowAServer.Body(index: bodyIndex)!.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                        ordinal: ForwardOrdinal,
                        value: FixedQ4816.One
                    ));
                }
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

                // The pose the transfer maps is the one the source body holds after the step that minted it, before
                // the next drain detaches it.
                if (rowAServer.Population.IsActive(index: walker)) {
                    sourcePosition = rowAServer.Body(index: walker)!.FixedPosition;
                    sourceYaw = rowAServer.Body(index: walker)!.FixedYaw;
                }
            }

            if (!shouldCross) {
                Assert.Equal(actual: crossedOnTick, expected: -1);
                Assert.True(condition: rowAServer.Population.IsActive(index: walker));
                Assert.Equal(
                    actual: OnlyActive(population: rowBServer.Population),
                    expected: -1
                );

                return;
            }

            Assert.True(condition: (crossedOnTick > 0), userMessage: $"the {traveller} walked {WalkBound} ticks toward the door and never crossed: it stands at {sourcePosition} facing {sourceYaw}, the face at {sourceFace.Frame.Origin} normal {sourceFace.Frame.Normal} half {sourceFace.Frame.HalfWidth}x{sourceFace.Frame.HalfHeight}x{sourceFace.Frame.HalfDepth}, aperture {(sourceFace.Aperture is not null)}");

            // The traveller alone arrived: a local seat standing in row A beside a party door another traveller
            // entered stays in row A.
            var arrivals = Enumerable.Range(start: 0, count: rowBServer.Population.Capacity)
                .Where(predicate: index => rowBServer.Population.IsActive(index: index)).ToArray();

            Assert.Equal(actual: arrivals.Length, expected: walkers.Length);
            foreach (var bodyIndex in walkers) {
                Assert.False(condition: rowAServer.Population.IsActive(index: bodyIndex));
            }
            var arrivedIndex = arrivals[0];

            Assert.NotEqual(
                actual: arrivedIndex,
                expected: -1
            );

            if ((traveller != Traveller.Seat) && joinsSeat) {
                Assert.True(
                    condition: rowAServer.Population.IsActive(index: seat.Index),
                    userMessage: "the local seat went with a traveller that is no local seat"
                );
            }

            var arrived = rowBServer.Body(index: arrivedIndex);
            var sourceFrame = sourceFace.Frame;
            var counterpartFrame = counterpartFace.Frame;
            var expected = WorldFrameIsometry.MapArrival(
                destination: in counterpartFrame,
                source: in sourceFrame,
                travelerPlanarVelocity: default,
                travelerPosition: sourcePosition,
                travelerVerticalVelocity: FixedQ4816.Zero,
                travelerYawRadians: sourceYaw
            );

            Assert.NotNull(@object: arrived);
            Assert.Equal(
                expected: expected.Position,
                actual: arrived!.FixedPosition
            );
            Assert.Equal(
                expected: expected.YawRadians,
                actual: arrived.FixedYaw
            );
        } finally {
            rowAInstance.Dispose();
            rowAStateDirectory.Dispose();
            rowBInstance.Dispose();
            rowBStateDirectory.Dispose();
        }
    }
}
