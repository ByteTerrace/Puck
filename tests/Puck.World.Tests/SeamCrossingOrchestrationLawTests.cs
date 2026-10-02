using Puck.Commands;
using System.Numerics;
using Xunit;

using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the scan-triggered half of a seam crossing — <c>WorldInstanceHost.ScanInstanceAdjacencies</c>
/// minting a transfer off the ordinary per-step boundary scan, never the admin-driven <c>EnqueueTransfer</c> path
/// <see cref="WorldInstanceHostTwoRowTransferLawTests"/> and <see cref="HostRoundtripFixture"/> already cover. A body
/// already past a vertical wall's ownership threshold must cross within a small, bounded, authored number of host
/// ticks — the mint is a pure function of the swept position against the threshold (<c>WorldAdjacencyRegion.Sweep</c>),
/// never a function of how long a caller waits before reading it back — and the destination row answers an ordinary
/// query the instant the transfer settles.
/// </summary>
public sealed class SeamCrossingOrchestrationLawTests {
    private static Puck.Maths.FixedQ4816 FixedQ4816(float value) => Puck.Maths.FixedQ4816.FromDouble(value: value);
    // A minimal vertical-wall pair, mirroring the shipped quilt shards' own east/west seam (WorldFaceFrame.IsYawOnly)
    // without any of the island's own geometry. Fixtures.BuildDocument authors no seat collider, so its derived
    // threshold is the contact skin, 0.02; a nonzero authored hysteresis may widen it.
    private static WorldDefinition SeamRow(string neighbourFileName, float outwardYaw, string name, string counterpart, float hysteresis) {
        var document = Fixtures.BuildDocument();

        return document with {
            References = [new WorldReference(
                Name: SafeName.Parse(candidate: "neighbour"),
                Document: WorldDocumentName.OfDocumentFile(path: neighbourFileName)
            )],
            Destinations = [new WorldDestination(
                Name: SafeName.Parse(candidate: "neighbour"),
                Reference: SafeName.Parse(candidate: "neighbour"),
                Scope: WorldDestinationScope.Global,
                Durability: WorldDestinationDurability.Persisted
            )],
            Adjacencies = [new WorldAdjacency(
                Name: SafeName.Parse(candidate: name),
                Destination: "neighbour",
                Counterpart: counterpart,
                Boundary: new WorldAdjacencyBoundary(
                    Center: Vector3.Zero,
                    Height: 8f,
                    OutwardPitchDegrees: 0f,
                    OutwardYawDegrees: outwardYaw,
                    Width: 8f
                ),
                Hysteresis: hysteresis
            )],
        };
    }
    private static WorldAdjacency ProjectionEdge(string name, string destination, string counterpart, float yaw, float hysteresis = 0f) => new(
        Name: SafeName.Parse(candidate: name), Destination: destination, Counterpart: counterpart,
        Boundary: new WorldAdjacencyBoundary(Vector3.Zero, yaw, 0f, 8f, 8f), Hysteresis: hysteresis
    );
    private static WorldDefinition ProjectionRow((string Name, string Path)[] neighbours, params WorldAdjacency[] edges) => Fixtures.BuildDocument() with {
        References = neighbours.Select(selector: neighbour => new WorldReference(SafeName.Parse(candidate: neighbour.Name), WorldDocumentName.OfDocumentFile(path: neighbour.Path))).ToArray(),
        Destinations = neighbours.Select(selector: neighbour => new WorldDestination(SafeName.Parse(candidate: neighbour.Name), neighbour.Name, WorldDestinationDurability.Persisted, WorldDestinationScope.Global)).ToArray(),
        Adjacencies = edges,
    };

    [Fact]
    public void CornerProjectionPathUsesTheIntermediateAuthoredOverlapDepth() {
        // The rows' hosts are disposed in the finally below, but a handle closing late must not fail a law that already
        // ran, so the delete is best-effort.
        using var directory = new TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-corner-projection-tests-"
        );
        var sourcePath = directory.PathOf(name: "source.world.json");
        var eastPath = directory.PathOf(name: "east.world.json");
        var southPath = directory.PathOf(name: "south.world.json");
        var cornerPath = directory.PathOf(name: "corner.world.json");
        var sourceDefinition = ProjectionRow([("east", eastPath), ("south", southPath), ("corner", cornerPath)], ProjectionEdge("east", "east", "west", 90f), ProjectionEdge("south", "south", "north", 0f));
        var eastDefinition = ProjectionRow([("source", sourcePath), ("corner", cornerPath)], ProjectionEdge("west", "source", "east", -90f), ProjectionEdge(counterpart: "north", destination: "corner", hysteresis: 8f, name: "south", yaw: 0f));
        var southDefinition = ProjectionRow([("source", sourcePath), ("corner", cornerPath)], ProjectionEdge("north", "source", "south", 180f), ProjectionEdge("east", "corner", "west", 90f));
        var cornerDefinition = ProjectionRow([("east", eastPath), ("south", southPath)], ProjectionEdge(counterpart: "south", destination: "east", hysteresis: 8f, name: "north", yaw: 180f), ProjectionEdge("west", "south", "east", -90f));
        var authored = Puck.Maths.FixedQ4816.FromDouble(value: 8d);

