using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a body walking into a portal face crosses to the portal's destination and arrives mapped. Two
/// file-backed rows run under one <see cref="WorldInstanceHost"/>: row A's door carries a <see cref="WorldPlacementPortal"/>
/// with <see cref="WorldPortalArrival.Mapped"/> arrival whose counterpart is row B's door, placed elsewhere and turned a
/// quarter. A seat joins row A and walks forward, under ordinary intent and ordinary host stepping, into the door's
/// one-sided aperture: the per-step portal scan (<c>WorldInstanceHost.ScanInstancePortals</c>) mints the transfer, and
/// the body then stands in row B, gone from row A, at exactly the pose <see cref="WorldFrameIsometry.MapArrival"/> maps
/// its last source pose to through the two doors' derived face frames. The control walks the same way beside the door,
/// outside its aperture, and stays in row A.
/// </summary>
public sealed class PortalWalkThroughLawTests {
    private const string DoorPrototype = "door";
    private const string Face = "door";
    private const float DoorScale = 5f;
    private const int WalkBound = 120;
    private const int ForwardOrdinal = 0;

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
    // One row: the fixture document, the door creation, one door, and the other row as its one destination.
    private static WorldDefinition Row(string neighbourPath, WorldPlacement door) => (Fixtures.BuildDocument() with {
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

    [InlineData(0f, true)]
    [InlineData(8f, false)]
    [Theory]
    public void ABodyWalkingIntoAPortalArrivesMappedInItsDestination(float startX, bool shouldCross) {
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
                    Travel: null
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

            var actor = Principal.Seat(slot: 0);

            Assert.True(condition: rowAServer.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: actor,
                Slot: actor.Index,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);

            for (var tick = 0; (tick < 5); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }

            // Stand the body a short walk in front of the door's aperture (or, for the control, as far to the side
            // of it), facing it: yaw zero faces -Z.
            rowAServer.Body(index: actor.Index)!.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: startX,
                y: 0f,
                yawRadians: 0f,
                z: -3f
            );

            var sourcePosition = default(FixedVector3);
            var sourceYaw = FixedQ4816.Zero;
            var crossedOnTick = -1;

            for (var tick = 0; (tick < WalkBound); tick++) {
                host.DrainPendingTransfers();

                if (rowBServer.Population.IsActive(index: actor.Index)) {
                    crossedOnTick = tick;

                    break;
                }

                var body = rowAServer.Body(index: actor.Index)!;

                body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                    ordinal: ForwardOrdinal,
                    value: FixedQ4816.One
                ));
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

                // The pose the transfer maps is the one the source body holds after the step that minted it, before
                // the next drain detaches it.
                if (rowAServer.Population.IsActive(index: actor.Index)) {
                    sourcePosition = rowAServer.Body(index: actor.Index)!.FixedPosition;
                    sourceYaw = rowAServer.Body(index: actor.Index)!.FixedYaw;
                }
            }

            if (!shouldCross) {
                Assert.Equal(expected: -1, actual: crossedOnTick);
                Assert.True(condition: rowAServer.Population.IsActive(index: actor.Index));
                Assert.False(condition: rowBServer.Population.IsActive(index: actor.Index));

                return;
            }

            Assert.True(condition: (crossedOnTick > 0), userMessage: $"the body walked {WalkBound} ticks toward the door and never crossed: it stands at {sourcePosition} facing {sourceYaw}, the face at {sourceFace.Frame.Origin} normal {sourceFace.Frame.Normal} half {sourceFace.Frame.HalfWidth}x{sourceFace.Frame.HalfHeight}x{sourceFace.Frame.HalfDepth}, aperture {sourceFace.Aperture is not null}");
            Assert.False(
                condition: rowAServer.Population.IsActive(index: actor.Index),
                userMessage: "the source seat stayed active after the crossing"
            );

            var arrived = rowBServer.Body(index: actor.Index);
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
