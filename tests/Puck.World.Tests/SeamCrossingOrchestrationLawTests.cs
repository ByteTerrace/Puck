using System.Numerics;
using Xunit;

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
    // References resolve relative to the authoring document's own file, and adjacency adoption matches a running
    // row by canonical file path (WorldInstanceHost.TryFindRunningInstanceByOrigin, WorldInstance.SourcePath) —
    // a bare in-memory name never canonicalizes, so this pair needs real, individually valid documents on disk even
    // though an adopted row's in-memory server is what actually answers the transfer, never a fresh reload.
    private static void SeamFiles(string rowAPath, WorldDefinition rowA, string rowBPath, WorldDefinition rowB) {
        File.WriteAllBytes(path: rowAPath, bytes: WorldDefinitionSerialization.Serialize(definition: rowA));
        File.WriteAllBytes(path: rowBPath, bytes: WorldDefinitionSerialization.Serialize(definition: rowB));
    }
    // A minimal vertical-wall pair, mirroring the shipped quilt shards' own east/west seam (WorldFaceFrame.IsYawOnly)
    // without any of the island's own geometry — the ownership threshold this pair carries is the reciprocal contact
    // hysteresis alone (Fixtures.BuildDocument authors no seat collider, so TryBodyReach is zero and the threshold is
    // the authored contact skin, 0.02).
    private static WorldDefinition SeamRow(string neighbourFileName, float outwardYaw, string name, string counterpart) {
        var document = Fixtures.BuildDocument();

        return document with {
            References = [new WorldReference(Name: SafeName.Parse(candidate: "neighbour"), Document: neighbourFileName)],
            Destinations = [new WorldDestination(
                Name: SafeName.Parse(candidate: "neighbour"),
                Reference: SafeName.Parse(candidate: "neighbour"),
                Scope: WorldDestinationScope.Global,
                Durability: WorldDestinationDurability.Persisted)],
            Adjacencies = [new WorldAdjacency(
                Name: SafeName.Parse(candidate: name),
                Destination: "neighbour",
                Counterpart: counterpart,
                Boundary: new WorldAdjacencyBoundary(Center: Vector3.Zero, Height: 8f, OutwardPitchDegrees: 0f, OutwardYawDegrees: outwardYaw, Width: 8f))],
        };
    }
    // HostRowFixture.HostRow hardcodes its origin to the bare row name (fine for the admin-driven EnqueueTransfer
    // path every other two-row law drives), so this suite builds its own rows over a REAL file path — the exact
    // shape HostRow.Build uses, with the one field (WorldInstance.SourcePath, read from `origin`, not
    // `documentOrigin`) that a file-canonicalized origin match needs.
    private static (WorldInstance Instance, WorldServer Server) BuildFileBackedRow(string name, string path, WorldDefinition definition) {
        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: $"puck-seam-crossing-tests-{name}-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines, instanceIdentity: name, narrationSink: new WorldConsoleNarrationSink());
        var link = new LoopbackTransport(server: server);
        var instance = new WorldInstance(
            name: name,
            origin: () => path,
            server: server,
            ownedMachines: machines,
            link: link,
            federation: new WorldFederationIdentity(Authenticator: new InertAuthenticator(), Subject: server.AuthorityIdentity),
            documentOrigin: new WorldFileOrigin(resolvedPath: path));

        return (instance, server);
    }

    [Fact]
    public void ScanTriggeredCrossing_MintsWithinAFewTicks_NeverAfterAnArbitraryWait() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-seam-crossing-tests-files-").FullName;
        var rowAPath = Path.Combine(directory, "row-a.world.json");
        var rowBPath = Path.Combine(directory, "row-b.world.json");
        var rowADefinition = SeamRow(neighbourFileName: rowBPath, outwardYaw: 90f, name: "east", counterpart: "west");
        var rowBDefinition = SeamRow(neighbourFileName: rowAPath, outwardYaw: -90f, name: "west", counterpart: "east");

        SeamFiles(rowAPath: rowAPath, rowA: rowADefinition, rowBPath: rowBPath, rowB: rowBDefinition);

        var machineId = Guid.NewGuid();
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: Directory.CreateTempSubdirectory(prefix: "puck-seam-crossing-tests-host-").FullName);
        var (rowAInstance, rowAServer) = BuildFileBackedRow(name: "row-a", path: rowAPath, definition: rowADefinition);
        var (rowBInstance, rowBServer) = BuildFileBackedRow(name: "row-b", path: rowBPath, definition: rowBDefinition);

        try {
            host.Admit(row: rowAInstance);
            host.Admit(row: rowBInstance);

            var actor = WorldPrincipal.Seat(slot: 0);

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
                x: 1.1f,
                y: ((float)((double)settled.Y)),
                yawRadians: 0f,
                z: ((float)((double)settled.Z)));

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

            Assert.True(
                condition: (mintedOnTick >= 0),
                userMessage: $"a body 1.1 raw units past a yaw-only seam's ownership threshold never crossed within {Bound} host ticks — the mint is policy-gated, not a pure function of position");
            Assert.False(condition: rowAServer.Population.IsActive(index: actor.Index), userMessage: "the source seat stayed active after a settled crossing");

            // The destination answers an ordinary query the instant the transfer settles — proving a "verb after the
            // crossing" always has something to read on the server side (the seat's own console drain barrier is a
            // separate, Puck.World-side concern this project cannot reach).
            var arrived = rowBServer.Body(index: actor.Index);

            Assert.NotNull(@object: arrived);
            Assert.Equal(expected: FixedQ4816(value: 1.1f), actual: arrived!.FixedPosition.X);
        } finally {
            rowAInstance.Dispose();
            rowBInstance.Dispose();
        }
    }

    private static Puck.Maths.FixedQ4816 FixedQ4816(float value) => Puck.Maths.FixedQ4816.FromDouble(value: value);
}