        Assert.True(condition: WorldAdjacencyPolicy.TryDeriveOverlap(depth: out var derived, local: eastDefinition, neighbour: cornerDefinition, reason: out var reason), userMessage: reason);
        Assert.True(condition: (derived < authored));

        FileBackedRows.Write((sourcePath, sourceDefinition), (eastPath, eastDefinition));
        FileBackedRows.Write((southPath, southDefinition), (cornerPath, cornerDefinition));
        using var hostStateRoot = new TemporaryDirectory(prefix: "puck-corner-projection-host-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None, admitsSpawn: true, machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(), resolver: new WorldSessionResolver(), seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostStateRoot.RootPath)
        );
        var rows = new[] {
            FileBackedRows.Build(definition: sourceDefinition, name: "source", path: sourcePath), FileBackedRows.Build(definition: eastDefinition, name: "east", path: eastPath),
            FileBackedRows.Build(definition: southDefinition, name: "south", path: southPath), FileBackedRows.Build(definition: cornerDefinition, name: "corner", path: cornerPath),
        };

        try {
            foreach (var row in rows) { host.Admit(row: row.Instance); }
            using var fields = new WorldAdjacencyFields(instances: host, sourceInstanceName: "source");
            var projection = fields.Visuals().Single(predicate: candidate => !candidate.Direct);

            Assert.Equal(expected: authored, actual: projection.Path[0].OverlapDepth);
            Assert.Equal(expected: authored, actual: projection.OverlapDepth);
        } finally {
            foreach (var row in rows) {
                row.Instance.Dispose();
                row.StateDirectory.Dispose();
            }
        }
    }
    [InlineData(0f, 1.1f, true)]
    [InlineData(2f, 1.1f, false)]
    [InlineData(2f, 2.1f, true)]
    [Theory]
    public void ScanTriggeredCrossingHonoursTheAuthoredOwnershipThreshold(float hysteresis, float position, bool shouldCross) {
        using var filesDirectory = new TemporaryDirectory(prefix: "puck-seam-crossing-tests-files-");
        var rowAPath = Path.Combine(
            path1: filesDirectory.RootPath,
            path2: "row-a.world.json"
        );
        var rowBPath = Path.Combine(
            path1: filesDirectory.RootPath,
            path2: "row-b.world.json"
        );
        var rowADefinition = SeamRow(
            counterpart: "west",
            hysteresis: hysteresis,
            name: "east",
            neighbourFileName: rowBPath,
            outwardYaw: 90f
        );
        var rowBDefinition = SeamRow(
            counterpart: "east",
            hysteresis: hysteresis,
            name: "west",
            neighbourFileName: rowAPath,
            outwardYaw: -90f
        );

        FileBackedRows.Write((rowAPath, rowADefinition), (rowBPath, rowBDefinition));

        var machineId = Guid.NewGuid();
        using var hostStateRoot = new TemporaryDirectory(prefix: "puck-seam-crossing-tests-host-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: machineId,
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

            // Settle the fresh join under ordinary stepping before moving the body — the same shape every two-row
            // scenario in HostRoundtripFixture starts from.
            for (var tick = 0; (tick < 5); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }

            var settled = rowAServer.Body(index: actor.Index)!.FixedPosition;

            // A body "at rest 1.1 raw units past the seam" — teleported, exactly the WorldBody.Pose hard-teleport
            // commit MappedArrivalApplicationLawTests documents: FixedPreviousPosition collapses to the same landing
            // spot, so the very next scan sees it already outside (WorldAdjacencyRegion.Sweep's parameter-zero case),
            // never a swept segment mid-flight.
            rowAServer.Body(index: actor.Index)!.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: position,
                y: ((float)((double)settled.Y)),
                yawRadians: 0f,
                z: ((float)((double)settled.Z))
            );

            const int Bound = 3;
            var mintedOnTick = -1;

            for (var tick = 0; (tick < Bound); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

                if (rowBServer.Population.IsActive(index: actor.Index)) {
                    mintedOnTick = tick;

                    break;
                }
            }

            Assert.Equal(actual: (mintedOnTick >= 0), expected: shouldCross);
            if (!shouldCross) {
                Assert.True(condition: rowAServer.Population.IsActive(index: actor.Index));
                Assert.False(condition: rowBServer.Population.IsActive(index: actor.Index));
                return;
            }
            Assert.False(
                condition: rowAServer.Population.IsActive(index: actor.Index),
                userMessage: "the source seat stayed active after a settled crossing"
            );

            // The destination answers an ordinary query the instant the transfer settles — proving a "verb after the
            // crossing" always has something to read on the server side (the seat's own console drain barrier is a
            // separate, Puck.World-side concern this project cannot reach).
            var arrived = rowBServer.Body(index: actor.Index);

            Assert.NotNull(@object: arrived);
            Assert.Equal(
                expected: FixedQ4816(value: position),
                actual: arrived!.FixedPosition.X
            );
        } finally {
            rowAInstance.Dispose();
            rowAStateDirectory.Dispose();
            rowBInstance.Dispose();
            rowBStateDirectory.Dispose();
        }
    }
}
